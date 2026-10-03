using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VSaga.Dashboard.Api.Auth;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;
using VSaga.Dashboard.Identity.Stores;
using static VSaga.Dashboard.Api.Tests.TestSessions;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// What the administration endpoint tests share: signed-in clients for an administrator and for callers who
/// must be refused, the golden JSON fixtures of the wire contract (checked in under
/// <c>dashboard-web/src/app/testing/contracts/admin/</c>, which <c>admin-api.service.spec.ts</c> asserts too,
/// and copied to the test output by the project file) and the comparisons against them.
/// </summary>
internal static class AdminApi
{
    public const string AdminPassword = "an administrator password";

    /// <summary>Carol's id in <c>team.request.json</c>: the fixture names an existing user, so the test creates her with it.</summary>
    public static readonly Guid FixtureMemberId = new("7f3c9a52-1d4e-4b8a-9c61-2f0e5b7d8a10");

    /// <summary>A value <see cref="AssertMatchesFixture"/> puts in place of a generated one, on both sides.</summary>
    private const string Generated = "<generated>";

    /// <summary>Creates <paramref name="username"/> with an unscoped Administrator grant and signs them in.</summary>
    public static Task<(DashboardUser User, SignInClient Client)> SignInAdministratorAsync(
        WebApplicationFactory<Program> host, string username = "admin") =>
        SignInAsync(host, username, AllTypes(BuiltInRoles.AdministratorId));

    /// <summary>Creates <paramref name="username"/> holding <paramref name="grants"/> and signs them in with <see cref="AdminPassword"/>.</summary>
    public static async Task<(DashboardUser User, SignInClient Client)> SignInAsync(
        WebApplicationFactory<Program> host, string username, params AccessGrant[] grants)
    {
        var user = await CreateUserWithPasswordAsync(host.Services, username, AdminPassword, grants: grants);
        var client = await SignInClient.StartAsync(host);
        using var login = await client.LoginAsync(username, AdminPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (user, client);
    }

    /// <summary>Signs in a user who already exists with <see cref="AdminPassword"/>.</summary>
    public static async Task<SignInClient> SignInExistingAsync(WebApplicationFactory<Program> host, string username)
    {
        var client = await SignInClient.StartAsync(host);
        using var login = await client.LoginAsync(username, AdminPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    /// <summary>A user written straight to the store with a chosen id, as a golden request that names one needs.</summary>
    public static async Task<DashboardUser> CreateUserWithIdAsync(IServiceProvider services, Guid id, string username)
    {
        var now = DateTimeOffset.UtcNow;
        var user = new DashboardUser(
            id, username, username, "not-a-password-hash", SecurityStamps.New(), IsEnabled: true, MustChangePassword: false,
            FailedSignInCount: 0, LockoutEndUtc: null, LastSignInAtUtc: null, now, now, []);
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDashboardIdentityStore>().CreateUserAsync(user, CancellationToken.None);
        return user;
    }

    /// <summary>A team written straight to the store, for a test whose own administrator holds access through it.</summary>
    public static async Task<DashboardTeam> CreateTeamAsync(IServiceProvider services, string name, Guid[] memberIds, params AccessGrant[] grants)
    {
        var team = new DashboardTeam(Guid.NewGuid(), name, Description: null, memberIds, grants);
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDashboardIdentityStore>().CreateTeamAsync(team, CancellationToken.None);
        return team;
    }

    /// <summary>A role written straight to the store.</summary>
    public static async Task<DashboardRole> CreateRoleAsync(IServiceProvider services, string name, params string[] permissions)
    {
        var role = new DashboardRole(Guid.NewGuid(), name, Description: null, IsBuiltIn: false, permissions);
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDashboardIdentityStore>().CreateRoleAsync(role, CancellationToken.None);
        return role;
    }

    /// <summary>The text of the golden fixture <paramref name="name"/>, exactly as checked in.</summary>
    public static string FixtureText(string name) =>
        File.ReadAllText(Path.Combine(FixtureDirectory, name));

    /// <summary>Every golden fixture's file name, ordinal order.</summary>
    public static IReadOnlyList<string> FixtureNames =>
        [.. Directory.GetFiles(FixtureDirectory, "*.json").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

    public static async Task<JsonNode> ReadNodeAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync()) ?? throw new InvalidOperationException("The response body is JSON null.");

    /// <summary>
    /// The response equals the fixture, ignoring member order, once every non-null value of a member named in
    /// <paramref name="generated"/> (ids and times the server makes up) is replaced on both sides. A null
    /// stays null, so a fixture that says "no lockout" still pins it.
    /// </summary>
    public static void AssertMatchesFixture(string fixture, JsonNode actual, params string[] generated)
    {
        var expected = Normalised(JsonNode.Parse(FixtureText(fixture))!, generated);
        var normalisedActual = Normalised(actual.DeepClone(), generated);
        Assert.True(
            JsonNode.DeepEquals(expected, normalisedActual),
            $"The response does not match {fixture}.{Environment.NewLine}Expected: {expected.ToJsonString()}{Environment.NewLine}Actual:   {normalisedActual.ToJsonString()}");
    }

    /// <summary>
    /// The response has the fixture's shape: the same members at every level (recursing into objects and into
    /// array elements when both sides have some) and values of the same JSON kind, a boolean being one kind.
    /// For answers of a fixture's type whose values differ from it (a list, an update).
    /// </summary>
    public static void AssertSameShape(string fixture, JsonNode? actual) =>
        AssertShape(JsonNode.Parse(FixtureText(fixture)), actual, "$", fixture);

    public static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string? code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await SignInClient.ReadJsonAsync(response);
        if (code is null)
            Assert.False(problem.TryGetProperty("code", out _));
        else
            Assert.Equal(code, problem.GetProperty("code").GetString());
        return problem;
    }

    /// <summary>The <c>errors</c> keys of a 400 validation problem, ordinal order.</summary>
    public static async Task<string[]> AssertValidationErrorsAsync(HttpResponseMessage response)
    {
        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, AuthProblems.ValidationCode);
        return [.. problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Every one of <paramref name="routes"/> is 401 without credentials, and 403 naming <c>access.manage</c> for a
    /// user without it (an Operator for all saga types), for a user whose Administrator grant is scoped to one saga
    /// type, and for the API key, which never holds it.
    /// </summary>
    public static async Task AssertOnlyUnscopedManagersGetThroughAsync(
        WebApplicationFactory<Program> host, IReadOnlyList<(HttpMethod Method, string Path)> routes)
    {
        var (_, operatorClient) = await SignInAsync(host, "operator", AllTypes(BuiltInRoles.OperatorId));
        var (_, scopedClient) = await SignInAsync(host, "scoped-admin", ForTypes(BuiltInRoles.AdministratorId, "OrderSaga"));
        using var anonymous = await SignInClient.StartAsync(host);
        using var apiKey = host.CreateClient();
        apiKey.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, DashboardApiFactory.TestApiKey);

        using (operatorClient)
        using (scopedClient)
        {
            foreach (var (method, path) in routes)
            {
                var body = method == HttpMethod.Get || method == HttpMethod.Delete ? null : "{}";

                using var unauthenticated = await anonymous.SendAsync(method, path, body);
                await AssertProblemAsync(unauthenticated, HttpStatusCode.Unauthorized, AuthProblems.UnauthenticatedCode);

                foreach (var client in new[] { operatorClient, scopedClient })
                {
                    using var refused = await client.SendAsync(method, path, body);
                    await AssertForbiddenAsync(refused, $"{method} {path}");
                }

                using var request = new HttpRequestMessage(method, path);
                if (body is not null)
                    request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                using var byKey = await apiKey.SendAsync(request);
                var problem = await AssertForbiddenAsync(byKey, $"{method} {path} with the API key");
                Assert.Contains("API key never holds access.manage", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
            }
        }
    }

    private static async Task<JsonElement> AssertForbiddenAsync(HttpResponseMessage response, string what)
    {
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{what}: expected 403, got {(int)response.StatusCode}");
        var problem = await AssertProblemAsync(response, HttpStatusCode.Forbidden, AuthProblems.ForbiddenCode);
        Assert.Equal(Permissions.AccessManage, problem.GetProperty("permission").GetString());
        return problem;
    }

    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Contracts", "Admin");

    private static JsonNode Normalised(JsonNode node, string[] generated)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj.ToList())
                {
                    if (value is null)
                        continue;
                    if (generated.Contains(name, StringComparer.Ordinal))
                        obj[name] = Generated;
                    else
                        Normalised(value, generated);
                }

                break;
            case JsonArray array:
                foreach (var item in array.OfType<JsonNode>())
                    Normalised(item, generated);
                break;
        }

        return node;
    }

    private static void AssertShape(JsonNode? expected, JsonNode? actual, string path, string fixture)
    {
        if (expected is null || actual is null)
            return;

        switch (expected)
        {
            case JsonObject expectedObject:
                var actualObject = Assert.IsType<JsonObject>(actual);
                Assert.True(
                    expectedObject.Select(p => p.Key).Order(StringComparer.Ordinal).SequenceEqual(actualObject.Select(p => p.Key).Order(StringComparer.Ordinal), StringComparer.Ordinal),
                    $"{path} has members [{string.Join(", ", actualObject.Select(p => p.Key))}], {fixture} has [{string.Join(", ", expectedObject.Select(p => p.Key))}]");
                foreach (var (name, value) in expectedObject)
                    AssertShape(value, actualObject[name], $"{path}.{name}", fixture);
                break;
            case JsonArray expectedArray:
                var actualArray = Assert.IsType<JsonArray>(actual);
                if (expectedArray.Count > 0)
                {
                    foreach (var item in actualArray)
                        AssertShape(expectedArray[0], item, path + "[]", fixture);
                }

                break;
            default:
                Assert.True(Kind(expected) == Kind(actual), $"{path} is a {actual.GetValueKind()}, {fixture} has a {expected.GetValueKind()}");
                break;
        }
    }

    private static JsonValueKind Kind(JsonNode node) => node.GetValueKind() is JsonValueKind.False ? JsonValueKind.True : node.GetValueKind();
}

/// <summary>Every log entry of a host, with its event id and formatted message.</summary>
internal sealed class AuditLogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<(string Category, int EventId, string Message)> _entries = new();

    /// <summary>The formatted messages of <see cref="DashboardAudit"/> events with <paramref name="eventId"/>.</summary>
    public IReadOnlyList<string> Audit(int eventId) =>
        [.. _entries.Where(e => e.EventId == eventId && string.Equals(e.Category, DashboardAudit.CategoryName, StringComparison.Ordinal)).Select(e => e.Message)];

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
        // Nothing to release; the entries outlive the host for the assertions.
    }

    private sealed class Logger(AuditLogCapture owner, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner._entries.Enqueue((categoryName, eventId.Id, formatter(state, exception)));
    }
}
