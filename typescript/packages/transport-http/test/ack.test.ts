import { type MockInstance, afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  CORRELATION_ID_HEADER,
  MESSAGE_ID_HEADER,
  MESSAGE_TYPE_HEADER,
  type ReceivedMessage,
  envelopeFrom,
  newCorrelationId,
  newEnvelope,
  newMessageId,
} from '@vsaga/protocol';

import { MAX_REQUEUE_ATTEMPTS } from '../src/dispatcher.js';
import { type TestNode, startTestNode } from './test-node.js';

/** `@vsaga/protocol` exports this one only inside `ENGINE_OWNED_HEADERS`, so name it here. */
const DELIVERY_ATTEMPT_HEADER_NAME = 'x-vsaga-delivery-attempt';

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/** Resolves once `predicate` holds, polled on the macrotask queue, or rejects after `timeoutMs`. */
async function waitFor(predicate: () => boolean, timeoutMs = 2_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (!predicate()) {
    if (Date.now() > deadline) throw new Error('timed out waiting for condition');
    await delay(5);
  }
}

/**
 * docs/design/http-based-sagas.md §4.4's ack model, ported from
 * dotnet/tests/VSaga.Transport.Http.Tests's coverage of HttpInboundDispatcher's two ack contexts.
 *
 * The asymmetry under test is the whole point: a delivery this transport itself enqueued (a
 * same-process publish to a local subscriber, or a 200 sync reply) can genuinely be redelivered,
 * because re-enqueuing it reproduces it exactly. A delivery that arrived as an inbound HTTP request
 * cannot -- it is dispatched inline under an ambient sync-reply collector and the peer's response
 * is decided by that dispatch's outcome -- so both nack forms log at error and drop there.
 *
 * Every assertion below is on console.error/console.warn because this package has no logger
 * abstraction: transport-level diagnostics go to the console with a `[vsaga]` prefix, the same
 * convention `@vsaga/transport-rabbitmq` uses for its channel-error warnings.
 */
describe('ack model', () => {
  const nodes: TestNode[] = [];
  let errors: MockInstance<typeof console.error>;
  let warnings: MockInstance<typeof console.warn>;

  beforeEach(() => {
    errors = vi.spyOn(console, 'error').mockImplementation(() => {});
    warnings = vi.spyOn(console, 'warn').mockImplementation(() => {});
  });

  afterEach(async () => {
    vi.restoreAllMocks();
    await Promise.all(nodes.splice(0).map((n) => n.close()));
  });

  async function node(): Promise<TestNode> {
    const testNode = await startTestNode();
    nodes.push(testNode);
    return testNode;
  }

  /** Every console.error line this test has seen, joined -- enough to assert on identity fragments. */
  function errorText(): string {
    return errors.mock.calls.map((call) => call.join(' ')).join('\n');
  }

  function warningText(): string {
    return warnings.mock.calls.map((call) => call.join(' ')).join('\n');
  }

  describe('a delivery this transport enqueued itself', () => {
    it('redelivers a nack(requeue: true) byte-identically -- same message id, same headers', async () => {
      const solo = (await node()).bind();

      const deliveries: ReceivedMessage[] = [];
      await solo.subscribe(
        {
          consumerName: 'RequeueConsumer',
          messageTypeNames: ['RequeueMe'],
          queueNameHint: 'requeue-queue',
        },
        async (message) => {
          deliveries.push(message);
          if (deliveries.length === 1) await message.ack.nack(true);
          else await message.ack.ack();
        },
      );

      const correlationId = newCorrelationId();
      const messageId = newMessageId();
      await solo.publish('RequeueMe', Buffer.from(JSON.stringify({ text: 'retry-me' })), {
        correlationId,
        messageId,
        // The orchestrator's own redelivery bound rides in this header. A requeue must not touch
        // it: the dispatcher's cap is carried on the ack context precisely so a redelivery stays
        // byte-identical and the two bounds compose instead of colliding.
        headers: { [DELIVERY_ATTEMPT_HEADER_NAME]: '3' },
      });

      await waitFor(() => deliveries.length === 2);
      await delay(50);

      expect(deliveries).toHaveLength(2);
      for (const delivery of deliveries) {
        expect(delivery.messageTypeName).toBe('RequeueMe');
        expect(delivery.correlationId).toBe(correlationId);
        expect(delivery.messageId).toBe(messageId);
        expect(delivery.headers[DELIVERY_ATTEMPT_HEADER_NAME]).toBe('3');
        expect(delivery.body.toString('utf8')).toBe(JSON.stringify({ text: 'retry-me' }));
      }

      expect(warningText()).toContain(`requeue 1 of ${MAX_REQUEUE_ATTEMPTS}`);
    });

    it(`stops honouring requeue after ${MAX_REQUEUE_ATTEMPTS} hops and drops with an error log`, async () => {
      const solo = (await node()).bind();

      let deliveries = 0;
      await solo.subscribe(
        {
          consumerName: 'AlwaysRequeues',
          messageTypeNames: ['SpinMe'],
          queueNameHint: 'spin-queue',
        },
        async (message) => {
          deliveries++;
          await message.ack.nack(true);
        },
      );

      const correlationId = newCorrelationId();
      await solo.publish('SpinMe', Buffer.from('{}'), newEnvelope(correlationId));

      // The original delivery plus MAX_REQUEUE_ATTEMPTS redeliveries, and then it must stop: an
      // unbounded requeue here would spin the local dispatch path forever on a counter the
      // orchestrator's own x-vsaga-delivery-attempt bound never sees.
      await waitFor(() => deliveries === MAX_REQUEUE_ATTEMPTS + 1);
      await delay(100);
      expect(deliveries).toBe(MAX_REQUEUE_ATTEMPTS + 1);

      expect(errorText()).toContain(`after ${MAX_REQUEUE_ATTEMPTS} requeues`);
      expect(errorText()).toContain(correlationId);
    });

    it('logs nack(requeue: false) at error with the message type, correlation id and message id', async () => {
      const solo = (await node()).bind();

      const handled = { done: false };
      await solo.subscribe(
        {
          consumerName: 'DeadLetterConsumer',
          messageTypeNames: ['DropMe'],
          queueNameHint: 'drop-queue',
        },
        async (message) => {
          await message.ack.nack(false);
          handled.done = true;
        },
      );

      const correlationId = newCorrelationId();
      const messageId = newMessageId();
      await solo.publish('DropMe', Buffer.from('{}'), { correlationId, messageId, headers: {} });

      await waitFor(() => handled.done);
      await delay(50);

      const text = errorText();
      expect(text).toContain('DropMe');
      expect(text).toContain(correlationId);
      expect(text).toContain(messageId);
      expect(text).toContain('requeue: false');
      expect(text).toContain('no dead-letter queue');
    });

    it('settles once -- an ack followed by a nack(requeue: true) redelivers nothing', async () => {
      const solo = (await node()).bind();

      let deliveries = 0;
      await solo.subscribe(
        {
          consumerName: 'AcksThenNacks',
          messageTypeNames: ['SettleOnce'],
          queueNameHint: 'settle-queue',
        },
        async (message) => {
          deliveries++;
          await message.ack.ack();
          // The shape a `finally` produces when a handler acks on the happy path and nacks on the
          // way out of an error it caught itself: first settle wins, this one is a no-op.
          await message.ack.nack(true);
          await message.ack.nack(false);
        },
      );

      await solo.publish('SettleOnce', Buffer.from('{}'), newEnvelope(newCorrelationId()));

      await waitFor(() => deliveries === 1);
      await delay(100);

      expect(deliveries).toBe(1);
      expect(errors).not.toHaveBeenCalled();
      expect(warnings).not.toHaveBeenCalled();
    });

    it('degrades to log-and-drop when the transport is already closed', async () => {
      const solo = (await node()).bind();

      const handled = { done: false };
      await solo.subscribe(
        {
          consumerName: 'ClosesMidFlight',
          messageTypeNames: ['LateNack'],
          queueNameHint: 'late-nack-queue',
        },
        async (message) => {
          // A handler unwinding through shutdown: there is nowhere left to redeliver to, and
          // scheduling a dispatch with no subscribers would be a silent loss rather than a
          // diagnosable one.
          await solo.close();
          await message.ack.nack(true);
          handled.done = true;
        },
      );

      await solo.publish('LateNack', Buffer.from('{}'), newEnvelope(newCorrelationId()));

      await waitFor(() => handled.done);
      await delay(50);

      expect(errorText()).toContain('already closed');
    });

    it('applies the same ack model to a 200 synchronous reply', async () => {
      const receiverNode = await node();
      const senderNode = await node();

      const receiver = receiverNode.bind();
      const sender = senderNode.bind({
        endpoints: { receiver: receiverNode.baseUrl },
        routes: { Command: ['receiver'] },
      });

      await receiver.subscribe(
        {
          consumerName: 'Receiver',
          messageTypeNames: ['Command'],
          queueNameHint: 'receiver-command-queue',
        },
        async (message) => {
          await receiver.publish(
            'Reply',
            Buffer.from(JSON.stringify({ text: 'ok' })),
            envelopeFrom('Receiver', message.correlationId, message.messageId),
          );
        },
      );

      // The reply comes back over HTTP, but it is enqueued as a plain deferred dispatch -- no
      // ambient collector, no response riding on its outcome -- so re-enqueuing it reproduces its
      // delivery exactly, and requeue is honest here in a way it is not for an inbound request.
      const replies: ReceivedMessage[] = [];
      await sender.subscribe(
        {
          consumerName: 'ReplyListener',
          messageTypeNames: ['Reply'],
          queueNameHint: 'sender-reply-queue',
        },
        async (message) => {
          replies.push(message);
          if (replies.length === 1) await message.ack.nack(true);
          else await message.ack.ack();
        },
      );

      const correlationId = newCorrelationId();
      await sender.publish('Command', Buffer.from('{}'), newEnvelope(correlationId));

      await waitFor(() => replies.length === 2);
      await delay(50);

      expect(replies).toHaveLength(2);
      expect(replies[1]!.messageId).toBe(replies[0]!.messageId);
      expect(replies[1]!.correlationId).toBe(correlationId);
    });
  });

  describe('a delivery that arrived as an inbound HTTP request', () => {
    /** POSTs one message straight at `node`'s own receive endpoint -- the genuine inline path. */
    async function post(
      testNode: TestNode,
      inboundPath: string,
      messageTypeName: string,
      correlationId: string,
      messageId: string,
    ): Promise<number> {
      const response = await fetch(`${testNode.baseUrl}${inboundPath}`, {
        method: 'POST',
        headers: {
          'content-type': 'application/json',
          [MESSAGE_TYPE_HEADER]: messageTypeName,
          [MESSAGE_ID_HEADER]: messageId,
          [CORRELATION_ID_HEADER]: correlationId,
        },
        body: Buffer.from('{}'),
      });
      await response.arrayBuffer();
      return response.status;
    }

    it('logs nack(requeue: true) at error and drops -- the peer that POSTed it owns the retry', async () => {
      const testNode = await node();
      const transport = testNode.bind();

      let deliveries = 0;
      await transport.subscribe(
        {
          consumerName: 'InboundRequeuer',
          messageTypeNames: ['InboundRequeue'],
          queueNameHint: 'inbound-requeue-queue',
        },
        async (message) => {
          deliveries++;
          await message.ack.nack(true);
        },
      );

      const correlationId = newCorrelationId();
      const messageId = newMessageId();
      const status = await post(
        testNode,
        transport.inboundPath,
        'InboundRequeue',
        correlationId,
        messageId,
      );

      await delay(100);

      expect(status).toBe(202);
      expect(deliveries).toBe(1);

      const text = errorText();
      expect(text).toContain('InboundRequeue');
      expect(text).toContain(correlationId);
      expect(text).toContain(messageId);
      expect(text).toContain('inbound HTTP request');
      expect(text).toContain('treated as requeue: false');
    });

    it('logs nack(requeue: false) at error and drops', async () => {
      const testNode = await node();
      const transport = testNode.bind();

      await transport.subscribe(
        {
          consumerName: 'InboundDropper',
          messageTypeNames: ['InboundDrop'],
          queueNameHint: 'inbound-drop-queue',
        },
        async (message) => {
          await message.ack.nack(false);
          // A second settle down an unwinding path must not log twice.
          await message.ack.nack(false);
        },
      );

      const correlationId = newCorrelationId();
      const messageId = newMessageId();
      await post(testNode, transport.inboundPath, 'InboundDrop', correlationId, messageId);

      await delay(50);

      expect(errors).toHaveBeenCalledTimes(1);
      const text = errorText();
      expect(text).toContain('InboundDrop');
      expect(text).toContain(correlationId);
      expect(text).toContain(messageId);
      expect(text).toContain('no dead-letter queue');
    });

    it('ack() is silent -- the ordinary path a participant takes on every successful handle', async () => {
      const testNode = await node();
      const transport = testNode.bind();

      await transport.subscribe(
        {
          consumerName: 'InboundAcker',
          messageTypeNames: ['InboundAck'],
          queueNameHint: 'inbound-ack-queue',
        },
        async (message) => {
          await message.ack.ack();
        },
      );

      await post(testNode, transport.inboundPath, 'InboundAck', newCorrelationId(), newMessageId());

      await delay(50);
      expect(errors).not.toHaveBeenCalled();
      expect(warnings).not.toHaveBeenCalled();
    });
  });
});
