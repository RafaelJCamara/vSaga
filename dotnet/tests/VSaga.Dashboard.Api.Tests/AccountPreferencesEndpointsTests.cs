using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VSaga.Dashboard.Api.Auth;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Stores;
using static VSaga.Dashboard.Api.Tests.AdminApi;
using static VSaga.Dashboard.Api.Tests.TestSessions;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// The account's display choices (ADR 0015, part 4): <c>PUT /api/auth/preferences</c> stores them, the session
/// reports them, the API key has none, and a value the dashboard does not offer is refused.
/// </summary>
public sealed class AccountPreferencesEndpointsTests : IAsyncLifetime, IAsyncDisposable
{
    private const string Password = "correct horse battery";

    private readonly DashboardApiFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    // xunit 2 calls IAsyncLifetime.DisposeAsync, never a test class's IAsyncDisposable.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Session_OfAUserWithNoChoice_HasNullPreferences_AndAnAnonymousOneToo()
    {
        await CreateUserWithPasswordAsync(_factory.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        using var client = await SignInClient.StartAsync(_factory);

        Assert.Equal(JsonValueKind.Null, (await client.SessionAsync()).GetProperty("preferences").ValueKind);
        using var login = await client.LoginAsync("alice", Password);

        Assert.Equal(JsonValueKind.Null, (await client.SessionAsync()).GetProperty("preferences").ValueKind);
    }

    [Fact]
    public async Task Put_StoresTheChoices_AnswersThem_AndTheSessionReportsThem()
    {
        var alice = await CreateUserWithPasswordAsync(_factory.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        using var client = await SignInClient.StartAsync(_factory);
        using var login = await client.LoginAsync("alice", Password);

        using var response = await client.PutAsync("/api/auth/preferences", """{"theme":"high-contrast","density":"compact"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = await SignInClient.ReadJsonAsync(response);
        Assert.Equal("high-contrast", answer.GetProperty("theme").GetString());
        Assert.Equal("compact", answer.GetProperty("density").GetString());
        var preferences = (await client.SessionAsync()).GetProperty("preferences");
        Assert.Equal("high-contrast", preferences.GetProperty("theme").GetString());
        Assert.Equal("compact", preferences.GetProperty("density").GetString());
        Assert.Equal("""{"theme":"high-contrast","density":"compact"}""", (await StoredAsync(alice.Id))!.DisplayPreferences);
    }

    [Fact]
    public async Task Put_ReplacesWhatWasStored_AndABodyWithNeitherMemberClearsIt()
    {
        var alice = await CreateUserWithPasswordAsync(_factory.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        using var client = await SignInClient.StartAsync(_factory);
        using var login = await client.LoginAsync("alice", Password);
        using var first = await client.PutAsync("/api/auth/preferences", """{"theme":"dark","density":"compact"}""");

        using var second = await client.PutAsync("/api/auth/preferences", """{"theme":"system"}""");

        var preferences = (await client.SessionAsync()).GetProperty("preferences");
        Assert.Equal("system", preferences.GetProperty("theme").GetString());
        Assert.Equal(JsonValueKind.Null, preferences.GetProperty("density").ValueKind);
        Assert.Equal("""{"theme":"system"}""", (await StoredAsync(alice.Id))!.DisplayPreferences);

        using var cleared = await client.PutAsync("/api/auth/preferences", "{}");

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Null((await StoredAsync(alice.Id))!.DisplayPreferences);
        Assert.Equal(JsonValueKind.Null, (await client.SessionAsync()).GetProperty("preferences").ValueKind);
    }

    [Fact]
    public async Task Put_ChangesNothingElseAboutTheAccount_AndTheSessionStands()
    {
        var alice = await CreateUserWithPasswordAsync(_factory.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        using var client = await SignInClient.StartAsync(_factory);
        using var login = await client.LoginAsync("alice", Password);
        var before = (await StoredAsync(alice.Id))!;

        using var response = await client.PutAsync("/api/auth/preferences", """{"theme":"dark"}""");

        var after = (await StoredAsync(alice.Id))!;
        Assert.Equal(before.SecurityStamp, after.SecurityStamp);
        Assert.Equal(before.UpdatedAtUtc, after.UpdatedAtUtc);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.Null(SignInClient.SetCookieHeader(response, CookieName(_factory.Services)));
        Assert.True((await client.SessionAsync()).GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task TheChoices_FollowTheAccountToAnotherBrowser()
    {
        await CreateUserWithPasswordAsync(_factory.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        using var here = await SignInClient.StartAsync(_factory);
        using var hereLogin = await here.LoginAsync("alice", Password);
        using var put = await here.PutAsync("/api/auth/preferences", """{"theme":"light","density":"compact"}""");

        using var there = await SignInClient.StartAsync(_factory);
        using var thereLogin = await there.LoginAsync("alice", Password);

        var preferences = (await there.SessionAsync()).GetProperty("preferences");
        Assert.Equal("light", preferences.GetProperty("theme").GetString());
        Assert.Equal("compact", preferences.GetProperty("density").GetString());
    }

    [Fact]
    public async Task TheChoices_BelongToOneAccount()
    {
        await CreateUserWithPasswordAsync(_factory.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        await CreateUserWithPasswordAsync(_factory.Services, "bob", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        using var alice = await SignInClient.StartAsync(_factory);
        using var aliceLogin = await alice.LoginAsync("alice", Password);
        using var put = await alice.PutAsync("/api/auth/preferences", """{"theme":"dark"}""");

        using var bob = await SignInClient.StartAsync(_factory);
        using var bobLogin = await bob.LoginAsync("bob", Password);

        Assert.Equal(JsonValueKind.Null, (await bob.SessionAsync()).GetProperty("preferences").ValueKind);
    }

    [Theory]
    [InlineData("""{"theme":"sepia"}""", "theme")]
    [InlineData("""{"density":"roomy"}""", "density")]
    [InlineData("""{"theme":"DARK"}""", "theme")]
    [InlineData("""{"theme":"","density":""}""", "density,theme")]
    public async Task Put_AValueTheDashboardDoesNotOffer_Is400NamingTheMember_AndStoresNothing(string body, string members)
    {
        var alice = await CreateUserWithPasswordAsync(_factory.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        using var client = await SignInClient.StartAsync(_factory);
        using var login = await client.LoginAsync("alice", Password);

        using var response = await client.PutAsync("/api/auth/preferences", body);

        Assert.Equal(members.Split(','), await AssertValidationErrorsAsync(response));
        Assert.Null((await StoredAsync(alice.Id))!.DisplayPreferences);
    }

    [Theory]
    [InlineData("""{"theme":"dark","fontSize":"large"}""", "fontSize")]
    [InlineData("""{"theme":"dark","theme":"light"}""", "theme")]
    [InlineData("""{"theme":7}""", "theme")]
    [InlineData("""["dark"]""", "request")]
    [InlineData("not json", "request")]
    public async Task Put_ABodyThatIsNotThisRequest_Is400Validation(string body, string member)
    {
        await CreateUserWithPasswordAsync(_factory.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        using var client = await SignInClient.StartAsync(_factory);
        using var login = await client.LoginAsync("alice", Password);

        using var response = await client.PutAsync("/api/auth/preferences", body);

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, AuthProblems.ValidationCode);
        Assert.True(problem.GetProperty("errors").TryGetProperty(member, out _), problem.ToString());
    }

    [Fact]
    public async Task Put_Anonymous_Is401()
    {
        using var client = await SignInClient.StartAsync(_factory);

        using var response = await client.PutAsync("/api/auth/preferences", """{"theme":"dark"}""");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Put_WithoutAnAntiforgeryToken_Is400Antiforgery_AndStoresNothing()
    {
        var alice = await CreateUserWithPasswordAsync(_factory.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        using var client = await SignInClient.StartAsync(_factory);
        using var login = await client.LoginAsync("alice", Password);

        using var response = await client.SendAsync(HttpMethod.Put, "/api/auth/preferences", """{"theme":"dark"}""", token: "");

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, AuthProblems.AntiforgeryCode);
        Assert.Null((await StoredAsync(alice.Id))!.DisplayPreferences);
    }

    [Fact]
    public async Task Put_WithTheApiKey_Is403_AndItsSessionHasNoPreferences()
    {
        using var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, DashboardApiFactory.TestApiKey);

        using var response = await http.PutAsync(
            "/api/auth/preferences", new StringContent("""{"theme":"dark"}""", System.Text.Encoding.UTF8, "application/json"));
        using var session = await http.GetAsync("/api/auth/session");

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, AuthProblems.ForbiddenCode);
        var body = await SignInClient.ReadJsonAsync(session);
        Assert.True(body.GetProperty("authenticated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("preferences").ValueKind);
    }

    [Fact]
    public async Task APasswordChange_KeepsTheChoices()
    {
        await CreateUserWithPasswordAsync(_factory.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        using var client = await SignInClient.StartAsync(_factory);
        using var login = await client.LoginAsync("alice", Password);
        using var put = await client.PutAsync("/api/auth/preferences", """{"theme":"dark","density":"compact"}""");

        using var change = await client.ChangePasswordAsync(Password, "staple battery horse");

        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        var preferences = (await client.SessionAsync()).GetProperty("preferences");
        Assert.Equal("dark", preferences.GetProperty("theme").GetString());
        Assert.Equal("compact", preferences.GetProperty("density").GetString());
    }

    [Fact]
    public async Task Session_ReadsTolerantly_WhatANewerImageWrote()
    {
        var alice = await CreateUserWithPasswordAsync(_factory.Services, "alice", Password, grants: AllTypes(BuiltInRoles.ViewerId));
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDashboardIdentityStore>().SetDisplayPreferencesAsync(
                alice.Id, """{"theme":"dark","density":"spacious","fontSize":"large"}""", CancellationToken.None);
        }

        using var client = await SignInClient.StartAsync(_factory);
        using var login = await client.LoginAsync("alice", Password);

        var preferences = (await client.SessionAsync()).GetProperty("preferences");
        Assert.Equal("dark", preferences.GetProperty("theme").GetString());
        Assert.Equal(JsonValueKind.Null, preferences.GetProperty("density").ValueKind);
        Assert.False(preferences.TryGetProperty("fontSize", out _));
    }

    private async Task<DashboardUser?> StoredAsync(Guid id)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDashboardIdentityStore>().FindUserAsync(id, CancellationToken.None);
    }
}
