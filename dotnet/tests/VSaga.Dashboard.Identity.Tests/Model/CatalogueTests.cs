using VSaga.Dashboard.Identity.Model;

namespace VSaga.Dashboard.Identity.Tests.Model;

/// <summary>The permission catalogue and the built-in roles are a contract with stored data and the SPA.</summary>
public sealed class CatalogueTests
{
    [Fact]
    public void Permissions_AreTheFourKeysWithTheirLabelsScopeAndImplications()
    {
        Assert.Equal(
            [
                ("sagas.view", "View sagas", true, ""),
                ("sagas.data", "View saga data", true, "sagas.view"),
                ("sagas.retry", "Retry sagas", true, "sagas.view"),
                ("access.manage", "Manage access", false, ""),
            ],
            Permissions.All.Select(p => (p.Key, p.Name, p.Scopable, string.Join(',', p.Implies))));
        Assert.All(Permissions.All, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
    }

    [Fact]
    public void Permissions_FindIsOrdinal()
    {
        Assert.True(Permissions.IsKnown("sagas.view"));
        Assert.False(Permissions.IsKnown("SAGAS.VIEW"));
        Assert.False(Permissions.IsKnown("sagas.delete"));
        Assert.Null(Permissions.Find("access.manage "));
    }

    /// <summary>The ids are written into grants; changing one would orphan every grant of that role.</summary>
    [Fact]
    public void BuiltInRoles_HaveFixedIdsAndTheirPermissionSets()
    {
        Assert.Equal(new Guid("a0000000-0000-0000-0000-000000000001"), BuiltInRoles.Administrator.Id);
        Assert.Equal(new Guid("a0000000-0000-0000-0000-000000000002"), BuiltInRoles.Operator.Id);
        Assert.Equal(new Guid("a0000000-0000-0000-0000-000000000003"), BuiltInRoles.Viewer.Id);

        Assert.Equal(["sagas.view", "sagas.data", "sagas.retry", "access.manage"], BuiltInRoles.Administrator.Permissions);
        Assert.Equal(["sagas.view", "sagas.data", "sagas.retry"], BuiltInRoles.Operator.Permissions);
        Assert.Equal(["sagas.view", "sagas.data"], BuiltInRoles.Viewer.Permissions);
        Assert.All(BuiltInRoles.All, r => Assert.True(r.IsBuiltIn));
        Assert.All(BuiltInRoles.All, r => Assert.All(r.Permissions, p => Assert.True(Permissions.IsKnown(p))));
    }

    [Fact]
    public void BuiltInRoles_FindByIdAndByNameIgnoringCase()
    {
        Assert.Same(BuiltInRoles.Operator, BuiltInRoles.Find(BuiltInRoles.OperatorId));
        Assert.Same(BuiltInRoles.Viewer, BuiltInRoles.FindByName(" viewer "));
        Assert.Null(BuiltInRoles.Find(Guid.NewGuid()));
        Assert.Null(BuiltInRoles.FindByName("Auditor"));
    }

    [Fact]
    public void Normalize_TrimsAndUpperCasesInvariantly()
    {
        Assert.Equal("ALICE.SMITH@EXAMPLE", IdentityNames.Normalize("  alice.Smith@example "));
        Assert.Equal("ÜBER", IdentityNames.Normalize("über"));
    }
}
