using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Core.Runtime;

namespace VSaga.Core.Dsl;

/// <summary>
/// Walks a saga's visited states (most-recently-visited first) running whichever registered
/// compensation delegates apply, logging start/success/failure for each. Shared by
/// <see cref="OrchestratedSagaDefinition{TState}"/> and <see cref="ChoreographedSagaDefinition{TState}"/>
/// (via their own per-kind compensation dictionaries) so both DSLs' <c>.Compensate()</c> gets the exact
/// same one-failing-compensation-doesn't-abandon-the-rest semantics.
/// </summary>
internal static class CompensationRunner
{
    public static async Task RunAsync<TState>(
        IReadOnlyDictionary<string, Func<ISagaContext<TState>, CancellationToken, Task>> compensations,
        ISagaContext<TState> context,
        IEnumerable<string> statesMostRecentFirst,
        CancellationToken cancellationToken)
        where TState : SagaState, new()
    {
        var statesToCompensate = statesMostRecentFirst.Where(compensations.ContainsKey).ToList();
        if (statesToCompensate.Count == 0)
            return;

        var log = (ISagaContextLogSink)context;
        var sagaType = context.Saga.SagaType;

        await log.LogAsync(SagaLogEntry.Create(context.CorrelationId, sagaType, SagaEntryType.CompensationStarted), cancellationToken);

        foreach (var stateName in statesToCompensate)
            await RunOneAsync(compensations, log, context, sagaType, stateName, cancellationToken);
    }

    /// <summary>
    /// Runs one state's compensation delegate, logging its outcome. Failures are caught (not
    /// propagated) so one failing compensation doesn't abandon the rest — e.g. a failed refund
    /// shouldn't stop the inventory release that follows it — the failure is still fully visible in the
    /// timeline via CompensationStepFailed.
    /// </summary>
    private static async Task RunOneAsync<TState>(
        IReadOnlyDictionary<string, Func<ISagaContext<TState>, CancellationToken, Task>> compensations,
        ISagaContextLogSink log,
        ISagaContext<TState> context,
        string sagaType,
        string stateName,
        CancellationToken cancellationToken)
        where TState : SagaState, new()
    {
        try
        {
            await compensations[stateName](context, cancellationToken);
            await log.LogAsync(SagaLogEntry.Create(context.CorrelationId, sagaType, SagaEntryType.CompensationStepSucceeded, fromState: stateName), cancellationToken);
        }
        catch (Exception ex)
        {
            await log.LogAsync(SagaLogEntry.Create(context.CorrelationId, sagaType, SagaEntryType.CompensationStepFailed, fromState: stateName, errorMessage: SagaErrorText.Describe(ex)), cancellationToken);
        }
    }
}
