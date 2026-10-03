using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.SignalR;
using VSaga.Dashboard.Identity.Services;

namespace VSaga.Dashboard.Api.Hubs;

/// <summary>
/// The live hub connections and whose they are, so an access change can drop the connections it affects
/// (design §8.6). A WebSocket is authenticated once, when it connects, and the hub's groups are joined
/// under the access the caller had then; nothing on an open socket re-checks the session. So whenever a
/// user's access, credentials or standing change (password change, sign-out, administrator reset, disable,
/// enable, delete, grant or team changes, and a deleted or changed role) that user's connections are closed,
/// and every connection when a role changes, since a role can be anyone's. A connection is closed through
/// <see cref="IConnectionLifetimeNotificationFeature.RequestClose"/>, the way SignalR closes one whose
/// ticket expired: the client is sent a close message that allows it to reconnect, so it reconnects, which
/// authenticates again (a disabled user, or a session whose security stamp rotated, gets 401 on negotiate),
/// and resubscribes, which resolves access afresh, so group membership always follows the current access.
/// <see cref="HubCallerContext.Abort"/> would not do: it tells the client not to reconnect, and the dashboard
/// would stay without live updates until the page is reloaded. It is only the fallback for a connection that
/// offers no such feature.
/// </summary>
/// <remarks>
/// A connection is recorded before it can join a group (<see cref="SagaHub.OnConnectedAsync"/> runs before
/// any invocation), and every subscription reads the store again, so a change committed between the
/// connect and the record is still caught: either the subscription already sees it, or the close that
/// follows the commit finds the connection. A connection that cannot be closed is logged and skipped, so one
/// failure never leaves the other affected connections open.
/// </remarks>
public sealed partial class HubConnectionRegistry(ILogger<HubConnectionRegistry> logger) : IAccessChangeObserver
{
    private readonly ConcurrentDictionary<string, Registration> _connections = new(StringComparer.Ordinal);

    /// <summary>How many connections are recorded.</summary>
    public int Count => _connections.Count;

    /// <summary>Records <paramref name="connection"/> under the user its principal names (none for the API key).</summary>
    public void Add(HubCallerContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connections[connection.ConnectionId] = new Registration(UserIdOf(connection.User), connection);
    }

    /// <summary>Forgets a connection that has ended.</summary>
    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    /// <summary>Closes every connection of these users.</summary>
    public Task UsersChangedAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        var affected = userIds.ToHashSet();
        Drop(registration => registration.UserId is { } id && affected.Contains(id), "users' access changed");
        return Task.CompletedTask;
    }

    /// <summary>Closes every connection, the API key's included: a role change can affect anyone.</summary>
    public Task AllUsersChangedAsync(CancellationToken cancellationToken)
    {
        Drop(_ => true, "a role changed");
        return Task.CompletedTask;
    }

    private void Drop(Func<Registration, bool> isAffected, string reason)
    {
        var dropped = 0;
        foreach (var (connectionId, registration) in _connections)
        {
            if (!isAffected(registration) || !_connections.TryRemove(connectionId, out _))
                continue;

            try
            {
                Close(registration.Connection);
                dropped++;
            }
            catch (Exception ex)
            {
                LogDropFailed(logger, ex, connectionId);
            }
        }

        if (dropped > 0)
            LogDropped(logger, dropped, reason);
    }

    // The close SignalR itself uses for an expired ticket: a close message with allowReconnect, so the client
    // reconnects. Abort() sends allowReconnect: false, which stops the JS client for good.
    private static void Close(HubCallerContext connection)
    {
        if (connection.Features.Get<IConnectionLifetimeNotificationFeature>() is { } lifetime)
            lifetime.RequestClose();
        else
            connection.Abort();
    }

    private static Guid? UserIdOf(ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirst(DashboardClaims.Subject)?.Value, out var userId) ? userId : null;

    private sealed record Registration(Guid? UserId, HubCallerContext Connection);

    [LoggerMessage(EventId = 7320, EventName = "HubConnectionsDropped", Level = LogLevel.Information,
        Message = "Closed {Count} live hub connection(s) because {Reason}; the clients reconnect under the current access")]
    private static partial void LogDropped(ILogger logger, int count, string reason);

    [LoggerMessage(EventId = 7321, EventName = "HubConnectionDropFailed", Level = LogLevel.Warning,
        Message = "Could not close live hub connection {ConnectionId} after an access change; the other affected connections were still closed")]
    private static partial void LogDropFailed(ILogger logger, Exception exception, string connectionId);
}
