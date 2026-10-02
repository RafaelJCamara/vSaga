namespace VSaga.Dashboard.Identity.Model;

/// <summary>
/// The three roles defined in code. Their ids are fixed so a grant written by one version still names the
/// same role in the next, and start-up upserts them from here, so their permission sets always match this
/// file. They are immutable through the administration API.
/// </summary>
public static class BuiltInRoles
{
    public static readonly Guid AdministratorId = new("a0000000-0000-0000-0000-000000000001");

    public static readonly Guid OperatorId = new("a0000000-0000-0000-0000-000000000002");

    public static readonly Guid ViewerId = new("a0000000-0000-0000-0000-000000000003");

    public static DashboardRole Administrator { get; } = new(
        AdministratorId,
        "Administrator",
        "Everything: view sagas and their data, retry, and manage users, teams and roles.",
        IsBuiltIn: true,
        [Permissions.SagasView, Permissions.SagasData, Permissions.SagasRetry, Permissions.AccessManage]);

    public static DashboardRole Operator { get; } = new(
        OperatorId,
        "Operator",
        "View sagas and their data, and retry failed sagas.",
        IsBuiltIn: true,
        [Permissions.SagasView, Permissions.SagasData, Permissions.SagasRetry]);

    public static DashboardRole Viewer { get; } = new(
        ViewerId,
        "Viewer",
        "View sagas and their data.",
        IsBuiltIn: true,
        [Permissions.SagasView, Permissions.SagasData]);

    /// <summary>Administrator, Operator, Viewer.</summary>
    public static IReadOnlyList<DashboardRole> All { get; } = [Administrator, Operator, Viewer];

    /// <summary>The built-in role with this id, or null.</summary>
    public static DashboardRole? Find(Guid id) => All.FirstOrDefault(r => r.Id == id);

    /// <summary>The built-in role with this name, compared as <see cref="IdentityNames.Normalize"/> does, or null.</summary>
    public static DashboardRole? FindByName(string name)
    {
        var normalized = IdentityNames.Normalize(name);
        return All.FirstOrDefault(r => string.Equals(IdentityNames.Normalize(r.Name), normalized, StringComparison.Ordinal));
    }
}
