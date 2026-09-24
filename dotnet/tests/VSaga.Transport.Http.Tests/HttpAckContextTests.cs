using System.Text.Json;
using VSaga.Abstractions.Transport;

namespace VSaga.Transport.Http.Tests;

public sealed record NackProbe(string Text);
public sealed record NackCommand(string Text);
public sealed record NackReply(string Text);

/// <summary>
/// docs/design/http-based-sagas.md §4.4's ack model, which has no broker underneath it: <c>AckAsync</c>
/// drops, <c>NackAsync(requeue: true)</c> re-enqueues, <c>NackAsync(requeue: false)</c> logs at error
/// and drops. The interesting part of that contract on this adapter is *which* of its three delivery
/// paths can honour requeue at all, so these tests pin each one separately:
/// <list type="bullet">
/// <item>a same-process publish that resolved to a local subscriber -- requeue works;</item>
/// <item>a 200 reply to our own outbound POST -- also requeue, because it is already an ordinary
/// deferred dispatch off the same channel, so a redelivery is indistinguishable from the original;</item>
/// <item>an inbound HTTP request -- error-log and drop, because its delivery is inseparable from the
/// in-flight request (ambient reply collector, response written from that dispatch's outcome).</item>
/// </list>
/// Plus the two hazards the model creates: an unbounded requeue loop, and a handler settling twice.
/// No Testcontainers here either -- everything runs against in-memory TestServer nodes.
/// </summary>
public sealed class HttpAckContextTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Long enough that a redelivery -- which in every passing test here arrives in single-digit milliseconds -- would have been observed if one were coming at all.</summary>
    private static readonly TimeSpan NoRedeliveryWindow = TimeSpan.FromMilliseconds(300);

    private const string DeliveryAttemptHeader = "x-vsaga-delivery-attempt";

    private static Task PublishProbeAsync(HttpMessageTransport transport, Guid correlationId, IReadOnlyDictionary<string, string>? headers = null) =>
        transport.PublishRawAsync(nameof(NackProbe), JsonSerializer.SerializeToUtf8Bytes(new NackProbe("probe")),
            MessageEnvelope.New(correlationId, headers));

    /// <summary>
    /// The core of the contract. No route or endpoint is configured for NackProbe at all, so this
    /// publish is routable only through the local subscriber (§3.3a) and lands on the dispatcher's own
    /// in-process channel -- the one place a redelivery can go on a brokerless transport.
    /// </summary>
    [Fact]
    public async Task NackWithRequeue_OnALocalDelivery_RedeliversToTheSameSubscriber()
    {
        var registry = new NodeRegistry();
        await using var solo = await HttpTestNode.StartAsync("solo.test", registry, _ => { });
        var transport = solo.GetRequiredService<HttpMessageTransport>();

        var deliveries = 0;
        var redeliveredTcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        await transport.SubscribeAsync(new TransportSubscription("Requeuer", [typeof(NackProbe)], "solo-requeue-queue"),
            async (received, ct) =>
            {
                if (Interlocked.Increment(ref deliveries) == 1)
                {
                    await received.Ack.NackAsync(requeue: true, ct);
                    return;
                }

                await received.Ack.AckAsync(ct);
                redeliveredTcs.TrySetResult(received);
            });

        var correlationId = Guid.NewGuid();
        await PublishProbeAsync(transport, correlationId);

        var redelivered = await redeliveredTcs.Task.WaitAsync(Timeout);
        Assert.Equal(correlationId, redelivered.CorrelationId);
        Assert.Equal(nameof(NackProbe), redelivered.MessageTypeName);

        // Not just "something arrived": the body has to survive the round trip through the channel too,
        // since a redelivery that loses its payload is no redelivery at all.
        Assert.Equal("probe", JsonSerializer.Deserialize<NackProbe>(redelivered.Body.Span)!.Text);
    }

    /// <summary>
    /// The redelivered copy has to carry <c>x-vsaga-delivery-attempt</c> through unchanged. That header
    /// is the engine's own redelivery counter -- SagaOrchestrator.HandleInfrastructureFailureAsync
    /// increments it on each republish and dead-letters at MaxDeliveryAttempts -- and this transport
    /// neither owns it nor may reset it: a requeue that dropped or zeroed it would silently uncap the
    /// orchestrator's redelivery for every message that had also been requeued once. Asserting it is
    /// *unchanged* (not incremented) is the point: a requeue is not a delivery attempt in the
    /// orchestrator's sense, and the requeue loop is bounded separately, by
    /// <see cref="HttpInboundDispatcher.MaxRequeueAttempts"/>.
    /// </summary>
    [Fact]
    public async Task NackWithRequeue_CarriesTheDeliveryAttemptHeaderThroughUnchanged()
    {
        var registry = new NodeRegistry();
        await using var solo = await HttpTestNode.StartAsync("solo.test", registry, _ => { });
        var transport = solo.GetRequiredService<HttpMessageTransport>();

        var deliveries = 0;
        var redeliveredTcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        await transport.SubscribeAsync(new TransportSubscription("HeaderRequeuer", [typeof(NackProbe)], "solo-requeue-headers-queue"),
            async (received, ct) =>
            {
                if (Interlocked.Increment(ref deliveries) == 1)
                {
                    await received.Ack.NackAsync(requeue: true, ct);
                    return;
                }

                await received.Ack.AckAsync(ct);
                redeliveredTcs.TrySetResult(received);
            });

        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Exactly what SagaOrchestrator writes on a republish, spelled the way it writes it.
            [DeliveryAttemptHeader] = "4",
            [MessageEnvelope.SourceServiceHeader] = "orders-service",
        };

        await PublishProbeAsync(transport, Guid.NewGuid(), headers);

        var redelivered = await redeliveredTcs.Task.WaitAsync(Timeout);
        Assert.Equal("4", redelivered.Headers[DeliveryAttemptHeader]);
        Assert.Equal("orders-service", redelivered.Headers[MessageEnvelope.SourceServiceHeader]);
    }

    /// <summary>§4.4's third case: requeue: false drops. The error log that goes with it is the adapter's entire dead-letter record (no IHttpDeadLetterSink by design), but what is pinned here is that nothing comes back.</summary>
    [Fact]
    public async Task NackWithoutRequeue_DropsTheMessageInsteadOfRedelivering()
    {
        var registry = new NodeRegistry();
        await using var solo = await HttpTestNode.StartAsync("solo.test", registry, _ => { });
        var transport = solo.GetRequiredService<HttpMessageTransport>();

        var deliveries = 0;
        var firstDeliveryNackedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await transport.SubscribeAsync(new TransportSubscription("Dropper", [typeof(NackProbe)], "solo-drop-queue"),
            async (received, ct) =>
            {
                Interlocked.Increment(ref deliveries);
                await received.Ack.NackAsync(requeue: false, ct);
                firstDeliveryNackedTcs.TrySetResult();
            });

        await PublishProbeAsync(transport, Guid.NewGuid());

        // Waiting on the nack itself, not merely on the handler being entered: by the time this
        // completes, the branch that *would* have written a redelivery to the channel has already run
        // and chosen not to, so the window below is only there to let a mistaken write be drained and
        // dispatched -- not to give the nack time to happen.
        await firstDeliveryNackedTcs.Task.WaitAsync(Timeout);
        await Task.Delay(NoRedeliveryWindow);

        Assert.Equal(1, Volatile.Read(ref deliveries));
    }

    /// <summary>
    /// The one genuinely unbounded shape this ack model admits, and the reason
    /// <see cref="HttpInboundDispatcher.MaxRequeueAttempts"/> exists. Nothing in the engine bounds it:
    /// SagaOrchestrator bounds *its* redelivery with the x-vsaga-delivery-attempt header it increments
    /// itself, and it never calls NackAsync(requeue: true) at all -- so a handler that always requeues
    /// increments nothing anyone reads, and would spin the local dispatch channel at full speed
    /// forever. This handler is exactly that handler; the cap is what stops it.
    /// </summary>
    [Fact]
    public async Task NackWithRequeue_StopsAtTheRequeueCapRatherThanLoopingForever()
    {
        var registry = new NodeRegistry();
        await using var solo = await HttpTestNode.StartAsync("solo.test", registry, _ => { });
        var transport = solo.GetRequiredService<HttpMessageTransport>();

        // One original delivery plus MaxRequeueAttempts redeliveries: the Nth delivery carries a requeue
        // count of N-1, so the first delivery whose count has reached the cap is the last one.
        const int expectedDeliveries = HttpInboundDispatcher.MaxRequeueAttempts + 1;

        var deliveries = 0;
        var capReachedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await transport.SubscribeAsync(new TransportSubscription("AlwaysRequeues", [typeof(NackProbe)], "solo-requeue-cap-queue"),
            async (received, ct) =>
            {
                var delivery = Interlocked.Increment(ref deliveries);
                await received.Ack.NackAsync(requeue: true, ct);

                if (delivery >= expectedDeliveries)
                    capReachedTcs.TrySetResult();
            });

        await PublishProbeAsync(transport, Guid.NewGuid());

        await capReachedTcs.Task.WaitAsync(Timeout);
        await Task.Delay(NoRedeliveryWindow);

        Assert.Equal(expectedDeliveries, Volatile.Read(ref deliveries));
    }

    /// <summary>
    /// Settling is idempotent, first settle wins. A handler with an ack on the happy path and a nack in
    /// a <c>finally</c>/<c>catch</c> is ordinary code, and on a real broker the second call would be a
    /// protocol error rather than a second delivery -- here it must simply do nothing.
    /// </summary>
    [Fact]
    public async Task AckFollowedByNackWithRequeue_NeverRedelivers_TheFirstSettleWins()
    {
        var registry = new NodeRegistry();
        await using var solo = await HttpTestNode.StartAsync("solo.test", registry, _ => { });
        var transport = solo.GetRequiredService<HttpMessageTransport>();

        var deliveries = 0;
        var settledTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await transport.SubscribeAsync(new TransportSubscription("AcksThenNacks", [typeof(NackProbe)], "solo-ack-then-nack-queue"),
            async (received, ct) =>
            {
                Interlocked.Increment(ref deliveries);
                await received.Ack.AckAsync(ct);
                await received.Ack.NackAsync(requeue: true, ct);
                await received.Ack.NackAsync(requeue: true, ct);
                settledTcs.TrySetResult();
            });

        await PublishProbeAsync(transport, Guid.NewGuid());

        await settledTcs.Task.WaitAsync(Timeout);
        await Task.Delay(NoRedeliveryWindow);

        Assert.Equal(1, Volatile.Read(ref deliveries));
    }

    /// <summary>The other half of idempotency, and the one that would actually corrupt delivery counts: two requeue nacks on the same delivery must produce one redelivery, not two.</summary>
    [Fact]
    public async Task NackWithRequeueTwice_RedeliversExactlyOnce()
    {
        var registry = new NodeRegistry();
        await using var solo = await HttpTestNode.StartAsync("solo.test", registry, _ => { });
        var transport = solo.GetRequiredService<HttpMessageTransport>();

        var deliveries = 0;
        var secondDeliveryTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await transport.SubscribeAsync(new TransportSubscription("DoubleNacker", [typeof(NackProbe)], "solo-double-nack-queue"),
            async (received, ct) =>
            {
                if (Interlocked.Increment(ref deliveries) == 1)
                {
                    await received.Ack.NackAsync(requeue: true, ct);
                    await received.Ack.NackAsync(requeue: true, ct);
                    return;
                }

                await received.Ack.AckAsync(ct);
                secondDeliveryTcs.TrySetResult();
            });

        await PublishProbeAsync(transport, Guid.NewGuid());

        await secondDeliveryTcs.Task.WaitAsync(Timeout);
        await Task.Delay(NoRedeliveryWindow);

        Assert.Equal(2, Volatile.Read(ref deliveries));
    }

    /// <summary>
    /// The 200-reply path gets the full contract, requeue included: by the time a reply reaches a
    /// subscriber it is already a plain deferred dispatch off the local channel -- no ambient reply
    /// collector, no HTTP response riding on its outcome -- so re-enqueuing it reproduces its original
    /// delivery exactly. Contrast the inbound-request path below, which cannot say the same.
    /// </summary>
    [Fact]
    public async Task NackWithRequeue_OnASyncReply_RedeliversTheReply()
    {
        var registry = new NodeRegistry();
        await using var receiver = await HttpTestNode.StartAsync("receiver.test", registry, _ => { });
        await using var sender = await HttpTestNode.StartAsync("sender.test", registry, o =>
        {
            o.Endpoints["receiver"] = "http://receiver.test";
            o.Routes[nameof(NackCommand)] = ["receiver"];
        });

        var receiverTransport = receiver.GetRequiredService<HttpMessageTransport>();
        var senderTransport = sender.GetRequiredService<HttpMessageTransport>();

        // NackReply has no route or local subscriber on the receiver side, so it is captured as this
        // handler's synchronous reply and comes back as the 200 body.
        await receiverTransport.SubscribeAsync(new TransportSubscription("ReplyingReceiver", [typeof(NackCommand)], "receiver-nack-command-queue"),
            async (received, ct) => await receiverTransport.PublishAsync(new NackReply("ok"),
                MessageEnvelope.From("ReplyingReceiver", received.CorrelationId, received.MessageId), ct));

        var deliveries = 0;
        var redeliveredTcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        await senderTransport.SubscribeAsync(new TransportSubscription("ReplyRequeuer", [typeof(NackReply)], "sender-nack-reply-queue"),
            async (received, ct) =>
            {
                if (Interlocked.Increment(ref deliveries) == 1)
                {
                    await received.Ack.NackAsync(requeue: true, ct);
                    return;
                }

                await received.Ack.AckAsync(ct);
                redeliveredTcs.TrySetResult(received);
            });

        var correlationId = Guid.NewGuid();
        await senderTransport.PublishAsync(new NackCommand("charge"), MessageEnvelope.New(correlationId));

        var redelivered = await redeliveredTcs.Task.WaitAsync(Timeout);
        Assert.Equal(correlationId, redelivered.CorrelationId);
        Assert.Equal(nameof(NackReply), redelivered.MessageTypeName);
    }

    /// <summary>
    /// The one path that deliberately does NOT honour requeue, so this pins the asymmetry rather than
    /// leaving it to a comment. An inbound request's delivery is inseparable from the HTTP request
    /// carrying it: it is dispatched inline under the ambient SyncReplyCollector, and the status and
    /// body this peer gets back are decided by that dispatch's outcome. A local re-enqueue would run a
    /// later, collector-less copy whose reply publish would throw unroutable instead of answering
    /// anyone -- a different delivery wearing the original's name. The peer that POSTed the message
    /// owns its retry, so both nack forms log at error and drop here.
    /// </summary>
    [Fact]
    public async Task NackWithRequeue_OnAnInboundHttpRequest_DropsBecauseThePeerOwnsTheRetry()
    {
        var registry = new NodeRegistry();
        await using var receiver = await HttpTestNode.StartAsync("receiver.test", registry, _ => { });
        await using var sender = await HttpTestNode.StartAsync("sender.test", registry, o =>
        {
            o.Endpoints["receiver"] = "http://receiver.test";
            o.Routes[nameof(NackProbe)] = ["receiver"];
        });

        var receiverTransport = receiver.GetRequiredService<HttpMessageTransport>();
        var senderTransport = sender.GetRequiredService<HttpMessageTransport>();

        var deliveries = 0;
        await receiverTransport.SubscribeAsync(new TransportSubscription("InboundRequeuer", [typeof(NackProbe)], "receiver-inbound-nack-queue"),
            async (received, ct) =>
            {
                Interlocked.Increment(ref deliveries);
                await received.Ack.NackAsync(requeue: true, ct);
            });

        // Returns only once the receiver's inline dispatch has completed and its 202 has been written,
        // so the nack has already happened -- and, unlike a local delivery, has had nothing to write to.
        await senderTransport.PublishAsync(new NackProbe("probe"), MessageEnvelope.New(Guid.NewGuid()));
        await Task.Delay(NoRedeliveryWindow);

        Assert.Equal(1, Volatile.Read(ref deliveries));
    }
}
