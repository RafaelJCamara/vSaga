using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Stores;

namespace VSaga.Dashboard.Identity.Services;

/// <summary>
/// Every change to users, teams and roles. Each mutation runs inside one exclusive write scope: it loads the
/// whole snapshot (users, teams, roles), validates the request against it, applies the change to a copy,
/// checks on that proposed snapshot that an enabled user still holds <c>access.manage</c> for all saga
/// types, writes and commits. Only then does it log the audit event and tell the
/// <see cref="IAccessChangeObserver"/> whose access changed (everyone, for a role change), so a live
/// connection is never dropped for a change that was rolled back.
/// <para>
/// Passwords are hashed before the scope opens, so the write lock is never held for the hashing work. Team
/// membership is written only through the team. The security stamp rotates on a password change, an
/// administrator's reset, when the account is disabled or enabled, and when a wrong current password on a
/// password change locks the account.
/// </para>
/// </summary>
/// <exception cref="IdentityValidationException">Thrown by a mutation whose request is malformed.</exception>
/// <exception cref="IdentityRuleException">Thrown by a mutation that would break an access rule.</exception>
/// <exception cref="IdentityNotFoundException">Thrown by a mutation naming a record that does not exist.</exception>
public sealed class AccessAdministrationService
{
    private const string UserKind = "user";
    private const string TeamKind = "team";
    private const string RoleKind = "role";
    private const string Succeeded = "succeeded";

    private readonly IDashboardIdentityStore _store;
    private readonly IPasswordHasher<DashboardUser> _hasher;
    private readonly PasswordPolicy _policy;
    private readonly IAccessChangeObserver _observer;
    private readonly DashboardSecuritySettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _audit;

    public AccessAdministrationService(
        IDashboardIdentityStore store,
        IPasswordHasher<DashboardUser> hasher,
        PasswordPolicy policy,
        IAccessChangeObserver observer,
        DashboardSecuritySettings settings,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _store = store;
        _hasher = hasher;
        _policy = policy;
        _observer = observer;
        _settings = settings;
        _timeProvider = timeProvider;
        _audit = loggerFactory.CreateLogger(DashboardAudit.CategoryName);
    }

    public Task<DashboardUser> CreateUserAsync(NewUser request, AuditContext audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AuditedAsync(audit, AccessActions.CreateUser, UserKind, null, async () =>
        {
            var errors = new ValidationErrors();
            var username = AccessValidation.Username(errors, "username", request.Username);
            var displayName = AccessValidation.RequiredText(errors, "displayName", request.DisplayName, AccessValidation.MaxDisplayNameLength);
            AddPasswordErrors(errors, "password", request.Username, request.Password, currentPassword: null);
            errors.ThrowIfAny();

            var hash = _hasher.HashPassword(DummyPasswordHash.Placeholder, request.Password!);
            var now = _timeProvider.GetUtcNow();
            return await InScopeAsync(async snapshot =>
            {
                var grants = AccessValidation.Grants(errors, "grants", request.Grants, snapshot.Roles);
                errors.ThrowIfAny();
                if (snapshot.Users.Any(u => SameName(u.Username, username!)))
                    throw Taken(IdentityRuleCodes.UsernameTaken, "user", username!);

                var user = new DashboardUser(
                    Guid.NewGuid(), username!, displayName!, hash, SecurityStamps.New(), IsEnabled: true, request.MustChangePassword,
                    FailedSignInCount: 0, LockoutEndUtc: null, LastSignInAtUtc: null, now, now, grants);
                EnsureAdministratorRemains(snapshot with { Users = [.. snapshot.Users, user] });
                await _store.CreateUserAsync(user, cancellationToken);
                return new Committed<DashboardUser>(user, user.Id, user.Username, DescribeUser(user), Notification.None);
            }, cancellationToken);
        });
    }

    /// <summary>Applies the non-null members of <paramref name="changes"/>; enabling or disabling rotates the stamp.</summary>
    public Task<DashboardUser> UpdateUserAsync(Guid id, UserChanges changes, AuditContext audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return AuditedAsync(audit, AccessActions.UpdateUser, UserKind, id, () => InScopeAsync(async snapshot =>
        {
            var current = snapshot.User(id);
            var errors = new ValidationErrors();
            var displayName = changes.DisplayName is null
                ? current.DisplayName
                : AccessValidation.RequiredText(errors, "displayName", changes.DisplayName, AccessValidation.MaxDisplayNameLength);
            var grants = changes.Grants is null ? current.Grants : AccessValidation.Grants(errors, "grants", changes.Grants, snapshot.Roles);
            errors.ThrowIfAny();

            var isEnabled = changes.IsEnabled ?? current.IsEnabled;
            var updated = current with
            {
                DisplayName = displayName!,
                IsEnabled = isEnabled,
                SecurityStamp = isEnabled == current.IsEnabled ? current.SecurityStamp : SecurityStamps.New(),
                Grants = grants,
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
            };
            EnsureAdministratorRemains(snapshot.WithUser(updated));
            await _store.UpdateUserAsync(updated, cancellationToken);

            var accessChanged = isEnabled != current.IsEnabled || changes.Grants is not null;
            return new Committed<DashboardUser>(
                updated, id, updated.Username, DescribeUser(updated), accessChanged ? Notification.Users(id) : Notification.None);
        }, cancellationToken));
    }

    /// <summary>Deletes the user, their grants and their membership of every team.</summary>
    public Task DeleteUserAsync(Guid id, AuditContext audit, CancellationToken cancellationToken) =>
        AuditedAsync(audit, AccessActions.DeleteUser, UserKind, id, () => InScopeAsync(async snapshot =>
        {
            var current = snapshot.User(id);
            EnsureAdministratorRemains(snapshot with
            {
                Users = [.. snapshot.Users.Where(u => u.Id != id)],
                Teams = [.. snapshot.Teams.Select(t => t with { MemberIds = [.. t.MemberIds.Where(m => m != id)] })],
            });
            await _store.DeleteUserAsync(id, cancellationToken);
            return new Committed<bool>(true, id, current.Username, "deleted", Notification.Users(id));
        }, cancellationToken));

    /// <summary>
    /// An administrator sets a user's password: the stamp rotates, which ends the user's sessions, and
    /// <paramref name="mustChangePassword"/> decides whether the user must choose another at next sign-in.
    /// The failure count and any lockout are left to <see cref="UnlockUserAsync"/>.
    /// </summary>
    public Task<DashboardUser> ResetPasswordAsync(
        Guid id, string? newPassword, bool mustChangePassword, AuditContext audit, CancellationToken cancellationToken) =>
        AuditedAsync(audit, AccessActions.ResetPassword, UserKind, id, async () =>
        {
            var existing = await _store.FindUserAsync(id, cancellationToken)
                ?? throw new IdentityNotFoundException(IdentityEntityKind.User, id);
            var errors = new ValidationErrors();
            AddPasswordErrors(errors, "newPassword", existing.Username, newPassword, currentPassword: null);
            errors.ThrowIfAny();

            var hash = _hasher.HashPassword(existing, newPassword!);
            return await InScopeAsync(async snapshot =>
            {
                var updated = snapshot.User(id) with
                {
                    PasswordHash = hash,
                    SecurityStamp = SecurityStamps.New(),
                    MustChangePassword = mustChangePassword,
                    UpdatedAtUtc = _timeProvider.GetUtcNow(),
                };
                EnsureAdministratorRemains(snapshot.WithUser(updated));
                await _store.UpdateUserAsync(updated, cancellationToken);
                return new Committed<DashboardUser>(
                    updated, id, updated.Username, $"mustChangePassword={mustChangePassword}", Notification.Users(id));
            }, cancellationToken);
        });

    /// <summary>Clears the failure count and any lockout. Access is unchanged, so nobody is notified.</summary>
    public Task<DashboardUser> UnlockUserAsync(Guid id, AuditContext audit, CancellationToken cancellationToken) =>
        AuditedAsync(audit, AccessActions.UnlockUser, UserKind, id, () => InScopeAsync(async snapshot =>
        {
            var updated = snapshot.User(id) with
            {
                FailedSignInCount = 0,
                LockoutEndUtc = null,
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
            };
            EnsureAdministratorRemains(snapshot.WithUser(updated));
            await _store.UpdateUserAsync(updated, cancellationToken);
            return new Committed<DashboardUser>(updated, id, updated.Username, "unlocked", Notification.None);
        }, cancellationToken));

    /// <summary>
    /// A signed-in user changes their own password. The new password is checked first (policy, and not the
    /// current one). A wrong current password counts against the account like a failed sign-in and can lock
    /// it; the failure that reaches the threshold also rotates the stamp and notifies the observer, ending
    /// every session of the user (the one that guessed included). A session gains no more guesses at the
    /// current password than the sign-in endpoint allows. A locked or disabled account is refused without
    /// checking anything and its sessions are left alone, so an outsider's failed sign-ins cannot end them.
    /// On success the stamp rotates (ending every other session), <c>MustChangePassword</c> clears and the
    /// failure count resets.
    /// </summary>
    public async Task<PasswordChangeResult> ChangeOwnPasswordAsync(
        Guid userId, string? currentPassword, string? newPassword, AuditContext audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audit);
        try
        {
            var result = await ChangeOwnPasswordCoreAsync(userId, currentPassword, newPassword, cancellationToken);
            if (result.Status == PasswordChangeStatus.Changed)
            {
                DashboardAudit.AccessChanged(
                    _audit, audit.Actor, AccessActions.ChangeOwnPassword, UserKind, userId, result.User!.Username, audit.ClientAddress, Succeeded, "password changed");
                await NotifyAsync(AccessActions.ChangeOwnPassword, Notification.Users(userId));
            }
            else
            {
                var outcome = result switch
                {
                    { Status: PasswordChangeStatus.Refused } => "refused",
                    { LockedUntilUtc: null } => "invalid_credentials",
                    _ => "invalid_credentials_locked_out",
                };
                DashboardAudit.AccessChangeRejected(_audit, audit.Actor, AccessActions.ChangeOwnPassword, UserKind, userId, audit.ClientAddress, outcome);

                // The failure that locked the account rotated the stamp: live connections must go too.
                if (result.LockedUntilUtc is not null)
                    await NotifyAsync(AccessActions.ChangeOwnPassword, Notification.Users(userId));
            }

            return result;
        }
        catch (Exception ex) when (RejectionCode(ex) is { } code)
        {
            DashboardAudit.AccessChangeRejected(_audit, audit.Actor, AccessActions.ChangeOwnPassword, UserKind, userId, audit.ClientAddress, code);
            throw;
        }
    }

    public Task<DashboardTeam> CreateTeamAsync(TeamDraft draft, AuditContext audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return AuditedAsync(audit, AccessActions.CreateTeam, TeamKind, null, () => InScopeAsync(async snapshot =>
        {
            var team = ValidateTeam(Guid.NewGuid(), draft, snapshot);
            EnsureAdministratorRemains(snapshot with { Teams = [.. snapshot.Teams, team] });
            await _store.CreateTeamAsync(team, cancellationToken);
            return new Committed<DashboardTeam>(team, team.Id, team.Name, DescribeTeam(team), Notification.Users([.. team.MemberIds]));
        }, cancellationToken));
    }

    /// <summary>Replaces the team, members and grants included; every member before or after is notified.</summary>
    public Task<DashboardTeam> UpdateTeamAsync(Guid id, TeamDraft draft, AuditContext audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return AuditedAsync(audit, AccessActions.UpdateTeam, TeamKind, id, () => InScopeAsync(async snapshot =>
        {
            var current = snapshot.Team(id);
            var team = ValidateTeam(id, draft, snapshot);
            EnsureAdministratorRemains(snapshot with { Teams = [.. snapshot.Teams.Select(t => t.Id == id ? team : t)] });
            await _store.UpdateTeamAsync(team, cancellationToken);
            return new Committed<DashboardTeam>(
                team, id, team.Name, DescribeTeam(team), Notification.Users([.. current.MemberIds.Union(team.MemberIds)]));
        }, cancellationToken));
    }

    public Task DeleteTeamAsync(Guid id, AuditContext audit, CancellationToken cancellationToken) =>
        AuditedAsync(audit, AccessActions.DeleteTeam, TeamKind, id, () => InScopeAsync(async snapshot =>
        {
            var current = snapshot.Team(id);
            EnsureAdministratorRemains(snapshot with { Teams = [.. snapshot.Teams.Where(t => t.Id != id)] });
            await _store.DeleteTeamAsync(id, cancellationToken);
            return new Committed<bool>(true, id, current.Name, "deleted", Notification.Users([.. current.MemberIds]));
        }, cancellationToken));

    public Task<DashboardRole> CreateRoleAsync(RoleDraft draft, AuditContext audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return AuditedAsync(audit, AccessActions.CreateRole, RoleKind, null, () => InScopeAsync(async snapshot =>
        {
            var role = ValidateRole(Guid.NewGuid(), draft, snapshot);
            await _store.CreateRoleAsync(role, cancellationToken);
            return new Committed<DashboardRole>(role, role.Id, role.Name, DescribeRole(role), Notification.None);
        }, cancellationToken));
    }

    /// <summary>Replaces a custom role. Anyone may hold it, so every live connection is notified.</summary>
    public Task<DashboardRole> UpdateRoleAsync(Guid id, RoleDraft draft, AuditContext audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return AuditedAsync(audit, AccessActions.UpdateRole, RoleKind, id, () => InScopeAsync(async snapshot =>
        {
            EnsureMutable(snapshot.Role(id));
            var role = ValidateRole(id, draft, snapshot);
            EnsureAdministratorRemains(snapshot with { Roles = [.. snapshot.Roles.Select(r => r.Id == id ? role : r)] });
            await _store.UpdateRoleAsync(role, cancellationToken);
            return new Committed<DashboardRole>(role, id, role.Name, DescribeRole(role), Notification.Everyone);
        }, cancellationToken));
    }

    /// <summary>
    /// Deletes a custom role that no user or team holds. Every live connection is notified all the same: the
    /// API key may act as that role (<c>Dashboard:ApiKeyRole</c>), and without it the key's connections would
    /// stay open on access it no longer has.
    /// </summary>
    public Task DeleteRoleAsync(Guid id, AuditContext audit, CancellationToken cancellationToken) =>
        AuditedAsync(audit, AccessActions.DeleteRole, RoleKind, id, () => InScopeAsync(async snapshot =>
        {
            var current = snapshot.Role(id);
            EnsureMutable(current);
            if (snapshot.Users.SelectMany(u => u.Grants).Concat(snapshot.Teams.SelectMany(t => t.Grants)).Any(g => g.RoleId == id))
                throw new IdentityRuleException(IdentityRuleCodes.RoleInUse, $"The role '{current.Name}' is still granted to a user or a team.");

            EnsureAdministratorRemains(snapshot with { Roles = [.. snapshot.Roles.Where(r => r.Id != id)] });
            await _store.DeleteRoleAsync(id, cancellationToken);
            return new Committed<bool>(true, id, current.Name, "deleted", Notification.Everyone);
        }, cancellationToken));

    private async Task<PasswordChangeResult> ChangeOwnPasswordCoreAsync(
        Guid userId, string? currentPassword, string? newPassword, CancellationToken cancellationToken)
    {
        var user = await _store.FindUserAsync(userId, cancellationToken)
            ?? throw new IdentityNotFoundException(IdentityEntityKind.User, userId);
        var errors = new ValidationErrors();
        AddPasswordErrors(errors, "newPassword", user.Username, newPassword, currentPassword);
        errors.ThrowIfAny();

        var now = _timeProvider.GetUtcNow();
        if (!user.IsEnabled || user.LockoutEndUtc > now)
            return new PasswordChangeResult(PasswordChangeStatus.Refused, null, null);

        if (!VerifiesCurrent(user, currentPassword))
        {
            var lockedUntil = await _store.RecordFailedSignInAsync(
                userId, _settings.LockoutMaxFailedAttempts, now, _settings.LockoutDuration, cancellationToken);
            if (lockedUntil is not null)
                await EndSessionsAsync(userId, cancellationToken);

            return new PasswordChangeResult(PasswordChangeStatus.WrongCurrentPassword, null, lockedUntil);
        }

        var hash = _hasher.HashPassword(user, newPassword!);
        return await InScopeAsync(snapshot =>
        {
            var current = snapshot.User(userId);
            // Disabled or locked since the first read: refused, as it would have been then.
            if (!current.IsEnabled || current.LockoutEndUtc > now)
                return Task.FromResult(new PasswordChangeResult(PasswordChangeStatus.Refused, null, null));

            // Changed since it was verified (an administrator's reset, another change, or a sign-in that
            // rehashed it): check again, now under the lock. Rare, so the hashing work under the lock is
            // acceptable here.
            if (!string.Equals(current.PasswordHash, user.PasswordHash, StringComparison.Ordinal) && !VerifiesCurrent(current, currentPassword))
                return Task.FromResult(new PasswordChangeResult(PasswordChangeStatus.WrongCurrentPassword, null, null));

            return WriteOwnPasswordAsync(current, hash, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Rotates the stamp, so every session of the user ends, the one that asked included, and on the server:
    /// a copy of the cookie that ignores the sign-out stops working too. The failure count and the lockout
    /// <see cref="IDashboardIdentityStore.RecordFailedSignInAsync"/> just wrote are re-read under the lock and kept.
    /// </summary>
    private Task EndSessionsAsync(Guid userId, CancellationToken cancellationToken) =>
        InScopeAsync(async snapshot =>
        {
            var updated = snapshot.User(userId) with
            {
                SecurityStamp = SecurityStamps.New(),
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
            };
            await _store.UpdateUserAsync(updated, cancellationToken);
            return true;
        }, cancellationToken);

    private async Task<PasswordChangeResult> WriteOwnPasswordAsync(DashboardUser current, string hash, CancellationToken cancellationToken)
    {
        var updated = current with
        {
            PasswordHash = hash,
            SecurityStamp = SecurityStamps.New(),
            MustChangePassword = false,
            FailedSignInCount = 0,
            LockoutEndUtc = null,
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
        };
        await _store.UpdateUserAsync(updated, cancellationToken);
        return new PasswordChangeResult(PasswordChangeStatus.Changed, updated, null);
    }

    private bool VerifiesCurrent(DashboardUser user, string? currentPassword) =>
        !string.IsNullOrEmpty(currentPassword)
        && currentPassword.Length <= PasswordPolicy.MaxLength
        && _hasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword) != PasswordVerificationResult.Failed;

    private void AddPasswordErrors(ValidationErrors errors, string path, string? username, string? password, string? currentPassword)
    {
        foreach (var message in _policy.Validate(username, password, currentPassword))
            errors.Add(path, message);
    }

    private static DashboardTeam ValidateTeam(Guid id, TeamDraft draft, Snapshot snapshot)
    {
        var errors = new ValidationErrors();
        var name = AccessValidation.RequiredText(errors, "name", draft.Name, AccessValidation.MaxNameLength);
        var description = AccessValidation.Description(errors, "description", draft.Description);
        var memberIds = draft.MemberIds ?? [];
        for (var i = 0; i < memberIds.Count; i++)
        {
            if (!snapshot.Users.Any(u => u.Id == memberIds[i]))
                errors.Add(AccessValidation.Indexed("memberIds", i), "No user has this id.");
            else if (memberIds.Take(i).Contains(memberIds[i]))
                errors.Add(AccessValidation.Indexed("memberIds", i), "This user is already a member.");
        }

        var grants = AccessValidation.Grants(errors, "grants", draft.Grants, snapshot.Roles);
        errors.ThrowIfAny();
        if (snapshot.Teams.Any(t => t.Id != id && SameName(t.Name, name!)))
            throw Taken(IdentityRuleCodes.NameTaken, "team", name!);

        return new DashboardTeam(id, name!, description, [.. memberIds], grants);
    }

    private static DashboardRole ValidateRole(Guid id, RoleDraft draft, Snapshot snapshot)
    {
        var errors = new ValidationErrors();
        var name = AccessValidation.RequiredText(errors, "name", draft.Name, AccessValidation.MaxNameLength);
        var description = AccessValidation.Description(errors, "description", draft.Description);
        var permissions = AccessValidation.RolePermissions(errors, "permissions", draft.Permissions);
        errors.ThrowIfAny();
        if (snapshot.Roles.Any(r => r.Id != id && SameName(r.Name, name!)) || BuiltInRoles.FindByName(name!) is { } builtIn && builtIn.Id != id)
            throw Taken(IdentityRuleCodes.NameTaken, "role", name!);

        return new DashboardRole(id, name!, description, IsBuiltIn: false, permissions);
    }

    private static void EnsureMutable(DashboardRole role)
    {
        if (role.IsBuiltIn || BuiltInRoles.Find(role.Id) is not null)
            throw new IdentityRuleException(IdentityRuleCodes.RoleImmutable, $"The built-in role '{role.Name}' cannot be changed or deleted.");
    }

    /// <summary>
    /// The invariant: at least one enabled user holds <c>access.manage</c> for all saga types. A user who must
    /// change their password counts, since that user regains the access by changing it; a locked user counts,
    /// since a lockout ends by itself.
    /// </summary>
    private static void EnsureAdministratorRemains(Snapshot proposed)
    {
        var hasAdministrator = proposed.Users.Any(u =>
            u.IsEnabled
            && AccessEvaluator.Evaluate(u with { MustChangePassword = false }, proposed.Teams, proposed.Roles).HasUnscoped(Permissions.AccessManage));
        if (!hasAdministrator)
        {
            throw new IdentityRuleException(
                IdentityRuleCodes.LastAdministrator,
                "This change would leave no enabled user who can manage access for all saga types.");
        }
    }

    private static bool SameName(string a, string b) =>
        string.Equals(IdentityNames.Normalize(a), IdentityNames.Normalize(b), StringComparison.Ordinal);

    private static IdentityRuleException Taken(string code, string noun, string name) =>
        new(code, $"A {noun} named '{name}' already exists; names are compared ignoring case.");

    private static string DescribeUser(DashboardUser user) =>
        $"isEnabled={user.IsEnabled}, mustChangePassword={user.MustChangePassword}, grants={user.Grants.Count}";

    private static string DescribeTeam(DashboardTeam team) =>
        $"members={team.MemberIds.Count}, grants={team.Grants.Count}";

    private static string DescribeRole(DashboardRole role) =>
        "permissions=" + string.Join(" ", role.Permissions);

    /// <summary>Runs <paramref name="work"/> on a freshly loaded snapshot inside one exclusive scope, and commits.</summary>
    private async Task<T> InScopeAsync<T>(Func<Snapshot, Task<T>> work, CancellationToken cancellationToken)
    {
        await using var scope = await _store.BeginExclusiveAsync(cancellationToken);
        var snapshot = new Snapshot(
            await _store.ListUsersAsync(cancellationToken),
            await _store.ListTeamsAsync(cancellationToken),
            await _store.ListRolesAsync(cancellationToken));
        T result;
        try
        {
            result = await work(snapshot);
        }
        catch (IdentityConflictException ex)
        {
            // The snapshot check already ran under the same lock; this is the store's own backstop.
            throw new IdentityRuleException(
                ex.Kind == IdentityEntityKind.User ? IdentityRuleCodes.UsernameTaken : IdentityRuleCodes.NameTaken, ex.Message);
        }

        await scope.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<T> AuditedAsync<T>(AuditContext audit, string action, string targetKind, Guid? targetId, Func<Task<Committed<T>>> change)
    {
        ArgumentNullException.ThrowIfNull(audit);
        Committed<T> committed;
        try
        {
            committed = await change();
        }
        catch (Exception ex) when (RejectionCode(ex) is { } code)
        {
            DashboardAudit.AccessChangeRejected(_audit, audit.Actor, action, targetKind, targetId, audit.ClientAddress, code);
            throw;
        }

        DashboardAudit.AccessChanged(
            _audit, audit.Actor, action, targetKind, committed.TargetId, committed.Target, audit.ClientAddress, Succeeded, committed.Details);
        await NotifyAsync(action, committed.Notify);
        return committed.Result;
    }

    private static string? RejectionCode(Exception exception) => exception switch
    {
        IdentityValidationException => "validation",
        IdentityRuleException rule => rule.Code,
        IdentityNotFoundException => "not_found",
        _ => null,
    };

    /// <summary>Best effort and after the commit: the change stands whatever the observer does.</summary>
    private async Task NotifyAsync(string action, Notification notification)
    {
        if (notification.IsNone)
            return;

        try
        {
            if (notification.All)
                await _observer.AllUsersChangedAsync(CancellationToken.None);
            else
                await _observer.UsersChangedAsync(notification.UserIds, CancellationToken.None);
        }
        catch (Exception ex)
        {
            DashboardAudit.AccessChangeNotificationFailed(_audit, ex, action);
        }
    }

    private sealed record Snapshot(IReadOnlyList<DashboardUser> Users, IReadOnlyList<DashboardTeam> Teams, IReadOnlyList<DashboardRole> Roles)
    {
        public DashboardUser User(Guid id) =>
            Users.FirstOrDefault(u => u.Id == id) ?? throw new IdentityNotFoundException(IdentityEntityKind.User, id);

        public DashboardTeam Team(Guid id) =>
            Teams.FirstOrDefault(t => t.Id == id) ?? throw new IdentityNotFoundException(IdentityEntityKind.Team, id);

        public DashboardRole Role(Guid id) =>
            Roles.FirstOrDefault(r => r.Id == id) ?? throw new IdentityNotFoundException(IdentityEntityKind.Role, id);

        public Snapshot WithUser(DashboardUser user) => this with { Users = [.. Users.Select(u => u.Id == user.Id ? user : u)] };
    }

    private sealed record Notification(bool All, IReadOnlyCollection<Guid> UserIds)
    {
        public static Notification None { get; } = new(All: false, []);

        public static Notification Everyone { get; } = new(All: true, []);

        public bool IsNone => !All && UserIds.Count == 0;

        public static Notification Users(params Guid[] userIds) => new(All: false, userIds);
    }

    private sealed record Committed<T>(T Result, Guid TargetId, string Target, string Details, Notification Notify);
}
