using VSaga.Abstractions.Notifications;
using VSaga.Abstractions.Sagas;
using VSaga.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VSaga.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the VSaga saga engine: the hosted service that subscribes registered sagas to the
    /// configured <c>IMessageTransport</c>, the timeout dispatcher, and (if nothing else registered
    /// one already) a no-op <see cref="ISagaChangeNotifier"/>. Requires an <c>IMessageTransport</c>
    /// and, per registered saga TState, an <c>ISagaSnapshotStore&lt;TState&gt;</c> to already be
    /// registered (e.g. via AddVSagaInMemoryPersistence/AddVSagaEfCore and AddVSagaInMemoryTransport/AddVSagaRabbitMq).
    /// </summary>
    public static IServiceCollection AddVSagaEngine(this IServiceCollection services, Action<SagaEngineBuilder> configure)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SagaOrchestratorOptions>();
        services.TryAddSingleton<SagaOutboxOptions>();
        services.TryAddSingleton<ISagaChangeNotifier>(NullSagaChangeNotifier.Instance);
        services.AddSingleton<ISagaRetryDispatcher, SagaRetryDispatcher>();
        services.AddHostedService<SagaEngineHostedService>();
        services.AddHostedService<SagaTimeoutDispatcherHostedService>();
        services.AddHostedService<SagaOutboxDispatcherHostedService>();

        configure(new SagaEngineBuilder(services));

        return services;
    }
}

public sealed class SagaEngineBuilder(IServiceCollection services)
{
    /// <summary>
    /// Registers a saga definition (a singleton, stateless DSL/model shared across all of that saga
    /// type's instances) and wires it into the engine. <see cref="SagaOrchestrator{TState}"/> is
    /// registered Scoped — not Singleton — because it depends on persistence stores that may need a
    /// short-lived unit of work (e.g. EF Core's DbContext); <see cref="SagaRuntime{TState}"/> opens a
    /// fresh scope per message/timeout/retry so a Singleton hosted service can still drive it safely.
    /// </summary>
    public SagaEngineBuilder AddSaga<TDefinition, TState>()
        where TDefinition : class, ISagaDefinition<TState>
        where TState : SagaState, new()
    {
        // Two definitions sharing one TState cannot both work: the engine resolves a saga's definition
        // as ISagaDefinition<TState>, so the second registration silently wins and the first saga never
        // runs — no error, its messages simply go nowhere. Caught here at startup instead, because the
        // symptom otherwise shows up as an inexplicably missing saga at runtime. Give each saga its own
        // state class, even if the two would be structurally identical.
        if (services.Any(d => d.ServiceType == typeof(ISagaDefinition<TState>)))
        {
            throw new SagaDefinitionException(
                $"A saga is already registered with state type '{typeof(TState).Name}'; '{typeof(TDefinition).Name}' cannot share it. " +
                "Each saga definition needs its own TState, since the engine resolves definitions by state type.");
        }

        services.AddSingleton<TDefinition>();
        services.AddSingleton<ISagaDefinition<TState>>(sp => sp.GetRequiredService<TDefinition>());
        services.AddScoped<SagaOrchestrator<TState>>();
        services.AddSingleton<ISagaRuntime, SagaRuntime<TState>>();

        return this;
    }

    /// <summary>
    /// Configures engine-wide orchestrator behavior: <see cref="SagaOrchestratorOptions.MaxDeliveryAttempts"/>
    /// and the state snapshots (<see cref="SagaOrchestratorOptions.RecordStateSnapshots"/>,
    /// <see cref="SagaOrchestratorOptions.MaxStateSnapshotBytes"/>,
    /// <see cref="SagaOrchestratorOptions.MaxStateSnapshotBytesPerSaga"/>,
    /// <see cref="SagaOrchestratorOptions.StateSnapshotTimeout"/>). A host can bind a configuration
    /// section here, e.g. <c>ConfigureOrchestrator(o =&gt; configuration.GetSection("Orchestrator").Bind(o))</c>.
    /// <c>AddVSagaEngine</c> already
    /// registers a default-valued <see cref="SagaOrchestratorOptions"/> via <c>TryAddSingleton</c>
    /// before this delegate runs, so calling this here overrides it -- order relative to
    /// <see cref="AddSaga{TDefinition, TState}"/> doesn't matter, only that it's called somewhere
    /// inside the same <c>AddVSagaEngine(...)</c> delegate.
    /// </summary>
    public SagaEngineBuilder ConfigureOrchestrator(Action<SagaOrchestratorOptions> configure)
    {
        var options = new SagaOrchestratorOptions();
        configure(options);
        services.AddSingleton(options);
        return this;
    }

    /// <summary>
    /// Configures the outbox dispatcher (<see cref="SagaOutboxOptions.Mode"/>,
    /// <see cref="SagaOutboxOptions.PollInterval"/>, <see cref="SagaOutboxOptions.BatchSize"/>). See
    /// <see cref="ConfigureOrchestrator"/> for why this, not <c>services.Configure&lt;T&gt;</c>, is
    /// the right way to set these.
    /// </summary>
    public SagaEngineBuilder ConfigureOutbox(Action<SagaOutboxOptions> configure)
    {
        var options = new SagaOutboxOptions();
        configure(options);
        services.AddSingleton(options);
        return this;
    }
}
