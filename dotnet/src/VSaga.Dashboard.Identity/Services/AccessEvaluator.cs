using VSaga.Dashboard.Identity.Model;

namespace VSaga.Dashboard.Identity.Services;

/// <summary>
/// The saga types one permission is held for: every type (including types that have not run yet), a set
/// of exact names compared ordinally, or none.
/// </summary>
public sealed class SagaTypeScope
{
    private static readonly IReadOnlySet<string> NoTypes = new HashSet<string>(StringComparer.Ordinal);

    private SagaTypeScope(bool isAll, IReadOnlySet<string> sagaTypes)
    {
        IsAll = isAll;
        SagaTypes = sagaTypes;
    }

    /// <summary>The permission is not held for any saga type.</summary>
    public static SagaTypeScope None { get; } = new(isAll: false, NoTypes);

    /// <summary>The permission is held for every saga type.</summary>
    public static SagaTypeScope All { get; } = new(isAll: true, NoTypes);

    /// <summary>True for every saga type; <see cref="SagaTypes"/> is then empty.</summary>
    public bool IsAll { get; }

    /// <summary>The named saga types, compared ordinally; empty when <see cref="IsAll"/> or when nothing is held.</summary>
    public IReadOnlySet<string> SagaTypes { get; }

    /// <summary>True when the scope covers no saga type at all.</summary>
    public bool IsEmpty => !IsAll && SagaTypes.Count == 0;

    /// <summary>The scope holding exactly these names (ordinal); <see cref="None"/> when there are none.</summary>
    public static SagaTypeScope Of(IEnumerable<string> sagaTypes)
    {
        var set = new HashSet<string>(sagaTypes, StringComparer.Ordinal);
        return set.Count == 0 ? None : new SagaTypeScope(isAll: false, set);
    }

    /// <summary>True when <paramref name="sagaType"/> is covered, compared ordinally.</summary>
    public bool Contains(string sagaType) => IsAll || SagaTypes.Contains(sagaType);
}

/// <summary>
/// What a caller may do: for each permission, the saga types it is held for. Built by
/// <see cref="AccessEvaluator"/>; immutable.
/// </summary>
public sealed class EffectiveAccess
{
    private readonly IReadOnlyDictionary<string, SagaTypeScope> _scopes;

    internal EffectiveAccess(IReadOnlyDictionary<string, SagaTypeScope> scopes) => _scopes = scopes;

    /// <summary>No permission for any saga type.</summary>
    public static EffectiveAccess None { get; } = new(new Dictionary<string, SagaTypeScope>(StringComparer.Ordinal));

    /// <summary>The permissions held for at least one saga type, in catalogue order.</summary>
    public IReadOnlyList<string> Permissions =>
        [.. Model.Permissions.All.Select(p => p.Key).Where(HasAny)];

    /// <summary>True when <paramref name="permission"/> is held for <paramref name="sagaType"/> (ordinal).</summary>
    public bool Has(string permission, string sagaType) => ScopeFor(permission).Contains(sagaType);

    /// <summary>True when <paramref name="permission"/> is held for at least one saga type.</summary>
    public bool HasAny(string permission) => !ScopeFor(permission).IsEmpty;

    /// <summary>True when <paramref name="permission"/> is held for every saga type.</summary>
    public bool HasUnscoped(string permission) => ScopeFor(permission).IsAll;

    /// <summary>The saga types <paramref name="permission"/> is held for; <see cref="SagaTypeScope.None"/> when not held.</summary>
    public SagaTypeScope ScopeFor(string permission) =>
        _scopes.TryGetValue(permission, out var scope) ? scope : SagaTypeScope.None;
}

/// <summary>
/// Turns grants into <see cref="EffectiveAccess"/>. Pure: everything it needs is passed in, so the
/// administration service can evaluate a proposed snapshot exactly as a request will evaluate the stored one.
/// </summary>
public static class AccessEvaluator
{
    /// <summary>
    /// The union of the user's own grants and the grants of every team in <paramref name="teams"/> that lists
    /// the user as a member. A disabled user, and a user who must change their password, hold nothing.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="teams">Teams to consider; teams the user is not a member of are ignored.</param>
    /// <param name="roles">The roles grants may name; a grant naming a role not listed confers nothing.</param>
    public static EffectiveAccess Evaluate(DashboardUser user, IEnumerable<DashboardTeam> teams, IEnumerable<DashboardRole> roles)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(teams);
        if (!user.IsEnabled || user.MustChangePassword)
            return EffectiveAccess.None;

        var grants = user.Grants.Concat(teams.Where(t => t.MemberIds.Contains(user.Id)).SelectMany(t => t.Grants));
        return EvaluateGrants(grants, roles);
    }

    /// <summary>
    /// The access a set of grants confers. For every grant and each catalogue permission of its role:
    /// <c>access.manage</c> counts only in a grant for all saga types; the grant's scope joins the
    /// permission's scope; and every permission the catalogue says it implies joins with the same scope.
    /// Keys that are not in the catalogue are ignored.
    /// </summary>
    public static EffectiveAccess EvaluateGrants(IEnumerable<AccessGrant> grants, IEnumerable<DashboardRole> roles)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(roles);

        var rolesById = roles.ToDictionary(r => r.Id);
        var all = new HashSet<string>(StringComparer.Ordinal);
        var named = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var grant in grants)
        {
            if (!rolesById.TryGetValue(grant.RoleId, out var role))
                continue;

            foreach (var permission in role.Permissions.Select(Permissions.Find).OfType<PermissionDefinition>())
            {
                if (!permission.Scopable && !grant.AllSagaTypes)
                    continue;

                foreach (var key in permission.Implies.Prepend(permission.Key))
                {
                    if (grant.AllSagaTypes)
                        all.Add(key);
                    else
                        AddNamed(named, key, grant.SagaTypes);
                }
            }
        }

        var scopes = new Dictionary<string, SagaTypeScope>(StringComparer.Ordinal);
        foreach (var key in all)
            scopes[key] = SagaTypeScope.All;
        foreach (var (key, types) in named.Where(n => !all.Contains(n.Key)))
            scopes[key] = SagaTypeScope.Of(types);

        return new EffectiveAccess(scopes);
    }

    private static void AddNamed(Dictionary<string, HashSet<string>> named, string key, IReadOnlyList<string> sagaTypes)
    {
        if (!named.TryGetValue(key, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            named[key] = set;
        }

        set.UnionWith(sagaTypes);
    }
}
