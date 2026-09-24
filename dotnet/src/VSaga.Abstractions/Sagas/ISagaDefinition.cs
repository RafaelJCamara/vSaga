namespace VSaga.Abstractions.Sagas;

/// <summary>
/// Describes a saga: its identity, the message types it reacts to, and how it reacts to them.
/// Implemented by the fluent DSL base classes in VSaga.Core; consumed by the orchestrator/engine
/// and by anything (e.g. the dashboard) that needs to introspect saga metadata without depending on Core.
/// </summary>
public interface ISagaDefinition<TState> where TState : SagaState, new()
{
    string SagaType { get; }

    SagaKind Kind { get; }

    /// <summary>The state a brand new saga instance starts in.</summary>
    string InitialStateName { get; }

    /// <summary>All message CLR types this saga has a handler registered for, in any state.</summary>
    IReadOnlyCollection<Type> MessageTypes { get; }

    /// <summary>
    /// Message types that can start a brand new saga instance. An orchestrated definition derives these
    /// from the handlers registered under <see cref="InitialStateName"/>; a choreographed one from its
    /// explicit <c>StartsNewInstance()</c> declarations, which are not tied to any state.
    /// </summary>
    IReadOnlyCollection<Type> InitiatingMessageTypes { get; }

    bool CanInitiate(Type messageType);

    /// <summary>
    /// Runs the registered handler (if any) for <paramref name="message"/> against the saga's current
    /// state. Mutates <paramref name="context"/>.Saga in place (CurrentState/Status/business fields)
    /// and may call context.PublishAsync/SendAsync as side effects. Throws if the step's action fails
    /// after any configured step-level retries are exhausted; the orchestrator is responsible for
    /// marking the saga Failed and persisting in that case.
    /// </summary>
    Task<SagaStepOutcome> HandleAsync(ISagaContext<TState> context, object message, CancellationToken cancellationToken);

    /// <summary>Runs the timeout handler registered for <paramref name="forState"/>, if any.</summary>
    Task<SagaStepOutcome> HandleTimeoutAsync(ISagaContext<TState> context, string forState, CancellationToken cancellationToken);

    /// <summary>Timeout duration configured for a state, if any (used to schedule a timeout on entry).</summary>
    TimeSpan? GetTimeout(string forState);

    /// <summary>
    /// Extracts <paramref name="message"/>'s business-key correlation value, if this saga declared one via
    /// <c>CorrelateOn(...)</c> and registered a <c>CorrelateBy</c> extractor for this message's CLR type.
    /// Null means this message carries no business key — the caller should fall back to the transport
    /// correlation id, which is every saga's behaviour today.
    /// </summary>
    string? TryGetCorrelationKey(object message);
}
