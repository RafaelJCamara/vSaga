using System.Collections.Concurrent;
using System.Threading.Channels;
using VSaga.Abstractions.Transport;
using Microsoft.Extensions.Logging;

namespace VSaga.Transport.Http;

/// <summary>
/// Owns the local subscriber registry (populated by SubscribeAsync, matched exactly like
/// InMemoryMessageTransport.DispatchAsync) and the per-correlation-id dispatch gate that is this
/// adapter's whole answer to docs/design/http-based-sagas.md §3.1: a reply must never re-enter a saga while
/// its own step is still running.
/// <para>
/// Exactly two entry points ever reach a local subscriber, and the asymmetry between them is the
/// entire §3.1 answer:
/// </para>
/// <list type="bullet">
/// <item><see cref="DispatchInlineAsync"/> -- a genuine inbound HTTP request. Dispatched immediately,
/// holding the gate, because the handler's reply has to be captured before the response is written --
/// unless the gate can't be acquired within <see cref="InlineGateAcquireTimeout"/>, in which case it
/// falls back to the deferred path below rather than blocking the connection for the full
/// RequestTimeout (found live: a fan-out reply that routes back to its own originating service can
/// otherwise deadlock that service's gate against itself).</item>
/// <item><see cref="EnqueueLocalDispatch"/> -- everything else that resolves to a local subscriber: a
/// same-process PublishAsync/PublishRawAsync (including §3.3a's redelivery, which runs from *inside*
/// an already-gated dispatch) and a 200 reply to our own outbound POST. Never dispatched inline --
/// always enqueued to <see cref="_localDispatchChannel"/> and drained by <see cref="PumpLoopAsync"/>,
/// which takes the same gate. That is what lets a redelivery enqueue itself without deadlocking on the
/// gate its own catch block is running inside, and what makes a reply wait for the publishing step to
/// finish (i.e. until after PersistAsync and the ack) before it can be dispatched.
/// </item>
/// </list>
/// <para>
/// It also owns the §4.4 ack model, which has no broker underneath it and therefore exactly one place
/// a redelivery can go -- <see cref="_localDispatchChannel"/>. <see cref="EnqueueLocalDelivery"/>
/// hands out a delivery whose <c>NackAsync(requeue: true)</c> genuinely re-enqueues (a redelivery off
/// that channel is byte-identical to the original delivery, headers and all);
/// <see cref="CreateInboundRequestAck"/> hands out one that can only log at error and drop, because an
/// inbound request's delivery is inseparable from the HTTP request carrying it. See both members for
/// the full reasoning, and <see cref="MaxRequeueAttempts"/> for what bounds a requeue loop.
/// </para>
/// </summary>
public sealed class HttpInboundDispatcher : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, SubscriberEntry> _subscribers = new();

    // Keyed on the raw transport correlation id, not any resolved saga instance -- production-readiness.md
    // §5.4's documented, accepted gap: two messages resolving to the same saga via a shared business key
    // (§5.2/§5.3) but carrying different transport correlation ids get independent gate entries here and
    // run fully concurrently. Pinned by HttpInboundDispatcherGateHazardTests (VSaga.Transport.Http.Tests).
    // The backstop for that case -- the snapshot store's optimistic-concurrency Version check, and that a
    // SagaConcurrencyException from it reliably reaches SagaOrchestrator.HandleInfrastructureFailureAsync's
    // redelivery rather than being swallowed -- is verified separately by
    // SagaOrchestratorConcurrencyRedeliveryTests (VSaga.Core.Tests). Change either exception path or this
    // key without re-checking both; §5.4 has the full account of what that backstop does and does not
    // actually guarantee.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _correlationGates = new();
    private readonly Channel<ReceivedMessage> _localDispatchChannel = Channel.CreateUnbounded<ReceivedMessage>();
    private readonly ILogger<HttpInboundDispatcher> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _pumpTask;

    public HttpInboundDispatcher(ILogger<HttpInboundDispatcher> logger)
    {
        _logger = logger;
        _pumpTask = Task.Run(() => PumpLoopAsync(_stopping.Token), _stopping.Token);
    }

    private sealed record SubscriberEntry(TransportSubscription Subscription, Func<ReceivedMessage, CancellationToken, Task> Handler);

    private sealed class Unsubscriber(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    public Task<IDisposable> SubscribeAsync(TransportSubscription subscription, Func<ReceivedMessage, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        _subscribers[id] = new SubscriberEntry(subscription, handler);
        return Task.FromResult<IDisposable>(new Unsubscriber(() => _subscribers.TryRemove(id, out _)));
    }

    /// <summary>Whether any locally-registered subscription declares this message type -- the local half of §3.3a's routing union.</summary>
    public bool HasLocalSubscriber(string messageTypeName) =>
        _subscribers.Values.Any(s => MatchesType(s.Subscription, messageTypeName));

    /// <summary>Queues an already-constructed delivery for local dispatch without blocking on the correlation gate -- see the type doc for why this, never inline, is the only path other than a genuine inbound request. Keeps <paramref name="received"/>'s own ack context: deferring a delivery is a delay, not a change of custody.</summary>
    public void EnqueueLocalDispatch(ReceivedMessage received) =>
        _localDispatchChannel.Writer.TryWrite(received);

    /// <summary>
    /// How many times one delivery may be handed back by <c>NackAsync(requeue: true)</c> before this
    /// dispatcher stops honouring the requeue and drops it with an error log instead.
    /// <para>
    /// This is the bound on the one genuinely unbounded shape §4.4's ack model admits. The engine's own
    /// redelivery loop is bounded elsewhere and differently: SagaOrchestrator.HandleInfrastructureFailureAsync
    /// republishes through <c>PublishRawAsync</c> with an incremented <c>x-vsaga-delivery-attempt</c>
    /// header and dead-letters once it reaches <c>SagaOrchestratorOptions.MaxDeliveryAttempts</c> -- it
    /// never calls <c>NackAsync(requeue: true)</c> at all. A requeue therefore increments nothing the
    /// orchestrator reads, so a handler that unconditionally requeues would spin this channel forever
    /// on a counter nobody owns. Hence a counter this dispatcher owns, carried on the ack context of
    /// each successive delivery rather than in the headers -- which keeps a redelivery byte-identical
    /// to its original, <c>x-vsaga-delivery-attempt</c> included (see <see cref="TryRequeue"/>).
    /// </para>
    /// <para>
    /// The two bounds compose rather than cancel: a requeue chain terminates here after at most this
    /// many hops, and a republish chain terminates at MaxDeliveryAttempts because every republish
    /// increments the header this dispatcher preserves. Interleaving them is bounded by their product,
    /// which is finite -- no arrangement of the two produces an unbounded redelivery loop.
    /// </para>
    /// </summary>
    public const int MaxRequeueAttempts = 5;

    /// <summary>
    /// Enqueues a message whose delivery this dispatcher itself owns -- a same-process
    /// PublishAsync/PublishRawAsync that resolved to a local subscriber, or a 200 reply to our own
    /// outbound POST -- carrying an ack context that implements §4.4's model for real: <c>AckAsync</c>
    /// drops, <c>NackAsync(requeue: true)</c> re-enqueues onto this same channel, and
    /// <c>NackAsync(requeue: false)</c> logs at error and drops. Requeue is honest here precisely
    /// because the redelivery lands back where the original delivery came from, with the same headers
    /// and the same deferred (never inline) dispatch semantics -- nothing about the redelivered copy
    /// differs from the first one except that it is later.
    /// </summary>
    public void EnqueueLocalDelivery(string messageTypeName, Guid correlationId, string messageId,
        ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string> headers) =>
        EnqueueLocalDispatch(BuildLocalDelivery(messageTypeName, correlationId, messageId, body, headers, requeueCount: 0));

    /// <summary>
    /// An ack context for a delivery that arrived as a genuine inbound HTTP request and was dispatched
    /// inline by <see cref="DispatchInlineAsync"/>: <c>AckAsync</c> drops, and <em>both</em> nack forms
    /// log at error and drop.
    /// <para>
    /// <c>requeue: true</c> is deliberately not supported on this path, and re-enqueuing onto the local
    /// channel would not be an honest implementation of it. That delivery is inseparable from the HTTP
    /// request carrying it: it runs under the ambient <see cref="SyncReplyCollector"/>, and the status
    /// and body the remote peer receives are decided by this very dispatch's outcome. A re-enqueued
    /// copy would be dispatched later, off the pump, with no collector installed and the response long
    /// since written -- so a participant's reply publish (the whole point of an inbound request here)
    /// would find nothing to capture it and throw unroutable instead. That is a different, reply-less
    /// delivery wearing the original's name, not a redelivery of it. The peer that POSTed the message
    /// owns its retry; this process cannot ask for one.
    /// </para>
    /// <para>
    /// Deliberately also used for the copy <see cref="DispatchInlineAsync"/> defers to the pump on a
    /// gate-acquire timeout, even though that copy does land on the channel: that deferral is a delay
    /// of the same delivery, and letting gate contention silently decide whether a message's nack means
    /// "redeliver" or "drop" would be worse than one simple rule.
    /// </para>
    /// </summary>
    public IMessageAckContext CreateInboundRequestAck(string messageTypeName, Guid correlationId, string messageId) =>
        new InboundRequestAckContext(_logger, messageTypeName, correlationId, messageId);

    private ReceivedMessage BuildLocalDelivery(string messageTypeName, Guid correlationId, string messageId,
        ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string> headers, int requeueCount)
    {
        var ack = new ChannelRequeueAckContext(this, requeueCount);
        var delivery = new ReceivedMessage(messageTypeName, correlationId, messageId, body, headers, ack);
        ack.Attach(delivery);
        return delivery;
    }

    /// <summary>
    /// Re-enqueues one delivery under a fresh ack context carrying an incremented requeue count (see
    /// <see cref="MaxRequeueAttempts"/>). Everything else is passed through by reference -- the very
    /// same headers dictionary instance, so <c>x-vsaga-delivery-attempt</c> and every other engine
    /// header reach the redelivered copy exactly as the orchestrator wrote them, and the cap that
    /// header feeds keeps bounding redelivery across the requeue. Returns false once the channel has
    /// been completed (shutdown), which is the caller's cue to fall back to logging and dropping rather
    /// than throwing into a handler that is already unwinding.
    /// </summary>
    private bool TryRequeue(ReceivedMessage delivery, int nextRequeueCount) =>
        _localDispatchChannel.Writer.TryWrite(BuildLocalDelivery(
            delivery.MessageTypeName, delivery.CorrelationId, delivery.MessageId, delivery.Body, delivery.Headers, nextRequeueCount));

    /// <summary>
    /// Bound on acquiring the correlation gate for a genuine inbound request before giving up and
    /// deferring to the pump instead of continuing to block the HTTP connection. Found live: a fan-out
    /// reply that routes back to its own originating service (e.g. OrderShipped reaching both its local
    /// participants and back to the saga host) can deadlock that service's own gate against itself --
    /// the saga's dispatch holds the gate while awaiting ShipOrder's response, and Participants can't
    /// finish answering ShipOrder until its own nested OrderShipped POST back to the saga host is
    /// accepted, which needs the very gate the saga is still holding. Deferring after a short bound
    /// breaks the cycle losslessly (202 now, dispatched once the gate frees) instead of blocking for
    /// the full RequestTimeout.
    /// </summary>
    private static readonly TimeSpan InlineGateAcquireTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The one inline path: a genuine inbound HTTP request, dispatched immediately under an ambient reply collector so a synchronous reply can be captured before the caller's response is written.</summary>
    public async Task<InlineDispatchResult> DispatchInlineAsync(ReceivedMessage received, CancellationToken cancellationToken)
    {
        var gate = _correlationGates.GetOrAdd(received.CorrelationId, static _ => new SemaphoreSlim(1, 1));
        var acquired = await gate.WaitAsync(InlineGateAcquireTimeout, cancellationToken);

        if (!acquired)
        {
            _logger.LogWarning(
                "Could not acquire the dispatch gate for correlation {CorrelationId} within {Timeout} -- deferring {MessageType} to the local dispatch queue instead of blocking this request",
                received.CorrelationId, InlineGateAcquireTimeout, received.MessageTypeName);
            EnqueueLocalDispatch(received);
            return InlineDispatchResult.Accepted;
        }

        var collector = new SyncReplyCollector();
        SyncReplyCollectorAccessor.Current = collector;
        try
        {
            await RunSubscribersAsync(received, cancellationToken);
        }
        finally
        {
            collector.Seal();
            SyncReplyCollectorAccessor.Current = null;
            ReleaseGate(received.CorrelationId, gate);
        }

        return collector.Captured is { } reply ? InlineDispatchResult.WithReply(reply) : InlineDispatchResult.Accepted;
    }

    /// <summary>
    /// Drains the channel and fans each item out as its own fire-and-forget dispatch rather than
    /// awaiting one before reading the next -- correctness for a single correlation id comes entirely
    /// from the gate in <see cref="DispatchToSubscribersAsync"/>, not from pump ordering, so an
    /// unrelated correlation's dispatch is never held up behind a slow one.
    /// </summary>
    private async Task PumpLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var received in _localDispatchChannel.Reader.ReadAllAsync(cancellationToken))
            {
                _ = DispatchAndLogAsync(received, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task DispatchAndLogAsync(ReceivedMessage received, CancellationToken cancellationToken)
    {
        try
        {
            await DispatchToSubscribersAsync(received, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error draining local dispatch for {MessageType} correlation {CorrelationId}",
                received.MessageTypeName, received.CorrelationId);
        }
    }

    /// <summary>
    /// Acquires the per-correlation gate, then invokes every matching subscriber's handler in turn,
    /// each independently caught and logged -- mirroring RabbitMqTransport's dispatch-level catch
    /// (log + drop rather than propagate) so one failing subscriber can't take down a sibling's fan-out
    /// delivery of the same message, exactly as if each had its own broker-bound queue.
    /// </summary>
    private async Task DispatchToSubscribersAsync(ReceivedMessage received, CancellationToken cancellationToken)
    {
        var gate = _correlationGates.GetOrAdd(received.CorrelationId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await RunSubscribersAsync(received, cancellationToken);
        }
        finally
        {
            ReleaseGate(received.CorrelationId, gate);
        }
    }

    /// <summary>Invokes every matching subscriber's handler in turn, each independently caught and logged -- mirroring RabbitMqTransport's dispatch-level catch (log + drop rather than propagate) so one failing subscriber can't take down a sibling's fan-out delivery of the same message, exactly as if each had its own broker-bound queue. Assumes the caller already holds this correlation's gate.</summary>
    private async Task RunSubscribersAsync(ReceivedMessage received, CancellationToken cancellationToken)
    {
        foreach (var subscriber in _subscribers.Values)
        {
            if (!MatchesType(subscriber.Subscription, received.MessageTypeName))
                continue;

            try
            {
                await subscriber.Handler(received, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error dispatching {MessageType} to consumer {ConsumerName} for correlation {CorrelationId}",
                    received.MessageTypeName, subscriber.Subscription.ConsumerName, received.CorrelationId);
            }
        }
    }

    private void ReleaseGate(Guid correlationId, SemaphoreSlim gate)
    {
        gate.Release();

        // Best-effort cleanup: only removes the entry if it's uncontended at this exact moment, which
        // is safe either way -- see the type's remarks in docs/design/http-based-sagas.md §4.4 for why a
        // benign TOCTOU race here can't strand a waiter (an uncontended semaphore has none).
        if (gate.CurrentCount == 1)
            _correlationGates.TryRemove(new KeyValuePair<Guid, SemaphoreSlim>(correlationId, gate));
    }

    private static bool MatchesType(TransportSubscription subscription, string messageTypeName) =>
        subscription.MessageTypes.Any(t => string.Equals(t.Name, messageTypeName, StringComparison.Ordinal));

    /// <summary>
    /// §4.4's ack model for a delivery this dispatcher owns (see <see cref="EnqueueLocalDelivery"/>):
    /// ack drops, nack(requeue: true) re-enqueues onto the same in-process channel, nack(requeue: false)
    /// logs at error and drops. Settling is idempotent via an <see cref="Interlocked"/> guard, so a
    /// handler that acks and then nacks in a <c>finally</c>, or nacks twice down two unwinding paths,
    /// can never enqueue the same message twice -- first settle wins, every later call is a no-op.
    /// </summary>
    private sealed class ChannelRequeueAckContext(HttpInboundDispatcher dispatcher, int requeueCount) : IMessageAckContext
    {
        private ReceivedMessage _delivery = null!;
        private int _settled;

        /// <summary>Completes the cycle between a delivery and its ack context; called once, before the delivery is written to the channel, so nothing can observe an unattached context.</summary>
        internal void Attach(ReceivedMessage delivery) => _delivery = delivery;

        private bool TrySettle() => Interlocked.Exchange(ref _settled, 1) == 0;

        public Task AckAsync(CancellationToken cancellationToken = default)
        {
            TrySettle();
            return Task.CompletedTask;
        }

        public Task NackAsync(bool requeue, CancellationToken cancellationToken = default)
        {
            if (!TrySettle())
                return Task.CompletedTask;

            if (!requeue)
            {
                // No broker and no dead-letter queue by design (§4.4: an IHttpDeadLetterSink with one
                // logging implementation is ceremony), so an error log with enough identity to find the
                // message in the sender's own logs IS the dead-letter record.
                dispatcher._logger.LogError(
                    "Dropping {MessageType} (correlation {CorrelationId}, message {MessageId}) nacked with requeue: false -- this transport has no dead-letter queue, so the saga's own state timeout is the safety net",
                    _delivery.MessageTypeName, _delivery.CorrelationId, _delivery.MessageId);
                return Task.CompletedTask;
            }

            if (requeueCount >= MaxRequeueAttempts)
            {
                dispatcher._logger.LogError(
                    "Dropping {MessageType} (correlation {CorrelationId}, message {MessageId}) nacked with requeue: true after {RequeueCount} requeues -- the {MaxRequeueAttempts}-requeue cap is what stops a handler that always requeues from spinning the local dispatch channel forever",
                    _delivery.MessageTypeName, _delivery.CorrelationId, _delivery.MessageId, requeueCount, MaxRequeueAttempts);
                return Task.CompletedTask;
            }

            if (!dispatcher.TryRequeue(_delivery, requeueCount + 1))
            {
                dispatcher._logger.LogError(
                    "Dropping {MessageType} (correlation {CorrelationId}, message {MessageId}) nacked with requeue: true -- the local dispatch channel is already completed (shutting down), so there is nowhere to redeliver it to",
                    _delivery.MessageTypeName, _delivery.CorrelationId, _delivery.MessageId);
                return Task.CompletedTask;
            }

            dispatcher._logger.LogWarning(
                "Requeued {MessageType} (correlation {CorrelationId}, message {MessageId}) onto the local dispatch channel, requeue {RequeueCount} of {MaxRequeueAttempts}",
                _delivery.MessageTypeName, _delivery.CorrelationId, _delivery.MessageId, requeueCount + 1, MaxRequeueAttempts);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// §4.4's ack model for a delivery that belongs to an in-flight inbound HTTP request: ack drops,
    /// and both nack forms log at error and drop. See <see cref="CreateInboundRequestAck"/> for why
    /// requeue is not supported here rather than faked with a local re-enqueue. Idempotent on exactly
    /// the same terms as <see cref="ChannelRequeueAckContext"/>, so a double nack logs once.
    /// </summary>
    private sealed class InboundRequestAckContext(ILogger logger, string messageTypeName, Guid correlationId, string messageId) : IMessageAckContext
    {
        private int _settled;

        private bool TrySettle() => Interlocked.Exchange(ref _settled, 1) == 0;

        public Task AckAsync(CancellationToken cancellationToken = default)
        {
            TrySettle();
            return Task.CompletedTask;
        }

        public Task NackAsync(bool requeue, CancellationToken cancellationToken = default)
        {
            if (!TrySettle())
                return Task.CompletedTask;

            if (requeue)
            {
                logger.LogError(
                    "Dropping {MessageType} (correlation {CorrelationId}, message {MessageId}) nacked with requeue: true -- it arrived as an inbound HTTP request, whose delivery this process cannot redeliver (the peer that POSTed it owns its retry); treated as requeue: false",
                    messageTypeName, correlationId, messageId);
                return Task.CompletedTask;
            }

            logger.LogError(
                "Dropping {MessageType} (correlation {CorrelationId}, message {MessageId}) nacked with requeue: false -- this transport has no dead-letter queue, so the saga's own state timeout is the safety net",
                messageTypeName, correlationId, messageId);
            return Task.CompletedTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _localDispatchChannel.Writer.TryComplete();
        await _stopping.CancelAsync();

        try
        {
            await _pumpTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _stopping.Dispose();
    }
}

public readonly record struct CapturedReply(string MessageTypeName, ReadOnlyMemory<byte> Body, MessageEnvelope Envelope);

public readonly struct InlineDispatchResult
{
    public static readonly InlineDispatchResult Accepted;

    public CapturedReply? Reply { get; private init; }

    public static InlineDispatchResult WithReply(CapturedReply reply) => new() { Reply = reply };
}

/// <summary>
/// Ambient (AsyncLocal) collector installed by the receive endpoint for the duration of one inline
/// dispatch -- the only seam available to intercept a handler's ordinary PublishAsync call and capture
/// it as that same request's synchronous reply (docs/design/http-based-sagas.md §3.2). Always a fresh instance
/// per request, never shared/static, and sealed once the response has been written so a handler's
/// `_ = Task.Run(...)` fire-and-forget -- which inherits the AsyncLocal via its captured
/// ExecutionContext -- falls through to a real publish attempt afterward instead of writing into a
/// completed collector.
/// </summary>
public sealed class SyncReplyCollector
{
    private readonly Lock _lock = new();
    private bool _sealed;

    public CapturedReply? Captured { get; private set; }

    /// <summary>True if this call captured <paramref name="reply"/> as the reply; false if the collector is sealed or already holds one -- the caller must then throw MessageTransportPublishException instead (a second unroutable message, or a post-response publish).</summary>
    public bool TryCapture(CapturedReply reply)
    {
        lock (_lock)
        {
            if (_sealed || Captured is not null)
                return false;

            Captured = reply;
            return true;
        }
    }

    public void Seal()
    {
        lock (_lock)
            _sealed = true;
    }
}

public static class SyncReplyCollectorAccessor
{
    private static readonly AsyncLocal<SyncReplyCollector?> Ambient = new();

    public static SyncReplyCollector? Current
    {
        get => Ambient.Value;
        set => Ambient.Value = value;
    }
}
