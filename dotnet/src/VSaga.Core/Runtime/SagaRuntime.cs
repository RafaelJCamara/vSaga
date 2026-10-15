using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Abstractions.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace VSaga.Core.Runtime;

/// <summary>
/// Singleton-safe facade over the per-message-scoped <see cref="SagaOrchestrator{TState}"/>. The
/// orchestrator is registered Scoped (not Singleton) because persistence providers like EF Core need
/// a fresh, short-lived DbContext per unit of work — so each handled message/timeout/retry opens its
/// own DI scope here rather than capturing one orchestrator instance for the app's lifetime.
/// </summary>
internal sealed class SagaRuntime<TState>(IServiceScopeFactory scopeFactory, ISagaDefinition<TState> definition) : ISagaRuntime
    where TState : SagaState, new()
{
    public string SagaType => definition.SagaType;

    public SagaKind Kind => definition.Kind;

    public TimeSpan? GetTimeout(string state) => definition.GetTimeout(state);

    public TimeSpan? ShortestTimeout => definition.ShortestTimeout;

    public TransportSubscription Subscription { get; } =
        new(definition.SagaType, definition.MessageTypes.ToList(), $"vsaga.saga.{definition.SagaType}");

    public async Task HandleReceivedAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SagaOrchestrator<TState>>().HandleAsync(message, cancellationToken);
    }

    public async Task HandleTimeoutAsync(SagaTimeout timeout, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SagaOrchestrator<TState>>().HandleTimeoutAsync(timeout, cancellationToken);
    }

    public async Task RetryAsync(Guid correlationId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SagaOrchestrator<TState>>().RetryAsync(correlationId, cancellationToken);
    }
}
