using MongoDB.Driver;

namespace VSaga.Persistence.MongoDB;

/// <summary>
/// Reads the driver's error shapes. The rule, as a contract: only a duplicate key on the collection a
/// store expects one on maps to a domain exception; every other driver error is an infrastructure
/// failure the engine redelivers. Mapping an infrastructure error to <c>SagaConcurrencyException</c> would
/// make a timeout vanish with its side effects already sent; mapping a genuine collision to an
/// infrastructure error would redeliver a message forever.
/// </summary>
internal static class MongoErrors
{
    private const int DuplicateKeyCode = 11000;

    /// <summary>A duplicate-key error anywhere in <paramref name="exception"/>, whichever shape the driver used to report it.</summary>
    public static bool IsDuplicateKey(Exception exception) =>
        WriteErrors(exception).Any(e => e.Category == ServerErrorCategory.DuplicateKey)
        || exception is MongoCommandException { Code: DuplicateKeyCode };

    /// <summary>
    /// A duplicate key on <paramref name="collectionName"/> of <paramref name="databaseName"/> specifically.
    /// The server names the namespace in the error message (<c>E11000 duplicate key error collection:
    /// db.coll index: name dup key: ...</c>); a staged outbox row colliding inside the same transaction
    /// as a snapshot insert must not be reported as the saga already existing.
    /// </summary>
    public static bool IsDuplicateKeyOn(Exception exception, string databaseName, string collectionName)
    {
        var ns = $"collection: {databaseName}.{collectionName} ";
        return WriteErrors(exception).Any(e => e.Category == ServerErrorCategory.DuplicateKey && e.Message.Contains(ns, StringComparison.Ordinal))
               || (exception is MongoCommandException { Code: DuplicateKeyCode } command && command.Message.Contains(ns, StringComparison.Ordinal));
    }

    private static IEnumerable<WriteError> WriteErrors(Exception exception) => exception switch
    {
        MongoWriteException { WriteError: { } error } => [error],
        MongoBulkWriteException bulk => bulk.WriteErrors,
        _ => [],
    };
}
