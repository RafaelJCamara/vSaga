using Microsoft.EntityFrameworkCore;

namespace VSaga.Dashboard.Identity.EFCore;

/// <summary>
/// The dashboard identity schema: users, teams, roles, their grants, team membership and the Data
/// Protection key ring. Its own database, separate from the saga store, so the dashboard's users never
/// depend on which saga persistence provider a deployment chose.
/// </summary>
public sealed class DashboardIdentityDbContext(DbContextOptions<DashboardIdentityDbContext> options) : DbContext(options)
{
    /// <summary>The length the API allows for a username; the normalised form is never longer.</summary>
    internal const int UsernameMaxLength = 64;

    /// <summary>The length the API allows for team and role names.</summary>
    internal const int NameMaxLength = 64;

    internal const int DisplayNameMaxLength = 128;

    internal const int DescriptionMaxLength = 256;

    public DbSet<DashboardUserEntity> Users => Set<DashboardUserEntity>();

    public DbSet<DashboardTeamEntity> Teams => Set<DashboardTeamEntity>();

    public DbSet<DashboardTeamMemberEntity> TeamMembers => Set<DashboardTeamMemberEntity>();

    public DbSet<DashboardRoleEntity> Roles => Set<DashboardRoleEntity>();

    public DbSet<DashboardUserGrantEntity> UserGrants => Set<DashboardUserGrantEntity>();

    public DbSet<DashboardTeamGrantEntity> TeamGrants => Set<DashboardTeamGrantEntity>();

    public DbSet<DataProtectionKeyEntity> DataProtectionKeys => Set<DataProtectionKeyEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureUsers(modelBuilder);
        ConfigureTeams(modelBuilder);
        ConfigureRoles(modelBuilder);
        ConfigureGrants(modelBuilder);

        modelBuilder.Entity<DataProtectionKeyEntity>(b =>
        {
            b.ToTable("DataProtectionKeys");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();
            b.Property(x => x.FriendlyName).HasMaxLength(256);
            b.Property(x => x.Xml).IsRequired();
        });
    }

    private static void ConfigureUsers(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DashboardUserEntity>(b =>
        {
            b.ToTable("Users");
            b.HasKey(x => x.Id);
            b.Property(x => x.Username).HasMaxLength(UsernameMaxLength).IsRequired();
            b.Property(x => x.NormalizedUsername).HasMaxLength(UsernameMaxLength).IsRequired();
            b.Property(x => x.DisplayName).HasMaxLength(DisplayNameMaxLength).IsRequired();
            b.Property(x => x.PasswordHash).IsRequired();
            b.Property(x => x.SecurityStamp).HasMaxLength(64).IsRequired();
            b.HasIndex(x => x.NormalizedUsername).IsUnique();
            b.HasMany(x => x.Grants).WithOne().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureTeams(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DashboardTeamEntity>(b =>
        {
            b.ToTable("Teams");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(NameMaxLength).IsRequired();
            b.Property(x => x.NormalizedName).HasMaxLength(NameMaxLength).IsRequired();
            b.Property(x => x.Description).HasMaxLength(DescriptionMaxLength);
            b.HasIndex(x => x.NormalizedName).IsUnique();
            b.HasMany(x => x.Members).WithOne().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(x => x.Grants).WithOne().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DashboardTeamMemberEntity>(b =>
        {
            b.ToTable("TeamMembers");
            b.HasKey(x => new { x.TeamId, x.UserId });
            // "Which teams is this user in?" for access evaluation; the key leads with TeamId.
            b.HasIndex(x => x.UserId);
            b.HasOne<DashboardUserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureRoles(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DashboardRoleEntity>(b =>
        {
            b.ToTable("Roles");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(NameMaxLength).IsRequired();
            b.Property(x => x.NormalizedName).HasMaxLength(NameMaxLength).IsRequired();
            b.Property(x => x.Description).HasMaxLength(DescriptionMaxLength);
            b.HasIndex(x => x.NormalizedName).IsUnique();
        });
    }

    /// <summary>
    /// A grant goes with its owner and never outlives its role: deleting a role still named by a grant is
    /// refused by the restrict key, which backs the store's own in-use check.
    /// </summary>
    private static void ConfigureGrants(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DashboardUserGrantEntity>(b =>
        {
            b.ToTable("UserGrants");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.UserId, x.RoleId }).IsUnique();
            b.HasOne<DashboardRoleEntity>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DashboardTeamGrantEntity>(b =>
        {
            b.ToTable("TeamGrants");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TeamId, x.RoleId }).IsUnique();
            b.HasOne<DashboardRoleEntity>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
