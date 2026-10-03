using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;
using static VSaga.Dashboard.Api.Tests.AdminApi;
using static VSaga.Dashboard.Api.Tests.TestSessions;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// <c>/api/admin/permissions</c> and <c>/api/admin/roles</c> (design §8.9): the catalogue and each role verb
/// against the golden fixtures, the built-in roles immutable, a role in use kept, validation keyed by request
/// path, unknown members refused, the last-administrator rule through a custom role, the audit trail, and who
/// may call them at all.
/// </summary>
public sealed class AdminRolesEndpointsTests : IAsyncLifetime, IAsyncDisposable
{
    private const string Roles = "/api/admin/roles";
    private const string PermissionsPath = "/api/admin/permissions";

    private readonly DashboardApiFactory _factory = new();
    private readonly AuditLogCapture _logs = new();
    private readonly WebApplicationFactory<Program> _host;

    public AdminRolesEndpointsTests() =>
        _host = _factory.WithWebHostBuilder(b => b.ConfigureLogging(logging => logging.AddProvider(_logs)));

    public Task InitializeAsync() => Task.CompletedTask;

    // xunit 2 calls IAsyncLifetime.DisposeAsync, never a test class's IAsyncDisposable.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Permissions_IsTheGoldenCatalogue_WithScopableAndImplies()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            using var response = await admin.GetAsync(PermissionsPath);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertMatchesFixture("permissions.response.json", await ReadNodeAsync(response));
        }
    }

    [Fact]
    public async Task Create_TheGoldenRequest_Is201_WithTheGoldenRole_AndItsLocation()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            using var created = await admin.PostAsync(Roles, FixtureText("role.request.json"));

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var role = await ReadNodeAsync(created);
            AssertMatchesFixture("role.response.json", role, "id");
            Assert.Equal($"{Roles}/{role["id"]!.GetValue<Guid>()}", created.Headers.Location?.OriginalString);

            using var read = await admin.GetAsync(created.Headers.Location!.OriginalString);
            Assert.True(JsonNode.DeepEquals(role, await ReadNodeAsync(read)));
        }
    }

    [Fact]
    public async Task Update_TheGoldenRequest_Is200_AndReplacesTheRole()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            var id = await CreateRoleAsync(admin, """{"name":"Helpdesk","description":"Old","permissions":["sagas.data"]}""");

            using var updated = await admin.PutAsync($"{Roles}/{id}", FixtureText("role.request.json"));

            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            var role = await ReadNodeAsync(updated);
            AssertMatchesFixture("role.response.json", role, "id");
            Assert.Equal(id, role["id"]!.GetValue<Guid>());
        }
    }

    [Fact]
    public async Task List_HoldsTheBuiltInRolesAndTheCustomOnes_InTheGoldenShape()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            await CreateRoleAsync(admin, FixtureText("role.request.json"));

            using var list = await admin.GetAsync(Roles);

            var roles = (await ReadNodeAsync(list)).AsArray();
            foreach (var role in roles)
                AssertSameShape("role.response.json", role);
            Assert.Equal(
                new[] { ("Administrator", true), ("Operator", true), ("Support", false), ("Viewer", true) },
                roles.Select(r => (r!["name"]!.GetValue<string>(), r["isBuiltIn"]!.GetValue<bool>())));
            var viewer = roles.Single(r => r!["id"]!.GetValue<Guid>() == BuiltInRoles.ViewerId)!;
            Assert.Equal([Permissions.SagasView, Permissions.SagasData], viewer["permissions"]!.AsArray().Select(p => p!.GetValue<string>()), StringComparer.Ordinal);
        }
    }

    [Fact]
    public async Task TheBuiltInRoles_CannotBeChangedOrDeleted_409RoleImmutable()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            using var update = await admin.PutAsync($"{Roles}/{BuiltInRoles.ViewerId}", """{"name":"Viewer","permissions":["sagas.view"]}""");
            using var delete = await admin.DeleteAsync($"{Roles}/{BuiltInRoles.OperatorId}");

            await AssertProblemAsync(update, HttpStatusCode.Conflict, IdentityRuleCodes.RoleImmutable);
            await AssertProblemAsync(delete, HttpStatusCode.Conflict, IdentityRuleCodes.RoleImmutable);
            using var viewer = await admin.GetAsync($"{Roles}/{BuiltInRoles.ViewerId}");
            Assert.Equal(2, (await ReadNodeAsync(viewer))["permissions"]!.AsArray().Count);
        }
    }

    [Fact]
    public async Task ARoleStillGranted_CannotBeDeleted_409RoleInUse_AndOnceUngrantedItCan()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            var id = await CreateRoleAsync(admin, FixtureText("role.request.json"));
            using var user = await admin.PostAsync(
                "/api/admin/users",
                $$"""{"username":"carol","displayName":"Carol","password":"a temporary password","grants":[{"roleId":"{{id}}","allSagaTypes":true}]}""");
            var carol = (await ReadNodeAsync(user))["id"]!.GetValue<Guid>();

            using var inUse = await admin.DeleteAsync($"{Roles}/{id}");
            using var ungrant = await admin.PutAsync($"/api/admin/users/{carol}", """{"grants":[]}""");
            using var delete = await admin.DeleteAsync($"{Roles}/{id}");
            using var read = await admin.GetAsync($"{Roles}/{id}");

            await AssertProblemAsync(inUse, HttpStatusCode.Conflict, IdentityRuleCodes.RoleInUse);
            Assert.Equal(HttpStatusCode.OK, ungrant.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            await AssertProblemAsync(read, HttpStatusCode.NotFound, code: null);
        }
    }

    [Theory]
    [InlineData("""{"name":"support","permissions":["sagas.view"]}""")]
    [InlineData("""{"name":"VIEWER","permissions":["sagas.view"]}""")]
    public async Task ANameTakenIgnoringCase_BuiltInNamesIncluded_Is409NameTaken(string body)
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            await CreateRoleAsync(admin, FixtureText("role.request.json"));

            using var response = await admin.PostAsync(Roles, body);

            await AssertProblemAsync(response, HttpStatusCode.Conflict, IdentityRuleCodes.NameTaken);
        }
    }

    [Theory]
    [InlineData("""{"name":" ","permissions":["sagas.view"]}""", new[] { "name" })]
    [InlineData("""{"name":"Empty","permissions":[]}""", new[] { "permissions" })]
    [InlineData("""{"name":"Empty"}""", new[] { "permissions" })]
    [InlineData("""{"name":"Odd","permissions":["sagas.view","sagas.delete",null]}""", new[] { "permissions[1]", "permissions[2]" })]
    public async Task ValidationErrors_AreKeyedByCamelCaseRequestPaths(string body, string[] keys)
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            using var response = await admin.PostAsync(Roles, body);

            Assert.Equal(keys, await AssertValidationErrorsAsync(response));
        }
    }

    [Theory]
    [InlineData("""{"name":"Support","permissions":["sagas.view"],"isBuiltIn":false}""", "isBuiltIn")]
    [InlineData("""{"name":"Support","permissions":"sagas.view"}""", "permissions")]
    public async Task AnUnknownMemberOrAWrongValue_Is400_NamingItsPath(string body, string member)
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            using var response = await admin.PostAsync(Roles, body);

            Assert.Equal(new[] { member }, await AssertValidationErrorsAsync(response));
        }
    }

    [Fact]
    public async Task TakingAccessManageFromTheRoleTheLastAdministratorHolds_Is409LastAdministrator()
    {
        var root = await AdminApi.CreateRoleAsync(
            _host.Services, "Root", Permissions.SagasView, Permissions.SagasData, Permissions.SagasRetry, Permissions.AccessManage);
        var (_, admin) = await SignInAsync(_host, "admin", AllTypes(root.Id));
        using (admin)
        {
            using var response = await admin.PutAsync($"{Roles}/{root.Id}", """{"name":"Root","permissions":["sagas.view"]}""");

            await AssertProblemAsync(response, HttpStatusCode.Conflict, IdentityRuleCodes.LastAdministrator);
            using var still = await admin.GetAsync($"{Roles}/{root.Id}");
            Assert.Contains(Permissions.AccessManage, (await ReadNodeAsync(still))["permissions"]!.AsArray().Select(p => p!.GetValue<string>()), StringComparer.Ordinal);
        }
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task AnUnknownId_Is404(string method)
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            var body = string.Equals(method, "PUT", StringComparison.Ordinal) ?"""{"name":"Nobody","permissions":["sagas.view"]}""" : null;
            using var response = await admin.SendAsync(new HttpMethod(method), $"{Roles}/{Guid.NewGuid()}", body);

            var problem = await AssertProblemAsync(response, HttpStatusCode.NotFound, code: null);
            Assert.Contains("No role with id", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task EveryRoleChange_IsAudited_UnderTheAdministratorsName_AndARefusalToo()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            var id = await CreateRoleAsync(admin, FixtureText("role.request.json"));
            (await admin.PutAsync($"{Roles}/{id}", """{"name":"Support","permissions":["sagas.view"]}""")).Dispose();
            (await admin.DeleteAsync($"{Roles}/{id}")).Dispose();
            (await admin.DeleteAsync($"{Roles}/{BuiltInRoles.ViewerId}")).Dispose();

            var changed = _logs.Audit(7100);
            foreach (var action in new[] { AccessActions.CreateRole, AccessActions.UpdateRole, AccessActions.DeleteRole })
                Assert.Contains(changed, m => m.StartsWith($"Audit: dashboard:admin {action} role {id}", StringComparison.Ordinal));
            Assert.Contains(_logs.Audit(7101), m => m.StartsWith($"Audit: dashboard:admin {AccessActions.DeleteRole} role {BuiltInRoles.ViewerId}", StringComparison.Ordinal)
                && m.EndsWith(IdentityRuleCodes.RoleImmutable, StringComparison.Ordinal));
        }
    }

    [Fact]
    public Task OnlyAnUnscopedManager_GetsThrough_ThePermissionsAndEveryRoleRoute()
    {
        var id = Guid.NewGuid();
        return AssertOnlyUnscopedManagersGetThroughAsync(
            _host,
            [
                (HttpMethod.Get, PermissionsPath),
                (HttpMethod.Get, Roles),
                (HttpMethod.Post, Roles),
                (HttpMethod.Get, $"{Roles}/{id}"),
                (HttpMethod.Put, $"{Roles}/{id}"),
                (HttpMethod.Delete, $"{Roles}/{id}"),
            ]);
    }

    private static async Task<Guid> CreateRoleAsync(SignInClient admin, string body)
    {
        using var created = await admin.PostAsync(Roles, body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await ReadNodeAsync(created))["id"]!.GetValue<Guid>();
    }
}
