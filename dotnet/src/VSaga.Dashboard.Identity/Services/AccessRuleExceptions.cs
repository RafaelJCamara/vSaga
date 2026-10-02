namespace VSaga.Dashboard.Identity.Services;

/// <summary>The problem codes an access rule violation carries; the API answers each with 409.</summary>
public static class IdentityRuleCodes
{
    /// <summary>The change would leave no enabled user holding <c>access.manage</c> for all saga types.</summary>
    public const string LastAdministrator = "last_administrator";

    /// <summary>Another user already has that username, ignoring case.</summary>
    public const string UsernameTaken = "username_taken";

    /// <summary>Another team or role already has that name, ignoring case.</summary>
    public const string NameTaken = "name_taken";

    /// <summary>A built-in role cannot be edited or deleted.</summary>
    public const string RoleImmutable = "role_immutable";

    /// <summary>A role still granted to a user or a team cannot be deleted.</summary>
    public const string RoleInUse = "role_in_use";
}

/// <summary>A change breaks one of the access rules; <see cref="Code"/> is one of <see cref="IdentityRuleCodes"/>.</summary>
public class IdentityRuleException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// A request to change access is malformed. <see cref="Errors"/> maps camelCase request paths (for example
/// <c>username</c> or <c>grants[0].sagaTypes</c>) to what is wrong there, in the shape of a validation
/// problem's <c>errors</c>.
/// </summary>
public class IdentityValidationException(IReadOnlyDictionary<string, string[]> errors)
    : Exception("The request is not valid: " + string.Join("; ", errors.Select(e => e.Key + ": " + string.Join(" ", e.Value))))
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}
