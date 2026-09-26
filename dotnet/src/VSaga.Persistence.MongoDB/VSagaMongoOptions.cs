namespace VSaga.Persistence.MongoDB;

/// <summary>
/// Options for the MongoDB persistence provider (<c>AddVSagaMongoDb</c>). Client-level settings the
/// driver already models -- TLS, connection pooling, timeouts, the <c>ClusterConfigurator</c> hook that
/// provider-level tracing needs -- are not duplicated here: <c>AddVSagaMongoDb</c>'s second parameter
/// hands out the driver's own <c>MongoClientSettings</c> for those, the same precedent
/// <c>AddVSagaEfCore</c> sets by exposing <c>DbContextOptionsBuilder</c> rather than wrapping it.
/// </summary>
public sealed class VSagaMongoOptions
{
    /// <summary>
    /// A MongoDB connection string (<c>mongodb://host:port/?replicaSet=rs0</c>). The provider pins the
    /// primary read preference, local read concern and majority write concern on every handle it opens
    /// and reports Unhealthy, naming the setting, when the string explicitly says otherwise -- a
    /// <c>readPreference=secondaryPreferred</c> or a <c>w=1</c> would silently break the redelivery
    /// dedupe and the business-key race, which no correctness test can catch.
    /// </summary>
    public string ConnectionString { get; set; } = "mongodb://localhost:27017/?replicaSet=rs0";

    /// <summary>
    /// The database every collection lives in. Null (the default) takes the connection string's own
    /// database path, or <c>vsaga</c> when it has none. One database per service is the isolation rule:
    /// two vSaga services sharing one database would share one outbox and one timeout schedule.
    /// </summary>
    public string? DatabaseName { get; set; }

    /// <summary>
    /// The most bytes a <c>SagaLogEntry.PayloadJson</c> may occupy before the event log store replaces
    /// it with a small JSON marker and logs a warning. The engine records the full inbound message body
    /// on every <c>SagaStarted</c> entry <i>before</i> the step runs, and MongoDB caps a document at
    /// 16 MB: without this guard an oversized message that Postgres accepts would make the saga
    /// permanently unstartable, redelivering to exhaustion. The snapshot's state blob and an outbox
    /// row's body are not guarded -- their write errors surface as ordinary infrastructure failures.
    /// </summary>
    public int MaxPayloadJsonBytes { get; set; } = 12 * 1024 * 1024;

    /// <summary>
    /// How often the bootstrapper re-runs index creation and the topology probe. Index creation is
    /// idempotent, and re-probing is what lets the health check notice a replica set that lost its
    /// primary or a connection string that was changed under a running host.
    /// </summary>
    public TimeSpan ProbeInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A Pending outbox row older than this is counted as stranded by the health check, which reports
    /// the count in its data. The outbox dispatcher normally claims a row well inside its grace period,
    /// so a row this old means the dispatcher is not running or cannot reach the broker. Reported, not
    /// failed on: the store itself is healthy.
    /// </summary>
    public TimeSpan StrandedOutboxThreshold { get; set; } = TimeSpan.FromMinutes(5);
}
