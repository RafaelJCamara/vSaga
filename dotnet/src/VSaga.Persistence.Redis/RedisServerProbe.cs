using System.Globalization;
using StackExchange.Redis;

namespace VSaga.Persistence.Redis;

/// <summary>
/// Validates the operator's Redis configuration rather than documenting it: <c>appendonly</c>,
/// <c>maxmemory-policy</c>, cluster mode, the role, memory pressure, the torn-write sentinel and the
/// schema marker, at bootstrap and on every health tick. A probe never throws; it reports. Failing open
/// is the one thing it must not do -- a <c>CONFIG GET</c> the server refuses (as several managed tiers do)
/// is reported as "unverifiable", never as healthy.
/// </summary>
public sealed class RedisServerProbe(RedisConnection connection, RedisKeySpace keys, VSagaRedisOptions options)
{
    private const int TornInstancesReported = 10;
    private static readonly RedisScript Ping = new(RedisLuaScripts.Ping);

    /// <summary>The most recent report, which the persist path's memory gate reads; null until the first probe completes.</summary>
    public RedisProbeReport? Latest { get; private set; }

    public async Task<RedisProbeReport> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var report = new ReportBuilder(DateTimeOffset.UtcNow);
        try
        {
            await ProbeCoreAsync(report, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            report.Fail($"The probe itself failed: {ex.GetType().Name}: {ex.Message}");
        }

        return Latest = report.Build();
    }

    private async Task ProbeCoreAsync(ReportBuilder report, CancellationToken cancellationToken)
    {
        var multiplexer = await connection.GetMultiplexerAsync(cancellationToken);
        var server = multiplexer.GetServers().FirstOrDefault(s => s.IsConnected && !s.IsReplica);
        if (server is null)
        {
            report.Fail("No connected Redis primary: " + (multiplexer.GetStatus() ?? "no status"));
            return;
        }

        var info = (await server.InfoAsync()).ToDictionary(g => g.Key, g => g.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase);
        ProbeTopology(report, info);
        ProbeMemory(report, info);
        await ProbeConfigurationAsync(report, server);

        var db = multiplexer.GetDatabase();
        await ProbeSchemaAndSentinelAsync(report, db);
        await ProbeScriptingAsync(report, db);
    }

    private static void ProbeTopology(ReportBuilder report, Dictionary<string, Dictionary<string, string>> info)
    {
        report.ServerVersion = Info(info, "Server", "valkey_version") ?? Info(info, "Server", "redis_version");
        report.ServerName = Info(info, "Server", "server_name") ?? "redis";
        report.ConnectedReplicas = int.TryParse(Info(info, "Replication", "connected_slaves"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var replicas) ? replicas : 0;

        if (string.Equals(Info(info, "Cluster", "cluster_enabled"), "1", StringComparison.Ordinal) || string.Equals(Info(info, "Server", "redis_mode"), "cluster", StringComparison.Ordinal))
            report.Fail("Redis Cluster is unsupported: the persist script's atomic unit (a snapshot, its outbox rows and every summary index) needs a single shard. Use a single primary with replicas.");

        if (Info(info, "Replication", "role") is { } role && !string.Equals(role, "master", StringComparison.Ordinal))
            report.Fail($"Connected to a replica (role '{role}'); every read and write must go to the primary.");
    }

    private void ProbeMemory(ReportBuilder report, Dictionary<string, Dictionary<string, string>> info)
    {
        report.UsedMemory = long.TryParse(Info(info, "Memory", "used_memory"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var used) ? used : 0;
        report.MaxMemory = long.TryParse(Info(info, "Memory", "maxmemory"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var max) ? max : 0;

        if (report.MaxMemory > 0 && (double)report.UsedMemory / report.MaxMemory > options.WriteMemoryThreshold)
            report.Fail(string.Create(CultureInfo.InvariantCulture, $"Memory pressure: used_memory is {(double)report.UsedMemory / report.MaxMemory:P1} of maxmemory, above the {options.WriteMemoryThreshold:P0} write threshold; persists are refused until it falls (RAM is this provider's dataset ceiling)."));
    }

    private static async Task ProbeConfigurationAsync(ReportBuilder report, IServer server)
    {
        try
        {
            report.AppendOnly = await ConfigAsync(server, "appendonly");
            report.AppendFsync = await ConfigAsync(server, "appendfsync");
            report.MaxMemoryPolicy = await ConfigAsync(server, "maxmemory-policy");
            report.MinReplicasToWrite = await ConfigAsync(server, "min-replicas-to-write");
        }
        catch (RedisServerException ex)
        {
            report.Fail($"CONFIG GET is unavailable ({ex.Message.Trim()}), so appendonly and maxmemory-policy cannot be verified; the durability and no-eviction guarantees are unverifiable on this server.");
            return;
        }

        if (!string.Equals(report.AppendOnly, "yes", StringComparison.OrdinalIgnoreCase))
            report.Fail($"appendonly is '{report.AppendOnly}', not 'yes': without the AOF every acknowledged write is lost on a restart, including the event-log entries the redelivery dedupe rests on.");

        if (!string.Equals(report.MaxMemoryPolicy, "noeviction", StringComparison.OrdinalIgnoreCase))
            report.Fail($"maxmemory-policy is '{report.MaxMemoryPolicy}', not 'noeviction': under any other policy Redis deletes snapshots, timeouts and outbox rows silently, with no error at any call site. A shared cache instance is unsupported.");
    }

    private async Task ProbeSchemaAndSentinelAsync(ReportBuilder report, IDatabase db)
    {
        var schemaVersion = await db.HashGetAsync(keys.Meta, RedisPersistenceBootstrapper.SchemaVersionField);
        if (schemaVersion.IsNull)
            report.Fail($"The schema marker {keys.Meta}[{RedisPersistenceBootstrapper.SchemaVersionField}] is missing: the bootstrapper has not completed against this server, or the key space was flushed.");
        else if (!string.Equals(schemaVersion.ToString(), RedisPersistenceBootstrapper.SchemaVersion, StringComparison.Ordinal))
            report.Fail($"The key space is schema version '{schemaVersion.ToString()}'; this provider expects '{RedisPersistenceBootstrapper.SchemaVersion}'.");

        var torn = await db.HashGetAllAsync(keys.Torn);
        report.TornWrites = torn.Length;
        report.TornInstances = torn.Take(TornInstancesReported).Select(e => e.Name.ToString()).ToList();
        if (torn.Length > 0)
            report.Fail($"{torn.Length} torn write(s): a persist script aborted after its first write, leaving these instances half-applied: {string.Join(", ", report.TornInstances)}. Inspect and repair them, then HDEL their fields from {keys.Torn}.");
    }

    private static async Task ProbeScriptingAsync(ReportBuilder report, IDatabase db)
    {
        try
        {
            await Ping.EvaluateAsync(db, [], []);
        }
        catch (RedisServerException ex)
        {
            report.Fail($"Server-side Lua is unavailable ({ex.Message.Trim()}); every write path of this provider is a script.");
        }
    }

    private static string? Info(Dictionary<string, Dictionary<string, string>> info, string section, string key) =>
        info.TryGetValue(section, out var values) && values.TryGetValue(key, out var value) ? value : null;

    private static async Task<string?> ConfigAsync(IServer server, string name)
    {
        var pairs = await server.ConfigGetAsync(name);
        return pairs.Length == 0 ? null : pairs[0].Value;
    }

    private sealed class ReportBuilder(DateTimeOffset probedAtUtc)
    {
        private readonly List<string> _failures = [];

        public string? ServerVersion { get; set; }

        public string? ServerName { get; set; }

        public string? AppendOnly { get; set; }

        public string? AppendFsync { get; set; }

        public string? MaxMemoryPolicy { get; set; }

        public string? MinReplicasToWrite { get; set; }

        public int ConnectedReplicas { get; set; }

        public long UsedMemory { get; set; }

        public long MaxMemory { get; set; }

        public int TornWrites { get; set; }

        public IReadOnlyList<string> TornInstances { get; set; } = [];

        public void Fail(string reason) => _failures.Add(reason);

        public RedisProbeReport Build() => new(probedAtUtc, _failures, ServerName, ServerVersion, AppendOnly, AppendFsync, MaxMemoryPolicy,
            MinReplicasToWrite, ConnectedReplicas, UsedMemory, MaxMemory, TornWrites, TornInstances);
    }
}

/// <summary>What one probe found. <see cref="Failures"/> empty means every guarantee the provider depends on was verified.</summary>
public sealed record RedisProbeReport(
    DateTimeOffset ProbedAtUtc,
    IReadOnlyList<string> Failures,
    string? ServerName,
    string? ServerVersion,
    string? AppendOnly,
    string? AppendFsync,
    string? MaxMemoryPolicy,
    string? MinReplicasToWrite,
    int ConnectedReplicas,
    long UsedMemory,
    long MaxMemory,
    int TornWrites,
    IReadOnlyList<string> TornInstances)
{
    public bool IsHealthy => Failures.Count == 0;

    /// <summary>used_memory over maxmemory, or null when the server has no maxmemory limit to measure against.</summary>
    public double? MemoryRatio => MaxMemory > 0 ? (double)UsedMemory / MaxMemory : null;

    /// <summary>
    /// "B" when <c>appendfsync always</c> and at least one replica is required for a write -- the only
    /// configuration under which the provider is documented as production-tier; "A" otherwise.
    /// </summary>
    public string DurabilityTier =>
        string.Equals(AppendFsync, "always", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(MinReplicasToWrite, NumberStyles.Integer, CultureInfo.InvariantCulture, out var replicas) && replicas > 0
            ? "B"
            : "A";

    public string Describe() =>
        IsHealthy
            ? string.Create(CultureInfo.InvariantCulture, $"{ServerName} {ServerVersion}, durability tier {DurabilityTier} (appendfsync {AppendFsync}, min-replicas-to-write {MinReplicasToWrite}, {ConnectedReplicas} replica(s)), {DescribeMemory()}.")
            : string.Join(" ", Failures);

    private string DescribeMemory() =>
        MemoryRatio is { } ratio
            ? string.Create(CultureInfo.InvariantCulture, $"{ratio:P1} of maxmemory used")
            : string.Create(CultureInfo.InvariantCulture, $"{UsedMemory / 1_048_576.0:F1} MiB used with no maxmemory limit");
}
