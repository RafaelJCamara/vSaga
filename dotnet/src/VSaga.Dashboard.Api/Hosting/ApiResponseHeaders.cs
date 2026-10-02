namespace VSaga.Dashboard.Api.Hosting;

/// <summary>
/// Marks every <c>/api</c> response, successes and failures alike, as never to be stored by a browser or a
/// proxy (saga data, and from sign-in on the session, live in these bodies) and as JSON a browser must not
/// sniff into something else. The API port is reachable without nginx, so the API sets them itself.
/// </summary>
internal static class ApiResponseHeaders
{
    internal const string CacheControl = "no-store";

    internal const string ContentTypeOptions = "nosniff";

    private static readonly PathString ApiPath = new("/api");

    /// <summary>
    /// Call before authentication, so a 401 or 403 carries the headers too. They are applied as the response
    /// starts, after whatever the endpoint set. The path test is the router's: case-insensitive.
    /// </summary>
    internal static WebApplication UseApiResponseHeaders(this WebApplication app)
    {
        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(ApiPath, StringComparison.OrdinalIgnoreCase))
                context.Response.OnStarting(Apply, context.Response);

            return next(context);
        });
        return app;
    }

    private static Task Apply(object state)
    {
        var response = (HttpResponse)state;
        response.Headers.CacheControl = CacheControl;
        response.Headers.XContentTypeOptions = ContentTypeOptions;
        return Task.CompletedTask;
    }
}
