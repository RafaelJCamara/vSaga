using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VSaga.Observability;

/// <summary>
/// Reads the store on an interval for how many instances are <c>Running</c>, per saga type, and caches the answer for
/// the <c>vsaga.saga.running</c> gauge (ADR 0013). It holds an <see cref="IServiceScopeFactory"/>, never a store: the
/// reader is resolved from a scope created for each read and released with it, so nothing scoped (an EF Core
/// <c>DbContext</c>) is captured by a singleton, the shape the timeout dispatcher had before it was fixed.
/// </summary>
internal sealed class RunningSagasRefreshHostedService : BackgroundService
{
    /// <summary>After this many failed reads in a row the last reading is withdrawn: a dead read must not look like a quiet store.</summary>
    private const int WithdrawAfterFailures = 3;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RunningSagasOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RunningSagasRefreshHostedService> _logger;

    /// <summary>
    /// Every saga type this process has seen running. The reader omits a type with none, so a type that was running and
    /// no longer is reported as <c>0</c> for as long as the process lives: an alert rule sees a recovery as a value.
    /// </summary>
    private readonly HashSet<string> _seenTypes = new(StringComparer.Ordinal);
    private int _consecutiveFailures;

    public RunningSagasRefreshHostedService(
        IServiceScopeFactory scopeFactory,
        RunningSagasOptions options,
        TimeProvider timeProvider,
        ILogger<RunningSagasRefreshHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.RefreshInterval, _timeProvider);

        do
        {
            if (!await RefreshAsync(stoppingToken))
                return;
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public override void Dispose()
    {
        RunningSagasMetrics.Withdraw(this);
        base.Dispose();
    }

    /// <summary>
    /// One read that never throws except for cancellation of <paramref name="stoppingToken"/>: a failure is logged, and
    /// the previous reading kept until it has failed <see cref="WithdrawAfterFailures"/> times running. Each read has the
    /// refresh interval as its deadline, so a hung store call cannot hold the loop. Returns false when there is no
    /// reader to read from, which ends the service.
    /// </summary>
    internal async Task<bool> RefreshAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timeout = new CancellationTokenSource(_options.RefreshInterval, _timeProvider);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var reader = scope.ServiceProvider.GetService<ISagaSummaryReader>();
            if (reader is null)
            {
                _logger.LogWarning("The vsaga.saga.running gauge needs an ISagaSummaryReader, and none is registered; it will not be reported");
                return false;
            }

            Publish(await reader.CountByTypeAsync(SagaStatus.Running, deadline.Token));
            _consecutiveFailures = 0;
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _consecutiveFailures++;
            _logger.LogError(ex, "Reading the number of running sagas failed ({Failures} in a row)", _consecutiveFailures);
            if (_consecutiveFailures == WithdrawAfterFailures)
            {
                RunningSagasMetrics.Withdraw(this);
                _logger.LogError("Reading the number of running sagas has failed {Failures} times in a row; vsaga.saga.running is no longer reported until a read succeeds", _consecutiveFailures);
            }
        }

        return true;
    }

    private void Publish(IReadOnlyDictionary<string, long> counts)
    {
        _seenTypes.UnionWith(counts.Keys);
        RunningSagasMetrics.Publish(this, _seenTypes.ToDictionary(type => type, type => counts.GetValueOrDefault(type), StringComparer.Ordinal));
    }
}
