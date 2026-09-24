using System.Text.Json;
using VSaga.Abstractions.Transport;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.RabbitMQ;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;

namespace VSaga.Transport.Wolverine;

/// <summary>
/// Wolverine-based transport: publishes/sends via Wolverine's raw-send primitive
/// (<c>IDestinationEndpoint.SendRawMessageAsync</c>) over WolverineFx.RabbitMQ, and listens via a Wolverine
/// RabbitMQ queue endpoint started immediately on this node (<c>IEndpointCollection.StartListenerAsync</c>)
/// per <see cref="SubscribeAsync"/> call. Do not "simplify" that to <c>IWolverineRuntime</c>'s
/// <c>RegisterListenerAsync</c>: it is leader-elected, durability-store-backed multi-tenancy machinery that
/// activates a listener on *some* node within a cluster-assignment cycle (default 30s), which left every
/// test hanging past its timeout (docs/transports/wolverine.md). Every message — regardless of its real
/// VSaga message type — is carried as the single marker type <see cref="RawEnvelope"/> as far as
/// Wolverine's own handler-discovery is concerned; see that type's doc comment for why. This deliberately never touches Wolverine's own saga
/// support, transactional inbox/outbox, or message-type-based handler routing for business messages —
/// VSaga.Core's SagaOrchestrator already owns retry, redelivery, compensation, and dedup (see
/// IMessageTransport's doc comment).
/// </summary>
public sealed class WolverineTransport(
    IWolverineRuntime runtime,
    RawDispatchRegistry registry,
    WolverineTransportOptions options,
    ILogger<WolverineTransport> logger) : IMessageTransport
{
    public Task PublishAsync<TMessage>(TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        where TMessage : notnull
    {
        var messageType = message.GetType();
        var body = JsonSerializer.SerializeToUtf8Bytes(message, messageType);
        return PublishInternalAsync(messageType.Name, body, envelope, RabbitMqEndpointUri.Topic(options.ExchangeName, messageType.Name), cancellationToken);
    }

    public Task SendAsync<TMessage>(string destination, TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        where TMessage : notnull
    {
        var messageType = message.GetType();
        var body = JsonSerializer.SerializeToUtf8Bytes(message, messageType);
        return PublishInternalAsync(messageType.Name, body, envelope, RabbitMqEndpointUri.Queue(destination), cancellationToken);
    }

    public Task PublishRawAsync(string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken cancellationToken = default) =>
        PublishInternalAsync(messageTypeName, body.ToArray(), envelope, RabbitMqEndpointUri.Topic(options.ExchangeName, messageTypeName), cancellationToken);

    public Task SendRawAsync(string destination, string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken cancellationToken = default) =>
        PublishInternalAsync(messageTypeName, body.ToArray(), envelope, RabbitMqEndpointUri.Queue(destination), cancellationToken);

    private async Task PublishInternalAsync(string messageTypeName, byte[] body, MessageEnvelope envelope, Uri destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var headers = envelope.Headers is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(envelope.Headers, StringComparer.Ordinal);

        // Everything VSaga needs travels inside this self-contained wire payload rather than relying on
        // Wolverine's own Envelope.Headers surviving its trip through the RabbitMQ envelope mapper — see
        // WireEnvelope's doc comment.
        var wire = new WireEnvelope(messageTypeName, envelope.CorrelationId, envelope.MessageId, headers, body);
        var wireBytes = JsonSerializer.SerializeToUtf8Bytes(wire);

        var bus = new MessageBus(runtime);

        try
        {
            await bus.EndpointFor(destination).SendRawMessageAsync(wireBytes, configure: env =>
            {
                env.SetMessageType<RawEnvelope>();
                env.Id = Guid.TryParse(envelope.MessageId, out var id) ? id : Guid.NewGuid();
                env.CorrelationId = envelope.CorrelationId.ToString();
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Publish of {MessageType} for correlation id {CorrelationId} to {Destination} failed",
                messageTypeName, envelope.CorrelationId, destination);
            throw new MessageTransportPublishException(messageTypeName, envelope.CorrelationId, isUnroutable: false, ex);
        }
    }

    public async Task<IDisposable> SubscribeAsync(TransportSubscription subscription, Func<ReceivedMessage, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
    {
        var queueUri = RabbitMqEndpointUri.Queue(subscription.QueueNameHint);

        // JIT topology declaration, mirroring RabbitMqTransport.DeclareSubscriptionTopologyAsync: one
        // durable queue per consumer, bound to the shared topic exchange for each of its declared message
        // types — via Wolverine's own runtime object-management API rather than a raw RabbitMQ.Client
        // channel, since this transport otherwise never touches RabbitMQ.Client directly.
        RabbitMqQueue? queue = null;
        await runtime.ModifyRabbitMqObjects(o =>
        {
            // Declaring the exchange here too (not just in ServiceCollectionExtensions' opts.UseRabbitMq
            // config) is deliberate: that config-time declaration only affects Wolverine's own lazily
            // auto-provisioned *sending* endpoints, but RabbitMqQueue.BindExchange below requires the
            // exchange to already exist as a distinct object at the moment this runs - AMQP's
            // exchange.declare is idempotent, so redeclaring it identically on every SubscribeAsync call
            // is safe.
            var exchange = o.DeclareExchange(options.ExchangeName);
            exchange.ExchangeType = ExchangeType.Topic;

            queue = o.DeclareQueue(subscription.QueueNameHint);
            foreach (var messageType in subscription.MessageTypes)
                queue.BindExchange(options.ExchangeName, messageType.Name);
        });

        // RabbitMqQueue IS a Wolverine Endpoint (RabbitMqQueue -> RabbitMqEndpoint -> Endpoint), and the
        // instance ModifyRabbitMqObjects just registered into the transport's own endpoint cache is the
        // same one IEndpointCollection.EndpointFor(queueUri) would resolve later - using it directly here
        // avoids a redundant, possibly-null lookup immediately after declaring it.
        registry.Register(queueUri, handler);
        await runtime.Endpoints.StartListenerAsync(queue!, cancellationToken);

        return new Subscription(runtime, registry, queueUri, logger);
    }

    private sealed class Subscription(IWolverineRuntime runtime, RawDispatchRegistry registry, Uri queueUri, ILogger logger) : IDisposable
    {
        public void Dispose()
        {
            registry.Unregister(queueUri);

            // IDisposable.Dispose can't be async, and stopping the listener's own failure here (e.g. the
            // broker connection is already gone during shutdown) shouldn't throw out of Dispose - so this
            // is a deliberate, logged, best-effort fire-and-forget, not an oversight - mirroring
            // SagaOrchestrator.RecordDeliveryExhaustedAsync's own "log and swallow" precedent.
            _ = StopListenerBestEffortAsync();
        }

        private async Task StopListenerBestEffortAsync()
        {
            try
            {
                var endpoint = runtime.Endpoints.EndpointFor(queueUri);
                if (endpoint is not null)
                    await runtime.Endpoints.StopListenerAsync(endpoint, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to stop Wolverine listener for {QueueUri} during subscription teardown", queueUri);
            }
        }
    }
}
