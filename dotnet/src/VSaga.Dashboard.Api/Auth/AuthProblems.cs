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
