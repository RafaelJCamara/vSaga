using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Stores;

namespace VSaga.Dashboard.Identity.EFCore;

/// <summary>
/// <see cref="IDashboardIdentityStore"/> over EF Core. Reads are untracked and mapped to the store-neutral
/// records; every write that checks before it writes (a name conflict, a role or member that must exist)
/// runs in a transaction, the caller's exclusive scope when one is open or its own otherwise. On SQLite a
/// transaction begins as <c>BEGIN IMMEDIATE</c>, so the check and the write are never interleaved with
/// another writer's. The sign-in counters are single <c>UPDATE</c> statements, which is what keeps
/// concurrent failed sign-ins from losing an increment without a row version.
/// </summary>
public sealed class EfCoreDashboardIdentityStore(DashboardIdentityDbContext db) : IDashboardIdentityStore
{
    public Task InitializeAsync(CancellationToken cancellationToken) => db.Database.MigrateAsync(cancellationToken);

    public Task<bool> CanConnectAsync(CancellationToken cancellationToken) => db.Database.CanConnectAsync(cancellationToken);

    public async Task<IIdentityWriteScope> BeginExclusiveAsync(CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("An exclusive identity write scope is already open on this store.");

        // Microsoft.Data.Sqlite starts it as BEGIN IMMEDIATE: the write lock is taken here, so a second
        // scope waits at this call (up to the command timeout) instead of reading a snapshot it could not
        // later write without a busy error.
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        return new WriteScope(db, transaction);
    }

    public Task<int> CountUsersAsync(CancellationToken cancellationToken) => db.Users.CountAsync(cancellationToken);

    public async Task<DashboardUser?> FindUserAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await ReadUsers().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<DashboardUser?> FindUserByNameAsync(string username, CancellationToken cancellationToken)
    {
        var normalized = IdentityNames.Normalize(username);
        var entity = await ReadUsers().FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<DashboardUser>> ListUsersAsync(CancellationToken cancellationToken)
    {
        var entities = await ReadUsers().OrderBy(u => u.NormalizedUsername).ToListAsync(cancellationToken);
        return entities.ConvertAll(ToRecord);
    }

    public Task CreateUserAsync(DashboardUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return WriteAsync(async () =>
        {
            var normalized = IdentityNames.Normalize(user.Username);
            if (await db.Users.AnyAsync(u => u.NormalizedUsername == normalized, cancellationToken))
                throw new IdentityConflictException(IdentityEntityKind.User, user.Username);
            await EnsureRolesExistAsync(user.Grants, cancellationToken);

            var entity = new DashboardUserEntity { Id = user.Id };
            CopyTo(user, entity, normalized);
            db.Users.Add(entity);
            db.UserGrants.AddRange(user.Grants.Select(g => ToUserGrantEntity(user.Id, g)));
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    public Task UpdateUserAsync(DashboardUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        // A full replace also writes the sign-in counters; outside the caller's exclusive scope it could
        // undo a failed or successful sign-in recorded since the caller read the user.
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A user is replaced only inside an exclusive identity write scope, after reading it in that scope.");
        return WriteAsync(async () =>
        {
            var entity = await db.Users.FirstOrDefaultAsync(u => u.Id == user.Id, cancellationToken)
                ?? throw new IdentityNotFoundException(IdentityEntityKind.User, user.Id);
            var normalized = IdentityNames.Normalize(user.Username);
            if (await db.Users.AnyAsync(u => u.NormalizedUsername == normalized && u.Id != user.Id, cancellationToken))
                throw new IdentityConflictException(IdentityEntityKind.User, user.Username);
            await EnsureRolesExistAsync(user.Grants, cancellationToken);

            CopyTo(user, entity, normalized);
            await db.UserGrants.Where(g => g.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
            db.UserGrants.AddRange(user.Grants.Select(g => ToUserGrantEntity(user.Id, g)));
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    public Task DeleteUserAsync(Guid id, CancellationToken cancellationToken) => WriteAsync(async () =>
    {
        // Explicit rather than left to the cascading keys, so the store does not depend on the connection
        // having enabled foreign keys.
        await db.TeamMembers.Where(m => m.UserId == id).ExecuteDeleteAsync(cancellationToken);
        await db.UserGrants.Where(g => g.UserId == id).ExecuteDeleteAsync(cancellationToken);
        if (await db.Users.Where(u => u.Id == id).ExecuteDeleteAsync(cancellationToken) == 0)
            throw new IdentityNotFoundException(IdentityEntityKind.User, id);
    }, cancellationToken);

    public Task<DateTimeOffset?> RecordFailedSignInAsync(Guid id, int maxAttempts, DateTimeOffset now, TimeSpan lockout, CancellationToken cancellationToken) =>
        WriteAsync(async () =>
        {
            var user = db.Users.Where(u => u.Id == id);
            var updated = maxAttempts <= 0
                ? await user.ExecuteUpdateAsync(s => s.SetProperty(u => u.FailedSignInCount, u => u.FailedSignInCount + 1), cancellationToken)
                : await IncrementOrLockAsync(user, maxAttempts, now + lockout, cancellationToken);
            if (updated == 0)
                throw new IdentityNotFoundException(IdentityEntityKind.User, id);

            // Read inside the same transaction, so this is the outcome of this failure and not of a later one.
            var lockoutEnd = await user.Select(u => u.LockoutEndUtc).FirstAsync(cancellationToken);
            return lockoutEnd > now ? lockoutEnd : null;
        }, cancellationToken);

    public async Task RecordSignInAsync(Guid id, DateTimeOffset now, string? rehash, CancellationToken cancellationToken)
    {
        var updated = await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.FailedSignInCount, 0)
            .SetProperty(u => u.LockoutEndUtc, (DateTimeOffset?)null)
            .SetProperty(u => u.LastSignInAtUtc, (DateTimeOffset?)now)
            .SetProperty(u => u.PasswordHash, u => rehash ?? u.PasswordHash), cancellationToken);
        if (updated == 0)
            throw new IdentityNotFoundException(IdentityEntityKind.User, id);
    }

    public async Task<DashboardTeam?> FindTeamAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await ReadTeams().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<DashboardTeam>> ListTeamsAsync(CancellationToken cancellationToken)
    {
        var entities = await ReadTeams().OrderBy(t => t.NormalizedName).ToListAsync(cancellationToken);
        return entities.ConvertAll(ToRecord);
    }

    public async Task<IReadOnlyList<DashboardTeam>> ListTeamsForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var entities = await ReadTeams()
            .Where(t => t.Members.Any(m => m.UserId == userId))
            .OrderBy(t => t.NormalizedName)
            .ToListAsync(cancellationToken);
        return entities.ConvertAll(ToRecord);
    }

    public Task CreateTeamAsync(DashboardTeam team, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(team);
        return WriteAsync(async () =>
        {
            var normalized = IdentityNames.Normalize(team.Name);
            if (await db.Teams.AnyAsync(t => t.NormalizedName == normalized, cancellationToken))
                throw new IdentityConflictException(IdentityEntityKind.Team, team.Name);
            await EnsureUsersExistAsync(team.MemberIds, cancellationToken);
            await EnsureRolesExistAsync(team.Grants, cancellationToken);

            db.Teams.Add(new DashboardTeamEntity
            {
                Id = team.Id,
                Name = team.Name,
                NormalizedName = normalized,
                Description = team.Description,
            });
            AddMembersAndGrants(team);
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    public Task UpdateTeamAsync(DashboardTeam team, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(team);
        return WriteAsync(async () =>
        {
            var entity = await db.Teams.FirstOrDefaultAsync(t => t.Id == team.Id, cancellationToken)
                ?? throw new IdentityNotFoundException(IdentityEntityKind.Team, team.Id);
            var normalized = IdentityNames.Normalize(team.Name);
            if (await db.Teams.AnyAsync(t => t.NormalizedName == normalized && t.Id != team.Id, cancellationToken))
                throw new IdentityConflictException(IdentityEntityKind.Team, team.Name);
            await EnsureUsersExistAsync(team.MemberIds, cancellationToken);
            await EnsureRolesExistAsync(team.Grants, cancellationToken);

            entity.Name = team.Name;
            entity.NormalizedName = normalized;
            entity.Description = team.Description;
            await db.TeamMembers.Where(m => m.TeamId == team.Id).ExecuteDeleteAsync(cancellationToken);
            await db.TeamGrants.Where(g => g.TeamId == team.Id).ExecuteDeleteAsync(cancellationToken);
            AddMembersAndGrants(team);
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    public Task DeleteTeamAsync(Guid id, CancellationToken cancellationToken) => WriteAsync(async () =>
    {
        await db.TeamMembers.Where(m => m.TeamId == id).ExecuteDeleteAsync(cancellationToken);
        await db.TeamGrants.Where(g => g.TeamId == id).ExecuteDeleteAsync(cancellationToken);
        if (await db.Teams.Where(t => t.Id == id).ExecuteDeleteAsync(cancellationToken) == 0)
            throw new IdentityNotFoundException(IdentityEntityKind.Team, id);
    }, cancellationToken);

    public async Task<DashboardRole?> FindRoleAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<DashboardRole?> FindRoleByNameAsync(string name, CancellationToken cancellationToken)
    {
        var normalized = IdentityNames.Normalize(name);
        var entity = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.NormalizedName == normalized, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<DashboardRole>> ListRolesAsync(CancellationToken cancellationToken)
    {
        var entities = await db.Roles.AsNoTracking().OrderBy(r => r.NormalizedName).ToListAsync(cancellationToken);
        return entities.ConvertAll(ToRecord);
    }

    public Task CreateRoleAsync(DashboardRole role, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(role);
        return WriteAsync(async () =>
        {
            var normalized = IdentityNames.Normalize(role.Name);
            if (await db.Roles.AnyAsync(r => r.NormalizedName == normalized, cancellationToken))
                throw new IdentityConflictException(IdentityEntityKind.Role, role.Name);

            var entity = new DashboardRoleEntity { Id = role.Id };
            CopyTo(role, entity, normalized);
            db.Roles.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    public Task UpdateRoleAsync(DashboardRole role, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(role);
        return WriteAsync(async () =>
        {
            var entity = await db.Roles.FirstOrDefaultAsync(r => r.Id == role.Id, cancellationToken)
                ?? throw new IdentityNotFoundException(IdentityEntityKind.Role, role.Id);
            var normalized = IdentityNames.Normalize(role.Name);
            if (await db.Roles.AnyAsync(r => r.NormalizedName == normalized && r.Id != role.Id, cancellationToken))
                throw new IdentityConflictException(IdentityEntityKind.Role, role.Name);

            CopyTo(role, entity, normalized);
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    public Task DeleteRoleAsync(Guid id, CancellationToken cancellationToken) => WriteAsync(async () =>
    {
        if (await IsRoleInUseAsync(id, cancellationToken))
            throw new IdentityReferenceException($"Role '{id}' is still granted to a user or a team.");
        if (await db.Roles.Where(r => r.Id == id).ExecuteDeleteAsync(cancellationToken) == 0)
            throw new IdentityNotFoundException(IdentityEntityKind.Role, id);
    }, cancellationToken);

    public async Task<bool> IsRoleInUseAsync(Guid roleId, CancellationToken cancellationToken) =>
        await db.UserGrants.AnyAsync(g => g.RoleId == roleId, cancellationToken)
        || await db.TeamGrants.AnyAsync(g => g.RoleId == roleId, cancellationToken);

    private IQueryable<DashboardUserEntity> ReadUsers() =>
        db.Users.AsNoTracking().Include(u => u.Grants);

    private IQueryable<DashboardTeamEntity> ReadTeams() =>
        db.Teams.AsNoTracking().Include(t => t.Members).Include(t => t.Grants).AsSingleQuery();

    /// <summary>
    /// One statement: the count and the lockout are both computed from the row's current count, so two
    /// concurrent failures increment twice and exactly one of them crosses the threshold.
    /// </summary>
    private static Task<int> IncrementOrLockAsync(IQueryable<DashboardUserEntity> user, int maxAttempts, DateTimeOffset lockoutEnd, CancellationToken cancellationToken)
    {
        DateTimeOffset? lockedUntil = lockoutEnd;
        return user.ExecuteUpdateAsync(s => s
            .SetProperty(u => u.LockoutEndUtc, u => u.FailedSignInCount + 1 >= maxAttempts ? lockedUntil : u.LockoutEndUtc)
            .SetProperty(u => u.FailedSignInCount, u => u.FailedSignInCount + 1 >= maxAttempts ? 0 : u.FailedSignInCount + 1), cancellationToken);
    }

    /// <summary>
    /// Runs a write in the open exclusive scope, or in a transaction of its own when none is open, and
    /// always leaves the change tracker empty: reads are untracked, so nothing tracked may outlive the
    /// write that loaded it and go stale against a later single-statement update.
    /// </summary>
    private async Task<T> WriteAsync<T>(Func<Task<T>> write, CancellationToken cancellationToken)
    {
        try
        {
            if (db.Database.CurrentTransaction is not null)
                return await write();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var result = await write();
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private Task WriteAsync(Func<Task> write, CancellationToken cancellationToken) =>
        WriteAsync(async () =>
        {
            await write();
            return true;
        }, cancellationToken);

    private async Task EnsureRolesExistAsync(IReadOnlyList<AccessGrant> grants, CancellationToken cancellationToken)
    {
        var roleIds = grants.Select(g => g.RoleId).Distinct().ToList();
        if (roleIds.Count != grants.Count)
            throw new ArgumentException("A user or team holds at most one grant per role.", nameof(grants));
        if (roleIds.Count == 0)
            return;

        var found = await db.Roles.CountAsync(r => roleIds.Contains(r.Id), cancellationToken);
        if (found != roleIds.Count)
            throw new IdentityReferenceException("A grant names a role that does not exist.");
    }

    private async Task EnsureUsersExistAsync(IReadOnlyList<Guid> userIds, CancellationToken cancellationToken)
    {
        var distinct = userIds.Distinct().ToList();
        if (distinct.Count != userIds.Count)
            throw new ArgumentException("A team lists each member once.", nameof(userIds));
        if (distinct.Count == 0)
            return;

        var found = await db.Users.CountAsync(u => distinct.Contains(u.Id), cancellationToken);
        if (found != distinct.Count)
            throw new IdentityReferenceException("A team member is not a user.");
    }

    private void AddMembersAndGrants(DashboardTeam team)
    {
        db.TeamMembers.AddRange(team.MemberIds.Select(userId => new DashboardTeamMemberEntity { TeamId = team.Id, UserId = userId }));
        db.TeamGrants.AddRange(team.Grants.Select(g => new DashboardTeamGrantEntity
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            RoleId = g.RoleId,
            AllSagaTypes = g.AllSagaTypes,
            SagaTypes = [.. g.SagaTypes],
        }));
    }

    private static DashboardUserGrantEntity ToUserGrantEntity(Guid userId, AccessGrant grant) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        RoleId = grant.RoleId,
        AllSagaTypes = grant.AllSagaTypes,
        SagaTypes = [.. grant.SagaTypes],
    };

    private static void CopyTo(DashboardUser user, DashboardUserEntity entity, string normalizedUsername)
    {
        entity.Username = user.Username;
        entity.NormalizedUsername = normalizedUsername;
        entity.DisplayName = user.DisplayName;
        entity.PasswordHash = user.PasswordHash;
        entity.SecurityStamp = user.SecurityStamp;
        entity.IsEnabled = user.IsEnabled;
        entity.MustChangePassword = user.MustChangePassword;
        entity.FailedSignInCount = user.FailedSignInCount;
        entity.LockoutEndUtc = user.LockoutEndUtc;
        entity.LastSignInAtUtc = user.LastSignInAtUtc;
        entity.CreatedAtUtc = user.CreatedAtUtc;
        entity.UpdatedAtUtc = user.UpdatedAtUtc;
    }

    private static void CopyTo(DashboardRole role, DashboardRoleEntity entity, string normalizedName)
    {
        entity.Name = role.Name;
        entity.NormalizedName = normalizedName;
        entity.Description = role.Description;
        entity.IsBuiltIn = role.IsBuiltIn;
        entity.Permissions = [.. role.Permissions];
    }

    private static DashboardUser ToRecord(DashboardUserEntity e) => new(
        e.Id,
        e.Username,
        e.DisplayName,
        e.PasswordHash,
        e.SecurityStamp,
        e.IsEnabled,
        e.MustChangePassword,
        e.FailedSignInCount,
        e.LockoutEndUtc,
        e.LastSignInAtUtc,
        e.CreatedAtUtc,
        e.UpdatedAtUtc,
        [.. e.Grants.OrderBy(g => g.RoleId).Select(g => new AccessGrant(g.RoleId, g.AllSagaTypes, [.. g.SagaTypes]))]);

    private static DashboardTeam ToRecord(DashboardTeamEntity e) => new(
        e.Id,
        e.Name,
        e.Description,
        [.. e.Members.Select(m => m.UserId).Order()],
        [.. e.Grants.OrderBy(g => g.RoleId).Select(g => new AccessGrant(g.RoleId, g.AllSagaTypes, [.. g.SagaTypes]))]);

    private static DashboardRole ToRecord(DashboardRoleEntity e) =>
        new(e.Id, e.Name, e.Description, e.IsBuiltIn, [.. e.Permissions]);

    /// <summary>The exclusive scope: the context's transaction, with the change tracker emptied on the way out.</summary>
    private sealed class WriteScope(DashboardIdentityDbContext db, IDbContextTransaction transaction) : IIdentityWriteScope
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            // Disposing an uncommitted transaction rolls it back.
            await transaction.DisposeAsync();
            db.ChangeTracker.Clear();
        }
    }
}
