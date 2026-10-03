using Microsoft.AspNetCore.Mvc;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;

namespace VSaga.Dashboard.Api.Auth;

/// <summary>
/// The problem bodies every authentication and authorization failure answers with, whichever scheme handled
/// the request: a cookie that is missing, expired or revoked and an API key that is missing or wrong get the
/// same 401, so the response cannot be used to probe which credential was close, and every body points at
/// the documentation.
/// </summary>
public static class AuthProblems
{
    /// <summary>The sentence every 401 and 403 body ends with.</summary>
    public const string DocumentationPointer = "See docs/dashboard.md#authentication.";

    public const string UnauthenticatedCode = "unauthenticated";

    public const string ForbiddenCode = "forbidden";

    public const string PasswordChangeRequiredCode = "password_change_required";

    public const string AntiforgeryCode = "antiforgery";

    public const string InvalidCredentialsCode = "invalid_credentials";

    public const string ValidationCode = "validation";

    public const string RateLimitedCode = "rate_limited";

    public const string IdentityUnavailableCode = "identity_unavailable";

    public const string SetupUnavailableCode = "setup_unavailable";

    /// <summary>The detail of every failed sign-in, whatever the reason.</summary>
    public const string InvalidCredentialsDetail = "The username or password is not correct, or the account cannot sign in right now.";

    /// <summary>The 401 detail: how to sign in, and which API-key forms are accepted where.</summary>
    public static readonly string UnauthorizedDetail =
        "Sign in at /login, or send the dashboard API key as the "
        + $"'{ApiKeyAuthenticationDefaults.HeaderName}' header or an "
        + $"'Authorization: {ApiKeyAuthenticationDefaults.BearerPrefix.Trim()} <key>' header (the "
        + $"'{ApiKeyAuthenticationDefaults.QueryStringParameterName}' query string is accepted on /hubs only). "
        + DocumentationPointer;

    /// <summary>
    /// Writes 401 with <c>code</c> <see cref="UnauthenticatedCode"/>. Deliberately identical for every reason:
    /// the reason is logged by the scheme that failed, never echoed.
    /// </summary>
    public static Task WriteUnauthorizedAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Response.HasStarted)
            return Task.CompletedTask;

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = ApiKeyAuthenticationDefaults.SchemeName;
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Unauthorized",
            Detail = UnauthorizedDetail,
            Instance = context.Request.Path,
            Extensions = { ["code"] = UnauthenticatedCode },
        };
        return WriteAsync(context, problem);
    }

    /// <summary>
    /// Writes 403 naming the permission that was missing and the saga type it was checked for (each null
    /// when not known). A user who must change their password gets <see cref="PasswordChangeRequiredCode"/>,
    /// since that, not a grant, is what stands in the way.
    /// </summary>
    public static Task WriteForbiddenAsync(HttpContext context, CallerAccess? caller, string? permission, string? sagaType)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Response.HasStarted)
            return Task.CompletedTask;

        var (code, detail) = caller switch
        {
            { MustChangePassword: true } => (PasswordChangeRequiredCode, "Change your password before using the dashboard."),
            { Kind: CallerKind.ApiKey } when string.Equals(permission, Permissions.AccessManage, StringComparison.Ordinal) =>
                (ForbiddenCode, "The API key never holds access.manage; sign in as a user who does."),
            _ => (ForbiddenCode, ForbiddenDetail(permission, sagaType)),
        };

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Forbidden",
            Detail = $"{detail} {DocumentationPointer}",
            Instance = context.Request.Path,
            Extensions =
            {
                ["code"] = code,
                ["permission"] = permission,
                ["sagaType"] = sagaType,
            },
        };
        return WriteAsync(context, problem);
    }

    /// <summary>
    /// 401 <see cref="InvalidCredentialsCode"/>: one body for an unknown user, a wrong password, a locked and
    /// a disabled account, so the answer says nothing about which it was.
    /// </summary>
    public static IResult InvalidCredentials() =>
        Problem(StatusCodes.Status401Unauthorized, "Sign-in failed", InvalidCredentialsDetail, InvalidCredentialsCode);

    /// <summary>
    /// 400 <see cref="InvalidCredentialsCode"/> for a wrong current password on a password change, with the
    /// field named in <c>errors</c>. Not 401: the session itself is fine, and the SPA signs out on a 401.
    /// </summary>
    public static IResult WrongCurrentPassword(string message) =>
        CurrentPasswordProblem(message, "The current password is not correct");

    /// <summary>
    /// 400 <see cref="InvalidCredentialsCode"/> with <c>errors.currentPassword</c> for an account that is locked
    /// or disabled: the current password was not checked, so the title and message do not call it wrong.
    /// </summary>
    public static IResult CurrentPasswordRefused(string message) =>
        CurrentPasswordProblem(message, "The password cannot be changed right now");

    private static IResult CurrentPasswordProblem(string message, string title) =>
        TypedResults.ValidationProblem(
            new Dictionary<string, string[]>(StringComparer.Ordinal) { ["currentPassword"] = [message] },
            detail: message,
            title: title,
            extensions: Code(InvalidCredentialsCode));

    /// <summary>400 <see cref="ValidationCode"/>, <c>errors</c> keyed by camelCase request paths.</summary>
    public static IResult Validation(IReadOnlyDictionary<string, string[]> errors, string detail = "The request is not valid.") =>
        TypedResults.ValidationProblem(
            errors.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal),
            detail: detail,
            extensions: Code(ValidationCode));

    /// <summary>400 <see cref="AntiforgeryCode"/>: the request's antiforgery token is missing or does not match this session.</summary>
    public static IResult Antiforgery() =>
        Problem(
            StatusCodes.Status400BadRequest,
            "Antiforgery check failed",
            "The request's X-XSRF-TOKEN header is missing or does not match this session; read GET /api/auth/session again and retry.",
            AntiforgeryCode);

    /// <summary>
    /// 403 <see cref="ForbiddenCode"/> from <see cref="HubOriginGuard"/>: the hub was reached from a page on another
    /// origin. The expected origin is logged, not echoed.
    /// </summary>
    public static IResult ForeignHubOrigin() =>
        Problem(
            StatusCodes.Status403Forbidden,
            "Forbidden",
            "The live-update hub accepts connections only from the dashboard's own origin, or from Dashboard:WebOrigin when that is set. "
            + DocumentationPointer,
            ForbiddenCode);

    /// <summary>429 <see cref="RateLimitedCode"/> with a <c>Retry-After</c> header in whole seconds.</summary>
    public static IResult RateLimited(HttpContext context, TimeSpan retryAfter)
    {
        ArgumentNullException.ThrowIfNull(context);
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Problem(
            StatusCodes.Status429TooManyRequests,
            "Too many attempts",
            $"Too many attempts; try again in {seconds} s.",
            RateLimitedCode);
    }

    /// <summary>503 <see cref="IdentityUnavailableCode"/>: the identity store is not ready, so nobody can sign in.</summary>
    public static IResult IdentityUnavailable() =>
        Problem(
            StatusCodes.Status503ServiceUnavailable,
            "Sign-in is unavailable",
            "The identity store is not ready, so sign-in is unavailable; the identity check on /health says why.",
            IdentityUnavailableCode);

    /// <summary>
    /// 409 <see cref="SetupUnavailableCode"/>: first-run setup cannot be completed; <paramref name="detail"/> says
    /// why (a user exists, seed keys are set, the seed could not be applied, or no code is in force).
    /// </summary>
    public static IResult SetupUnavailable(string detail) =>
        Problem(StatusCodes.Status409Conflict, "First-run setup is not available", detail, SetupUnavailableCode);

    /// <summary>
    /// 400 <see cref="InvalidCredentialsCode"/> with <c>errors.code</c>: the one-time setup code is missing or
    /// wrong. Not 401, which the SPA reads as signed out.
    /// </summary>
    public static IResult WrongSetupCode()
    {
        const string message = "The setup code is not correct. Copy it from the API log (it was logged at start), or from Dashboard:Setup:Code when that is set.";
        return TypedResults.ValidationProblem(
            new Dictionary<string, string[]>(StringComparer.Ordinal) { ["code"] = [message] },
            detail: message,
            title: "The setup code is not correct",
            extensions: Code(InvalidCredentialsCode));
    }

    /// <summary>
    /// 409 with an access rule's <paramref name="code"/> (<c>username_taken</c>, <c>name_taken</c>,
    /// <c>role_in_use</c>, <c>role_immutable</c> or <c>last_administrator</c>, see <see cref="IdentityRuleCodes"/>)
    /// and the rule's own explanation.
    /// </summary>
    public static IResult AccessRuleConflict(string code, string detail) =>
        Problem(StatusCodes.Status409Conflict, "The change breaks an access rule", detail, code);

    /// <summary>404 for a user, team or role id that does not exist. The design's code list has none for it.</summary>
    public static IResult NotFound(string detail) =>
        TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status404NotFound, title: "Not found");

    private static IResult Problem(int status, string title, string detail, string code) =>
        TypedResults.Problem(detail: detail, statusCode: status, title: title, extensions: Code(code));

    private static Dictionary<string, object?> Code(string code) => new(StringComparer.Ordinal) { ["code"] = code };

    private static string ForbiddenDetail(string? permission, string? sagaType) => (permission, sagaType) switch
    {
        (null, _) => "You do not have access to this resource.",
        (_, null) => $"This needs the {permission} permission.",
        _ => $"This needs the {permission} permission for saga type '{sagaType}'.",
    };

    // The contentType argument is load-bearing: WriteAsJsonAsync sets "application/json" itself and would
    // overwrite a Response.ContentType assigned before the call.
    private static Task WriteAsync(HttpContext context, ProblemDetails problem) =>
        context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", context.RequestAborted);
}
