using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using VSaga.Dashboard.Identity.Services;

namespace VSaga.Dashboard.Api.Hubs;

/// <summary>
/// The live hub connections and whose they are, so an access change can drop the connections it affects
/// (design §8.6). A WebSocket is authenticated once, when it connects, and the hub's groups are joined
/// under the access the caller had then; nothing on an open socket re-checks the session. So whenever a
/// user's access, credentials or standing change (password change, sign-out, administrator reset, disable,
/// enable, delete, grant or team changes) that user's connections are aborted, and every connection when a
/// role changes, since a role can be anyone's. The client reconnects, which authenticates again (a disabled
/// user's negotiate gets 401), and resubscribes, which resolves access afresh, so group membership always
/// follows the current access.
/// </summary>
/// <remarks>
/// A connection is recorded before it can join a group (<see cref="SagaHub.OnConnectedAsync"/> runs before
/// any invocation), and every subscription reads the store again, so a change committed between the
/// connect and the record is still caught: either the subscription already sees it, or the abort that
/// follows the commit finds the connection.
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

    /// <summary>Aborts every connection of these users.</summary>
    public Task UsersChangedAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        var affected = userIds.ToHashSet();
        Abort(registration => registration.UserId is { } id && affected.Contains(id), "users' access changed");
        return Task.CompletedTask;
    }

    /// <summary>Aborts every connection, the API key's included: a role change can affect anyone.</summary>
    public Task AllUsersChangedAsync(CancellationToken cancellationToken)
    {
        Abort(_ => true, "a role changed");
        return Task.CompletedTask;
    }

    private void Abort(Func<Registration, bool> isAffected, string reason)
    {
        var aborted = 0;
        foreach (var (connectionId, registration) in _connections)
        {
            if (!isAffected(registration) || !_connections.TryRemove(connectionId, out _))
                continue;

            registration.Connection.Abort();
            aborted++;
        }

        if (aborted > 0)
            LogAborted(logger, aborted, reason);
    }

    private static Guid? UserIdOf(ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirst(DashboardClaims.Subject)?.Value, out var userId) ? userId : null;

    private sealed record Registration(Guid? UserId, HubCallerContext Connection);

    [LoggerMessage(EventId = 7320, EventName = "HubConnectionsAborted", Level = LogLevel.Information,
        Message = "Dropped {Count} live hub connection(s) because {Reason}; the clients reconnect under the current access")]
    private static partial void LogAborted(ILogger logger, int count, string reason);
}
