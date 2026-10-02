using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using VSaga.Dashboard.Identity.Services;
using VSaga.Dashboard.Identity.Stores;
using VSaga.Dashboard.Identity.Tests.EFCore;

namespace VSaga.Dashboard.Identity.Tests.Services;

public sealed class IdentityStoreXmlRepositoryTests : IDisposable
{
    private readonly IdentityDatabaseFile _file = new();

    public void Dispose() => _file.Dispose();

    [Fact]
    public async Task BeforeTheStoreIsReady_ReadingAndWritingThrow_SoNoKeyIsMintedInMemory()
    {
        await using var services = _file.BuildServices();
        var repository = services.GetRequiredService<IdentityStoreXmlRepository>();

        Assert.Throws<IdentityUnavailableException>(repository.GetAllElements);
        Assert.Throws<IdentityUnavailableException>(() => repository.StoreElement(new XElement("key"), "k1"));

        // Through the framework too: protecting needs a key, and none may be created while the store is down.
        var protector = services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
        Assert.ThrowsAny<Exception>(() => protector.Protect("payload"));
        Assert.False(File.Exists(_file.DatabasePath));
    }

    [Fact]
    public async Task OnceReady_StoresAndReadsTheElementsInOrder()
    {
        await using var services = _file.BuildServices();
        Assert.True(await services.GetRequiredService<IdentityStartup>().EnsureReadyAsync(CancellationToken.None));
        var repository = services.GetRequiredService<IdentityStoreXmlRepository>();

        repository.StoreElement(new XElement("key", new XAttribute("id", "1"), new XElement("secret", "a")), "key-1");
        repository.StoreElement(new XElement("key", new XAttribute("id", "2")), "key-2");

        var elements = repository.GetAllElements();
        Assert.Equal(["1", "2"], elements.Select(e => (string)e.Attribute("id")!), StringComparer.Ordinal);
        Assert.Equal("a", (string)elements.First().Element("secret")!);
    }

    [Fact]
    public async Task ARestartedApi_ReadsWhatTheLastOneProtected()
    {
        string protectedPayload;
        await using (var first = _file.BuildServices())
        {
            Assert.True(await first.GetRequiredService<IdentityStartup>().EnsureReadyAsync(CancellationToken.None));
            protectedPayload = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("session").Protect("signed-in");
        }

        await using var restarted = _file.BuildServices();
        Assert.True(await restarted.GetRequiredService<IdentityStartup>().EnsureReadyAsync(CancellationToken.None));

        var unprotected = restarted.GetRequiredService<IDataProtectionProvider>().CreateProtector("session").Unprotect(protectedPayload);
        Assert.Equal("signed-in", unprotected);
    }
}
