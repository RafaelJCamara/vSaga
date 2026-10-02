using VSaga.Dashboard.Identity.Services;

namespace VSaga.Dashboard.Api.Auth;

/// <summary>
/// The caller resolved while authenticating this request, set by the scheme that authenticated it (the
/// cookie's <c>ValidatePrincipal</c> or the API-key handler), so authorization and the endpoints read the
/// store once per request.
/// </summary>
public interface ICallerAccessFeature
{
    CallerAccess Caller { get; }
}

/// <summary>The request feature holding the resolved caller.</summary>
public sealed class CallerAccessFeature(CallerAccess caller) : ICallerAccessFeature
{
    public CallerAccess Caller { get; } = caller;
}

public static class CallerAccessHttpContextExtensions
{
    /// <summary>The caller resolved for this request, or null when it was not authenticated.</summary>
    public static CallerAccess? GetCaller(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<ICallerAccessFeature>()?.Caller;
    }

    internal static void SetCaller(this HttpContext context, CallerAccess caller) =>
        context.Features.Set<ICallerAccessFeature>(new CallerAccessFeature(caller));
}
