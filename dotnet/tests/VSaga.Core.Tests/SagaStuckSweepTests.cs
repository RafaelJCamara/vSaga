using System.Diagnostics.Metrics;
using VSaga.Abstractions.Diagnostics;
using VSaga.Abstractions.Notifications;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Core.Runtime;
using VSaga.Persistence.InMemory;
using VSaga.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace VSaga.Core.Tests;

/// <summary>
/// docs/design/stuck-saga-detection.md §5 and §7: the sweep over a real in-memory store with a fake clock. The gauge is
/// process-wide, so every case that reads it lives in this one class, which xUnit runs one case at a time, and each
/// disposes its provider, which withdraws the host's reading. <c>TestOrderSaga</c> declares one timeout, five minutes in
/// <c>AwaitingPayment</c>.
/// </summary>
public sealed class SagaStuckSweepTests : IAsyncLifetime
{
    private const string AwaitingPayment = nameof(TestOrderSaga.AwaitingPayment);
    private const string AwaitingInventory = nameof(TestOrderSaga.AwaitingInventory);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly CapturingLogger _log = new();
    private readonly ToggleableReader _reader = new();
    private ServiceProvider? _provider;

    private ServiceProvider Provider => _provider ?? throw new InvalidOperationException("Build the host first.");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_provider is not null)
            await Provider.DisposeAsync();

        Assert.True(SagaStuckMetrics.PublishedReadings == 0, "A reading outlived its host");
    }

    private SagaStuckSweepHostedService Build(Action<SagaStuckOptions>? configure = null, bool configureStuckDetection = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ILogger<SagaStuckSweepHostedService>>(_log);
        services.AddSingleton<TimeProvider>(_clock);
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddSingleton<ISagaSummaryReader>(sp => _reader.Wrapping(sp.GetRequiredService<InMemorySagaStore>()));
        services.AddVSagaEngine(o =>
        {
            o.AddSaga<TestOrderSaga, TestOrderSagaState>();
            if (configureStuckDetection)
                o.ConfigureStuckDetection(configure);
        });

        _provider = services.BuildServiceProvider();
        return _provider.GetServices<IHostedService>().OfType<SagaStuckSweepHostedService>().Single();
    }

    private async Task<Guid> InsertAsync(string currentState, SagaStatus status, TimeSpan idle)
    {
        await using var scope = Provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        var state = new TestOrderSagaState
        {
            CorrelationId = Guid.NewGuid(),
            SagaType = Provider.GetRequiredService<TestOrderSaga>().SagaType,
            CurrentState = currentState,
            Status = status,
            CreatedAtUtc = _clock.GetUtcNow() - TimeSpan.FromDays(1),
            UpdatedAtUtc = _clock.GetUtcNow() - idle,
        };
        await store.InsertAsync(state);
        return state.CorrelationId;
    }

    private async Task TouchAsync(Guid correlationId)
    {
        await using var scope = Provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        var state = (await store.FindAsync(Provider.GetRequiredService<TestOrderSaga>().SagaType, correlationId))!;
        state.UpdatedAtUtc = _clock.GetUtcNow();
        await store.UpdateAsync(state, state.Version);
    }

    private static Dictionary<(string SagaType, string Cause), long> ReadGauge()
    {
        var readings = new Dictionary<(string, string), long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (string.Equals(instrument.Meter.Name, VSagaDiagnostics.MeterName, StringComparison.Ordinal) && string.Equals(instrument.Name, "vsaga.saga.stuck", StringComparison.Ordinal))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? type = null, cause = null;
            foreach (var tag in tags)
            {
                if (string.Equals(tag.Key, VSagaDiagnostics.TagSagaType, StringComparison.Ordinal))
                    type = (string?)tag.Value;
                else if (string.Equals(tag.Key, VSagaDiagnostics.TagStuckCause, StringComparison.Ordinal))
                    cause = (string?)tag.Value;
            }

            readings[(type!, cause!)] = value;
        });
        listener.Start();
        listener.RecordObservableInstruments();
        return readings;
    }

    [Fact]
    public async Task EachCauseIsCounted_AndOnlyTheStuckAreLogged()
    {
        var sweep = Build();
        var overdue = await InsertAsync(AwaitingPayment, SagaStatus.Running, TimeSpan.FromMinutes(8));
        var idle = await InsertAsync(AwaitingInventory, SagaStatus.Running, TimeSpan.FromHours(2));
        var compensating = await InsertAsync(AwaitingPayment, SagaStatus.Compensating, TimeSpan.FromHours(2));
        await InsertAsync(AwaitingPayment, SagaStatus.Running, TimeSpan.FromMinutes(6));      // within its timeout plus the grace
        await InsertAsync(AwaitingInventory, SagaStatus.Running, TimeSpan.FromMinutes(30));   // idle, but under the threshold
        await InsertAsync(AwaitingPayment, SagaStatus.Failed, TimeSpan.FromDays(3));          // terminal, however old

        await sweep.RunSweepAsync(CancellationToken.None);

        var type = Provider.GetRequiredService<TestOrderSaga>().SagaType;
        var gauge = ReadGauge();
        Assert.Equal(1, gauge[(type, nameof(StuckCause.TimeoutOverdue))]);
        Assert.Equal(1, gauge[(type, nameof(StuckCause.IdleWithoutTimeout))]);
        Assert.Equal(1, gauge[(type, nameof(StuckCause.CompensationIdle))]);
        var warnings = _log.Messages(LogLevel.Warning);
        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, w => w.Contains(overdue.ToString(), StringComparison.Ordinal) && w.Contains("TimeoutOverdue", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains(idle.ToString(), StringComparison.Ordinal) && w.Contains("IdleWithoutTimeout", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains(compensating.ToString(), StringComparison.Ordinal) && w.Contains("CompensationIdle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASagaIsReportedOnceWhileItStaysStuck_AndAgainOnlyAfterItHasRecovered()
    {
        var sweep = Build();
        var saga = await InsertAsync(AwaitingInventory, SagaStatus.Running, TimeSpan.FromHours(2));

        await sweep.RunSweepAsync(CancellationToken.None);
        await sweep.RunSweepAsync(CancellationToken.None);
        Assert.Single(_log.Messages(LogLevel.Warning));
        Assert.Empty(_log.Messages(LogLevel.Information));

        await TouchAsync(saga);
        await sweep.RunSweepAsync(CancellationToken.None);
        var recovered = Assert.Single(_log.Messages(LogLevel.Information));
        Assert.Contains(saga.ToString(), recovered, StringComparison.Ordinal);
        Assert.Equal(0, ReadGauge()[(Provider.GetRequiredService<TestOrderSaga>().SagaType, nameof(StuckCause.IdleWithoutTimeout))]);

        _clock.Advance(TimeSpan.FromHours(2));
        await sweep.RunSweepAsync(CancellationToken.None);
        Assert.Equal(2, _log.Messages(LogLevel.Warning).Count);
    }

    [Fact]
    public async Task ASweepWithNothingStuck_ReportsZeroForEveryCause()
    {
        var sweep = Build();
        await InsertAsync(AwaitingInventory, SagaStatus.Running, TimeSpan.FromMinutes(5));

        await sweep.RunSweepAsync(CancellationToken.None);

        var type = Provider.GetRequiredService<TestOrderSaga>().SagaType;
        var gauge = ReadGauge();
        Assert.Equal(Enum.GetValues<StuckCause>().Length, gauge.Count);
        Assert.All(gauge, reading => Assert.Equal(0, reading.Value));
        Assert.All(gauge.Keys, key => Assert.Equal(type, key.SagaType));
        Assert.Empty(_log.Messages(LogLevel.Warning));
    }

    [Fact]
    public async Task APerSagaTypeOverrideAndAnExclusion_ChangeWhatTheSweepReads()
    {
        var sweep = Build(o => o.IdleThresholdBySagaType[nameof(TestOrderSaga)] = TimeSpan.FromMinutes(10));
        await InsertAsync(AwaitingInventory, SagaStatus.Running, TimeSpan.FromMinutes(30));

        await sweep.RunSweepAsync(CancellationToken.None);

        Assert.Equal(1, ReadGauge()[(nameof(TestOrderSaga), nameof(StuckCause.IdleWithoutTimeout))]);
        await Provider.DisposeAsync();

        var excluded = Build(o => o.ExcludedSagaTypes.Add(nameof(TestOrderSaga)));
        await InsertAsync(AwaitingInventory, SagaStatus.Running, TimeSpan.FromDays(1));
        await excluded.RunSweepAsync(CancellationToken.None);

        Assert.Empty(ReadGauge());
    }

    [Fact]
    public async Task OnlyTheLimitIsLoggedOneByOne_AndTheRestIsCountedInOneLine()
    {
        var sweep = Build(o => o.MaxReportedPerSweep = 2);
        for (var i = 0; i < 5; i++)
            await InsertAsync(AwaitingInventory, SagaStatus.Running, TimeSpan.FromHours(2));

        await sweep.RunSweepAsync(CancellationToken.None);

        var warnings = _log.Messages(LogLevel.Warning);
        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, w => w.StartsWith("3 more TestOrderSaga instances are stuck", StringComparison.Ordinal));
        Assert.Equal(5, ReadGauge()[(nameof(TestOrderSaga), nameof(StuckCause.IdleWithoutTimeout))]);
    }

    [Fact]
    public async Task AReadStoppedByTheScanLimit_IsCountedAsALowerBoundAndSaysSo()
    {
        var sweep = Build(o => o.MaxScannedPerType = 1);
        for (var i = 0; i < 201; i++)
            await InsertAsync(AwaitingInventory, SagaStatus.Running, TimeSpan.FromHours(2));

        await sweep.RunSweepAsync(CancellationToken.None);

        Assert.Equal(200, ReadGauge()[(nameof(TestOrderSaga), nameof(StuckCause.IdleWithoutTimeout))]);
        Assert.Contains(_log.Messages(LogLevel.Warning), w => w.Contains("lower bound", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedSweepKeepsTheLastReading_ThreeInARowWithdrawItAndASuccessRestoresIt()
    {
        var sweep = Build();
        await InsertAsync(AwaitingInventory, SagaStatus.Running, TimeSpan.FromHours(2));
        var key = (nameof(TestOrderSaga), nameof(StuckCause.IdleWithoutTimeout));
        await sweep.RunSweepAsync(CancellationToken.None);
        Assert.Equal(1, ReadGauge()[key]);

        _reader.Failing = true;
        await sweep.RunSweepAsync(CancellationToken.None);
        await sweep.RunSweepAsync(CancellationToken.None);
        Assert.Equal(1, ReadGauge()[key]);

        await sweep.RunSweepAsync(CancellationToken.None);
        Assert.Empty(ReadGauge());
        Assert.Equal(3, _log.Messages(LogLevel.Error).Count(m => m.StartsWith("The stuck-saga sweep failed", StringComparison.Ordinal)));

        _reader.Failing = false;
        await sweep.RunSweepAsync(CancellationToken.None);
        Assert.Equal(1, ReadGauge()[key]);
    }

    [Fact]
    public async Task DisposingTheHost_WithdrawsItsReading()
    {
        var sweep = Build();
        await InsertAsync(AwaitingInventory, SagaStatus.Running, TimeSpan.FromHours(2));
        await sweep.RunSweepAsync(CancellationToken.None);
        Assert.NotEmpty(ReadGauge());

        sweep.Dispose();
        Assert.Empty(ReadGauge());
    }

    [Fact]
    public async Task DisposingTheProvider_WithdrawsTheHostsReading()
    {
        var sweep = Build();
        await InsertAsync(AwaitingInventory, SagaStatus.Running, TimeSpan.FromHours(2));
        await sweep.RunSweepAsync(CancellationToken.None);

        await Provider.DisposeAsync();

        Assert.Empty(ReadGauge());
    }

    [Fact]
    public async Task WithoutConfigureStuckDetection_TheSweepIsOffAndDoesNothingWhenStarted()
    {
        var sweep = Build(configureStuckDetection: false);
        await InsertAsync(AwaitingInventory, SagaStatus.Running, TimeSpan.FromDays(1));

        await sweep.StartAsync(CancellationToken.None);
        await sweep.StopAsync(CancellationToken.None);

        Assert.Empty(ReadGauge());
        Assert.Empty(_log.Messages(LogLevel.Warning));
        Assert.False(Provider.GetRequiredService<SagaStuckOptions>().Enabled);
    }

    [Fact]
    public async Task TheHostedSweep_RunsOnItsIntervalAndReportsWhatBecameStuck()
    {
        var sweep = Build(o => o.SweepInterval = TimeSpan.FromMinutes(1));
        await sweep.StartAsync(CancellationToken.None);
        try
        {
            // The first sweep runs at start-up and finds nothing; a saga that then goes idle is found by the next tick.
            await _reader.WaitForListAsync(1);
            await InsertAsync(AwaitingInventory, SagaStatus.Running, TimeSpan.FromHours(2));

            _clock.Advance(TimeSpan.FromMinutes(1));
            await _reader.WaitForListAsync(2);

            await Task.Delay(100);
            Assert.Equal(1, ReadGauge()[(nameof(TestOrderSaga), nameof(StuckCause.IdleWithoutTimeout))]);
        }
        finally
        {
            await sweep.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void ConfigureStuckDetection_IsTheOptIn_AndAConfigurationCanStillSwitchItOff()
    {
        var on = new ServiceCollection();
        on.AddVSagaEngine(o => o.ConfigureStuckDetection());
        var off = new ServiceCollection();
        off.AddVSagaEngine(o => o.ConfigureStuckDetection(options => options.Enabled = false));
        var unconfigured = new ServiceCollection();
        unconfigured.AddVSagaEngine(_ => { });

        Assert.True(Options(on).Enabled);
        Assert.False(Options(off).Enabled);
        Assert.False(Options(unconfigured).Enabled);
    }

    private static SagaStuckOptions Options(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<SagaStuckOptions>();
    }

    [Fact]
    public void TheDefaults_AreTheOnesTheDesignProposes()
    {
        var options = new SagaStuckOptions();

        Assert.False(options.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(1), options.SweepInterval);
        Assert.Equal(TimeSpan.FromHours(1), options.IdleThreshold);
        Assert.Equal(TimeSpan.FromMinutes(2), options.TimeoutGrace);
        Assert.Equal(200, options.MaxReportedPerSweep);
        Assert.Equal(10_000, options.MaxScannedPerType);
        Assert.Empty(options.IdleThresholdBySagaType);
        Assert.Empty(options.ExcludedSagaTypes);
    }

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void ConfigureStuckDetection_RejectsAValueTheSweepCannotWorkWith(Action<SagaStuckOptions> configure, string parameter)
    {
        var services = new ServiceCollection();

        var thrown = Assert.Throws<ArgumentOutOfRangeException>(() => services.AddVSagaEngine(o => o.ConfigureStuckDetection(configure)));

        Assert.StartsWith(parameter, thrown.ParamName, StringComparison.Ordinal);
    }

    public static TheoryData<Action<SagaStuckOptions>, string> InvalidConfigurations() => new()
    {
        { o => o.SweepInterval = TimeSpan.Zero, nameof(SagaStuckOptions.SweepInterval) },
        { o => o.IdleThreshold = TimeSpan.FromSeconds(-1), nameof(SagaStuckOptions.IdleThreshold) },
        { o => o.IdleThreshold = TimeSpan.FromDays(4000), nameof(SagaStuckOptions.IdleThreshold) },
        { o => o.IdleThresholdBySagaType["OrderSaga"] = TimeSpan.Zero, nameof(SagaStuckOptions.IdleThresholdBySagaType) },
        { o => o.TimeoutGrace = TimeSpan.FromSeconds(-1), nameof(SagaStuckOptions.TimeoutGrace) },
        { o => o.MaxReportedPerSweep = 0, nameof(SagaStuckOptions.MaxReportedPerSweep) },
        { o => o.MaxScannedPerType = 0, nameof(SagaStuckOptions.MaxScannedPerType) },
    };

    private sealed class CapturingLogger : ILogger<SagaStuckSweepHostedService>
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

    /// <summary>The in-memory reader, with a switch that makes <c>ListAsync</c> throw and a count of the lists it has served.</summary>
    private sealed class ToggleableReader : ISagaSummaryReader
    {
        private ISagaSummaryReader _inner = null!;
        private int _lists;

        public bool Failing { get; set; }

        public ToggleableReader Wrapping(ISagaSummaryReader inner)
        {
            _inner = inner;
            return this;
        }

        public async Task WaitForListAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref _lists) < count)
            {
                Assert.True(DateTime.UtcNow < deadline, $"The sweep did not list {count} times in ten seconds.");
                await Task.Delay(10);
            }
        }

        public async Task<PagedResult<SagaSummary>> ListAsync(SagaListFilter filter, CancellationToken cancellationToken = default)
        {
            if (Failing)
                throw new InvalidOperationException("The store is unreachable.");

            var result = await _inner.ListAsync(filter, cancellationToken);
            Interlocked.Increment(ref _lists);
            return result;
        }

        public Task<SagaSummary?> GetAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
            _inner.GetAsync(sagaType, correlationId, cancellationToken);

        public Task<string?> GetDataJsonAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
            _inner.GetDataJsonAsync(sagaType, correlationId, cancellationToken);

        public Task<IReadOnlyList<SagaSummary>> FindByCorrelationIdAsync(Guid correlationId, CancellationToken cancellationToken = default) =>
            _inner.FindByCorrelationIdAsync(correlationId, cancellationToken);

        public Task<IReadOnlyList<SagaSummary>> FindChildrenAsync(string parentSagaType, Guid parentCorrelationId, CancellationToken cancellationToken = default) =>
            _inner.FindChildrenAsync(parentSagaType, parentCorrelationId, cancellationToken);

        public Task<IReadOnlyList<SagaTypeInfo>> GetSagaTypesAsync(CancellationToken cancellationToken = default) =>
            _inner.GetSagaTypesAsync(cancellationToken);
    }
}
