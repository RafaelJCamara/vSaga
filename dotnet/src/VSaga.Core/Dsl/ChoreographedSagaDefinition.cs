using System.Linq.Expressions;
using VSaga.Abstractions.Sagas;

namespace VSaga.Core.Dsl;

/// <summary>
/// Base class for a fluently-defined, choreographed saga. Derive from this, declare the events it
/// reacts to in the constructor, and register the derived type via the same
/// <c>services.AddVSagaEngine(o => o.AddSaga&lt;TDefinition, TState&gt;())</c> call used for orchestrated
/// sagas — the runtime (<c>SagaOrchestrator&lt;TState&gt;</c>, persistence, timeouts, the dashboard) only
/// ever depends on <see cref="ISagaDefinition{TState}"/>, so it needs no changes to drive either kind.
///
/// The structural difference from <see cref="OrchestratedSagaDefinition{TState}"/> is what a
/// choreography actually is: there is no central conductor commanding "what happens next", so handlers
/// are registered per event type only (<c>On&lt;TEvent&gt;()</c>), not gated by the instance's current
/// recorded state the way <c>During(state).When&lt;TEvent&gt;()</c> gates orchestration's steps. Any
/// registered event can be observed while the saga is in any state, because independent participants —
/// not this definition — decide what to publish and when. <c>RecordState(...)</c> only ever labels the
/// latest milestone for the dashboard/timeline and for keying <c>Compensate</c>/<c>WithTimeout</c>; it
/// never gates dispatch. Likewise, more than one event type can <c>StartsNewInstance()</c>, since
/// choreography has no single designated first step — whichever participant happens to publish first
/// is the one that starts tracking.
/// </summary>
public abstract class ChoreographedSagaDefinition<TState> : ISagaDefinition<TState>
    where TState : SagaState, new()
{
    private readonly ChoreographySagaModel<TState> _model = new();
    private IReadOnlyCollection<Type>? _messageTypes;

    public string SagaType { get; }

    public SagaKind Kind => SagaKind.Choreographed;

    protected ChoreographedSagaDefinition(string? sagaType = null)
    {
        SagaType = sagaType ?? GetType().Name;
    }

    public string InitialStateName =>
        _model.InitialStateName ?? throw new SagaDefinitionException($"Saga '{SagaType}' never declared an InitialState(...).");

    /// <summary>Computed once (the DSL registration table is immutable after the derived constructor runs) and cached.</summary>
    public IReadOnlyCollection<Type> MessageTypes =>
        _messageTypes ??= _model.StepsByMessageType.Keys.ToArray();

    public IReadOnlyCollection<Type> InitiatingMessageTypes => _model.InitiatingMessageTypes;

    public bool CanInitiate(Type messageType) => _model.InitiatingMessageTypes.Contains(messageType);

    /// <summary>
    /// Declares the label a brand new saga instance starts in, before any event's <c>RecordState(...)</c>
    /// has run. A choreography still needs exactly one of these (same as orchestration) because the
    /// engine seeds every new instance's <c>CurrentState</c> from it up front, before dispatching the
    /// initiating event — which event actually created the instance is whichever <c>StartsNewInstance()</c>
    /// type was observed, not this label.
    /// </summary>
    protected State<TState> InitialState(string name)
    {
        if (_model.InitialStateName is not null)
            throw new SagaDefinitionException($"Saga '{SagaType}' already declared InitialState '{_model.InitialStateName}'.");

        _model.InitialStateName = name;
        return new State<TState>(name);
    }

    protected static State<TState> State(string name) => new(name);

    /// <summary>Registers a reaction to one event type, independent of the saga's current recorded state.</summary>
    protected ChoreographyEventBuilder<TState, TMessage> On<TMessage>() where TMessage : notnull
    {
        var step = new StepDefinition<TState>(typeof(TMessage));
        _model.AddStep(step);

        return new ChoreographyEventBuilder<TState, TMessage>(_model, step);
    }

    /// <summary>Registers compensation for a state that ran; invoked (most-recently-visited first) by <c>.Compensate()</c>.</summary>
    protected void Compensate(State<TState> forState, Func<ISagaContext<TState>, CancellationToken, Task> action) =>
        _model.Compensations[forState.Name] = action;

    protected void WithTimeout(State<TState> state, TimeSpan after, Action<TimeoutBuilder<TState>> configure)
    {
        var builder = new TimeoutBuilder<TState>(_model.RunCompensationAsync);
        configure(builder);
        _model.Timeouts[state.Name] = (after, builder.Step);
    }

    protected void OnUnhandledEvent(UnhandledEventPolicy policy) => _model.UnhandledEventPolicy = policy;

    /// <summary>
    /// Declares which state property is this saga type's business key. Arms correlation: a
    /// <c>CorrelateBy</c> targeting the same property additionally registers as that message type's key
    /// extractor. A saga that never calls this is unaffected.
    /// </summary>
    protected void CorrelateOn(Expression<Func<TState, object?>> selector)
    {
        _model.Correlation.DeclareBusinessKey(CorrelationSelector.ResolveProperty(selector, SagaType));
    }

    public string? TryGetCorrelationKey(object message) => _model.Correlation.TryExtract(message);

    public TimeSpan? GetTimeout(string forState) =>
        _model.Timeouts.TryGetValue(forState, out var timeout) ? timeout.Delay : null;

    public TimeSpan? ShortestTimeout =>
        _model.Timeouts.Count == 0 ? null : _model.Timeouts.Values.Min(t => t.Delay);

    public async Task<SagaStepOutcome> HandleAsync(ISagaContext<TState> context, object message, CancellationToken cancellationToken)
    {
        var fromState = context.Saga.CurrentState;
        var messageType = message.GetType();

        if (!_model.StepsByMessageType.TryGetValue(messageType, out var step))
        {
            if (_model.UnhandledEventPolicy == UnhandledEventPolicy.Throw)
                throw new InvalidOperationException($"Saga '{SagaType}' has no handler registered for {messageType.Name}.");

            return SagaStepOutcome.Unhandled(fromState);
        }

        await StepExecutor.RunAsync(step, context, message, cancellationToken);

        var toState = step.ResolveTargetState(context.Saga, fromState);
        context.Saga.CurrentState = toState;

        // Resolved after the step's actions have run, so a selector sees the state they just wrote —
        // that is what lets the last of several independent events be the one that completes the saga.
        var finalStatus = step.ResolveFinalStatus(context.Saga);
        if (finalStatus is { } status)
            context.Saga.Status = status;

        return new SagaStepOutcome(true, fromState, toState, finalStatus);
    }

    public async Task<SagaStepOutcome> HandleTimeoutAsync(ISagaContext<TState> context, string forState, CancellationToken cancellationToken)
    {
        if (!_model.Timeouts.TryGetValue(forState, out var timeout))
            return SagaStepOutcome.Unhandled(forState);

        await StepExecutor.RunAsync(timeout.Step, context, TimeoutSignal.Instance, cancellationToken);

        var toState = timeout.Step.ResolveTargetState(context.Saga, forState);
        context.Saga.CurrentState = toState;

        var finalStatus = timeout.Step.ResolveFinalStatus(context.Saga);
        if (finalStatus is { } status)
            context.Saga.Status = status;

        return new SagaStepOutcome(true, forState, toState, finalStatus);
    }
}
