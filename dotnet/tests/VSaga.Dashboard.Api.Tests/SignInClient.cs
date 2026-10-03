using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using VSaga.Dashboard.Api.Auth;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// A browser-like client of one test host: it keeps cookies, remembers the antiforgery request token the
/// API last issued in the <c>XSRF-TOKEN</c> cookie, and sends it in <c>X-XSRF-TOKEN</c> on unsafe requests,
/// as the SPA does.
/// </summary>
internal sealed class SignInClient : IDisposable
{
    private SignInClient(HttpClient http) => Http = http;

    public HttpClient Http { get; }

    /// <summary>The request token from the last response that issued one; null before any.</summary>
    public string? Token { get; private set; }

    /// <summary>A client that has read the session once, so it holds a token (and the antiforgery cookie).</summary>
    public static async Task<SignInClient> StartAsync(WebApplicationFactory<Program> factory)
    {
        var client = new SignInClient(factory.CreateClient());
        using var session = await client.GetAsync("/api/auth/session");
        return client;
    }

    public async Task<HttpResponseMessage> GetAsync(string path)
    {
        var response = await Http.GetAsync(path);
        Remember(response);
        return response;
    }

    /// <summary>Posts <paramref name="json"/> (no body when null) with <paramref name="token"/>, by default the last one issued; pass "" for none.</summary>
    public Task<HttpResponseMessage> PostAsync(string path, string? json = null, string? token = null) =>
        PostContentAsync(path, json is null ? null : new StringContent(json, Encoding.UTF8, "application/json"), token);

    /// <summary>Posts <paramref name="content"/> (disposed with the request) with the token, as <see cref="PostAsync"/> does.</summary>
    public Task<HttpResponseMessage> PostContentAsync(string path, HttpContent? content, string? token = null) =>
        SendContentAsync(HttpMethod.Post, path, content, token);

    /// <summary>Sends <paramref name="json"/> (no body when null) with <paramref name="method"/> and the token, as <see cref="PostAsync"/> does.</summary>
    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null, string? token = null) =>
        SendContentAsync(method, path, json is null ? null : new StringContent(json, Encoding.UTF8, "application/json"), token);

    public Task<HttpResponseMessage> PutAsync(string path, string json) => SendAsync(HttpMethod.Put, path, json);

    public Task<HttpResponseMessage> DeleteAsync(string path) => SendAsync(HttpMethod.Delete, path);

    private async Task<HttpResponseMessage> SendContentAsync(HttpMethod method, string path, HttpContent? content, string? token)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Content = content;

        var sent = token ?? Token;
        if (!string.IsNullOrEmpty(sent))
            request.Headers.Add(AntiforgeryEnforcement.HeaderName, sent);

        var response = await Http.SendAsync(request);
        Remember(response);
        return response;
    }

    public Task<HttpResponseMessage> LoginAsync(string username, string password, string? token = null) =>
        PostAsync("/api/auth/login", JsonSerializer.Serialize(new { username, password }), token);

    public Task<HttpResponseMessage> ChangePasswordAsync(string currentPassword, string newPassword) =>
        PostAsync("/api/auth/password", JsonSerializer.Serialize(new { currentPassword, newPassword }));

    /// <summary>The session as the API describes it now.</summary>
    public async Task<JsonElement> SessionAsync()
    {
        using var response = await GetAsync("/api/auth/session");
        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response);
    }

    /// <summary>A UTF-8 JSON body that declares its length, or one sent without a Content-Length (streamed).</summary>
    public static HttpContent Body(string json, bool declaresItsLength)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        HttpContent content = declaresItsLength ? new ByteArrayContent(bytes) : new UnsizedContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return content;
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        await JsonSerializer.DeserializeAsync<JsonElement>(await response.Content.ReadAsStreamAsync());

    /// <summary>The value a response's <c>Set-Cookie</c> gives <paramref name="name"/>, or null when it sets none.</summary>
    public static string? SetCookie(HttpResponseMessage response, string name) =>
        SetCookieHeader(response, name)?[(name.Length + 1)..].Split(';', 2)[0];

    /// <summary>The whole <c>Set-Cookie</c> header a response sends for <paramref name="name"/>, or null.</summary>
    public static string? SetCookieHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.FirstOrDefault(c => c.StartsWith(name + "=", StringComparison.Ordinal))
            : null;

    public void Dispose() => Http.Dispose();

    private void Remember(HttpResponseMessage response)
    {
        if (SetCookie(response, AntiforgeryEnforcement.RequestTokenCookieName) is { Length: > 0 } token)
            Token = token;
    }

    private sealed class UnsizedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
