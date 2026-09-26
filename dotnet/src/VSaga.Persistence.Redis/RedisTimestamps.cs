namespace VSaga.Persistence.Redis;

/// <summary>
/// Every projected timestamp -- a summary's <c>CreatedAtUtc</c>/<c>UpdatedAtUtc</c>, a timeout's due time,
/// an outbox row's created time, a topology entry's last-seen time -- is stored as integer Unix
/// <b>microseconds</b>, both as the sorted-set score that orders it and as the hash field that reads it
/// back. One representation, so the order an index yields and the value a predicate compares can never
/// disagree.
/// </summary>
/// <remarks>
/// <para>
/// Microseconds, not ticks, because a sorted-set score is an IEEE-754 double: today's <c>UtcTicks</c>
/// (about 6.4e17) is far above 2^53, so a ticks-valued score is lossy and non-injective. Unix
/// microseconds (about 1.8e15) are exact until the year 2255. The truncation this costs is the same one
/// the Postgres provider's <c>timestamp</c> columns apply, so the provider declares a one-microsecond
/// <c>TimestampResolution</c> exactly as Postgres does; the state blob keeps full ticks on both.
/// </para>
/// <para>
/// This is why <c>UpdatedSince</c>'s strict <c>&gt;</c> needs no boundary re-check: a row whose stored
/// microsecond equals the floor of the caller's instant reads back as a value at or below that instant,
/// so an exclusive score range is exactly the contract's predicate.
/// </para>
/// </remarks>
internal static class RedisTimestamps
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    /// <summary>Unix microseconds, floored -- so a stored value never reads back later than the instant written.</summary>
    public static long ToMicroseconds(DateTimeOffset instant)
    {
        var ticks = instant.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var quotient = Math.DivRem(ticks, TicksPerMicrosecond, out var remainder);
        return remainder < 0 ? quotient - 1 : quotient;
    }

    public static DateTimeOffset FromMicroseconds(long microseconds) =>
        new(DateTimeOffset.UnixEpoch.UtcTicks + (microseconds * TicksPerMicrosecond), TimeSpan.Zero);
}
