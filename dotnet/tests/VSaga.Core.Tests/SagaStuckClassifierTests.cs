using VSaga.Abstractions.Notifications;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Core.Runtime;

namespace VSaga.Core.Tests;

/// <summary>
/// docs/design/stuck-saga-detection.md §3: the definition of "stuck" as pure functions, so each boundary is a case
/// of its own. The defaults the cases lean on: idle threshold 1 hour, timeout grace 2 minutes.
/// </summary>
public sealed class SagaStuckClassifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SagaStuckOptions Defaults = new();

    private static SagaSummary Summary(SagaStatus status, TimeSpan idle, string sagaType = "OrderSaga", int version = 3) =>
        new(Guid.NewGuid(), sagaType, SagaKind.Orchestrated, "Waiting", status, Now - TimeSpan.FromDays(2), Now - idle, version, null, null);

    [Fact]
    public void RunningInAStateWithATimeout_IsStuckOnlyPastTheTimeoutPlusTheGrace()
    {
        var timeout = TimeSpan.FromMinutes(5);
        var threshold = timeout + Defaults.TimeoutGrace;

        var onTheBoundary = SagaStuckClassifier.Classify(Summary(SagaStatus.Running, threshold), timeout, Defaults, Now);
        var aTickOver = SagaStuckClassifier.Classify(Summary(SagaStatus.Running, threshold + TimeSpan.FromTicks(1)), timeout, Defaults, Now);
        var longIdleButWithinTheTimeout = SagaStuckClassifier.Classify(Summary(SagaStatus.Running, TimeSpan.FromMinutes(6)), timeout, Defaults, Now);

        Assert.Null(onTheBoundary);
        Assert.Null(longIdleButWithinTheTimeout);
        Assert.NotNull(aTickOver);
        Assert.Equal(StuckCause.TimeoutOverdue, aTickOver.Cause);
        Assert.Equal(threshold, aTickOver.Threshold);
    }

    [Fact]
    public void RunningInAStateWithoutATimeout_IsStuckOnlyPastTheIdleThreshold()
    {
        var onTheBoundary = SagaStuckClassifier.Classify(Summary(SagaStatus.Running, TimeSpan.FromHours(1)), null, Defaults, Now);
        var aTickOver = SagaStuckClassifier.Classify(Summary(SagaStatus.Running, TimeSpan.FromHours(1) + TimeSpan.FromTicks(1)), null, Defaults, Now);

        Assert.Null(onTheBoundary);
        Assert.NotNull(aTickOver);
        Assert.Equal(StuckCause.IdleWithoutTimeout, aTickOver.Cause);
        Assert.Equal(TimeSpan.FromHours(1), aTickOver.Threshold);
    }

    [Fact]
    public void Compensating_IsStuckPastTheIdleThreshold_WhateverTheStateTimeoutIs()
    {
        var withATimeout = SagaStuckClassifier.Classify(Summary(SagaStatus.Compensating, TimeSpan.FromMinutes(30)), TimeSpan.FromMinutes(5), Defaults, Now);
        var stuck = SagaStuckClassifier.Classify(Summary(SagaStatus.Compensating, TimeSpan.FromHours(2)), TimeSpan.FromMinutes(5), Defaults, Now);

        // Half an hour idle is past the state's timeout plus grace, but a compensating saga is judged by the idle threshold.
        Assert.Null(withATimeout);
        Assert.NotNull(stuck);
        Assert.Equal(StuckCause.CompensationIdle, stuck.Cause);
        Assert.Equal(TimeSpan.FromHours(1), stuck.Threshold);
    }

    [Theory]
    [InlineData(SagaStatus.Completed)]
    [InlineData(SagaStatus.Failed)]
    [InlineData(SagaStatus.Compensated)]
    [InlineData(SagaStatus.TimedOut)]
    [InlineData(SagaStatus.Cancelled)]
    public void ASagaInATerminalStatus_IsNeverStuck(SagaStatus status)
    {
        Assert.Null(SagaStuckClassifier.Classify(Summary(status, TimeSpan.FromDays(30)), null, Defaults, Now));
        Assert.Null(SagaStuckClassifier.Classify(Summary(status, TimeSpan.FromDays(30)), TimeSpan.FromMinutes(5), Defaults, Now));
    }

    [Fact]
    public void ATimeoutLongerThanTheIdleThreshold_IsNotFlaggedBeforeItCouldHaveFired()
    {
        var timeout = TimeSpan.FromHours(24);

        var pastTheIdleThresholdOnly = SagaStuckClassifier.Classify(Summary(SagaStatus.Running, TimeSpan.FromHours(2)), timeout, Defaults, Now);
        var pastTheTimeout = SagaStuckClassifier.Classify(Summary(SagaStatus.Running, timeout + Defaults.TimeoutGrace + TimeSpan.FromTicks(1)), timeout, Defaults, Now);

        Assert.Null(pastTheIdleThresholdOnly);
        Assert.NotNull(pastTheTimeout);
        Assert.Equal(StuckCause.TimeoutOverdue, pastTheTimeout.Cause);
    }

    [Fact]
    public void ASagaTypeOverride_AppliesToThatTypeOnly()
    {
        var options = new SagaStuckOptions();
        options.IdleThresholdBySagaType["OrderSaga"] = TimeSpan.FromMinutes(10);

        var overridden = SagaStuckClassifier.Classify(Summary(SagaStatus.Running, TimeSpan.FromMinutes(11), "OrderSaga"), null, options, Now);
        var other = SagaStuckClassifier.Classify(Summary(SagaStatus.Running, TimeSpan.FromMinutes(11), "PaymentSaga"), null, options, Now);
        var overriddenCompensating = SagaStuckClassifier.Classify(Summary(SagaStatus.Compensating, TimeSpan.FromMinutes(11), "OrderSaga"), null, options, Now);

        Assert.NotNull(overridden);
        Assert.Equal(TimeSpan.FromMinutes(10), overridden.Threshold);
        Assert.Null(other);
        Assert.NotNull(overriddenCompensating);
    }

    [Fact]
    public void TheStuckSaga_CarriesWhatAReceiverNeedsToDeduplicateAndToLook()
    {
        var summary = Summary(SagaStatus.Running, TimeSpan.FromHours(3), version: 7);

        var stuck = SagaStuckClassifier.Classify(summary, null, Defaults, Now);

        Assert.NotNull(stuck);
        Assert.Equal(summary.SagaType, stuck.SagaType);
        Assert.Equal(summary.CorrelationId, stuck.CorrelationId);
        Assert.Equal("Waiting", stuck.CurrentState);
        Assert.Equal(SagaStatus.Running, stuck.Status);
        Assert.Equal(TimeSpan.FromHours(3), stuck.IdleFor);
        Assert.Equal(summary.UpdatedAtUtc, stuck.UpdatedAtUtc);
        Assert.Equal(7, stuck.Version);
    }

    [Theory]
    [InlineData(null, 60)]
    [InlineData(5, 7)]
    [InlineData(58, 60)]
    [InlineData(300, 60)]
    public void TheFloor_IsTheSmallerOfTheIdleThresholdAndTheShortestTimeoutPlusTheGrace(int? shortestTimeoutMinutes, int expectedMinutes)
    {
        TimeSpan? shortest = shortestTimeoutMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null;

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), SagaStuckClassifier.Floor("OrderSaga", shortest, Defaults));
    }

    [Fact]
    public void ADefinition_ReportsItsShortestTimeout_InBothDsls_AndNullWhenItDeclaresNone()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), new TestOrderSaga().ShortestTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), new TestShippingChoreography().ShortestTimeout);
        Assert.Null(new TestFanOutChoreography().ShortestTimeout);
    }

    [Fact]
    public void TheFloor_UsesTheSagaTypesOwnIdleThreshold()
    {
        var options = new SagaStuckOptions();
        options.IdleThresholdBySagaType["OrderSaga"] = TimeSpan.FromMinutes(20);

        Assert.Equal(TimeSpan.FromMinutes(20), SagaStuckClassifier.Floor("OrderSaga", null, options));
        Assert.Equal(TimeSpan.FromMinutes(60), SagaStuckClassifier.Floor("PaymentSaga", null, options));
    }
}
