using System.Net;
using VSaga.Abstractions.Persistence;

namespace VSaga.Http.Tests;

/// <summary>
/// The SagaContext log sink stamps the step's inbound message id as the causation id of an entry that
/// names none (docs/design/dashboard-usability-and-access.md §6.6). For a .CallHttp step that is the
/// request entry, so the dashboard can attach it to the step that made the call; the reply already
/// names its cause (the request's own id, which is what the map stitches on) and keeps it.
/// </summary>
public sealed class CallHttpCausationTests
{
    [Fact]
    public async Task CallHttpRequest_CarriesTheInboundMessageId_AndTheReplyKeepsTheRequestAsItsCause()
    {
        await using var harness = CallHttpTestHarness.Create(_ =>
            StubHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"body":"charged"}"""));

        await harness.WhenAsync(new BeginHttpCall("REQ-CAUSE-1"));
        await harness.AssertStateAsync(harness.Saga.Succeeded);

        var timeline = await harness.GetTimelineAsync();
        var started = Assert.Single(timeline, e => e.EntryType == SagaEntryType.SagaStarted);
        Assert.NotNull(started.MessageId);

        var request = Assert.Single(timeline, e => e.EntryType == SagaEntryType.MessagePublished
            && e.MessageType is { } type && type.StartsWith("POST ", StringComparison.Ordinal));
        Assert.Equal(started.MessageId, request.CausationId);

        var reply = Assert.Single(timeline, e => e.EntryType == SagaEntryType.MessageReceived
            && string.Equals(e.SourceService, "call-target.test/charge", StringComparison.Ordinal));
        Assert.NotNull(request.MessageId);
        Assert.Equal(request.MessageId, reply.CausationId);
    }
}
