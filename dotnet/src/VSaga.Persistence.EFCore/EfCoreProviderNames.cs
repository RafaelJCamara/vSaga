namespace VSaga.Persistence.EFCore;

/// <summary>
/// The EF Core provider names the stores branch on. <see cref="Npgsql"/> is the one provider whose claims
/// are atomic under concurrent dispatchers: <c>EfCoreSagaTimeoutStore.ClaimDueAsync</c> and
/// <c>EfCoreSagaOutboxStore.ClaimPendingAsync</c> each compare <c>DbContext.Database.ProviderName</c>
/// against it, ordinally, and take their <c>UPDATE ... FOR UPDATE SKIP LOCKED ... RETURNING</c> path only
/// on a match. Every other provider -- SQLite in tests, SQL Server, anything else -- gets a plain
/// load-then-update that is correct for exactly one dispatcher instance (ADR 0004). One constant, so the
/// two guards cannot drift apart.
/// </summary>
internal static class EfCoreProviderNames
{
    public const string Npgsql = "Npgsql.EntityFrameworkCore.PostgreSQL";
}
