namespace VSaga.Dashboard.Identity.EFCore;

/// <summary>A row of <c>Users</c>. Mapped to and from <see cref="Model.DashboardUser"/> by the store.</summary>
public sealed class DashboardUserEntity
{
    public Guid Id { get; set; }

    public string Username { get; set; } = string.Empty;

    /// <summary><see cref="Model.IdentityNames.Normalize"/> of <see cref="Username"/>; carries the unique index.</summary>
    public string NormalizedUsername { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public string SecurityStamp { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    public bool MustChangePassword { get; set; }

    public int FailedSignInCount { get; set; }

    public DateTimeOffset? LockoutEndUtc { get; set; }

    public DateTimeOffset? LastSignInAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public ICollection<DashboardUserGrantEntity> Grants { get; } = [];
}

/// <summary>A row of <c>Teams</c>.</summary>
public sealed class DashboardTeamEntity
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary><see cref="Model.IdentityNames.Normalize"/> of <see cref="Name"/>; carries the unique index.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public ICollection<DashboardTeamMemberEntity> Members { get; } = [];

    public ICollection<DashboardTeamGrantEntity> Grants { get; } = [];
}

/// <summary>A row of <c>TeamMembers</c>: one user in one team. Removed with either side.</summary>
public sealed class DashboardTeamMemberEntity
{
    public Guid TeamId { get; set; }

    public Guid UserId { get; set; }
}

/// <summary>A row of <c>Roles</c>.</summary>
public sealed class DashboardRoleEntity
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary><see cref="Model.IdentityNames.Normalize"/> of <see cref="Name"/>; carries the unique index.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsBuiltIn { get; set; }

    /// <summary>A primitive collection: one JSON text column. Evaluated in memory, never queried inside.</summary>
    public IList<string> Permissions { get; set; } = [];
}

/// <summary>A row of <c>UserGrants</c>: one role held by one user. At most one per (user, role).</summary>
public sealed class DashboardUserGrantEntity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid RoleId { get; set; }

    public bool AllSagaTypes { get; set; }

    /// <summary>A primitive collection: one JSON text column, stored exactly as given.</summary>
    public IList<string> SagaTypes { get; set; } = [];
}

/// <summary>A row of <c>TeamGrants</c>: one role held by one team. At most one per (team, role).</summary>
public sealed class DashboardTeamGrantEntity
{
    public Guid Id { get; set; }

    public Guid TeamId { get; set; }

    public Guid RoleId { get; set; }

    public bool AllSagaTypes { get; set; }

    /// <summary>A primitive collection: one JSON text column, stored exactly as given.</summary>
    public IList<string> SagaTypes { get; set; } = [];
}

/// <summary>A row of <c>DataProtectionKeys</c>: one serialised Data Protection key.</summary>
public sealed class DataProtectionKeyEntity
{
    public int Id { get; set; }

    public string? FriendlyName { get; set; }

    public string Xml { get; set; } = string.Empty;
}
