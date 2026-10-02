namespace VSaga.Dashboard.Identity.Model;

/// <summary>One entry of the permission catalogue, as <c>GET /api/admin/permissions</c> describes it.</summary>
/// <param name="Key">The stable key stored in roles and checked by the API.</param>
/// <param name="Name">The label the dashboard shows.</param>
/// <param name="Description">What the permission allows, in plain words.</param>
/// <param name="Scopable">
/// True when a grant for named saga types confers it for those types. False for <see cref="Permissions.AccessManage"/>,
/// which counts only in a grant for all saga types.
/// </param>
/// <param name="Implies">Keys held for the same scope whenever this one is.</param>
public sealed record PermissionDefinition(
    string Key,
    string Name,
    string Description,
    bool Scopable,
    IReadOnlyList<string> Implies);

/// <summary>The four permissions a role can combine. Nothing else is a valid permission key.</summary>
public static class Permissions
{
    public const string SagasView = "sagas.view";

    public const string SagasData = "sagas.data";

    public const string SagasRetry = "sagas.retry";

    public const string AccessManage = "access.manage";

    /// <summary>The catalogue, in the order the dashboard lists it.</summary>
    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(
            SagasView,
            "View sagas",
            "List sagas and open their detail, timeline and map without saga data, message payloads or error text; see children, correlations, saga types and the retry plan.",
            Scopable: true,
            Implies: []),
        new(
            SagasData,
            "View saga data",
            "See saga state, message payloads and error messages.",
            Scopable: true,
            Implies: [SagasView]),
        new(
            SagasRetry,
            "Retry sagas",
            "Re-run the failed step of a failed or timed-out saga.",
            Scopable: true,
            Implies: [SagasView]),
        new(
            AccessManage,
            "Manage access",
            "Manage users, teams and roles. Counts only in a grant for all saga types.",
            Scopable: false,
            Implies: []),
    ];

    /// <summary>The catalogue entry for <paramref name="key"/> (ordinal), or null when it is not a permission.</summary>
    public static PermissionDefinition? Find(string key) =>
        All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal));

    /// <summary>True when <paramref name="key"/> is one of the four keys, compared ordinally.</summary>
    public static bool IsKnown(string key) => Find(key) is not null;
}
