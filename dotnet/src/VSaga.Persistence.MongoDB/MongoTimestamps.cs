namespace VSaga.Persistence.MongoDB;

/// <summary>
/// Every projected timestamp is stored as a pair: a BSON <c>Date</c> for a human reading the document in
/// <c>mongosh</c>, and the instant's exact UTC ticks as an <c>Int64</c>, which every range filter, every
/// sort and every index uses. BSON <c>Date</c> is millisecond-precision where the engine stamps at 100 ns
/// and <c>SagaListFilter.UpdatedSince</c> is contractually <i>strictly</i> greater: a millisecond field
/// would collapse a thousand distinct instants onto one value and the dashboard's change poller, whose
/// watermark is the last timestamp it pushed, would silently skip every row that shares it. Both halves
/// are written from this one mapping so they cannot disagree; the ticks half is the one read back.
/// </summary>
internal static class MongoTimestamps
{
    public static long ToTicks(DateTimeOffset instant) => instant.UtcTicks;

    public static DateTime ToDate(DateTimeOffset instant) => instant.UtcDateTime;

    public static DateTimeOffset FromTicks(long ticks) => new(ticks, TimeSpan.Zero);
}
