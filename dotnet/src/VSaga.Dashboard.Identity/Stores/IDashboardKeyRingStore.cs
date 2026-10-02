namespace VSaga.Dashboard.Identity.Stores;

/// <summary>One Data Protection key, as the framework's <c>IXmlRepository</c> hands it over.</summary>
/// <param name="FriendlyName">The name the key manager gives the element, or null.</param>
/// <param name="Xml">The serialised key element.</param>
public sealed record KeyRingEntry(string? FriendlyName, string Xml);

/// <summary>
/// Where the dashboard's Data Protection key ring lives, so a recreated API keeps its sessions. Synchronous
/// because <c>IXmlRepository</c>, its only caller, is. Scoped, and called on its own unit of work: never from
/// inside an exclusive identity write scope.
/// </summary>
public interface IDashboardKeyRingStore
{
    /// <summary>Every stored key, oldest first.</summary>
    IReadOnlyList<KeyRingEntry> Load();

    /// <summary>Appends a key. Keys are never updated or removed; the key manager expires them by date.</summary>
    void Save(KeyRingEntry entry);
}
