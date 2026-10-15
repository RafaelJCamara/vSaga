using VSaga.Abstractions.Notifications;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VSaga.Core.Runtime;

/// <summary>
/// Reads the store on an interval for the sagas this process hosts that have been idle past their threshold,
/// classifies each (<see cref="SagaStuckClassifier"/>), and reports them: the <c>vsaga.saga.stuck</c> gauge, a warning
/// the first time a saga is seen stuck and an information line when it leaves the set
/// (docs/design/stuck-saga-detection.md §5, §7). Detection only: it never changes a saga.
/// </summary>
internal sealed class SagaStuckSweepHostedService : BackgroundService
{
    private const int PageSize = 200;

    /// <summary>After this many failed sweeps in a row the last reading is withdrawn: a dead sweep must not read as a quiet store.</summary>
    private const int WithdrawAfterFailures = 3;

    private static readonly SagaStatus[] LiveStatuses = [SagaStatus.Running, SagaStatus.Compensating];

    private readonly List<ISagaRuntime> _runtimes;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SagaStuckOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SagaStuckSweepHostedService> _logger;
    private readonly HashSet<(string SagaType, Guid CorrelationId)> _reported = [];
    private int _consecutiveFailures;

    public SagaStuckSweepHostedService(
        IEnumerable<ISagaRuntime> runtimes,
        IServiceScopeFactory scopeFactory,
        SagaStuckOptions options,
        TimeProvider timeProvider,
        ILogger<SagaStuckSweepHostedService> logger)
    {
        _options = options;
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;

        // Only the types this process hosts: it can classify those, and no others (the timeout dispatcher's own rule).
        _runtimes = [.. runtimes.Where(r => !options.ExcludedSagaTypes.Contains(r.SagaType))];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || _runtimes.Count == 0)
            return;

        using var timer = new PeriodicTimer(_options.SweepInterval, _timeProvider);

        do
        {
            await RunSweepAsync(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public override void Dispose()
    {
        SagaStuckMetrics.Withdraw(this);
        base.Dispose();
    }

    /// <summary>One sweep that never throws except for cancellation: a failure is logged, and the previous reading kept until it has failed <see cref="WithdrawAfterFailures"/> times running.</summary>
    internal async Task RunSweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SweepAsync(cancellationToken);
            _consecutiveFailures = 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _consecutiveFailures++;
            _logger.LogError(ex, "The stuck-saga sweep failed ({Failures} in a row)", _consecutiveFailures);
            if (_consecutiveFailures == WithdrawAfterFailures)
            {
                SagaStuckMetrics.Withdraw(this);
                _logger.LogError("The stuck-saga sweep has failed {Failures} times in a row; vsaga.saga.stuck is no longer reported until a sweep succeeds", _consecutiveFailures);
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetService<ISagaSummaryReader>()
                     ?? throw new InvalidOperationException("The stuck-saga sweep needs an ISagaSummaryReader, and none is registered.");

        var now = _timeProvider.GetUtcNow();
        var scans = new List<(ISagaRuntime Runtime, TypeScan Scan)>();
        foreach (var runtime in _runtimes)
            scans.Add((runtime, await ScanAsync(reader, runtime, now, cancellationToken)));

        SagaStuckMetrics.Publish(this, scans.ToDictionary(s => s.Runtime.SagaType, s => Count(s.Scan.Stuck), StringComparer.Ordinal));
        Report(scans.SelectMany(s => s.Scan.Stuck).ToList(), scans.Where(s => s.Scan.Truncated).Select(s => s.Runtime.SagaType));
    }

    /// <summary>
    /// Every instance of one saga type idle past the type's floor, oldest first, in pages, classified. The query is
    /// scoped to the one type because a process reports only what it can classify. A page walk is not a snapshot: an
    /// instance that recovers between two pages shifts the later rows up by one, so a row can be missed for one sweep
    /// and is read on the next.
    /// </summary>
    private async Task<TypeScan> ScanAsync(ISagaSummaryReader reader, ISagaRuntime runtime, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var floor = SagaStuckClassifier.Floor(runtime.SagaType, runtime.ShortestTimeout, _options);
        var stuck = new List<StuckSaga>();
        var scanned = 0;
        var page = 1;

        while (scanned < _options.MaxScannedPerType)
        {
            var result = await reader.ListAsync(new SagaListFilter
            {
                SagaType = runtime.SagaType,
                Statuses = LiveStatuses,
                UpdatedBefore = now - floor,
                SortBy = SagaSortColumn.UpdatedAt,
                Page = page,
                PageSize = PageSize,
            }, cancellationToken);

            stuck.AddRange(result.Items
                .Select(row => SagaStuckClassifier.Classify(row, runtime.GetTimeout(row.CurrentState), _options, now))
                .OfType<StuckSaga>());

            scanned += result.Items.Count;
            page++;
            if (result.Items.Count < PageSize || scanned >= result.TotalCount)
                return new TypeScan(stuck, Truncated: false);
        }

        return new TypeScan(stuck, Truncated: true);
    }

    private static IReadOnlyDictionary<StuckCause, int> Count(IReadOnlyCollection<StuckSaga> stuck) =>
        Enum.GetValues<StuckCause>().ToDictionary(cause => cause, cause => stuck.Count(s => s.Cause == cause));

    /// <summary>
    /// Diffs this sweep's stuck set against the last one: a warning for each saga newly seen stuck and an information
    /// line for each that left, each capped per saga type with the overflow counted in one further line, so a store
    /// that goes quiet all at once logs a bounded amount. A restart forgets the set and so reports what is still stuck again.
    /// </summary>
    private void Report(List<StuckSaga> stuck, IEnumerable<string> truncatedTypes)
    {
        foreach (var sagaType in truncatedTypes)
            _logger.LogWarning("The stuck-saga sweep read {Limit} {SagaType} instances and stopped: more are idle than it will read, so vsaga.saga.stuck is a lower bound", _options.MaxScannedPerType, sagaType);

        var current = stuck.ToDictionary(s => (s.SagaType, s.CorrelationId));
        foreach (var group in current.Values.Where(s => !_reported.Contains((s.SagaType, s.CorrelationId))).GroupBy(s => s.SagaType, StringComparer.Ordinal))
            LogNewlyStuck(group.Key, [.. group]);

        foreach (var group in _reported.Where(key => !current.ContainsKey(key)).GroupBy(key => key.SagaType, StringComparer.Ordinal))
            LogRecovered(group.Key, [.. group]);

        _reported.Clear();
        _reported.UnionWith(current.Keys);
    }

    private void LogNewlyStuck(string sagaType, List<StuckSaga> newlyStuck)
    {
        foreach (var saga in newlyStuck.Take(_options.MaxReportedPerSweep))
        {
            _logger.LogWarning(
                "Saga {SagaType} {CorrelationId} is stuck: {Status} in state {CurrentState}, idle for {IdleFor} (threshold {Threshold}, cause {Cause}, last updated {UpdatedAtUtc:O})",
                saga.SagaType, saga.CorrelationId, saga.Status, saga.CurrentState, saga.IdleFor, saga.Threshold, saga.Cause, saga.UpdatedAtUtc);
        }

        if (newlyStuck.Count > _options.MaxReportedPerSweep)
            _logger.LogWarning("{Count} more {SagaType} instances are stuck, not listed", newlyStuck.Count - _options.MaxReportedPerSweep, sagaType);
    }

    private void LogRecovered(string sagaType, List<(string SagaType, Guid CorrelationId)> recovered)
    {
        foreach (var saga in recovered.Take(_options.MaxReportedPerSweep))
            _logger.LogInformation("Saga {SagaType} {CorrelationId} is no longer stuck", saga.SagaType, saga.CorrelationId);

        if (recovered.Count > _options.MaxReportedPerSweep)
            _logger.LogInformation("{Count} more {SagaType} instances are no longer stuck, not listed", recovered.Count - _options.MaxReportedPerSweep, sagaType);
    }

    private sealed record TypeScan(IReadOnlyCollection<StuckSaga> Stuck, bool Truncated);
}
