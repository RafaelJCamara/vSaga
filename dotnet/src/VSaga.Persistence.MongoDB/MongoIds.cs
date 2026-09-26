using System.Globalization;

namespace VSaga.Persistence.MongoDB;

/// <summary>
/// Document ids. Every <c>_id</c> is a scalar string, never a subdocument: a composite BSON subdocument
/// compares by field order and byte equality, so a reordered class map would make every lookup miss and
/// every insert create a second document instead of colliding -- the one schema change that could never
/// be migrated in place. The encodings are injective: a GUID is fixed-width so it goes first, and any
/// other user-supplied component that is not last carries its own length.
/// </summary>
internal static class MongoIds
{
    /// <summary>A saga instance and its sequence counter: <c>{correlationId:D}|{sagaType}</c>.</summary>
    public static string Instance(Guid correlationId, string sagaType) => $"{correlationId:D}|{sagaType}";

    /// <summary>The instance id's two halves. The GUID is 36 characters, so the separator is at a fixed position whatever the saga type contains.</summary>
    public static (Guid CorrelationId, string SagaType) ParseInstance(string id) =>
        (Guid.ParseExact(id.AsSpan(0, 36), "D"), id[37..]);

    /// <summary>A consumer registration: <c>{len}:{serviceName}|{messageType}</c>.</summary>
    public static string Registration(string serviceName, string messageType) =>
        string.Create(CultureInfo.InvariantCulture, $"{serviceName.Length}:{serviceName}|{messageType}");

    /// <summary>The correlation id's stored text: the lower-case <c>D</c> format, which is what <c>Search</c> matches.</summary>
    public static string CorrelationText(Guid correlationId) => correlationId.ToString("D");
}
