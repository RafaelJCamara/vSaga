using VSaga.Dashboard.Identity.Tests.Stores;

namespace VSaga.Dashboard.Identity.Tests.EFCore;

/// <summary>The store contract against the EF Core store on SQLite.</summary>
public sealed class SqliteDashboardIdentityStoreTests : DashboardIdentityStoreContractTests
{
    protected override async Task<IIdentityStoreHarness> CreateHarnessAsync() => await SqliteIdentityStoreHarness.CreateAsync();
}
