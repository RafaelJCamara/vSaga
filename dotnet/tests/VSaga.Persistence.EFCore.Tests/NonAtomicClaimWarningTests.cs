using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace VSaga.Persistence.EFCore.Tests;

/// <summary>
/// ADR 0004's required warning: a provider with no atomic claim (SQLite here) logs once per process and store
/// when the fallback claim first runs. The once-flag is process-wide, so each store has exactly one test that
/// passes a logger and the assertions never depend on test order.
/// </summary>
public sealed class NonAtomicClaimWarningTests : IAsyncLifetime, IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<VSagaDbContext> _options;

    public NonAtomicClaimWarningTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<VSagaDbContext>().UseSqlite(_connection).Options;
        using var db = new VSagaDbContext(_options);
        db.Database.EnsureCreated();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // xunit 2 calls IAsyncLifetime.DisposeAsync, never a test class's IAsyncDisposable.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    [Fact]
    public async Task The_timeout_store_warns_once_that_its_claim_is_for_one_dispatcher()
    {
        var logger = new CapturingLogger<EfCoreSagaTimeoutStore>();

        for (var i = 0; i < 3; i++)
        {
            await using var db = new VSagaDbContext(_options);
            await new EfCoreSagaTimeoutStore(db, logger).ClaimDueAsync(DateTimeOffset.UtcNow, 10);
        }

        var warning = Assert.Single(logger.Messages);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(nameof(EfCoreSagaTimeoutStore), warning.Text, StringComparison.Ordinal);
        Assert.Contains("Microsoft.EntityFrameworkCore.Sqlite", warning.Text, StringComparison.Ordinal);
        Assert.Contains("single dispatcher", warning.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_outbox_store_warns_once_that_its_claim_is_for_one_dispatcher()
    {
        var logger = new CapturingLogger<EfCoreSagaOutboxStore>();

        for (var i = 0; i < 3; i++)
        {
            await using var db = new VSagaDbContext(_options);
            await new EfCoreSagaOutboxStore(db, logger).ClaimPendingAsync(DateTimeOffset.UtcNow, 10);
        }

        var warning = Assert.Single(logger.Messages);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(nameof(EfCoreSagaOutboxStore), warning.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_store_built_without_a_logger_still_claims()
    {
        await using var db = new VSagaDbContext(_options);

        var claimed = await new EfCoreSagaTimeoutStore(db).ClaimDueAsync(DateTimeOffset.UtcNow, 10);

        Assert.Empty(claimed);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Text)> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add((logLevel, formatter(state, exception)));
    }
}
