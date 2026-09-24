import { AsyncLocalStorage } from 'node:async_hooks';
import type {
  MessageAckContext,
  MessageEnvelope,
  ReceivedMessage,
  Subscription,
  TransportSubscription,
} from '@vsaga/protocol';

/**
 * How many times one delivery may be handed back by `nack(requeue: true)` before this dispatcher
 * stops honouring the requeue and drops it with an error log instead. Mirrors
 * `HttpInboundDispatcher.MaxRequeueAttempts` (dotnet/src/VSaga.Transport.Http/HttpInboundDispatcher.cs).
 *
 * This is the bound on the one genuinely unbounded shape docs/design/http-based-sagas.md §4.4's ack
 * model admits. The engine's own redelivery loop is bounded elsewhere and differently: it
 * republishes with an incremented `x-vsaga-delivery-attempt` header and dead-letters at
 * `MaxDeliveryAttempts`, and never calls `nack(requeue: true)` at all. A requeue therefore
 * increments nothing the orchestrator reads, so a handler that unconditionally requeues would spin
 * this dispatcher forever on a counter nobody owns. Hence a counter this dispatcher owns, carried
 * on the ack context of each successive delivery rather than in the headers -- which keeps a
 * redelivery byte-identical to its original, `x-vsaga-delivery-attempt` included.
 *
 * The two bounds compose rather than cancel: a requeue chain terminates here after at most this
 * many hops, and a republish chain terminates at `MaxDeliveryAttempts` because every republish
 * increments the header this dispatcher preserves. Interleaving them is bounded by their product,
 * which is finite.
 */
export const MAX_REQUEUE_ATTEMPTS = 5;

/** Just enough of a delivery to name it in a log line: message type, correlation id, message id. */
interface DeliveryIdentity {
  readonly messageTypeName: string;
  readonly correlationId: string;
  readonly messageId: string;
}

function describe(delivery: DeliveryIdentity): string {
  return `${delivery.messageTypeName} (correlation ${delivery.correlationId}, message ${delivery.messageId})`;
}

/**
 * docs/design/http-based-sagas.md §4.4's ack model for a delivery this dispatcher owns (see
 * {@link HttpInboundDispatcher.enqueueLocalDelivery}): ack drops, `nack(requeue: true)` re-enqueues
 * onto the same in-process dispatch path, `nack(requeue: false)` logs at error and drops. Mirrors
 * `ChannelRequeueAckContext` (dotnet/src/VSaga.Transport.Http/HttpInboundDispatcher.cs).
 *
 * Settling is idempotent, so a handler that acks and then nacks in a `finally`, or nacks twice down
 * two unwinding paths, can never enqueue the same message twice -- first settle wins, every later
 * call is a no-op. Node needs no interlocked exchange for that: a settle runs to completion before
 * any other continuation can observe the flag, since nothing between the read and the write awaits.
 */
class LocalDeliveryAckContext implements MessageAckContext {
  readonly #dispatcher: HttpInboundDispatcher;
  readonly #requeueCount: number;
  #delivery: ReceivedMessage | undefined;
  #settled = false;

  constructor(dispatcher: HttpInboundDispatcher, requeueCount: number) {
    this.#dispatcher = dispatcher;
    this.#requeueCount = requeueCount;
  }

  /** Completes the cycle between a delivery and its ack context; called once, before the delivery is enqueued, so nothing can observe an unattached context. */
  attach(delivery: ReceivedMessage): void {
    this.#delivery = delivery;
  }

  #trySettle(): boolean {
    if (this.#settled) return false;
    this.#settled = true;
    return true;
  }

  ack(): Promise<void> {
    this.#trySettle();
    return Promise.resolve();
  }

  nack(requeue: boolean): Promise<void> {
    if (!this.#trySettle()) return Promise.resolve();

    const delivery = this.#delivery!;

    if (!requeue) {
      // No broker and no dead-letter queue by design (§4.4: an IHttpDeadLetterSink with one logging
      // implementation is ceremony), so an error log with enough identity to find the message in the
      // sender's own logs IS the dead-letter record.
      console.error(
        `[vsaga] HTTP transport dropping ${describe(delivery)} nacked with requeue: false -- ` +
          `this transport has no dead-letter queue, so the saga's own state timeout is the safety net`,
      );
      return Promise.resolve();
    }

    if (this.#requeueCount >= MAX_REQUEUE_ATTEMPTS) {
      console.error(
        `[vsaga] HTTP transport dropping ${describe(delivery)} nacked with requeue: true after ` +
          `${this.#requeueCount} requeues -- the ${MAX_REQUEUE_ATTEMPTS}-requeue cap is what stops a ` +
          `handler that always requeues from spinning the local dispatch path forever`,
      );
      return Promise.resolve();
    }

    if (!this.#dispatcher.tryRequeue(delivery, this.#requeueCount + 1)) {
      console.error(
        `[vsaga] HTTP transport dropping ${describe(delivery)} nacked with requeue: true -- the ` +
          `transport is already closed (shutting down), so there is nowhere to redeliver it to`,
      );
      return Promise.resolve();
    }

    console.warn(
      `[vsaga] HTTP transport requeued ${describe(delivery)} for local dispatch, requeue ` +
        `${this.#requeueCount + 1} of ${MAX_REQUEUE_ATTEMPTS}`,
    );
    return Promise.resolve();
  }
}

/**
 * docs/design/http-based-sagas.md §4.4's ack model for a delivery that belongs to an in-flight
 * inbound HTTP request: ack drops, and *both* nack forms log at error and drop. Mirrors
 * `InboundRequestAckContext` (dotnet/src/VSaga.Transport.Http/HttpInboundDispatcher.cs), idempotent
 * on exactly the same terms as {@link LocalDeliveryAckContext}, so a double nack logs once.
 *
 * See {@link HttpInboundDispatcher.createInboundRequestAck} for why `requeue: true` is not
 * supported here rather than faked with a local re-enqueue.
 */
class InboundRequestAckContext implements MessageAckContext {
  readonly #delivery: DeliveryIdentity;
  #settled = false;

  constructor(delivery: DeliveryIdentity) {
    this.#delivery = delivery;
  }

  #trySettle(): boolean {
    if (this.#settled) return false;
    this.#settled = true;
    return true;
  }

  ack(): Promise<void> {
    this.#trySettle();
    return Promise.resolve();
  }

  nack(requeue: boolean): Promise<void> {
    if (!this.#trySettle()) return Promise.resolve();

    if (requeue) {
      console.error(
        `[vsaga] HTTP transport dropping ${describe(this.#delivery)} nacked with requeue: true -- ` +
          `it arrived as an inbound HTTP request, whose delivery this process cannot redeliver (the ` +
          `peer that POSTed it owns its retry); treated as requeue: false`,
      );
      return Promise.resolve();
    }

    console.error(
      `[vsaga] HTTP transport dropping ${describe(this.#delivery)} nacked with requeue: false -- ` +
        `this transport has no dead-letter queue, so the saga's own state timeout is the safety net`,
    );
    return Promise.resolve();
  }
}

export interface CapturedReply {
  readonly messageTypeName: string;
  readonly body: Buffer;
  readonly envelope: MessageEnvelope;
}

export interface InlineDispatchResult {
  readonly reply?: CapturedReply;
}

const ACCEPTED: InlineDispatchResult = {};

/**
 * Ambient collector installed by dispatchInline() for the duration of one inline dispatch -- the
 * only seam available to intercept a handler's ordinary publish() call and capture it as that
 * same request's synchronous reply (docs/design/http-based-sagas.md §3.2). Always a fresh instance per
 * request, never shared, and sealed once every subscriber handler's own *awaited* chain has
 * settled, so a publish a handler genuinely awaits (however many ticks it takes) is always seen
 * before sealing.
 *
 * Unlike .NET, this is NOT a hard guarantee against a handler's *detached*, never-awaited publish
 * (`void somePromise.then(() => ctx.publish(...))`): .NET's own equivalent case only reliably
 * falls through to a real publish attempt because `Task.Run` always incurs real thread-pool
 * dispatch latency, not because of an enforced ordering -- nothing stops a sufficiently fast
 * `Task.Run` continuation from winning there either. Node has no such latency for a continuation
 * that's already-queued as a microtask (e.g. `.then()` on an already-resolved promise): it can run
 * -- and empirically does -- before this collector is sealed. Treat "detached publish from inside
 * a handler" as unspecified ordering on both runtimes, not a supported pattern; a handler that
 * wants a publish to definitely NOT be captured as the sync reply must `await` it.
 */
class SyncReplyCollector {
  #sealed = false;
  #captured: CapturedReply | undefined;

  get captured(): CapturedReply | undefined {
    return this.#captured;
  }

  /** True if this call captured `reply` as the reply; false if sealed or already holding one -- the caller must then fall through to a normal (possibly unroutable) publish. */
  tryCapture(reply: CapturedReply): boolean {
    if (this.#sealed || this.#captured !== undefined) return false;
    this.#captured = reply;
    return true;
  }

  seal(): void {
    this.#sealed = true;
  }
}

const syncReplyCollectorStorage = new AsyncLocalStorage<SyncReplyCollector | undefined>();

/** The ambient collector for the in-flight inline dispatch on this async context, if any. */
export function currentSyncReplyCollector(): Pick<SyncReplyCollector, 'tryCapture'> | undefined {
  return syncReplyCollectorStorage.getStore();
}

interface SubscriberEntry {
  readonly subscription: TransportSubscription;
  readonly handler: (message: ReceivedMessage) => Promise<void>;
}

/**
 * Bound on acquiring the correlation gate for a genuine inbound request before giving up and
 * deferring to a background dispatch instead of continuing to block the HTTP connection. Found
 * live (docs/design/http-based-sagas.md §4.4a): a fan-out reply that routes back to its own originating
 * service can deadlock that service's own gate against itself -- the saga's dispatch holds the
 * gate while awaiting a step's HTTP response, and the participant handling that step can't finish
 * answering until its own nested reply back to the saga host is accepted, which needs the very
 * gate the saga is still holding. Deferring after a short bound breaks the cycle losslessly (202
 * now, dispatched once the gate frees) instead of blocking for the full request timeout.
 */
const INLINE_GATE_ACQUIRE_TIMEOUT_MS = 5000;

/**
 * A per-correlation-id mutex. Node has no threads, so unlike .NET's SemaphoreSlim this exists
 * purely to serialize *interleaving* across await points, not true concurrency -- but the
 * correctness property (two dispatches for the same correlation id never run overlapping) is the
 * same one docs/design/http-based-sagas.md §3.1 needs.
 */
class AsyncGate {
  #locked = false;
  #waiters: Array<() => void> = [];

  /** Resolves true once acquired, or false if `timeoutMs` elapses first without acquiring. */
  async acquire(timeoutMs?: number): Promise<boolean> {
    if (!this.#locked) {
      this.#locked = true;
      return true;
    }

    return new Promise<boolean>((resolve) => {
      let settled = false;
      let timer: ReturnType<typeof setTimeout> | undefined;

      const onAcquired = (): void => {
        if (settled) return;
        settled = true;
        if (timer !== undefined) clearTimeout(timer);
        resolve(true);
      };

      if (timeoutMs !== undefined) {
        timer = setTimeout(() => {
          if (settled) return;
          settled = true;
          const index = this.#waiters.indexOf(onAcquired);
          if (index >= 0) this.#waiters.splice(index, 1);
          resolve(false);
        }, timeoutMs);
      }

      this.#waiters.push(onAcquired);
    });
  }

  /** Hands the lock straight to the next waiter, if any, or marks the gate free. */
  release(): void {
    const next = this.#waiters.shift();
    if (next) {
      next();
      return;
    }
    this.#locked = false;
  }

  /** True once nobody holds or is waiting on this gate -- an idle gate has no waiter to strand if dropped. */
  get isIdle(): boolean {
    return !this.#locked && this.#waiters.length === 0;
  }
}

/**
 * Owns the local subscriber registry (populated by subscribe()) and the per-correlation-id
 * dispatch gate that is this adapter's whole answer to docs/design/http-based-sagas.md §3.1: a reply
 * must never re-enter a saga while its own step is still running.
 *
 * Exactly two entry points ever reach a local subscriber, and the asymmetry between them is the
 * entire §3.1 answer:
 *   - dispatchInline() -- a genuine inbound HTTP request. Dispatched immediately, holding the
 *     gate, because the handler's reply has to be captured before the response is written --
 *     unless the gate can't be acquired within INLINE_GATE_ACQUIRE_TIMEOUT_MS, in which case it
 *     falls back to enqueueLocalDispatch() instead of blocking the connection for the full
 *     request timeout.
 *   - enqueueLocalDispatch() -- everything else that resolves to a local subscriber: a
 *     same-process publish/send (including redelivery of an inbound type, which runs from
 *     *inside* an already-gated dispatch) and a 200 reply to our own outbound POST. Never
 *     dispatched inline -- always scheduled as its own task that acquires the same gate, which is
 *     what lets redelivery enqueue itself without deadlocking on the gate its own caller may
 *     already hold, and what makes a reply wait for the publishing step to finish before it can be
 *     dispatched.
 *
 * Unlike the .NET dispatcher there is no explicit Channel + pump loop: enqueueLocalDispatch kicks
 * off its own async task directly. That task runs synchronously up to its first await (acquiring
 * the gate), so calls made in enqueue order register on the gate's waiter queue in that same
 * order -- Node's run-to-first-await semantics give the same FIFO-per-correlation ordering the
 * .NET Channel provides, without needing a queue data structure to get it.
 *
 * It also owns §4.4's ack model, which has no broker underneath it and therefore exactly one place
 * a redelivery can go -- the deferred dispatch path above. {@link enqueueLocalDelivery} hands out a
 * delivery whose `nack(requeue: true)` genuinely re-enqueues (a redelivery is byte-identical to the
 * original delivery, headers and all); {@link createInboundRequestAck} hands out one that can only
 * log at error and drop, because an inbound request's delivery is inseparable from the HTTP request
 * carrying it. See both members for the reasoning, and {@link MAX_REQUEUE_ATTEMPTS} for what bounds
 * a requeue loop.
 */
export class HttpInboundDispatcher {
  readonly #subscribers = new Map<string, SubscriberEntry>();
  readonly #correlationGates = new Map<string, AsyncGate>();
  #nextSubscriberId = 0;
  #closed = false;

  subscribe(
    subscription: TransportSubscription,
    handler: (message: ReceivedMessage) => Promise<void>,
  ): Subscription {
    const id = String(this.#nextSubscriberId++);
    this.#subscribers.set(id, { subscription, handler });
    return {
      close: () => {
        this.#subscribers.delete(id);
        return Promise.resolve();
      },
    };
  }

  /** Whether any locally-registered subscription declares this message type -- the local half of the routing union (docs/design/http-based-sagas.md §3.3a). */
  hasLocalSubscriber(messageTypeName: string): boolean {
    for (const entry of this.#subscribers.values()) {
      if (entry.subscription.messageTypeNames.includes(messageTypeName)) return true;
    }
    return false;
  }

  /**
   * Queues a message for local dispatch without blocking on the correlation gate -- see the class
   * doc for why this, never inline, is the only path other than a genuine inbound request.
   *
   * Explicitly detached from whatever ambient sync-reply collector is active at the call site by
   * running under an `undefined` AsyncLocalStorage store. This call can happen from *inside* an
   * active dispatchInline() (a same-process publish from a handler, or redelivery running from
   * that handler's own catch block), and without this the newly-spawned task would inherit that
   * unrelated request's collector via ordinary AsyncLocalStorage propagation -- letting this
   * dispatch's own eventual unroutable publish get hijacked as if it were THAT other, unrelated
   * request's synchronous reply. .NET can't have this problem structurally: HttpInboundDispatcher's
   * pump is one persistent Task started once at construction, long before any request-scoped
   * AsyncLocal exists, so a pump-driven dispatch always observes a null collector. Running with an
   * explicit `undefined` store here reproduces that same guarantee.
   */
  enqueueLocalDispatch(received: ReceivedMessage): void {
    void syncReplyCollectorStorage.run(undefined, () => this.#dispatchAndDrop(received));
  }

  /**
   * Enqueues a message whose delivery this dispatcher itself owns -- a same-process publish/send
   * that resolved to a local subscriber, or a 200 reply to our own outbound POST -- carrying an ack
   * context that implements docs/design/http-based-sagas.md §4.4's model for real: `ack()` drops,
   * `nack(true)` re-enqueues onto this same deferred path, and `nack(false)` logs at error and
   * drops. Mirrors `HttpInboundDispatcher.EnqueueLocalDelivery`.
   *
   * Requeue is honest here precisely because the redelivery lands back where the original delivery
   * came from, with the same headers and the same deferred (never inline) dispatch semantics --
   * nothing about the redelivered copy differs from the first one except that it is later.
   */
  enqueueLocalDelivery(
    messageTypeName: string,
    correlationId: string,
    messageId: string,
    body: Buffer,
    headers: Readonly<Record<string, string>>,
  ): void {
    this.enqueueLocalDispatch(
      this.#buildLocalDelivery(messageTypeName, correlationId, messageId, body, headers, 0),
    );
  }

  /**
   * An ack context for a delivery that arrived as a genuine inbound HTTP request and was dispatched
   * inline by {@link dispatchInline}: `ack()` drops, and *both* nack forms log at error and drop.
   * Mirrors `HttpInboundDispatcher.CreateInboundRequestAck`.
   *
   * `requeue: true` is deliberately not supported on this path, and re-enqueuing onto the local
   * dispatch path would not be an honest implementation of it. That delivery is inseparable from
   * the HTTP request carrying it: it runs under the ambient sync-reply collector, and the status
   * and body the remote peer receives are decided by this very dispatch's outcome. A re-enqueued
   * copy would be dispatched later, detached from that collector and with the response long since
   * written -- so a participant's reply publish (the whole point of an inbound request here) would
   * find nothing to capture it and throw unroutable instead. That is a different, reply-less
   * delivery wearing the original's name, not a redelivery of it. The peer that POSTed the message
   * owns its retry; this process cannot ask for one.
   *
   * Deliberately also used for the copy {@link dispatchInline} defers on a gate-acquire timeout,
   * even though that copy does go down the deferred path: that deferral is a delay of the same
   * delivery, and letting gate contention silently decide whether a message's nack means
   * "redeliver" or "drop" would be worse than one simple rule.
   */
  createInboundRequestAck(
    messageTypeName: string,
    correlationId: string,
    messageId: string,
  ): MessageAckContext {
    return new InboundRequestAckContext({ messageTypeName, correlationId, messageId });
  }

  /**
   * Re-enqueues one delivery under a fresh ack context carrying an incremented requeue count (see
   * {@link MAX_REQUEUE_ATTEMPTS}). Everything else is passed through by reference -- the very same
   * headers object and body buffer, so `x-vsaga-delivery-attempt` and every other engine header
   * reach the redelivered copy exactly as the orchestrator wrote them, and the cap that header
   * feeds keeps bounding redelivery across the requeue. Returns false once the transport has been
   * closed, which is the caller's cue to log and drop rather than throw into a handler that is
   * already unwinding.
   *
   * Public only so {@link LocalDeliveryAckContext} can reach it; neither this class nor that one is
   * re-exported from the package index.
   */
  tryRequeue(delivery: ReceivedMessage, nextRequeueCount: number): boolean {
    if (this.#closed) return false;

    this.enqueueLocalDispatch(
      this.#buildLocalDelivery(
        delivery.messageTypeName,
        delivery.correlationId,
        delivery.messageId,
        delivery.body,
        delivery.headers,
        nextRequeueCount,
      ),
    );
    return true;
  }

  #buildLocalDelivery(
    messageTypeName: string,
    correlationId: string,
    messageId: string,
    body: Buffer,
    headers: Readonly<Record<string, string>>,
    requeueCount: number,
  ): ReceivedMessage {
    const ack = new LocalDeliveryAckContext(this, requeueCount);
    const delivery: ReceivedMessage = {
      messageTypeName,
      correlationId,
      messageId,
      body,
      headers,
      ack,
    };
    ack.attach(delivery);
    return delivery;
  }

  /** The one inline path: a genuine inbound HTTP request, dispatched immediately under an ambient reply collector so a synchronous reply can be captured before the caller's response is written. */
  async dispatchInline(received: ReceivedMessage): Promise<InlineDispatchResult> {
    const gate = this.#gateFor(received.correlationId);
    const acquired = await gate.acquire(INLINE_GATE_ACQUIRE_TIMEOUT_MS);

    if (!acquired) {
      this.enqueueLocalDispatch(received);
      return ACCEPTED;
    }

    const collector = new SyncReplyCollector();
    try {
      await syncReplyCollectorStorage.run(collector, () => this.#runSubscribers(received));
    } finally {
      collector.seal();
      this.#releaseGate(received.correlationId, gate);
    }

    return collector.captured ? { reply: collector.captured } : ACCEPTED;
  }

  async #dispatchAndDrop(received: ReceivedMessage): Promise<void> {
    const gate = this.#gateFor(received.correlationId);
    await gate.acquire();
    try {
      await this.#runSubscribers(received);
    } finally {
      this.#releaseGate(received.correlationId, gate);
    }
  }

  /**
   * Invokes every matching subscriber's handler in turn, each independently caught and dropped so
   * one failing subscriber can't take down a sibling's fan-out delivery of the same message --
   * mirroring RabbitMqTransport's own dispatch-level catch (log + drop rather than propagate,
   * `@vsaga/transport-rabbitmq`'s `#dispatch`). Assumes the caller already holds this
   * correlation's gate.
   *
   * Snapshots the subscriber list up front rather than iterating the live Map: close() clearing
   * `#subscribers` while this loop is paused at an `await` would otherwise silently truncate a
   * still-in-flight multi-subscriber fan-out (Map iteration ends early once the Map it's iterating
   * is cleared), leaving a later subscriber's handler never invoked despite the dispatch appearing
   * to complete normally to its caller.
   */
  async #runSubscribers(received: ReceivedMessage): Promise<void> {
    for (const entry of [...this.#subscribers.values()]) {
      if (!entry.subscription.messageTypeNames.includes(received.messageTypeName)) continue;

      try {
        await entry.handler(received);
      } catch {
        // Swallowed on purpose -- see the method doc.
      }
    }
  }

  #gateFor(correlationId: string): AsyncGate {
    let gate = this.#correlationGates.get(correlationId);
    if (!gate) {
      gate = new AsyncGate();
      this.#correlationGates.set(correlationId, gate);
    }
    return gate;
  }

  /** Best-effort cleanup: only removes the entry if it's uncontended right now, which is safe either way -- an idle gate has no waiter to strand. */
  #releaseGate(correlationId: string, gate: AsyncGate): void {
    gate.release();
    if (gate.isIdle) this.#correlationGates.delete(correlationId);
  }

  /**
   * Drops every locally-registered subscriber. A dispatch already in flight still runs to
   * completion; nothing new is handed to a removed subscriber afterward.
   *
   * Also the point past which {@link tryRequeue} refuses -- the analogue of .NET completing the
   * local dispatch channel on DisposeAsync. A `nack(requeue: true)` from a handler unwinding after
   * this degrades to an error log and a drop rather than scheduling a dispatch that has no
   * subscribers left to reach.
   */
  close(): void {
    this.#closed = true;
    this.#subscribers.clear();
  }
}
