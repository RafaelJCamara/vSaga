using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using VSaga.Dashboard.Api.Endpoints;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;
using static VSaga.Dashboard.Api.Tests.AdminApi;
using static VSaga.Dashboard.Api.Tests.TestSessions;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// <c>/api/admin/teams</c> (design §8.9): each verb against the golden fixtures, membership written through the
/// team and read back on the user, validation keyed by request path, unknown members refused, the conflicts,
/// the audit trail, and who may call it at all.
/// </summary>
public sealed class AdminTeamsEndpointsTests : IAsyncLifetime, IAsyncDisposable
{
    private const string Teams = "/api/admin/teams";

    private readonly DashboardApiFactory _factory = new();
    private readonly AuditLogCapture _logs = new();
    private readonly WebApplicationFactory<Program> _host;

    public AdminTeamsEndpointsTests() =>
        _host = _factory.WithWebHostBuilder(b => b.ConfigureLogging(logging => logging.AddProvider(_logs)));

    public Task InitializeAsync() => Task.CompletedTask;

    // xunit 2 calls IAsyncLifetime.DisposeAsync, never a test class's IAsyncDisposable.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Create_TheGoldenRequest_Is201_WithTheGoldenTeam_AndItsLocation()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        await CreateUserWithIdAsync(_host.Services, FixtureMemberId, "carol");
        using (admin)
        {
            using var created = await admin.PostAsync(Teams, FixtureText("team.request.json"));

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var team = await ReadNodeAsync(created);
            AssertMatchesFixture("team.response.json", team, "id");
            Assert.Equal($"{Teams}/{team["id"]!.GetValue<Guid>()}", created.Headers.Location?.OriginalString);

            using var read = await admin.GetAsync(created.Headers.Location!.OriginalString);
            Assert.True(JsonNode.DeepEquals(team, await ReadNodeAsync(read)));
        }
    }

    [Fact]
    public async Task Update_TheGoldenRequest_Is200_AndReplacesTheWholeTeam()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        await CreateUserWithIdAsync(_host.Services, FixtureMemberId, "carol");
        var dave = await CreateUserAsync(_host.Services, "dave");
        using (admin)
        {
            var id = await CreateTeamAsync(admin, $$"""{"name":"Ops","description":"Old","memberIds":["{{dave.Id}}"]}""");

            using var updated = await admin.PutAsync($"{Teams}/{id}", FixtureText("team.request.json"));

            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            var team = await ReadNodeAsync(updated);
            AssertMatchesFixture("team.response.json", team, "id");
            Assert.Equal(id, team["id"]!.GetValue<Guid>());
        }
    }

    [Fact]
    public async Task List_HasTheGoldenShape_ByName_AndArraysAreNeverNull()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        await CreateUserWithIdAsync(_host.Services, FixtureMemberId, "carol");
        using (admin)
        {
            await CreateTeamAsync(admin, FixtureText("team.request.json"));
            await CreateTeamAsync(admin, """{"name":"Auditors"}""");

            using var list = await admin.GetAsync(Teams);

            var teams = (await ReadNodeAsync(list)).AsArray();
            Assert.Equal(["Auditors", "Payments"], teams.Select(t => t!["name"]!.GetValue<string>()), StringComparer.Ordinal);
            foreach (var team in teams)
                AssertSameShape("team.response.json", team);
            Assert.Null(teams[0]!["description"]);
            Assert.Empty(teams[0]!["memberIds"]!.AsArray());
            Assert.Empty(teams[0]!["grants"]!.AsArray());
        }
    }

    [Fact]
    public async Task Membership_IsWrittenThroughTheTeam_AndReadOnTheUser()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        var (carol, carolClient) = await SignInAsync(_host, "carol");
        using (admin)
        using (carolClient)
        {
            using var before = await carolClient.GetAsync("/api/sagas");
            var id = await CreateTeamAsync(
                admin, $$"""{"name":"Viewers","memberIds":["{{carol.Id}}"],"grants":[{"roleId":"{{BuiltInRoles.ViewerId}}","allSagaTypes":true}]}""");

            using var member = await admin.GetAsync($"/api/admin/users/{carol.Id}");
            using var withAccess = await carolClient.GetAsync("/api/sagas");
            using var removed = await admin.PutAsync($"{Teams}/{id}", """{"name":"Viewers","memberIds":[]}""");
            using var notMember = await admin.GetAsync($"/api/admin/users/{carol.Id}");
            using var withoutAccess = await carolClient.GetAsync("/api/sagas");

            Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);
            Assert.Equal(new[] { id }, (await ReadNodeAsync(member))["teamIds"]!.AsArray().Select(t => t!.GetValue<Guid>()));
            Assert.Equal(HttpStatusCode.OK, withAccess.StatusCode);
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
            Assert.Empty((await ReadNodeAsync(notMember))["teamIds"]!.AsArray());
            Assert.Equal(HttpStatusCode.Forbidden, withoutAccess.StatusCode);
        }
    }

    [Fact]
    public async Task ValidationErrors_AreKeyedByCamelCaseRequestPaths()
    {
        var (me, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            using var response = await admin.PostAsync(
                Teams,
                $$"""
                {"name":" ","description":"{{new string('x', 257)}}","memberIds":["{{me.Id}}","{{Guid.NewGuid()}}","{{me.Id}}"],
                 "grants":[{"roleId":"{{BuiltInRoles.ViewerId}}","allSagaTypes":false,"sagaTypes":["OrderSaga"," "]},{"roleId":"{{BuiltInRoles.ViewerId}}","allSagaTypes":true}]}
                """);

            Assert.Equal(
                ["description", "grants[0].sagaTypes", "grants[1].roleId", "memberIds[1]", "memberIds[2]", "name"],
                await AssertValidationErrorsAsync(response),
                StringComparer.Ordinal);
        }
    }

    [Theory]
    [InlineData("""{"name":"Ops","members":[]}""", "members")]
    [InlineData("""{"name":"Ops","memberIds":["not-a-guid"]}""", "memberIds[0]")]
    [InlineData("""{"name":"Ops","grants":[{"roleId":"a0000000-0000-0000-0000-000000000003","allSagaTypes":true,"scope":"all"}]}""", "grants[0].scope")]
    public async Task AnUnknownMemberOrAWrongValue_Is400_NamingItsPath(string body, string member)
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            using var response = await admin.PostAsync(Teams, body);

            Assert.Equal(new[] { member }, await AssertValidationErrorsAsync(response));
            using var list = await admin.GetAsync(Teams);
            Assert.Empty((await ReadNodeAsync(list)).AsArray());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABodyOverTheCap_Is400_NamingTheRequest_AndCreatesNothing(bool declaresItsLength)
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            var json = """{"name":"Ops"}""".PadRight(AdminEndpoints.MaxRequestBodyBytes + 1);

            using var response = await admin.PostContentAsync(Teams, SignInClient.Body(json, declaresItsLength));

            Assert.Equal(["request"], await AssertValidationErrorsAsync(response));
            using var list = await admin.GetAsync(Teams);
            Assert.Empty((await ReadNodeAsync(list)).AsArray());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABodyOfExactlyTheCap_IsRead(bool declaresItsLength)
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            var json = """{"name":"Ops"}""".PadRight(AdminEndpoints.MaxRequestBodyBytes);

            using var response = await admin.PostContentAsync(Teams, SignInClient.Body(json, declaresItsLength));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    [Fact]
    public async Task ANameTakenIgnoringCase_Is409NameTaken_OnCreateAndOnUpdate()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            await CreateTeamAsync(admin, """{"name":"Payments"}""");
            var other = await CreateTeamAsync(admin, """{"name":"Ops"}""");

            using var create = await admin.PostAsync(Teams, """{"name":" payments "}""");
            using var rename = await admin.PutAsync($"{Teams}/{other}", """{"name":"PAYMENTS"}""");

            await AssertProblemAsync(create, HttpStatusCode.Conflict, IdentityRuleCodes.NameTaken);
            await AssertProblemAsync(rename, HttpStatusCode.Conflict, IdentityRuleCodes.NameTaken);
        }
    }

    [Fact]
    public async Task ChangingTheTeamThatHoldsTheLastAdministratorsAccess_Is409LastAdministrator()
    {
        var admin = await CreateUserWithPasswordAsync(_host.Services, "admin", AdminPassword);
        var team = await AdminApi.CreateTeamAsync(_host.Services, "Admins", [admin.Id], AllTypes(BuiltInRoles.AdministratorId));
        using var client = await SignInExistingAsync(_host, "admin");

        using var empty = await client.PutAsync($"{Teams}/{team.Id}", """{"name":"Admins","grants":[{"roleId":"a0000000-0000-0000-0000-000000000001","allSagaTypes":true}]}""");
        using var demote = await client.PutAsync($"{Teams}/{team.Id}", $$"""{"name":"Admins","memberIds":["{{admin.Id}}"]}""");
        using var delete = await client.DeleteAsync($"{Teams}/{team.Id}");

        await AssertProblemAsync(empty, HttpStatusCode.Conflict, IdentityRuleCodes.LastAdministrator);
        await AssertProblemAsync(demote, HttpStatusCode.Conflict, IdentityRuleCodes.LastAdministrator);
        await AssertProblemAsync(delete, HttpStatusCode.Conflict, IdentityRuleCodes.LastAdministrator);
        using var still = await client.GetAsync($"{Teams}/{team.Id}");
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);
    }

    [Fact]
    public async Task Delete_Is204_TheTeamIsGone_AndItsMembersStay()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        var dave = await CreateUserAsync(_host.Services, "dave");
        using (admin)
        {
            var id = await CreateTeamAsync(admin, $$"""{"name":"Ops","memberIds":["{{dave.Id}}"]}""");

            using var delete = await admin.DeleteAsync($"{Teams}/{id}");
            using var read = await admin.GetAsync($"{Teams}/{id}");
            using var member = await admin.GetAsync($"/api/admin/users/{dave.Id}");

            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            await AssertProblemAsync(read, HttpStatusCode.NotFound, code: null);
            Assert.Equal(HttpStatusCode.OK, member.StatusCode);
            Assert.Empty((await ReadNodeAsync(member))["teamIds"]!.AsArray());
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
            var body = string.Equals(method, "PUT", StringComparison.Ordinal) ? """{"name":"Nobody"}""" : null;
            using var response = await admin.SendAsync(new HttpMethod(method), $"{Teams}/{Guid.NewGuid()}", body);

            var problem = await AssertProblemAsync(response, HttpStatusCode.NotFound, code: null);
            Assert.Contains("No team with id", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task EveryTeamChange_IsAudited_UnderTheAdministratorsName_AndARefusalToo()
    {
        var (_, admin) = await SignInAdministratorAsync(_host);
        using (admin)
        {
            var id = await CreateTeamAsync(admin, """{"name":"Ops"}""");
            (await admin.PutAsync($"{Teams}/{id}", """{"name":"Operations"}""")).Dispose();
            (await admin.DeleteAsync($"{Teams}/{id}")).Dispose();
            (await admin.PostAsync(Teams, """{"name":""}""")).Dispose();

            var changed = _logs.Audit(7100);
            foreach (var action in new[] { AccessActions.CreateTeam, AccessActions.UpdateTeam, AccessActions.DeleteTeam })
                Assert.Contains(changed, m => m.StartsWith($"Audit: dashboard:admin {action} team {id}", StringComparison.Ordinal));
            Assert.Contains(_logs.Audit(7101), m => m.StartsWith($"Audit: dashboard:admin {AccessActions.CreateTeam} team", StringComparison.Ordinal)
                && m.EndsWith("validation", StringComparison.Ordinal));
        }
    }

    [Fact]
    public Task OnlyAnUnscopedManager_GetsThrough_EveryTeamRoute()
    {
        var id = Guid.NewGuid();
        return AssertOnlyUnscopedManagersGetThroughAsync(
            _host,
            [
                (HttpMethod.Get, Teams),
                (HttpMethod.Post, Teams),
                (HttpMethod.Get, $"{Teams}/{id}"),
                (HttpMethod.Put, $"{Teams}/{id}"),
                (HttpMethod.Delete, $"{Teams}/{id}"),
            ]);
    }

    private static async Task<Guid> CreateTeamAsync(SignInClient admin, string body)
    {
        using var created = await admin.PostAsync(Teams, body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await ReadNodeAsync(created))["id"]!.GetValue<Guid>();
    }
}
