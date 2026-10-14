using System.Net;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;

namespace VSaga.Http.Tests;

/// <summary>
/// docs/design/http-based-sagas.md §5.4's mapping table: 2xx -> success message, an explicit status -> its own
/// mapped message, 5xx -> failure message, and a network-level failure/timeout (no response at all) ->
/// failure message too. Every one of these runs through VSaga.Testing's SagaTestHarness, which drives
/// the real publish -> receive -> orchestrator path over InMemoryMessageTransport -- and every one of
/// them only passes because .CallHttp's loopback uses PublishAfterCommitAsync rather than PublishAsync:
/// InMemoryMessageTransport dispatches synchronously and re-entrantly, so a plain PublishAsync loopback
/// would re-enter this saga while the CallHttp step is still executing, before it has transitioned out
/// of Start -- the mapped message would arrive for a (Start, HttpCallSucceeded) pair nothing handles,
/// get logged as UnexpectedEvent, and the saga would never leave AwaitingResult. See
/// CallHttpOrderingMutationTests for the test that pins this by actually reverting the fix.
/// </summary>
public sealed class CallHttpMappingTests
{
    [Fact]
    public async Task TwoHundredResponse_MapsToTheConfiguredOnSuccessMessage()
    {
        await using var harness = CallHttpTestHarness.Create(_ =>
            StubHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"body":"charged"}"""));

        await harness.WhenAsync(new BeginHttpCall("REQ-1"));

        var state = await harness.AssertStateAsync(harness.Saga.Succeeded);
        Assert.Equal(SagaStatus.Completed, state.Status);
        Assert.Equal("charged", state.Result);
    }

    [Fact]
    public async Task ExplicitStatus_MapsToItsOwnConfiguredMessage()
    {
        await using var harness = CallHttpTestHarness.Create(_ =>
            StubHttpMessageHandler.JsonResponse((HttpStatusCode)402, """{"reason":"card_declined"}"""));

        await harness.WhenAsync(new BeginHttpCall("REQ-2"));

        var state = await harness.AssertStateAsync(harness.Saga.Declined);
        Assert.Equal(SagaStatus.Failed, state.Status);
        Assert.Equal("card_declined", state.Result);
    }

    [Fact]
    public async Task FiveHundredResponse_MapsToTheConfiguredOnFailureMessage()
    {
        await using var harness = CallHttpTestHarness.Create(_ =>
            StubHttpMessageHandler.JsonResponse(HttpStatusCode.InternalServerError, """{"reason":"gateway_down"}"""));

        await harness.WhenAsync(new BeginHttpCall("REQ-3"));

        var state = await harness.AssertStateAsync(harness.Saga.Failed);
        Assert.Equal(SagaStatus.Failed, state.Status);
        Assert.Equal("gateway_down", state.Result);
    }

    [Fact]
    public async Task NetworkFailure_WithNoResponseAtAll_AlsoMapsToTheConfiguredOnFailureMessage()
    {
        await using var harness = CallHttpTestHarness.Create(_ => throw new HttpRequestException("connection refused"));

        await harness.WhenAsync(new BeginHttpCall("REQ-4"));

        var state = await harness.AssertStateAsync(harness.Saga.Failed);
        Assert.Equal(SagaStatus.Failed, state.Status);
        Assert.Null(state.Result); // deserialized from "{}" -- no body to hydrate a reason from
    }

    [Fact]
    public async Task NetworkFailure_RecordsTheInnermostCause_OnTheReplyEntry()
    {
        // HttpClient wraps the socket error: the outer message alone never says what went wrong.
        await using var harness = CallHttpTestHarness.Create(_ =>
            throw new HttpRequestException("An error occurred while sending the request.", new IOException("No connection could be made because the target machine actively refused it.")));

        await harness.WhenAsync(new BeginHttpCall("REQ-4B"));

        var timeline = await harness.GetTimelineAsync();
        var reply = Assert.Single(timeline, e => e.EntryType == SagaEntryType.MessageReceived && e.ErrorMessage is not null);
        Assert.Equal("An error occurred while sending the request. Cause: No connection could be made because the target machine actively refused it.", reply.ErrorMessage);
    }

    [Fact]
    public async Task Timeout_AlsoMapsToTheConfiguredOnFailureMessage()
    {
        await using var harness = CallHttpTestHarness.Create(_ => throw new TaskCanceledException("request timed out"));

        await harness.WhenAsync(new BeginHttpCall("REQ-5"));

        var state = await harness.AssertStateAsync(harness.Saga.Failed);
        Assert.Equal(SagaStatus.Failed, state.Status);
    }
}
