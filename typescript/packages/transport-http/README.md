# @vsaga/transport-http

A brokerless vSaga `MessageTransport` over plain HTTP — wire-compatible with
`dotnet/src/VSaga.Transport.Http`. No RabbitMQ, no broker infrastructure: `publish`/`send` POST
directly to a configured endpoint, and a 200 response is itself the reply.

## Install

```sh
npm install @vsaga/transport-http
```

You'll also need a hosting adapter to receive inbound requests: `@vsaga/express`,
`@vsaga/fastify`, or `@vsaga/nestjs`.

## Usage

```ts
import { createHttpTransport } from '@vsaga/transport-http';
import { createParticipant } from '@vsaga/participant';

const transport = createHttpTransport({
  serviceName: 'payments',
  endpoints: { orders: 'http://orders:8080' },
  routes: { ChargeCard: ['orders'] },
});

const payments = createParticipant({ serviceName: 'payments', queue: 'payments', transport });
// ... register handlers, payments.start() ...
```

Then mount `transport`'s inbound receive endpoint with one of the hosting adapters, e.g.
`@vsaga/express`:

```ts
import express from 'express';
import { createVSagaRouter } from '@vsaga/express';

const app = express();
app.use(createVSagaRouter(transport));
app.listen(8080);
```

## Options

| Option             | Default             | Meaning                                                                                           |
| ------------------ | ------------------- | ------------------------------------------------------------------------------------------------- |
| `serviceName`      | `'vsaga-http'`      | This process's own identity, for diagnostics only.                                                |
| `endpoints`        | `{}`                | Endpoint name → base URL, e.g. `{ payments: 'http://payments:8080' }`.                            |
| `routes`           | `{}`                | Message type name → endpoint names to POST to on `publish()`. A `"*"` key is a wildcard fallback. |
| `requestTimeoutMs` | `30000`             | Per-request timeout for the outbound HTTP call.                                                   |
| `inboundPath`      | `'/vsaga/messages'` | Path this service's own receive endpoint is mapped to by a hosting adapter.                       |

## Notes

- vSaga ships no auth opinion for the inbound endpoint; apply your own via the hosting framework.
- The inbound handler reads the raw request body — hosting adapters must not let the framework's
  own body parser consume it first (each adapter's README covers the details for that framework).
- No broker underneath: a reply is fed straight back into whichever local subscriber the reply's
  message type resolves to.
- **Ack model.** `ack()` drops the delivery. `nack(requeue: false)` has no dead-letter queue to go
  to, so it logs at error with the message type, correlation id and message id — that log line _is_
  the dead-letter record, and the saga's own state timeout is the safety net.
  `nack(requeue: true)` depends on where the delivery came from:
  - a message this transport enqueued itself — a same-process `publish()`/`send()` that resolved to
    a local subscriber, or a `200` synchronous reply to our own outbound POST — is genuinely
    re-dispatched, byte-identically (same message id, same headers, `x-vsaga-delivery-attempt`
    included), up to 5 requeues per delivery before it is dropped with an error log;
  - a message that arrived as an **inbound HTTP request** cannot be redelivered by this process: it
    is dispatched inline and the peer's response is decided by that dispatch's outcome, so the peer
    that POSTed it owns the retry. Both nack forms log at error and drop.

  Settling is idempotent in both cases: first settle wins, so an `ack()` followed by a `nack()` on
  an unwinding path redelivers nothing. Matches `VSaga.Transport.Http` on .NET exactly.

## License

MIT
