using System.Diagnostics;
using VSaga.Abstractions.Diagnostics;

namespace VSaga.Abstractions.Transport;

/// <summary>Metadata stamped onto every outbound message alongside its payload.</summary>
public sealed record MessageEnvelope(
    Guid CorrelationId,
    string MessageId,
    IReadOnlyDictionary<string, string>? Headers = null)
{
    /// <summary>Stamped by whoever publishes, so the receiving service can learn who sent a message — see MessageEnvelope.From.</summary>
    public const string SourceServiceHeader = "x-vsaga-source-service";

    /// <summary>The MessageId of the inbound message being handled when this envelope's message was published, if any — the map's causation-stitching key.</summary>
    public const string CausationIdHeader = "x-vsaga-causation-id";

    /// <summary>
    /// Set by <c>ISagaContext.StartChildAsync</c> on a child saga's initiating message, naming the saga
    /// type that started it. Whichever saga's <c>CanInitiate</c> matches that message reads this pair
    /// once, at instance creation, onto <c>SagaState.ParentSagaType</c>/<c>ParentCorrelationId</c> —
    /// there is no compile-time link between parent and child, only these two headers.
    /// </summary>
    public const string ParentSagaTypeHeader = "x-vsaga-parent-saga-type";

    /// <summary>The correlation id of the instance that published a child's initiating message — see <see cref="ParentSagaTypeHeader"/>.</summary>
    public const string ParentCorrelationIdHeader = "x-vsaga-parent-correlation-id";

    /// <summary>
    /// Names the one saga type a redriven message is meant for. The dashboard's retry stamps it on the
    /// message it republishes, because a publish by message type under a saga's correlation id would
    /// otherwise reach every saga type subscribed to that type and run their steps too. The engine
    /// acknowledges and ignores a message whose header names another saga type (ordinal comparison):
    /// no timeline entry, no deserialisation, no instance lookup. A message without the header is
    /// handled as it always was. The <c>x-vsaga-</c> prefix is what makes every wire adapter carry it
    /// unchanged (Brighter and HTTP keep only prefixed headers). It never propagates: outbound envelopes
    /// are built fresh by <see cref="From"/>, so the messages a redriven step publishes reach every
    /// subscriber as usual, while an infrastructure redelivery copies the inbound headers and keeps it.
    /// </summary>
    public const string TargetSagaTypeHeader = "x-vsaga-target-saga-type";

    public static MessageEnvelope New(Guid correlationId, IReadOnlyDictionary<string, string>? headers = null) =>
        new(correlationId, Guid.NewGuid().ToString("N"), headers);

    /// <summary>Stamps the publisher's service identity (and, if this publish was caused by handling an inbound message, that message's id) onto the envelope's headers, plus the current <see cref="Activity"/>'s W3C trace context, if any.</summary>
    public static MessageEnvelope From(string sourceService, Guid correlationId, string? causationId = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);

        if (headers is not null)
        {
            foreach (var (key, value) in headers)
                merged[key] = value;
        }

        merged[SourceServiceHeader] = sourceService;
        if (causationId is not null)
            merged[CausationIdHeader] = causationId;

        if (Activity.Current is { } activity)
            VSagaDiagnostics.Inject(activity.Context, merged);

        return new MessageEnvelope(correlationId, Guid.NewGuid().ToString("N"), merged);
    }
}
