using System.Collections.Concurrent;
using System.Text.Json;
using VSaga.Abstractions.Transport;

namespace VSaga.Transport.InMemory;

/// <summary>
/// In-process dispatch that still round-trips messages through JSON, exactly like the RabbitMQ
/// adapter, so saga definitions and the orchestrator behave identically regardless of transport.
/// Used for local dev and as the transport half of VSaga.Testing.
/// <para>
/// A publish fans out to every subscriber declaring the message type; a send
/// (<see cref="SendAsync{TMessage}"/>/<see cref="SendRawAsync"/>) additionally requires the
/// subscription's <see cref="TransportSubscription.QueueNameHint"/> to equal the destination, so
/// addressed sends stay addressed here rather than only on a real broker.
/// </para>
/// </summary>
public sealed class InMemoryMessageTransport : IMessageTransport
{
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
    private readonly List<PublishedMessage> _published = [];
    private readonly Lock _publishedLock = new();

    public sealed record PublishedMessage(object? Message, string MessageTypeName, MessageEnvelope Envelope, string? Destination);

    private sealed record Subscriber(TransportSubscription Subscription, Func<ReceivedMessage, CancellationToken, Task> Handler);

    /// <summary>All messages published/sent so far, for test assertions. Cleared by <see cref="Reset"/>.</summary>
    public IReadOnlyList<PublishedMessage> GetPublished()
    {
        lock (_publishedLock)
        {
            return _published.ToList();
        }
    }

    public void Reset()
    {
        lock (_publishedLock)
        {
            _published.Clear();
        }
    }

    public Task PublishAsync<TMessage>(TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        where TMessage : notnull
    {
        var messageType = message.GetType();
        var body = JsonSerializer.SerializeToUtf8Bytes(message, messageType);
        return DispatchAsync(message, messageType.Name, body, envelope, destination: null, cancellationToken);
    }

    public Task SendAsync<TMessage>(string destination, TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        where TMessage : notnull
    {
        var messageType = message.GetType();
        var body = JsonSerializer.SerializeToUtf8Bytes(message, messageType);
        return DispatchAsync(message, messageType.Name, body, envelope, destination, cancellationToken);
    }

    public Task PublishRawAsync(string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken cancellationToken = default) =>
        DispatchAsync(message: null, messageTypeName, body, envelope, destination: null, cancellationToken);

    public Task SendRawAsync(string destination, string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken cancellationToken = default) =>
        DispatchAsync(message: null, messageTypeName, body, envelope, destination, cancellationToken);

    public Task<IDisposable> SubscribeAsync(TransportSubscription subscription, Func<ReceivedMessage, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        _subscribers[id] = new Subscriber(subscription, handler);
        return Task.FromResult<IDisposable>(new Unsubscriber(() => _subscribers.TryRemove(id, out _)));
    }

    private async Task DispatchAsync(object? message, string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, string? destination, CancellationToken cancellationToken)
    {
        lock (_publishedLock)
        {
            _published.Add(new PublishedMessage(message, messageTypeName, envelope, destination));
        }

        foreach (var subscriber in _subscribers.Values)
        {
            if (!subscriber.Subscription.MessageTypes.Any(t => string.Equals(t.Name, messageTypeName, StringComparison.Ordinal)))
                continue;

            // An addressed send reaches one queue, not the whole type's fan-out. Every broker adapter
            // resolves `destination` to a queue name (RabbitMQ publishes to the default exchange with it
            // as the routing key; Brighter binds each queue to its own name as an extra routing key), and
            // TransportSubscription.QueueNameHint is that same name on the consuming side. Without this
            // match a SendAsync-isolation test would pass here and fail against every real broker.
            // A send addressed to a queue nobody has subscribed reaches nothing and does not throw --
            // there is no broker here to return it as unroutable the way RabbitMqTransport does.
            if (destination is not null && !string.Equals(subscriber.Subscription.QueueNameHint, destination, StringComparison.Ordinal))
                continue;

            var received = new ReceivedMessage(
                messageTypeName,
                envelope.CorrelationId,
                envelope.MessageId,
                body,
                envelope.Headers ?? new Dictionary<string, string>(StringComparer.Ordinal),
                NoOpAckContext.Instance);

            await subscriber.Handler(received, cancellationToken);
        }
    }

    private sealed class NoOpAckContext : IMessageAckContext
    {
        public static readonly NoOpAckContext Instance = new();

        public Task AckAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task NackAsync(bool requeue, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Unsubscriber(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
