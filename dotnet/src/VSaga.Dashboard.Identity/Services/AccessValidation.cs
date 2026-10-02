using System.Globalization;
using VSaga.Dashboard.Identity.Model;

namespace VSaga.Dashboard.Identity.Services;

/// <summary>The shape rules for usernames, names, descriptions, permissions and grants (design §8.2, §8.9).</summary>
public static class AccessValidation
{
    public const int MinUsernameLength = 3;
    public const int MaxUsernameLength = CredentialVerifier.MaxUsernameLength;
    public const int MaxDisplayNameLength = 128;
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 256;
    public const int MaxGrantsPerSubject = 20;
    public const int MaxSagaTypesPerGrant = 100;

    /// <summary>The <c>SagaType</c> column length of the saga stores.</summary>
    public const int MaxSagaTypeLength = 200;

    private const string UsernameSymbols = "._@+-";

    /// <summary>
    /// True when <paramref name="username"/> is 3 to 64 of <c>[A-Za-z0-9._@+-]</c>, starts with a letter or a
    /// digit, and is not <c>api-key</c> in any case, which names the API key in audit records.
    /// </summary>
    public static bool IsValidUsername(string? username) =>
        username is { Length: >= MinUsernameLength and <= MaxUsernameLength }
        && char.IsAsciiLetterOrDigit(username[0])
        && username.All(c => char.IsAsciiLetterOrDigit(c) || UsernameSymbols.Contains(c, StringComparison.Ordinal))
        && !string.Equals(username, CallerAccess.ApiKeyUsername, StringComparison.OrdinalIgnoreCase);

    internal static string? Username(ValidationErrors errors, string path, string? username)
    {
        if (IsValidUsername(username))
            return username;

        errors.Add(path, string.Equals(username, CallerAccess.ApiKeyUsername, StringComparison.OrdinalIgnoreCase)
            ? "This username is reserved for the API key."
            : $"Use {MinUsernameLength} to {MaxUsernameLength} letters, digits or . _ @ + -, starting with a letter or a digit.");
        return null;
    }

    /// <summary>Trimmed text of 1 to <paramref name="maxLength"/> characters with no control characters, or null after recording an error.</summary>
    internal static string? RequiredText(ValidationErrors errors, string path, string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > maxLength || trimmed.Any(char.IsControl))
        {
            errors.Add(path, $"Enter 1 to {maxLength} characters, with no control characters.");
            return null;
        }

        return trimmed;
    }

    /// <summary>
    /// A trimmed description of at most 256 characters with no control characters other than line breaks and
    /// tabs, or null when blank. Each broken rule gets its own message, shown beside the field.
    /// </summary>
    internal static string? Description(ValidationErrors errors, string path, string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        if (trimmed.Length > MaxDescriptionLength)
            errors.Add(path, $"Use at most {MaxDescriptionLength} characters.");
        if (trimmed.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
            errors.Add(path, "Use no control characters other than line breaks and tabs.");
        return trimmed;
    }

    /// <summary>A non-empty subset of the catalogue, returned once each in catalogue order.</summary>
    internal static IReadOnlyList<string> RolePermissions(ValidationErrors errors, string path, IReadOnlyList<string>? permissions)
    {
        if (permissions is null || permissions.Count == 0)
        {
            errors.Add(path, "Choose at least one permission.");
            return [];
        }

        for (var i = 0; i < permissions.Count; i++)
        {
            if (permissions[i] is null || !Permissions.IsKnown(permissions[i]))
                errors.Add(Indexed(path, i), $"'{permissions[i]}' is not a permission.");
        }

        return [.. Permissions.All.Select(p => p.Key).Where(k => permissions.Contains(k, StringComparer.Ordinal))];
    }

    /// <summary>
    /// At most 20 grants, one per role, each naming a role in <paramref name="roles"/>; a grant for all saga
    /// types names none, any other names 1 to 100 distinct saga types. Names are trimmed and stored as validated.
    /// </summary>
    internal static IReadOnlyList<AccessGrant> Grants(
        ValidationErrors errors, string path, IReadOnlyList<AccessGrant>? grants, IReadOnlyCollection<DashboardRole> roles)
    {
        if (grants is null || grants.Count == 0)
            return [];

        if (grants.Count > MaxGrantsPerSubject)
            errors.Add(path, $"Hold at most {MaxGrantsPerSubject} grants.");

        var seen = new HashSet<Guid>();
        var result = new List<AccessGrant>(grants.Count);
        for (var i = 0; i < grants.Count; i++)
        {
            var grantPath = Indexed(path, i);
            var grant = grants[i];
            if (grant is null)
            {
                // A JSON body can carry "grants":[null]; it is a malformed request, not a server error.
                errors.Add(grantPath, "Each grant names a role and its saga types.");
                continue;
            }

            if (!roles.Any(r => r.Id == grant.RoleId))
                errors.Add(grantPath + ".roleId", "No role has this id.");
            else if (!seen.Add(grant.RoleId))
                errors.Add(grantPath + ".roleId", "This role is already granted; hold each role once.");

            var sagaTypes = grant.SagaTypes ?? [];
            if (grant.AllSagaTypes && sagaTypes.Count > 0)
                errors.Add(grantPath + ".sagaTypes", "A grant for all saga types names no saga types.");

            result.Add(new AccessGrant(
                grant.RoleId,
                grant.AllSagaTypes,
                grant.AllSagaTypes ? [] : SagaTypes(errors, grantPath + ".sagaTypes", sagaTypes)));
        }

        return result;
    }

    internal static string Indexed(string path, int index) =>
        string.Create(CultureInfo.InvariantCulture, $"{path}[{index}]");

    private static IReadOnlyList<string> SagaTypes(ValidationErrors errors, string path, IReadOnlyList<string> sagaTypes)
    {
        if (sagaTypes.Count is 0 or > MaxSagaTypesPerGrant)
        {
            errors.Add(path, $"Name 1 to {MaxSagaTypesPerGrant} saga types, or grant all saga types.");
            return [];
        }

        var result = new List<string>(sagaTypes.Count);
        foreach (var name in sagaTypes)
        {
            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxSagaTypeLength || trimmed.Any(char.IsControl))
                errors.Add(path, $"Each saga type name is 1 to {MaxSagaTypeLength} characters, not blank and with no control characters.");
            else if (result.Contains(trimmed, StringComparer.Ordinal))
                errors.Add(path, $"'{trimmed}' is listed twice.");
            else
                result.Add(trimmed);
        }

        return result;
    }
}

/// <summary>Validation errors keyed by camelCase request path, collected before anything is written.</summary>
internal sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool Any => _errors.Count > 0;

    public void Add(string path, string message)
    {
        if (!_errors.TryGetValue(path, out var messages))
        {
            messages = [];
            _errors[path] = messages;
        }

        if (!messages.Contains(message, StringComparer.Ordinal))
            messages.Add(message);
    }

    /// <exception cref="IdentityValidationException">At least one error was recorded.</exception>
    public void ThrowIfAny()
    {
        if (Any)
            throw new IdentityValidationException(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal));
    }
}
