using VSaga.Abstractions.Diagnostics;
using VSaga.Abstractions.Transport;
using global::MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.RabbitMq;

namespace VSaga.Transport.MassTransit.Tests;

public sealed record PingMessage(string Text);

#pragma warning disable CA1001 // _provider/_container are disposed in DisposeAsync() via xUnit's IAsyncLifetime, not IAsyncDisposable
public sealed class MassTransitTransportTests : IAsyncLifetime
{
#pragma warning restore CA1001
    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4-management").Build();
    private ServiceProvider _provider = null!;
    private IBusControl _busControl = null!;
    private IMessageTransport _transport = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaMassTransit(o =>
        {
            o.ConnectionString = _container.GetConnectionString();
            o.ExchangeName = "vsaga.saga.events.test";
        });

        _provider = services.BuildServiceProvider();
        _busControl = _provider.GetRequiredService<IBusControl>();
        await _busControl.StartAsync();

        _transport = _provider.GetRequiredService<IMessageTransport>();
    }

    public async Task DisposeAsync()
    {
        await _busControl.StopAsync();
        await _provider.DisposeAsync();
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
        // No subscriber has ever bound a queue for this message type on this test's own exchange, so
        // with MassTransit's Mandatory publish flag set, the broker must return it as unroutable
        // (MassTransit surfaces this as MessageReturnedException) rather than silently dropping it.
        var ex = await Assert.ThrowsAsync<MessageTransportPublishException>(() =>
            _transport.PublishAsync(new PingMessage("nobody's listening"), MessageEnvelope.New(Guid.NewGuid())));

        Assert.True(ex.IsUnroutable);
    }

    [Fact]
    public async Task PublishAndSubscribe_PropagatesAllFourVSagaHeadersUnchanged()
    {
        var correlationId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = new TransportSubscription("HeaderConsumer", [typeof(PingMessage)], "vsaga.test.header-queue");
        using var handle = await _transport.SubscribeAsync(subscription, async (received, ct) =>
        {
            tcs.TrySetResult(received);
            await received.Ack.AckAsync(ct);
        });

        var stampedHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessageEnvelope.SourceServiceHeader] = "order-processing-test",
            [MessageEnvelope.CausationIdHeader] = "causation-" + Guid.NewGuid().ToString("N"),
            [MessageEnvelope.ParentSagaTypeHeader] = "ParentSagaTypeForTest",
            [MessageEnvelope.ParentCorrelationIdHeader] = Guid.NewGuid().ToString(),
        };

        await _transport.PublishAsync(new PingMessage("with-headers"), MessageEnvelope.New(correlationId, stampedHeaders));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(tcs.Task, completed);

        var received = await tcs.Task;
        foreach (var (key, expectedValue) in stampedHeaders)
        {
            Assert.True(received.Headers.TryGetValue(key, out var actualValue), $"Header '{key}' did not survive the round trip at all.");
            Assert.Equal(expectedValue, actualValue);
        }
    }

    /// <summary>
    /// §6/production-readiness §8.17: `traceparent`/`tracestate` deliberately carry no `x-vsaga-`
    /// prefix -- interoperability with a non-vSaga consumer is the point -- so they ride
    /// MassTransit's own SendContext.Headers/ConsumeContext.Headers under their bare W3C names,
    /// alongside MassTransit's unrelated `Diagnostic-Id` instrumentation header rather than in place
    /// of it. The sibling four-header test above can't stand in for this one: it only ever exercises
    /// prefixed names, which is exactly the class of bug (a prefix filter, or a transport that
    /// reserves the W3C names for itself) that would drop these two silently. Asserted
    /// byte-identical, not merely present -- a truncated or re-formatted traceparent still "exists".
    /// </summary>
    [Fact]
    public async Task PublishAndSubscribe_PropagatesTraceParentAndTraceStateHeaders()
    {
        var correlationId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = new TransportSubscription("TraceConsumer", [typeof(PingMessage)], "vsaga.test.trace-queue");
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
