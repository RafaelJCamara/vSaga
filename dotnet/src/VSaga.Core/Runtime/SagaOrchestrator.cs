using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using VSaga.Abstractions.Diagnostics;
using VSaga.Abstractions.Notifications;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Abstractions.Transport;
using Microsoft.Extensions.Logging;

namespace VSaga.Core.Runtime;

/// <summary>
/// Drives one saga type: deserializes inbound messages, loads/creates the snapshot, calls the saga
/// definition, persists the result with optimistic concurrency, appends to the event log, schedules
/// timeouts, and notifies the dashboard. This is where retry/compensation/concurrency semantics
/// described in the architecture actually live.
/// </summary>
public sealed class SagaOrchestrator<TState>(
    ISagaDefinition<TState> definition,
    ISagaSnapshotStore<TState> snapshotStore,
    ISagaEventLogStore eventLog,
    ISagaTimeoutStore timeoutStore,
    ISagaOutboxStore outboxStore,
    IMessageTransport transport,
    ISagaChangeNotifier notifier,
    IServiceProvider services,
    TimeProvider timeProvider,
    SagaOrchestratorOptions options,
    SagaOutboxOptions outboxOptions,
    ILogger<SagaOrchestrator<TState>> logger)
    where TState : SagaState, new()
{
    private const string DeliveryAttemptHeader = "x-vsaga-delivery-attempt";

    /// <summary>§8 item 11: under Mode=All every ctx publish joins the deferred queue, so it gets the same outbox row and post-commit dispatch PublishAfterCommitAsync always has.</summary>
    private bool DeferAllPublishes => outboxOptions.Mode == SagaOutboxMode.All;

    private readonly Dictionary<string, Type> _messageTypesByName =
        definition.MessageTypes.ToDictionary(t => t.Name, t => t, StringComparer.Ordinal);

    public string SagaType => definition.SagaType;

    public async Task HandleAsync(ReceivedMessage received, CancellationToken cancellationToken)
    {
        // Starts as the transport id and is updated the moment HandleCoreAsync resolves an instance
        // (including via a business-key hit, §5.3) -- captured here, outside the try, so a later
        // exception (e.g. a lost persist race) still has it available in the catch below. See
        // RecordDeliveryExhaustedAsync's own note: this is NOT used for the redelivery envelope itself,
        // only for the dead-letter bookkeeping once attempts are exhausted.
        var resolvedCorrelationId = received.CorrelationId;

        try
        {
            await HandleCoreAsync(received, id => resolvedCorrelationId = id, cancellationToken);
            await received.Ack.AckAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await HandleInfrastructureFailureAsync(received, resolvedCorrelationId, ex, cancellationToken);
        }
    }

    /// <summary>
    /// Handles a failure from outside the saga definition's own step logic (deserialize errors,
    /// persistence-store exceptions, transport hiccups — HandleStepFailureAsync already handles
    /// failures thrown by the saga's own steps by marking it Failed). Redelivers with an incremented
    /// attempt count under the cap; once exhausted, records why and dead-letters instead of requeuing
    /// forever. If the redelivery publish itself throws, that's deliberately left to propagate — it
    /// reaches RabbitMqTransport's own dispatch-level catch, which nacks without requeue, so a
    /// redelivery that can't even be attempted still fails safe into the dead-letter queue.
    /// </summary>
    private async Task HandleInfrastructureFailureAsync(ReceivedMessage received, Guid resolvedCorrelationId, Exception ex, CancellationToken cancellationToken)
    {
        var attempt = GetDeliveryAttempt(received.Headers);

        if (attempt < options.MaxDeliveryAttempts)
        {
            logger.LogWarning(ex, "Infrastructure error processing {MessageType} for saga {SagaType} (attempt {Attempt}/{MaxAttempts}); redelivering",
                received.MessageTypeName, SagaType, attempt + 1, options.MaxDeliveryAttempts);

            var headers = new Dictionary<string, string>(received.Headers, StringComparer.Ordinal)
            {
                [DeliveryAttemptHeader] = (attempt + 1).ToString(CultureInfo.InvariantCulture),
            };
            var envelope = new MessageEnvelope(received.CorrelationId, received.MessageId, headers);

            // Same CorrelationId/MessageId as the original delivery, not a fresh one: if a durable log
            // entry for this exact message was already written before the failure, the dedupe check in
            // HandleCoreAsync will correctly recognize the redelivered copy and skip it rather than
            // reprocess it — the same safe-by-default behavior RabbitMQ's own requeue:true gives today.
            await transport.PublishRawAsync(received.MessageTypeName, received.Body, envelope, cancellationToken);
            await received.Ack.AckAsync(cancellationToken);
            return;
        }

        logger.LogError(ex, "Infrastructure error processing {MessageType} for saga {SagaType} after {MaxAttempts} delivery attempts; dead-lettering",
            received.MessageTypeName, SagaType, options.MaxDeliveryAttempts);

        await RecordDeliveryExhaustedAsync(received, resolvedCorrelationId, ex, cancellationToken);
        await received.Ack.NackAsync(requeue: false, cancellationToken);
    }

    /// <summary>
    /// Best-effort visibility for a dead-lettered message: the message is already durably routed to the
    /// DLQ by the NackAsync that follows this call regardless of whether this succeeds, so failures
    /// here are logged and swallowed rather than left to interfere with that.
    /// </summary>
    /// <param name="received">The dead-lettered inbound message; only its MessageTypeName/MessageId are used here.</param>
    /// <param name="resolvedCorrelationId">
    /// The saga instance HandleCoreAsync actually resolved this message against, not necessarily
    /// <paramref name="received"/>'s own transport correlation id — a business-key hit (§5.3) can
    /// resolve to a *different* existing instance's id. Keying this method's bookkeeping on
    /// received.CorrelationId instead (the pre-item-14 behaviour) would log a DeliveryExhausted entry
    /// under an id no row was ever stored against, and the FindAsync/PersistAsync below would then find
    /// nothing to mark Failed -- the saga that actually kept losing its persist race never gets its
    /// terminal state recorded. Falls back to received.CorrelationId when resolution never got that far
    /// (e.g. a deserialize failure), which is the correct id in that case.
    /// </param>
    /// <param name="ex">The infrastructure exception that exhausted redelivery, recorded as the DeliveryExhausted entry's error message.</param>
    /// <param name="cancellationToken">Cancellation token for the log/persist/notify calls below.</param>
    private async Task RecordDeliveryExhaustedAsync(ReceivedMessage received, Guid resolvedCorrelationId, Exception ex, CancellationToken cancellationToken)
    {
        try
        {
            await LogAsync(SagaLogEntry.Create(resolvedCorrelationId, SagaType, SagaEntryType.DeliveryExhausted,
                messageType: received.MessageTypeName, messageId: received.MessageId, errorMessage: ex.Message), cancellationToken);

            var state = await snapshotStore.FindAsync(SagaType, resolvedCorrelationId, cancellationToken);
            if (state is not null && state.Status is not (SagaStatus.Completed or SagaStatus.Failed or SagaStatus.TimedOut))
            {
                var expectedVersion = state.Version;
                state.Status = SagaStatus.Failed;
                await PersistAsync(state, isNew: false, expectedVersion, cancellationToken);

                // production-readiness.md §6/§8.18's terminal-status wiring missed this site because the
                // dead-letter path predates it. The rule is that every path reaching a terminal status
                // records the duration; the other three are RecordTimeoutOutcomeAsync,
                // HandleStepFailureAsync, and PersistAndFinalizeStepSuccessAsync. Recorded only after the
                // persist above actually commits, same reasoning as those three.
                VSagaDiagnostics.SagasFailed.Add(1, new KeyValuePair<string, object?>(VSagaDiagnostics.TagSagaType, SagaType));
                VSagaDiagnostics.SagaDuration.Record((timeProvider.GetUtcNow() - state.CreatedAtUtc).TotalMilliseconds,
                    new KeyValuePair<string, object?>(VSagaDiagnostics.TagSagaType, SagaType));

                await notifier.SagaUpdatedAsync(ToSummary(state), cancellationToken);
            }
        }
        catch (Exception loggingEx)
        {
            logger.LogError(loggingEx, "Failed to record delivery-exhausted state for saga {SagaType} correlation {CorrelationId}; message is still being dead-lettered",
                SagaType, resolvedCorrelationId);
        }
    }

    private static int GetDeliveryAttempt(IReadOnlyDictionary<string, string> headers) =>
        headers.TryGetValue(DeliveryAttemptHeader, out var value) &&
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempt)
            ? attempt
            : 0;

    /// <summary>The publisher-stamped service identity carried on an inbound message, if any — see MessageEnvelope.From.</summary>
    private static string? GetSourceService(IReadOnlyDictionary<string, string> headers) =>
        headers.TryGetValue(MessageEnvelope.SourceServiceHeader, out var value) ? value : null;

    /// <summary>
    /// The MessageId of whatever outbound message this inbound one is a reply to, if the publisher
    /// stamped one — this is what SagaMapBuilder stitches a reply's SourceService back onto the
    /// original outbound edge's destination.
    /// </summary>
    private static string? GetCausationId(IReadOnlyDictionary<string, string> headers) =>
        headers.TryGetValue(MessageEnvelope.CausationIdHeader, out var value) ? value : null;

    /// <summary>The saga type that started this one via <c>StartChildAsync</c>, if this message is a child's initiating message — see MessageEnvelope.ParentSagaTypeHeader.</summary>
    private static string? GetParentSagaType(IReadOnlyDictionary<string, string> headers) =>
        headers.TryGetValue(MessageEnvelope.ParentSagaTypeHeader, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    /// <summary>
    /// The parent instance's correlation id, if the publisher stamped a parseable one. An unparseable
    /// value is treated as absent rather than fatal: the linkage is dashboard/traceability metadata, and
    /// a malformed header from some future publisher must not stop a child saga from running at all.
    /// </summary>
    private static Guid? GetParentCorrelationId(IReadOnlyDictionary<string, string> headers) =>
        headers.TryGetValue(MessageEnvelope.ParentCorrelationIdHeader, out var value) && Guid.TryParse(value, out var parsed)
            ? parsed
            : null;

    /// <summary>Manual, dashboard/API-triggered redrive of a Failed saga: replays the exact message that last failed.</summary>
    public async Task RetryAsync(Guid correlationId, CancellationToken cancellationToken)
    {
        var existing = await snapshotStore.FindAsync(SagaType, correlationId, cancellationToken)
                       ?? throw new SagaNotFoundException(SagaType, correlationId);

        // Note: this in-process path only redrives a recorded technical failure (StepFailed) — unlike
        // VSaga.Dashboard.Api's /retry endpoint, it doesn't yet have the reset-and-replay-from-start
        // fallback for sagas that reached Failed via a normal business transition, so it deliberately
        // stays narrower (Failed only, not TimedOut) to match what it can actually redrive.
        if (existing.Status != SagaStatus.Failed)
            throw new SagaRetryNotAllowedException(SagaType, correlationId, existing.Status.ToString());

        var timeline = await eventLog.GetTimelineAsync(SagaType, correlationId, cancellationToken);
        var lastFailure = timeline.LastOrDefault(e => e.EntryType == SagaEntryType.StepFailed);

        if (lastFailure is not { MessageType: not null, PayloadJson: not null })
            throw new InvalidOperationException($"Saga '{correlationId}' has no recorded failed step to retry.");

        if (!_messageTypesByName.TryGetValue(lastFailure.MessageType, out var clrType))
            throw new InvalidOperationException($"Unknown message type '{lastFailure.MessageType}' recorded for saga '{correlationId}'.");

        var message = JsonSerializer.Deserialize(lastFailure.PayloadJson, clrType)
                      ?? throw new InvalidOperationException("Failed to deserialize the message being retried.");

        existing.Status = SagaStatus.Running;

        await LogAsync(SagaLogEntry.Create(correlationId, SagaType, SagaEntryType.ManualRetryRequested,
            fromState: existing.CurrentState, messageType: lastFailure.MessageType, messageId: lastFailure.MessageId), cancellationToken);

        await RunStepAsync(existing, message, lastFailure.MessageType, lastFailure.MessageId ?? Guid.NewGuid().ToString("N"),
            new Dictionary<string, string>(StringComparer.Ordinal), isNew: false, needsInsert: false, cancellationToken);
    }

    /// <summary>Invoked by the timeout dispatcher for a due, previously-scheduled state timeout.</summary>
    public async Task HandleTimeoutAsync(SagaTimeout timeout, CancellationToken cancellationToken)
    {
        var state = await snapshotStore.FindAsync(SagaType, timeout.CorrelationId, cancellationToken);

        // The saga may have already moved past this state (its pending timeout is cancelled on
        // transition, but a race with an in-flight timer tick is possible) or been deleted — either
        // way there's nothing to do.
        if (state is null || !string.Equals(state.CurrentState, timeout.ForState, StringComparison.Ordinal) || state.Status != SagaStatus.Running)
            return;

        // Claim this timeout with a version-checked write BEFORE calling into the saga definition,
        // which is where Compensate()/Publish() actually dispatch side effects. Without this, a normal
        // message (e.g. a reply landing right at this state's timeout boundary) that reads the same
        // snapshot version concurrently — before either branch has written back — would let this
        // timeout publish its side effects regardless of whether its own later persist then loses the
        // optimistic-concurrency check against that message's write. Claiming first folds the race
        // into a single optimistic-concurrency envelope: a stale timeout is caught and abandoned right
        // here, before anything can be published, rather than after.
        if (!await TryPersistOrLogRaceLossAsync(state, timeout, sideEffectsAlreadyRan: false, cancellationToken))
            return;

        await LogAsync(SagaLogEntry.Create(timeout.CorrelationId, SagaType, SagaEntryType.TimeoutFired, fromState: timeout.ForState), cancellationToken);

        var visitedStates = await GetVisitedStatesAsync(timeout.CorrelationId, cancellationToken);
        var context = new SagaContext<TState>(state, timeout.CorrelationId, new Dictionary<string, string>(StringComparer.Ordinal), visitedStates,
            services, transport, SagaType, inboundMessageId: null, LogAsync, DeferAllPublishes, cancellationToken);

        var outcome = await definition.HandleTimeoutAsync(context, timeout.ForState, cancellationToken);
        if (!outcome.WasHandled)
            return;

        await LogAsync(SagaLogEntry.Create(timeout.CorrelationId, SagaType, SagaEntryType.StepSucceeded,
            fromState: outcome.FromState, toState: outcome.ToState), cancellationToken);

        if (!string.Equals(outcome.ToState, outcome.FromState, StringComparison.Ordinal) && definition.GetTimeout(outcome.ToState) is { } delay)
        {
            var dueAt = timeProvider.GetUtcNow() + delay;
            await timeoutStore.ScheduleAsync(SagaType, timeout.CorrelationId, outcome.ToState, dueAt, cancellationToken);
            await LogAsync(SagaLogEntry.Create(timeout.CorrelationId, SagaType, SagaEntryType.TimeoutScheduled, toState: outcome.ToState), cancellationToken);
        }

        await CommitAndDispatchTimeoutAsync(state, timeout, outcome, context, cancellationToken);
    }

    /// <summary>
    /// The stage/persist/dispatch tail of <see cref="HandleTimeoutAsync"/> — split out to stay under the
    /// analyzer's method-length cap, like <see cref="RecordTimeoutOutcomeAsync"/>.
    /// </summary>
    private async Task CommitAndDispatchTimeoutAsync(TState state, SagaTimeout timeout, SagaStepOutcome outcome,
        SagaContext<TState> context, CancellationToken cancellationToken)
    {
        // §4.1 step 2's outbox rows, staged so the persist below commits them with the snapshot -- or,
        // if it loses its race, leaves them uncommitted for the discard path to drop. A terminal
        // timeout's ChildSagaFinished stages in the same breath and for the same reason (§4.3's second
        // publish surface): it is justified by precisely the transition this persist is about to record.
        await EnqueueOutboxRowsAsync(context, cancellationToken);
        var stagedChildFinished = outcome.FinalStatus is { } terminalStatus
            ? await StageChildSagaFinishedAsync(state, terminalStatus, causationMessageId: null, cancellationToken)
            : null;

        // HandleTimeoutAsync's up-front claim only closes the race up to this point —
        // definition.HandleTimeoutAsync just ran real Compensate()/Publish() I/O (and any step-level
        // RetryPolicy delays), which is real wall-clock time a second concurrent write could land in. If
        // that happens, this persist loses the same optimistic-concurrency check, but unlike the claim,
        // those side effects have already gone out — they can't be un-published. Logging this case
        // distinctly (rather than letting it propagate to the dispatcher's generic catch-and-log) at
        // least makes that distinction visible instead of indistinguishably silent.
        if (!await TryPersistOrLogRaceLossAsync(state, timeout, sideEffectsAlreadyRan: true, cancellationToken))
        {
            await DiscardStagedChildSagaFinishedAsync(state, stagedChildFinished, timeout.ForState, cancellationToken);
            await DiscardDeferredPublishesAsync(timeout.CorrelationId, context, timeout.ForState, cancellationToken);
            return;
        }

        await DrainDeferredPublishesAsync(timeout.CorrelationId, context, cancellationToken);
        await RecordTimeoutOutcomeAsync(state, outcome, stagedChildFinished, cancellationToken);
    }

    /// <summary>
    /// Diagnostics/notification/Slice-2b-safety-net bookkeeping once a timeout's own persist has
    /// committed — split out of HandleTimeoutAsync to stay under the analyzer's method-length cap.
    /// </summary>
    private async Task RecordTimeoutOutcomeAsync(TState state, SagaStepOutcome outcome, StagedChildSagaFinished? stagedChildFinished, CancellationToken cancellationToken)
    {
        if (outcome.FinalStatus == SagaStatus.Failed)
            VSagaDiagnostics.SagasFailed.Add(1, new KeyValuePair<string, object?>(VSagaDiagnostics.TagSagaType, SagaType));
        else if (outcome.FinalStatus == SagaStatus.Completed)
            VSagaDiagnostics.SagasCompleted.Add(1, new KeyValuePair<string, object?>(VSagaDiagnostics.TagSagaType, SagaType));

        // production-readiness.md §6/§8.18: the timeout-outcome counterpart to HandleStepSuccessAsync's
        // own SagaDuration recording -- this is the other of the two places a saga already reaches a
        // terminal status.
        if (outcome.FinalStatus is not null)
            VSagaDiagnostics.SagaDuration.Record((timeProvider.GetUtcNow() - state.CreatedAtUtc).TotalMilliseconds,
                new KeyValuePair<string, object?>(VSagaDiagnostics.TagSagaType, SagaType));

        await notifier.SagaUpdatedAsync(ToSummary(state), cancellationToken);

        // A timeout that goes terminal is one of the two structural gaps ctx.NotifyParentAsync cannot
        // reach on its own — the timeout step itself can technically still call NotifyParentAsync (this
        // does not suppress that), but nothing requires it to, so the engine reports on the child's
        // behalf regardless. Staged before the caller's persist; this only sends it.
        await PublishChildSagaFinishedAsync(state, stagedChildFinished, cancellationToken);
    }

    /// <summary>
    /// Persists <paramref name="state"/> with an optimistic-concurrency check against its current
    /// Version, returning false (and logging) instead of throwing if a concurrent write already moved
    /// the saga past this version. Used for both the up-front timeout claim and the final persist after
    /// running the definition's timeout step — <paramref name="sideEffectsAlreadyRan"/> only changes the
    /// log level/message, since a race lost at the final persist means Compensate()/Publish() already
    /// fired and can't be un-sent, unlike a race lost at the claim.
    /// </summary>
    private async Task<bool> TryPersistOrLogRaceLossAsync(TState state, SagaTimeout timeout, bool sideEffectsAlreadyRan, CancellationToken cancellationToken)
    {
        try
        {
            await PersistAsync(state, isNew: false, state.Version, cancellationToken);
            return true;
        }
        catch (SagaConcurrencyException ex)
        {
            if (sideEffectsAlreadyRan)
            {
                logger.LogWarning(ex,
                    "Timeout for saga {SagaType} correlation {CorrelationId} in state {State} lost a second race after its Compensate()/Publish() side effects already ran; those side effects were sent but this timeout's own state transition was not persisted",
                    SagaType, timeout.CorrelationId, timeout.ForState);
            }
            else
            {
                logger.LogInformation(ex,
                    "Timeout for saga {SagaType} correlation {CorrelationId} in state {State} lost the race to a concurrent update; skipping",
                    SagaType, timeout.CorrelationId, timeout.ForState);
            }

            return false;
        }
    }

    private async Task HandleCoreAsync(ReceivedMessage received, Action<Guid> onResolved, CancellationToken cancellationToken)
    {
        if (!_messageTypesByName.TryGetValue(received.MessageTypeName, out var clrType))
        {
            logger.LogWarning("Ignoring message of unknown type {MessageType} for saga {SagaType}", received.MessageTypeName, SagaType);
            return;
        }

        var message = JsonSerializer.Deserialize(received.Body.Span, clrType)
                      ?? throw new InvalidOperationException($"Failed to deserialize {received.MessageTypeName}.");

        var resolved = await ResolveInstanceAsync(received, message, clrType, cancellationToken);
        if (resolved is not { } instance)
        {
            await LogAsync(SagaLogEntry.Create(received.CorrelationId, SagaType, SagaEntryType.UnexpectedEvent,
                messageType: received.MessageTypeName, messageId: received.MessageId), cancellationToken);
            return;
        }

        var (existing, correlationId, isNew, needsInsert) = instance;

        // Reports the resolved instance's own id back to HandleAsync before anything below can throw,
        // so RecordDeliveryExhaustedAsync keys its dead-letter bookkeeping on the right instance even
        // when a business-key hit (§5.3) means correlationId != received.CorrelationId.
        onResolved(correlationId);

        if (isNew)
        {
            // PayloadJson is recorded here (not just on StepFailed) so a saga that later reaches a
            // terminal Failed state through a normal business transition — no exception, so no
            // StepFailed entry to redrive — can still be retried by the dashboard: it replays this
            // exact initiating message against the saga once reset back to its initial state.
            await LogAsync(SagaLogEntry.Create(correlationId, SagaType, SagaEntryType.SagaStarted,
                toState: existing.CurrentState, messageType: received.MessageTypeName, messageId: received.MessageId,
                payloadJson: System.Text.Encoding.UTF8.GetString(received.Body.Span),
                sourceService: GetSourceService(received.Headers), causationId: GetCausationId(received.Headers)), cancellationToken);
        }
        else if (await eventLog.IsDuplicateAsync(SagaType, correlationId, received.MessageId, cancellationToken))
        {
            logger.LogDebug("Skipping duplicate message {MessageId} for saga {CorrelationId}", received.MessageId, correlationId);
            return;
        }

        await RunStepAsync(existing, message, received.MessageTypeName, received.MessageId, received.Headers, isNew, needsInsert, cancellationToken);
    }

    /// <summary>
    /// The instance <see cref="HandleCoreAsync"/> should run this message's step against, plus how to
    /// persist it: <see cref="IsNew"/> gates the SagaStarted log entry / duplicate check / SagasStarted
    /// metric, while <see cref="NeedsInsert"/> — independent of <see cref="IsNew"/> — selects
    /// PersistAsync's Insert-vs-Update branch at the end of the step. They diverge exactly when the
    /// business-key reservation in <see cref="CreateAndReserveNewInstanceAsync"/> already inserted the
    /// row: the saga is still new (for metrics/logging), but the row is already durable, so the
    /// end-of-step persist must Update it, not Insert it a second time. Null return means neither a
    /// transport-id nor a business-key lookup found an instance, and <c>CanInitiate</c> refused to start
    /// one either.
    /// </summary>
    private readonly record struct ResolvedInstance(TState State, Guid CorrelationId, bool IsNew, bool NeedsInsert);

    /// <summary>
    /// production-readiness.md §5.3: tries the transport correlation id first (unchanged), then — on a
    /// miss — the business key this message may carry (non-null only for a saga that declared
    /// CorrelateOn and registered a CorrelateBy extractor for this message's CLR type). A business-key
    /// hit is NOT a new saga: it is the *same* instance observed under a different transport correlation
    /// id (e.g. a reply published against its own fresh id rather than the id the initiating command
    /// used), so the returned CorrelationId is the found instance's own — it flows into every LogAsync in
    /// HandleCoreAsync and, via RunStepAsync's state.CorrelationId, into every outbound envelope.
    /// Substituting the inbound id instead would fork the timeline. Both lookups missing falls through to
    /// <see cref="CreateAndReserveNewInstanceAsync"/>, gated by CanInitiate exactly as before.
    /// </summary>
    /// <remarks>
    /// A business key is bound to the instance that first reserved it for the lifetime of that instance
    /// -- the partial unique index (§5.2) never releases a key when its saga reaches a terminal status,
    /// so this lookup does not check <c>existing.Status</c>. A later message carrying the same business
    /// key therefore always routes back to that same (possibly Completed/Failed) instance rather than
    /// starting a fresh one; if the definition has no transition out of a terminal state for that
    /// message type, HandleCoreAsync logs it as UnexpectedEvent, exactly as a stale transport-id hit
    /// against a terminal instance already did before this method existed. Whether a business key
    /// *should* become reusable once its saga terminates is a product decision this plan does not make
    /// (production-readiness.md is silent on it); this is documented behaviour, not an oversight.
    /// </remarks>
    private async Task<ResolvedInstance?> ResolveInstanceAsync(ReceivedMessage received, object message, Type clrType, CancellationToken cancellationToken)
    {
        var correlationId = received.CorrelationId;
        var existing = await snapshotStore.FindAsync(SagaType, correlationId, cancellationToken);

        var businessKey = existing is null ? definition.TryGetCorrelationKey(message) : null;
        if (existing is null && businessKey is not null)
        {
            existing = await snapshotStore.FindByBusinessKeyAsync(SagaType, businessKey, cancellationToken);
            if (existing is not null)
                correlationId = existing.CorrelationId;
        }

        if (existing is not null)
            return new ResolvedInstance(existing, correlationId, IsNew: false, NeedsInsert: false);

        return definition.CanInitiate(clrType)
            ? await CreateAndReserveNewInstanceAsync(correlationId, businessKey, received.Headers, cancellationToken)
            : null;
    }

    /// <summary>
    /// §5.2: resolves the double-initiate race by reserving the business key before the step runs, not
    /// by catching a collision after it already ran — the partial unique index on
    /// (SagaType, BusinessKey) adjudicates a concurrent double-initiate right here, before either side's
    /// RunStepAsync has executed a single (possibly non-idempotent) step action. A saga with no business
    /// key on this message (either it never called CorrelateOn, or this message type has no registered
    /// extractor) skips the reservation entirely — <c>NeedsInsert: true</c> reproduces the original
    /// behaviour unchanged: PersistAsync inserts once, at the end of the step.
    /// </summary>
    private async Task<ResolvedInstance> CreateAndReserveNewInstanceAsync(Guid correlationId, string? businessKey,
        IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var candidate = NewInstance(correlationId, headers);

        if (businessKey is null)
            return new ResolvedInstance(candidate, correlationId, IsNew: true, NeedsInsert: true);

        candidate.BusinessKey = businessKey;

        try
        {
            await snapshotStore.InsertAsync(candidate, cancellationToken);
            return new ResolvedInstance(candidate, correlationId, IsNew: true, NeedsInsert: false);
        }
        catch (SagaAlreadyExistsException ex)
        {
            // Lost the race: a concurrent initiate already reserved this business key first. Not an
            // infrastructure failure — look the winner up and continue exactly as a business-key hit in
            // ResolveInstanceAsync would have: this message is not starting a new saga after all.
            //
            // ex may instead be an unrelated infra failure the store mislabelled as a collision
            // (production-readiness.md §8.14's review; see SagaAlreadyExistsException's own note) -- if
            // so there genuinely is no winner to find, and ex.InnerException (chained by
            // EfCoreSagaSnapshotStore.InsertAsync) is included below so the real cause stays diagnosable
            // instead of being discarded behind a generic "no winner found" message.
            var winner = await snapshotStore.FindByBusinessKeyAsync(SagaType, businessKey, cancellationToken)
                         ?? throw new InvalidOperationException(
                             $"Saga '{SagaType}' lost the business-key reservation race for '{businessKey}' but no winning instance could be found.", ex.InnerException ?? ex);

            return new ResolvedInstance(winner, winner.CorrelationId, IsNew: false, NeedsInsert: false);
        }
    }

    /// <summary>
    /// The blank snapshot for a saga this message is opening. This is the only place a parent link is
    /// ever read off the wire: an instance's parent is fixed at creation, so a later message carrying
    /// those headers — a redelivery, or a second saga type observing the same child message — takes the
    /// existing-instance path in <see cref="HandleCoreAsync"/> and cannot re-parent anything.
    /// </summary>
    private TState NewInstance(Guid correlationId, IReadOnlyDictionary<string, string> headers)
    {
        var now = timeProvider.GetUtcNow();

        // Both halves or neither. A half-stamped link (one header present, the other missing or
        // unparseable) would read as a child in the dashboard while being unreachable from the parent,
        // since FindChildrenAsync matches on the pair — better to record an honest root saga.
        var parentSagaType = GetParentSagaType(headers);
        var parentCorrelationId = GetParentCorrelationId(headers);
        var hasParent = parentSagaType is not null && parentCorrelationId is not null;

        return new TState
        {
            CorrelationId = correlationId,
            SagaType = SagaType,
            Kind = definition.Kind,
            CurrentState = definition.InitialStateName,
            Status = SagaStatus.Running,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ParentSagaType = hasParent ? parentSagaType : null,
            ParentCorrelationId = hasParent ? parentCorrelationId : null,
        };
    }

    /// <summary>
    /// production-readiness.md §6/§8.18: ActivityKind.Consumer with an explicitly extracted parent
    /// context, not the plain StartActivity(string) overload that used to adopt Activity.Current. That
    /// mattered because Activity.Current is unreliable two different ways depending on transport: null
    /// on a broker transport (so every hop rooted a fresh trace), and on HTTP's inline dispatch path
    /// (HttpInboundDispatcher.DispatchInlineAsync) it is ASP.NET Core's own *server* activity for the
    /// inbound request -- silently attaching this span to the wrong trace rather than the one carried by
    /// the message itself. HTTP's pump path never had this problem: PumpLoopAsync runs on its own
    /// long-lived background loop, decoupled from any request's ExecutionContext, so Activity.Current
    /// there was already null, same as a broker. Passing an explicit parentContext sidesteps
    /// Activity.Current entirely and fixes both paths uniformly. Split out of RunStepAsync purely for
    /// length.
    /// </summary>
    private Activity? StartConsumerSpan(string fromState, Guid correlationId, IReadOnlyDictionary<string, string> headers)
    {
        VSagaDiagnostics.TryExtractActivityContext(headers, out var parentContext);
        var activity = VSagaDiagnostics.ActivitySource.StartActivity(
            $"saga.step {SagaType}.{fromState}", ActivityKind.Consumer, parentContext);
        activity?.SetTag(VSagaDiagnostics.TagSagaType, SagaType);
        activity?.SetTag(VSagaDiagnostics.TagSagaKind, definition.Kind.ToString());
        activity?.SetTag(VSagaDiagnostics.TagCorrelationId, correlationId.ToString());
        activity?.SetTag(VSagaDiagnostics.TagFromState, fromState);

        // A retry of the same logical delivery belongs in the same trace, not a fresh linked span. The
        // redelivery headers already carry the original traceparent forward -- HandleInfrastructureFailureAsync
        // copies every inbound header across as-is -- so the extraction above already keeps this in the
        // same trace; this tag just makes the retry visible on the span itself.
        var deliveryAttempt = GetDeliveryAttempt(headers);
        if (deliveryAttempt > 0)
            activity?.SetTag(VSagaDiagnostics.TagDeliveryAttempt, deliveryAttempt);

        return activity;
    }

    private async Task RunStepAsync(TState state, object message, string messageTypeName, string messageId,
        IReadOnlyDictionary<string, string> headers, bool isNew, bool needsInsert, CancellationToken cancellationToken)
    {
        var correlationId = state.CorrelationId;
        var expectedVersion = state.Version;
        var fromState = state.CurrentState;

        // Any message that gets this far (past the initial-vs-existing and duplicate checks) is about
        // to be reprocessed for real — a Failed saga picking one up (via manual RetryAsync's replay or
        // a fresh redelivery of the same message type) is no longer stuck, so optimistically resume
        // Running. If the step fails again, the catch block below sets it back to Failed.
        if (state.Status == SagaStatus.Failed)
            state.Status = SagaStatus.Running;

        await LogAsync(SagaLogEntry.Create(correlationId, SagaType, SagaEntryType.MessageReceived,
            messageType: messageTypeName, messageId: messageId,
            sourceService: GetSourceService(headers), causationId: GetCausationId(headers)), cancellationToken);

        var visitedStates = await GetVisitedStatesAsync(correlationId, cancellationToken);
        var context = new SagaContext<TState>(state, correlationId, headers, visitedStates, services, transport, SagaType, messageId, LogAsync, DeferAllPublishes, cancellationToken);

        using var activity = StartConsumerSpan(fromState, correlationId, headers);

        var stopwatch = Stopwatch.StartNew();
        SagaStepOutcome outcome;

        try
        {
            outcome = await definition.HandleAsync(context, message, cancellationToken);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            VSagaDiagnostics.StepDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>(VSagaDiagnostics.TagSagaType, SagaType));
            await HandleStepFailureAsync(state, ex, correlationId, fromState, message, messageTypeName, messageId, needsInsert, expectedVersion, activity, context, cancellationToken);
            return;
        }

        stopwatch.Stop();
        VSagaDiagnostics.StepDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>(VSagaDiagnostics.TagSagaType, SagaType));
        activity?.SetTag(VSagaDiagnostics.TagToState, outcome.ToState);

        await HandleStepSuccessAsync(state, outcome, correlationId, fromState, messageTypeName, messageId, isNew, needsInsert, expectedVersion, activity, context, cancellationToken);
    }

    /// <summary>
    /// <paramref name="needsInsert"/> selects PersistAsync's Insert-vs-Update branch — true only when
    /// this step's instance was neither found by an existing lookup nor already reserved by the
    /// business-key early insert in <see cref="HandleCoreAsync"/> (§5.2); it is unrelated to whether the
    /// saga is "new" for metrics purposes, which HandleStepSuccessAsync tracks separately via its own
    /// <c>isNew</c> parameter.
    /// </summary>
    private async Task HandleStepFailureAsync(TState state, Exception ex, Guid correlationId, string fromState, object message,
        string messageTypeName, string messageId, bool needsInsert, int expectedVersion, Activity? activity, SagaContext<TState> context, CancellationToken cancellationToken)
    {
        state.Status = SagaStatus.Failed;

        // Marks the span itself failed, not just the log entry below -- an OTel backend filtering by
        // status wouldn't otherwise see this as an error at all, only the trace/span ids the log entry
        // already records for cross-referencing.
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

        var payloadJson = JsonSerializer.Serialize(message, message.GetType());

        await LogAsync(SagaLogEntry.Create(correlationId, SagaType, SagaEntryType.StepFailed,
            fromState: fromState, messageType: messageTypeName, messageId: messageId,
            payloadJson: payloadJson, errorMessage: ex.Message,
            traceId: activity?.TraceId.ToString(), spanId: activity?.SpanId.ToString()), cancellationToken);

        // Staged before the persist, like every other outbox row, so the row recording "this saga
        // finished Failed" commits with the snapshot that says so. If this persist throws, the row stays
        // uncommitted and the message is never announced -- and on the redelivery-exhausted path, where
        // RecordDeliveryExhaustedAsync's own append does flush it, that path marks the saga Failed too,
        // so the row it commits still matches the outcome that was actually recorded.
        var stagedChildFinished = await StageChildSagaFinishedAsync(state, SagaStatus.Failed, messageId, cancellationToken);

        try
        {
            await PersistAsync(state, needsInsert, expectedVersion, cancellationToken);
        }
        catch (SagaConcurrencyException)
        {
            // Symmetric with HandleStepSuccessAsync's own race-loss branch below: this persist lost the
            // optimistic-concurrency check, so the Failed transition just logged above was never
            // actually recorded. The staged ChildSagaFinished row above is discarded the same way the
            // timeout path's DiscardStagedChildSagaFinishedAsync does -- left uncommitted it would tell
            // this saga's parent it finished Failed when the snapshot never actually recorded that,
            // exactly the class of bug production-readiness.md §8.15's review caught in
            // HandleStepSuccessAsync. Re-throw (don't swallow, unlike the timeout path) so HandleAsync's
            // catch drives the usual infrastructure-failure redelivery -- §5.4/§8.15 confirmed this
            // exception reliably reaches it.
            await DiscardStagedChildSagaFinishedAsync(state, stagedChildFinished, fromState, cancellationToken);
            throw;
        }

        VSagaDiagnostics.SagasFailed.Add(1, new KeyValuePair<string, object?>(VSagaDiagnostics.TagSagaType, SagaType));
        VSagaDiagnostics.SagaDuration.Record((timeProvider.GetUtcNow() - state.CreatedAtUtc).TotalMilliseconds,
            new KeyValuePair<string, object?>(VSagaDiagnostics.TagSagaType, SagaType));

        // Anything queued via ctx.PublishAfterCommitAsync before the throw belongs to a transition that
        // was never reached -- the persist just above records Failed, not the outcome those publishes
        // assumed. Discarding (not draining) matches DiscardDeferredPublishesAsync's other caller, the
        // timeout-race path: publishing now would announce a transition nobody recorded.
        await DiscardDeferredPublishesAsync(correlationId, context, fromState, cancellationToken);

        await notifier.SagaUpdatedAsync(ToSummary(state), cancellationToken);

        // An unhandled exception is the other structural gap NotifyParentAsync cannot reach: the step
        // that threw never ran to a point where the child's own code could report back. Always terminal
        // here (state.Status is unconditionally Failed above), unlike the timeout path.
        await PublishChildSagaFinishedAsync(state, stagedChildFinished, cancellationToken);
    }

    private async Task HandleStepSuccessAsync(TState state, SagaStepOutcome outcome, Guid correlationId, string fromState,
        string messageTypeName, string messageId, bool isNew, bool needsInsert, int expectedVersion, Activity? activity, SagaContext<TState> context, CancellationToken cancellationToken)
    {
        if (!outcome.WasHandled)
        {
            await LogAsync(SagaLogEntry.Create(correlationId, SagaType, SagaEntryType.UnexpectedEvent,
                fromState: fromState, messageType: messageTypeName, messageId: messageId), cancellationToken);
            return;
        }

        await LogAsync(SagaLogEntry.Create(correlationId, SagaType, SagaEntryType.StepSucceeded,
            fromState: outcome.FromState, toState: outcome.ToState, messageType: messageTypeName, messageId: messageId,
            traceId: activity?.TraceId.ToString(), spanId: activity?.SpanId.ToString()), cancellationToken);

        if (!string.Equals(outcome.ToState, outcome.FromState, StringComparison.Ordinal))
        {
            await timeoutStore.CancelAsync(SagaType, correlationId, outcome.FromState, cancellationToken);

            if (definition.GetTimeout(outcome.ToState) is { } delay)
            {
                var dueAt = timeProvider.GetUtcNow() + delay;
                await timeoutStore.ScheduleAsync(SagaType, correlationId, outcome.ToState, dueAt, cancellationToken);
                await LogAsync(SagaLogEntry.Create(correlationId, SagaType, SagaEntryType.TimeoutScheduled, toState: outcome.ToState), cancellationToken);
            }
        }

        if (outcome.FinalStatus is not null)
            await LogAsync(SagaLogEntry.Create(correlationId, SagaType, SagaEntryType.SagaCompleted, toState: outcome.ToState), cancellationToken);

        await PersistAndFinalizeStepSuccessAsync(state, outcome, correlationId, fromState, isNew, needsInsert, expectedVersion, context, cancellationToken);
    }

    /// <summary>
    /// The persist/metrics/dispatch tail of <see cref="HandleStepSuccessAsync"/> — split out to stay
    /// under the analyzer's method-length cap, like <see cref="CommitAndDispatchTimeoutAsync"/> and
    /// <see cref="RecordTimeoutOutcomeAsync"/> are for <see cref="HandleTimeoutAsync"/>.
    /// </summary>
    private async Task PersistAndFinalizeStepSuccessAsync(TState state, SagaStepOutcome outcome, Guid correlationId, string fromState,
        bool isNew, bool needsInsert, int expectedVersion, SagaContext<TState> context, CancellationToken cancellationToken)
    {
        // production-readiness.md §4.1 step 2: staged immediately before this persist, which commits
        // them with the snapshot in one implicit transaction, so a crash between that commit and the
        // inline drain just below still leaves a durable Pending row for the recovery poller.
        await EnqueueOutboxRowsAsync(context, cancellationToken);

        try
        {
            await PersistAsync(state, needsInsert, expectedVersion, cancellationToken);
        }
        catch (SagaConcurrencyException)
        {
            // Lost the race after this step's own side effects (a non-deferred ctx.Publish/SendAsync,
            // if any) already ran -- see production-readiness.md §5.4's note that only outbox-staged
            // publishes are protected here. The transition this method logged above was never actually
            // recorded, so the outbox rows staged immediately above must be discarded, not left Pending:
            // production-readiness.md §8.15's review built a live repro proving that on the in-memory
            // provider (whose EnqueueAsync commits non-transactionally, unlike EF Core's change-tracker
            // staging), an undiscarded row here survives the lost race and the recovery poller later
            // sends it for a transition the snapshot never recorded -- a duplicate/phantom side effect.
            // Re-throw (don't swallow, unlike the timeout path's TryPersistOrLogRaceLossAsync) so
            // HandleAsync's catch drives the usual infrastructure-failure redelivery -- §5.4/§8.15
            // confirmed this exception reliably reaches it.
            await DiscardDeferredPublishesAsync(correlationId, context, fromState, cancellationToken);
            throw;
        }

        // Only recorded once the persist above has actually committed -- recording it earlier (where
        // this call used to live, right after HandleStepSuccessAsync's own SagaCompleted log entry)
        // would emit a phantom SagaDuration for a transition a lost concurrency race then discarded,
        // the sibling bug to the outbox one just above that production-readiness.md §8.18's review
        // independently caught.
        if (outcome.FinalStatus is not null)
            VSagaDiagnostics.SagaDuration.Record((timeProvider.GetUtcNow() - state.CreatedAtUtc).TotalMilliseconds,
                new KeyValuePair<string, object?>(VSagaDiagnostics.TagSagaType, SagaType));

        await DrainDeferredPublishesAsync(correlationId, context, cancellationToken);

        if (isNew)
            VSagaDiagnostics.SagasStarted.Add(1, new KeyValuePair<string, object?>(VSagaDiagnostics.TagSagaType, SagaType));

        switch (outcome.FinalStatus)
        {
            case SagaStatus.Completed:
                VSagaDiagnostics.SagasCompleted.Add(1, new KeyValuePair<string, object?>(VSagaDiagnostics.TagSagaType, SagaType));
                break;
            case SagaStatus.Failed:
                VSagaDiagnostics.SagasFailed.Add(1, new KeyValuePair<string, object?>(VSagaDiagnostics.TagSagaType, SagaType));
                break;
        }

        await notifier.SagaUpdatedAsync(ToSummary(state), cancellationToken);
    }

    /// <summary>
    /// Stages one durable outbox row per queued publish — production-readiness.md §4.1 step 2. Called
    /// immediately before a PersistAsync so that persist's own SaveChangesAsync commits the rows and the
    /// snapshot in one implicit transaction: nothing here is durable yet on return, which is exactly the
    /// point. A persist that then throws (including HandleTimeoutAsync's race-checked one) leaves these
    /// uncommitted, and DiscardDeferredPublishesAsync drops them from the unit of work before anything
    /// else can flush them.
    /// </summary>
    /// <remarks>
    /// The row's headers are <c>publish.Envelope.Headers</c> as it stood when the publish was queued
    /// (item 16's injection of whatever consumer-span context was ambient then) -- not the producer
    /// span <see cref="SagaContext{TState}"/>'s <c>PublishInternalAsync</c> creates and re-stamps the
    /// envelope with, since that span does not exist until the closure actually runs (the inline drain
    /// right after this method's caller persists, or -- for a row that survives to be claimed later --
    /// never, because a crash-recovery replay resends the durable bytes/headers directly rather than
    /// re-invoking PublishInternalAsync). A producer span cannot be created before it is known whether
    /// the publish will even survive this persist, so there is no earlier point at which to capture its
    /// context instead. A crash-recovery replay therefore names the consumer span (or whatever was
    /// ambient at enqueue time) as its trace parent rather than a producer span that, on the crashed
    /// process, never got the chance to complete either -- arguably the more honest parent to record for
    /// that case, not a bug to fix.
    /// </remarks>
    private async Task EnqueueOutboxRowsAsync(ISagaContextDeferredPublisher publisher, CancellationToken cancellationToken)
    {
        foreach (var publish in publisher.DeferredPublishes)
        {
            // The envelope's correlation id and destination, not the publishing saga's: under Mode=All a
            // queued StartChildAsync carries a fresh id, NotifyParentAsync the parent's, and SendAsync a
            // destination. A row keyed on the publishing saga instead would have the recovery poller
            // republish the message under the wrong identity — see DeferredPublish's own note.
            await outboxStore.EnqueueAsync(SagaType, publish.Envelope.CorrelationId, publish.Envelope.MessageId,
                publish.MessageType, publish.Body, publish.Destination,
                publish.Envelope.Headers ?? new Dictionary<string, string>(StringComparer.Ordinal),
                timeProvider.GetUtcNow(), cancellationToken);
        }
    }

    /// <summary>
    /// Runs every message queued via ctx.PublishAfterCommitAsync during this step, strictly in the order
    /// queued and one at a time — never Task.WhenAll, the same reason OrderSaga's own compensation
    /// publishes are sequential (a shared DbContext behind this saga's event log is only ever safe to
    /// use one operation at a time). Only ever called after this step's own PersistAsync has already
    /// committed, so a publish failing here has nowhere safe to go: unlike a publish failing inside the
    /// step itself (which fails the whole step), this is caught, logged, and recorded on the timeline
    /// instead of thrown, and the saga is left Running for its own state timeout to rescue rather than
    /// being silently discarded by the redelivery dedupe check (§3.1 of docs/design/http-based-sagas.md).
    /// <see cref="EnqueueOutboxRowsAsync"/>'s durability copies were committed by the persist that
    /// preceded this drain -- each is marked Dispatched right after its matching send succeeds, so the
    /// recovery poller only ever sees rows for a publish that hasn't actually gone out yet.
    /// </summary>
    private async Task DrainDeferredPublishesAsync(Guid correlationId, ISagaContextDeferredPublisher publisher, CancellationToken cancellationToken)
    {
        foreach (var publish in publisher.DeferredPublishes)
        {
            try
            {
                await publish.SendAsync();
                await outboxStore.MarkDispatchedAsync(publish.Envelope.MessageId, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Deferred publish failed for saga {SagaType} correlation {CorrelationId} after its step already committed; leaving the saga Running for its own state timeout to rescue it",
                    SagaType, correlationId);

                await LogAsync(SagaLogEntry.Create(correlationId, SagaType, SagaEntryType.DeliveryExhausted, errorMessage: ex.Message), cancellationToken);
            }
        }
    }

    /// <summary>
    /// docs/design/mixed-sagas.md §5: the timeout's own final persist lost the optimistic-concurrency race, so
    /// the state transition that queued these was never actually committed -- publishing them now would
    /// announce a transition nobody recorded. Reuses DrainDeferredPublishesAsync's own "leave the saga
    /// for its own timeout to rescue it" policy (one DeliveryExhausted entry per dropped publish, logged
    /// and swallowed, never thrown) rather than inventing a second one -- the only difference from a
    /// drain is that these are never sent at all.
    /// </summary>
    /// <summary>
    /// The <see cref="DiscardDeferredPublishesAsync"/> counterpart for the engine's own staged
    /// ChildSagaFinished row: the persist that would have made this saga terminal lost its race, so the
    /// saga is not in fact finished and announcing otherwise would be a lie the parent acts on.
    /// </summary>
    private async Task DiscardStagedChildSagaFinishedAsync(TState state, StagedChildSagaFinished? staged, string forState, CancellationToken cancellationToken)
    {
        if (staged is not { } pending)
            return;

        await outboxStore.DiscardPendingAsync([pending.Envelope.MessageId], cancellationToken);

        logger.LogWarning(
            "Discarding the engine's ChildSagaFinished for saga {SagaType} correlation {CorrelationId} in state {State}: its timeout lost the persist race, so this saga never actually reached a terminal status",
            SagaType, state.CorrelationId, forState);
    }

    private async Task DiscardDeferredPublishesAsync(Guid correlationId, ISagaContextDeferredPublisher publisher, string forState, CancellationToken cancellationToken)
    {
        // Before the LogAsync calls below, not after: those append to the event log through the same
        // shared unit of work, so their SaveChangesAsync would commit the very outbox rows this discard
        // exists to suppress -- turning each dropped publish into one the recovery poller then sends
        // anyway, ~DispatchGracePeriod later.
        await outboxStore.DiscardPendingAsync(
            publisher.DeferredPublishes.Select(publish => publish.Envelope.MessageId).ToList(), cancellationToken);

        foreach (var messageType in publisher.DeferredPublishes.Select(publish => publish.MessageType))
        {
            logger.LogWarning(
                "Discarding a deferred publish of {MessageType} for saga {SagaType} correlation {CorrelationId} in state {State}: its timeout lost the persist race, so the transition that queued it was never recorded",
                messageType, SagaType, correlationId, forState);

            await LogAsync(SagaLogEntry.Create(correlationId, SagaType, SagaEntryType.DeliveryExhausted,
                fromState: forState, messageType: messageType,
                errorMessage: "Deferred publish discarded: its timeout lost the persist race before committing."), cancellationToken);
        }
    }

    private Task PersistAsync(TState state, bool isNew, int expectedVersion, CancellationToken cancellationToken)
    {
        state.UpdatedAtUtc = timeProvider.GetUtcNow();

        return isNew
            ? snapshotStore.InsertAsync(state, cancellationToken)
            : snapshotStore.UpdateAsync(state, expectedVersion, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> GetVisitedStatesAsync(Guid correlationId, CancellationToken cancellationToken)
    {
        var timeline = await eventLog.GetTimelineAsync(SagaType, correlationId, cancellationToken);

        return timeline
            .Where(e => e.ToState is not null)
            .Select(e => e.ToState!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Slice 2b's safety net: publishes ChildSagaFinished to <paramref name="state"/>'s parent (if any)
    /// on its behalf. Called only from the two paths ctx.NotifyParentAsync structurally cannot reach —
    /// HandleStepFailureAsync's exception path and HandleTimeoutAsync's terminal-timeout path — never
    /// from the ordinary message-driven success path, where a child had every opportunity to report its
    /// actual result itself and firing here too would be a redundant, data-free duplicate of that.
    /// <para>
    /// Not routed through SagaContext/ISagaContext, unlike NotifyParentAsync: this is the engine
    /// publishing on the child's behalf, not the child's own step code, so it uses <c>transport</c>
    /// directly and logs onto this instance's own timeline itself.
    /// </para>
    /// <para>
    /// No per-parent opt-in check here by design: a parent only ever receives this if it declared a
    /// handler for <see cref="ChildSagaFinished"/> somewhere in its own DSL, because that declaration is
    /// what <c>SagaRuntime</c>'s transport subscription is built from
    /// (<c>ISagaDefinition.MessageTypes</c>) — a parent that never asked for it is never even subscribed,
    /// so publishing unconditionally here is exactly as safe as NotifyParentAsync's own unconditional
    /// publish.
    /// </para>
    /// </summary>
    private async Task<StagedChildSagaFinished?> StageChildSagaFinishedAsync(TState state, SagaStatus status, string? causationMessageId, CancellationToken cancellationToken)
    {
        if (state.ParentCorrelationId is not { } parentCorrelationId)
            return null;

        var message = new ChildSagaFinished(state.CorrelationId, SagaType, status);
        var envelope = MessageEnvelope.From(SagaType, parentCorrelationId, causationMessageId);

        await outboxStore.EnqueueAsync(SagaType, state.CorrelationId, envelope.MessageId, nameof(ChildSagaFinished),
            JsonSerializer.SerializeToUtf8Bytes(message), destination: null, envelope.Headers!,
            timeProvider.GetUtcNow(), cancellationToken);

        return new StagedChildSagaFinished(message, envelope, causationMessageId);
    }

    /// <summary>
    /// Sends what <see cref="StageChildSagaFinishedAsync"/> staged, once the persist that made this saga
    /// terminal has committed its outbox row along with the snapshot. Typed <c>transport.PublishAsync</c>,
    /// not the raw path, for the same §4.1 reason the deferred drain is typed: an in-memory subscriber
    /// asserting on <c>PublishedMessage.Message</c> would see null through <c>PublishRawAsync</c>.
    /// </summary>
    private async Task PublishChildSagaFinishedAsync(TState state, StagedChildSagaFinished? staged, CancellationToken cancellationToken)
    {
        if (staged is not { } pending)
            return;

        await transport.PublishAsync(pending.Message, pending.Envelope, cancellationToken);
        await outboxStore.MarkDispatchedAsync(pending.Envelope.MessageId, cancellationToken);

        await LogAsync(SagaLogEntry.Create(state.CorrelationId, SagaType, SagaEntryType.ChildSagaFinished,
            messageType: nameof(ChildSagaFinished), messageId: pending.Envelope.MessageId,
            sourceService: SagaType, causationId: pending.CausationMessageId), cancellationToken);
    }

    /// <summary>
    /// The engine's own ChildSagaFinished publish, staged into the outbox before the persist that
    /// justifies it and awaiting its inline send afterwards — the second publish surface §4.3 names.
    /// Held as a value rather than re-derived after the persist so the row and the send describe one
    /// message identity, exactly as DeferredPublish does for ctx.PublishAfterCommitAsync.
    /// </summary>
    private readonly record struct StagedChildSagaFinished(ChildSagaFinished Message, MessageEnvelope Envelope, string? CausationMessageId);

    private async Task LogAsync(SagaLogEntry entry, CancellationToken cancellationToken)
    {
        await eventLog.AppendAsync(entry, cancellationToken);
        await notifier.TimelineEntryAddedAsync(entry.SagaType, entry.CorrelationId, entry, cancellationToken);
    }

    private static SagaSummary ToSummary(TState state) =>
        new(state.CorrelationId, state.SagaType, state.Kind, state.CurrentState, state.Status, state.CreatedAtUtc, state.UpdatedAtUtc, state.Version,
            state.ParentSagaType, state.ParentCorrelationId);
}
