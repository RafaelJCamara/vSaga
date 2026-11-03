using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace VSaga.Persistence.EFCore;

/// <summary>
/// The EF Core provider names the stores branch on. <see cref="Npgsql"/> and <see cref="SqlServer"/> are the two
/// providers whose claims are atomic under concurrent dispatchers: <c>EfCoreSagaTimeoutStore.ClaimDueAsync</c> and
/// <c>EfCoreSagaOutboxStore.ClaimPendingAsync</c> each compare <c>DbContext.Database.ProviderName</c> against them,
/// ordinally, and take a single-statement claim only on a match (<c>UPDATE ... FOR UPDATE SKIP LOCKED ... RETURNING</c>
/// on Npgsql, <c>UPDATE ... OUTPUT</c> over a <c>READPAST</c> read on SQL Server). Every other provider -- SQLite in
/// tests, anything else -- gets a plain load-then-update that is correct for exactly one dispatcher instance
/// (ADR 0004, extended to SQL Server by ADR 0009). One home for the names, so the two stores cannot drift apart.
/// </summary>
internal static class EfCoreProviderNames
{
    public const string Npgsql = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public const string SqlServer = "Microsoft.EntityFrameworkCore.SqlServer";

    /// <summary>
    /// The collation SQL Server compares saga type names under where a set must match exactly: the column's own
    /// is the database's, case-insensitive by default. Binary, so it is also accent- and width-sensitive. It still
    /// ignores trailing spaces, as every SQL Server comparison does; the raw claim SQL adds a length check for that.
    /// </summary>
    public const string SqlServerBinaryCollation = "Latin1_General_100_BIN2";

    private static readonly ConcurrentDictionary<string, byte> WarnedStores = new(StringComparer.Ordinal);

    /// <summary>
    /// ADR 0004's required warning: a provider that is neither Npgsql nor SQL Server claims with a plain
    /// load-then-update, which double-claims under two dispatchers with no error anywhere. Logged once per
    /// process and store, the first time the fallback runs (a few seconds after start, as the dispatchers poll),
    /// so a scoped store created on every poll does not repeat it.
    /// </summary>
    public static void WarnAboutNonAtomicClaim(ILogger? logger, string store, string? providerName)
    {
        if (logger is null || !WarnedStores.TryAdd(store, 0))
            return;

        logger.LogWarning(
            "{Store} claims with a plain load-then-update on EF Core provider {Provider}, which is only correct for a single dispatcher instance: " +
            "two instances would claim the same rows and fire timeouts or publish outbox messages twice. Run exactly one dispatcher, or use Npgsql or SQL Server.",
            store, providerName ?? "(unknown)");
    }

    public static bool IsNpgsql(string? providerName) => string.Equals(providerName, Npgsql, StringComparison.Ordinal);

    public static bool IsSqlServer(string? providerName) => string.Equals(providerName, SqlServer, StringComparison.Ordinal);
}
