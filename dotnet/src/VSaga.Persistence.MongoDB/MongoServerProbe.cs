using System.Globalization;
using VSaga.Abstractions.Persistence;
using MongoDB.Bson;
using MongoDB.Driver;

namespace VSaga.Persistence.MongoDB;

/// <summary>
/// Verifies the provider's prerequisites rather than documenting them: the topology (a replica set,
/// with a primary), the server version, the connection string's explicit settings against the ones the
/// provider pins, the indexes and the schema marker -- at bootstrap and on every health tick. A probe
/// never throws; it reports, and it never fails open: what it cannot verify is a failure, not a pass.
/// </summary>
public sealed class MongoServerProbe(MongoConnection connection, MongoCollections collections, VSagaMongoOptions options)
{
    /// <summary>The oldest server the provider accepts: <c>hello</c> and every transaction feature used here are older, but nothing below this is tested or still maintained.</summary>
    public const int MinimumMajorVersion = 6;

    /// <summary>The most recent report; null until the first probe completes.</summary>
    public MongoProbeReport? Latest { get; private set; }

    public async Task<MongoProbeReport> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var report = new ReportBuilder(DateTimeOffset.UtcNow);
        foreach (var contradiction in connection.ConnectionStringContradictions)
            report.Fail(contradiction);

        try
        {
            await ProbeCoreAsync(report, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            report.Fail($"The probe itself failed against MongoDB: {ex.GetType().Name}: {ex.Message}");
        }

        return Latest = report.Build();
    }

    private async Task ProbeCoreAsync(ReportBuilder report, CancellationToken cancellationToken)
    {
        await ProbeTopologyAsync(report, cancellationToken);
        await ProbeVersionAsync(report, cancellationToken);
        await ProbeIndexesAsync(report, cancellationToken);
        await ProbeSchemaMarkerAsync(report, cancellationToken);
        await ProbeStrandedOutboxAsync(report, cancellationToken);
    }

    /// <summary>
    /// <c>hello</c>, not the driver's own cluster description alone: a direct connection to one member
    /// reports the member, and the command is what says whether it belongs to a replica set (<c>setName</c>),
    /// is a mongos (<c>msg: isdbgrid</c>) or is standalone. Transactions need the first; the second is
    /// unsupported in this version because a shard key was never designed and cross-shard transaction cost
    /// never measured; the third is refused outright, with no non-transactional escape hatch, because a
    /// durable phantom outbox row is exactly the bug the outbox exists to close.
    /// </summary>
    private async Task ProbeTopologyAsync(ReportBuilder report, CancellationToken cancellationToken)
    {
        var hello = await connection.Database.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: cancellationToken);
        report.ReplicaSetName = hello.TryGetValue("setName", out var setName) ? setName.AsString : null;
        report.IsWritablePrimary = hello.TryGetValue("isWritablePrimary", out var primary) && primary.ToBoolean();
        var isMongos = hello.TryGetValue("msg", out var msg) && string.Equals(msg.AsString, "isdbgrid", StringComparison.Ordinal);

        if (isMongos)
        {
            report.Topology = "sharded";
            report.Fail("Connected to a mongos: sharded clusters are unsupported by this provider version (no shard key is designed and cross-shard transaction cost is unmeasured). Use a replica set.");
        }
        else if (report.ReplicaSetName is null)
        {
            report.Topology = "standalone";
            report.Fail("Connected to a standalone mongod: the provider commits each snapshot and its outbox rows in one multi-document transaction, which needs a replica set. Start mongod with --replSet and run rs.initiate(); a single-member set is enough.");
        }
        else
        {
            report.Topology = "replica set";
            if (!report.IsWritablePrimary)
                report.Fail($"Connected to a member of replica set '{report.ReplicaSetName}' that is not the primary: every read and write must go to the primary, and none has been elected or the connection is pinned to a secondary.");
        }
    }

    private async Task ProbeVersionAsync(ReportBuilder report, CancellationToken cancellationToken)
    {
        var buildInfo = await connection.Database.RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1), cancellationToken: cancellationToken);
        report.ServerVersion = buildInfo.TryGetValue("version", out var version) ? version.AsString : null;

        var major = report.ServerVersion?.Split('.')[0];
        if (major is not null && int.TryParse(major, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed < MinimumMajorVersion)
            report.Fail($"MongoDB {report.ServerVersion} is below the supported minimum {MinimumMajorVersion}.0.");
    }

    /// <summary>Every expected index by name, in particular the business-key index that adjudicates the concurrent-double-initiate race; sagas must not run without it.</summary>
    private async Task ProbeIndexesAsync(ReportBuilder report, CancellationToken cancellationToken)
    {
        var present = await MongoIndexes.ListAsync(connection, cancellationToken);
        var missing = MongoIndexes.Expected
            .SelectMany(expected => expected.Value.Where(name => !present[expected.Key].Contains(name)).Select(name => $"{expected.Key}.{name}"))
            .ToList();

        report.IndexesInPlace = missing.Count == 0;
        if (missing.Count > 0)
            report.Fail($"Index(es) missing: {string.Join(", ", missing)}. The bootstrapper has not completed against this database, or index creation failed (a server without partial-index support, such as Amazon DocumentDB, cannot host this provider).");
    }

    private async Task ProbeSchemaMarkerAsync(ReportBuilder report, CancellationToken cancellationToken)
    {
        var marker = await collections.Meta.Find(Builders<SchemaMarkerDocument>.Filter.Eq(d => d.Id, SchemaMarkerDocument.MarkerId)).FirstOrDefaultAsync(cancellationToken);
        if (marker is null)
            report.Fail($"The schema marker ({MongoCollections.MetaName}/{SchemaMarkerDocument.MarkerId}) is missing: the bootstrapper has not completed against this database.");
        else if (marker.SchemaVersion != SagaInstanceDocument.CurrentSchemaVersion)
            report.Fail($"The database is schema version {marker.SchemaVersion}; this provider expects {SagaInstanceDocument.CurrentSchemaVersion}.");
    }

    /// <summary>Counted and reported, not failed on: a Pending row older than the threshold means the dispatcher is not draining, not that the store is broken.</summary>
    private async Task ProbeStrandedOutboxAsync(ReportBuilder report, CancellationToken cancellationToken)
    {
        var cutoff = MongoTimestamps.ToTicks(DateTimeOffset.UtcNow - options.StrandedOutboxThreshold);
        var filter = Builders<SagaOutboxDocument>.Filter.Eq(d => d.Status, SagaOutboxStatus.Pending)
                     & Builders<SagaOutboxDocument>.Filter.Lt(d => d.CreatedAtTicks, cutoff);

        report.StrandedOutboxRows = await collections.Outbox.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
    }

    private sealed class ReportBuilder(DateTimeOffset probedAtUtc)
    {
        private readonly List<string> _failures = [];

        public string? ServerVersion { get; set; }

        public string Topology { get; set; } = "unknown";

        public string? ReplicaSetName { get; set; }

        public bool IsWritablePrimary { get; set; }

        public bool IndexesInPlace { get; set; }

        public long StrandedOutboxRows { get; set; }

        public void Fail(string reason) => _failures.Add(reason);

        public MongoProbeReport Build() =>
            new(probedAtUtc, _failures, ServerVersion, Topology, ReplicaSetName, IsWritablePrimary, IndexesInPlace, StrandedOutboxRows);
    }
}

/// <summary>What one probe found. <see cref="Failures"/> empty means every prerequisite the provider depends on was verified.</summary>
public sealed record MongoProbeReport(
    DateTimeOffset ProbedAtUtc,
    IReadOnlyList<string> Failures,
    string? ServerVersion,
    string Topology,
    string? ReplicaSetName,
    bool IsWritablePrimary,
    bool IndexesInPlace,
    long StrandedOutboxRows)
{
    public bool IsHealthy => Failures.Count == 0;

    public string Describe() =>
        IsHealthy
            ? string.Create(CultureInfo.InvariantCulture, $"MongoDB {ServerVersion}, {Topology} {ReplicaSetName} (primary), indexes in place, {StrandedOutboxRows} stranded outbox row(s).")
            : string.Join(" ", Failures);
}
