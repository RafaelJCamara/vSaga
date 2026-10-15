using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Abstractions.Transport;

namespace VSaga.Core.Runtime;

/// <summary>Non-generic facade over <see cref="SagaOrchestrator{TState}"/> so the hosted services can hold a heterogeneous collection across saga TState types.</summary>
internal interface ISagaRuntime
{
    string SagaType { get; }

    SagaKind Kind { get; }

    /// <summary>The definition's timeout for <paramref name="state"/>, or null when it declares none; the stuck-saga sweep reads it to classify an idle saga.</summary>
    TimeSpan? GetTimeout(string state);

    /// <summary>The shortest timeout any state declares, or null (see <see cref="ISagaDefinition{TState}.ShortestTimeout"/>).</summary>
    TimeSpan? ShortestTimeout { get; }

    TransportSubscription Subscription { get; }

    Task HandleReceivedAsync(ReceivedMessage message, CancellationToken cancellationToken);

    Task HandleTimeoutAsync(SagaTimeout timeout, CancellationToken cancellationToken);

    Task RetryAsync(Guid correlationId, CancellationToken cancellationToken);
}
