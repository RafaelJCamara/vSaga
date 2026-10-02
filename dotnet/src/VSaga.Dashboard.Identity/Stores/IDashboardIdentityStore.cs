using VSaga.Dashboard.Identity.Model;

namespace VSaga.Dashboard.Identity.Stores;

/// <summary>
/// Persistence for dashboard users, teams and roles, shaped around whole aggregates: every read returns a
/// complete record (grants and members included) and every write replaces one. Nothing exposes a query
/// surface or a join, so another store (a document database, say) implements it without emulating a
/// relational model. Scoped: one instance per unit of work, never shared between concurrent callers.
/// </summary>
/// <remarks>
/// Names are unique ignoring case and surrounding whitespace (<see cref="IdentityNames.Normalize"/>).
/// Rules about what may be written (the last administrator, built-in roles, validation) belong to the
/// services; the store enforces only uniqueness and references, and serialises check-then-write through
/// <see cref="BeginExclusiveAsync"/>.
/// </remarks>
public interface IDashboardIdentityStore
{
    /// <summary>Brings the schema up to date. Idempotent.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>True when the store answers.</summary>
    Task<bool> CanConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Opens a write scope that excludes every other writer until it is committed or disposed. Reads and
    /// writes made through this store instance inside the scope see one consistent snapshot, so a service
    /// can load everything, check an invariant on the proposed change and write it with no interleaving.
    /// Disposing without <see cref="IIdentityWriteScope.CommitAsync"/> discards every write made in it.
    /// Only one scope may be open per store instance.
    /// </summary>
    Task<IIdentityWriteScope> BeginExclusiveAsync(CancellationToken cancellationToken);

    Task<int> CountUsersAsync(CancellationToken cancellationToken);

    Task<DashboardUser?> FindUserAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The user whose username matches ignoring case and surrounding whitespace, or null.</summary>
    Task<DashboardUser?> FindUserByNameAsync(string username, CancellationToken cancellationToken);

    /// <summary>Every user, ordered by normalised username.</summary>
    Task<IReadOnlyList<DashboardUser>> ListUsersAsync(CancellationToken cancellationToken);

    /// <exception cref="IdentityConflictException">The username is taken, ignoring case.</exception>
    /// <exception cref="IdentityReferenceException">A grant names a role that does not exist.</exception>
    Task CreateUserAsync(DashboardUser user, CancellationToken cancellationToken);

    /// <summary>Replaces every field of the user, its grants included. Team membership is not part of a user.</summary>
    /// <remarks>
    /// The failed sign-in count, the lockout end and the last sign-in time are overwritten too, so a record
    /// read before a concurrent <see cref="RecordFailedSignInAsync"/> or <see cref="RecordSignInAsync"/>
    /// would undo it (reset a lockout counter mid-attack, or clear a lockout). The caller therefore reads
    /// the user through this store inside a <see cref="BeginExclusiveAsync"/> scope and writes it in that
    /// same scope; called with no scope open, the update throws <see cref="InvalidOperationException"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No exclusive scope is open on this store.</exception>
    /// <exception cref="IdentityNotFoundException">No user has that id.</exception>
    /// <exception cref="IdentityConflictException">The username is taken by another user, ignoring case.</exception>
    /// <exception cref="IdentityReferenceException">A grant names a role that does not exist.</exception>
    Task UpdateUserAsync(DashboardUser user, CancellationToken cancellationToken);

    /// <summary>Deletes the user, its grants and its membership of every team.</summary>
    /// <exception cref="IdentityNotFoundException">No user has that id.</exception>
    Task DeleteUserAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Counts one failed sign-in as a single atomic change, so concurrent failures never lose an increment.
    /// When the count reaches <paramref name="maxAttempts"/> the account is locked until
    /// <paramref name="now"/> + <paramref name="lockout"/> and the count starts again from zero.
    /// <paramref name="maxAttempts"/> of zero or less counts without ever locking.
    /// </summary>
    /// <returns>The end of the lockout in force after this failure, or null when the account is not locked.</returns>
    /// <exception cref="IdentityNotFoundException">No user has that id.</exception>
    Task<DateTimeOffset?> RecordFailedSignInAsync(Guid id, int maxAttempts, DateTimeOffset now, TimeSpan lockout, CancellationToken cancellationToken);

    /// <summary>
    /// Records a successful sign-in as one atomic change: clears the failure count and any lockout, sets the
    /// last sign-in time and, when <paramref name="rehash"/> is not null, replaces the password hash.
    /// </summary>
    /// <exception cref="IdentityNotFoundException">No user has that id.</exception>
    Task RecordSignInAsync(Guid id, DateTimeOffset now, string? rehash, CancellationToken cancellationToken);

    Task<DashboardTeam?> FindTeamAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Every team, ordered by normalised name.</summary>
    Task<IReadOnlyList<DashboardTeam>> ListTeamsAsync(CancellationToken cancellationToken);

    /// <summary>The teams the user is a member of, ordered by normalised name.</summary>
    Task<IReadOnlyList<DashboardTeam>> ListTeamsForUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <exception cref="IdentityConflictException">The name is taken, ignoring case.</exception>
    /// <exception cref="IdentityReferenceException">A member is not a user, or a grant names a role that does not exist.</exception>
    Task CreateTeamAsync(DashboardTeam team, CancellationToken cancellationToken);

    /// <summary>Replaces every field of the team, its members and grants included.</summary>
    /// <exception cref="IdentityNotFoundException">No team has that id.</exception>
    /// <exception cref="IdentityConflictException">The name is taken by another team, ignoring case.</exception>
    /// <exception cref="IdentityReferenceException">A member is not a user, or a grant names a role that does not exist.</exception>
    Task UpdateTeamAsync(DashboardTeam team, CancellationToken cancellationToken);

    /// <summary>Deletes the team, its grants and its memberships; the users stay.</summary>
    /// <exception cref="IdentityNotFoundException">No team has that id.</exception>
    Task DeleteTeamAsync(Guid id, CancellationToken cancellationToken);

    Task<DashboardRole?> FindRoleAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The role whose name matches ignoring case and surrounding whitespace, or null.</summary>
    Task<DashboardRole?> FindRoleByNameAsync(string name, CancellationToken cancellationToken);

    /// <summary>Every role, ordered by normalised name.</summary>
    Task<IReadOnlyList<DashboardRole>> ListRolesAsync(CancellationToken cancellationToken);

    /// <exception cref="IdentityConflictException">The name is taken, ignoring case.</exception>
    Task CreateRoleAsync(DashboardRole role, CancellationToken cancellationToken);

    /// <summary>Replaces every field of the role.</summary>
    /// <exception cref="IdentityNotFoundException">No role has that id.</exception>
    /// <exception cref="IdentityConflictException">The name is taken by another role, ignoring case.</exception>
    Task UpdateRoleAsync(DashboardRole role, CancellationToken cancellationToken);

    /// <exception cref="IdentityNotFoundException">No role has that id.</exception>
    /// <exception cref="IdentityReferenceException">A user or team grant still names the role.</exception>
    Task DeleteRoleAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>True when any user or team grant names the role.</summary>
    Task<bool> IsRoleInUseAsync(Guid roleId, CancellationToken cancellationToken);
}

/// <summary>An open exclusive write scope (see <see cref="IDashboardIdentityStore.BeginExclusiveAsync"/>).</summary>
public interface IIdentityWriteScope : IAsyncDisposable
{
    /// <summary>Makes every write made in the scope durable and visible, and ends the exclusion.</summary>
    Task CommitAsync(CancellationToken cancellationToken);
}
