using VSaga.Abstractions.Diagnostics;
using VSaga.Abstractions.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.RabbitMq;

namespace VSaga.Transport.RabbitMQ.Tests;

public sealed record PingMessage(string Text);

#pragma warning disable CA1001 // _connectionManager is disposed in DisposeAsync() via xUnit's IAsyncLifetime, not IAsyncDisposable
public sealed class RabbitMqTransportTests : IAsyncLifetime
{
#pragma warning restore CA1001
    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4-management").Build();
    private RabbitMqConnectionManager _connectionManager = null!;
    private RabbitMqTransport _transport = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var options = new RabbitMqOptions
        {
            ConnectionString = _container.GetConnectionString(),
            ExchangeName = "vsaga.saga.events.test",
            DeadLetterExchangeName = "vsaga.dlx.test",
        };

        _connectionManager = new RabbitMqConnectionManager(options);
        _transport = new RabbitMqTransport(_connectionManager, options, new DefaultRoutingKeyConvention(), NullLogger<RabbitMqTransport>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _connectionManager.DisposeAsync();
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task PublishAndSubscribe_DeliversMessageWithCorrelationAndType()
    {
        var correlationId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = new TransportSubscription("TestConsumer", [typeof(PingMessage)], "vsaga.test.ping-queue");
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

        var subscription = new TransportSubscription("TestConsumer2", [typeof(PingMessage)], "vsaga.test.direct-queue");
        using var handle = await _transport.SubscribeAsync(subscription, async (received, ct) =>
        {
            tcs.TrySetResult(received);
            await received.Ack.AckAsync(ct);
        });

        await _transport.SendAsync("vsaga.test.direct-queue", new PingMessage("direct"), MessageEnvelope.New(correlationId));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(tcs.Task, completed);
        Assert.Equal(correlationId, (await tcs.Task).CorrelationId);
    }

    [Fact]
    public async Task SendRaw_DeliversDirectlyToNamedQueueWithoutExchange()
    {
        var correlationId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = new TransportSubscription("TestConsumer3", [typeof(PingMessage)], "vsaga.test.direct-raw-queue");
        using var handle = await _transport.SubscribeAsync(subscription, async (received, ct) =>
        {
            tcs.TrySetResult(received);
            await received.Ack.AckAsync(ct);
        });

        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new PingMessage("raw-direct"));
        await _transport.SendRawAsync("vsaga.test.direct-raw-queue", nameof(PingMessage), body, MessageEnvelope.New(correlationId));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(tcs.Task, completed);

        var received = await tcs.Task;
        Assert.Equal(correlationId, received.CorrelationId);
        Assert.Equal(nameof(PingMessage), received.MessageTypeName);
    }

    [Fact]
    public async Task Publish_ToUnboundRoutingKey_ThrowsUnroutablePublishException()
    {
        // No subscriber has ever bound a queue for this message type, so with publisher confirms +
        // mandatory:true the broker must return it as unroutable rather than silently dropping it.
        var ex = await Assert.ThrowsAsync<MessageTransportPublishException>(() =>
            _transport.PublishAsync(new PingMessage("nobody's listening"), MessageEnvelope.New(Guid.NewGuid())));

        Assert.True(ex.IsUnroutable);
        // The likely-cause hint that closes the "docs never warn about this" field-test finding.
        Assert.Contains("SubscribeAsync", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reference adapter's own version of the header-threading round trip every other adapter's
    /// suite carries: BuildHeaders copies MessageEnvelope.Headers verbatim into BasicProperties.Headers
    /// and ToStringHeaders copies the delivery's headers back out with no allowlist, so all four
    /// x-vsaga- envelope headers must arrive byte-identical -- note RabbitMQ.Client hands string header
    /// values back as AMQP longstr byte arrays, so this also covers GetHeaderString's UTF-8 decode, the
    /// one place the values could silently change shape between publish and receive.
    /// </summary>
    [Fact]
    public async Task PublishAndSubscribe_PropagatesAllFourVSagaHeadersUnchanged()
    {
        var correlationId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = new TransportSubscription("TestConsumer4", [typeof(PingMessage)], "vsaga.test.headers-queue");
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
        };

        await _transport.PublishAsync(new PingMessage("sub-saga headers"), MessageEnvelope.New(correlationId, headers));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(tcs.Task, completed);

        var received = await tcs.Task;
        Assert.Equal(headers[MessageEnvelope.SourceServiceHeader], received.Headers[MessageEnvelope.SourceServiceHeader]);
        Assert.Equal(headers[MessageEnvelope.CausationIdHeader], received.Headers[MessageEnvelope.CausationIdHeader]);
        Assert.Equal(headers[MessageEnvelope.ParentSagaTypeHeader], received.Headers[MessageEnvelope.ParentSagaTypeHeader]);
        Assert.Equal(headers[MessageEnvelope.ParentCorrelationIdHeader], received.Headers[MessageEnvelope.ParentCorrelationIdHeader]);
    }

    /// <summary>
    /// §6/production-readiness §8.17: `traceparent`/`tracestate` deliberately carry no `x-vsaga-`
    /// prefix (interoperability with a non-vSaga consumer is the point), so any adapter that filters
    /// inbound headers by that prefix drops them silently. This transport filters nothing on either
    /// side, and this test is what holds that: the full 55-character W3C traceparent and a
    /// multi-vendor tracestate must come back exactly as published, not merely present.
    /// </summary>
    [Fact]
    public async Task PublishAndSubscribe_PropagatesTraceParentAndTraceStateHeaders()
    {
        var correlationId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = new TransportSubscription("TestConsumer5", [typeof(PingMessage)], "vsaga.test.trace-queue");
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

        await _transport.PublishAsync(new PingMessage("traced"), MessageEnvelope.New(correlationId, headers));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(tcs.Task, completed);

        var received = await tcs.Task;
        Assert.Equal(headers[VSagaDiagnostics.TraceParentHeader], received.Headers[VSagaDiagnostics.TraceParentHeader]);
        Assert.Equal(headers[VSagaDiagnostics.TraceStateHeader], received.Headers[VSagaDiagnostics.TraceStateHeader]);
    }
}
