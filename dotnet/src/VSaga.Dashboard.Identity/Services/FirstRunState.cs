namespace VSaga.Dashboard.Identity.Services;

/// <summary>
/// What start-up decided about the first administrator, for the session, setup and health answers: whether
/// first-run setup is open (and with which one-time code), and why a configured seed could not be applied.
/// A singleton written by <see cref="FirstAdministratorService"/>. Until it has run, setup is closed.
/// </summary>
public sealed class FirstRunState
{
    private volatile string? _setupCode;
    private volatile string? _seedProblem;
    private volatile bool _seedConfigured;

    /// <summary>True while first-run setup can be completed with the one-time code.</summary>
    public bool IsSetupOpen => _setupCode is not null;

    /// <summary>
    /// Why the configured seed (or <c>Dashboard:Admin:ResetOnStart</c>) could not be applied at start, naming
    /// the key to fix but never its value; null when it was applied, ignored or not configured.
    /// </summary>
    public string? SeedProblem => _seedProblem;

    /// <summary>
    /// Why setup cannot be completed while no user exists: the seed problem, the seed keys being set, or no
    /// code being in force.
    /// </summary>
    public string SetupUnavailableReason
    {
        get
        {
            if (_seedProblem is { } problem)
                return $"{problem} Fix it and restart the API; first-run setup is not offered while {FirstAdministratorSettings.UsernameKey} or {FirstAdministratorSettings.PasswordKey} is set.";

            return _seedConfigured
                ? $"First-run setup is not offered while {FirstAdministratorSettings.UsernameKey} or {FirstAdministratorSettings.PasswordKey} is set; restart the API to create that administrator."
                : "No setup code is in force; restart the API and it logs a new one.";
        }
    }

    /// <summary>
    /// Ends first-run setup for the life of the process: a user exists, so the code may never be used again.
    /// </summary>
    public void CloseSetup() => _setupCode = null;

    /// <summary>
    /// True when setup is open and <paramref name="submitted"/> is its code, compared in fixed time and in
    /// canonical form (<see cref="SetupCodes.Canonical"/>).
    /// </summary>
    public bool MatchesSetupCode(string? submitted) =>
        _setupCode is { } code && SetupCodes.Matches(code, submitted);

    /// <summary>Records whether seed keys are set and, when the seed could not be applied, why.</summary>
    internal void RecordSeed(bool configured, string? problem)
    {
        _seedConfigured = configured;
        _seedProblem = problem;
    }

    /// <summary>Opens setup with <paramref name="code"/>; false, keeping the code in force, when it is already open.</summary>
    internal bool OpenSetup(string code) =>
        Interlocked.CompareExchange(ref _setupCode, code, null) is null;
}
