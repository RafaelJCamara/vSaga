namespace VSaga.Dashboard.Identity.Stores;

/// <summary>The kind of identity record an exception is about.</summary>
public enum IdentityEntityKind
{
    User,
    Team,
    Role,
}

/// <summary>A write would give two users, two teams or two roles the same name, ignoring case.</summary>
public class IdentityConflictException(IdentityEntityKind kind, string name)
    : Exception($"A {IdentityEntityNames.Noun(kind)} named '{name}' already exists; names are compared ignoring case.")
{
    public IdentityEntityKind Kind { get; } = kind;

    /// <summary>The name as the rejected write gave it.</summary>
    public string Name { get; } = name;
}

/// <summary>An update or delete named a user, team or role that does not exist.</summary>
public class IdentityNotFoundException(IdentityEntityKind kind, Guid id)
    : Exception($"No {IdentityEntityNames.Noun(kind)} with id '{id}' exists.")
{
    public IdentityEntityKind Kind { get; } = kind;

    public Guid Id { get; } = id;
}

/// <summary>
/// A write names something that does not exist (a grant's role, a team member), or a delete would leave
/// a grant naming a role that no longer exists.
/// </summary>
public class IdentityReferenceException(string message) : Exception(message);

/// <summary>
/// The identity store is not ready (see <see cref="Services.IdentityStartup"/>): its database could not be
/// opened, migrated or reached. Thrown rather than answering with nothing, so that, for one, Data Protection
/// never mistakes an unreachable key ring for an empty one and mints a key that dies with the process.
/// </summary>
public class IdentityUnavailableException(string message) : Exception(message);

internal static class IdentityEntityNames
{
    public static string Noun(IdentityEntityKind kind) => kind switch
    {
        IdentityEntityKind.User => "user",
        IdentityEntityKind.Team => "team",
        _ => "role",
    };
}
