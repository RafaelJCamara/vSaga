using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using VSaga.Dashboard.Api.Auth;
using VSaga.Dashboard.Identity;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;
using VSaga.Dashboard.Identity.Stores;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace VSaga.Dashboard.Api.Endpoints;

/// <summary>
/// Signing in and out (design §8.3, §8.4, §8.9): <c>GET /api/auth/session</c>, <c>POST /api/auth/login</c>,
/// <c>POST /api/auth/logout</c> and <c>POST /api/auth/password</c>.
/// <list type="bullet">
/// <item>Every one first makes sure the identity store is ready (its key ring protects the cookie and the
/// antiforgery tokens) and answers 503 <c>identity_unavailable</c> when it is not.</item>
/// <item>Each answers a fresh <see cref="SessionResponse"/> and issues the antiforgery request token for the
/// principal it leaves on the request, after setting <c>HttpContext.User</c> to it.</item>
/// <item>A failed sign-in is one uniform 401 <c>invalid_credentials</c>, completing no earlier than
/// <see cref="FailureFloor"/> (plus jitter) after the request started, so neither the body nor the timing
/// tells an unknown user from a wrong password, a locked or a disabled account.</item>
/// <item>Sign-in and password change are rate limited per client address and username, and the password
/// hashing they do runs under a global concurrency limit (<see cref="AuthRateLimits"/>).</item>
/// </list>
/// Request bodies are read here rather than bound by the framework, so an unknown member or malformed JSON is
/// a 400 <c>validation</c> problem naming the member.
/// </summary>
public static class AuthEndpoints
{
    /// <summary>The earliest a failed sign-in answers, measured from the start of the request.</summary>
    public static readonly TimeSpan FailureFloor = TimeSpan.FromMilliseconds(300);

    /// <summary>How long these endpoints wait for the identity store to become ready before answering 503.</summary>
    public static readonly TimeSpan IdentityReadyWait = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The largest request body these endpoints read (16 KiB). Login reads its body before any rate limit, since
    /// the window is keyed on the username in it, so the body itself must be cheap to refuse.
    /// </summary>
    public const int MaxRequestBodyBytes = 16 * 1024;

    private const int MaxJitterMilliseconds = 50;
    private const string LoginAction = "sign-in";
    private const string PasswordAction = "password-change";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Authentication");

        group.MapGet("/session", GetSessionAsync).WithName("GetSession").AllowAnonymous();
        group.MapPost("/login", LoginAsync).WithName("Login").AllowAnonymous().Accepts<LoginRequest>("application/json");
        group.MapPost("/logout", LogoutAsync).WithName("Logout").AllowAnonymous();
        group.MapPost("/password", ChangePasswordAsync).WithName("ChangePassword").RequireAuthorization()
            .Accepts<ChangePasswordRequest>("application/json");
        return app;
    }

    private static async Task<IResult> GetSessionAsync(HttpContext context, IdentityStartup startup)
    {
        if (!await startup.EnsureReadyAsync(IdentityReadyWait, context.RequestAborted))
            return AuthProblems.IdentityUnavailable();

        AntiforgeryEnforcement.IssueRequestToken(context);
        return TypedResults.Ok(await SessionAsync(context, context.GetCaller()));
    }

    private static async Task<IResult> LoginAsync(HttpContext context, IdentityStartup startup, AuthRateLimits limits, TimeProvider time)
    {
        var started = time.GetTimestamp();
        if (!await startup.EnsureReadyAsync(IdentityReadyWait, context.RequestAborted))
            return AuthProblems.IdentityUnavailable();

        var (request, invalid) = await ReadBodyAsync<LoginRequest>(context);
        if (request is null)
            return invalid!;

        var address = ClientAddress(context);
        using var window = limits.AcquireSignIn(address, request.Username);
        if (!window.IsAcquired)
            return RateLimited(context, window, LoginAction, request.Username);

        CredentialVerification verification;
        using (var hashing = await limits.AcquireHashingAsync(context.RequestAborted))
        {
            if (!hashing.IsAcquired)
                return RateLimited(context, hashing, LoginAction, request.Username);

            var verifier = context.RequestServices.GetRequiredService<CredentialVerifier>();
            verification = await verifier.VerifyAsync(request.Username, request.Password, context.RequestAborted);
        }

        if (verification.User is not { } user)
        {
            AuditFailure(context, request.Username, verification);
            await WaitForFailureFloorAsync(time, started, context.RequestAborted);
            return AuthProblems.InvalidCredentials();
        }

        var caller = await SignInAsync(context, user, time);
        DashboardAudit.SignedIn(Audit(context), user.Username, user.Id, address);
        return TypedResults.Ok(await SessionAsync(context, caller));
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, IdentityStartup startup)
    {
        if (!await startup.EnsureReadyAsync(IdentityReadyWait, context.RequestAborted))
            return AuthProblems.IdentityUnavailable();

        if (context.GetCaller() is { Kind: CallerKind.User, UserId: { } userId } caller)
        {
            await context.SignOutAsync(DashboardAuthExtensions.CookieScheme);
            DashboardAudit.SignedOut(Audit(context), caller.Username, userId, ClientAddress(context));
            await NotifyUserChangedAsync(context, userId, "sign-out");
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        context.ClearCaller();
        AntiforgeryEnforcement.IssueRequestToken(context);
        return TypedResults.Ok(await SessionAsync(context, caller: null));
    }

    private static async Task<IResult> ChangePasswordAsync(HttpContext context, IdentityStartup startup, AuthRateLimits limits, TimeProvider time)
    {
        if (!await startup.EnsureReadyAsync(IdentityReadyWait, context.RequestAborted))
            return AuthProblems.IdentityUnavailable();

        // Only a signed-in user has a password to change; the API key gets 403.
        if (context.GetCaller() is not { Kind: CallerKind.User, UserId: { } userId } caller)
        {
            await AuthProblems.WriteForbiddenAsync(context, context.GetCaller(), permission: null, sagaType: null);
            return Results.Empty;
        }

        var (request, invalid) = await ReadBodyAsync<ChangePasswordRequest>(context);
        if (request is null)
            return invalid!;

        using var window = limits.AcquireSignIn(ClientAddress(context), caller.Username);
        if (!window.IsAcquired)
            return RateLimited(context, window, PasswordAction, caller.Username);

        using var hashing = await limits.AcquireHashingAsync(context.RequestAborted);
        if (!hashing.IsAcquired)
            return RateLimited(context, hashing, PasswordAction, caller.Username);

        var administration = context.RequestServices.GetRequiredService<AccessAdministrationService>();
        PasswordChangeResult result;
        try
        {
            result = await administration.ChangeOwnPasswordAsync(
                userId, request.CurrentPassword, request.NewPassword, new AuditContext(caller.AuditActor, ClientAddress(context)), context.RequestAborted);
        }
        catch (IdentityValidationException ex)
        {
            return AuthProblems.Validation(ex.Errors);
        }
        catch (IdentityNotFoundException)
        {
            // Deleted meanwhile: the session no longer stands.
            await context.SignOutAsync(DashboardAuthExtensions.CookieScheme);
            await AuthProblems.WriteUnauthorizedAsync(context);
            return Results.Empty;
        }

        return await PasswordChangeOutcomeAsync(context, caller, result, time);
    }

    /// <summary>
    /// Changed: the stamp rotated (the service told the observer), so the session is issued again under the new
    /// one. Wrong: 400 naming <c>currentPassword</c>. When that failure locked the account the service rotated
    /// the stamp and told the observer, which ends every copy of this session on the server; the browser's
    /// cookie is deleted here too. Failures here and at sign-in share one counter, so a session gets no more
    /// guesses at the current password than the sign-in endpoint already allows. Refused: the account was
    /// already locked (or disabled) and the current password was never checked, so the answer does not say it
    /// was wrong and the session is left alone: an outsider's failed sign-ins must not end the owner's session.
    /// </summary>
    private static async Task<IResult> PasswordChangeOutcomeAsync(HttpContext context, CallerAccess caller, PasswordChangeResult result, TimeProvider time)
    {
        switch (result)
        {
            case { Status: PasswordChangeStatus.Changed, User: { } user }:
                var signedIn = await SignInAsync(context, user, time);
                return TypedResults.Ok(await SessionAsync(context, signedIn));

            case { Status: PasswordChangeStatus.Refused }:
                return AuthProblems.CurrentPasswordRefused("The account is locked or disabled for now, so its password cannot be changed; try again later.");

            case { LockedUntilUtc: { } lockedUntil }:
                var userId = caller.UserId!.Value;
                DashboardAudit.AccountLockedOut(Audit(context), userId, caller.Username, lockedUntil, ClientAddress(context));
                await context.SignOutAsync(DashboardAuthExtensions.CookieScheme);
                DashboardAudit.SessionEnded(Audit(context), caller.Username, userId, ClientAddress(context), "the account is locked after wrong current passwords");
                return AuthProblems.WrongCurrentPassword(
                    "The current password is not correct, and too many wrong attempts have locked the account for now; you have been signed out.");

            default:
                return AuthProblems.WrongCurrentPassword("The current password is not correct.");
        }
    }

    /// <summary>
    /// Issues the session cookie for <paramref name="user"/> (not persistent, the sign-in time recorded), then
    /// makes it this request's principal and caller and issues the antiforgery token bound to it.
    /// </summary>
    private static async Task<CallerAccess?> SignInAsync(HttpContext context, DashboardUser user, TimeProvider time)
    {
        var principal = DashboardClaims.ForUser(user, DashboardAuthExtensions.CookieScheme);
        var properties = new AuthenticationProperties { IsPersistent = false };
        DashboardCookieEvents.SetSignedInAt(properties, time.GetUtcNow());
        await context.SignInAsync(DashboardAuthExtensions.CookieScheme, principal, properties);

        context.User = principal;
        var caller = await context.RequestServices.GetRequiredService<ICallerAccessResolver>().ResolveAsync(principal, context.RequestAborted);
        if (caller is null)
            context.ClearCaller();
        else
            context.SetCaller(caller);

        AntiforgeryEnforcement.IssueRequestToken(context);
        return caller;
    }

    private static async Task<SessionResponse> SessionAsync(HttpContext context, CallerAccess? caller)
    {
        var store = context.RequestServices.GetRequiredService<IDashboardIdentityStore>();
        var settings = context.RequestServices.GetRequiredService<DashboardSecuritySettings>();
        var setupRequired = await store.CountUsersAsync(context.RequestAborted) == 0;

        // First-run setup is not offered yet, so it is never available.
        return SessionResponse.For(caller, setupRequired, setupAvailable: false, settings.PasswordMinLength);
    }

    /// <summary>
    /// The JSON body as <typeparamref name="T"/> with the API's serializer options, or a 400 validation problem
    /// naming the offending member (camelCase path) when it is malformed, has an unknown member or is missing,
    /// or naming <c>request</c> when it is larger than <see cref="MaxRequestBodyBytes"/>.
    /// </summary>
    private static async Task<(T? Request, IResult? Invalid)> ReadBodyAsync<T>(HttpContext context)
        where T : class
    {
        if (await ReadCappedBodyAsync(context) is not { } body)
        {
            return (null, AuthProblems.Validation(
                new Dictionary<string, string[]>(StringComparer.Ordinal) { ["request"] = [$"The request body is larger than {MaxRequestBodyBytes / 1024} KiB."] }));
        }

        var options = context.RequestServices.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;
        try
        {
            var request = JsonSerializer.Deserialize<T>(body.Span, options);
            if (request is not null)
                return (request, null);
        }
        catch (JsonException ex)
        {
            var member = ex.Path is { Length: > 2 } path && path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : "request";
            return (null, AuthProblems.Validation(
                new Dictionary<string, string[]>(StringComparer.Ordinal) { [member] = ["Not a member of this request, or not a valid value for it."] }));
        }

        return (null, AuthProblems.Validation(
            new Dictionary<string, string[]>(StringComparer.Ordinal) { ["request"] = ["Send the request as a JSON object."] }));
    }

    /// <summary>
    /// The request body, or null when it is larger than <see cref="MaxRequestBodyBytes"/>. A declared length over
    /// the cap is refused unread; otherwise at most one byte past the cap is read (the server is told the cap
    /// too, where it lets the limit be changed), so an oversized body costs no more than the cap to refuse.
    /// </summary>
    private static async Task<ReadOnlyMemory<byte>?> ReadCappedBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength > MaxRequestBodyBytes)
            return null;

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } serverLimit)
            serverLimit.MaxRequestBodySize = MaxRequestBodyBytes;

        var buffer = new byte[MaxRequestBodyBytes + 1];
        var length = 0;
        try
        {
            var read = -1;
            while (read != 0 && length < buffer.Length)
            {
                read = await context.Request.Body.ReadAsync(buffer.AsMemory(length), context.RequestAborted);
                length += read;
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }

        return length > MaxRequestBodyBytes ? null : buffer.AsMemory(0, length);
    }

    private static IResult RateLimited(HttpContext context, RateLimitLease lease, string action, string? username)
    {
        DashboardAudit.RateLimited(Audit(context), action, AuditableUsername(username), ClientAddress(context));
        return AuthProblems.RateLimited(context, AuthRateLimits.RetryAfter(lease));
    }

    private static void AuditFailure(HttpContext context, string? username, CredentialVerification verification)
    {
        var logger = Audit(context);
        var address = ClientAddress(context);
        DashboardAudit.SignInFailed(logger, AuditableUsername(username), address, verification.Failure.ToString());
        if (verification.LockedUntilUtc is { } lockedUntil)
            DashboardAudit.AccountLockedOut(logger, userId: null, AuditableUsername(username), lockedUntil, address);
    }

    private static async Task WaitForFailureFloorAsync(TimeProvider time, long started, CancellationToken cancellationToken)
    {
        var target = FailureFloor + TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(MaxJitterMilliseconds));
        var remaining = target - time.GetElapsedTime(started);
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, time, cancellationToken);
    }

    /// <summary>Best effort: a session that ended must not fail because live connections could not be told.</summary>
    private static async Task NotifyUserChangedAsync(HttpContext context, Guid userId, string action)
    {
        try
        {
            await context.RequestServices.GetRequiredService<IAccessChangeObserver>().UsersChangedAsync([userId], CancellationToken.None);
        }
        catch (Exception ex)
        {
            DashboardAudit.AccessChangeNotificationFailed(Audit(context), ex, action);
        }
    }

    /// <summary>A submitted username is logged only when it passes the username rule.</summary>
    private static string? AuditableUsername(string? username) => AccessValidation.IsValidUsername(username) ? username : null;

    private static string? ClientAddress(HttpContext context) => context.Connection.RemoteIpAddress?.ToString();

    private static ILogger Audit(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(DashboardAudit.CategoryName);
}
