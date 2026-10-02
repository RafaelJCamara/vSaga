using System.Security.Claims;
using VSaga.Dashboard.Identity.Model;

namespace VSaga.Dashboard.Identity.Services;

/// <summary>
/// The claims a dashboard principal carries, and the two ways one is made. A session holds only who the user
/// is and the security stamp it was issued under; what the user may do is never in the cookie, it is
/// resolved from the store on every request (<see cref="ICallerAccessResolver"/>).
/// </summary>
public static class DashboardClaims
{
    /// <summary>The user's id, or <see cref="CallerAccess.ApiKeyUsername"/> for the API key.</summary>
    public const string Subject = "sub";

    /// <summary>The username.</summary>
    public const string Name = "name";

    /// <summary>The user's security stamp when the session was issued.</summary>
    public const string SecurityStamp = "vsaga:stamp";

    /// <summary>The authentication type of an API-key principal; a session's principal never has it.</summary>
    public const string ApiKeyAuthenticationType = "ApiKey";

    /// <summary>The principal a session is issued for: subject, username and the user's current stamp.</summary>
    public static ClaimsPrincipal ForUser(DashboardUser user, string authenticationType)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Principal(
            authenticationType,
            new Claim(Subject, user.Id.ToString("D")),
            new Claim(Name, user.Username),
            new Claim(SecurityStamp, user.SecurityStamp));
    }

    /// <summary>The principal of a request that presented the right API key.</summary>
    public static ClaimsPrincipal ForApiKey() =>
        Principal(ApiKeyAuthenticationType, new Claim(Subject, CallerAccess.ApiKeyUsername), new Claim(Name, CallerAccess.ApiKeyUsername));

    private static ClaimsPrincipal Principal(string authenticationType, params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType, Name, roleType: null));
}
