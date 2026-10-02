using VSaga.Dashboard.Identity.Services;

namespace VSaga.Dashboard.Api.Auth;

/// <summary>
/// The <see cref="IAccessChangeObserver"/> until something holds live connections to drop: it does nothing.
/// Sign-out, password changes and access administration call the observer all the same, so the hub's
/// connection registry only has to replace this registration.
/// </summary>
internal sealed class NoAccessChangeObserver : IAccessChangeObserver
{
    public Task UsersChangedAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AllUsersChangedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
