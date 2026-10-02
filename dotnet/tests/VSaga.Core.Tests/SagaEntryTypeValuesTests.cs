using VSaga.Abstractions.Persistence;

namespace VSaga.Core.Tests;

/// <summary>
/// Every provider persists <see cref="SagaEntryType"/> as a plain integer (the EF InitialCreate migration,
/// MongoDB documents and Redis list elements), so a member inserted rather than appended would silently
/// reinterpret every stored row. This pins each member's numeric value; a new member is appended last and
/// added here.
/// </summary>
public sealed class SagaEntryTypeValuesTests
{
    [Theory]
    [InlineData(SagaEntryType.SagaStarted, 0)]
    [InlineData(SagaEntryType.StateEntered, 1)]
    [InlineData(SagaEntryType.MessageReceived, 2)]
    [InlineData(SagaEntryType.UnexpectedEvent, 3)]
    [InlineData(SagaEntryType.StepStarted, 4)]
    [InlineData(SagaEntryType.StepSucceeded, 5)]
    [InlineData(SagaEntryType.StepFailed, 6)]
    [InlineData(SagaEntryType.CompensationStarted, 7)]
    [InlineData(SagaEntryType.CompensationStepSucceeded, 8)]
    [InlineData(SagaEntryType.CompensationStepFailed, 9)]
    [InlineData(SagaEntryType.TimeoutScheduled, 10)]
    [InlineData(SagaEntryType.TimeoutFired, 11)]
    [InlineData(SagaEntryType.TimeoutCancelled, 12)]
    [InlineData(SagaEntryType.ManualRetryRequested, 13)]
    [InlineData(SagaEntryType.SagaCompleted, 14)]
    [InlineData(SagaEntryType.SagaCancelled, 15)]
    [InlineData(SagaEntryType.DeliveryExhausted, 16)]
    [InlineData(SagaEntryType.MessagePublished, 17)]
    [InlineData(SagaEntryType.MessageSent, 18)]
    [InlineData(SagaEntryType.ChildSagaStarted, 19)]
    [InlineData(SagaEntryType.ChildSagaFinished, 20)]
    [InlineData(SagaEntryType.StatePersisted, 21)]
    public void EachMemberKeepsItsStoredNumericValue(SagaEntryType entryType, int expected)
    {
        Assert.Equal(expected, (int)entryType);
    }

    [Fact]
    public void EveryMemberIsPinned()
    {
        Assert.Equal(Enumerable.Range(0, 22), Enum.GetValues<SagaEntryType>().Select(e => (int)e));
    }
}
