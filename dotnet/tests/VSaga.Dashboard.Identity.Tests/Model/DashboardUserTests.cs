using VSaga.Dashboard.Identity.Model;

namespace VSaga.Dashboard.Identity.Tests.Model;

/// <summary>A user that reaches a log line, an exception message or an audit event must not carry its secrets.</summary>
public sealed class DashboardUserTests
{
    [Fact]
    public void ToString_LeavesOutThePasswordHashAndTheSecurityStamp()
    {
        var at = new DateTimeOffset(2026, 10, 2, 9, 30, 0, TimeSpan.Zero);
        var user = new DashboardUser(
            new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"),
            "alice",
            "Alice Example",
            PasswordHash: "AQAAAAIAAYagAAAAEsecret-hash-value",
            SecurityStamp: "secret-stamp-value",
            IsEnabled: true,
            MustChangePassword: false,
            FailedSignInCount: 2,
            LockoutEndUtc: null,
            LastSignInAtUtc: at,
            CreatedAtUtc: at,
            UpdatedAtUtc: at,
            Grants: [new AccessGrant(BuiltInRoles.ViewerId, AllSagaTypes: true, [])]);

        var text = user.ToString();

        Assert.DoesNotContain("secret-hash-value", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-stamp-value", text, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(DashboardUser.PasswordHash), text, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(DashboardUser.SecurityStamp), text, StringComparison.Ordinal);
        Assert.StartsWith("DashboardUser { Id = 0f8fad5b-d9cb-469f-a165-70867728950e, Username = alice, DisplayName = Alice Example,", text, StringComparison.Ordinal);
        Assert.Contains("FailedSignInCount = 2", text, StringComparison.Ordinal);
        Assert.Contains("LastSignInAtUtc = 2026-10-02T09:30:00.0000000+00:00", text, StringComparison.Ordinal);
        Assert.EndsWith("GrantCount = 1 }", text, StringComparison.Ordinal);
    }
}
