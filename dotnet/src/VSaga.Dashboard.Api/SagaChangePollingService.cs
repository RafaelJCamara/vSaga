using VSaga.Abstractions.Persistence;
using VSaga.Dashboard.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace VSaga.Dashboard.Api;

/// <summary>
/// Saga processing typically happens in a different process than the dashboard (e.g. the sample's
/// OrderProcessing host) — <see cref="Hubs.SignalRSagaChangeNotifier"/> only fires for an orchestrator
/// running in *this* process, which won't happen unless the dashboard is also configured with its own
/// AddSaga&lt;&gt; registrations. This background poller is what actually delivers near-live updates
/// for the common case: it periodically diffs the store against what it saw last tick and pushes
/// SignalR updates for whatever changed. A future VSaga.Chaos/scale-out story could replace this with
/// a message-bus-relayed push; polling every second is a reasonable v1 trade-off.
/// </summary>
internal sealed class SagaChangePollingService(
    IServiceScopeFactory scopeFactory,
    IHubContext<SagaHub, ISagaHubClient> hub,
    ILogger<SagaChangePollingService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>Rows fetched per <c>ListAsync</c> round trip while draining a tick.</summary>
    internal const int PageSize = 100;

    /// <summary>
    /// Ceiling on how many pages that actually contained changes one tick will drain. Without it a
    /// large backlog (a replay, or the sample under sustained load) would keep a single tick pushing
    /// for an unbounded time and starve the loop. Pages that contain no changes don't count against
    /// it: burning the budget on them would let a big-but-quiet store stop the poller from ever
    /// reaching the changed tail, which would be worse than the bug this bound exists to contain.
    /// <para>
    /// Now that the query itself carries <see cref="SagaListFilter.UpdatedSince"/>, a reader that
    /// honours it never returns a page of unchanged rows in the first place, so that carve-out is
    /// inert for the in-tree providers. It is kept because the property is optional: a third-party
    /// <see cref="ISagaSummaryReader"/> written against the older contract silently ignores it and
    /// falls back to exactly the whole-table scan this rule was written for.
    /// </para>
    /// </summary>
    internal const int MaxChangePagesPerTick = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var since = DateTimeOffset.UtcNow;
        using var timer = new PeriodicTimer(PollInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                since = await PollOnceAsync(since, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Deliberately swallowed so one bad tick (a transient database blip, say) doesn't tear
                // down the loop and silently end live updates for every connected dashboard. `since` is
                // left untouched, so whatever changed during the failed tick is picked up by the next one.
                logger.LogError(ex, "Error polling for saga changes");
            }
        }
    }

    /// <summary>
    /// One poll tick: diffs the store against <paramref name="since"/> and pushes a SignalR update for
    /// each change, returning the new watermark to poll from next time.
    /// <para>
    /// Walks pages sorted by <see cref="SagaSortColumn.UpdatedAt"/> <em>ascending</em> and drains them
    /// all rather than reading one capped page. Reading a single page of the default
    /// most-recently-updated-first ordering while still advancing the watermark to the newest row seen
    /// silently dropped every change past that page: those rows were older than the new watermark, so
    /// no later tick ever looked at them again. The pages are narrowed server-side by
    /// <see cref="SagaListFilter.UpdatedSince"/>, so the drain starts at the changed tail instead of
    /// paging through the unchanged head of the table to find it.
    /// </para>
    /// <para>
    /// The invariant that makes this safe: rows are consumed oldest-change-first, and the returned
    /// watermark is the timestamp of a row this tick actually pushed. Anything not reached is therefore
    /// strictly newer than the watermark, so the next tick's <c>&gt; since</c> window still contains it.
    /// Stopping early (<see cref="MaxChangePagesPerTick"/>) can only cost latency, never an update.
    /// </para>
    /// <para>
    /// Split out of the timer loop so the diff/push logic is directly testable without driving a real
    /// <see cref="PeriodicTimer"/> — the alternative was a test that advances a clock and races the
    /// background task's continuation.
    /// </para>
    /// </summary>
    internal async Task<DateTimeOffset> PollOnceAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        // ISagaSummaryReader is Scoped (EF Core's DbContext needs to be) — this singleton
        // background service opens a fresh scope per tick rather than capturing one instance.
        await using var scope = scopeFactory.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<ISagaSummaryReader>();

        var page = 1;
        var changePagesDrained = 0;
        var stoppedEarly = false;

        // Newest timestamp pushed this tick, and the newest one strictly below it — see the watermark
        // choice at the bottom for why the runner-up is worth tracking.
        DateTimeOffset? newest = null;
        DateTimeOffset? newestBelowFinalTie = null;

        while (true)
        {
            var result = await reader.ListAsync(BuildChangeFilter(since, page), cancellationToken);

            var drainedThisPage = false;

            foreach (var summary in result.Items)
            {
                // Strictly greater: a row stamped exactly at the watermark was pushed by the tick that
                // set it, and `>=` would re-push it every tick forever. Redundant against a reader that
                // honours UpdatedSince (both in-tree providers apply the identical `>` predicate), but
                // kept rather than deleted: UpdatedSince is an optional property on a public filter, so
                // an ISagaSummaryReader implemented outside this repo compiles fine while ignoring it
                // and would hand back the whole table. Keeping the check means such a reader degrades
                // to the previous scan-everything behaviour instead of re-pushing every saga in the
                // store on every tick, and it costs one comparison per row.
                if (summary.UpdatedAtUtc <= since)
                    continue;

                await hub.Clients.Group(SagaHub.ListGroup).SagaUpdated(summary);
                await hub.Clients.Group(SagaHub.GroupForSaga(summary.SagaType, summary.CorrelationId)).SagaUpdated(summary);

                if (newest is { } previous && previous < summary.UpdatedAtUtc)
                    newestBelowFinalTie = previous;

                newest = summary.UpdatedAtUtc;
                drainedThisPage = true;
            }

            if (HasDrainedEveryChange(result, page))
                break;

            if (drainedThisPage && ++changePagesDrained >= MaxChangePagesPerTick)
            {
                stoppedEarly = true;
                break;
            }

            page++;
        }

        // Advanced only after the pushes succeed: if one throws, ExecuteAsync's catch leaves the old
        // watermark in place and the next tick retries the same window rather than skipping past it.
        return ResolveWatermark(since, newest, newestBelowFinalTie, stoppedEarly);
    }

    /// <summary>
    /// The one query shape this service issues. <c>UpdatedSince</c> pushes the watermark into the query
    /// so the ascending drain starts at the changed tail; without it the store returns the whole table
    /// oldest-first and every tick pages through (and discards) every row at or below the watermark
    /// before reaching anything new — O(table size) round trips per second on a large store.
    /// </summary>
    private static SagaListFilter BuildChangeFilter(DateTimeOffset since, int page) =>
        new()
        {
            UpdatedSince = since,
            Page = page,
            PageSize = PageSize,
            SortBy = SagaSortColumn.UpdatedAt,
            SortDescending = false,
        };

    /// <summary>
    /// A short page, or one that reaches the reported total, means everything the filter selected was
    /// walked. Both conditions survive <c>UpdatedSince</c> narrowing the result set, because both are
    /// expressed purely in terms of that set rather than the table:
    /// <list type="bullet">
    /// <item><c>since</c> is fixed for the whole drain, so every page is an offset into the *same*
    /// filtered, ascending sequence. Page N therefore skips exactly the rows pages 1..N-1 already
    /// pushed — the offsets shift meaning along with TotalCount, consistently.</item>
    /// <item>TotalCount is the count of changed rows, not of the table, so <c>page * PageSize &gt;=
    /// TotalCount</c> reads "have we offset past every changed row?" — exactly the stop condition
    /// wanted. It fires at a lower page number than a table-wide count would, and that is correct
    /// rather than premature: the pages it skips hold only rows at or below the watermark, which this
    /// tick would have discarded anyway.</item>
    /// <item>A short page still means the filtered sequence has no further rows.</item>
    /// </list>
    /// And if a reader ignores <c>UpdatedSince</c> entirely, TotalCount is the whole table again and
    /// both conditions read exactly as they did before it existed.
    /// </summary>
    private static bool HasDrainedEveryChange(PagedResult<SagaSummary> result, int page) =>
        result.Items.Count < PageSize || (long)page * PageSize >= result.TotalCount;

    /// <summary>
    /// Picks the watermark the next tick starts from. Nothing pushed leaves it untouched. A complete
    /// drain advances to the newest row pushed.
    /// <para>
    /// A truncated drain is the interesting case: rows never read may share the newest pushed
    /// timestamp — two sagas updated in the same clock tick can straddle the point the drain gave up
    /// at, and a strict <c>&gt; watermark</c> window would skip the ones left behind. Retreating to the
    /// newest timestamp strictly below that trailing tie group makes the next tick re-push the group,
    /// which is harmless (<c>SagaUpdated</c> is a full-summary upsert on the client) where a drop is
    /// not. When every row drained shares one timestamp there is nothing to retreat to, and holding
    /// position would re-drain the identical page set forever, so the tie is accepted and the watermark
    /// advances.
    /// </para>
    /// </summary>
    private static DateTimeOffset ResolveWatermark(
        DateTimeOffset since, DateTimeOffset? newest, DateTimeOffset? newestBelowFinalTie, bool stoppedEarly)
    {
        if (newest is not { } watermark)
            return since;

        return stoppedEarly ? newestBelowFinalTie ?? watermark : watermark;
    }
}
