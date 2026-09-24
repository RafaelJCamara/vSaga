using System.Linq.Expressions;
using VSaga.Abstractions.Sagas;

namespace VSaga.Core.Dsl;

/// <summary>
/// Base class for a fluently-defined, orchestrated saga. Derive from this, declare states and
/// transitions in the constructor, and register the derived type via
/// <c>services.AddVSagaEngine(o => o.AddSaga&lt;TDefinition, TState&gt;())</c>.
/// </summary>
public abstract class OrchestratedSagaDefinition<TState> : ISagaDefinition<TState>
    where TState : SagaState, new()
{
    private readonly SagaDefinitionModel<TState> _model = new();
    private IReadOnlyCollection<Type>? _messageTypes;
    private IReadOnlyCollection<Type>? _initiatingMessageTypes;

    public string SagaType { get; }

    public SagaKind Kind => SagaKind.Orchestrated;

    protected OrchestratedSagaDefinition(string? sagaType = null)
    {
        SagaType = sagaType ?? GetType().Name;
    }

    public string InitialStateName =>
        _model.InitialStateName ?? throw new SagaDefinitionException($"Saga '{SagaType}' never declared an InitialState(...).");

    /// <summary>Computed once (the DSL registration table is immutable after the derived constructor runs) and cached.</summary>
    public IReadOnlyCollection<Type> MessageTypes =>
        _messageTypes ??= ComputeMessageTypes();

    /// <summary>Computed once (the DSL registration table is immutable after the derived constructor runs) and cached.</summary>
    public IReadOnlyCollection<Type> InitiatingMessageTypes =>
        _initiatingMessageTypes ??= ComputeInitiatingMessageTypes();

    private IReadOnlyCollection<Type> ComputeMessageTypes() =>
        _model.StepsByState.Values.SelectMany(byType => byType.Keys).Distinct().ToArray();

    private IReadOnlyCollection<Type> ComputeInitiatingMessageTypes() =>
        _model.StepsByState.TryGetValue(InitialStateName, out var steps) ? steps.Keys.ToArray() : [];

    public bool CanInitiate(Type messageType) => InitiatingMessageTypes.Contains(messageType);

    /// <summary>Declares the state a brand new saga instance starts in.</summary>
    protected State<TState> InitialState(string name)
    {
        if (_model.InitialStateName is not null)
            throw new SagaDefinitionException($"Saga '{SagaType}' already declared InitialState '{_model.InitialStateName}'.");

        _model.InitialStateName = name;
        return new State<TState>(name);
    }

    protected static State<TState> State(string name) => new(name);

    protected StateBuilder<TState> During(params State<TState>[] states) =>
        new(_model, states.Select(s => s.Name).ToArray());

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
    /// extractor, letting the orchestrator find this saga by business key when the transport correlation
    /// id misses. A saga that never calls this is unaffected — every existing <c>CorrelateBy</c> call site
    /// keeps its original behaviour (assign onto state, nothing else). Calling it is exclusive, though: a
    /// <c>CorrelateBy</c> targeting any other property then throws <see cref="SagaDefinitionException"/>
    /// from this constructor, as does declaring <c>CorrelateOn</c> a second time.
    /// </summary>
    protected void CorrelateOn(Expression<Func<TState, object?>> selector)
    {
        _model.Correlation.DeclareBusinessKey(CorrelationSelector.ResolveProperty(selector, SagaType));
    }

    public string? TryGetCorrelationKey(object message) => _model.Correlation.TryExtract(message);

    public TimeSpan? GetTimeout(string forState) =>
        _model.Timeouts.TryGetValue(forState, out var timeout) ? timeout.Delay : null;

    public async Task<SagaStepOutcome> HandleAsync(ISagaContext<TState> context, object message, CancellationToken cancellationToken)
    {
        var fromState = context.Saga.CurrentState;
        var messageType = message.GetType();

        if (!_model.StepsByState.TryGetValue(fromState, out var steps) || !steps.TryGetValue(messageType, out var step))
        {
            if (_model.UnhandledEventPolicy == UnhandledEventPolicy.Throw)
                throw new InvalidOperationException($"Saga '{SagaType}' has no handler for {messageType.Name} while in state '{fromState}'.");

            return SagaStepOutcome.Unhandled(fromState);
        }

        await ExecuteStepAsync(step, context, message, cancellationToken);

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

        await ExecuteStepAsync(timeout.Step, context, TimeoutSignal.Instance, cancellationToken);

        var toState = timeout.Step.ResolveTargetState(context.Saga, forState);
        context.Saga.CurrentState = toState;

        var finalStatus = timeout.Step.ResolveFinalStatus(context.Saga);
        if (finalStatus is { } status)
            context.Saga.Status = status;

        return new SagaStepOutcome(true, forState, toState, finalStatus);
    }

    private static Task ExecuteStepAsync(StepDefinition<TState> step, ISagaContext<TState> context, object message, CancellationToken cancellationToken) =>
        StepExecutor.RunAsync(step, context, message, cancellationToken);
}
