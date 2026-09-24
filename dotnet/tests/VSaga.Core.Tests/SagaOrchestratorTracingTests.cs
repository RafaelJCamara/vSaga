using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using VSaga.Abstractions.Diagnostics;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Abstractions.Transport;
using VSaga.Core.Runtime;
using VSaga.Persistence.InMemory;
using VSaga.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VSaga.Core.Tests;

/// <summary>
/// production-readiness.md §6/§8.18: the consumer span's ActivityKind.Consumer + header extraction, the
/// new producer span, activity.SetStatus on a step failure, SagaDuration's wiring, and RunningSagas'
/// deletion. See <see cref="TracingTestSaga"/> for the fixture these drive -- a saga of its own so a
/// process-wide ActivityListener/MeterListener here can't be confused by unrelated tests' SagaType tags.
/// </summary>
public sealed class SagaOrchestratorTracingTests
{
    private static async Task<ServiceProvider> BuildProviderAsync(Action<ServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(o => o.AddSaga<TracingTestSaga, TracingTestSagaState>());
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        return provider;
    }

    /// <summary>
    /// Captures every Activity this process starts against VSagaDiagnostics.ActivitySource, keyed by its
    /// final (post-Stop) state so a status set mid-span is visible. AddActivityListener is process-wide,
    /// so this ALSO sees spans from unrelated tests in other classes running concurrently -- filtered
    /// out here by <paramref name="sagaType"/> (TracingTestSaga's own, never shared with another
    /// fixture) rather than left for the caller to filter, and queued in a ConcurrentQueue rather than a
    /// plain List so that concurrent writes from those other tests' threads can never race a caller
    /// enumerating this one's own results.
    /// </summary>
    private static ActivityListener CreateCapturingListener(string sagaType, ConcurrentQueue<Activity> captured)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, VSagaDiagnostics.ActivitySourceName, StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (string.Equals(activity.GetTagItem(VSagaDiagnostics.TagSagaType) as string, sagaType, StringComparison.Ordinal))
                    captured.Enqueue(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>Captures every vsaga.saga.duration recording tagged with <paramref name="sagaType"/> -- filtered so a concurrently-running test using some other fixture's SagaType can't pollute the result.</summary>
    private static MeterListener CreateDurationListener(string sagaType, List<double> recorded)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (string.Equals(instrument.Meter.Name, VSagaDiagnostics.MeterName, StringComparison.Ordinal) &&
                    string.Equals(instrument.Name, "vsaga.saga.duration", StringComparison.Ordinal))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, measurement, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (string.Equals(tag.Key, VSagaDiagnostics.TagSagaType, StringComparison.Ordinal) && Equals(tag.Value, sagaType))
                {
                    lock (recorded)
                        recorded.Add(measurement);
                    return;
                }
            }
        });
        listener.Start();
        return listener;
    }

    private static string BuildTraceParentHeader(ActivityTraceId traceId, ActivitySpanId spanId) =>
        $"00-{traceId.ToHexString()}-{spanId.ToHexString()}-01";

    [Fact]
    public async Task ConsumerSpan_OnABrokerTransport_ExtractsTheParentFromInboundHeaders_NotAFreshRoot()
    {
        await using var provider = await BuildProviderAsync();
        var transport = provider.GetRequiredService<InMemoryMessageTransport>();
        var sagaType = provider.GetRequiredService<TracingTestSaga>().SagaType;
        var correlationId = Guid.NewGuid();

        var parentTraceId = ActivityTraceId.CreateRandom();
        var parentSpanId = ActivitySpanId.CreateRandom();
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [VSagaDiagnostics.TraceParentHeader] = BuildTraceParentHeader(parentTraceId, parentSpanId),
        };

        var captured = new ConcurrentQueue<Activity>();
        using var listener = CreateCapturingListener(sagaType, captured);

        // InMemoryMessageTransport dispatches inline/recursively -- no broker in between, standing in
        // for RabbitMQ/MassTransit/etc, which likewise carry no ambient Activity of their own; only the
        // header says which trace this belongs to.
        await transport.PublishAsync(new BeginTracingTest("ORD-T1"), new MessageEnvelope(correlationId, Guid.NewGuid().ToString("N"), headers));

        var stepActivity = Assert.Single(captured, a => a.OperationName.StartsWith("saga.step", StringComparison.Ordinal));
        Assert.Equal(ActivityKind.Consumer, stepActivity.Kind);
        Assert.Equal(parentTraceId, stepActivity.TraceId); // same trace as the header, not a fresh root
        Assert.Equal(parentSpanId, stepActivity.ParentSpanId);
    }

    [Fact]
    public async Task ConsumerSpan_WithNoInboundTraceParent_StillRootsAValidTrace()
    {
        await using var provider = await BuildProviderAsync();
        var transport = provider.GetRequiredService<InMemoryMessageTransport>();
        var sagaType = provider.GetRequiredService<TracingTestSaga>().SagaType;
        var correlationId = Guid.NewGuid();

        var captured = new ConcurrentQueue<Activity>();
        using var listener = CreateCapturingListener(sagaType, captured);

        await transport.PublishAsync(new BeginTracingTest("ORD-T1B"), MessageEnvelope.New(correlationId));

        var stepActivity = Assert.Single(captured, a => a.OperationName.StartsWith("saga.step", StringComparison.Ordinal));
        Assert.NotEqual(default, stepActivity.TraceId); // extraction gracefully falling back must not leave tracing broken
        Assert.Equal(default, stepActivity.ParentSpanId);
    }

    [Fact]
    public async Task ConsumerSpan_WithAnAmbientForeignActivity_StillExtractsFromHeaders_NotFromActivityCurrent()
    {
        // Stands in for HttpInboundDispatcher.DispatchInlineAsync: a genuine inbound HTTP request runs
        // the saga's handler synchronously nested inside ASP.NET Core's own request pipeline, where
        // Activity.Current is ASP.NET Core's *server* activity for that request -- a different trace
        // than the one the message's own traceparent header names. VSaga.Core.Tests hosts no ASP.NET
        // Core pipeline of its own, so a hand-started Activity plays that role here; what matters is
        // only that some unrelated Activity is ambient (Activity.Current) while the message is handled.
        await using var provider = await BuildProviderAsync();
        var transport = provider.GetRequiredService<InMemoryMessageTransport>();
        var sagaType = provider.GetRequiredService<TracingTestSaga>().SagaType;
        var correlationId = Guid.NewGuid();

        var messageTraceId = ActivityTraceId.CreateRandom();
        var messageSpanId = ActivitySpanId.CreateRandom();
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [VSagaDiagnostics.TraceParentHeader] = BuildTraceParentHeader(messageTraceId, messageSpanId),
        };

        var captured = new ConcurrentQueue<Activity>();
        using var listener = CreateCapturingListener(sagaType, captured);

        using var foreignServerActivity = new Activity("fake.aspnetcore.server-request").SetIdFormat(ActivityIdFormat.W3C);
        foreignServerActivity.Start();
        try
        {
            await transport.PublishAsync(new BeginTracingTest("ORD-T2"), new MessageEnvelope(correlationId, Guid.NewGuid().ToString("N"), headers));
        }
        finally
        {
            foreignServerActivity.Stop();
        }

        var stepActivity = Assert.Single(captured, a => a.OperationName.StartsWith("saga.step", StringComparison.Ordinal));
        Assert.Equal(messageTraceId, stepActivity.TraceId);
        Assert.NotEqual(foreignServerActivity.TraceId, stepActivity.TraceId);
    }

    [Fact]
    public async Task StepFailure_SetsTheConsumerSpanStatusToError()
    {
        await using var provider = await BuildProviderAsync();
        var transport = provider.GetRequiredService<InMemoryMessageTransport>();
        var sagaType = provider.GetRequiredService<TracingTestSaga>().SagaType;
        var correlationId = Guid.NewGuid();

        var captured = new ConcurrentQueue<Activity>();
        using var listener = CreateCapturingListener(sagaType, captured);

        await transport.PublishAsync(new BeginTracingTest("ORD-T3"), MessageEnvelope.New(correlationId));
        await transport.PublishAsync(new TracingTestBoom(), MessageEnvelope.New(correlationId));

        var failedStepActivity = Assert.Single(captured, a => a.OperationName.EndsWith(".Awaiting", StringComparison.Ordinal));
        Assert.Equal(ActivityStatusCode.Error, failedStepActivity.Status);
        Assert.Equal("simulated step failure", failedStepActivity.StatusDescription);
    }

    [Fact]
    public async Task SagaDuration_RecordsOnNormalCompletion()
    {
        await using var provider = await BuildProviderAsync();
        var transport = provider.GetRequiredService<InMemoryMessageTransport>();
        var sagaType = provider.GetRequiredService<TracingTestSaga>().SagaType;
        var correlationId = Guid.NewGuid();

        var recorded = new List<double>();
        using var listener = CreateDurationListener(sagaType, recorded);

        await transport.PublishAsync(new BeginTracingTest("ORD-T4"), MessageEnvelope.New(correlationId));
        await transport.PublishAsync(new TracingTestAdvance(), MessageEnvelope.New(correlationId));

        var duration = Assert.Single(recorded);
        Assert.True(duration >= 0);

        var snapshotStore = provider.GetRequiredService<ISagaSnapshotStore<TracingTestSagaState>>();
        var state = await snapshotStore.FindAsync(sagaType, correlationId);
        Assert.Equal(SagaStatus.Completed, state!.Status);
    }

    [Fact]
    public async Task SagaDuration_RecordsOnTimeoutCompletion()
    {
        await using var provider = await BuildProviderAsync();
        var transport = provider.GetRequiredService<InMemoryMessageTransport>();
        var sagaType = provider.GetRequiredService<TracingTestSaga>().SagaType;
        var correlationId = Guid.NewGuid();

        await transport.PublishAsync(new BeginTracingTest("ORD-T5"), MessageEnvelope.New(correlationId));

        var timeoutStore = provider.GetRequiredService<ISagaTimeoutStore>();
        var due = await timeoutStore.ClaimDueAsync(DateTimeOffset.UtcNow.AddHours(1), batchSize: 10);
        var timeout = Assert.Single(due, t => t.CorrelationId == correlationId);

        var recorded = new List<double>();
        using var listener = CreateDurationListener(sagaType, recorded);

        var orchestrator = provider.GetRequiredService<SagaOrchestrator<TracingTestSagaState>>();
        await orchestrator.HandleTimeoutAsync(timeout, CancellationToken.None);

        var duration = Assert.Single(recorded);
        Assert.True(duration >= 0);

        var snapshotStore = provider.GetRequiredService<ISagaSnapshotStore<TracingTestSagaState>>();
        var state = await snapshotStore.FindAsync(sagaType, correlationId);
        Assert.Equal(SagaStatus.TimedOut, state!.Status);
    }

    /// <summary>Throws on its first call, then delegates -- the same shape as SagaOrchestratorInfrastructureFailureTests' own FlakyEventLogStore, forcing HandleInfrastructureFailureAsync's redelivery path.</summary>
    private sealed class FlakyOnceEventLogStore(ISagaEventLogStore inner) : ISagaEventLogStore
    {
        private int _remainingFailures = 1;

        public Task<long> AppendAsync(SagaLogEntry entry, CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref _remainingFailures, 0, 1) == 1)
                throw new InvalidOperationException("simulated transient infrastructure failure");

            return inner.AppendAsync(entry, cancellationToken);
        }

        public Task<bool> IsDuplicateAsync(string sagaType, Guid correlationId, string messageId, CancellationToken cancellationToken = default) =>
            inner.IsDuplicateAsync(sagaType, correlationId, messageId, cancellationToken);

        public Task<IReadOnlyList<SagaLogEntry>> GetTimelineAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
            inner.GetTimelineAsync(sagaType, correlationId, cancellationToken);
    }

    [Fact]
    public async Task Redelivery_EchoesTheSameTraceAndTagsTheDeliveryAttempt()
    {
        await using var provider = await BuildProviderAsync(services =>
            services.AddSingleton<ISagaEventLogStore>(sp =>
                new FlakyOnceEventLogStore(sp.GetRequiredService<InMemorySagaStore>())));

        var transport = provider.GetRequiredService<InMemoryMessageTransport>();
        var sagaType = provider.GetRequiredService<TracingTestSaga>().SagaType;
        var correlationId = Guid.NewGuid();

        var messageTraceId = ActivityTraceId.CreateRandom();
        var messageSpanId = ActivitySpanId.CreateRandom();
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [VSagaDiagnostics.TraceParentHeader] = BuildTraceParentHeader(messageTraceId, messageSpanId),
        };

        var captured = new ConcurrentQueue<Activity>();
        using var listener = CreateCapturingListener(sagaType, captured);

        // Single await: HandleInfrastructureFailureAsync's redelivery runs synchronously/recursively
        // through the in-memory transport's inline dispatch (SagaOrchestratorInfrastructureFailureTests'
        // own precedent), so the whole fail-then-redeliver-then-succeed sequence has completed by the
        // time this returns.
        await transport.PublishAsync(new BeginTracingTest("ORD-T6"), new MessageEnvelope(correlationId, Guid.NewGuid().ToString("N"), headers));

        // The first (failing) attempt never reaches RunStepAsync's activity creation at all -- it throws
        // out of LogAsync's own append before the consumer span is even started -- so exactly one span
        // exists here, for the redelivered attempt.
        var stepActivity = Assert.Single(captured, a => a.OperationName.StartsWith("saga.step", StringComparison.Ordinal));
        Assert.Equal(messageTraceId, stepActivity.TraceId); // echoed forward, not a fresh root
        Assert.Equal(1, (int)stepActivity.GetTagItem(VSagaDiagnostics.TagDeliveryAttempt)!);
    }

    [Fact]
    public void RunningSagas_NoLongerExistsOnVSagaDiagnostics()
    {
        // production-readiness.md §6/§8.18: deleted rather than wired, because an UpDownCounter here is
        // process-local and non-idempotent (a restart/redelivery/second-replica desyncs it permanently
        // with no way to self-correct). Reflection, not just "doesn't compile", so this stays a real
        // regression check rather than something only a stray reference would ever catch.
        var field = typeof(VSagaDiagnostics).GetField("RunningSagas", BindingFlags.Public | BindingFlags.Static);
        Assert.Null(field);
    }
}
