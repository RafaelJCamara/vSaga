using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
/// never throws. The connection registry closes the connections an access change affects: the user's own
/// on a password change, a sign-out, a disable (from any session, so another session's socket goes too),
/// and every connection on a role change or a deleted role. It closes them the way SignalR closes one whose
/// ticket expired, with a close message that lets the client reconnect (a plain <c>Abort()</c> would stop the
/// SPA's client for good), and a connection that cannot be closed does not stop the others. The first half
/// drives the hub directly with a stub resolver; the second goes over a real WebSocket
/// (<see cref="HubTestConnection"/>) through the real composition.
/// </summary>
public sealed class SagaHubAccessTests : IAsyncLifetime, IAsyncDisposable
{
    private const string ConnectionId = "conn-1";
    private const string Password = "correct horse battery";
    private const string RegistryCategory = "VSaga.Dashboard.Api.Hubs.HubConnectionRegistry";
    private const int DroppedEventId = 7320;
    private const int DropFailedEventId = 7321;
    private const int AbortFailedEventId = 7322;

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
    public async Task AUsersChange_ClosesEveryConnectionOfThatUser_AndNoOther()
    {
        var registry = NewRegistry();
        var alice = Guid.NewGuid();
        var aliceOnTwoTabs = new[] { Connection("a1", alice), Connection("a2", alice) };
        var bob = Connection("b1", Guid.NewGuid());
        var apiKey = new TestHubCallerContext("k1", DashboardClaims.ForApiKey());
        foreach (var connection in aliceOnTwoTabs.Append(bob).Append(apiKey))
            registry.Add(connection);

        await registry.UsersChangedAsync([alice], CancellationToken.None);

        Assert.All(aliceOnTwoTabs, c => Assert.Equal(1, c.Closes));
        Assert.Equal(0, bob.Closes);
        Assert.Equal(0, apiKey.Closes);
        Assert.Equal(2, registry.Count);
    }

    /// <summary>A graceful close (a close message the client may reconnect after) is what the registry asks for, never an abort.</summary>
    [Fact]
    public async Task AUsersChange_NeverAbortsAConnectionThatCanBeClosedGracefully()
    {
        var registry = NewRegistry();
        var alice = Guid.NewGuid();
        var connection = Connection("a1", alice);
        registry.Add(connection);

        await registry.UsersChangedAsync([alice], CancellationToken.None);

        Assert.Equal(1, connection.Closes);
        Assert.Equal(0, connection.Aborts);
    }

    [Fact]
    public async Task AConnectionThatOffersNoGracefulClose_IsAborted()
    {
        var registry = NewRegistry();
        var alice = Guid.NewGuid();
        var connection = new TestHubCallerContext("a1", UserPrincipal(alice), offersGracefulClose: false);
        registry.Add(connection);

        await registry.UsersChangedAsync([alice], CancellationToken.None);

        Assert.Equal(1, connection.Aborts);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task ARoleChange_ClosesEveryConnection_TheApiKeysIncluded()
    {
        var registry = NewRegistry();
        var connections = new[] { Connection("a1", Guid.NewGuid()), Connection("b1", Guid.NewGuid()), new TestHubCallerContext("k1", DashboardClaims.ForApiKey()) };
        foreach (var connection in connections)
            registry.Add(connection);

        await registry.AllUsersChangedAsync(CancellationToken.None);

        Assert.All(connections, c => Assert.Equal(1, c.Closes));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task AConnectionThatEnded_IsNotClosed()
    {
        var registry = NewRegistry();
        var userId = Guid.NewGuid();
        var ended = Connection("a1", userId);
        registry.Add(ended);
        registry.Remove("a1");

        await registry.UsersChangedAsync([userId], CancellationToken.None);

        Assert.Equal(0, ended.Closes);
        Assert.Equal(0, ended.Aborts);
    }

    /// <summary>
    /// One connection that cannot be closed must not leave the user's others open, nor stay open itself: the failure is
    /// logged at Warning, the connection is aborted as the fallback (it is already out of the registry, so no later
    /// change would find it) and forgotten like the rest, and the count of the log line is of those closed.
    /// </summary>
    [Fact]
    public async Task AConnectionThatCannotBeClosed_IsLoggedAndAborted_AndDoesNotStopTheOthers()
    {
        var logs = new AuditLogCapture();
        var registry = NewRegistry(logs);
        var alice = Guid.NewGuid();
        var connections = new[] { Connection("a1", alice), Connection("a2", alice), Connection("a3", alice) };
        connections[1].FailToClose(new InvalidOperationException("transport already gone"));
        foreach (var connection in connections)
            registry.Add(connection);

        await registry.UsersChangedAsync([alice], CancellationToken.None);

        Assert.All(connections, c => Assert.Equal(1, c.Closes));
        Assert.Equal([0, 1, 0], connections.Select(c => c.Aborts));
        Assert.Equal(0, registry.Count);
        var failed = Assert.Single(logs.Logged(RegistryCategory, DropFailedEventId));
        Assert.Equal(LogLevel.Warning, failed.Level);
        Assert.Contains("a2", failed.Message, StringComparison.Ordinal);
        Assert.Empty(logs.Logged(RegistryCategory, AbortFailedEventId));
        var dropped = Assert.Single(logs.Logged(RegistryCategory, DroppedEventId));
        Assert.Contains("Closed 2 live hub connection(s)", dropped.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A connection that can be neither closed nor aborted stays open, which is logged at Error, and still does not
    /// stop the user's others from being closed.
    /// </summary>
    [Fact]
    public async Task AConnectionThatCanBeNeitherClosedNorAborted_IsLoggedAsAnError_AndDoesNotStopTheOthers()
    {
        var logs = new AuditLogCapture();
        var registry = NewRegistry(logs);
        var alice = Guid.NewGuid();
        var connections = new[] { Connection("a1", alice), Connection("a2", alice), Connection("a3", alice) };
        connections[1].FailToClose(new InvalidOperationException("transport already gone"));
        connections[1].FailToAbort(new InvalidOperationException("still gone"));
        foreach (var connection in connections)
            registry.Add(connection);

        await registry.UsersChangedAsync([alice], CancellationToken.None);

        Assert.All(connections, c => Assert.Equal(1, c.Closes));
        Assert.Equal([0, 1, 0], connections.Select(c => c.Aborts));
        Assert.Equal(0, registry.Count);
        Assert.Equal(LogLevel.Warning, Assert.Single(logs.Logged(RegistryCategory, DropFailedEventId)).Level);
        var abortFailed = Assert.Single(logs.Logged(RegistryCategory, AbortFailedEventId));
        Assert.Equal(LogLevel.Error, abortFailed.Level);
        Assert.Contains("a2", abortFailed.Message, StringComparison.Ordinal);
        Assert.Contains("Closed 2 live hub connection(s)", Assert.Single(logs.Logged(RegistryCategory, DroppedEventId)).Message, StringComparison.Ordinal);
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

    /// <summary>
    /// <c>UnsubscribeFromList</c> leaves what <c>SubscribeToList</c> joined using the connection's items, which only a real
    /// connection keeps from one invocation to the next. Two instance groups are the witnesses: had the list group
    /// not been left, the first saga's push would arrive twice (list, then instance) and the second push received
    /// would be the first saga's again, not the second's.
    /// </summary>
    [Fact]
    public async Task OverTheWire_UnsubscribeFromList_LeavesTheListGroup()
    {
        var user = await CreateUserWithPasswordAsync(_factory.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        await using var connection = await HubTestConnection.ConnectAsync(_factory, CookieHeader(_factory.Services, user, DateTimeOffset.UtcNow));
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());

        Assert.True((await connection.InvokeAsync("SubscribeToList")).GetBoolean());
        await connection.InvokeAsync("UnsubscribeFromList");
        Assert.True((await connection.InvokeAsync("SubscribeToSaga", "OrderSaga", first.ToString())).GetBoolean());
        Assert.True((await connection.InvokeAsync("SubscribeToSaga", "OrderSaga", second.ToString())).GetBoolean());

        var hub = _factory.Services.GetRequiredService<IHubContext<SagaHub, ISagaHubClient>>();
        await SagaHub.PushSagaUpdatedAsync(hub, Summary("OrderSaga", first));
        await SagaHub.PushSagaUpdatedAsync(hub, Summary("OrderSaga", second));

        Assert.Equal(first, (await connection.NextPushAsync()).Arguments[0].GetProperty("correlationId").GetGuid());
        Assert.Equal(second, (await connection.NextPushAsync()).Arguments[0].GetProperty("correlationId").GetGuid());
    }

    [Fact]
    public async Task APasswordChangeInAnotherSession_ClosesTheConnectionForReconnect_AndItsCookieNoLongerNegotiates()
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

        HubTestConnection.AssertClosedForReconnect(await connection.WaitForCloseAsync());
        using var negotiate = await HubTestConnection.NegotiateAsync(_factory, cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
    }

    [Fact]
    public async Task SigningOutInAnotherSession_ClosesTheConnectionForReconnect()
    {
        var user = await CreateUserWithPasswordAsync(_factory.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        await using var connection = await HubTestConnection.ConnectAsync(_factory, CookieHeader(_factory.Services, user, DateTimeOffset.UtcNow));
        Assert.True((await connection.InvokeAsync("SubscribeToList")).GetBoolean());

        using var otherSession = await SignInClient.StartAsync(_factory);
        using (var login = await otherSession.LoginAsync("alice", Password))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using (var logout = await otherSession.PostAsync("/api/auth/logout"))
            Assert.True(logout.IsSuccessStatusCode);

        HubTestConnection.AssertClosedForReconnect(await connection.WaitForCloseAsync());
    }

    [Fact]
    public async Task DisablingTheUser_ClosesTheirConnectionForReconnect_LeavesOthersOpen_AndTheirNegotiateGets401()
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

        HubTestConnection.AssertClosedForReconnect(await aliceConnection.WaitForCloseAsync());
        Assert.True((await bobConnection.InvokeAsync("SubscribeToList")).GetBoolean());
        using var negotiate = await HubTestConnection.NegotiateAsync(_factory, aliceCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
    }

    [Fact]
    public async Task ChangingARole_ClosesEveryonesConnectionForReconnect()
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

        HubTestConnection.AssertClosedForReconnect(await aliceConnection.WaitForCloseAsync());
        HubTestConnection.AssertClosedForReconnect(await bobConnection.WaitForCloseAsync());
    }

    /// <summary>
    /// The API key can act as a custom role no grant names (<c>Dashboard:ApiKeyRole</c>), so deleting an unused role
    /// still has to close the live connections, the key's included.
    /// </summary>
    [Fact]
    public async Task DeletingARole_ClosesEveryonesConnectionForReconnect_TheApiKeysIncluded()
    {
        var spare = await AdminApi.CreateRoleAsync(_factory.Services, "Spare", Permissions.SagasView);
        var alice = await CreateUserWithPasswordAsync(_factory.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        await using var aliceConnection = await HubTestConnection.ConnectAsync(_factory, CookieHeader(_factory.Services, alice, DateTimeOffset.UtcNow));
        await using var keyConnection = await HubTestConnection.ConnectAsync(_factory, cookieHeader: null, apiKey: DashboardApiFactory.TestApiKey);
        var (_, admin) = await AdminApi.SignInAdministratorAsync(_factory);

        using (admin)
        using (var delete = await admin.DeleteAsync($"/api/admin/roles/{spare.Id}"))
            Assert.True(delete.IsSuccessStatusCode, $"Deleting the role answered {delete.StatusCode}.");

        HubTestConnection.AssertClosedForReconnect(await aliceConnection.WaitForCloseAsync());
        HubTestConnection.AssertClosedForReconnect(await keyConnection.WaitForCloseAsync());
    }

    /// <summary>The API key's connection (the key sent as a header) subscribes like any caller, and a role change closes it too.</summary>
    [Fact]
    public async Task TheApiKeysConnection_Subscribes_AndIsClosedForReconnectByARoleChange()
    {
        var watchers = await AdminApi.CreateRoleAsync(_factory.Services, "Watchers", Permissions.SagasView);
        await using var connection = await HubTestConnection.ConnectAsync(_factory, cookieHeader: null, apiKey: DashboardApiFactory.TestApiKey);
        Assert.True((await connection.InvokeAsync("SubscribeToList")).GetBoolean());
        Assert.True((await connection.InvokeAsync("SubscribeToSaga", "OrderSaga", Guid.NewGuid().ToString())).GetBoolean());
        var (_, admin) = await AdminApi.SignInAdministratorAsync(_factory);

        using (admin)
        using (var update = await admin.PutAsync($"/api/admin/roles/{watchers.Id}", """{"name":"Watchers","permissions":["sagas.view","sagas.data"]}"""))
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        HubTestConnection.AssertClosedForReconnect(await connection.WaitForCloseAsync());
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

    private static HubConnectionRegistry NewRegistry(AuditLogCapture logs) =>
        new(new TypedLogger<HubConnectionRegistry>(logs.CreateLogger(RegistryCategory)));

    private static ClaimsPrincipal UserPrincipal(Guid userId) =>
        new(new ClaimsIdentity([new Claim(DashboardClaims.Subject, userId.ToString("D"))], "Test"));

    private static TestHubCallerContext Connection(string connectionId, Guid userId) => new(connectionId, UserPrincipal(userId));

    private static SagaSummary Summary(string sagaType, Guid? correlationId = null) =>
        new(correlationId ?? Guid.NewGuid(), sagaType, SagaKind.Orchestrated, "Running", SagaStatus.Running, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1,
            ParentSagaType: null, ParentCorrelationId: null);

    /// <summary>Hands a category's logger to something that asks for the typed one.</summary>
    private sealed class TypedLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
