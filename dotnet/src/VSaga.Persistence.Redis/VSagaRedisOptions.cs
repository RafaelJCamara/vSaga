namespace VSaga.Persistence.Redis;

/// <summary>
/// Options for the Redis persistence provider (<c>AddVSagaRedis</c>). Connection-level settings that
/// StackExchange.Redis already models -- TLS, <c>AbortOnConnectFail</c>, retry counts, multiplexer
/// sizing -- are not duplicated here: <c>AddVSagaRedis</c>'s second parameter hands out the client's own
/// <c>ConfigurationOptions</c> for those, the same precedent <c>AddVSagaEfCore</c> sets by exposing
/// <c>DbContextOptionsBuilder</c> rather than wrapping it.
/// </summary>
public sealed class VSagaRedisOptions
{
    /// <summary>
    /// A StackExchange.Redis connection string (<c>host:port,password=...</c>). Parsed into the
    /// <c>ConfigurationOptions</c> that <c>AddVSagaRedis</c>'s <c>configureConnection</c> callback then
    /// sees, so anything set there overrides what the string said.
    /// </summary>
    public string ConnectionString { get; set; } = "localhost:6379";

    /// <summary>
    /// The key-space namespace. Every key the provider owns is prefixed <c>{vsaga:&lt;Namespace&gt;}:</c>
    /// -- the braces are a Redis hash tag, so the whole key space hashes to one slot. Two vSaga
    /// deployments may share one Redis instance only under distinct namespaces, and even then a
    /// prefix is a naming convention, not a security boundary: a neighbour's <c>FLUSHALL</c> or
    /// <c>CONFIG SET</c> reaches every namespace. A dedicated instance is the documented prerequisite.
    /// </summary>
    public string Namespace { get; set; } = "default";

    /// <summary>
    /// The fraction of <c>maxmemory</c> above which persists are refused before any write, as a
    /// pre-flight gate. Redis cannot roll back a Lua script, so a script that runs out of memory
    /// midway tears the write set; refusing loudly at 90% (the default) is what keeps that residual
    /// small. Ignored when the server reports no <c>maxmemory</c> limit, since there is then no
    /// ceiling to measure against. The used/max ratio is read on the health-probe interval, not on
    /// every write.
    /// </summary>
    public double WriteMemoryThreshold { get; set; } = 0.90;

    /// <summary>
    /// How many index members a <c>Search</c> may scan before <c>ISagaSummaryReader.ListAsync</c> throws
    /// <see cref="RedisSearchScanLimitExceededException"/>. Core Redis has no substring index, so a
    /// search is a case-folded scan of the candidate members; above this bound the provider refuses
    /// rather than truncating a page silently. The dashboard maps the exception to HTTP 400.
    /// </summary>
    public int MaxSearchScanMembers { get; set; } = 100_000;

    /// <summary>
    /// How often the bootstrapper re-probes the server's configuration (<c>appendonly</c>,
    /// <c>maxmemory-policy</c>, cluster mode, memory pressure, the torn-write sentinel). A live
    /// <c>CONFIG SET</c> by a neighbouring operator silently changes the durability guarantees, so the
    /// probe is repeated rather than run once. The health check reports the latest probe.
    /// </summary>
    public TimeSpan ProbeInterval { get; set; } = TimeSpan.FromSeconds(10);
}
