using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using VSaga.Dashboard.Api.Auth;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace VSaga.Dashboard.Api.Endpoints;

/// <summary>
/// Reads a JSON request body for the <c>/api/auth</c> and <c>/api/admin</c> endpoints instead of letting the
/// framework bind it, so that a body the request records refuse (an unknown member, under
/// <c>JsonUnmappedMemberHandling.Disallow</c>, a member named twice, or a value of the wrong type) is a 400 <c>validation</c> problem
/// naming the member by its camelCase request path, such as <c>grants[0].roleId</c>, rather than the
/// framework's bare 400.
/// </summary>
internal static class JsonRequestBody
{
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> StrictOptions = new();

    /// <summary>
    /// The JSON body as <typeparamref name="T"/> with the API's serializer options, or a 400 validation problem
    /// naming the offending member when it is malformed, has an unknown member, names a member twice or is
    /// missing, or naming
    /// <c>request</c> when it is larger than <paramref name="maxBytes"/>.
    /// </summary>
    public static async Task<(T? Request, IResult? Invalid)> ReadAsync<T>(HttpContext context, int maxBytes)
        where T : class
    {
        if (await ReadCappedAsync(context, maxBytes) is not { } body)
            return (null, Invalid("request", $"The request body is larger than {SizeText(maxBytes)}."));

        var options = Strict(context.RequestServices.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions);
        try
        {
            var request = JsonSerializer.Deserialize<T>(body.Span, options);
            if (request is not null)
                return (request, null);
        }
        catch (JsonException ex)
        {
            var member = ex.Path is { Length: > 2 } path && path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : "request";
            return (null, Invalid(member, "Not a member of this request, or not a valid value for it."));
        }

        return (null, Invalid("request", "Send the request as a JSON object."));
    }

    /// <summary>
    /// The request body, or null when it is larger than <paramref name="maxBytes"/>. A declared length over the
    /// cap is refused unread; otherwise at most one byte past the cap (or past the declared length) is read, and
    /// the server is told the cap too where it lets the limit be changed, so an oversized body costs no more than
    /// the cap to refuse.
    /// </summary>
    private static async Task<ReadOnlyMemory<byte>?> ReadCappedAsync(HttpContext context, int maxBytes)
    {
        var declared = context.Request.ContentLength;
        if (declared > maxBytes)
            return null;

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } serverLimit)
            serverLimit.MaxRequestBodySize = maxBytes;

        // A body that declares its length gets a buffer of that size: the administration cap is large enough
        // that allocating all of it for every small request would be wasteful.
        var buffer = new byte[(declared is { } length ? (int)length : maxBytes) + 1];
        var read = 0;
        try
        {
            var last = -1;
            while (last != 0 && read < buffer.Length)
            {
                last = await context.Request.Body.ReadAsync(buffer.AsMemory(read), context.RequestAborted);
                read += last;
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }

        return read > maxBytes ? null : buffer.AsMemory(0, read);
    }

    /// <summary>
    /// A copy of the API's serializer options that also refuses a member named twice (<c>AllowDuplicateProperties
    /// = false</c>), case variants included, so <c>{"isEnabled":false,"IsEnabled":true}</c> is a 400 rather than
    /// whichever came last. One copy per options instance, made on first use.
    /// </summary>
    private static JsonSerializerOptions Strict(JsonSerializerOptions options) =>
        StrictOptions.GetValue(options, static source => new JsonSerializerOptions(source) { AllowDuplicateProperties = false });

    private static IResult Invalid(string member, string message) =>
        AuthProblems.Validation(new Dictionary<string, string[]>(StringComparer.Ordinal) { [member] = [message] });

    private static string SizeText(int bytes) =>
        bytes % (1024 * 1024) == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024 * 1024)} MiB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024} KiB");
}
