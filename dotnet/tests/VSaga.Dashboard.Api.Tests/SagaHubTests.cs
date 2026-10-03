using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using VSaga.Dashboard.Api.Hubs;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// The hub's group-membership contract. This is deliberately close to the wire: <c>SubscribeToSaga</c>
/// takes <c>(sagaType, correlationId)</c>, and until now that two-argument shape was only ever verified
/// by hand against a running stack — a regression to the old single-argument form would have compiled,
/// passed CI, and broken every dashboard detail page's live updates at runtime.
/// </summary>
public sealed class SagaHubTests
{
    private const string ConnectionId = "conn-1";

    // A caller who may view every saga type: these tests are about the groups' names and shape; what access
    // decides is SagaHubAccessTests.
    private static (SagaHub Hub, RecordingGroupManager Groups) NewHub()
    {
        var groups = new RecordingGroupManager();
        var hub = new SagaHub(StubCallerAccessResolver.FullAccess(), new HubConnectionRegistry(NullLogger<HubConnectionRegistry>.Instance))
        {
            Groups = groups,
            Context = new TestHubCallerContext(ConnectionId, new ClaimsPrincipal(new ClaimsIdentity("Test"))),
        };

        return (hub, groups);
    }

    [Fact]
    public void GroupForSaga_IsKeyedByBothHalvesOfTheSagaIdentity()
    {
        var correlationId = Guid.NewGuid();

        var group = SagaHub.GroupForSaga("OrderSaga", correlationId);

        Assert.Equal($"saga:OrderSaga:{correlationId}", group);
    }

    /// <summary>
    /// The reason the group name carries the saga type at all: two saga types legitimately track one
    /// correlation id (OrderSaga and PostShipmentChoreography in the sample), and a detail page watching
    /// one must not be handed the other's updates.
    /// </summary>
    [Fact]
    public void GroupForSaga_SeparatesTwoSagaTypesSharingOneCorrelationId()
    {
        var correlationId = Guid.NewGuid();

        var orchestrated = SagaHub.GroupForSaga("OrderSaga", correlationId);
        var choreographed = SagaHub.GroupForSaga("PostShipmentChoreography", correlationId);

        Assert.NotEqual(orchestrated, choreographed, StringComparer.Ordinal);
    }

    [Fact]
    public async Task SubscribeToSaga_JoinsThatInstancesGroupOnly()
    {
        var (hub, groups) = NewHub();
        var correlationId = Guid.NewGuid();

        await hub.SubscribeToSaga("OrderSaga", correlationId.ToString());

        var joined = Assert.Single(groups.Added);
        Assert.Equal(ConnectionId, joined.ConnectionId);
        Assert.Equal(SagaHub.GroupForSaga("OrderSaga", correlationId), joined.GroupName);
        Assert.Empty(groups.Removed);
    }

    [Fact]
    public async Task UnsubscribeFromSaga_LeavesTheSameGroupItJoined()
    {
        var (hub, groups) = NewHub();
        var correlationId = Guid.NewGuid();

        await hub.SubscribeToSaga("OrderSaga", correlationId.ToString());
        await hub.UnsubscribeFromSaga("OrderSaga", correlationId.ToString());

        var left = Assert.Single(groups.Removed);
        Assert.Equal(groups.Added[0].GroupName, left.GroupName);
        Assert.Equal(ConnectionId, left.ConnectionId);
    }

    /// <summary>
    /// A malformed correlation id (a stale link, a hand-edited URL) used to fail the whole hub
    /// invocation via SignalR's default Guid model binder -- surfacing client-side as "Failed to
    /// invoke 'SubscribeToSaga' due to an error on the server". Parsing it here instead lets it join
    /// no group and return cleanly, matching how the REST endpoint for the same id already degrades.
    /// </summary>
    [Fact]
    public async Task SubscribeToSaga_WithAMalformedCorrelationId_JoinsNoGroupAndDoesNotThrow()
    {
        var (hub, groups) = NewHub();

        var joined = await hub.SubscribeToSaga("OrderSaga", "not-a-guid");

        Assert.False(joined);
        Assert.Empty(groups.Added);
    }

    /// <summary>
    /// A hand-made invocation can send a null saga type: it answers false like any refusal and joins no group,
    /// rather than failing the invocation.
    /// </summary>
    [Fact]
    public async Task SubscribeToSaga_WithANullSagaType_AnswersFalseAndJoinsNoGroup()
    {
        var (hub, groups) = NewHub();

        var joined = await hub.SubscribeToSaga(null, Guid.NewGuid().ToString());

        Assert.False(joined);
        Assert.Empty(groups.Added);
    }

    [Fact]
    public async Task UnsubscribeFromSaga_WithAMalformedCorrelationId_DoesNotThrow()
    {
        var (hub, groups) = NewHub();

        await hub.UnsubscribeFromSaga("OrderSaga", "not-a-guid");

        Assert.Empty(groups.Removed);
    }

    [Fact]
    public async Task SubscribingToTwoSagaTypesUnderOneCorrelationIdJoinsTwoDistinctGroups()
    {
        var (hub, groups) = NewHub();
        var correlationId = Guid.NewGuid();

        await hub.SubscribeToSaga("OrderSaga", correlationId.ToString());
        await hub.SubscribeToSaga("PostShipmentChoreography", correlationId.ToString());

        Assert.Equal(2, groups.Added.Count);
        Assert.Equal(2, groups.Added.Select(g => g.GroupName).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Unsubscribing from one instance must not remove the connection from a sibling's group.</summary>
    [Fact]
    public async Task UnsubscribingFromOneSagaTypeLeavesTheOthersGroupIntact()
    {
        var (hub, groups) = NewHub();
        var correlationId = Guid.NewGuid();

        await hub.SubscribeToSaga("OrderSaga", correlationId.ToString());
        await hub.SubscribeToSaga("PostShipmentChoreography", correlationId.ToString());
        await hub.UnsubscribeFromSaga("OrderSaga", correlationId.ToString());

        var left = Assert.Single(groups.Removed);
        Assert.Equal(SagaHub.GroupForSaga("OrderSaga", correlationId), left.GroupName);
        Assert.NotEqual(SagaHub.GroupForSaga("PostShipmentChoreography", correlationId), left.GroupName, StringComparer.Ordinal);
    }

    [Fact]
    public async Task SubscribeAndUnsubscribeFromList_UseTheSharedListGroup()
    {
        var (hub, groups) = NewHub();

        await hub.SubscribeToList();
        await hub.UnsubscribeFromList();

        Assert.Equal(SagaHub.ListGroup, Assert.Single(groups.Added).GroupName);
        Assert.Equal(SagaHub.ListGroup, Assert.Single(groups.Removed).GroupName);
    }

    /// <summary>The list group is shared across every saga, so it must not collide with any instance group.</summary>
    [Fact]
    public void ListGroup_IsDistinctFromEveryPerSagaGroup()
    {
        Assert.NotEqual(SagaHub.ListGroup, SagaHub.GroupForSaga("OrderSaga", Guid.NewGuid()), StringComparer.Ordinal);
    }

    /// <summary>
    /// The per-type list groups have a prefix of their own, so no saga type name (not even "list") can make one
    /// equal the shared list group or an instance group.
    /// </summary>
    [Theory]
    [InlineData("OrderSaga")]
    [InlineData("list")]
    [InlineData("")]
    public void ListGroupForType_IsDistinctFromTheSharedListGroupAndEveryInstanceGroup(string sagaType)
    {
        var group = SagaHub.ListGroupForType(sagaType);

        Assert.Equal("saga-list:" + sagaType, group);
        Assert.NotEqual(SagaHub.ListGroup, group, StringComparer.Ordinal);
        Assert.False(group.StartsWith("saga:", StringComparison.Ordinal));
    }
}
