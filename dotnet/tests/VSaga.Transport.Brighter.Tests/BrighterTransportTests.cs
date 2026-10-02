using VSaga.Abstractions.Diagnostics;
using VSaga.Abstractions.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Testcontainers.RabbitMq;

namespace VSaga.Transport.Brighter.Tests;

public sealed record PingMessage(string Text);

#pragma warning disable CA1001 // no unmanaged/disposable fields are held outside what IAsyncLifetime.DisposeAsync already tears down
public sealed class BrighterTransportTests : IAsyncLifetime
{
#pragma warning restore CA1001
    private const string ExchangeName = "vsaga.saga.events.brighter.test";

    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4-management").Build();
    private BrighterTransport _transport = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var options = new BrighterOptions
        {
            ConnectionString = _container.GetConnectionString(),
            ExchangeName = ExchangeName,
        };

        _transport = new BrighterTransport(options, NullLogger<BrighterTransport>.Instance);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    [Fact]
    public async Task PublishAndSubscribe_DeliversMessageWithCorrelationAndType()
    {
        var correlationId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = new TransportSubscription("TestConsumer", [typeof(PingMessage)], "vsaga.brighter.test.ping-queue");
        using var handle = await _transport.SubscribeAsync(subscription, async (received, ct) =>
        {
            tcs.TrySetResult(received);
            await received.Ack.AckAsync(ct);
        });

        await _transport.PublishAsync(new PingMessage("hello"), MessageEnvelope.New(correlationId));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(tcs.Task, completed);

        var received = await tcs.Task;
        Assert.Equal(correlationId, received.CorrelationId);
        Assert.Equal(nameof(PingMessage), received.MessageTypeName);

        var payload = System.Text.Json.JsonSerializer.Deserialize<PingMessage>(received.Body.Span);
        Assert.Equal("hello", payload!.Text);
    }

    [Fact]
    public async Task Send_DeliversDirectlyToNamedQueueWithoutExchange()
    {
        var correlationId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = new TransportSubscription("TestConsumer2", [typeof(PingMessage)], "vsaga.brighter.test.direct-queue");
        using var handle = await _transport.SubscribeAsync(subscription, async (received, ct) =>
        {
            tcs.TrySetResult(received);
            await received.Ack.AckAsync(ct);
        });

        await _transport.SendAsync("vsaga.brighter.test.direct-queue", new PingMessage("direct"), MessageEnvelope.New(correlationId));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(tcs.Task, completed);
        Assert.Equal(correlationId, (await tcs.Task).CorrelationId);
    }

    [Fact]
    public async Task SendRaw_DeliversDirectlyToNamedQueueWithoutExchange()
    {
        var correlationId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = new TransportSubscription("TestConsumer3", [typeof(PingMessage)], "vsaga.brighter.test.direct-raw-queue");
        using var handle = await _transport.SubscribeAsync(subscription, async (received, ct) =>
        {
            tcs.TrySetResult(received);
            await received.Ack.AckAsync(ct);
        });

        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new PingMessage("raw-direct"));
        await _transport.SendRawAsync("vsaga.brighter.test.direct-raw-queue", nameof(PingMessage), body, MessageEnvelope.New(correlationId));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(tcs.Task, completed);

        var received = await tcs.Task;
        Assert.Equal(correlationId, received.CorrelationId);
        Assert.Equal(nameof(PingMessage), received.MessageTypeName);
    }

    // Deviation from the RabbitMQ transport's equivalent test (see deviationsOrIssues in the build
    // report and docs/transports/brighter.md): Paramore.Brighter.MessagingGateway.RMQ.Async's
    // RmqMessageProducer never sets AMQP's "mandatory" flag when publishing. Confirmed both by reading
    // the package's exchange/publish code and by direct testing against a live broker: publishing to a
    // routing key nobody has ever bound a queue to still yields a broker-side ack (Success=true) on the
    // publisher-confirm, because the broker only refuses to route a message back to the publisher (a
    // "basic.return") when the publish explicitly opts into mandatory delivery, which this package's
    // producer does not. There is no equivalent of RabbitMqTransport's mandatory-plus-publisher-confirms
    // unroutable-return detection exposed anywhere in this package (no such option on RmqPublication,
    // RmqMessagingGatewayConnection, or RmqMessageProducer's constructors). Rather than fake a passing
    // test for a throw that cannot occur, this test documents the actual, verified behavior instead.
    [Fact]
    public async Task Publish_ToUnboundRoutingKey_DoesNotThrow_NoMandatoryReturnSupportInBrighterRmqGateway()
    {
        var exception = await Record.ExceptionAsync(() =>
            _transport.PublishAsync(new PingMessage("nobody's listening"), MessageEnvelope.New(Guid.NewGuid())));

        // No exception: the broker confirms the publish even though it was never routed anywhere.
        Assert.Null(exception);
    }

    // The genuine-nack counterpart to the test above -- closes the coverage gap a prior fix (see
    // BrighterTransport.SendWithConfirmationAsync's own remarks) left open. TransportSubscription/
    // SubscribeAsync has no way to declare queue arguments, so this raw RabbitMQ.Client channel
    // (against the same Testcontainers broker, same exchange BrighterTransport itself publishes to)
    // declares a queue the broker is guaranteed to reject every publish into: x-max-length 0 with
    // x-overflow reject-publish means even the very first message overflows. This is what proved, by
    // direct testing, that RabbitMQ.Client 7.2.2 throws RabbitMQ.Client.Exceptions.PublishException
    // synchronously out of SendAsync for a genuine nack -- see the comment above
    // SendWithConfirmationAsync for the full mechanism.
    [Fact]
    public async Task Send_ToOverflowingQueue_ThrowsMessageTransportPublishException_NotIsUnroutable()
    {
        const string queueName = "vsaga.brighter.test.overflow-queue";

        await using (var connection = await new ConnectionFactory { Uri = new Uri(_container.GetConnectionString()) }.CreateConnectionAsync())
        await using (var channel = await connection.CreateChannelAsync())
        {
            await channel.ExchangeDeclareAsync(ExchangeName, "topic", durable: true);
            await channel.QueueDeclareAsync(
                queueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["x-max-length"] = 0,
                    ["x-overflow"] = "reject-publish",
                });
            await channel.QueueBindAsync(queueName, ExchangeName, queueName);
        }

        var exception = await Record.ExceptionAsync(() =>
            _transport.SendAsync(queueName, new PingMessage("should be rejected"), MessageEnvelope.New(Guid.NewGuid())));

        var publishException = Assert.IsType<MessageTransportPublishException>(exception);
        // isUnroutable: false -- this is a broker-side rejection of a message that WAS routed to a
        // real, bound queue, the opposite of the zero-bound-queues scenario above.
        Assert.False(publishException.IsUnroutable);
        var brokerException = Assert.IsType<PublishException>(publishException.InnerException);
        Assert.False(brokerException.IsReturn);
    }

    [Fact]
    public async Task PublishAndSubscribe_PropagatesAllFourVSagaHeadersUnchanged()
    {
        var correlationId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = new TransportSubscription("TestConsumer3", [typeof(PingMessage)], "vsaga.brighter.test.headers-queue");
        using var handle = await _transport.SubscribeAsync(subscription, async (received, ct) =>
        {
            tcs.TrySetResult(received);
            await received.Ack.AckAsync(ct);
        });

        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessageEnvelope.SourceServiceHeader] = "order-processing-test",
            [MessageEnvelope.CausationIdHeader] = "causation-" + Guid.NewGuid().ToString("N"),
            [MessageEnvelope.ParentSagaTypeHeader] = "InvoiceFollowUpSaga",
            [MessageEnvelope.ParentCorrelationIdHeader] = Guid.NewGuid().ToString(),
            [MessageEnvelope.TargetSagaTypeHeader] = "InvoiceFollowUpSaga",
        };
        var envelope = MessageEnvelope.New(correlationId, headers);

        await _transport.PublishAsync(new PingMessage("sub-saga headers"), envelope);

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(tcs.Task, completed);

        var received = await tcs.Task;
        Assert.Equal(headers[MessageEnvelope.SourceServiceHeader], received.Headers[MessageEnvelope.SourceServiceHeader]);
        Assert.Equal(headers[MessageEnvelope.CausationIdHeader], received.Headers[MessageEnvelope.CausationIdHeader]);
        Assert.Equal(headers[MessageEnvelope.ParentSagaTypeHeader], received.Headers[MessageEnvelope.ParentSagaTypeHeader]);
        Assert.Equal(headers[MessageEnvelope.ParentCorrelationIdHeader], received.Headers[MessageEnvelope.ParentCorrelationIdHeader]);
        Assert.Equal(headers[MessageEnvelope.TargetSagaTypeHeader], received.Headers[MessageEnvelope.TargetSagaTypeHeader]);
    }

    /// <summary>
    /// §6/production-readiness §8.17: BuildReceivedMessage filters MessageHeader.Bag to the
    /// "x-vsaga-" prefix on receipt, to keep Brighter's own CloudEvents-flavored Bag noise out of
    /// ReceivedMessage.Headers. `traceparent`/`tracestate` carry no such prefix by design
    /// (interoperability with a non-vSaga consumer is the point), so they need allowlisting by exact
    /// name alongside that filter or they're silently dropped on receipt even though PublishAsync
    /// already writes every envelope header into the Bag unfiltered.
    /// </summary>
    [Fact]
    public async Task PublishAndSubscribe_PropagatesTraceParentAndTraceStateHeaders()
    {
        var correlationId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = new TransportSubscription("TestConsumer4", [typeof(PingMessage)], "vsaga.brighter.test.trace-queue");
        using var handle = await _transport.SubscribeAsync(subscription, async (received, ct) =>
        {
            tcs.TrySetResult(received);
            await received.Ack.AckAsync(ct);
        });

        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [VSagaDiagnostics.TraceParentHeader] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            [VSagaDiagnostics.TraceStateHeader] = "vendor1=value1,vendor2=value2",
        };
        var envelope = MessageEnvelope.New(correlationId, headers);

        await _transport.PublishAsync(new PingMessage("traced"), envelope);

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(tcs.Task, completed);

        var received = await tcs.Task;
        Assert.Equal(headers[VSagaDiagnostics.TraceParentHeader], received.Headers[VSagaDiagnostics.TraceParentHeader]);
        Assert.Equal(headers[VSagaDiagnostics.TraceStateHeader], received.Headers[VSagaDiagnostics.TraceStateHeader]);
    }
}
