using VSaga.Dashboard.Identity;
using VSaga.Dashboard.Identity.Model;
using static VSaga.Dashboard.Api.Tests.TestSessions;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// A hub socket outlives the cookie check that opened it, so it is closed when the ticket it opened under expires
/// (<c>CloseOnAuthenticationExpiration</c>), with the close message SignalR sends for that, which lets the client
/// reconnect; and the expiry SignalR reads is capped at the session's absolute lifetime
/// (<see cref="VSaga.Dashboard.Api.Auth.DashboardCookieEvents"/>). Each test has to wait for a real
/// ticket to run out, about ten seconds, which is why they live in a class of their own: xUnit runs the tests of
/// one class one after another but the classes side by side, so these do not lengthen <see cref="SagaHubAccessTests"/>.
/// </summary>
public sealed class HubTicketExpiryTests : IAsyncLifetime, IAsyncDisposable
{
    /// <summary>Long enough for a slow host to close the connection after the ticket ran out, far shorter than the hour an uncapped ticket would last.</summary>
    private static readonly TimeSpan CloseWithin = TimeSpan.FromSeconds(30);

    private readonly DashboardApiFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    // xunit 2 calls IAsyncLifetime.DisposeAsync, never a test class's IAsyncDisposable.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    /// <summary>
    /// Socket traffic does not slide the session: a connection is closed, with a close message that lets the client
    /// reconnect, when the ticket it opened under expires. The ticket was issued a second ago and expires in eight
    /// seconds, so negotiate and connect, which a slow host can stretch, still find it valid, and sliding renewal,
    /// which waits for half its life to pass, does not touch it. The wait is long for the same reason.
    /// </summary>
    [Fact]
    public async Task AConnectionWhoseTicketExpires_IsClosedForReconnect()
    {
        var user = await CreateUserAsync(_factory.Services, "alice", grants: AllTypes(BuiltInRoles.ViewerId));
        var now = DateTimeOffset.UtcNow;
        var cookie = CookieHeader(_factory.Services, user, now, issuedUtc: now.AddSeconds(-1), expiresUtc: now.AddSeconds(8));
        await using var connection = await HubTestConnection.ConnectAsync(_factory, cookie);
        Assert.True((await connection.InvokeAsync("SubscribeToList")).GetBoolean());

        HubTestConnection.AssertClosedForReconnect(await connection.WaitForCloseAsync(within: CloseWithin));
    }

    /// <summary>
    /// The sliding ticket can outlast the session's absolute lifetime (the cookie is renewed while it is used), but
    /// a socket must not: the expiry SignalR reads is capped at the sign-in time plus the absolute lifetime. Signed
    /// in ten seconds short of that lifetime, with a ticket valid for another hour, the connection is closed within
    /// seconds, not an hour from now.
    /// </summary>
    [Fact]
    public async Task AConnectionIsClosedForReconnectAtTheSessionsAbsoluteLifetime_WhateverTheSlidingTicketSays()
    {
        var user = await CreateUserAsync(_factory.Services, "alice", grants: AllTypes(BuiltInRoles.ViewerId));
        var signedInAt = DateTimeOffset.UtcNow - DashboardSecuritySettings.Default.SessionAbsoluteTimeout + TimeSpan.FromSeconds(10);
        var cookie = CookieHeader(_factory.Services, user, signedInAt);
        await using var connection = await HubTestConnection.ConnectAsync(_factory, cookie);
        Assert.True((await connection.InvokeAsync("SubscribeToList")).GetBoolean());

        HubTestConnection.AssertClosedForReconnect(await connection.WaitForCloseAsync(within: CloseWithin));
    }
}
