using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Dashboard.Api.Hubs;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;
using static VSaga.Dashboard.Api.Tests.TestSessions;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// Access on the hub (design §8.6). The subscriptions resolve the caller afresh and join only what
/// <c>sagas.view</c> covers: the shared list group for every type, one list group per named type otherwise
/// (types that have not run yet included), an instance group only in scope; a refusal answers false and
/// never throws. The connection registry aborts the connections an access change affects: the user's own
/// on a password change, a sign-out, a disable (from any session, so another session's socket goes too),
/// and every connection on a role change. The first half drives the hub directly with a stub resolver; the
/// second goes over a real WebSocket (<see cref="HubTestConnection"/>) through the real composition.
/// </summary>
public sealed class SagaHubAccessTests : IAsyncLifetime, IAsyncDisposable
{
    private const string ConnectionId = "conn-1";
    private const string Password = "correct horse battery";

    private readonly DashboardApiFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    // xunit 2 calls IAsyncLifetime.DisposeAsync, never a test class's IAsyncDisposable.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task SubscribeToList_WithViewForEveryType_JoinsTheSharedListGroupOnly()
    {
        var (hub, groups, _) = NewHub(StubCallerAccessResolver.UserWith(AllTypes(BuiltInRoles.ViewerId)));

        Assert.True(await hub.SubscribeToList());

        Assert.Equal([SagaHub.ListGroup], groups.Added.Select(g => g.GroupName), StringComparer.Ordinal);
    }

    [Fact]
    public async Task SubscribeToList_WithViewScopedToNamedTypes_JoinsOneListGroupPerType_IncludingTypesThatHaveNotRun()
    {
        var caller = StubCallerAccessResolver.UserWith(
            ForTypes(BuiltInRoles.ViewerId, "OrderSaga", "NotRunYet"),
            ForTypes(BuiltInRoles.OperatorId, "InvoiceSaga"));
        var (hub, groups, _) = NewHub(caller);

        Assert.True(await hub.SubscribeToList());

        Assert.Equal(
            [SagaHub.ListGroupForType("InvoiceSaga"), SagaHub.ListGroupForType("NotRunYet"), SagaHub.ListGroupForType("OrderSaga")],
            groups.Added.Select(g => g.GroupName),
            StringComparer.Ordinal);
        Assert.All(groups.Added, g => Assert.Equal(ConnectionId, g.ConnectionId));
    }

    [Theory]
    [InlineData("refused by the store")]
    [InlineData("must change password")]
    [InlineData("no grant")]
    public async Task SubscribeToList_WithoutView_ReturnsFalse_AndJoinsNothing(string who)
    {
        var caller = who switch
        {
            "refused by the store" => null,
            "must change password" => StubCallerAccessResolver.UserWith(AllTypes(BuiltInRoles.ViewerId)) with
            {
                MustChangePassword = true,
                Access = EffectiveAccess.None,
            },
            _ => StubCallerAccessResolver.UserWith(),
        };
        var (hub, groups, _) = NewHub(caller);

        Assert.False(await hub.SubscribeToList());
        Assert.False(await hub.SubscribeToSaga("OrderSaga", Guid.NewGuid().ToString()));

        Assert.Empty(groups.Added);
    }

    [Fact]
    public async Task UnsubscribeFromList_LeavesExactlyTheGroupsSubscribeJoined()
    {
        var (hub, groups, _) = NewHub(StubCallerAccessResolver.UserWith(ForTypes(BuiltInRoles.ViewerId, "OrderSaga", "InvoiceSaga")));

        await hub.SubscribeToList();
        await hub.UnsubscribeFromList();

        Assert.Equal(
            groups.Added.Select(g => g.GroupName).Order(StringComparer.Ordinal),
            groups.Removed.Select(g => g.GroupName).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(groups.Removed, g => string.Equals(g.GroupName, SagaHub.ListGroup, StringComparison.Ordinal));
    }

    /// <summary>Each subscription asks the store again, so a second one follows the access the caller has now.</summary>
    [Fact]
    public async Task SubscribeToList_Again_ResolvesAfresh_AndLeavesGroupsNoLongerCovered()
    {
        var resolver = new StubCallerAccessResolver(StubCallerAccessResolver.UserWith(AllTypes(BuiltInRoles.ViewerId)));
        var (hub, groups, _) = NewHub(resolver);

        await hub.SubscribeToList();
        resolver.Caller = StubCallerAccessResolver.UserWith(ForTypes(BuiltInRoles.ViewerId, "OrderSaga"));
        Assert.True(await hub.SubscribeToList());

        Assert.Equal(2, resolver.Calls);
        Assert.Equal(SagaHub.ListGroup, Assert.Single(groups.Removed).GroupName);
        Assert.Equal(SagaHub.ListGroupForType("OrderSaga"), groups.Added[^1].GroupName);
    }

    [Fact]
    public async Task SubscribeToSaga_JoinsOnlyWithViewOnThatType()
    {
        var resolver = new StubCallerAccessResolver(StubCallerAccessResolver.UserWith(ForTypes(BuiltInRoles.ViewerId, "OrderSaga")));
        var (hub, groups, _) = NewHub(resolver);
        var correlationId = Guid.NewGuid();

        Assert.True(await hub.SubscribeToSaga("OrderSaga", correlationId.ToString()));
        Assert.False(await hub.SubscribeToSaga("InvoiceSaga", correlationId.ToString()));
        Assert.False(await hub.SubscribeToSaga("ordersaga", correlationId.ToString()));

        Assert.Equal(SagaHub.GroupForSaga("OrderSaga", correlationId), Assert.Single(groups.Added).GroupName);
        Assert.Equal(3, resolver.Calls);
    }

    [Fact]
    public async Task AConnectionWithoutAPrincipal_JoinsNothing()
    {
        var resolver = StubCallerAccessResolver.FullAccess();
        var groups = new RecordingGroupManager();
        var hub = new SagaHub(resolver, NewRegistry()) { Groups = groups, Context = new TestHubCallerContext(ConnectionId) };

        Assert.False(await hub.SubscribeToList());
        Assert.False(await hub.SubscribeToSaga("OrderSaga", Guid.NewGuid().ToString()));

        Assert.Empty(groups.Added);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task Connecting_RecordsTheConnection_AndDisconnecting_ForgetsIt()
    {
        var (hub, _, registry) = NewHub(StubCallerAccessResolver.UserWith(AllTypes(BuiltInRoles.ViewerId)));

        await hub.OnConnectedAsync();
        Assert.Equal(1, registry.Count);

        await hub.OnDisconnectedAsync(exception: null);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task AUsersChange_AbortsEveryConnectionOfThatUser_AndNoOther()
    {
        var registry = NewRegistry();
        var alice = Guid.NewGuid();
        var aliceOnTwoTabs = new[] { Connection("a1", alice), Connection("a2", alice) };
        var bob = Connection("b1", Guid.NewGuid());
        var apiKey = new TestHubCallerContext("k1", DashboardClaims.ForApiKey());
        foreach (var connection in aliceOnTwoTabs.Append(bob).Append(apiKey))
            registry.Add(connection);

        await registry.UsersChangedAsync([alice], CancellationToken.None);

        Assert.All(aliceOnTwoTabs, c => Assert.Equal(1, c.Aborts));
        Assert.Equal(0, bob.Aborts);
        Assert.Equal(0, apiKey.Aborts);
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public async Task ARoleChange_AbortsEveryConnection_TheApiKeysIncluded()
    {
        var registry = NewRegistry();
        var connections = new[] { Connection("a1", Guid.NewGuid()), Connection("b1", Guid.NewGuid()), new TestHubCallerContext("k1", DashboardClaims.ForApiKey()) };
        foreach (var connection in connections)
            registry.Add(connection);

        await registry.AllUsersChangedAsync(CancellationToken.None);

        Assert.All(connections, c => Assert.Equal(1, c.Aborts));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task AConnectionThatEnded_IsNotAborted()
    {
        var registry = NewRegistry();
        var userId = Guid.NewGuid();
        var ended = Connection("a1", userId);
        registry.Add(ended);
        registry.Remove("a1");

        await registry.UsersChangedAsync([userId], CancellationToken.None);

        Assert.Equal(0, ended.Aborts);
    }

    /// <summary>Over the wire: a scoped user's subscriptions answer true or false, and the pushes follow the scope.</summary>
    [Fact]
    public async Task OverTheWire_AScopedUserSubscribes_AndReceivesOnlyItsTypesPushes()
    {
        var user = await CreateUserWithPasswordAsync(_factory.Services, "scoped", Password, grants: ForTypes(BuiltInRoles.ViewerId, "OrderSaga"));
        await using var connection = await HubTestConnection.ConnectAsync(_factory, CookieHeader(_factory.Services, user, DateTimeOffset.UtcNow));

        Assert.True((await connection.InvokeAsync("SubscribeToList")).GetBoolean());
        Assert.False((await connection.InvokeAsync("SubscribeToSaga", "InvoiceSaga", Guid.NewGuid().ToString())).GetBoolean());
        Assert.True((await connection.InvokeAsync("SubscribeToSaga", "OrderSaga", Guid.NewGuid().ToString())).GetBoolean());

        var hub = _factory.Services.GetRequiredService<IHubContext<SagaHub, ISagaHubClient>>();
        await SagaHub.PushSagaUpdatedAsync(hub, Summary("InvoiceSaga"));
        await SagaHub.PushSagaUpdatedAsync(hub, Summary("OrderSaga"));

        // Pushes to one connection arrive in the order they were sent, so the first one received being
        // OrderSaga's means InvoiceSaga's never came.
        var (target, arguments) = await connection.NextPushAsync();
        Assert.Equal("SagaUpdated", target);
        Assert.Equal("OrderSaga", arguments[0].GetProperty("sagaType").GetString());
    }

    [Fact]
    public async Task APasswordChangeInAnotherSession_AbortsTheConnection_AndItsCookieNoLongerNegotiates()
    {
        var user = await CreateUserWithPasswordAsync(_factory.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        var cookie = CookieHeader(_factory.Services, user, DateTimeOffset.UtcNow);
        await using var connection = await HubTestConnection.ConnectAsync(_factory, cookie);
        Assert.True((await connection.InvokeAsync("SubscribeToList")).GetBoolean());

        using var otherSession = await SignInClient.StartAsync(_factory);
        using (var login = await otherSession.LoginAsync("alice", Password))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using (var change = await otherSession.ChangePasswordAsync(Password, "a brand new passphrase"))
            Assert.Equal(HttpStatusCode.OK, change.StatusCode);

        Assert.True(await connection.WaitForCloseAsync());
        using var negotiate = await HubTestConnection.NegotiateAsync(_factory, cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
    }

    [Fact]
    public async Task SigningOutInAnotherSession_AbortsTheConnection()
    {
        var user = await CreateUserWithPasswordAsync(_factory.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        await using var connection = await HubTestConnection.ConnectAsync(_factory, CookieHeader(_factory.Services, user, DateTimeOffset.UtcNow));
        Assert.True((await connection.InvokeAsync("SubscribeToList")).GetBoolean());

        using var otherSession = await SignInClient.StartAsync(_factory);
        using (var login = await otherSession.LoginAsync("alice", Password))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using (var logout = await otherSession.PostAsync("/api/auth/logout"))
            Assert.True(logout.IsSuccessStatusCode);

        Assert.True(await connection.WaitForCloseAsync());
    }

    [Fact]
    public async Task DisablingTheUser_AbortsTheirConnection_LeavesOthersOpen_AndTheirNegotiateGets401()
    {
        var alice = await CreateUserWithPasswordAsync(_factory.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        var bob = await CreateUserWithPasswordAsync(_factory.Services, "bob", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        var aliceCookie = CookieHeader(_factory.Services, alice, DateTimeOffset.UtcNow);
        await using var aliceConnection = await HubTestConnection.ConnectAsync(_factory, aliceCookie);
        await using var bobConnection = await HubTestConnection.ConnectAsync(_factory, CookieHeader(_factory.Services, bob, DateTimeOffset.UtcNow));
        var (_, admin) = await AdminApi.SignInAdministratorAsync(_factory);

        using (admin)
        using (var disable = await admin.PutAsync($"/api/admin/users/{alice.Id}", """{"isEnabled":false}"""))
            Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        Assert.True(await aliceConnection.WaitForCloseAsync());
        Assert.True((await bobConnection.InvokeAsync("SubscribeToList")).GetBoolean());
        using var negotiate = await HubTestConnection.NegotiateAsync(_factory, aliceCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
    }

    [Fact]
    public async Task ChangingARole_AbortsEveryonesConnection()
    {
        var watchers = await AdminApi.CreateRoleAsync(_factory.Services, "Watchers", Permissions.SagasView);
        var alice = await CreateUserWithPasswordAsync(_factory.Services, "alice", Password, grants: AllTypes(watchers.Id));
        var bob = await CreateUserWithPasswordAsync(_factory.Services, "bob", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        await using var aliceConnection = await HubTestConnection.ConnectAsync(_factory, CookieHeader(_factory.Services, alice, DateTimeOffset.UtcNow));
        await using var bobConnection = await HubTestConnection.ConnectAsync(_factory, CookieHeader(_factory.Services, bob, DateTimeOffset.UtcNow));
        var (_, admin) = await AdminApi.SignInAdministratorAsync(_factory);

        using (admin)
        using (var update = await admin.PutAsync($"/api/admin/roles/{watchers.Id}", """{"name":"Watchers","permissions":["sagas.view","sagas.data"]}"""))
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        Assert.True(await aliceConnection.WaitForCloseAsync());
        Assert.True(await bobConnection.WaitForCloseAsync());
    }

    /// <summary>
    /// Socket traffic does not slide the session: a connection is closed when the ticket it opened under
    /// expires. Issued a second ago and expiring in three, the ticket is not renewed by the negotiate and connect
    /// requests (sliding renewal waits for half its life to pass).
    /// </summary>
    [Fact]
    public async Task AConnectionWhoseTicketExpires_IsClosed()
    {
        var user = await CreateUserWithPasswordAsync(_factory.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        var now = DateTimeOffset.UtcNow;
        var cookie = CookieHeader(_factory.Services, user, now, issuedUtc: now.AddSeconds(-1), expiresUtc: now.AddSeconds(3));
        await using var connection = await HubTestConnection.ConnectAsync(_factory, cookie);
        Assert.True((await connection.InvokeAsync("SubscribeToList")).GetBoolean());

        Assert.True(await connection.WaitForCloseAsync());
    }

    private static (SagaHub Hub, RecordingGroupManager Groups, HubConnectionRegistry Registry) NewHub(CallerAccess? caller) =>
        NewHub(new StubCallerAccessResolver(caller));

    private static (SagaHub Hub, RecordingGroupManager Groups, HubConnectionRegistry Registry) NewHub(StubCallerAccessResolver resolver)
    {
        var groups = new RecordingGroupManager();
        var registry = NewRegistry();
        var hub = new SagaHub(resolver, registry)
        {
            Groups = groups,
            Context = Connection(ConnectionId, Guid.NewGuid()),
        };

        return (hub, groups, registry);
    }

    private static HubConnectionRegistry NewRegistry() => new(NullLogger<HubConnectionRegistry>.Instance);

    private static TestHubCallerContext Connection(string connectionId, Guid userId) =>
        new(connectionId, new ClaimsPrincipal(new ClaimsIdentity([new Claim(DashboardClaims.Subject, userId.ToString("D"))], "Test")));

    private static SagaSummary Summary(string sagaType) =>
        new(Guid.NewGuid(), sagaType, SagaKind.Orchestrated, "Running", SagaStatus.Running, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1,
            ParentSagaType: null, ParentCorrelationId: null);
}
