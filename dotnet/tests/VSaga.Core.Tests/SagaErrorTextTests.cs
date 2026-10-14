using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Abstractions.Transport;
using VSaga.Core.Dsl;
using VSaga.Core.Runtime;
using VSaga.Persistence.InMemory;
using VSaga.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VSaga.Core.Tests;

/// <summary>
/// The timeline's error text names the cause of a failure, not just the exception that wrapped it. EF Core's stores
/// map every <c>DbUpdateException</c> to <see cref="SagaAlreadyExistsException"/>, so a transient database error
/// was recorded in a <c>DeliveryExhausted</c> entry as "already exists" (live run of 2026-10-13/14, Postgres
/// trigger that raised in the instance UPDATE); the cause is only the innermost exception.
/// </summary>
public sealed class SagaErrorTextTests
{
    // ---- the helper ----

    [Fact]
    public void Describe_AnExceptionWithoutAnInnerOne_IsItsMessageUnchanged() =>
        Assert.Equal("plain", SagaErrorText.Describe(new InvalidOperationException("plain")));

    [Fact]
    public void Describe_AppendsTheInnermostMessage_NotTheMiddleOnes()
    {
        var chain = new InvalidOperationException("outer", new InvalidCastException("middle", new TimeoutException("the real cause")));

        Assert.Equal("outer Cause: the real cause", SagaErrorText.Describe(chain));
    }

    [Fact]
    public void Describe_DoesNotRepeatAnInnerMessageTheOuterOneAlreadyContains()
    {
        var chain = new InvalidOperationException("step failed: boom", new TimeoutException("boom"));

        Assert.Equal("step failed: boom", SagaErrorText.Describe(chain));
    }

    [Fact]
    public void Describe_IgnoresABlankInnerMessage() =>
        Assert.Equal("outer", SagaErrorText.Describe(new InvalidOperationException("outer", new TimeoutException("  "))));

    [Fact]
    public void Describe_CutsALongInnermostMessage_AtTheBound()
    {
        var chain = new InvalidOperationException("outer", new TimeoutException(new string('x', SagaErrorText.MaxCauseLength + 500)));

        var text = SagaErrorText.Describe(chain);

        Assert.Equal("outer Cause: " + new string('x', SagaErrorText.MaxCauseLength) + "...", text);
    }

    // ---- the DeliveryExhausted entry, through the orchestrator ----

    /// <summary>Throws the given exception on every append until its budget is spent, then delegates to the real store.</summary>
    private sealed class FailingEventLogStore(ISagaEventLogStore inner, Func<Exception> failure, int failures) : ISagaEventLogStore
    {
        private int _remaining = failures;

        public Task<long> AppendAsync(SagaLogEntry entry, CancellationToken cancellationToken = default)
        {
            if (_remaining > 0)
            {
                _remaining--;
                throw failure();
            }

            return inner.AppendAsync(entry, cancellationToken);
        }

        public Task<bool> IsDuplicateAsync(string sagaType, Guid correlationId, string messageId, CancellationToken cancellationToken = default) =>
            inner.IsDuplicateAsync(sagaType, correlationId, messageId, cancellationToken);

        public Task<IReadOnlyList<SagaLogEntry>> GetTimelineAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
            inner.GetTimelineAsync(sagaType, correlationId, cancellationToken);
    }

    private static async Task<ServiceProvider> StartAsync(Func<Exception>? failure = null)
    {
        const int maxDeliveryAttempts = 2;

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddSingleton(new SagaOrchestratorOptions { MaxDeliveryAttempts = maxDeliveryAttempts });
        services.AddVSagaEngine(o => o.AddSaga<ErrorTextSaga, ErrorTextSagaState>());

        if (failure is not null)
        {
            // One failure per delivery (the first append of each), then the dead-letter entry itself goes through.
            services.AddSingleton<ISagaEventLogStore>(sp =>
                new FailingEventLogStore(sp.GetRequiredService<InMemorySagaStore>(), failure, failures: maxDeliveryAttempts + 1));
        }

        var provider = services.BuildServiceProvider();
        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        return provider;
    }

    private static Task<IReadOnlyList<SagaLogEntry>> TimelineAsync(ServiceProvider provider, Guid correlationId) =>
        provider.GetRequiredService<ISagaEventLogStore>()
            .GetTimelineAsync(provider.GetRequiredService<ErrorTextSaga>().SagaType, correlationId);

    [Fact]
    public async Task DeliveryExhausted_AfterATransientDatabaseError_NamesTheRealCause_NotOnlyAlreadyExists()
    {
        // What EfCoreSagaSnapshotStore throws for a failed save that is not a collision: the "already exists"
        // wrapper around the database's own error (here the text a Postgres trigger raised).
        await using var provider = await StartAsync(() => new SagaAlreadyExistsException(
            "ErrorTextSaga", Guid.Empty, new InvalidOperationException("P0001: injected transient database failure")));
        var correlationId = Guid.NewGuid();

        await provider.GetRequiredService<InMemoryMessageTransport>()
            .PublishAsync(new ErrorTextBegin("ET-1"), MessageEnvelope.New(correlationId));

        var exhausted = Assert.Single(await TimelineAsync(provider, correlationId), e => e.EntryType == SagaEntryType.DeliveryExhausted);
        Assert.Contains("injected transient database failure", exhausted.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeliveryExhausted_AfterARealCollision_StillReadsAsOne()
    {
        await using var provider = await StartAsync(() => new SagaAlreadyExistsException(
            "ErrorTextSaga", Guid.Empty, new InvalidOperationException("23505: duplicate key value violates unique constraint \"IX_SagaInstances_SagaType_BusinessKey\"")));
        var correlationId = Guid.NewGuid();

        await provider.GetRequiredService<InMemoryMessageTransport>()
            .PublishAsync(new ErrorTextBegin("ET-2"), MessageEnvelope.New(correlationId));

        var exhausted = Assert.Single(await TimelineAsync(provider, correlationId), e => e.EntryType == SagaEntryType.DeliveryExhausted);
        Assert.Contains("already exists", exhausted.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("duplicate key value violates unique constraint", exhausted.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeliveryExhausted_WithoutAnInnerException_RecordsTheMessageAsItWas()
    {
        await using var provider = await StartAsync(() => new InvalidOperationException("simulated transient infrastructure failure"));
        var correlationId = Guid.NewGuid();

        await provider.GetRequiredService<InMemoryMessageTransport>()
            .PublishAsync(new ErrorTextBegin("ET-3"), MessageEnvelope.New(correlationId));

        var exhausted = Assert.Single(await TimelineAsync(provider, correlationId), e => e.EntryType == SagaEntryType.DeliveryExhausted);
        Assert.Equal("simulated transient infrastructure failure", exhausted.ErrorMessage);
    }

    // ---- the other entries that record an exception ----

    [Fact]
    public async Task StepFailed_ForAWrappedException_NamesTheInnermostCause()
    {
        await using var provider = await StartAsync();
        var transport = provider.GetRequiredService<InMemoryMessageTransport>();
        var correlationId = Guid.NewGuid();

        await transport.PublishAsync(new ErrorTextBegin("ET-4"), MessageEnvelope.New(correlationId));
        await transport.PublishAsync(new ErrorTextBoom(), MessageEnvelope.New(correlationId));

        var failed = Assert.Single(await TimelineAsync(provider, correlationId), e => e.EntryType == SagaEntryType.StepFailed);
        Assert.Equal("The step could not finish. Cause: the connection was refused", failed.ErrorMessage);
    }

    [Fact]
    public async Task CompensationStepFailed_ForAWrappedException_NamesTheInnermostCause()
    {
        await using var provider = await StartAsync();
        var transport = provider.GetRequiredService<InMemoryMessageTransport>();
        var correlationId = Guid.NewGuid();

        await transport.PublishAsync(new ErrorTextBegin("ET-5"), MessageEnvelope.New(correlationId));
        await transport.PublishAsync(new ErrorTextUndo(), MessageEnvelope.New(correlationId));

        var failed = Assert.Single(await TimelineAsync(provider, correlationId), e => e.EntryType == SagaEntryType.CompensationStepFailed);
        Assert.Equal("The undo could not finish. Cause: the refund service timed out", failed.ErrorMessage);
    }
}

public sealed class ErrorTextSagaState : SagaState
{
    public string? JobId { get; set; }
}

public sealed record ErrorTextBegin(string JobId);

#pragma warning disable S2094
public sealed record ErrorTextBoom;
public sealed record ErrorTextUndo;
#pragma warning restore S2094

/// <summary>Begin parks the saga in Held; Boom's step and Undo's compensation then throw an exception with an inner one.</summary>
public sealed class ErrorTextSaga : OrchestratedSagaDefinition<ErrorTextSagaState>
{
    public State<ErrorTextSagaState> Open { get; }
    public State<ErrorTextSagaState> Held { get; }
    public State<ErrorTextSagaState> Undone { get; }

    public ErrorTextSaga()
    {
        Open = InitialState(nameof(Open));
        Held = State(nameof(Held));
        Undone = State(nameof(Undone));

        During(Open)
            .When<ErrorTextBegin>()
                .CorrelateBy(m => m.JobId, s => s.JobId)
                .TransitionTo(Held);

        During(Held)
            .When<ErrorTextBoom>()
                .Then((_, _) => throw new InvalidOperationException("The step could not finish.", new HttpRequestException("the connection was refused")))
            .When<ErrorTextUndo>()
                .Compensate()
                .TransitionTo(Undone)
                .Finalize(SagaStatus.Failed);

        Compensate(Held, (_, _) => throw new InvalidOperationException("The undo could not finish.", new TimeoutException("the refund service timed out")));
    }
}
