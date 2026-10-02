using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;
using static VSaga.Dashboard.Identity.Tests.Services.ServiceTestContext;

namespace VSaga.Dashboard.Identity.Tests.Services;

public sealed class AccessEvaluatorTests
{
    private static readonly DashboardRole RetryOnly = new(Guid.NewGuid(), "Retry only", null, IsBuiltIn: false, [Permissions.SagasRetry]);

    private static readonly DashboardRole ManageOnly = new(Guid.NewGuid(), "Manage only", null, IsBuiltIn: false, [Permissions.AccessManage]);

    private static readonly IReadOnlyList<DashboardRole> Roles = [.. BuiltInRoles.All, RetryOnly, ManageOnly];

    [Fact]
    public void Evaluate_UnscopedAdministrator_HoldsEverythingEverywhere()
    {
        var access = AccessEvaluator.Evaluate(User(AllTypes(BuiltInRoles.AdministratorId)), [], Roles);

        foreach (var permission in Permissions.All)
            Assert.True(access.HasUnscoped(permission.Key), permission.Key);
        Assert.True(access.Has(Permissions.SagasRetry, "AnyTypeAtAll"));
        Assert.Equal([Permissions.SagasView, Permissions.SagasData, Permissions.SagasRetry, Permissions.AccessManage], access.Permissions);
    }

    [Fact]
    public void Evaluate_ScopedGrant_HoldsOnlyTheNamedTypesComparedOrdinally()
    {
        var access = AccessEvaluator.Evaluate(User(Named(BuiltInRoles.ViewerId, "OrderSaga")), [], Roles);

        Assert.True(access.Has(Permissions.SagasData, "OrderSaga"));
        Assert.False(access.Has(Permissions.SagasData, "orderSaga"));
        Assert.False(access.Has(Permissions.SagasData, "PaymentSaga"));
        Assert.True(access.HasAny(Permissions.SagasView));
        Assert.False(access.HasUnscoped(Permissions.SagasView));
        Assert.False(access.HasAny(Permissions.SagasRetry));
        Assert.Equal(["OrderSaga"], access.ScopeFor(Permissions.SagasView).SagaTypes, StringComparer.Ordinal);
    }

    [Fact]
    public void Evaluate_ImpliesViewForTheSameScope()
    {
        var access = AccessEvaluator.Evaluate(User(Named(RetryOnly.Id, "OrderSaga")), [], Roles);

        Assert.True(access.Has(Permissions.SagasRetry, "OrderSaga"));
        Assert.True(access.Has(Permissions.SagasView, "OrderSaga"));
        Assert.False(access.Has(Permissions.SagasView, "PaymentSaga"));
        Assert.False(access.HasAny(Permissions.SagasData));
    }

    [Fact]
    public void Evaluate_IgnoresAccessManageInAScopedGrant()
    {
        var scopedAdministrator = AccessEvaluator.Evaluate(User(Named(BuiltInRoles.AdministratorId, "OrderSaga")), [], Roles);
        var scopedManageOnly = AccessEvaluator.Evaluate(User(Named(ManageOnly.Id, "OrderSaga")), [], Roles);

        Assert.False(scopedAdministrator.HasAny(Permissions.AccessManage));
        Assert.False(scopedAdministrator.Has(Permissions.AccessManage, "OrderSaga"));
        Assert.True(scopedAdministrator.Has(Permissions.SagasRetry, "OrderSaga"));
        Assert.False(scopedManageOnly.HasAny(Permissions.AccessManage));
        Assert.Empty(scopedManageOnly.Permissions);
    }

    [Fact]
    public void Evaluate_IsTheUnionOfTheUserAndTheirTeams()
    {
        var user = User(Named(BuiltInRoles.ViewerId, "OrderSaga"));
        var team = Team([user.Id], Named(BuiltInRoles.OperatorId, "PaymentSaga"), Named(BuiltInRoles.ViewerId, "ShippingSaga"));

        var access = AccessEvaluator.Evaluate(user, [team], Roles);

        Assert.Equal(["OrderSaga", "PaymentSaga", "ShippingSaga"], access.ScopeFor(Permissions.SagasData).SagaTypes.Order(StringComparer.Ordinal), StringComparer.Ordinal);
        Assert.Equal(["PaymentSaga"], access.ScopeFor(Permissions.SagasRetry).SagaTypes, StringComparer.Ordinal);
    }

    [Fact]
    public void Evaluate_AnUnscopedGrantWinsOverScopedOnesForTheSamePermission()
    {
        var user = User(Named(BuiltInRoles.OperatorId, "OrderSaga"));
        var team = Team([user.Id], AllTypes(BuiltInRoles.ViewerId));

        var access = AccessEvaluator.Evaluate(user, [team], Roles);

        Assert.True(access.HasUnscoped(Permissions.SagasData));
        Assert.True(access.ScopeFor(Permissions.SagasData).IsAll);
        Assert.False(access.HasUnscoped(Permissions.SagasRetry));
        Assert.True(access.Has(Permissions.SagasRetry, "OrderSaga"));
    }

    [Fact]
    public void Evaluate_IgnoresTeamsTheUserIsNotIn()
    {
        var user = User();
        var otherTeam = Team([Guid.NewGuid()], AllTypes(BuiltInRoles.AdministratorId));

        var access = AccessEvaluator.Evaluate(user, [otherTeam], Roles);

        Assert.Empty(access.Permissions);
    }

    [Fact]
    public void Evaluate_ADisabledUser_HoldsNothing()
    {
        var user = User(AllTypes(BuiltInRoles.AdministratorId)) with { IsEnabled = false };
        var team = Team([user.Id], AllTypes(BuiltInRoles.ViewerId));

        var access = AccessEvaluator.Evaluate(user, [team], Roles);

        Assert.Empty(access.Permissions);
        Assert.False(access.HasAny(Permissions.SagasView));
    }

    [Fact]
    public void Evaluate_AUserWhoMustChangeTheirPassword_HoldsNothing()
    {
        var user = User(AllTypes(BuiltInRoles.AdministratorId)) with { MustChangePassword = true };
        var team = Team([user.Id], AllTypes(BuiltInRoles.ViewerId));

        Assert.Empty(AccessEvaluator.Evaluate(user, [team], Roles).Permissions);
    }

    [Fact]
    public void Evaluate_IgnoresUnknownRolesAndUnknownPermissionKeys()
    {
        var odd = new DashboardRole(Guid.NewGuid(), "Odd", null, IsBuiltIn: false, ["sagas.delete", Permissions.SagasView]);
        var user = User(AllTypes(Guid.NewGuid()), Named(odd.Id, "OrderSaga"));

        var access = AccessEvaluator.Evaluate(user, [], [odd]);

        Assert.Equal([Permissions.SagasView], access.Permissions);
        Assert.False(access.HasAny("sagas.delete"));
    }

    [Fact]
    public void EvaluateGrants_ANamedGrantWithNoTypes_ConfersNothing()
    {
        var access = AccessEvaluator.EvaluateGrants([new AccessGrant(BuiltInRoles.ViewerId, AllSagaTypes: false, [])], Roles);

        Assert.Empty(access.Permissions);
        Assert.True(access.ScopeFor(Permissions.SagasView).IsEmpty);
    }

    private static DashboardUser User(params AccessGrant[] grants) => new(
        Guid.NewGuid(), "someone", "Someone", "hash", "stamp", IsEnabled: true, MustChangePassword: false,
        FailedSignInCount: 0, LockoutEndUtc: null, LastSignInAtUtc: null, T0, T0, grants);

    private static DashboardTeam Team(IReadOnlyList<Guid> memberIds, params AccessGrant[] grants) =>
        new(Guid.NewGuid(), "team", null, memberIds, grants);
}
