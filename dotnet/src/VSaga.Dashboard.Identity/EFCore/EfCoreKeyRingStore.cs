using Microsoft.EntityFrameworkCore;
using VSaga.Dashboard.Identity.Stores;

namespace VSaga.Dashboard.Identity.EFCore;

/// <summary>
/// <see cref="IDashboardKeyRingStore"/> over the identity database's <c>DataProtectionKeys</c> table. It
/// must run on a context of its own: a key can be created while a request holds an exclusive identity
/// write scope, and on SQLite a second writer on another connection would wait for that scope to end.
/// </summary>
public sealed class EfCoreKeyRingStore(DashboardIdentityDbContext db) : IDashboardKeyRingStore
{
    public IReadOnlyList<KeyRingEntry> Load() =>
        db.DataProtectionKeys
            .AsNoTracking()
            .OrderBy(k => k.Id)
            .Select(k => new KeyRingEntry(k.FriendlyName, k.Xml))
            .ToList();

    public void Save(KeyRingEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        db.DataProtectionKeys.Add(new DataProtectionKeyEntity { FriendlyName = entry.FriendlyName, Xml = entry.Xml });
        try
        {
            db.SaveChanges();
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }
}
