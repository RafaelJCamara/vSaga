using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using VSaga.Dashboard.Api.Auth;
using VSaga.Dashboard.Api.Endpoints;
using VSaga.Dashboard.Identity;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;
using static VSaga.Dashboard.Api.Tests.AdminApi;
using static VSaga.Dashboard.Api.Tests.TestSessions;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// <c>/api/admin/users</c> (design §8.9): each verb against the golden fixtures, validation keyed by request
/// path, unknown members refused (team membership is not a user field), the conflicts, the forced password
/// change an administrator's password implies, disabling ending the user's session, the audit trail, and who
/// may call it at all.
/// </summary>
public sealed class AdminUsersEndpointsTests : IAsyncLifetime, IAsyncDisposable
{
    private const string Users = "/api/admin/users";
    private const string RequestSuffix = "Request";
    private const string ResponseSuffix = "Response";
    private const string CarolBody = """{"username":"carol","displayName":"Carol","password":"a temporary password"}""";

    private readonly DashboardApiFactory _factory = new();
    private readonly AuditLogCapture _logs = new();
    private readonly WebApplicationFactory<Program> _host;

    public AdminUsersEndpointsTests() =>
        _host = _factory.WithWebHostBuilder(b => b.ConfigureLogging(logging => logging.AddProvider(_logs)));

    public Task InitializeAsync() => Task.CompletedTask;

    // xunit 2 calls IAsyncLifetime.DisposeAsync, never a test class's IAsyncDisposable.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public void EveryAdministrationRecord_HasExactlyOneGoldenFixture()
    {
        Type[] authRecords = [typeof(SessionResponse), typeof(LoginRequest), typeof(SetupRequest), typeof(ChangePasswordRequest)];
        var records = typeof(AdminEndpoints).Assembly.GetExportedTypes()
            .Where(t => string.Equals(t.Namespace, typeof(AdminEndpoints).Namespace, StringComparison.Ordinal)
                && t.GetMethod("<Clone>$") is not null
                && (t.Name.EndsWith(RequestSuffix, StringComparison.Ordinal) || t.Name.EndsWith(ResponseSuffix, StringComparison.Ordinal))
                && !authRecords.Contains(t))
            .ToList();
        string[] problems = ["conflict-problem.response.json", "validation-problem.response.json"];

        Assert.Contains(typeof(CreateUserRequest), records);
        Assert.Equal(records.Select(FixtureNameOf).Concat(problems).Order(StringComparer.Ordinal), FixtureNames, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Create_TheGoldenRequest_Is201_WithTheGoldenUser_AndItsLocation()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            using var created = await admin.PostAsync(Users, FixtureText("create-user.request.json"));

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var user = await ReadNodeAsync(created);
            AssertMatchesFixture("user.response.json", user, "id", "createdAtUtc");
            Assert.Equal($"{Users}/{user["id"]!.GetValue<Guid>()}", created.Headers.Location?.OriginalString);

            using var read = await admin.GetAsync(created.Headers.Location!.OriginalString);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal(user.ToJsonString(), (await ReadNodeAsync(read)).ToJsonString());
        }
    }

    [Fact]
    public async Task Update_TheGoldenRequest_Is200_AndChangesWhatItNames()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            var id = await CreateAliceAsync(admin);

            using var updated = await admin.PutAsync($"{Users}/{id}", FixtureText("update-user.request.json"));

            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            var user = await ReadNodeAsync(updated);
            AssertSameShape("user.response.json", user);
            Assert.Equal("Alice Example (payments)", user["displayName"]!.GetValue<string>());
            Assert.False(user["isEnabled"]!.GetValue<bool>());
            var grant = Assert.Single(user["grants"]!.AsArray());
            Assert.Equal(BuiltInRoles.ViewerId, grant!["roleId"]!.GetValue<Guid>());
        }
    }

    [Fact]
    public async Task Update_LeavesWhatItDoesNotName()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            var id = await CreateAliceAsync(admin);

            using var updated = await admin.PutAsync($"{Users}/{id}", """{"displayName":"Alice"}""");

            var user = await ReadNodeAsync(updated);
            Assert.Equal("Alice", user["displayName"]!.GetValue<string>());
            Assert.True(user["isEnabled"]!.GetValue<bool>());
            Assert.Equal(2, user["grants"]!.AsArray().Count);
        }
    }

    [Fact]
    public async Task ResetPassword_TheGoldenRequest_EndsTheUsersSession_AndTheNewPasswordMustBeChanged()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        var (bob, bobClient) = await SignInAsync(_host, "bob", AllTypes(BuiltInRoles.ViewerId));
        using (admin)
        using (bobClient)
        {
            using var reset = await admin.PostAsync($"{Users}/{bob.Id}/password", FixtureText("reset-password.request.json"));

            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
            var user = await ReadNodeAsync(reset);
            AssertSameShape("user.response.json", user);
            Assert.True(user["mustChangePassword"]!.GetValue<bool>());
            using var oldSession = await bobClient.GetAsync("/api/sagas");
            Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);

            using var relogin = await SignInClient.StartAsync(_host);
            using var login = await relogin.LoginAsync("bob", "another temporary password");
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            Assert.True((await relogin.SessionAsync()).GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
        }
    }

    [Theory]
    [InlineData("""{"newPassword":"another temporary password"}""", true)]
    [InlineData("""{"newPassword":"another temporary password","mustChangePassword":false}""", false)]
    public async Task ResetPassword_MustChangePassword_DefaultsToTrue(string body, bool expected)
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        var bob = await CreateUserWithPasswordAsync(_host.Services, "bob", AdminPassword);
        using (admin)
        {
            using var reset = await admin.PostAsync($"{Users}/{bob.Id}/password", body);

            Assert.Equal(expected, (await ReadNodeAsync(reset))["mustChangePassword"]!.GetValue<bool>());
        }
    }

    [Theory]
    [InlineData("""{"username":"carol","displayName":"Carol","password":"a temporary password"}""", true)]
    [InlineData("""{"username":"carol","displayName":"Carol","password":"a temporary password","mustChangePassword":false}""", false)]
    public async Task Create_MustChangePassword_DefaultsToTrue_AndGrantsToNone(string body, bool expected)
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            using var created = await admin.PostAsync(Users, body);

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var user = await ReadNodeAsync(created);
            Assert.Equal(expected, user["mustChangePassword"]!.GetValue<bool>());
            Assert.Empty(user["grants"]!.AsArray());
            Assert.Empty(user["teamIds"]!.AsArray());
        }
    }

    [Fact]
    public async Task ListAndGet_HaveTheGoldenShape_AndTeamIdsComeFromTheTeams()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        var carol = await CreateUserWithIdAsync(_host.Services, FixtureMemberId, "carol");
        using (admin)
        {
            using var team = await admin.PostAsync("/api/admin/teams", FixtureText("team.request.json"));
            var teamId = (await ReadNodeAsync(team))["id"]!.GetValue<Guid>();

            using var list = await admin.GetAsync(Users);
            using var one = await admin.GetAsync($"{Users}/{carol.Id}");

            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var users = (await ReadNodeAsync(list)).AsArray();
            Assert.Equal(["admin", "carol"], users.Select(u => u!["username"]!.GetValue<string>()), StringComparer.Ordinal);
            foreach (var user in users)
                AssertSameShape("user.response.json", user);
            Assert.Equal([teamId], users[1]!["teamIds"]!.AsArray().Select(t => t!.GetValue<Guid>()));
            var wireTeamId = users[1]!["teamIds"]![0]!;
            Assert.Equal(JsonValueKind.String, wireTeamId.GetValueKind());
            Assert.True(Guid.TryParse(wireTeamId.GetValue<string>(), out _));
            Assert.Empty(users[0]!["teamIds"]!.AsArray());
            Assert.True(JsonNode.DeepEquals(users[1], await ReadNodeAsync(one)));
        }
    }

    [Theory]
    [InlineData("POST", "", """{"username":"carol","displayName":"Carol","password":"a temporary password","teamIds":[]}""", "teamIds")]
    [InlineData("POST", "", """{"username":"carol","displayName":"Carol","password":"a temporary password","enabled":true}""", "enabled")]
    [InlineData("PUT", "/{id}", """{"teamIds":[]}""", "teamIds")]
    [InlineData("PUT", "/{id}", """{"enabled":false}""", "enabled")]
    [InlineData("PUT", "/{id}", """{"grants":[{"roleId":"a0000000-0000-0000-0000-000000000003","allSagaTypes":true,"builtIn":false}]}""", "grants[0].builtIn")]
    [InlineData("PUT", "/{id}", """{"isEnabled":"no"}""", "isEnabled")]
    [InlineData("PUT", "/{id}", """{"isEnabled":false,"IsEnabled":true}""", "IsEnabled")]
    [InlineData("POST", "/{id}/password", """{"newPassword":"another temporary password","isEnabled":true}""", "isEnabled")]
    public async Task AnUnknownMemberOrAWrongValue_Is400_NamingItsPath_AndChangesNothing(string method, string route, string body, string member)
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            var id = await CreateAliceAsync(admin);

            using var response = await admin.SendAsync(new HttpMethod(method), Users + route.Replace("{id}", id.ToString(), StringComparison.Ordinal), body);

            Assert.Equal([member], await AssertValidationErrorsAsync(response));
            using var list = await admin.GetAsync(Users);
            var users = (await ReadNodeAsync(list)).AsArray();
            Assert.Equal(2, users.Count);
            var alice = users.Single(u => string.Equals(u!["username"]!.GetValue<string>(), "alice", StringComparison.Ordinal))!;
            Assert.True(alice["isEnabled"]!.GetValue<bool>());
            Assert.Equal(2, alice["grants"]!.AsArray().Count);

            // A refused reset must not have set the password: Alice still signs in with the one she was created with.
            var password = JsonNode.Parse(FixtureText("create-user.request.json"))!["password"]!.GetValue<string>();
            using var relogin = await SignInClient.StartAsync(_host);
            using var login = await relogin.LoginAsync("alice", password);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
    }

    [Fact]
    public async Task ValidationErrors_AreKeyedByCamelCaseRequestPaths()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            using var fields = await admin.PostAsync(Users, """{"username":"-x","displayName":" ","password":"short"}""");
            using var grants = await admin.PostAsync(Users, GrantErrorsBody);

            Assert.Equal(["displayName", "password", "username"], await AssertValidationErrorsAsync(fields));
            Assert.Equal(["grants[0].sagaTypes", "grants[1].roleId"], await AssertValidationErrorsAsync(grants));
        }
    }

    [Fact]
    public async Task AValidationProblem_HasTheGoldenShape()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            using var response = await admin.PostAsync(Users, GrantErrorsBody);

            AssertMatchesFixture("validation-problem.response.json", await ReadNodeAsync(response));
        }
    }

    [Fact]
    public async Task Create_AUsernameTakenIgnoringCase_Is409UsernameTaken()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            await CreateAliceAsync(admin);

            using var response = await admin.PostAsync(Users, """{"username":"ALICE","displayName":"Another Alice","password":"a temporary password"}""");

            await AssertProblemAsync(response, HttpStatusCode.Conflict, IdentityRuleCodes.UsernameTaken);
        }
    }

    [Fact]
    public async Task DisablingOrDeletingTheLastAdministrator_Is409_WithTheGoldenProblem_AndChangesNothing()
    {
        var (me, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            using var disable = await admin.PutAsync($"{Users}/{me.Id}", """{"isEnabled":false}""");
            using var demote = await admin.PutAsync($"{Users}/{me.Id}", """{"grants":[]}""");
            using var delete = await admin.DeleteAsync($"{Users}/{me.Id}");

            AssertMatchesFixture("conflict-problem.response.json", await ReadNodeAsync(disable));
            await AssertProblemAsync(disable, HttpStatusCode.Conflict, IdentityRuleCodes.LastAdministrator);
            await AssertProblemAsync(demote, HttpStatusCode.Conflict, IdentityRuleCodes.LastAdministrator);
            await AssertProblemAsync(delete, HttpStatusCode.Conflict, IdentityRuleCodes.LastAdministrator);
            using var still = await admin.GetAsync($"{Users}/{me.Id}");
            var user = await ReadNodeAsync(still);
            Assert.True(user["isEnabled"]!.GetValue<bool>());
            Assert.Single(user["grants"]!.AsArray());
        }
    }

    [Fact]
    public async Task Update_IsEnabledFalse_EndsThatUsersSession()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        var (bob, bobClient) = await SignInAsync(_host, "bob", AllTypes(BuiltInRoles.ViewerId));
        using (admin)
        using (bobClient)
        {
            using var before = await bobClient.GetAsync("/api/sagas");
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

            using var disable = await admin.PutAsync($"{Users}/{bob.Id}", """{"isEnabled":false}""");

            Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
            using var after = await bobClient.GetAsync("/api/sagas");
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        }
    }

    [Fact]
    public async Task Delete_Is204_AndTheUserIsGone()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            var id = await CreateAliceAsync(admin);

            using var delete = await admin.DeleteAsync($"{Users}/{id}");
            using var read = await admin.GetAsync($"{Users}/{id}");

            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            await AssertProblemAsync(read, HttpStatusCode.NotFound, code: null);
        }
    }

    [Fact]
    public async Task Unlock_ClearsTheLockout()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        var locked = await CreateUserWithPasswordAsync(_host.Services, "bob", AdminPassword, lockoutEndUtc: DateTimeOffset.UtcNow.AddMinutes(15));
        using (admin)
        {
            using var before = await admin.GetAsync($"{Users}/{locked.Id}");
            using var unlock = await admin.PostAsync($"{Users}/{locked.Id}/unlock");

            var lockedUntil = (await ReadNodeAsync(before))["lockedUntilUtc"];
            Assert.NotNull(lockedUntil);
            Assert.Equal(JsonValueKind.String, lockedUntil.GetValueKind());
            Assert.True(DateTimeOffset.TryParse(lockedUntil.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var lockedUntilUtc));
            Assert.True(lockedUntilUtc > DateTimeOffset.UtcNow);
            Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
            var user = await ReadNodeAsync(unlock);
            AssertSameShape("user.response.json", user);
            Assert.Null(user["lockedUntilUtc"]);
            using var bob = await SignInClient.StartAsync(_host);
            using var login = await bob.LoginAsync("bob", AdminPassword);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
    }

    [Theory]
    [InlineData("GET", "")]
    [InlineData("PUT", "")]
    [InlineData("DELETE", "")]
    [InlineData("POST", "/password")]
    [InlineData("POST", "/unlock")]
    public async Task AnUnknownId_Is404(string method, string suffix)
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            var body = (method, suffix) switch
            {
                ("PUT", _) => """{"displayName":"Nobody"}""",
                ("POST", "/password") => """{"newPassword":"another temporary password"}""",
                _ => null,
            };

            using var response = await admin.SendAsync(new HttpMethod(method), $"{Users}/{Guid.NewGuid()}{suffix}", body);

            var problem = await AssertProblemAsync(response, HttpStatusCode.NotFound, code: null);
            Assert.Contains("No user with id", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task EveryUserChange_IsAudited_UnderTheAdministratorsName_AndARefusalToo()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            var id = await CreateAliceAsync(admin);
            (await admin.PutAsync($"{Users}/{id}", """{"displayName":"Alice"}""")).Dispose();
            (await admin.PostAsync($"{Users}/{id}/password", """{"newPassword":"another temporary password"}""")).Dispose();
            (await admin.PostAsync($"{Users}/{id}/unlock")).Dispose();
            (await admin.DeleteAsync($"{Users}/{id}")).Dispose();
            (await admin.PostAsync(Users, """{"username":"admin","displayName":"Again","password":"a temporary password"}""")).Dispose();
            (await admin.PostAsync(Users, """{"username":"carol","displayName":"Carol","password":"a temporary password","teamIds":[]}""")).Dispose();

            var changed = _logs.Audit(7100);
            foreach (var action in new[] { AccessActions.CreateUser, AccessActions.UpdateUser, AccessActions.ResetPassword, AccessActions.UnlockUser, AccessActions.DeleteUser })
                Assert.Contains(changed, m => m.StartsWith($"Audit: dashboard:admin {action} user {id}", StringComparison.Ordinal));
            Assert.Contains(_logs.Audit(7101), m => m.StartsWith($"Audit: dashboard:admin {AccessActions.CreateUser} user", StringComparison.Ordinal)
                && m.EndsWith(IdentityRuleCodes.UsernameTaken, StringComparison.Ordinal));
            // The body naming teamIds is refused before the service sees it, and is audited all the same.
            Assert.Contains(_logs.Audit(7101), m => m.StartsWith($"Audit: dashboard:admin {AccessActions.CreateUser} user", StringComparison.Ordinal)
                && m.EndsWith(AuthProblems.ValidationCode, StringComparison.Ordinal));
            Assert.DoesNotContain(changed.Concat(_logs.Audit(7101)), m => m.Contains("temporary password", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task WithoutTheXsrfToken_EveryUserChange_Is400Antiforgery_AndTheUserIsUnchanged()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            var id = await CreateAliceAsync(admin);
            using var before = await admin.GetAsync($"{Users}/{id}");
            var alice = await ReadNodeAsync(before);

            (HttpMethod Method, string Path, string? Body)[] changes =
            [
                (HttpMethod.Post, Users, CarolBody),
                (HttpMethod.Put, $"{Users}/{id}", """{"displayName":"Not Alice","isEnabled":false}"""),
                (HttpMethod.Delete, $"{Users}/{id}", null),
                (HttpMethod.Post, $"{Users}/{id}/password", """{"newPassword":"another temporary password","mustChangePassword":false}"""),
            ];
            foreach (var (method, path, body) in changes)
            {
                using var refused = await admin.SendAsync(method, path, body, token: "");
                await AssertProblemAsync(refused, HttpStatusCode.BadRequest, AuthProblems.AntiforgeryCode);
            }

            using var after = await admin.GetAsync($"{Users}/{id}");
            Assert.Equal(HttpStatusCode.OK, after.StatusCode);
            Assert.True(JsonNode.DeepEquals(alice, await ReadNodeAsync(after)));
            using var list = await admin.GetAsync(Users);
            Assert.Equal(["admin", "alice"], (await ReadNodeAsync(list)).AsArray().Select(u => u!["username"]!.GetValue<string>()), StringComparer.Ordinal);
        }
    }

    [Fact]
    public async Task TheApiKey_ConfiguredAsAdministrator_Is403AccessManage_AndCreatesNoUser()
    {
        await using var host = _host.WithWebHostBuilder(b => b.UseSetting(DashboardSecuritySettings.ApiKeyRoleKey, "Administrator"));
        using var apiKey = host.CreateClient();
        apiKey.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, DashboardApiFactory.TestApiKey);

        using var list = await apiKey.GetAsync(Users);
        using var create = await apiKey.PostAsync(Users, new StringContent(CarolBody, System.Text.Encoding.UTF8, "application/json"));

        foreach (var refused in new[] { list, create })
        {
            var problem = await AssertProblemAsync(refused, HttpStatusCode.Forbidden, AuthProblems.ForbiddenCode);
            Assert.Equal(Permissions.AccessManage, problem.GetProperty("permission").GetString());
        }

        var (_, admin) = await SignInAdministratorAsync(host);
        using (admin)
        {
            using var users = await admin.GetAsync(Users);
            Assert.Equal(["admin"], (await ReadNodeAsync(users)).AsArray().Select(u => u!["username"]!.GetValue<string>()), StringComparer.Ordinal);
        }
    }

    [Fact]
    public Task OnlyAnUnscopedManager_GetsThrough_EveryUserRoute()
    {
        var id = Guid.NewGuid();
        return AssertOnlyUnscopedManagersGetThroughAsync(
            _host,
            [
                (HttpMethod.Get, Users),
                (HttpMethod.Post, Users),
                (HttpMethod.Get, $"{Users}/{id}"),
                (HttpMethod.Put, $"{Users}/{id}"),
                (HttpMethod.Delete, $"{Users}/{id}"),
                (HttpMethod.Post, $"{Users}/{id}/password"),
                (HttpMethod.Post, $"{Users}/{id}/unlock"),
            ]);
    }

    /// <summary>A grant naming no saga type, and a grant naming no role: both are reported by path.</summary>
    private static string GrantErrorsBody =>
        JsonSerializer.Serialize(new CreateUserRequest(
            "carol",
            "Carol",
            "a temporary password",
            MustChangePassword: null,
            [new GrantDto(BuiltInRoles.ViewerId, AllSagaTypes: false, []), new GrantDto(Guid.Empty, AllSagaTypes: true, null)]),
            JsonSerializerOptions.Web);

    /// <summary>
    /// The fixture a record's wire shape is pinned by: <c>CreateUserRequest</c> is <c>create-user.request.json</c>.
    /// <c>PermissionResponse</c> is only ever answered as the list, so its fixture is the plural.
    /// </summary>
    private static string FixtureNameOf(Type record)
    {
        var suffix = record.Name.EndsWith(RequestSuffix, StringComparison.Ordinal) ? RequestSuffix : ResponseSuffix;
        var stem = record.Name[..^suffix.Length] + (record == typeof(PermissionResponse) ? "s" : "");
        var kebab = new System.Text.StringBuilder();
        foreach (var c in stem)
        {
            if (char.IsUpper(c) && kebab.Length > 0)
                kebab.Append('-');
            kebab.Append(char.ToLowerInvariant(c));
        }

        return $"{kebab}.{suffix.ToLowerInvariant()}.json";
    }

    private static async Task<Guid> CreateAliceAsync(SignInClient admin)
    {
        using var created = await admin.PostAsync(Users, FixtureText("create-user.request.json"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await ReadNodeAsync(created))["id"]!.GetValue<Guid>();
    }
}
