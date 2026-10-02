using System.Diagnostics;
using System.Text.Json;
using VSaga.Abstractions.Diagnostics;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Abstractions.Transport;

namespace VSaga.Core.Runtime;

/// <summary>
/// Lets VSaga.Core.Dsl.SagaDefinitionModel append CompensationStarted/StepSucceeded/StepFailed entries
/// through the same log funnel a SagaContext already uses for outbound-message logging, without
/// widening the public ISagaContext&lt;TState&gt; DSL surface with an engine-internal concern.
/// </summary>
internal interface ISagaContextLogSink
{
    Task LogAsync(SagaLogEntry entry, CancellationToken cancellationToken);
}

/// <summary>
/// One queued publish — every ctx.PublishAfterCommitAsync call, plus every ordinary publish once
/// SagaOutboxOptions.Mode is All. Named by its message type so a drain or a discard (see
/// SagaOrchestrator.DiscardDeferredPublishesAsync) can log what it's handling rather than just count it.
/// Carries both the durability copy (production-readiness.md §4.3's outbox row shape -- everything
/// ISagaOutboxStore.EnqueueAsync needs) and the strongly-typed <see cref="SendAsync"/> closure the
/// inline drain still calls, per §4.1's constraint that the inline path cannot go through
/// PublishRawAsync without breaking TimeoutDrainTests.cs:75's typed-Message assertion.
/// <para>
/// The whole <see cref="Envelope"/> rather than just its MessageId, and <see cref="Destination"/>
/// alongside it, because Mode=All widened what can land here. Under Deferred these were redundant —
/// PublishAfterCommitAsync has no destination-taking overload and always publishes under the saga's own
/// correlation id. Under All, ctx.SendAsync queues a destination, StartChildAsync queues a *fresh*
/// correlation id and NotifyParentAsync the parent's, so a row recording the saga's own id (or dropping
/// the destination) would have the recovery poller republish the message somewhere it was never meant
/// to go — creating a child saga under the parent's id, or broadcasting an addressed send.
/// </para>
/// </summary>
internal readonly record struct DeferredPublish(
    string MessageType,
    MessageEnvelope Envelope,
    ReadOnlyMemory<byte> Body,
    string? Destination,
    Func<Task> SendAsync);

/// <summary>
/// Lets SagaOrchestrator.HandleStepSuccessAsync drain a step's ctx.PublishAfterCommitAsync queue once
/// its own PersistAsync has committed, through the same kind of internal cast ISagaContextLogSink already
/// uses, without widening the public ISagaContext&lt;TState&gt; DSL surface with an engine-internal concern.
/// <see cref="ClearDeferredPublishes"/> is docs/design/mixed-sagas.md §3.2's fix: StepExecutor calls it on a
/// step's retry so a replay from index 0 discards side effects queued but never committed by the
/// attempt that just threw, rather than accumulating one entry per attempt with no way to dedupe them
/// (each mints a fresh MessageId, so ISagaEventLogStore.IsDuplicateAsync can't catch the extras).
/// </summary>
internal interface ISagaContextDeferredPublisher
{
    IReadOnlyList<DeferredPublish> DeferredPublishes { get; }

    void ClearDeferredPublishes();
}

internal sealed class SagaContext<TState>(
    TState saga,
    Guid correlationId,
    IReadOnlyDictionary<string, string> headers,
    IReadOnlyList<string> visitedStates,
    IServiceProvider services,
    IMessageTransport transport,
    string sagaType,
    string? inboundMessageId,
    Func<SagaLogEntry, CancellationToken, Task> logAsync,
    bool deferAllPublishes,
    CancellationToken cancellationToken) : ISagaContext<TState>, ISagaContextLogSink, ISagaContextDeferredPublisher
    where TState : SagaState
{
    private readonly List<DeferredPublish> _deferredPublishes = [];

    public TState Saga { get; } = saga;

    public Guid CorrelationId { get; } = correlationId;

    public IReadOnlyList<string> VisitedStates { get; } = visitedStates;

    /// <summary>
    /// The UTF-8 length of the StatePersisted payloads the saga's timeline held when this step started,
    /// read alongside <see cref="VisitedStates"/>. Engine-internal: SagaOrchestrator holds the snapshot
    /// it records after this step to its per-saga budget with it (SagaOrchestratorOptions.MaxStateSnapshotBytesPerSaga).
    /// </summary>
    internal long RecordedSnapshotBytes { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; } = headers;

    public IServiceProvider Services { get; } = services;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    public Task PublishAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull =>
        RouteAsync(message, destination: null, MessageEnvelope.From(sagaType, CorrelationId, inboundMessageId), cancellationToken);

    public Task SendAsync<TMessage>(string destination, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull =>
        RouteAsync(message, destination, MessageEnvelope.From(sagaType, CorrelationId, inboundMessageId), cancellationToken);

    public Task StartChildAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull
    {
        // A fresh correlation id rather than this saga's. The snapshot primary key is
        // (SagaType, CorrelationId), so sharing one would cap a parent at a single child per saga type
        // and make a self-recursive saga (a RefundSaga starting a RefundSaga) collide with itself.
        // The link back is carried in headers instead, which SagaOrchestrator reads exactly once, when
        // it creates the child instance.
        var linkage = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessageEnvelope.ParentSagaTypeHeader] = sagaType,
            [MessageEnvelope.ParentCorrelationIdHeader] = CorrelationId.ToString(),
        };

        var envelope = MessageEnvelope.From(sagaType, Guid.NewGuid(), inboundMessageId, linkage);

        return RouteAsync(message, destination: null, envelope, cancellationToken, SagaEntryType.ChildSagaStarted);
    }

    public Task NotifyParentAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull
    {
        // Checked before any I/O: a root saga has no parent to publish under, and there is no sensible
        // id to fall back to — this must fail loudly rather than silently drop the notification or,
        // worse, publish under this saga's own id where nothing is waiting for it.
        if (Saga.ParentCorrelationId is not { } parentCorrelationId)
        {
            throw new InvalidOperationException(
                $"Saga '{sagaType}' correlation '{CorrelationId}' has no parent to notify. " +
                $"NotifyParentAsync can only be called from a saga started via StartChildAsync.");
        }

        // The only correlation id this can publish under is the one the engine already stamped onto
        // this instance — see the "not a general publish-under-any-id overload" note on the interface.
        var envelope = MessageEnvelope.From(sagaType, parentCorrelationId, inboundMessageId);

        return RouteAsync(message, destination: null, envelope, cancellationToken);
    }

    /// <summary>
    /// Queues the publish rather than sending it now — see the interface doc for why. Built eagerly at
    /// call time (correlation id, source service, causation id all come from this instance, exactly like
    /// PublishAsync's own envelope), so only the actual transport call and its timeline entry are
    /// deferred to the drain.
    /// </summary>
    public Task PublishAfterCommitAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull =>
        QueueAsync(message, destination: null, MessageEnvelope.From(sagaType, CorrelationId, inboundMessageId), cancellationToken);

    /// <summary>
    /// The Deferred-vs-All fork (production-readiness.md §8 item 11). Under the default Deferred mode an
    /// ordinary publish goes out inline exactly as it always has; under All it joins the same queue
    /// PublishAfterCommitAsync uses, which is the only way "routed through the outbox" can mean anything
    /// for a mid-step publish — a row written while the message is already gone guarantees nothing.
    /// PublishAfterCommitAsync itself never comes through here: it queues unconditionally, in both modes.
    /// </summary>
    private Task RouteAsync<TMessage>(TMessage message, string? destination, MessageEnvelope envelope,
        CancellationToken cancellationToken, SagaEntryType? entryTypeOverride = null) where TMessage : notnull =>
        deferAllPublishes
            ? QueueAsync(message, destination, envelope, cancellationToken, entryTypeOverride)
            : PublishInternalAsync(message, destination, envelope, cancellationToken, entryTypeOverride);

    /// <summary>
    /// Queues rather than sends. The closure calls <see cref="PublishInternalAsync"/> directly, never
    /// <see cref="RouteAsync"/> — under Mode=All the latter would re-queue the message the drain is
    /// trying to send, and nothing would ever go out.
    /// </summary>
    private Task QueueAsync<TMessage>(TMessage message, string? destination, MessageEnvelope envelope,
        CancellationToken cancellationToken, SagaEntryType? entryTypeOverride = null) where TMessage : notnull
    {
        _deferredPublishes.Add(new DeferredPublish(typeof(TMessage).Name, envelope,
            JsonSerializer.SerializeToUtf8Bytes(message), destination,
            () => PublishInternalAsync(message, destination, envelope, cancellationToken, entryTypeOverride)));

        return Task.CompletedTask;
    }

    IReadOnlyList<DeferredPublish> ISagaContextDeferredPublisher.DeferredPublishes => _deferredPublishes;

    void ISagaContextDeferredPublisher.ClearDeferredPublishes() => _deferredPublishes.Clear();

    /// <summary>
    /// Stamps the step's inbound message id as the causation id of an entry that names none (the
    /// Compensation entries, a .CallHttp request), so the dashboard can attach it to the step that logged
    /// it when another handler of the same instance interleaves. An entry with its own causation id (a
    /// .CallHttp reply, caused by the request) keeps it, and on the timeout path there is no inbound
    /// message, so entries pass through unchanged. The causation id filled here is never read by dedupe or
    /// by the map's stitching (the map reads causation only from MessageReceived entries, and a .CallHttp
    /// reply keeps its own), so nothing else moves.
    /// </summary>
    Task ISagaContextLogSink.LogAsync(SagaLogEntry entry, CancellationToken cancellationToken) =>
        logAsync(entry.CausationId is null && inboundMessageId is not null ? entry with { CausationId = inboundMessageId } : entry, cancellationToken);

    private async Task PublishInternalAsync<TMessage>(TMessage message, string? destination, MessageEnvelope envelope, CancellationToken cancellationToken, SagaEntryType? entryTypeOverride = null) where TMessage : notnull
    {
        var effectiveCt = cancellationToken == default ? CancellationToken : cancellationToken;

        // production-readiness.md §6/§8.18: the producer span, new here -- this is the single publish
        // chokepoint all five ISagaContext publish paths funnel into (RouteAsync/QueueAsync's closure
        // both land here), so it's the one place that can stamp every outbound message uniformly,
        // including ones a step queues from a context that never had a consumer span at all (e.g.
        // HandleTimeoutAsync's own SagaContext). MessageEnvelope.From already injects whatever
        // Activity.Current happens to be at envelope-construction time (item 16) -- but that happens at
        // each call site, before RouteAsync/QueueAsync is even invoked, so it can only ever see an
        // *ambient* activity (the enclosing consumer span, or nothing). This span is what that
        // injection was missing: it re-stamps the envelope's headers with its own context right before
        // the message actually goes out, so the header on the wire names this producer span -- not the
        // grandparent consumer span -- as the next hop's parent, which is what W3C trace context expects.
        using var activity = VSagaDiagnostics.ActivitySource.StartActivity(
            $"saga.publish {typeof(TMessage).Name}", ActivityKind.Producer);

        if (activity is not null)
        {
            activity.SetTag(VSagaDiagnostics.TagSagaType, sagaType);
            activity.SetTag(VSagaDiagnostics.TagCorrelationId, CorrelationId.ToString());

            var outboundHeaders = envelope.Headers is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(envelope.Headers, StringComparer.Ordinal);
            VSagaDiagnostics.Inject(activity.Context, outboundHeaders);
            envelope = envelope with { Headers = outboundHeaders };
        }

        if (destination is null)
            await transport.PublishAsync(message, envelope, effectiveCt);
        else
            await transport.SendAsync(destination, message, envelope, effectiveCt);

        // Logged only after the transport call succeeds — a publish that throws never happened, so it
        // must not leave a trace behind (the step-level failure path logs its own StepFailed entry for
        // that case).
        // CorrelationId, not envelope.CorrelationId: the entry belongs on the timeline of the saga that
        // published — for StartChildAsync that's the parent's (the child's own timeline opens with its
        // SagaStarted entry under its own id), and for NotifyParentAsync it's symmetrically the child's
        // own (the parent sees it arrive as an ordinary MessageReceived on its own timeline instead).
        // entryTypeOverride lets StartChildAsync tag its hop ChildSagaStarted instead of the ordinary
        // MessagePublished/MessageSent every other publish gets — see SagaEntryType's Slice 2b note.
        var entryType = entryTypeOverride ?? (destination is null ? SagaEntryType.MessagePublished : SagaEntryType.MessageSent);
        await logAsync(SagaLogEntry.Create(CorrelationId, sagaType, entryType,
            messageType: typeof(TMessage).Name, messageId: envelope.MessageId,
            sourceService: sagaType, destinationService: destination, causationId: inboundMessageId), effectiveCt);
    }
}
