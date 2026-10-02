using System.Security.Claims;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;
using VSaga.Dashboard.Identity.Stores;
using static VSaga.Dashboard.Identity.Tests.Services.ServiceTestContext;

namespace VSaga.Dashboard.Identity.Tests.Services;

public sealed class CallerAccessResolverTests : IAsyncLifetime
{
    private ServiceTestContext _context = null!;

    public async Task InitializeAsync() => _context = await CreateAsync();

    public async Task DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task User_WithTheCurrentStamp_ResolvesTheirOwnAndTheirTeamsGrants()
    {
        var alice = await _context.SeedUserAsync("alice", grants: Named(BuiltInRoles.ViewerId, "OrderSaga"));
        await _context.SeedTeamAsync("Payments", [alice.Id], Named(BuiltInRoles.OperatorId, "PaymentSaga"));

        var caller = await Resolver().ResolveAsync(DashboardClaims.ForUser(alice, "Cookie"), None);

        Assert.NotNull(caller);
        Assert.Equal(CallerKind.User, caller.Kind);
        Assert.Equal(alice.Id, caller.UserId);
        Assert.Equal("alice", caller.Username);
        Assert.Equal("dashboard:alice", caller.AuditActor);
        Assert.True(caller.Access.Has(Permissions.SagasData, "OrderSaga"));
        Assert.True(caller.Access.Has(Permissions.SagasRetry, "PaymentSaga"));
        Assert.False(caller.Access.Has(Permissions.SagasRetry, "OrderSaga"));
    }

    [Fact]
    public async Task User_WhoseStampRotated_IsNotResolved()
    {
        var alice = await _context.SeedUserAsync("alice", grants: AllTypes(BuiltInRoles.ViewerId));

        var caller = await Resolver().ResolveAsync(DashboardClaims.ForUser(alice with { SecurityStamp = SecurityStamps.New() }, "Cookie"), None);

        Assert.Null(caller);
    }

    [Fact]
    public async Task User_WithoutAStampClaim_IsNotResolved()
    {
        var alice = await _context.SeedUserAsync("alice", grants: AllTypes(BuiltInRoles.ViewerId));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(DashboardClaims.Subject, alice.Id.ToString())], "Cookie"));

        Assert.Null(await Resolver().ResolveAsync(principal, None));
    }

    [Fact]
    public async Task User_DisabledOrDeleted_IsNotResolved()
    {
        var disabled = await _context.SeedUserAsync("disabled", isEnabled: false, grants: AllTypes(BuiltInRoles.ViewerId));
        var deleted = await _context.SeedUserAsync("deleted", grants: AllTypes(BuiltInRoles.ViewerId));
        await _context.NewStore().DeleteUserAsync(deleted.Id, None);

        Assert.Null(await Resolver().ResolveAsync(DashboardClaims.ForUser(disabled, "Cookie"), None));
        Assert.Null(await Resolver().ResolveAsync(DashboardClaims.ForUser(deleted, "Cookie"), None));
    }

    [Fact]
    public async Task User_WhoMustChangeTheirPassword_IsResolvedWithNoAccess()
    {
        var alice = await _context.SeedUserAsync("alice", mustChangePassword: true, grants: AllTypes(BuiltInRoles.AdministratorId));

        var caller = await Resolver().ResolveAsync(DashboardClaims.ForUser(alice, "Cookie"), None);

        Assert.NotNull(caller);
        Assert.True(caller.MustChangePassword);
        Assert.Empty(caller.Access.Permissions);
    }

    [Fact]
    public async Task User_WhileTheStoreIsNotReady_IsNotResolvedAndTheStoreIsNotTouched()
    {
        var alice = await _context.SeedUserAsync("alice", grants: AllTypes(BuiltInRoles.ViewerId));
        var resolver = new CallerAccessResolver(new ThrowingServices(), new Readiness(false), DashboardSecuritySettings.Default);

        Assert.Null(await resolver.ResolveAsync(DashboardClaims.ForUser(alice, "Cookie"), None));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public async Task APrincipalWithoutAUserIdSubject_IsNotResolved(string subject)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(DashboardClaims.Subject, subject)], "Cookie"));

        Assert.Null(await Resolver().ResolveAsync(principal, None));
        Assert.Null(await Resolver().ResolveAsync(new ClaimsPrincipal(new ClaimsIdentity()), None));
    }

    [Fact]
    public async Task ApiKey_ByDefault_IsAViewerForEverySagaType()
    {
        var caller = await Resolver().ResolveAsync(DashboardClaims.ForApiKey(), None);

        Assert.NotNull(caller);
        Assert.Equal(CallerKind.ApiKey, caller.Kind);
        Assert.Null(caller.UserId);
        Assert.Equal("dashboard:api-key", caller.AuditActor);
        Assert.Equal([Permissions.SagasView, Permissions.SagasData], caller.Access.Permissions);
        Assert.True(caller.Access.HasUnscoped(Permissions.SagasData));
    }

    [Fact]
    public async Task ApiKey_AsAdministrator_NeverHoldsAccessManage()
    {
        var caller = await Resolver(ApiKeyRole("Administrator")).ResolveAsync(DashboardClaims.ForApiKey(), None);

        Assert.NotNull(caller);
        Assert.Equal([Permissions.SagasView, Permissions.SagasData, Permissions.SagasRetry], caller.Access.Permissions);
        Assert.False(caller.Access.HasAny(Permissions.AccessManage));
    }

    [Fact]
    public async Task ApiKey_NamingACustomRole_ReadsItFromTheStoreIgnoringCase()
    {
        await _context.SeedRoleAsync("Auditors", Permissions.SagasView, Permissions.AccessManage);

        var caller = await Resolver(ApiKeyRole("auditors")).ResolveAsync(DashboardClaims.ForApiKey(), None);

        Assert.NotNull(caller);
        Assert.Equal([Permissions.SagasView], caller.Access.Permissions);
        Assert.True(caller.Access.HasUnscoped(Permissions.SagasView));
    }

    [Fact]
    public async Task ApiKey_NamingNoRole_IsNotResolved()
    {
        Assert.Null(await Resolver(ApiKeyRole("Nobody")).ResolveAsync(DashboardClaims.ForApiKey(), None));
    }

    [Fact]
    public async Task ApiKey_WhileTheStoreIsNotReady_ResolvesABuiltInRoleFromCodeButNoCustomRole()
    {
        await _context.SeedRoleAsync("Auditors", Permissions.SagasView);
        var notReady = new Readiness(false);

        var builtIn = await new CallerAccessResolver(new ThrowingServices(), notReady, ApiKeyRole("Operator"))
            .ResolveAsync(DashboardClaims.ForApiKey(), None);
        var custom = await new CallerAccessResolver(new ThrowingServices(), notReady, ApiKeyRole("Auditors"))
            .ResolveAsync(DashboardClaims.ForApiKey(), None);

        Assert.NotNull(builtIn);
        Assert.True(builtIn.Access.HasUnscoped(Permissions.SagasRetry));
        Assert.Null(custom);
    }

    [Fact]
    public async Task TheApiKeySubject_FromAnythingButTheApiKeyScheme_IsNotResolved()
    {
        var forged = new ClaimsPrincipal(new ClaimsIdentity([new Claim(DashboardClaims.Subject, CallerAccess.ApiKeyUsername)], "Cookie"));

        Assert.Null(await Resolver().ResolveAsync(forged, None));
    }

    private static DashboardSecuritySettings ApiKeyRole(string role) => DashboardSecuritySettings.Default with { ApiKeyRole = role };

    private CallerAccessResolver Resolver(DashboardSecuritySettings? settings = null) =>
        new(new StoreServices(_context), new Readiness(true), settings ?? DashboardSecuritySettings.Default);

    private sealed record Readiness(bool IsReady) : IIdentityReadiness;

    /// <summary>A container holding only a fresh store.</summary>
    private sealed class StoreServices(ServiceTestContext context) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IDashboardIdentityStore) ? context.NewStore() : null;
    }

    /// <summary>A container that must not be asked for anything: building the store fails with no usable path.</summary>
    private sealed class ThrowingServices : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            throw new IdentityUnavailableException($"{serviceType.Name} was requested while the store is not ready.");
    }
}
