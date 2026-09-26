namespace VSaga.Persistence.Redis;

/// <summary>
/// Thrown by <c>ISagaSummaryReader.ListAsync</c> when a <c>Search</c> would scan more index members than
/// <see cref="VSagaRedisOptions.MaxSearchScanMembers"/> allows. Core Redis has no secondary index for
/// a substring match, so a search is a bounded scan; exceeding the bound is refused rather than
/// answered with a silently truncated page, which the dashboard's change poller would read as "the
/// result is exhausted". No other provider throws this; the dashboard maps it to HTTP 400.
/// </summary>
public sealed class RedisSearchScanLimitExceededException(int candidates, int limit)
    : Exception($"The search would scan {candidates} saga index members, above the configured limit of {limit} (VSagaRedisOptions.MaxSearchScanMembers). Narrow the search with a saga type, status or kind filter, or raise the limit.")
{
    public int Candidates { get; } = candidates;

    public int Limit { get; } = limit;
}

/// <summary>
/// Thrown before any write when the server's <c>used_memory</c>/<c>maxmemory</c> ratio, as last probed,
/// is above <see cref="VSagaRedisOptions.WriteMemoryThreshold"/>. A Lua script has no rollback, so a
/// persist refused up front is the alternative to one torn by an out-of-memory error midway. An
/// infrastructure failure: the engine redelivers the message.
/// </summary>
public sealed class RedisMemoryPressureException(double ratio, double threshold)
    : Exception($"Redis is at {ratio:P1} of maxmemory, above the {threshold:P0} write threshold (VSagaRedisOptions.WriteMemoryThreshold); refusing to persist before any write rather than risk a torn script.")
{
    public double Ratio { get; } = ratio;

    public double Threshold { get; } = threshold;
}

/// <summary>
/// A persist script returned a sentinel the provider does not define, or a shape it does not expect --
/// a mismatch between the C# and the Lua, never a data condition. Surfaced as an infrastructure
/// failure so the message redelivers against a provider that can be fixed.
/// </summary>
public sealed class RedisPersistenceProtocolException(string message) : Exception(message);
