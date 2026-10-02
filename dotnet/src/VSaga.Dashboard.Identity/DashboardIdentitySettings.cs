using Microsoft.Extensions.Configuration;

namespace VSaga.Dashboard.Identity;

/// <summary>
/// Where the dashboard's SQLite identity database lives, read once from configuration while composing. A
/// path that cannot be resolved does not fail composition: the API still starts, and the reason is what
/// <see cref="Services.IdentityStartup"/> reports through the <c>identity</c> health check, so a
/// misconfigured volume never takes the saga views down with it.
/// </summary>
/// <param name="DatabasePath">The absolute path of the database file, or null when it cannot be resolved.</param>
/// <param name="PathProblem">Why <paramref name="DatabasePath"/> is null, naming the key to set; null when it resolved.</param>
public sealed record DashboardIdentitySettings(string? DatabasePath, string? PathProblem)
{
    /// <summary>Which identity store the API composes; <see cref="SqliteProvider"/>, the only one, by default.</summary>
    public const string ProviderKey = "Dashboard:Identity:Provider";

    public const string SqliteProvider = "Sqlite";

    public const string SqlitePathKey = "Dashboard:Identity:Sqlite:Path";

    /// <summary>
    /// Set to <c>true</c> by the official .NET container images. Read through configuration (the API's
    /// configuration includes every environment variable) so a test can stand in for a container.
    /// </summary>
    public const string RunningInContainerKey = "DOTNET_RUNNING_IN_CONTAINER";

    /// <summary>
    /// Resolves <see cref="SqlitePathKey"/>. Set, it is made absolute. Unset inside a container, it is an
    /// error: the only safe place for the file is a volume the operator chose, and a default under the
    /// application directory would vanish with the container and every user in it. Unset outside one, it is
    /// <c>{LocalApplicationData}/vSaga/dashboard/identity.db</c>.
    /// </summary>
    public static DashboardIdentitySettings ReadSqlite(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration[SqlitePathKey];
        if (!string.IsNullOrWhiteSpace(configured))
            return new DashboardIdentitySettings(Path.GetFullPath(configured.Trim()), PathProblem: null);

        if (string.Equals(configuration[RunningInContainerKey], "true", StringComparison.OrdinalIgnoreCase))
        {
            return new DashboardIdentitySettings(
                DatabasePath: null,
                $"{SqlitePathKey} is not set. In a container it must name the identity database file on a volume "
                + "(the image sets /var/lib/vsaga-dashboard/identity.db); there is no default inside the container.");
        }

        // DoNotVerify: on Unix the default option answers empty when ~/.local/share does not exist yet (a fresh
        // service account), and IdentityStartup creates the missing folders itself, owner-only.
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(localData))
        {
            return new DashboardIdentitySettings(
                DatabasePath: null,
                $"{SqlitePathKey} is not set and this account has no local application data folder to default to; set it.");
        }

        return new DashboardIdentitySettings(Path.Combine(localData, "vSaga", "dashboard", "identity.db"), PathProblem: null);
    }
}
