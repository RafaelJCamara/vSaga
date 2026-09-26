using System.Text;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Abstractions.Transport;

namespace VSaga.Dashboard.Api.Endpoints;

public sealed record SagaDetail(SagaSummary Summary, string? DataJson);

/// <summary>
/// One (service, message type, queue) binding a participant reports about itself. Mirrors
/// @vsaga/protocol's TopologyRegistration; the API's camelCase JSON matches what that client sends.
/// </summary>
public sealed record TopologyRegistration(string ServiceName, string MessageType, string QueueName);

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

        group.MapGet("", async (ISagaSummaryReader reader, SagaStatus? status, string? sagaType, SagaKind? kind, string? search, int page = 1, int pageSize = 25, SagaSortColumn? sortBy = null, bool sortDescending = false, CancellationToken ct = default) =>
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

            return Results.Ok(await reader.ListAsync(filter, ct));
        })
        .WithName("ListSagas");

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

        group.MapGet("/{sagaType}/{correlationId:guid}/timeline", async (string sagaType, Guid correlationId, ISagaEventLogStore log, CancellationToken ct) =>
            Results.Ok(await log.GetTimelineAsync(sagaType, correlationId, ct)))
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

        MapTopologyEndpoints(app);
    }

    /// <summary>
    /// The registration side of the Saga Map's service topology, for participants that can't write to
    /// the store directly. A .NET participant gets this for free — AddVSagaTopologyRecording wraps its
    /// transport and writes to EfCoreServiceTopologyStore on every SubscribeAsync — but a Node
    /// participant (@vsaga/participant's httpTopologyReporter) has no EF Core and no schema, and
    /// handing it Postgres credentials to register two rows would be a far larger grant than the job
    /// needs. Without this endpoint such a participant still runs, but every node it owns renders as
    /// "Unresolved" on the map.
    /// </summary>
    private static void MapTopologyEndpoints(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/topology/registrations", async (
            TopologyRegistration[] registrations,
            IServiceTopologyStore topologyStore,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            if (registrations.Length == 0)
                return Results.NoContent();

            if (Array.Exists(registrations, r =>
                    string.IsNullOrWhiteSpace(r.ServiceName)
                    || string.IsNullOrWhiteSpace(r.MessageType)
                    || string.IsNullOrWhiteSpace(r.QueueName)))
            {
                return Results.BadRequest(new { error = "serviceName, messageType and queueName are all required on every registration." });
            }

            // RecordAsync is an upsert keyed on (ServiceName, MessageType), so a participant that
            // restarts and re-reports simply refreshes LastSeenAtUtc — the same shape and idempotency
            // the .NET recording path already relies on.
            var seenAtUtc = timeProvider.GetUtcNow();
            foreach (var registration in registrations)
                await topologyStore.RecordAsync(registration.ServiceName, registration.MessageType, registration.QueueName, seenAtUtc, ct);

            return Results.NoContent();
        })
            .WithTags("Topology")
            .WithName("RecordTopologyRegistrations")
            .RequireAuthorization();
    }

    private static async Task<IResult> GetSagaMapAsync(string sagaType, Guid correlationId, ISagaSummaryReader reader, ISagaEventLogStore log, IServiceTopologyStore topologyStore, CancellationToken ct)
    {
        var summary = await reader.GetAsync(sagaType, correlationId, ct);
        if (summary is null)
            return Results.NotFound();

        var timeline = await log.GetTimelineAsync(sagaType, correlationId, ct);
        var topology = await topologyStore.GetAllAsync(ct);

        return Results.Ok(SagaMapBuilder.Build(summary, timeline, topology));
    }

    private static async Task<IResult> RetrySagaAsync(string sagaType, Guid correlationId, ISagaSummaryReader reader, ISagaEventLogStore log, ISagaAdminStore admin, IMessageTransport transport, TimeProvider timeProvider, CancellationToken ct)
    {
        var summary = await reader.GetAsync(sagaType, correlationId, ct);
        if (summary is null)
            return Results.NotFound();

        if (summary.Status is not (SagaStatus.Failed or SagaStatus.TimedOut))
            return Results.Conflict(new { error = $"Saga '{sagaType}' instance '{correlationId}' cannot be retried while its status is '{summary.Status}'; only 'Failed' or 'TimedOut' sagas can be retried." });

        var timeline = await log.GetTimelineAsync(sagaType, correlationId, ct);

        // Two distinct redrive shapes:
        //  1. A technical failure (an action threw) — StepFailed carries the exact message that
        //     failed; replay just that one message against the saga's current (unchanged) state.
        //  2. A business failure or timeout — the saga reached Failed/TimedOut through a normal,
        //     successful step (e.g. "payment declined"), so there is no StepFailed entry at all.
        //     Retry here means starting over: reset the saga back to its initial state and replay
        //     the message that originally started it (SagaStarted carries that payload).
        var lastFailure = timeline.LastOrDefault(e => e.EntryType == SagaEntryType.StepFailed);
        var redrive = lastFailure is { MessageType: not null, PayloadJson: not null } ? lastFailure : null;
        var resetToState = summary.CurrentState;

        if (redrive is null)
        {
            var start = timeline.FirstOrDefault(e => e.EntryType == SagaEntryType.SagaStarted);
            if (start is not { MessageType: not null, PayloadJson: not null, ToState: not null })
                return Results.UnprocessableEntity(new { error = $"Saga '{sagaType}' instance '{correlationId}' has no recorded failure or start to retry from." });

            redrive = start;
            resetToState = start.ToState;
        }

        await log.AppendAsync(SagaLogEntry.Create(correlationId, summary.SagaType, SagaEntryType.ManualRetryRequested,
            fromState: summary.CurrentState, toState: resetToState, messageType: redrive.MessageType, messageId: redrive.MessageId), ct);

        return await ResetAndRedriveAsync(sagaType, correlationId, summary, resetToState, redrive, admin, transport, timeProvider, ct);
    }

    /// <summary>
    /// The reset/republish tail of <see cref="RetrySagaAsync"/> — split out to stay under the
    /// analyzer's method-length cap, the same shape VSaga.Core's orchestrator uses for its own
    /// persist/dispatch tails.
    /// </summary>
    private static async Task<IResult> ResetAndRedriveAsync(string sagaType, Guid correlationId, SagaSummary summary, string resetToState,
        SagaLogEntry redrive, ISagaAdminStore admin, IMessageTransport transport, TimeProvider timeProvider, CancellationToken ct)
    {
        if (!string.Equals(resetToState, summary.CurrentState, StringComparison.Ordinal))
        {
            try
            {
                // summary.Version is the version the caller read and validated — passing it is what
                // makes the 409 below mean "the saga changed since you looked", not "since some
                // later server-side re-read".
                await admin.ResetStateAsync(sagaType, correlationId, resetToState, SagaStatus.Running, summary.Version, timeProvider.GetUtcNow(), ct);
            }
            catch (SagaConcurrencyException)
            {
                // The saga advanced between the summary read and the reset — e.g. a live message
                // was processed concurrently. Mirrors the status guard's 409: the operator is
                // acting on a stale view and should reload before retrying again.
                return Results.Conflict(new { error = $"Saga '{sagaType}' instance '{correlationId}' was modified concurrently with this retry; reload and try again." });
            }
        }

        // Redrive by re-publishing the message with a fresh message id (so the dedupe check
        // doesn't discard it) and the same correlation id. This deliberately does not require the
        // dashboard to know the saga's TState/definition — whichever process actually runs that
        // saga's engine picks it up through its normal subscription, exactly like any other
        // delivery, and the orchestrator resumes Running on successful reprocessing.
        //
        // Note this republish is still correlation-id-addressed, so every saga type subscribed to
        // this message type sees it, not only `sagaType` — each one's own dedupe/initiation rules
        // then decide what to do with it. That is the same fan-out a first-time delivery has.
        var body = Encoding.UTF8.GetBytes(redrive.PayloadJson!);

        try
        {
            await transport.PublishRawAsync(redrive.MessageType!, body, MessageEnvelope.New(correlationId), ct);
        }
        catch (MessageTransportPublishException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway,
                detail: $"Saga '{sagaType}' instance '{correlationId}' could not be retried: {ex.Message}");
        }

        return Results.Accepted();
    }
}
