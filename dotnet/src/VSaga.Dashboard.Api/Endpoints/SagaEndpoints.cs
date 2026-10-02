using System.Text;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Abstractions.Transport;
using VSaga.Persistence.Redis;

namespace VSaga.Dashboard.Api.Endpoints;

public sealed record SagaDetail(SagaSummary Summary, string? DataJson);

public static class SagaEndpoints
{
    /// <summary>
    /// The largest page the list endpoint serves; a larger <c>pageSize</c> is clamped to it, and the
    /// response's <see cref="PagedResult{T}.PageSize"/> reports the size actually applied. The policy
    /// lives here rather than in the persistence providers, which clamp only to a minimum of 1: a
    /// caller's page size is an HTTP concern, and the providers must not each pick their own maximum
    /// (docs/design/persistence-contracts.md, fix F8). Five times the dashboard's largest page option,
    /// so no legitimate caller notices, and a bound on what one request can materialise.
    /// </summary>
    public const int MaxPageSize = 500;

    public static void MapSagaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sagas").WithTags("Sagas").RequireAuthorization();

        group.MapGet("", ListSagasAsync).WithName("ListSagas");

        // Every per-instance route is {sagaType}/{correlationId}: a correlation id alone no longer
        // identifies a saga instance, since two saga types may track the same one. Callers holding
        // only a correlation id resolve it first via /api/correlations/{correlationId} below.
        group.MapGet("/{sagaType}/{correlationId:guid}", async (string sagaType, Guid correlationId, ISagaSummaryReader reader, CancellationToken ct) =>
        {
            var summary = await reader.GetAsync(sagaType, correlationId, ct);
            if (summary is null)
                return Results.NotFound();

            var dataJson = await reader.GetDataJsonAsync(sagaType, correlationId, ct);
            return Results.Ok(new SagaDetail(summary, dataJson));
        })
        .WithName("GetSaga");

        group.MapGet("/{sagaType}/{correlationId:guid}/timeline", GetSagaTimelineAsync)
        .WithName("GetSagaTimeline");

        group.MapGet("/{sagaType}/{correlationId:guid}/map", GetSagaMapAsync)
        .WithName("GetSagaMap");

        // The sagas this one started via StartChildAsync. Deliberately not 404-ing on an unknown
        // parent: a saga with no children and a saga that does not exist both legitimately have an
        // empty child list, and the caller already has GET /{sagaType}/{correlationId} to tell them
        // apart. Children have their own correlation ids, so this is a different question from
        // /api/correlations/{id}, which finds saga types sharing one id.
        group.MapGet("/{sagaType}/{correlationId:guid}/children", async (string sagaType, Guid correlationId, ISagaSummaryReader reader, CancellationToken ct) =>
            Results.Ok(await reader.FindChildrenAsync(sagaType, correlationId, ct)))
        .WithName("GetSagaChildren");

        group.MapGet("/{sagaType}/{correlationId:guid}/retry-plan", GetRetryPlanAsync)
        .WithName("GetSagaRetryPlan");

        group.MapPost("/{sagaType}/{correlationId:guid}/retry", RetrySagaAsync)
        .WithName("RetrySaga");

        MapCrossInstanceEndpoints(app);
    }

    /// <summary>
    /// The two lookups that are not scoped to one saga instance, so they sit outside the
    /// <c>/api/sagas</c> group rather than under it. Split out of <see cref="MapSagaEndpoints"/> only
    /// for length.
    /// </summary>
    private static void MapCrossInstanceEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/saga-types", async (ISagaSummaryReader reader, CancellationToken ct) => Results.Ok(await reader.GetSagaTypesAsync(ct)))
            .WithTags("Sagas")
            .WithName("ListSagaTypes")
            .RequireAuthorization();

        // Deliberately a separate top-level path rather than /api/sagas/by-correlation/{id}, which
        // would sit in the same slot as {sagaType} and rely on literal-beats-parameter precedence to
        // disambiguate. Returns every saga instance tracking this correlation id — normally one, more
        // than one when several saga types observe the same business transaction. Note this is not the
        // sub-saga relation: a child has its own correlation id and is found via /children instead.
        app.MapGet("/api/correlations/{correlationId:guid}", async (Guid correlationId, ISagaSummaryReader reader, CancellationToken ct) =>
            Results.Ok(await reader.FindByCorrelationIdAsync(correlationId, ct)))
            .WithTags("Sagas")
            .WithName("FindSagasByCorrelationId")
            .RequireAuthorization();
    }

    private static async Task<IResult> ListSagasAsync(ISagaSummaryReader reader, SagaStatus? status, string? sagaType, SagaKind? kind, string? search, int page = 1, int pageSize = 25, SagaSortColumn? sortBy = null, bool sortDescending = false, CancellationToken ct = default)
    {
        var filter = new SagaListFilter
        {
            Status = status,
            SagaType = sagaType,
            Kind = kind,
            Search = search,
            Page = page <= 0 ? 1 : page,
            PageSize = pageSize <= 0 ? 25 : Math.Min(pageSize, MaxPageSize),
            SortBy = sortBy,
            SortDescending = sortDescending,
        };

        try
        {
            return Results.Ok(await reader.ListAsync(filter, ct));
        }
        catch (RedisSearchScanLimitExceededException ex)
        {
            // Redis has no substring index, so its provider bounds a search's scan and refuses above the
            // bound rather than truncating a page (docs/persistence.md, "Search"). The request is the
            // thing to change -- narrow it with a filter -- so it is a 400, not a 500.
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> GetSagaMapAsync(string sagaType, Guid correlationId, ISagaSummaryReader reader, ISagaEventLogStore log, IServiceTopologyStore topologyStore, CancellationToken ct)
    {
        var summary = await reader.GetAsync(sagaType, correlationId, ct);
        if (summary is null)
            return Results.NotFound();

        var timeline = await log.GetTimelineAsync(sagaType, correlationId, ct);
        var topology = await topologyStore.GetAllAsync(ct);

        // Same seam as the timeline: the authentication work supplies the caller's sagas.data answer.
        return Results.Ok(SagaTimelineRedaction.ApplyToMap(SagaMapBuilder.Build(summary, timeline, topology), includeData: true));
    }

    private static async Task<IResult> GetSagaTimelineAsync(string sagaType, Guid correlationId, ISagaEventLogStore log, CancellationToken ct)
    {
        var timeline = await log.GetTimelineAsync(sagaType, correlationId, ct);

        // includeData is fixed until the authentication work supplies it from the caller's sagas.data
        // permission; the seam is here now so payloads and error messages have one exit to guard.
        return Results.Ok(SagaTimelineRedaction.Apply(timeline, includeData: true));
    }

    /// <summary>
    /// What a retry of this saga would re-run, or why it cannot (<see cref="SagaRetryPlanner"/>). Read-only and
    /// never 409/422: a saga that cannot be retried answers 200 with <c>retryable: false</c> and the reason, so
    /// the SPA can still mark the failed step. The plan carries no message body.
    /// </summary>
    private static async Task<IResult> GetRetryPlanAsync(string sagaType, Guid correlationId, ISagaSummaryReader reader, ISagaEventLogStore log, CancellationToken ct)
    {
        var summary = await reader.GetAsync(sagaType, correlationId, ct);
        if (summary is null)
            return Results.NotFound();

        var timeline = await log.GetTimelineAsync(sagaType, correlationId, ct);
        return Results.Ok(SagaRetryPlanner.Plan(summary, timeline));
    }

    /// <summary>
    /// Re-runs the step the saga failed in, for this saga type only (docs/design/dashboard-usability-and-access.md
    /// §7.3, ADR 0008): resets CurrentState/Status to what they were before that step, then republishes only that
    /// step's message with a fresh id (so dedupe lets it through) and the target-saga-type header, which every
    /// other saga type subscribed to the message type acknowledges and ignores. Business fields inside the
    /// stored state are not rolled back.
    /// </summary>
    private static async Task<IResult> RetrySagaAsync(string sagaType, Guid correlationId, ISagaSummaryReader reader, ISagaEventLogStore log, ISagaAdminStore admin,
        IMessageTransport transport, TimeProvider timeProvider, SagaResetSnapshotRecorder snapshots, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var summary = await reader.GetAsync(sagaType, correlationId, ct);
        if (summary is null)
            return Results.NotFound();

        if (summary.Status is not (SagaStatus.Failed or SagaStatus.TimedOut))
            return Results.Conflict(new { error = $"Saga '{sagaType}' instance '{correlationId}' cannot be retried while its status is '{summary.Status}'; only 'Failed' or 'TimedOut' sagas can be retried." });

        var timeline = await log.GetTimelineAsync(sagaType, correlationId, ct);
        var plan = SagaRetryPlanner.Plan(summary, timeline);
        if (plan is not { Retryable: true, Step: { } step, MessageBody: { } body })
            return Results.UnprocessableEntity(new { error = plan.Reason });

        // Appended before the reset, so a 409 or 502 below leaves it in the timeline with no step after it.
        await log.AppendAsync(SagaLogEntry.Create(correlationId, summary.SagaType, SagaEntryType.ManualRetryRequested,
            fromState: summary.CurrentState, toState: step.FromState, messageType: step.MessageType, messageId: step.MessageId), ct);

        try
        {
            // Always, even when the state does not change (a StepFailed or DeliveryExhausted retry): the
            // version the caller read is the concurrency guard, so a saga that moved since answers 409, and
            // the saga leaves Failed at once, so a second click meets the status guard above.
            await admin.ResetStateAsync(sagaType, correlationId, step.FromState, SagaStatus.Running, summary.Version, timeProvider.GetUtcNow(), ct);
        }
        catch (SagaConcurrencyException)
        {
            return Results.Conflict(new { error = $"Saga '{sagaType}' instance '{correlationId}' was modified concurrently with this retry; reload and try again." });
        }

        // From here on the reset is committed, so nothing below listens to the request's token: a client that
        // disconnects now must not leave the saga Running in the step's from-state with no redrive, where the
        // status guard above would refuse every further retry. The work left is a few store calls and one
        // publish, each bounded by its own client's timeouts.
        //
        // The reset wrote a state no engine snapshot describes; record it before the redrive, so it is
        // sequenced ahead of the snapshots the redriven step records. Best effort (it never throws).
        await snapshots.RecordAsync(sagaType, correlationId, summary.Version + 1, timeline, CancellationToken.None);

        return await RedriveAsync(summary, step, body, timeline, admin, transport, timeProvider, snapshots,
            loggerFactory.CreateLogger(typeof(SagaEndpoints).FullName!));
    }

    /// <summary>
    /// The publish tail of <see cref="RetrySagaAsync"/>, split out for length. The redrive is a fresh envelope
    /// under the saga's correlation id carrying only the target-saga-type header. When the publish throws,
    /// whatever the exception, the reset is undone as far as it can be and the 502 says whether it was.
    /// </summary>
    private static async Task<IResult> RedriveAsync(SagaSummary summary, SagaRetryStep step, string body, IReadOnlyList<SagaLogEntry> timeline,
        ISagaAdminStore admin, IMessageTransport transport, TimeProvider timeProvider, SagaResetSnapshotRecorder snapshots, ILogger logger)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessageEnvelope.TargetSagaTypeHeader] = summary.SagaType,
        };

        string cause;
        try
        {
            await transport.PublishRawAsync(step.MessageType, Encoding.UTF8.GetBytes(body), MessageEnvelope.New(summary.CorrelationId, headers), CancellationToken.None);
            return Results.Accepted();
        }
        catch (MessageTransportPublishException ex)
        {
            cause = ex.Message;
        }
        catch (Exception ex)
        {
            // IMessageTransport does not promise to wrap every failure: RabbitMqTransport, for one, lets a
            // TaskCanceledException out of opening a channel on a broker that stopped answering. The reset is
            // committed either way, so it is undone the same way. That is safe even when the publish may have
            // gone out after all: the restore is version-checked, so if the redrive already moved the saga on,
            // the restore loses and reports it.
            logger.LogError(ex, "The retry redrive of {MessageType} for saga {SagaType} correlation {CorrelationId} failed to publish",
                step.MessageType, summary.SagaType, summary.CorrelationId);
            cause = $"The message transport failed to publish the {step.MessageType} message ({ex.GetType().Name}); the dashboard API log has the details.";
        }

        var restore = await RestoreAfterFailedRedriveAsync(summary, step, timeline, admin, timeProvider, snapshots, logger);

        return Results.Problem(statusCode: StatusCodes.Status502BadGateway,
            detail: $"Saga '{summary.SagaType}' instance '{summary.CorrelationId}' could not be retried: {cause} {restore.Outcome}",
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["restored"] = restore.Restored });
    }

    /// <summary>
    /// Best-effort undo of the retry reset after the redrive failed to publish: puts back the state and status
    /// the caller saw, at the version the reset wrote, and records that state like the reset's own. Not
    /// restored, after logging a warning, when the saga moved on since the reset (that newer state stands);
    /// not restored either, after logging an error, when the restore itself fails for any other reason (a store
    /// outage, a timeout, the saga deleted meanwhile). The outcome text never carries exception text; the log
    /// does. Runs without the request's token, like the rest of the work after the reset.
    /// </summary>
    private static async Task<RedriveRestore> RestoreAfterFailedRedriveAsync(SagaSummary summary, SagaRetryStep step, IReadOnlyList<SagaLogEntry> timeline,
        ISagaAdminStore admin, TimeProvider timeProvider, SagaResetSnapshotRecorder snapshots, ILogger logger)
    {
        try
        {
            await admin.ResetStateAsync(summary.SagaType, summary.CorrelationId, summary.CurrentState, summary.Status, summary.Version + 1, timeProvider.GetUtcNow(), CancellationToken.None);
        }
        catch (SagaConcurrencyException ex)
        {
            logger.LogWarning(ex,
                "The retry redrive of saga {SagaType} correlation {CorrelationId} failed to publish and the reset could not be undone: the saga was modified after the reset",
                summary.SagaType, summary.CorrelationId);
            return new RedriveRestore(false,
                "The saga could not be restored to its previous state and status because it was modified concurrently after the reset; reload it to see where it is now.");
        }
        catch (Exception ex)
        {
            // Without this the exception would escape as a bare 500 with no 'restored' flag, hiding that the
            // saga is left Running in the step's from-state, where every later retry meets the status guard.
            logger.LogError(ex,
                "The retry redrive of saga {SagaType} correlation {CorrelationId} failed to publish and the reset could not be undone",
                summary.SagaType, summary.CorrelationId);
            return new RedriveRestore(false,
                $"The saga could not be restored to its previous state; it is Running in state '{step.FromState}' with no redrive in flight. See the dashboard API log.");
        }

        await snapshots.RecordAsync(summary.SagaType, summary.CorrelationId, summary.Version + 2, timeline, CancellationToken.None);
        return new RedriveRestore(true, $"The saga was restored to state '{summary.CurrentState}' with status '{summary.Status}'.");
    }

    /// <summary>Whether the reset was undone after a failed redrive, and the sentence the 502 detail says about it.</summary>
    private readonly record struct RedriveRestore(bool Restored, string Outcome);
}
