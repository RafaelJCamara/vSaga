# @vsaga/protocol

vSaga's wire contract for Node participants: message declarations, the envelope shape, header
names, the routing-key convention, and the PascalCase body codec. It pulls in no third-party
runtime code — its single `dependencies` entry is `@types/node`, which ships type declarations
only (the `postbuild` step references them from the emitted `.d.ts`, so consumers resolve them
without declaring it themselves). Every other `@vsaga/*` package builds on this one.

Wire-compatible with `dotnet/src/VSaga.Abstractions` — a message published from a .NET saga and
consumed here (or vice versa) round-trips without translation.

## Install

```sh
npm install @vsaga/protocol
```

## Declaring a message type

A vSaga message type is one string: the C# short type name. It doubles as both the
`x-vsaga-message-type` header and (via `toRoutingKey`) the broker routing key, so the two can
never drift apart.

```ts
import { message } from '@vsaga/protocol';

interface OrderShippedBody {
  CorrelationId: string;
  OrderId: string;
  TrackingNumber: string;
}

export const OrderShipped = message<OrderShippedBody>('OrderShipped');
```

The name must be the exact CLR type name (`OrderShipped`), not a routing key (`order-shipped`) or
camelCase — the .NET orchestrator resolves inbound messages by looking `x-vsaga-message-type` up
in a dictionary keyed by `Type.Name`.

## What's in here

- **`message`** — declares a `MessageType<TBody>`, as above.
- **`envelopeFrom` / `newEnvelope`** — build a `MessageEnvelope` (correlation id, fresh message id,
  headers), including the causation-id wiring a reply needs.
- **`encodeBody` / `decodeBody`** — the PascalCase JSON codec every broker payload uses.
- **`buildHeaders`, header name constants, `assertHeadersSafe`** — the header set every transport
  applies, plus CR/LF injection guards.
- **`toRoutingKey`** — derives a broker routing key from a message type name.
- **`MessageTransport`, `ReceivedMessage`, `Subscription`, `TransportSubscription`,
  `MessageTransportPublishError`** — the transport contract every `@vsaga/transport-*` package
  implements.
- **`TopologyRegistration`, `TopologyReporter`** — the shape used to register a service's
  subscriptions with the Saga Map.

## Publishing without a participant

`@vsaga/participant` covers the receive side and gives handlers `ctx.reply`/`ctx.publish`, so most
code never touches a transport's own `publish`. Starting a saga from Node is the exception — there
is no inbound message to reply to — and that call takes an encoded body and an envelope rather than
a plain object:

```ts
import { encodeBody, newEnvelope } from '@vsaga/protocol';
import { createRabbitMqTransport } from '@vsaga/transport-rabbitmq';

const transport = await createRabbitMqTransport({ connectionString: 'amqp://localhost' });

// The correlation id must be a dashed Guid -- it is the id the .NET engine keys the saga instance
// on, and newEnvelope rejects any other shape rather than letting an unroutable id reach the wire.
const correlationId = crypto.randomUUID();

await transport.publish(
  'OrderSubmitted',
  encodeBody({ OrderId: 'ORD-1', CustomerId: 'CUST-1', Amount: 42.5 }),
  newEnvelope(correlationId),
);
```

`newEnvelope` mints the fresh message id; use `envelopeFrom` instead when the publish is causally
linked to a message you received, which is what `ctx.reply` does for you.

This package is otherwise rarely used directly — install a transport (`@vsaga/transport-http`,
`@vsaga/transport-rabbitmq`) and `@vsaga/participant`; both re-export what you need from here.

## License

MIT
