using System.Globalization;
using System.Text;

namespace VSaga.Abstractions.Persistence;

/// <summary>
/// The single definition of a <see cref="SagaEntryType.StatePersisted"/> entry and of its size marker. The
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
    /// Returns <paramref name="stateJson"/> when its UTF-8 length is at most <paramref name="maxBytes"/>,
    /// otherwise <c>{"$vsagaStateOmitted":true,"bytes":N,"limit":L}</c>. The marker mirrors MongoDB's
    /// <c>$vsagaPayloadOmitted</c> under its own key. A cap of 0 therefore records size-only markers.
    /// </summary>
    public static string ToPayload(string stateJson, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(stateJson);

        var bytes = Encoding.UTF8.GetByteCount(stateJson);
        return bytes <= maxBytes
            ? stateJson
            : string.Create(CultureInfo.InvariantCulture, $"{{\"$vsagaStateOmitted\":true,\"bytes\":{bytes},\"limit\":{maxBytes}}}");
    }
}
