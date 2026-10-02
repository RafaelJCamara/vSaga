using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Stores;

namespace VSaga.Dashboard.Identity.Services;

/// <summary>How a first-run setup request ended.</summary>
public enum SetupStatus
{
    /// <summary>The administrator was created; <see cref="SetupResult.User"/> holds it.</summary>
    Completed,

    /// <summary>Setup is not open (a user exists, seed keys are set, or no code is in force); <see cref="SetupResult.Detail"/> says which.</summary>
    Unavailable,

    /// <summary>The setup code was missing or wrong; nothing was checked or written beyond that.</summary>
    WrongCode,
}

/// <summary>The outcome of <see cref="FirstAdministratorService.CompleteSetupAsync"/>.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="User">The administrator created; null unless <see cref="SetupStatus.Completed"/>.</param>
/// <param name="Detail">Why setup is unavailable; null otherwise.</param>
public sealed record SetupResult(SetupStatus Status, DashboardUser? User, string? Detail);

/// <summary>
/// The first administrator (design §8.8). At start, after the store is migrated, <see cref="ApplyAtStartAsync"/>
/// either seeds that administrator from <c>Dashboard:Admin:Username</c> and <c>Dashboard:Admin:Password</c> or,
/// with neither set and no user in the store, opens first-run setup with a one-time code it logs at Warning.
/// <list type="bullet">
/// <item>Seeding fails closed: with either seed key set, setup is never opened, and a seed that cannot be
/// applied (one key missing, an invalid username, a password the policy rejects) is recorded in
/// <see cref="FirstRunState.SeedProblem"/>, which the <c>identity</c> health check and the session report.</item>
/// <item>A seed never touches a store that already has users, unless <c>Dashboard:Admin:ResetOnStart</c> is
/// true: then the seed user's password is reset, the account enabled and unlocked, its stamp rotated and an
/// Administrator grant for all saga types restored, creating the user when it is missing; still only with a
/// password the policy accepts.</item>
/// <item>Setup has no time window: the code, which only someone with the API's log or configuration has, is
/// the boundary. <see cref="CompleteSetupAsync"/> checks it in fixed time and re-checks, inside the exclusive
/// scope, that setup is still open and no user exists, so two visitors cannot both succeed.</item>
/// </list>
/// Each change is an audit event; passwords, hashes and codes never are.
/// </summary>
public sealed partial class FirstAdministratorService
{
    /// <summary>
    /// The audit actor of seeding and of <c>ResetOnStart</c>. Outside the <c>dashboard:</c> namespace of
    /// <see cref="CallerAccess.AuditActor"/>, so no username (which cannot contain ':') can produce it.
    /// </summary>
    public const string ConfigurationActor = "vsaga:configuration";

    /// <summary>The audit actor of first-run setup with the one-time code; like <see cref="ConfigurationActor"/>, never a user's.</summary>
    public const string SetupActor = "vsaga:setup";

    private const string UserKind = "user";

    private readonly IDashboardIdentityStore _store;
    private readonly IPasswordHasher<DashboardUser> _hasher;
    private readonly PasswordPolicy _policy;
    private readonly FirstAdministratorSettings _settings;
    private readonly FirstRunState _state;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly ILogger _audit;

    public FirstAdministratorService(
        IDashboardIdentityStore store,
        IPasswordHasher<DashboardUser> hasher,
        PasswordPolicy policy,
        FirstAdministratorSettings settings,
        FirstRunState state,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _store = store;
        _hasher = hasher;
        _policy = policy;
        _settings = settings;
        _state = state;
        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger<FirstAdministratorService>();
        _audit = loggerFactory.CreateLogger(DashboardAudit.CategoryName);
    }

    /// <summary>
    /// Seeds, resets or opens setup, as the configuration and the store say. Called once the store is ready;
    /// a store error propagates, so the start-up attempt fails and is retried.
    /// </summary>
    public async Task ApplyAtStartAsync(CancellationToken cancellationToken)
    {
        var users = await _store.CountUsersAsync(cancellationToken);
        if (!_settings.SeedConfigured)
        {
            _state.RecordSeed(configured: false, problem: null);
            OfferSetup(users);
            return;
        }

        _state.CloseSetup();
        if (users > 0 && !_settings.ResetOnStart)
        {
            _state.RecordSeed(configured: true, problem: null);
            LogSeedIgnored(_logger, users);
            return;
        }

        if (SeedProblem() is { } problem)
        {
            var reason = users == 0
                ? $"The first administrator could not be created from {FirstAdministratorSettings.UsernameKey} and {FirstAdministratorSettings.PasswordKey}: {problem}"
                : $"{FirstAdministratorSettings.ResetOnStartKey} is true but nothing was reset: {problem}";
            _state.RecordSeed(configured: true, reason);
            LogSeedNotApplied(_logger, reason);
            return;
        }

        _state.RecordSeed(configured: true, problem: null);
        await SeedOrResetAsync(cancellationToken);
    }

    /// <summary>
    /// Creates the first administrator when setup is open and <paramref name="code"/> is its code: an enabled
    /// user with the Administrator role for all saga types who need not change the password. Setup then closes
    /// for good.
    /// </summary>
    /// <exception cref="IdentityValidationException">The username, display name or password is not acceptable.</exception>
    public async Task<SetupResult> CompleteSetupAsync(
        string? username, string? displayName, string? password, string? code, AuditContext audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audit);
        if (!_state.IsSetupOpen)
            return await UnavailableAsync(audit, cancellationToken);

        if (!_state.MatchesSetupCode(code))
        {
            Rejected(audit, "invalid_credentials");
            return new SetupResult(SetupStatus.WrongCode, null, null);
        }

        var errors = new ValidationErrors();
        AccessValidation.Username(errors, "username", username);
        var display = AccessValidation.RequiredText(errors, "displayName", displayName, AccessValidation.MaxDisplayNameLength);
        foreach (var message in _policy.Validate(username, password))
            errors.Add("password", message);
        if (errors.Any)
            Rejected(audit, "validation");
        errors.ThrowIfAny();

        var hash = _hasher.HashPassword(DummyPasswordHash.Placeholder, password!);
        await using var scope = await _store.BeginExclusiveAsync(cancellationToken);
        if (!_state.IsSetupOpen || await _store.CountUsersAsync(cancellationToken) > 0)
        {
            // Another setup won, or a user was created another way: the code must never work again.
            _state.CloseSetup();
            return await UnavailableAsync(audit, cancellationToken);
        }

        // The endpoint signs this administrator in straight away, so the sign-in is recorded as login records it.
        var user = NewAdministrator(username!, display!, hash, signedIn: true);
        await _store.CreateUserAsync(user, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        _state.CloseSetup();
        DashboardAudit.AccessChanged(
            _audit, audit.Actor, AccessActions.SetupAdministrator, UserKind, user.Id, user.Username, audit.ClientAddress, "succeeded",
            "first administrator created with the setup code");
        return new SetupResult(SetupStatus.Completed, user, null);
    }

    /// <summary>
    /// Why the configured seed cannot be applied, naming the key but never its value, or null when it can.
    /// </summary>
    private string? SeedProblem()
    {
        if (_settings.Username is null)
            return $"{FirstAdministratorSettings.PasswordKey} is set but {FirstAdministratorSettings.UsernameKey} is not; set both.";

        if (_settings.Password is null)
            return $"{FirstAdministratorSettings.UsernameKey} is set but {FirstAdministratorSettings.PasswordKey} is not; set both.";

        if (!AccessValidation.IsValidUsername(_settings.Username))
        {
            return $"{FirstAdministratorSettings.UsernameKey} is not a valid username: use {AccessValidation.MinUsernameLength} to "
                + $"{AccessValidation.MaxUsernameLength} letters, digits or . _ @ + -, starting with a letter or a digit "
                + $"('{CallerAccess.ApiKeyUsername}' is reserved).";
        }

        var errors = _policy.Validate(_settings.Username, _settings.Password);
        return errors.Count == 0
            ? null
            : $"{FirstAdministratorSettings.PasswordKey} does not meet the password policy: {string.Join(" ", errors)}";
    }

    private void OfferSetup(int users)
    {
        if (users > 0)
        {
            _state.CloseSetup();
            return;
        }

        if (_settings.SetupCode is { } preset)
        {
            if (_state.OpenSetup(preset))
                LogSetupOpenWithPresetCode(_logger, FirstAdministratorSettings.SetupCodeKey);
            return;
        }

        var code = SetupCodes.Generate();
        if (_state.OpenSetup(code))
            LogSetupOpen(_logger, code);
    }

    /// <summary>
    /// The seed with no user in the store, or <c>ResetOnStart</c>: decided again under the write lock, with the
    /// password hashed before it is taken.
    /// </summary>
    private async Task SeedOrResetAsync(CancellationToken cancellationToken)
    {
        var username = _settings.Username!;
        var hash = _hasher.HashPassword(DummyPasswordHash.Placeholder, _settings.Password!);
        await using var scope = await _store.BeginExclusiveAsync(cancellationToken);
        var seeding = await _store.CountUsersAsync(cancellationToken) == 0;
        if (!seeding && !_settings.ResetOnStart)
            return;

        var existing = seeding ? null : await _store.FindUserByNameAsync(username, cancellationToken);
        var user = existing is null ? NewAdministrator(username, username, hash, signedIn: false) : Restored(existing, hash);
        if (existing is null)
            await _store.CreateUserAsync(user, cancellationToken);
        else
            await _store.UpdateUserAsync(user, cancellationToken);
        await scope.CommitAsync(cancellationToken);

        // With ResetOnStart on, even a start that only seeded an empty store is a reset start: the flag is still
        // on and every later start resets again, so the operator is told at Warning to set it back.
        var action = _settings.ResetOnStart ? AccessActions.ResetAdministratorOnStart : AccessActions.SeedAdministrator;
        var details = existing is null ? "administrator created from configuration" : "password reset, enabled, unlocked, Administrator for all saga types";
        DashboardAudit.AccessChanged(_audit, ConfigurationActor, action, UserKind, user.Id, user.Username, null, "succeeded", details);
        if (_settings.ResetOnStart)
            LogResetOnStart(_logger, user.Username, existing is null ? "created" : "reset", FirstAdministratorSettings.ResetOnStartKey);
        else
            LogSeeded(_logger, user.Username);
    }

    /// <summary>
    /// The seed user restored to a working administrator: the new password, enabled, unlocked, no forced change,
    /// a new stamp (which ends its sessions) and an Administrator grant for all saga types in place of any
    /// narrower one; its other grants stay.
    /// </summary>
    private DashboardUser Restored(DashboardUser existing, string hash) => existing with
    {
        PasswordHash = hash,
        SecurityStamp = SecurityStamps.New(),
        IsEnabled = true,
        MustChangePassword = false,
        FailedSignInCount = 0,
        LockoutEndUtc = null,
        UpdatedAtUtc = _timeProvider.GetUtcNow(),
        Grants = [.. existing.Grants.Where(g => g.RoleId != BuiltInRoles.AdministratorId), AdministratorForAllSagaTypes],
    };

    private DashboardUser NewAdministrator(string username, string displayName, string hash, bool signedIn)
    {
        var now = _timeProvider.GetUtcNow();
        return new DashboardUser(
            Guid.NewGuid(), username, displayName, hash, SecurityStamps.New(), IsEnabled: true, MustChangePassword: false,
            FailedSignInCount: 0, LockoutEndUtc: null, LastSignInAtUtc: signedIn ? now : null, now, now, [AdministratorForAllSagaTypes]);
    }

    private static AccessGrant AdministratorForAllSagaTypes => new(BuiltInRoles.AdministratorId, AllSagaTypes: true, []);

    private async Task<SetupResult> UnavailableAsync(AuditContext audit, CancellationToken cancellationToken)
    {
        Rejected(audit, "setup_unavailable");
        var detail = await _store.CountUsersAsync(cancellationToken) > 0
            ? "First-run setup is over: a dashboard user already exists. Sign in, or ask an administrator for an account."
            : _state.SetupUnavailableReason;
        return new SetupResult(SetupStatus.Unavailable, null, detail);
    }

    private void Rejected(AuditContext audit, string outcome) =>
        DashboardAudit.AccessChangeRejected(_audit, audit.Actor, AccessActions.SetupAdministrator, UserKind, null, audit.ClientAddress, outcome);

    [LoggerMessage(EventId = 7210, EventName = "SetupOpen", Level = LogLevel.Warning,
        Message = "No dashboard user exists and Dashboard:Admin:* is not set, so first-run setup is open. Open the dashboard and create the first administrator with this one-time setup code: {SetupCode} . It works until a user exists; a restart before then issues a new one")]
    private static partial void LogSetupOpen(ILogger logger, string setupCode);

    [LoggerMessage(EventId = 7211, EventName = "SetupOpenWithPresetCode", Level = LogLevel.Warning,
        Message = "No dashboard user exists and Dashboard:Admin:* is not set, so first-run setup is open. Open the dashboard and create the first administrator with the setup code set in {SetupCodeKey}")]
    private static partial void LogSetupOpenWithPresetCode(ILogger logger, string setupCodeKey);

    [LoggerMessage(EventId = 7212, EventName = "AdministratorSeeded", Level = LogLevel.Information,
        Message = "Created the first dashboard administrator '{Username}' from Dashboard:Admin:Username and Dashboard:Admin:Password")]
    private static partial void LogSeeded(ILogger logger, string username);

    [LoggerMessage(EventId = 7213, EventName = "SeedNotApplied", Level = LogLevel.Error,
        Message = "{Reason} Sign-in stays closed to a first administrator until this is fixed; the identity health check reports it")]
    private static partial void LogSeedNotApplied(ILogger logger, string reason);

    [LoggerMessage(EventId = 7214, EventName = "AdministratorResetOnStart", Level = LogLevel.Warning,
        Message = "Dashboard administrator '{Username}' {Outcome} at start: password from Dashboard:Admin:Password, enabled, unlocked, Administrator for all saga types, sessions ended. Set {ResetOnStartKey} back to false once you can sign in; while it is true every start does this again")]
    private static partial void LogResetOnStart(ILogger logger, string username, string outcome, string resetOnStartKey);

    [LoggerMessage(EventId = 7215, EventName = "SeedIgnored", Level = LogLevel.Debug,
        Message = "Dashboard:Admin:* is set but the identity store already has {UserCount} user(s), so nothing was seeded")]
    private static partial void LogSeedIgnored(ILogger logger, int userCount);
}
