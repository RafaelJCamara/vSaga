using VSaga.Dashboard.Identity.Stores;

namespace VSaga.Dashboard.Identity.Tests.Stores;

/// <summary>One empty identity database for one test case, and as many units of work over it as the case needs.</summary>
public interface IIdentityStoreHarness : IAsyncDisposable
{
    /// <summary>
    /// A new store instance with its own unit of work (on a relational store, its own connection), as a
    /// separate request would have. Safe to call from any thread; each instance is used by one caller.
    /// </summary>
    IDashboardIdentityStore CreateStore();

    /// <summary>A new key-ring store on its own unit of work over the same database.</summary>
    IDashboardKeyRingStore CreateKeyRingStore();
}
