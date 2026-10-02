namespace VSaga.Core.Runtime;

/// <summary>
/// Public entry point to the in-process manual retry, which redrives the message of a Failed saga's last
/// StepFailed, without depending on any single saga's generic TState. The dashboard's Retry does not go
/// through it: that resets the saga to the failed step's from-state and republishes the step's message.
/// </summary>
public interface ISagaRetryDispatcher
{
    Task RetryAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default);
}

internal sealed class SagaRetryDispatcher(IEnumerable<ISagaRuntime> runtimes) : ISagaRetryDispatcher
{
    public Task RetryAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default)
    {
        var runtime = runtimes.FirstOrDefault(r => string.Equals(r.SagaType, sagaType, StringComparison.Ordinal))
                      ?? throw new InvalidOperationException($"No saga is registered with type '{sagaType}'.");

        return runtime.RetryAsync(correlationId, cancellationToken);
    }
}
