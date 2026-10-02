using System.Globalization;
using System.Text;

namespace VSaga.Abstractions.Persistence;

/// <summary>
/// The single definition of a <see cref="SagaEntryType.StatePersisted"/> entry and of its size markers. The
/// engine (after each committed transition) and the dashboard (after a retry reset) both build the entry
/// here, so the shape and the marker text cannot drift between them.
/// </summary>
public static class SagaStateSnapshot
{
    /// <summary>The default per-snapshot cap, in UTF-8 bytes of the state JSON (256 KiB).</summary>
    public const int DefaultMaxBytes = 262_144;

    /// <summary>
    /// Builds the entry for a state the snapshot store just wrote. <paramref name="stateJson"/> is that
    /// blob; it is recorded as is, or as the size marker when it exceeds <paramref name="maxBytes"/>.
    /// <paramref name="messageType"/>/<paramref name="messageId"/> name the inbound message whose step the
    /// snapshot follows, and stay null after a timeout or a dashboard reset.
    /// </summary>
    public static SagaLogEntry CreateEntry(Guid correlationId, string sagaType, string stateJson, int maxBytes,
        string? messageType = null, string? messageId = null) =>
        SagaLogEntry.Create(correlationId, sagaType, SagaEntryType.StatePersisted,
            messageType: messageType, messageId: messageId, payloadJson: ToPayload(stateJson, maxBytes));

    /// <summary>
    /// The same entry as <see cref="CreateEntry(Guid, string, string, int, string?, string?)"/>, also held
    /// to a per-saga budget: see <see cref="ToPayload(string, int, long, long)"/>. The engine uses it for
    /// the snapshots that follow a successful step or a timeout.
    /// </summary>
    public static SagaLogEntry CreateEntry(Guid correlationId, string sagaType, string stateJson, int maxBytes,
        long recordedBytes, long budgetBytes, string? messageType, string? messageId) =>
        SagaLogEntry.Create(correlationId, sagaType, SagaEntryType.StatePersisted,
            messageType: messageType, messageId: messageId,
            payloadJson: ToPayload(stateJson, maxBytes, recordedBytes, budgetBytes));

    /// <summary>
    /// Returns <paramref name="stateJson"/> when its UTF-8 length is at most <paramref name="maxBytes"/>,
    /// otherwise <c>{"$vsagaStateOmitted":true,"bytes":N,"limit":L}</c>. The marker mirrors MongoDB's
    /// <c>$vsagaPayloadOmitted</c> under its own key. A cap of 0 therefore records size-only markers.
    /// </summary>
    public static string ToPayload(string stateJson, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(stateJson);

        var bytes = Encoding.UTF8.GetByteCount(stateJson);
        return bytes <= maxBytes ? stateJson : Marker(bytes, "limit", maxBytes);
    }

    /// <summary>
    /// <see cref="ToPayload(string, int)"/> under a per-saga budget as well. <paramref name="recordedBytes"/>
    /// is the UTF-8 length of the snapshot payloads the saga's timeline already holds. A state over the
    /// per-snapshot cap still gets the <c>limit</c> marker, since that is the reason it was left out. A state
    /// within the cap whose length, added to <paramref name="recordedBytes"/>, passes
    /// <paramref name="budgetBytes"/> gets <c>{"$vsagaStateOmitted":true,"bytes":N,"budget":B}</c> instead: the
    /// <c>budget</c> key in place of <c>limit</c> says the saga's allowance ran out, not that this state was
    /// too large. A budget of 0 or less means no budget.
    /// </summary>
    public static string ToPayload(string stateJson, int maxBytes, long recordedBytes, long budgetBytes)
    {
        var capped = ToPayload(stateJson, maxBytes);
        if (!ReferenceEquals(capped, stateJson) || budgetBytes <= 0)
            return capped;

        var bytes = Encoding.UTF8.GetByteCount(stateJson);
        return recordedBytes + bytes <= budgetBytes ? stateJson : Marker(bytes, "budget", budgetBytes);
    }

    /// <summary>The one definition of the size marker: <c>{"$vsagaStateOmitted":true,"bytes":N,"&lt;key&gt;":V}</c>.</summary>
    private static string Marker(int bytes, string key, long value) =>
        string.Create(CultureInfo.InvariantCulture, $"{{\"$vsagaStateOmitted\":true,\"bytes\":{bytes},\"{key}\":{value}}}");
}
