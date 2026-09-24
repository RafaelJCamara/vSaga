using VSaga.Abstractions.Diagnostics;
using VSaga.Abstractions.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.RabbitMq;

namespace VSaga.Transport.Wolverine.Tests;

public sealed record PingMessage(string Text);

/// <summary>
/// Structural mirror of VSaga.Transport.RabbitMQ.Tests/RabbitMqTransportTests.cs, adapted for the fact
/// that Wolverine's runtime (unlike RabbitMqTransport, which is just a plain object) needs a full generic
/// host lifecycle (services.AddWolverine registers Wolverine's own IHostedService, which is what actually
/// opens the broker connection and starts listeners) — one real RabbitMQ broker container per test class,
/// no mocks, exactly like the reference adapter's tests.
/// </summary>
#pragma warning disable CA1001 // _host/_container are disposed in DisposeAsync() via xUnit's IAsyncLifetime, not IAsyncDisposable
public sealed class WolverineTransportTests : IAsyncLifetime
{
#pragma warning restore CA1001
    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4-management").Build();
    private IHost _host = null!;
    private WolverineTransport _transport = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddVSagaWolverine(o =>
        {
            o.ConnectionString = _container.GetConnectionString();
            o.ExchangeName = "vsaga.saga.events.test";
        });

        _host = builder.Build();
        await _host.StartAsync();
        _transport = _host.Services.GetRequiredService<WolverineTransport>();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task PublishAndSubscribe_DeliversMessageWithCorrelationAndType()
    {
        var correlationId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = new TransportSubscription("TestConsumer", [typeof(PingMessage)], "vsaga.wolverine.test.ping-queue");
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

        var subscription = new TransportSubscription("TestConsumer2", [typeof(PingMessage)], "vsaga.wolverine.test.direct-queue");
        using var handle = await _transport.SubscribeAsync(subscription, async (received, ct) =>
        {
            tcs.TrySetResult(received);
            await received.Ack.AckAsync(ct);
        });

        await _transport.SendAsync("vsaga.wolverine.test.direct-queue", new PingMessage("direct"), MessageEnvelope.New(correlationId));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(tcs.Task, completed);
        Assert.Equal(correlationId, (await tcs.Task).CorrelationId);
    }

    [Fact]
    public async Task SendRaw_DeliversDirectlyToNamedQueueWithoutExchange()
    {
        var correlationId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = new TransportSubscription("TestConsumer3", [typeof(PingMessage)], "vsaga.wolverine.test.direct-raw-queue");
        using var handle = await _transport.SubscribeAsync(subscription, async (received, ct) =>
        {
            tcs.TrySetResult(received);
            await received.Ack.AckAsync(ct);
        });

        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new PingMessage("raw-direct"));
        await _transport.SendRawAsync("vsaga.wolverine.test.direct-raw-queue", nameof(PingMessage), body, MessageEnvelope.New(correlationId));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(tcs.Task, completed);

        var received = await tcs.Task;
        Assert.Equal(correlationId, received.CorrelationId);
        Assert.Equal(nameof(PingMessage), received.MessageTypeName);
    }

    /// <summary>
    /// RabbitMqTransportTests' sibling test of the same name asserts that publishing to a routing key with
    /// no bound queue throws MessageTransportPublishException, because RabbitMqTransport turns on
    /// mandatory:true + publisher confirms and RabbitMQ.Client surfaces the broker's basic.return as a
    /// PublishException. WolverineFx.RabbitMQ (6.30.0) has no equivalent: it exposes publisher-confirm
    /// settings (WolverineRabbitMqChannelOptions.PublisherConfirmationsEnabled /
    /// PublisherConfirmationTrackingEnabled, set via .ConfigureChannelCreation(...)) but never sets AMQP's
    /// "mandatory" flag and has no unroutable-return/"basic.return" handling anywhere in its binary
    /// (confirmed by scanning Wolverine.RabbitMQ.dll for "mandatory"/"Unroutable"/"BasicReturn" — zero
    /// matches) or its shipped XML docs. A message published to a routing key nobody is bound to is
    /// therefore silently discarded by the broker, exactly like calling RabbitMQ.Client's BasicPublishAsync
    /// yourself with mandatory:false — Wolverine's SendRawMessageAsync completes normally either way. This
    /// test asserts that actually-observed behavior rather than faking the RabbitMQ adapter's exception —
    /// see deviationsOrIssues in the task report for the full research trail.
    /// </summary>
    [Fact]
    public async Task Publish_ToUnboundRoutingKey_CompletesWithoutThrowing_NoWolverineUnroutableSignal()
    {
        var exception = await Record.ExceptionAsync(() =>
            _transport.PublishAsync(new PingMessage("nobody's listening"), MessageEnvelope.New(Guid.NewGuid())));

        Assert.Null(exception);
    }

    [Fact]
    public async Task PublishAndSubscribe_PropagatesAllFourVSagaHeadersUnchanged()
    {
        var correlationId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = new TransportSubscription("TestConsumer3", [typeof(PingMessage)], "vsaga.wolverine.test.headers-queue");
        using var handle = await _transport.SubscribeAsync(subscription, async (received, ct) =>
        {
            tcs.TrySetResult(received);
            await received.Ack.AckAsync(ct);
        });

        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessageEnvelope.SourceServiceHeader] = "orders-service",
            [MessageEnvelope.CausationIdHeader] = "causation-" + Guid.NewGuid().ToString("N"),
            [MessageEnvelope.ParentSagaTypeHeader] = "PostShipmentChoreography",
            [MessageEnvelope.ParentCorrelationIdHeader] = Guid.NewGuid().ToString(),
        };

        await _transport.PublishAsync(new PingMessage("carries headers"), new MessageEnvelope(correlationId, Guid.NewGuid().ToString("N"), headers));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(tcs.Task, completed);

        var received = await tcs.Task;
        Assert.Equal("orders-service", received.Headers[MessageEnvelope.SourceServiceHeader]);
        Assert.Equal(headers[MessageEnvelope.CausationIdHeader], received.Headers[MessageEnvelope.CausationIdHeader]);
        Assert.Equal("PostShipmentChoreography", received.Headers[MessageEnvelope.ParentSagaTypeHeader]);
        Assert.Equal(headers[MessageEnvelope.ParentCorrelationIdHeader], received.Headers[MessageEnvelope.ParentCorrelationIdHeader]);
    }

    /// <summary>
    /// §6/production-readiness §8.17: `traceparent`/`tracestate` deliberately carry no `x-vsaga-`
    /// prefix -- interoperability with a non-vSaga consumer is the point -- which is precisely why
    /// the sibling four-header test above cannot stand in for this one: anything that filters inbound
    /// headers by that prefix, or that reserves the W3C names for its own instrumentation, drops
    /// these two silently while the prefixed four sail through. This transport carries them inside
    /// WireEnvelope.Headers rather than Wolverine's own Envelope.Headers (see WireEnvelope's doc
    /// comment), so the full 55-character W3C traceparent and a multi-vendor tracestate must come
    /// back byte-identical -- not merely present, since a truncated or re-formatted traceparent still
    /// "exists" but breaks trace stitching.
    /// </summary>
    [Fact]
    public async Task PublishAndSubscribe_PropagatesTraceParentAndTraceStateHeaders()
    {
        var correlationId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = new TransportSubscription("TestConsumer4", [typeof(PingMessage)], "vsaga.wolverine.test.trace-queue");
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

        await _transport.PublishAsync(new PingMessage("traced"), new MessageEnvelope(correlationId, Guid.NewGuid().ToString("N"), headers));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(tcs.Task, completed);

        var received = await tcs.Task;
        Assert.Equal(headers[VSagaDiagnostics.TraceParentHeader], received.Headers[VSagaDiagnostics.TraceParentHeader]);
        Assert.Equal(headers[VSagaDiagnostics.TraceStateHeader], received.Headers[VSagaDiagnostics.TraceStateHeader]);
    }
}
