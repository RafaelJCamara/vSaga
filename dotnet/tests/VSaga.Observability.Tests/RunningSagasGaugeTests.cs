using System.Diagnostics.Metrics;
using VSaga.Abstractions.Diagnostics;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Observability;
using VSaga.Persistence.EFCore;
using VSaga.Persistence.InMemory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace VSaga.Observability.Tests;

/// <summary>
/// ADR 0013: the refresh service and the <c>vsaga.saga.running</c> gauge it feeds, over a scripted reader with a fake
/// clock, and end to end over the in-memory store and EF Core (a scoped <c>DbContext</c>, the case a captured scope
/// would break). The gauge is process-wide, so every case that reads it lives in this one class, which xUnit runs one
/// case at a time, and each disposes what it published.
/// </summary>
public sealed class RunningSagasGaugeTests : IAsyncLifetime
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly CapturingLogger _log = new();
    private readonly List<IAsyncDisposable> _disposables = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
            await disposable.DisposeAsync();

        Assert.True(RunningSagasMetrics.PublishedReadings == 0, "A reading outlived its service");
    }

    private static Dictionary<string, long> ReadGauge()
    {
        var readings = new Dictionary<string, long>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (string.Equals(instrument.Meter.Name, VSagaDiagnostics.MeterName, StringComparison.Ordinal) && string.Equals(instrument.Name, "vsaga.saga.running", StringComparison.Ordinal))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (string.Equals(tag.Key, VSagaDiagnostics.TagSagaType, StringComparison.Ordinal))
                    readings[(string)tag.Value!] = value;
            }
        });
        listener.Start();
        listener.RecordObservableInstruments();
        return readings;
    }

    private static Dictionary<string, long> Tally(params (string SagaType, long Count)[] counts) =>
        counts.ToDictionary(c => c.SagaType, c => c.Count, StringComparer.Ordinal);

    private static void AssertGauge(Dictionary<string, long> expected) =>
        Assert.Equal(expected.OrderBy(p => p.Key, StringComparer.Ordinal), ReadGauge().OrderBy(p => p.Key, StringComparer.Ordinal));

    private RunningSagasRefreshHostedService Service(IServiceProvider provider, TimeSpan? interval = null)
    {
        var service = new RunningSagasRefreshHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new RunningSagasOptions { RefreshInterval = interval ?? Interval },
            _clock,
            _log);
        _disposables.Add(new DisposableAdapter(service.Dispose));
        return service;
    }

    private ServiceProvider Provider(ScriptedReader? reader = null, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        if (reader is not null)
            services.AddSingleton<ISagaSummaryReader>(reader);
        configure?.Invoke(services);
        var provider = services.BuildServiceProvider(validateScopes: true);
        _disposables.Add(provider);
        return provider;
    }

    [Fact]
    public async Task TheGauge_ReportsTheCountOfEachType_AsTheReaderAnswers()
    {
        var reader = new ScriptedReader { Counts = Tally(("OrderSaga", 4), ("InvoiceSaga", 1)) };
        var service = Service(Provider(reader));

        Assert.True(await service.RefreshAsync(CancellationToken.None));

        AssertGauge(Tally(("OrderSaga", 4), ("InvoiceSaga", 1)));
        Assert.Equal([SagaStatus.Running], reader.AskedStatuses);
    }

    [Fact]
    public async Task ATypeThatStoppedRunning_IsReportedAsZero_NotDropped()
    {
        var reader = new ScriptedReader { Counts = Tally(("OrderSaga", 2), ("InvoiceSaga", 1)) };
        var service = Service(Provider(reader));
        await service.RefreshAsync(CancellationToken.None);

        reader.Counts = Tally(("OrderSaga", 1));
        await service.RefreshAsync(CancellationToken.None);

        AssertGauge(Tally(("OrderSaga", 1), ("InvoiceSaga", 0)));
    }

    [Fact]
    public async Task AStoreWithNothingRunning_ReportsNothingUntilATypeHasBeenSeen()
    {
        var reader = new ScriptedReader { Counts = Tally() };
        var service = Service(Provider(reader));

        await service.RefreshAsync(CancellationToken.None);

        Assert.Empty(ReadGauge());
    }

    [Fact]
    public async Task AFailedRead_KeepsThePreviousReading_UntilThreeFailInARow_ThenWithdrawsIt_AndASuccessPublishesAgain()
    {
        var reader = new ScriptedReader { Counts = Tally(("OrderSaga", 3)) };
        var service = Service(Provider(reader));
        await service.RefreshAsync(CancellationToken.None);

        reader.Fail = true;
        await service.RefreshAsync(CancellationToken.None);
        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal(3, ReadGauge()["OrderSaga"]);
        Assert.Equal(2, _log.Messages(LogLevel.Error).Count);

        await service.RefreshAsync(CancellationToken.None);
        Assert.Empty(ReadGauge());
        Assert.Contains(_log.Messages(LogLevel.Error), m => m.Contains("no longer reported", StringComparison.Ordinal));

        reader.Fail = false;
        reader.Counts = Tally(("OrderSaga", 5));
        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal(5, ReadGauge()["OrderSaga"]);
    }

    [Fact]
    public async Task ASuccessBetweenFailures_StartsTheCountOfFailuresOver()
    {
        var reader = new ScriptedReader { Counts = Tally(("OrderSaga", 3)) };
        var service = Service(Provider(reader));

        foreach (var fail in new[] { false, true, true, false, true, true })
        {
            reader.Fail = fail;
            await service.RefreshAsync(CancellationToken.None);
        }

        Assert.Equal(3, ReadGauge()["OrderSaga"]);
    }

    [Fact]
    public async Task ARead_ThatOutlastsTheInterval_IsCancelled_AndCountsAsAFailure()
    {
        var reader = new ScriptedReader { Counts = Tally(("OrderSaga", 3)), Hang = true };
        var service = Service(Provider(reader));

        var refresh = service.RefreshAsync(CancellationToken.None);
        await reader.Started.Task;
        _clock.Advance(Interval);

        Assert.True(await refresh);
        Assert.Single(_log.Messages(LogLevel.Error));
        Assert.Empty(ReadGauge());
    }

    [Fact]
    public async Task AStopRequest_PropagatesCancellation_InsteadOfCountingAsAFailure()
    {
        var reader = new ScriptedReader { Counts = Tally(("OrderSaga", 3)), Hang = true };
        var service = Service(Provider(reader));
        using var stopping = new CancellationTokenSource();

        var refresh = service.RefreshAsync(stopping.Token);
        await reader.Started.Task;
        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.Empty(_log.Messages(LogLevel.Error));
    }

    [Fact]
    public async Task WithoutAReader_TheServiceWarnsOnce_ReportsNothing_AndStops()
    {
        var service = Service(Provider());

        Assert.False(await service.RefreshAsync(CancellationToken.None));

        Assert.Single(_log.Messages(LogLevel.Warning));
        Assert.Empty(ReadGauge());
    }

    [Fact]
    public async Task DisposingTheService_WithdrawsItsReading()
    {
        var reader = new ScriptedReader { Counts = Tally(("OrderSaga", 3)) };
        var service = Service(Provider(reader));
        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal(1, RunningSagasMetrics.PublishedReadings);

        service.Dispose();

        Assert.Equal(0, RunningSagasMetrics.PublishedReadings);
        Assert.Empty(ReadGauge());
    }

    /// <summary>
    /// The reader is scoped here, as an EF Core context is, and the provider validates scopes: resolving it from the
    /// root would throw. Each read gets a reader of its own, disposed with its scope, so nothing scoped is held between
    /// two reads (the captive dependency).
    /// </summary>
    [Fact]
    public async Task EachRead_ResolvesTheReaderFromAScopeOfItsOwn_AndReleasesIt()
    {
        var readers = new List<ScriptedReader>();
        var provider = Provider(configure: services => services.AddScoped<ISagaSummaryReader>(_ =>
        {
            var reader = new ScriptedReader { Counts = Tally(("OrderSaga", readers.Count + 1)) };
            readers.Add(reader);
            return reader;
        }));
        var service = Service(provider);

        await service.RefreshAsync(CancellationToken.None);
        await service.RefreshAsync(CancellationToken.None);

        Assert.Equal(2, readers.Count);
        Assert.All(readers, r => Assert.True(r.Disposed, "A reader outlived its read"));
        Assert.Equal(2, ReadGauge()["OrderSaga"]);
    }

    [Fact]
    public async Task TheRunningService_RefreshesAtStart_AndEveryInterval()
    {
        var reader = new ScriptedReader { Counts = Tally(("OrderSaga", 1)) };
        var service = Service(Provider(reader));

        await service.StartAsync(CancellationToken.None);
        await reader.WaitForCallsAsync(1);
        Assert.Equal(1, ReadGauge()["OrderSaga"]);

        reader.Counts = Tally(("OrderSaga", 7));
        _clock.Advance(Interval);
        await reader.WaitForCallsAsync(2);
        await Eventually(() => ReadGauge()["OrderSaga"] == 7);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OverTheInMemoryStore_TheGaugeCountsRunningInstances_AndFollowsTheirFinish()
    {
        var provider = Provider(configure: services => services.AddVSagaInMemoryPersistence());
        await using (var scope = provider.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ISagaSnapshotStore<ProbeState>>();
            await store.InsertAsync(NewState("OrderSaga"));
            await store.InsertAsync(NewState("OrderSaga"));
            await store.InsertAsync(NewState("InvoiceSaga"));
            await store.InsertAsync(NewState("InvoiceSaga", SagaStatus.Completed));
        }

        var service = Service(provider);
        await service.RefreshAsync(CancellationToken.None);

        AssertGauge(Tally(("OrderSaga", 2), ("InvoiceSaga", 1)));
    }

    /// <summary>
    /// A real scoped store: EF Core's <c>DbContext</c> is scoped, and the provider validates scopes, so a service that
    /// held the reader across reads, or resolved it from the root, would fail here.
    /// </summary>
    [Fact]
    public async Task OverEfCore_ATransitionBetweenTwoReads_ShowsInTheSecond()
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = $"vsaga-running-{Guid.NewGuid():N}", Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared }.ToString();
        var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        _disposables.Add(new DisposableAdapter(() =>
        {
            SqliteConnection.ClearPool(keepAlive);
            keepAlive.Dispose();
        }));

        var provider = Provider(configure: services => services.AddVSagaEfCore(o => o.UseSqlite(connectionString)));
        var finishing = NewState("OrderSaga");
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<VSagaDbContext>().Database.EnsureCreatedAsync();
            var store = scope.ServiceProvider.GetRequiredService<ISagaSnapshotStore<ProbeState>>();
            await store.InsertAsync(finishing);
            await store.InsertAsync(NewState("OrderSaga"));
            await store.InsertAsync(NewState("InvoiceSaga"));
        }

        var service = Service(provider);
        await service.RefreshAsync(CancellationToken.None);
        AssertGauge(Tally(("OrderSaga", 2), ("InvoiceSaga", 1)));

        await using (var scope = provider.CreateAsyncScope())
        {
            finishing.Status = SagaStatus.Completed;
            await scope.ServiceProvider.GetRequiredService<ISagaSnapshotStore<ProbeState>>().UpdateAsync(finishing, finishing.Version);
        }

        await service.RefreshAsync(CancellationToken.None);
        AssertGauge(Tally(("OrderSaga", 1), ("InvoiceSaga", 1)));
    }

    private ProbeState NewState(string sagaType, SagaStatus status = SagaStatus.Running) => new()
    {
        CorrelationId = Guid.NewGuid(),
        SagaType = sagaType,
        CurrentState = "Started",
        Status = status,
        CreatedAtUtc = _clock.GetUtcNow(),
        UpdatedAtUtc = _clock.GetUtcNow(),
    };

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was never met");
            await Task.Delay(10);
        }
    }

    private sealed class ProbeState : SagaState;

    private sealed class DisposableAdapter(Action dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingLogger : ILogger<RunningSagasRefreshHostedService>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<string> Messages(LogLevel level)
        {
            lock (_entries)
                return [.. _entries.Where(e => e.Level == level).Select(e => e.Message)];
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>A reader whose count is a script: what it answers, whether it throws, and whether it never returns until cancelled.</summary>
    private sealed class ScriptedReader : ISagaSummaryReader, IDisposable
    {
        private int _calls;

        public IReadOnlyDictionary<string, long> Counts { get; set; } = new Dictionary<string, long>(StringComparer.Ordinal);

        public bool Fail { get; set; }

        public bool Hang { get; set; }

        public bool Disposed { get; private set; }

        public List<SagaStatus> AskedStatuses { get; } = [];

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose() => Disposed = true;

        public Task WaitForCallsAsync(int calls) => Eventually(() => Volatile.Read(ref _calls) >= calls);

        public async Task<IReadOnlyDictionary<string, long>> CountByTypeAsync(SagaStatus status, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            AskedStatuses.Add(status);
            Started.TrySetResult();
            if (Hang)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return Fail ? throw new InvalidOperationException("The store is down") : Counts;
        }

        public Task<PagedResult<SagaSummary>> ListAsync(SagaListFilter filter, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SagaSummary?> GetAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> GetDataJsonAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<SagaSummary>> FindByCorrelationIdAsync(Guid correlationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<SagaSummary>> FindChildrenAsync(string parentSagaType, Guid parentCorrelationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<SagaTypeInfo>> GetSagaTypesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
