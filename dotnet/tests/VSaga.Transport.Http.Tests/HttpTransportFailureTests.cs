using System.Net;
using System.Net.Http.Headers;
using VSaga.Abstractions.Transport;

namespace VSaga.Transport.Http.Tests;

/// <summary>
/// The ways a remote endpoint can fail a publish that is otherwise perfectly routable, plus the
/// wildcard route end to end. All the failures here are non-unroutable: the route existed and was
/// used, so a saga's retry/compensation path is the right response, whereas <c>IsUnroutable</c>
/// means the message had nowhere to go at all and retrying the same publish can only fail the same
/// way.
///
/// Split out from <see cref="HttpTransportTests"/> because these nodes talk to a canned responder
/// (NodeRegistry.RegisterStub) rather than to another real vSaga node.
/// </summary>
public sealed class HttpTransportFailureTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static async Task<HttpMessageTransport> SenderToStubAsync(
        NodeRegistry registry,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        Action<HttpTransportOptions>? configureOptions = null)
    {
        registry.RegisterStub("failing.test", respond);

        var sender = await HttpTestNode.StartAsync("sender.test", registry, o =>
        {
            o.Endpoints["receiver"] = "http://failing.test";
            o.Routes["PingMessage"] = ["receiver"];
            configureOptions?.Invoke(o);
        });

        return sender.GetRequiredService<HttpMessageTransport>();
    }

    [Fact]
    public async Task NonSuccessResponse_RejectsThePublishNamingTheStatus()
    {
        var registry = new NodeRegistry();
        var sender = await SenderToStubAsync(registry, (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var exception = await Assert.ThrowsAsync<MessageTransportPublishException>(() =>
            sender.PublishAsync(new PingMessage("boom"), MessageEnvelope.New(Guid.NewGuid())));

        Assert.False(exception.IsUnroutable);
        Assert.Equal(nameof(PingMessage), exception.MessageTypeName);
        Assert.Contains("500", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FourHundredResponse_RejectsTheSameWay_AnyNonSuccessIsAFailedPublishNotASilentDrop()
    {
        var registry = new NodeRegistry();
        var sender = await SenderToStubAsync(registry, (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var exception = await Assert.ThrowsAsync<MessageTransportPublishException>(() =>
            sender.PublishAsync(new PingMessage("nope"), MessageEnvelope.New(Guid.NewGuid())));

        Assert.False(exception.IsUnroutable);
    }

    [Fact]
    public async Task RequestOutlivingRequestTimeout_IsCancelledRatherThanHangingForever()
    {
        var registry = new NodeRegistry();

        // Never returns on its own: without the transport's own RequestTimeout this publish would
        // hang for as long as the connection stayed open, stalling the saga step that issued it with
        // no error and no timeout of its own.
        var sender = await SenderToStubAsync(registry,
            async (_, cancellationToken) =>
            {
                await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            },
            o => o.RequestTimeout = TimeSpan.FromMilliseconds(150));

        var publish = sender.PublishAsync(new PingMessage("hangs"), MessageEnvelope.New(Guid.NewGuid()));
        var exception = await Assert.ThrowsAsync<MessageTransportPublishException>(() => publish.WaitAsync(Timeout));

        Assert.False(exception.IsUnroutable);
        Assert.Contains("timed out", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoHundredMissingTheReservedHeaders_RejectsThePublish()
    {
        var registry = new NodeRegistry();

        // A 200 IS the reply on this transport, so one without the reserved headers cannot be
        // dispatched to anything -- surfacing it beats enqueuing a message with no type or correlation.
        var sender = await SenderToStubAsync(registry, (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"Text":"a reply with no envelope headers"}""",
                    System.Text.Encoding.UTF8, "application/json"),
            }));

        var exception = await Assert.ThrowsAsync<MessageTransportPublishException>(() =>
            sender.PublishAsync(new PingMessage("headerless"), MessageEnvelope.New(Guid.NewGuid())));

        Assert.False(exception.IsUnroutable);
        Assert.Contains("missing one of the required x-vsaga- headers", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoHundredWithAnUnparseableCorrelationId_RejectsThePublish()
    {
        var registry = new NodeRegistry();
        var sender = await SenderToStubAsync(registry, (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            };
            response.Headers.TryAddWithoutValidation(HttpMessageTransport.MessageTypeHeader, "PingReply");
            response.Headers.TryAddWithoutValidation(HttpMessageTransport.MessageIdHeader, Guid.NewGuid().ToString("N"));
            response.Headers.TryAddWithoutValidation(HttpMessageTransport.CorrelationIdHeader, "not-a-guid");
            return Task.FromResult(response);
        });

        var exception = await Assert.ThrowsAsync<MessageTransportPublishException>(() =>
            sender.PublishAsync(new PingMessage("bad correlation"), MessageEnvelope.New(Guid.NewGuid())));

        Assert.False(exception.IsUnroutable);
    }

    [Fact]
    public async Task ConnectionFailure_RejectsThePublishNamingTheCause()
    {
        var registry = new NodeRegistry();

        // What HttpClient surfaces for a refused TCP connection. The transport has to turn it into
        // something that names the cause rather than repeating HttpRequestException's own
        // platform-specific sentence.
        var sender = await SenderToStubAsync(registry, (_, _) =>
            Task.FromException<HttpResponseMessage>(
                new HttpRequestException(HttpRequestError.ConnectionError, "target machine actively refused it")));

        var exception = await Assert.ThrowsAsync<MessageTransportPublishException>(() =>
            sender.PublishAsync(new PingMessage("nobody home"), MessageEnvelope.New(Guid.NewGuid())));

        Assert.False(exception.IsUnroutable);
        Assert.Contains("connection refused", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wildcard route end to end, against a real node rather than a stub: the shape an ops/redrive
    /// process uses when it pushes every message type at one saga host rather than enumerating them.
    /// </summary>
    [Fact]
    public async Task Publish_WithNoExplicitRouteForTheType_FallsBackToTheWildcardEndpoint()
    {
        var registry = new NodeRegistry();
        await using var receiver = await HttpTestNode.StartAsync("receiver.test", registry, _ => { });
        await using var sender = await HttpTestNode.StartAsync("sender.test", registry, o =>
        {
            o.Endpoints["hub"] = "http://receiver.test";
            o.Routes[ConfigHttpRouteTable.WildcardRoute] = ["hub"];
        });

        var receiverTransport = receiver.GetRequiredService<HttpMessageTransport>();
        var senderTransport = sender.GetRequiredService<HttpMessageTransport>();

        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await receiverTransport.SubscribeAsync(new TransportSubscription("WildcardConsumer", [typeof(PingMessage)], "receiver-wildcard-queue"),
            (received, _) => { tcs.TrySetResult(received); return Task.CompletedTask; });

        var correlationId = Guid.NewGuid();
        await senderTransport.PublishAsync(new PingMessage("wildcard"), MessageEnvelope.New(correlationId));

        var received = await tcs.Task.WaitAsync(Timeout);
        Assert.Equal(correlationId, received.CorrelationId);
        Assert.Equal(nameof(PingMessage), received.MessageTypeName);
    }
}
