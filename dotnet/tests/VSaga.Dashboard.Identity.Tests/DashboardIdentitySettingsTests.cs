using Microsoft.Extensions.Configuration;

namespace VSaga.Dashboard.Identity.Tests;

public sealed class DashboardIdentitySettingsTests
{
    [Fact]
    public void ReadSqlite_WithAPath_MakesItAbsolute()
    {
        var settings = DashboardIdentitySettings.ReadSqlite(Configuration(path: " identity/dashboard.db ", inContainer: "true"));

        Assert.Equal(Path.GetFullPath("identity/dashboard.db"), settings.DatabasePath);
        Assert.Null(settings.PathProblem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReadSqlite_InAContainer_WithoutAPath_IsAProblemNamingTheKey_NotAFallback(string? path)
    {
        var settings = DashboardIdentitySettings.ReadSqlite(Configuration(path, inContainer: "True"));

        Assert.Null(settings.DatabasePath);
        Assert.StartsWith("Dashboard:Identity:Sqlite:Path is not set. In a container", settings.PathProblem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void ReadSqlite_OutsideAContainer_WithoutAPath_DefaultsUnderLocalApplicationData(string? inContainer)
    {
        var settings = DashboardIdentitySettings.ReadSqlite(Configuration(path: null, inContainer));

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        Assert.Equal(Path.Combine(localData, "vSaga", "dashboard", "identity.db"), settings.DatabasePath);
        Assert.Null(settings.PathProblem);
    }

    private static IConfiguration Configuration(string? path, string? inContainer) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [DashboardIdentitySettings.SqlitePathKey] = path,
                [DashboardIdentitySettings.RunningInContainerKey] = inContainer,
            })
            .Build();
}
