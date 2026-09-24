import {
  CORRELATION_ID_HEADER,
  MESSAGE_ID_HEADER,
  MESSAGE_TYPE_HEADER,
  type MessageEnvelope,
  type MessageTransport,
  MessageTransportPublishError,
  type ReceivedMessage,
  type Subscription,
  TRACE_PARENT_HEADER,
  TRACE_STATE_HEADER,
  type TransportSubscription,
  VSAGA_HEADER_PREFIX,
  assertHeadersSafe,
  buildHeaders,
  isDashedGuid,
  normalizeHeaders,
} from '@vsaga/protocol';

import { HttpInboundDispatcher, currentSyncReplyCollector } from './dispatcher.js';
import {
  type HttpTransportOptions,
  type ResolvedHttpTransportOptions,
  resolveOptions,
} from './options.js';
import { type HttpRouteTable, createConfigRouteTable } from './route-table.js';

/**
 * Node's fetch reports every network failure as a bare `TypeError: fetch failed` and hides the
 * real reason (ECONNREFUSED, DNS failure, TLS error) one or two `cause` levels down -- often
 * inside an AggregateError holding one entry per address the host resolved to. Digging that out
 * here is what turns "was rejected" into a message naming the actual problem.
 */
function describeFetchFailure(error: unknown): string {
  if (error instanceof DOMException && error.name === 'TimeoutError')
    return 'the request timed out';
  if (error instanceof DOMException && error.name === 'AbortError')
    return 'the request was aborted';

  for (let current: unknown = error, depth = 0; current !== undefined && depth < 4; depth++) {
    if (current instanceof AggregateError && current.errors.length > 0) {
      current = current.errors[0];
      continue;
    }
    if (current instanceof Error) {
      const code = (current as NodeJS.ErrnoException).code;
      if (code === 'ECONNREFUSED') return 'connection refused';
      if (code === 'ENOTFOUND' || code === 'EAI_AGAIN') return 'host not found';
      if (code === 'ETIMEDOUT') return 'the connection timed out';
      if (code !== undefined) return code;
      if (current.cause === undefined) return current.message;
      current = current.cause;
      continue;
    }
    break;
  }

  return error instanceof Error ? error.message : String(error);
}

/** One inbound HTTP request to `inboundPath`, in a shape any Node HTTP framework's own request object can be adapted to. */
export interface InboundHttpRequest {
  readonly headers: Readonly<Record<string, string | readonly string[] | undefined>>;
  readonly body: Buffer;
}

/** What a hosting adapter should write back as the HTTP response to an inbound request. */
export interface InboundHttpResponse {
  readonly status: 200 | 202 | 400;
  readonly headers?: Readonly<Record<string, string>>;
  readonly body?: Buffer;
}

/**
 * A vSaga-aware MessageTransport over plain HTTP, plus the framework-agnostic inbound entry point
 * every hosting adapter (`@vsaga/express`, `@vsaga/fastify`, `@vsaga/nestjs`, ...) wires its own
 * routing to -- the TypeScript analogue of `app.MapVSagaHttp()`
 * (dotnet/src/VSaga.Transport.Http/VSagaHttpEndpointExtensions.cs). vSaga ships no auth opinion
 * for this endpoint; adapters/callers apply their own.
 */
export interface HttpTransport extends MessageTransport {
  /** Path this service's own receive endpoint should be mapped to. */
  readonly inboundPath: string;
  /** Handles one inbound POST to `inboundPath`. Framework-agnostic: adapters translate their own request/response shape to and from this. */
  handleInboundRequest(request: InboundHttpRequest): Promise<InboundHttpResponse>;
}

/**
 * vSaga-aware, symmetric MessageTransport over plain HTTP: publish()/send() POST to
 * docs/design/http-based-sagas.md §4.2's wire format, and a 200 response with a full header set + body
 * is itself the reply, fed back into whichever local subscriber the reply's own message type
 * resolves to. No broker underneath -- see dispatcher.ts for how a reply is kept from re-entering
 * a saga while its own publishing step is still running, and how the ambient sync-reply collector
 * tells an unroutable publish from inside a handler apart from a routed one.
 *
 * Wire-compatible with dotnet/src/VSaga.Transport.Http/HttpMessageTransport.cs.
 */
export function createHttpTransport(options: HttpTransportOptions = {}): HttpTransport {
  const resolved = resolveOptions(options);
  return new HttpMessageTransportImpl(
    resolved,
    createConfigRouteTable(resolved),
    new HttpInboundDispatcher(),
  );
}

class HttpMessageTransportImpl implements HttpTransport {
  readonly #options: ResolvedHttpTransportOptions;
  readonly #routeTable: HttpRouteTable;
  readonly #dispatcher: HttpInboundDispatcher;

  constructor(
    options: ResolvedHttpTransportOptions,
    routeTable: HttpRouteTable,
    dispatcher: HttpInboundDispatcher,
  ) {
    this.#options = options;
    this.#routeTable = routeTable;
    this.#dispatcher = dispatcher;
  }

  get inboundPath(): string {
    return this.#options.inboundPath;
  }

  publish(
    messageTypeName: string,
    body: Buffer,
    envelope: MessageEnvelope,
    signal?: AbortSignal,
  ): Promise<void> {
    return this.#publishInternal(messageTypeName, body, envelope, undefined, signal);
  }

  send(
    destination: string,
    messageTypeName: string,
    body: Buffer,
    envelope: MessageEnvelope,
    signal?: AbortSignal,
  ): Promise<void> {
    return this.#publishInternal(messageTypeName, body, envelope, destination, signal);
  }

  subscribe(
    subscription: TransportSubscription,
    handler: (message: ReceivedMessage) => Promise<void>,
  ): Promise<Subscription> {
    return Promise.resolve(this.#dispatcher.subscribe(subscription, handler));
  }

  close(): Promise<void> {
    this.#dispatcher.close();
    return Promise.resolve();
  }

  /** Mirrors VSagaHttpEndpointExtensions.HandleInboundAsync. */
  async handleInboundRequest(request: InboundHttpRequest): Promise<InboundHttpResponse> {
    const headers = normalizeHeaders(request.headers);
    const messageTypeName = headers[MESSAGE_TYPE_HEADER];
    const messageId = headers[MESSAGE_ID_HEADER];
    const correlationId = headers[CORRELATION_ID_HEADER];

    if (!messageTypeName || !messageId || !correlationId || !isDashedGuid(correlationId)) {
      return { status: 400 };
    }

    // The one ack context in this adapter that cannot honour requeue: true
    // (docs/design/http-based-sagas.md §4.4), and deliberately says so at error level instead of
    // pretending. A delivery that arrives as an inbound HTTP request is inseparable from that
    // request -- it is dispatched inline under the ambient sync-reply collector, and the
    // status/body this peer gets back is decided by that dispatch's own outcome. Re-enqueuing a
    // copy onto the local dispatch path (which is what this transport's own enqueueLocalDelivery
    // deliveries do) would not redeliver *this* delivery: the copy would run later with no
    // collector installed and the response already written, so a participant's reply publish --
    // the entire point of an inbound request on this transport -- would find nothing to capture it
    // and throw unroutable instead. The peer that POSTed the message is the only party that can
    // retry it, and it has already been told the outcome. Both nack forms therefore log at error
    // and drop, which is at least diagnosable.
    const received: ReceivedMessage = {
      messageTypeName,
      correlationId,
      messageId,
      body: request.body,
      headers: extractVSagaHeaders(headers),
      ack: this.#dispatcher.createInboundRequestAck(messageTypeName, correlationId, messageId),
    };

    // CancellationToken.None on the .NET side, not the request's own -- deliberately not tying a
    // handler's own outbound calls (e.g. a fan-out reply back out over HTTP) to this inbound
    // connection's lifetime. There is no per-request signal threaded through dispatchInline here
    // for the same reason.
    const result = await this.#dispatcher.dispatchInline(received);

    if (!result.reply) return { status: 202 };

    return {
      status: 200,
      headers: {
        'content-type': 'application/json',
        ...buildHeaders(result.reply.envelope, result.reply.messageTypeName),
      },
      body: result.reply.body,
    };
  }

  /**
   * Resolves targets to the union of configured remote routes and local subscribers
   * (docs/design/http-based-sagas.md §3.3a) -- unroutable only when both are empty, in which case an
   * ambient sync-reply collector (present only while this call is running underneath a genuine
   * inbound HTTP request) gets first refusal at capturing it as that request's synchronous reply
   * (§3.2); only a message with a real destination, or one published outside any inline dispatch,
   * ever becomes a normal send/POST or a throw.
   */
  async #publishInternal(
    messageTypeName: string,
    body: Buffer,
    envelope: MessageEnvelope,
    explicitDestination: string | undefined,
    signal: AbortSignal | undefined,
  ): Promise<void> {
    assertHeadersSafe(envelope.headers);

    const remoteUrls =
      explicitDestination !== undefined
        ? this.#resolveExplicitDestination(explicitDestination)
        : this.#routeTable.resolveRemoteEndpoints(messageTypeName);

    // send()'s explicit destination bypasses routes entirely (§4.3) and therefore the local union
    // too -- a direct address is either configured or it isn't, mirroring RabbitMqTransport's
    // send() targeting a named queue with no exchange/binding lookup involved.
    const hasLocalSubscriber =
      explicitDestination === undefined && this.#dispatcher.hasLocalSubscriber(messageTypeName);

    if (remoteUrls.length === 0 && !hasLocalSubscriber) {
      const collector = currentSyncReplyCollector();
      if (collector?.tryCapture({ messageTypeName, body, envelope })) return;

      throw new MessageTransportPublishError(messageTypeName, envelope.correlationId, true, {
        detail: `no HTTP route or local subscriber is configured for message type '${messageTypeName}'.`,
      });
    }

    if (hasLocalSubscriber) {
      // enqueueLocalDelivery, not a hand-built ReceivedMessage: this delivery is the dispatcher's
      // own, so it gets the ack context that implements §4.4 for real -- nack(requeue: true)
      // re-enqueues onto the very path this call is writing to, carrying these same headers
      // (x-vsaga-delivery-attempt included, which is what keeps the orchestrator's redelivery cap
      // bounding a redelivered copy) and nack(requeue: false) logs at error and drops.
      this.#dispatcher.enqueueLocalDelivery(
        messageTypeName,
        envelope.correlationId,
        envelope.messageId,
        body,
        envelope.headers,
      );
    }

    if (remoteUrls.length === 1) {
      await this.#sendHttpRequest(remoteUrls[0]!, messageTypeName, body, envelope, signal);
    } else if (remoteUrls.length > 1) {
      await Promise.all(
        remoteUrls.map((url) =>
          this.#sendHttpRequest(url, messageTypeName, body, envelope, signal),
        ),
      );
    }
  }

  #resolveExplicitDestination(destination: string): readonly string[] {
    const url = this.#routeTable.resolveEndpointByName(destination);
    return url === undefined ? [] : [url];
  }

  async #sendHttpRequest(
    baseUrl: string,
    messageTypeName: string,
    body: Buffer,
    envelope: MessageEnvelope,
    signal: AbortSignal | undefined,
  ): Promise<void> {
    const timeoutSignal = AbortSignal.timeout(this.#options.requestTimeoutMs);
    const combinedSignal = signal ? AbortSignal.any([signal, timeoutSignal]) : timeoutSignal;

    let response: Response;
    try {
      response = await fetch(this.#buildRequestUrl(baseUrl), {
        method: 'POST',
        headers: { 'content-type': 'application/json', ...buildHeaders(envelope, messageTypeName) },
        body,
        signal: combinedSignal,
      });
    } catch (error) {
      throw new MessageTransportPublishError(messageTypeName, envelope.correlationId, false, {
        cause: error,
        detail: `POST to ${this.#buildRequestUrl(baseUrl)} failed: ${describeFetchFailure(error)}`,
      });
    }

    if (response.status === 202) {
      await response.body?.cancel();
      return;
    }

    if (!response.ok) {
      await response.body?.cancel();
      const detail = `POST to ${this.#buildRequestUrl(baseUrl)} returned ${response.status} ${response.statusText}.`;
      throw new MessageTransportPublishError(messageTypeName, envelope.correlationId, false, {
        cause: new Error(detail),
        detail,
      });
    }

    await this.#handleSyncReply(response, messageTypeName, envelope.correlationId);
  }

  /**
   * A 200 IS the reply (docs/design/http-based-sagas.md §1, §4.2) -- fed back to whatever local
   * subscriber the reply's own type resolves to via enqueueLocalDelivery, never dispatched
   * inline: this call is itself running inside whatever gated dispatch published the original
   * message, so dispatching the reply inline would either deadlock on that same correlation's
   * gate or, worse, re-enter the saga before its own step has persisted (§3.1).
   *
   * The reply is enqueued as a delivery the dispatcher owns, so §4.4's ack model applies to it in
   * full, requeue included. Worth being precise about why, since the reply arrived over HTTP and
   * the inbound-request path deliberately cannot requeue: what makes requeue honest is not where a
   * message came from but whether a redelivery is indistinguishable from the original delivery.
   * This one already *is* a plain deferred dispatch -- no ambient reply collector, no HTTP response
   * riding on its outcome -- so re-enqueuing it reproduces its delivery exactly. An inbound
   * request's does not; see HttpInboundDispatcher.createInboundRequestAck.
   */
  async #handleSyncReply(
    response: Response,
    originalMessageType: string,
    originalCorrelationId: string,
  ): Promise<void> {
    // Note: if an intermediary ever emits the same header name twice, the Fetch API's Headers
    // joins duplicates with ", " (comma-space), while .NET's ExtractVSagaHeaders joins with ","
    // (no space) -- a minor wire-format divergence for that narrow case. Not worth chasing: there
    // is no standard way to recover the original per-occurrence list from a Headers object to
    // rejoin it ourselves, and every header this transport itself ever sends is single-valued.
    const headers = normalizeHeaders(Object.fromEntries(response.headers.entries()));
    const replyTypeName = headers[MESSAGE_TYPE_HEADER];
    const replyMessageId = headers[MESSAGE_ID_HEADER];
    const replyCorrelationId = headers[CORRELATION_ID_HEADER];

    if (
      !replyTypeName ||
      !replyMessageId ||
      !replyCorrelationId ||
      !isDashedGuid(replyCorrelationId)
    ) {
      const detail =
        'the HTTP 200 reply is missing one of the required x-vsaga- headers (message-type/correlation-id/message-id).';
      throw new MessageTransportPublishError(originalMessageType, originalCorrelationId, false, {
        cause: new Error(detail),
        detail,
      });
    }

    const replyBody = Buffer.from(await response.arrayBuffer());

    this.#dispatcher.enqueueLocalDelivery(
      replyTypeName,
      replyCorrelationId,
      replyMessageId,
      replyBody,
      extractVSagaHeaders(headers),
    );
  }

  /**
   * Relative-merges `inboundPath` against `baseUrl` the same way `Uri`'s two-argument constructor
   * does on the .NET side (BuildRequestUri) rather than treating it as an absolute-path override,
   * so a `baseUrl` that already carries a sub-path merges identically on both runtimes.
   */
  #buildRequestUrl(baseUrl: string): string {
    const relativePath = this.#options.inboundPath.replace(/^\/+/, '');
    return new URL(relativePath, baseUrl).toString();
  }
}

/**
 * The three reserved headers plus every envelope header that arrived on the wire, filtered to the
 * `x-vsaga-` prefix -- mirrors HttpMessageTransport.ExtractVSagaHeaders /
 * VSagaHttpEndpointExtensions.ExtractVSagaHeaders. Node's http headers are already lower-cased by
 * the parser, which is what satisfies docs/design/http-based-sagas.md §3.3b's case-insensitivity
 * requirement without an explicit OrdinalIgnoreCase lookup -- the `.toLowerCase()` below is only
 * a defensive normalization for a hand-built (e.g. test) headers object.
 *
 * `traceparent`/`tracestate` are allowlisted by exact name alongside the prefix check -- the two
 * bare W3C trace context headers never carry the `x-vsaga-` prefix (interoperability is the whole
 * point), so they would otherwise be silently dropped here on both the inbound-request and
 * sync-reply paths.
 */
function extractVSagaHeaders(headers: Readonly<Record<string, string>>): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [key, value] of Object.entries(headers)) {
    const lowerKey = key.toLowerCase();
    if (
      lowerKey.startsWith(VSAGA_HEADER_PREFIX) ||
      lowerKey === TRACE_PARENT_HEADER ||
      lowerKey === TRACE_STATE_HEADER
    ) {
      result[lowerKey] = value;
    }
  }
  return result;
}
