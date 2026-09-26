using MongoDB.Driver;

namespace VSaga.Persistence.MongoDB;

/// <summary>
/// The one <see cref="IMongoClient"/> the provider's stores share, and the database handle with the
/// provider's guarantees pinned on it: reads go to the primary, at local read concern, and every write is
/// acknowledged by a majority. Pinned rather than assumed, because a <c>readPreference=secondaryPreferred</c>
/// in an operator's connection string -- a common "read-heavy dashboard" default -- would silently break
/// the redelivery dedupe, the business-key race adjudication and the compensation set, and a <c>w=1</c>
/// event-log append that has not replicated is rolled back when its primary loses an election. The
/// client does not connect at construction, so the host starts even while MongoDB is unreachable.
/// </summary>
public sealed class MongoConnection : IDisposable
{
    public const string DefaultDatabaseName = "vsaga";

    private static readonly MongoDatabaseSettings PinnedSettings = new()
    {
        ReadPreference = ReadPreference.Primary,
        ReadConcern = ReadConcern.Local,
        WriteConcern = WriteConcern.WMajority,
    };

    public MongoConnection(VSagaMongoOptions options, Action<MongoClientSettings>? configureClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var url = new MongoUrl(options.ConnectionString);
        var settings = MongoClientSettings.FromUrl(url);
        settings.ApplicationName ??= "vsaga";
        settings.ReadPreference = ReadPreference.Primary;
        settings.ReadConcern = ReadConcern.Local;
        settings.WriteConcern = WriteConcern.WMajority;
        settings.RetryWrites = true;
        settings.RetryReads = true;
        configureClient?.Invoke(settings);

        Client = new MongoClient(settings);
        _ownsClient = true;
        DatabaseName = options.DatabaseName ?? url.DatabaseName ?? DefaultDatabaseName;
        Database = Client.GetDatabase(DatabaseName, PinnedSettings);
        ConnectionStringContradictions = FindContradictions(url);
    }

    /// <summary>Shares an existing client -- the provider's tests open one client for every database they create.</summary>
    internal MongoConnection(IMongoClient client, string databaseName)
    {
        Client = client;
        DatabaseName = databaseName;
        Database = client.GetDatabase(databaseName, PinnedSettings);
        ConnectionStringContradictions = [];
    }

    private readonly bool _ownsClient;

    public IMongoClient Client { get; }

    public IMongoDatabase Database { get; }

    public string DatabaseName { get; }

    /// <summary>
    /// Settings the connection string stated explicitly that the provider overrides: each is reported by
    /// the probe as Unhealthy, naming the guarantee it would have broken, because an operator who wrote
    /// <c>readPreference=secondaryPreferred</c> meant it and should be told it does not apply here.
    /// </summary>
    public IReadOnlyList<string> ConnectionStringContradictions { get; }

    private static List<string> FindContradictions(MongoUrl url)
    {
        var contradictions = new List<string>();
        if (url.ReadPreference is { ReadPreferenceMode: not ReadPreferenceMode.Primary } readPreference)
            contradictions.Add($"The connection string sets readPreference={readPreference.ReadPreferenceMode}, which the provider overrides to primary: a read from a secondary can miss a committed event-log entry, business-key reservation or snapshot, which silently reprocesses a message, double-starts a saga or shortens a compensation set.");

        if (url.W is { } w && !string.Equals(w.ToString(), "majority", StringComparison.OrdinalIgnoreCase))
            contradictions.Add($"The connection string sets w={w}, which the provider overrides to majority: an event-log append acknowledged by fewer nodes is rolled back when the primary loses an election, and the redelivered message is then reprocessed as new.");

        if (url.ReadConcernLevel is { } level && level != ReadConcernLevel.Local)
            contradictions.Add($"The connection string sets readConcernLevel={level}, which the provider overrides to local: its reads are read-your-own-writes on the primary, and a majority or snapshot read concern would lag them.");

        return contradictions;
    }

    /// <summary>Disposes the client only when this instance created it; a shared client belongs to whoever opened it.</summary>
    public void Dispose()
    {
        if (_ownsClient)
            Client.Dispose();
    }
}
