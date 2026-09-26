using MongoDB.Driver;

namespace VSaga.Persistence.MongoDB;

/// <summary>
/// The one increment shape every allocated number comes from: <c>findOneAndUpdate({_id}, {$inc: {seq}},
/// upsert, returnDocument: after)</c>. MongoDB has no auto-increment, and the timeline's counter cannot
/// live in the snapshot because <c>AppendAsync</c> is legitimately called for an instance that has no
/// snapshot yet (the <c>SagaStarted</c> entry precedes the insert that creates it).
/// </summary>
/// <remarks>
/// The upsert of a document that does not exist yet can race a concurrent upsert of the same id and lose
/// with a duplicate-key error, which the server does not always retry for the caller. Two messages for
/// one correlation id arriving together both append <c>MessageReceived</c> with no serialisation gate,
/// so the race is ordinary, not a corner case; the loser retries a bounded number of times and then
/// wins, because by then the document exists.
/// </remarks>
internal static class MongoCounters
{
    private const int UpsertRaceRetries = 5;

    /// <summary>Increments <paramref name="id"/>'s counter by <paramref name="by"/> and returns its new value; the allocated range is <c>(result - by, result]</c>.</summary>
    public static async Task<long> IncrementAsync(IMongoCollection<CounterDocument> counters, string id, long by, CancellationToken cancellationToken)
    {
        var filter = Builders<CounterDocument>.Filter.Eq(c => c.Id, id);
        var update = Builders<CounterDocument>.Update.Inc(c => c.Seq, by);
        var options = new FindOneAndUpdateOptions<CounterDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After };

        var retriesLeft = UpsertRaceRetries;
        while (true)
        {
            try
            {
                var counter = await counters.FindOneAndUpdateAsync(filter, update, options, cancellationToken);
                return counter.Seq;
            }
            catch (MongoException ex) when (retriesLeft-- > 0 && MongoErrors.IsDuplicateKey(ex))
            {
                // Lost the upsert race: the rival created the document, so the next attempt increments it.
            }
        }
    }
}
