using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using VSaga.Dashboard.Identity.Stores;

namespace VSaga.Dashboard.Identity.Services;

/// <summary>
/// Keeps the Data Protection key ring in the identity store, so a recreated API container still reads the
/// session cookies it issued. A singleton, as the key manager holds it; each call runs on a DI scope, and so a
/// database context, of its own, never inside a request's exclusive identity write scope.
/// </summary>
/// <remarks>
/// While the store is not ready every call throws <see cref="IdentityUnavailableException"/>. Answering with
/// an empty ring instead would make the key manager mint a fresh key in memory, issue cookies that die with
/// the process, and reject every cookie signed with the stored ring once the store came back.
/// </remarks>
public sealed class IdentityStoreXmlRepository(IServiceScopeFactory scopes, IdentityStartup startup) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        ThrowIfNotReady();
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDashboardKeyRingStore>();
        return store.Load().Select(entry => XElement.Parse(entry.Xml)).ToList().AsReadOnly();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        ThrowIfNotReady();
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDashboardKeyRingStore>();
        store.Save(new KeyRingEntry(friendlyName, element.ToString(SaveOptions.DisableFormatting)));
    }

    private void ThrowIfNotReady()
    {
        if (!startup.IsReady)
            throw new IdentityUnavailableException($"The Data Protection key ring is in the identity store, which is not ready: {startup.FailureReason}");
    }
}
