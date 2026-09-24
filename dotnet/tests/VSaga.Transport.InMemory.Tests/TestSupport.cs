using VSaga.Abstractions.Transport;

namespace VSaga.Transport.InMemory.Tests;

internal sealed record PingMessage(string Text);

/// <summary>Counts invocations and passes through, so a test can prove the pipeline actually wraps the in-memory transport rather than asserting on a side effect a middleware may or may not have.</summary>
internal sealed class RecordingOutboundMiddleware : IOutboundMessageMiddleware
{
    public int InvokeCount { get; private set; }

    public string? LastDestinationHint { get; private set; }

    public Task InvokeAsync(OutboundMessageContext context, Func<OutboundMessageContext, Task> nextAsync)
    {
        InvokeCount++;
        LastDestinationHint = context.DestinationHint;
        return nextAsync(context);
    }
}

/// <summary>Inbound counterpart to <see cref="RecordingOutboundMiddleware"/>; can also suppress delivery, the one behaviour a silently-unwrapped transport would make impossible.</summary>
internal sealed class RecordingInboundMiddleware(bool suppress = false) : IInboundMessageMiddleware
{
    public int InvokeCount { get; private set; }

    public Task InvokeAsync(InboundMessageContext context, Func<InboundMessageContext, Task> nextAsync)
    {
        InvokeCount++;
        context.Suppressed = suppress;
        return nextAsync(context);
    }
}
