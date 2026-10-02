using System.Text.Json;
using VSaga.Abstractions.Diagnostics;
using VSaga.Abstractions.Transport;

namespace VSaga.Transport.Http.Tests;

public sealed record PingMessage(string Text);

// Marker messages, correlated purely by the envelope: no payload of their own to carry.
#pragma warning disable S2094
public sealed record Trigger;
public sealed record RoutedSideEffect;
#pragma warning restore S2094

public sealed record Command(string Text);
public sealed record Reply(string Text);
public sealed record RedeliverableCommand(string Text);

/// <summary>
/// Structural mirror of the other adapters' own Tests projects (RabbitMqTransportTests,
/// WolverineTransportTests, ...): the same four canonical test names, so the family reads as one, plus
/// five specific to this adapter, each pinning one constraint from docs/design/http-based-sagas.md §3. Unlike
/// those siblings, there's no broker container here -- each test hosts one or more in-memory
/// TestServer-backed "nodes" wired together by <see cref="NodeRegistry"/>, since this adapter's whole
/// point is that two vSaga services can talk without one.
/// </summary>
public sealed class HttpTransportTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task PublishAndSubscribe_DeliversMessageWithCorrelationAndType()
    {
        var registry = new NodeRegistry();
        await using var receiver = await HttpTestNode.StartAsync("receiver.test", registry, _ => { });
        await using var sender = await HttpTestNode.StartAsync("sender.test", registry, o =>
        {
            o.Endpoints["receiver"] = "http://receiver.test";
            o.Routes["PingMessage"] = ["receiver"];
        });

        var receiverTransport = receiver.GetRequiredService<HttpMessageTransport>();
        var senderTransport = sender.GetRequiredService<HttpMessageTransport>();

        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await receiverTransport.SubscribeAsync(new TransportSubscription("TestConsumer", [typeof(PingMessage)], "receiver-ping-queue"),
            (received, _) => { tcs.TrySetResult(received); return Task.CompletedTask; });

        var correlationId = Guid.NewGuid();
        await senderTransport.PublishAsync(new PingMessage("hello"), MessageEnvelope.New(correlationId));

        var received = await tcs.Task.WaitAsync(Timeout);
        Assert.Equal(correlationId, received.CorrelationId);
        Assert.Equal(nameof(PingMessage), received.MessageTypeName);

        var payload = JsonSerializer.Deserialize<PingMessage>(received.Body.Span);
        Assert.Equal("hello", payload!.Text);
    }

    [Fact]
    public async Task Send_DeliversDirectlyToNamedQueueWithoutExchange()
    {
        var registry = new NodeRegistry();
        await using var receiver = await HttpTestNode.StartAsync("receiver.test", registry, _ => { });
        await using var sender = await HttpTestNode.StartAsync("sender.test", registry, o =>
            o.Endpoints["receiver"] = "http://receiver.test");

        var receiverTransport = receiver.GetRequiredService<HttpMessageTransport>();
        var senderTransport = sender.GetRequiredService<HttpMessageTransport>();

        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await receiverTransport.SubscribeAsync(new TransportSubscription("TestConsumer2", [typeof(PingMessage)], "receiver-direct-queue"),
            (received, _) => { tcs.TrySetResult(received); return Task.CompletedTask; });

        // No Routes entry for PingMessage at all -- SendAsync resolves "receiver" as an endpoint name
        // directly, bypassing Routes entirely (docs/design/http-based-sagas.md §4.3's AMQP-default-exchange analogue).
        var correlationId = Guid.NewGuid();
        await senderTransport.SendAsync("receiver", new PingMessage("direct"), MessageEnvelope.New(correlationId));

        var received = await tcs.Task.WaitAsync(Timeout);
        Assert.Equal(correlationId, received.CorrelationId);
    }

    [Fact]
    public async Task SendRaw_DeliversDirectlyToNamedQueueWithoutExchange()
    {
        var registry = new NodeRegistry();
        await using var receiver = await HttpTestNode.StartAsync("receiver.test", registry, _ => { });
        await using var sender = await HttpTestNode.StartAsync("sender.test", registry, o =>
            o.Endpoints["receiver"] = "http://receiver.test");

        var receiverTransport = receiver.GetRequiredService<HttpMessageTransport>();
        var senderTransport = sender.GetRequiredService<HttpMessageTransport>();

        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await receiverTransport.SubscribeAsync(new TransportSubscription("TestConsumer4", [typeof(PingMessage)], "receiver-direct-raw-queue"),
            (received, _) => { tcs.TrySetResult(received); return Task.CompletedTask; });

        // No Routes entry for PingMessage at all -- SendRawAsync resolves "receiver" as an endpoint name
        // directly, exactly like SendAsync above, just carrying pre-serialized bytes instead of a typed message.
        var correlationId = Guid.NewGuid();
        var body = JsonSerializer.SerializeToUtf8Bytes(new PingMessage("raw-direct"));
        await senderTransport.SendRawAsync("receiver", nameof(PingMessage), body, MessageEnvelope.New(correlationId));

        var received = await tcs.Task.WaitAsync(Timeout);
        Assert.Equal(correlationId, received.CorrelationId);
        Assert.Equal(nameof(PingMessage), received.MessageTypeName);
    }

    [Fact]
    public async Task Publish_ToUnroutedMessageType_ThrowsUnroutablePublishException()
    {
        var registry = new NodeRegistry();
        await using var lonely = await HttpTestNode.StartAsync("lonely.test", registry, _ => { });
        var transport = lonely.GetRequiredService<HttpMessageTransport>();

        var exception = await Assert.ThrowsAsync<MessageTransportPublishException>(() =>
            transport.PublishAsync(new PingMessage("nobody's listening"), MessageEnvelope.New(Guid.NewGuid())));

        Assert.True(exception.IsUnroutable);
    }

    [Fact]
    public async Task PublishAndSubscribe_PropagatesAllFourVSagaHeadersUnchanged()
    {
        var registry = new NodeRegistry();
        await using var receiver = await HttpTestNode.StartAsync("receiver.test", registry, _ => { });
        await using var sender = await HttpTestNode.StartAsync("sender.test", registry, o =>
        {
            o.Endpoints["receiver"] = "http://receiver.test";
            o.Routes["PingMessage"] = ["receiver"];
        });

        var receiverTransport = receiver.GetRequiredService<HttpMessageTransport>();
        var senderTransport = sender.GetRequiredService<HttpMessageTransport>();

        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await receiverTransport.SubscribeAsync(new TransportSubscription("TestConsumer3", [typeof(PingMessage)], "receiver-headers-queue"),
            (received, _) => { tcs.TrySetResult(received); return Task.CompletedTask; });

        var correlationId = Guid.NewGuid();
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessageEnvelope.SourceServiceHeader] = "orders-service",
            [MessageEnvelope.CausationIdHeader] = "causation-" + Guid.NewGuid().ToString("N"),
            [MessageEnvelope.ParentSagaTypeHeader] = "PostShipmentChoreography",
            [MessageEnvelope.ParentCorrelationIdHeader] = Guid.NewGuid().ToString(),
            [MessageEnvelope.TargetSagaTypeHeader] = "InvoiceFollowUpSaga",
        };

        await senderTransport.PublishAsync(new PingMessage("carries headers"), new MessageEnvelope(correlationId, Guid.NewGuid().ToString("N"), headers));

        var received = await tcs.Task.WaitAsync(Timeout);
        Assert.Equal("orders-service", received.Headers[MessageEnvelope.SourceServiceHeader]);
        Assert.Equal(headers[MessageEnvelope.CausationIdHeader], received.Headers[MessageEnvelope.CausationIdHeader]);
        Assert.Equal("PostShipmentChoreography", received.Headers[MessageEnvelope.ParentSagaTypeHeader]);
        Assert.Equal(headers[MessageEnvelope.ParentCorrelationIdHeader], received.Headers[MessageEnvelope.ParentCorrelationIdHeader]);
        Assert.Equal("InvoiceFollowUpSaga", received.Headers[MessageEnvelope.TargetSagaTypeHeader]);
    }

    /// <summary>
    /// §4.2: x-vsaga-message-type has no home in MessageEnvelope, and the response path is exactly
    /// where it's easy to forget to stamp it. If it were missing, HandleSyncReplyAsync can't identify
    /// the reply and throws from inside the awaited PublishAsync call below, failing this test outright
    /// rather than the more roundabout way a dropped header usually fails (a hang until a saga's own
    /// timeout, or a silently unrouted message elsewhere).
    /// </summary>
    [Fact]
    public async Task SyncReply_ResponsePathCarriesFullEnvelopeIncludingMessageType()
    {
        var registry = new NodeRegistry();
        await using var receiver = await HttpTestNode.StartAsync("receiver.test", registry, _ => { });
        await using var sender = await HttpTestNode.StartAsync("sender.test", registry, o =>
        {
            o.Endpoints["receiver"] = "http://receiver.test";
            o.Routes["Command"] = ["receiver"];
        });

        var receiverTransport = receiver.GetRequiredService<HttpMessageTransport>();
        var senderTransport = sender.GetRequiredService<HttpMessageTransport>();

        // Reply has no route/local subscriber on the receiver side -- unroutable, so it's captured as
        // this handler's own synchronous reply to the inbound Command it's currently handling.
        await receiverTransport.SubscribeAsync(new TransportSubscription("Receiver", [typeof(Command)], "receiver-command-queue"),
            async (received, ct) => await receiverTransport.PublishAsync(new Reply("ok"),
                MessageEnvelope.From("Receiver", received.CorrelationId, received.MessageId), ct));

        var replyTcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await senderTransport.SubscribeAsync(new TransportSubscription("ReplyListener", [typeof(Reply)], "sender-reply-queue"),
            (received, _) => { replyTcs.TrySetResult(received); return Task.CompletedTask; });

        var correlationId = Guid.NewGuid();
        await senderTransport.PublishAsync(new Command("charge"), MessageEnvelope.New(correlationId));

        var received = await replyTcs.Task.WaitAsync(Timeout);
        Assert.Equal(nameof(Reply), received.MessageTypeName);
        Assert.Equal(correlationId, received.CorrelationId);
        Assert.Equal("ok", JsonSerializer.Deserialize<Reply>(received.Body.Span)!.Text);
    }

    /// <summary>
    /// §3.1: the reply must not re-enter the saga while its own publishing step is still running. Proven
    /// deterministically, not by timing -- the reply's own dispatch needs the same correlation id's gate
    /// that the trigger's still-running dispatch is holding, so it cannot have run by the time the
    /// trigger handler (which awaited the full HTTP round trip) makes its assertion, no matter how fast
    /// that round trip was.
    /// </summary>
    [Fact]
    public async Task SyncReply_IsNotDispatchedInlineDuringThePublishingStep()
    {
        var registry = new NodeRegistry();
        await using var receiver = await HttpTestNode.StartAsync("receiver.test", registry, _ => { });
        await using var sender = await HttpTestNode.StartAsync("sender.test", registry, o =>
        {
            o.Endpoints["receiver"] = "http://receiver.test";
            o.Routes["Command"] = ["receiver"];
        });

        var receiverTransport = receiver.GetRequiredService<HttpMessageTransport>();
        var senderTransport = sender.GetRequiredService<HttpMessageTransport>();

        await receiverTransport.SubscribeAsync(new TransportSubscription("Receiver", [typeof(Command)], "receiver-command-queue"),
            async (received, ct) => await receiverTransport.PublishAsync(new Reply("ok"),
                MessageEnvelope.From("Receiver", received.CorrelationId, received.MessageId), ct));

        var replySeenTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await senderTransport.SubscribeAsync(new TransportSubscription("ReplyListener", [typeof(Reply)], "sender-reply-queue"),
            (_, _) => { replySeenTcs.TrySetResult(true); return Task.CompletedTask; });

        // The trigger handler holds this correlation's dispatch gate open on purpose -- via
        // releaseGateTcs, completed only by this test, well after the Command round trip (and hence the
        // reply) has already come back -- so any dispatch of that reply observed during the delay below
        // can only be evidence of a genuine block, never scheduling luck. (An earlier version of this
        // test checked replySeenTcs immediately instead of after a real delay, and passed even with the
        // gate removed entirely: a same-thread synchronous continuation chain from the enqueue back up
        // to the trigger handler's own assertion consistently outraced the pump's thread-pool-scheduled
        // dispatch of the reply, regardless of the gate. Caught only by deliberately breaking the gate
        // and re-running this test -- exactly the kind of thing this repo's docs warn never survives a
        // test that only "looks" like it exercises the race.)
        var releaseGateTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commandRoundTripDoneTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await senderTransport.SubscribeAsync(new TransportSubscription("TriggerListener", [typeof(Trigger)], "sender-trigger-queue"),
            async (received, ct) =>
            {
                await senderTransport.PublishAsync(new Command("go"), MessageEnvelope.New(received.CorrelationId), ct);
                commandRoundTripDoneTcs.TrySetResult();
                await releaseGateTcs.Task; // keep holding this correlation's gate open
            });

        var correlationId = Guid.NewGuid();
        await senderTransport.PublishAsync(new Trigger(), MessageEnvelope.New(correlationId));

        await commandRoundTripDoneTcs.Task.WaitAsync(Timeout);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.False(replySeenTcs.Task.IsCompleted);

        releaseGateTcs.SetResult();

        // ...but it does arrive once the trigger's own dispatch finishes and releases the gate.
        Assert.True(await replySeenTcs.Task.WaitAsync(Timeout));
    }

    /// <summary>§3.3a: local subscriptions are part of the route table, exactly like SagaOrchestrator's own-type redelivery relies on over RabbitMQ's routing table.</summary>
    [Fact]
    public async Task Publish_OfLocallySubscribedType_ReEntersLocalSubscriber()
    {
        var registry = new NodeRegistry();
        await using var solo = await HttpTestNode.StartAsync("solo.test", registry, _ => { });
        var transport = solo.GetRequiredService<HttpMessageTransport>();

        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await transport.SubscribeAsync(new TransportSubscription("SelfConsumer", [typeof(RedeliverableCommand)], "solo-redeliver-queue"),
            (received, _) => { tcs.TrySetResult(received); return Task.CompletedTask; });

        var correlationId = Guid.NewGuid();
        var body = JsonSerializer.SerializeToUtf8Bytes(new RedeliverableCommand("retry-me"));

        // No Endpoints/Routes configured for this type at all -- routable only because of the local
        // subscriber above.
        await transport.PublishRawAsync(nameof(RedeliverableCommand), body, MessageEnvelope.New(correlationId));

        var received = await tcs.Task.WaitAsync(Timeout);
        Assert.Equal(correlationId, received.CorrelationId);
        Assert.Equal(nameof(RedeliverableCommand), received.MessageTypeName);
    }

    /// <summary>§3.3b: GetDeliveryAttempt does an ordinal lookup, so the inbound header dictionary must be OrdinalIgnoreCase to survive a proxy (or, here, a deliberately mixed-case sender) normalizing the header's casing.</summary>
    [Fact]
    public async Task DeliveryAttemptHeader_SurvivesACaseNormalizingRoundTrip()
    {
        var registry = new NodeRegistry();
        await using var receiver = await HttpTestNode.StartAsync("receiver.test", registry, _ => { });
        await using var sender = await HttpTestNode.StartAsync("sender.test", registry, o =>
        {
            o.Endpoints["receiver"] = "http://receiver.test";
            o.Routes["PingMessage"] = ["receiver"];
        });

        var receiverTransport = receiver.GetRequiredService<HttpMessageTransport>();
        var senderTransport = sender.GetRequiredService<HttpMessageTransport>();

        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await receiverTransport.SubscribeAsync(new TransportSubscription("TestConsumer", [typeof(PingMessage)], "receiver-attempt-queue"),
            (received, _) => { tcs.TrySetResult(received); return Task.CompletedTask; });

        var correlationId = Guid.NewGuid();
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Deliberately non-canonical casing, simulating a proxy (or another adapter's client) that
            // normalizes header names -- HTTP header names are case-insensitive on the wire.
            ["X-VSaga-Delivery-Attempt"] = "3",
        };

        await senderTransport.PublishAsync(new PingMessage("redelivered"), new MessageEnvelope(correlationId, Guid.NewGuid().ToString("N"), headers));

        var received = await tcs.Task.WaitAsync(Timeout);
        Assert.True(received.Headers.TryGetValue("x-vsaga-delivery-attempt", out var value));
        Assert.Equal("3", value);
    }

    /// <summary>
    /// §6/production-readiness §8.17: `traceparent`/`tracestate` carry no `x-vsaga-` prefix on
    /// purpose (interoperability with a non-vSaga consumer is the point), so both of this
    /// transport's ExtractVSagaHeaders methods -- VSagaHttpEndpointExtensions's on the inbound
    /// request side, HttpMessageTransport's own on the sync-reply response side -- need the two
    /// bare names allowlisted explicitly or they're silently dropped by the prefix filter. Exercises
    /// both directions of one HTTP round trip: the inbound publish (endpoint-side extractor) and the
    /// synchronous reply that comes back (transport-side extractor on the response).
    /// </summary>
    [Fact]
    public async Task TraceParentAndTraceStateHeaders_SurviveBothDirectionsOfAnHttpRoundTrip()
    {
        var registry = new NodeRegistry();
        await using var receiver = await HttpTestNode.StartAsync("receiver.test", registry, _ => { });
        await using var sender = await HttpTestNode.StartAsync("sender.test", registry, o =>
        {
            o.Endpoints["receiver"] = "http://receiver.test";
            o.Routes["Command"] = ["receiver"];
        });

        var receiverTransport = receiver.GetRequiredService<HttpMessageTransport>();
        var senderTransport = sender.GetRequiredService<HttpMessageTransport>();

        const string traceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        const string traceState = "vendor1=value1,vendor2=value2";

        // Reply carries the same trace context back, exactly as an instrumented participant threading
        // the inbound trace onto its reply would -- unroutable on the receiver side, so it's captured
        // as this handler's own synchronous reply.
        await receiverTransport.SubscribeAsync(new TransportSubscription("TraceReceiver", [typeof(Command)], "receiver-trace-queue"),
            async (received, ct) =>
            {
                Assert.Equal(traceParent, received.Headers[VSagaDiagnostics.TraceParentHeader]);
                Assert.Equal(traceState, received.Headers[VSagaDiagnostics.TraceStateHeader]);

                var replyHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [VSagaDiagnostics.TraceParentHeader] = traceParent,
                    [VSagaDiagnostics.TraceStateHeader] = traceState,
                };
                await receiverTransport.PublishAsync(new Reply("ok"),
                    new MessageEnvelope(received.CorrelationId, Guid.NewGuid().ToString("N"), replyHeaders), ct);
            });

        var replyTcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await senderTransport.SubscribeAsync(new TransportSubscription("TraceReplyListener", [typeof(Reply)], "sender-trace-reply-queue"),
            (received, _) => { replyTcs.TrySetResult(received); return Task.CompletedTask; });

        var correlationId = Guid.NewGuid();
        var outboundHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [VSagaDiagnostics.TraceParentHeader] = traceParent,
            [VSagaDiagnostics.TraceStateHeader] = traceState,
        };
        await senderTransport.PublishAsync(new Command("charge"), new MessageEnvelope(correlationId, Guid.NewGuid().ToString("N"), outboundHeaders));

        var reply = await replyTcs.Task.WaitAsync(Timeout);
        Assert.Equal(traceParent, reply.Headers[VSagaDiagnostics.TraceParentHeader]);
        Assert.Equal(traceState, reply.Headers[VSagaDiagnostics.TraceStateHeader]);
    }

    /// <summary>
    /// §3.2: a message that has a real route (the ShipOrder case) must go out as a normal POST, never
    /// be swallowed as the currently-in-flight inbound request's own synchronous reply, even though
    /// both are published from inside the very same inline dispatch.
    /// </summary>
    [Fact]
    public async Task Publish_OfRoutedTypeFromInsideAHandler_IsNotCapturedAsTheSyncReply()
    {
        var registry = new NodeRegistry();

        await using var receiver = await HttpTestNode.StartAsync("receiver.test", registry, o =>
        {
            o.Endpoints["sender"] = "http://sender.test";
            o.Routes["RoutedSideEffect"] = ["sender"];
        });
        await using var sender = await HttpTestNode.StartAsync("sender.test", registry, o =>
        {
            o.Endpoints["receiver"] = "http://receiver.test";
            o.Routes["Trigger"] = ["receiver"];
        });

        var receiverTransport = receiver.GetRequiredService<HttpMessageTransport>();
        var senderTransport = sender.GetRequiredService<HttpMessageTransport>();

        await receiverTransport.SubscribeAsync(new TransportSubscription("Receiver", [typeof(Trigger)], "receiver-trigger-queue"),
            async (received, ct) => await receiverTransport.PublishAsync(new RoutedSideEffect(), MessageEnvelope.New(received.CorrelationId), ct));

        var sideEffectTcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await senderTransport.SubscribeAsync(new TransportSubscription("SideEffectListener", [typeof(RoutedSideEffect)], "sender-sideeffect-queue"),
            (received, _) => { sideEffectTcs.TrySetResult(received); return Task.CompletedTask; });

        var correlationId = Guid.NewGuid();

        // Completes with an ordinary 202 from the receiver -- nothing was captured as Trigger's reply,
        // because RoutedSideEffect resolved to a real destination instead. That also makes this
        // dispatch a genuine, independent, inline-awaited inbound request on the sender's own endpoint:
        // it has to have already reached SideEffectListener by the time this call returns, since
        // receiver's own response to Trigger isn't written until *its* handler (which awaits publishing
        // RoutedSideEffect to completion) finishes. If RoutedSideEffect were instead captured as
        // Trigger's reply, SideEffectListener would only see it later, off of this call's own returned
        // 200 body, fed back asynchronously through the local-dispatch channel -- so checking
        // IsCompleted immediately below (not after any delay) is a real structural proof, not a timing
        // guess: a happens-before relationship through two nested, fully-awaited HTTP round trips, not
        // a race between independently-scheduled continuations.
        await senderTransport.PublishAsync(new Trigger(), MessageEnvelope.New(correlationId));
        Assert.True(sideEffectTcs.Task.IsCompleted);

        var received = await sideEffectTcs.Task.WaitAsync(Timeout);
        Assert.Equal(correlationId, received.CorrelationId);
        Assert.Equal(nameof(RoutedSideEffect), received.MessageTypeName);
    }
}
