using Microsoft.Extensions.Logging;

namespace VSaga.Dashboard.Identity.Services;

/// <summary>Who made an audited change, and from where.</summary>
/// <param name="Actor">The caller's audit name (<see cref="CallerAccess.AuditActor"/>), or a fixed name such as <c>dashboard:setup</c> for start-up work.</param>
/// <param name="ClientAddress">The client's address as the API sees it, or null when there is no request.</param>
public sealed record AuditContext(string Actor, string? ClientAddress);

/// <summary>The access changes the audit log names, as its <c>Action</c> property.</summary>
public static class AccessActions
{
    public const string CreateUser = "user.create";
    public const string UpdateUser = "user.update";
    public const string DeleteUser = "user.delete";
    public const string ResetPassword = "user.reset-password";
    public const string UnlockUser = "user.unlock";
    public const string ChangeOwnPassword = "user.change-password";
    public const string CreateTeam = "team.create";
    public const string UpdateTeam = "team.update";
    public const string DeleteTeam = "team.delete";
    public const string CreateRole = "role.create";
    public const string UpdateRole = "role.update";
    public const string DeleteRole = "role.delete";
}

/// <summary>
/// The structured audit events, all under one log category (<see cref="CategoryName"/>) so an operator can
/// route them on their own. Each carries the actor, the action, the target and the outcome, plus the
/// client address when there is a request. Event ids are stable: 7100-7199 are access administration.
/// Passwords and hashes are never logged, and a submitted username only once it passed the username rule.
/// </summary>
public static partial class DashboardAudit
{
    /// <summary>The log category of every audit event.</summary>
    public const string CategoryName = "VSaga.Dashboard.Audit";

    /// <summary>An access change was committed. <paramref name="details"/> says what changed, never a secret.</summary>
    [LoggerMessage(
        EventId = 7100,
        EventName = "AccessChanged",
        Level = LogLevel.Information,
        Message = "Audit: {Actor} {Action} {TargetKind} {TargetId} '{Target}' from {ClientAddress}: {Outcome} ({Details})")]
    public static partial void AccessChanged(
        ILogger logger, string actor, string action, string targetKind, Guid targetId, string target, string? clientAddress, string outcome, string details);

    /// <summary>
    /// An access change was refused: <paramref name="outcome"/> is the problem code (<c>validation</c>,
    /// <c>not_found</c>, <c>invalid_credentials</c> or one of <see cref="IdentityRuleCodes"/>).
    /// </summary>
    [LoggerMessage(
        EventId = 7101,
        EventName = "AccessChangeRejected",
        Level = LogLevel.Warning,
        Message = "Audit: {Actor} {Action} {TargetKind} {TargetId} from {ClientAddress}: {Outcome}")]
    public static partial void AccessChangeRejected(
        ILogger logger, string actor, string action, string targetKind, Guid? targetId, string? clientAddress, string outcome);

    /// <summary>The access-change observer failed after a change was committed; the change stands.</summary>
    [LoggerMessage(
        EventId = 7102,
        EventName = "AccessChangeNotificationFailed",
        Level = LogLevel.Warning,
        Message = "The {Action} change was committed, but notifying live connections failed; they keep their old access until they reconnect")]
    public static partial void AccessChangeNotificationFailed(ILogger logger, Exception exception, string action);
}
