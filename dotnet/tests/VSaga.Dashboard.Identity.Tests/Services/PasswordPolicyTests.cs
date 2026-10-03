using Microsoft.Extensions.Configuration;
using VSaga.Dashboard.Identity.Services;

namespace VSaga.Dashboard.Identity.Tests.Services;

public sealed class PasswordPolicyTests
{
    private static readonly PasswordPolicy Policy = new(DashboardSecuritySettings.Default);

    [Fact]
    public void Validate_AcceptsTheMinimumAndTheMaximumLength()
    {
        Assert.Empty(Policy.Validate("alice", new string('a', 12)));
        Assert.Empty(Policy.Validate("alice", new string('a', 128)));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(129)]
    public void Validate_RejectsALengthOutsideTheRange(int length)
    {
        var error = Assert.Single(Policy.Validate("alice", new string('a', length)));
        Assert.Contains("12 to 128", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Validate_RejectsAMissingPassword(string? password)
    {
        Assert.Single(Policy.Validate("alice", password));
    }

    [Fact]
    public void Validate_TakesTheMinimumFromSettings()
    {
        var policy = new PasswordPolicy(DashboardSecuritySettings.Default with { PasswordMinLength = 20 });

        Assert.Equal(20, policy.MinLength);
        Assert.Single(policy.Validate("alice", new string('a', 19)));
        Assert.Empty(policy.Validate("alice", new string('a', 20)));
    }

    [Theory]
    [InlineData("administrator", "administrator")]
    [InlineData("administrator", "ADMINISTRATOR")]
    [InlineData("Administrator", " administrator ")]
    public void Validate_RejectsThePasswordEqualToTheUsernameIgnoringCase(string username, string password)
    {
        var error = Assert.Single(Policy.Validate(username, password));
        Assert.Contains("username", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsANewPasswordEqualToTheCurrentOne()
    {
        var error = Assert.Single(Policy.Validate("alice", "the same long password", currentPassword: "the same long password"));
        Assert.Contains("differ", error, StringComparison.Ordinal);
        Assert.Empty(Policy.Validate("alice", "the same long password", currentPassword: "THE SAME LONG PASSWORD"));
    }
}

public sealed class DashboardSecuritySettingsTests
{
    [Fact]
    public void Read_WithNothingSet_GivesTheDefaults()
    {
        var settings = DashboardSecuritySettings.Read(Configuration());

        Assert.Equal(DashboardSecuritySettings.Default, settings);
        Assert.Equal(12, settings.PasswordMinLength);
        Assert.Equal(5, settings.LockoutMaxFailedAttempts);
        Assert.Equal(TimeSpan.FromMinutes(15), settings.LockoutDuration);
    }

    [Fact]
    public void Read_TakesEveryKey()
    {
        var settings = DashboardSecuritySettings.Read(Configuration(
            (DashboardSecuritySettings.PasswordMinLengthKey, "16"),
            (DashboardSecuritySettings.LockoutMaxFailedAttemptsKey, "0"),
            (DashboardSecuritySettings.LockoutMinutesKey, " 30 ")));

        Assert.Equal(new DashboardSecuritySettings(16, 0, TimeSpan.FromMinutes(30)), settings);
    }

    [Theory]
    [InlineData(DashboardSecuritySettings.PasswordMinLengthKey, "7")]
    [InlineData(DashboardSecuritySettings.PasswordMinLengthKey, "129")]
    [InlineData(DashboardSecuritySettings.LockoutMaxFailedAttemptsKey, "-1")]
    [InlineData(DashboardSecuritySettings.LockoutMaxFailedAttemptsKey, "101")]
    [InlineData(DashboardSecuritySettings.LockoutMinutesKey, "0")]
    [InlineData(DashboardSecuritySettings.LockoutMinutesKey, "1441")]
    [InlineData(DashboardSecuritySettings.LockoutMinutesKey, "fifteen")]
    [InlineData(DashboardSecuritySettings.PasswordMinLengthKey, "12.5")]
    [InlineData(DashboardSecuritySettings.SessionIdleTimeoutMinutesKey, "0")]
    [InlineData(DashboardSecuritySettings.SessionIdleTimeoutMinutesKey, "10081")]
    [InlineData(DashboardSecuritySettings.SessionAbsoluteTimeoutHoursKey, "0")]
    [InlineData(DashboardSecuritySettings.SessionAbsoluteTimeoutHoursKey, "721")]
    [InlineData(DashboardSecuritySettings.RequireHttpsKey, "yes")]
    [InlineData(DashboardSecuritySettings.SessionCookieNameKey, "vsaga session")]
    [InlineData(DashboardSecuritySettings.SessionCookieNameKey, "vsaga;session")]
    [InlineData(DashboardSecuritySettings.SessionCookieNameKey, "__Host-vsaga.session")]
    [InlineData(DashboardSecuritySettings.ApiKeyRoleKey, "r0123456789012345678901234567890123456789012345678901234567890123")]
    [InlineData(DashboardSecuritySettings.AuthPerMinuteKey, "0")]
    [InlineData(DashboardSecuritySettings.AuthPerMinuteKey, "1001")]
    public void Read_InvalidValue_FailsNamingTheKeyAndTheValue(string key, string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => DashboardSecuritySettings.Read(Configuration((key, value))));

        Assert.Contains(key, error.Message, StringComparison.Ordinal);
        Assert.Contains($"'{value}'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_CookieName_Accepts128CharactersAndRejects129()
    {
        // Compose names the cookie vsaga.session.<project>, and the default project name is the checkout
        // folder's name, so the limit leaves room for a long folder name.
        var longest = "vsaga.session." + new string('p', 114);
        var tooLong = longest + "p";

        var settings = DashboardSecuritySettings.Read(Configuration((DashboardSecuritySettings.SessionCookieNameKey, longest)));
        var error = Assert.Throws<InvalidOperationException>(
            () => DashboardSecuritySettings.Read(Configuration((DashboardSecuritySettings.SessionCookieNameKey, tooLong))));

        Assert.Equal(128, longest.Length);
        Assert.Equal(longest, settings.SessionCookieName);
        Assert.Contains(DashboardSecuritySettings.SessionCookieNameKey, error.Message, StringComparison.Ordinal);
        Assert.Contains("1 to 128", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_WithNothingSet_GivesTheSessionAndApiKeyDefaults()
    {
        var settings = DashboardSecuritySettings.Read(Configuration());

        Assert.Equal("vsaga.session", settings.SessionCookieName);
        Assert.Equal("vsaga.session", settings.EffectiveSessionCookieName);
        Assert.Equal(TimeSpan.FromMinutes(480), settings.SessionIdleTimeout);
        Assert.Equal(TimeSpan.FromHours(24), settings.SessionAbsoluteTimeout);
        Assert.False(settings.RequireHttps);
        Assert.Equal("Viewer", settings.ApiKeyRole);
        Assert.Equal(20, settings.AuthPerMinute);
    }

    [Fact]
    public void Read_TakesTheSessionAndApiKeyKeys()
    {
        var settings = DashboardSecuritySettings.Read(Configuration(
            (DashboardSecuritySettings.SessionCookieNameKey, " vsaga.session.overlay_2 "),
            (DashboardSecuritySettings.SessionIdleTimeoutMinutesKey, "30"),
            (DashboardSecuritySettings.SessionAbsoluteTimeoutHoursKey, "8"),
            (DashboardSecuritySettings.RequireHttpsKey, "True"),
            (DashboardSecuritySettings.ApiKeyRoleKey, " Operator "),
            (DashboardSecuritySettings.AuthPerMinuteKey, "5")));

        Assert.Equal("vsaga.session.overlay_2", settings.SessionCookieName);
        Assert.Equal("__Host-vsaga.session.overlay_2", settings.EffectiveSessionCookieName);
        Assert.Equal(TimeSpan.FromMinutes(30), settings.SessionIdleTimeout);
        Assert.Equal(TimeSpan.FromHours(8), settings.SessionAbsoluteTimeout);
        Assert.True(settings.RequireHttps);
        Assert.Equal("Operator", settings.ApiKeyRole);
        Assert.Equal(5, settings.AuthPerMinute);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}

public sealed class SecurityStampsTests
{
    [Fact]
    public void New_Is128RandomBitsAsHex()
    {
        var stamps = Enumerable.Range(0, 100).Select(_ => SecurityStamps.New()).ToList();

        Assert.All(stamps, s => Assert.Matches("^[0-9A-F]{32}$", s));
        Assert.Equal(stamps.Count, stamps.Distinct(StringComparer.Ordinal).Count());
    }
}
