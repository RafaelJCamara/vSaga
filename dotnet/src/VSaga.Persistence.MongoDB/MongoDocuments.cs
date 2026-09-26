using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace VSaga.Persistence.MongoDB;

// The stored shapes, mapped per class through attributes -- never through BsonDefaults or a global
// BsonSerializer registration, whose registry is process-wide and settable once: a host with its own
// MongoDB usage would be broken by merely referencing this package. No Guid and no DateTimeOffset is
// ever handed to the driver (a Guid needs a process-wide representation choice; a DateTimeOffset
// serialises as a subdocument that $gt and $sort would compare field by field): correlation ids are
// stored as their lower-case text, and timestamps as the Date + Ticks pair MongoTimestamps describes.
// Enums are stored as their numeric values, because the dashboard's Status sort is over SagaStatus's
// declared order. Every document carries sv, the layout version.

/// <summary>The current-state snapshot: the projected fields <c>ISagaSummaryReader</c> reads plus <c>dataJson</c>, the serialised <c>TState</c>, which is authoritative.</summary>
[BsonIgnoreExtraElements]
internal sealed class SagaInstanceDocument
{
    public const int CurrentSchemaVersion = 1;

    [BsonId]
    public string Id { get; set; } = string.Empty;

    [BsonElement("sagaType")]
    public string SagaType { get; set; } = string.Empty;

    /// <summary>The saga type lower-cased, so the dashboard's case-insensitive <c>Search</c> is a plain regex.</summary>
    [BsonElement("sagaTypeLower")]
    public string SagaTypeLower { get; set; } = string.Empty;

    [BsonElement("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    [BsonElement("kind")]
    public SagaKind Kind { get; set; }

    [BsonElement("currentState")]
    public string CurrentState { get; set; } = string.Empty;

    [BsonElement("status")]
    public SagaStatus Status { get; set; }

    [BsonElement("version")]
    public int Version { get; set; }

    [BsonElement("dataJson")]
    public string DataJson { get; set; } = string.Empty;

    [BsonElement("parentSagaType")]
    [BsonIgnoreIfNull]
    public string? ParentSagaType { get; set; }

    [BsonElement("parentCorrelationId")]
    [BsonIgnoreIfNull]
    public string? ParentCorrelationId { get; set; }

    /// <summary>Absent, not null, when the saga has none: the unique index's partial filter is <c>{$type: "string"}</c>, and an explicit null would be a value every second key-less saga collides on.</summary>
    [BsonElement("businessKey")]
    [BsonIgnoreIfNull]
    public string? BusinessKey { get; set; }

    [BsonElement("createdAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    [BsonElement("createdAtTicks")]
    public long CreatedAtTicks { get; set; }

    [BsonElement("updatedAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAt { get; set; }

    [BsonElement("updatedAtTicks")]
    public long UpdatedAtTicks { get; set; }

    [BsonElement("sv")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public static SagaInstanceDocument From<TState>(TState state, string dataJson) where TState : SagaState => new()
    {
        Id = MongoIds.Instance(state.CorrelationId, state.SagaType),
        SagaType = state.SagaType,
        SagaTypeLower = state.SagaType.ToLowerInvariant(),
        CorrelationId = MongoIds.CorrelationText(state.CorrelationId),
        Kind = state.Kind,
        CurrentState = state.CurrentState,
        Status = state.Status,
        Version = state.Version,
        DataJson = dataJson,
        ParentSagaType = state.ParentSagaType,
        ParentCorrelationId = state.ParentCorrelationId is { } parent ? MongoIds.CorrelationText(parent) : null,
        BusinessKey = state.BusinessKey,
        CreatedAt = MongoTimestamps.ToDate(state.CreatedAtUtc),
        CreatedAtTicks = MongoTimestamps.ToTicks(state.CreatedAtUtc),
        UpdatedAt = MongoTimestamps.ToDate(state.UpdatedAtUtc),
        UpdatedAtTicks = MongoTimestamps.ToTicks(state.UpdatedAtUtc),
    };

    public SagaSummary ToSummary() =>
        new(Guid.Parse(CorrelationId), SagaType, Kind, CurrentState, Status,
            MongoTimestamps.FromTicks(CreatedAtTicks), MongoTimestamps.FromTicks(UpdatedAtTicks), Version,
            ParentSagaType, ParentCorrelationId is { } parent ? Guid.Parse(parent) : null);
}

/// <summary>One timeline entry. <c>seq</c> is allocated per instance from <see cref="CounterDocument"/>; it replaces EF Core's identity column.</summary>
[BsonIgnoreExtraElements]
internal sealed class SagaEventLogDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("sagaType")]
    public string SagaType { get; set; } = string.Empty;

    [BsonElement("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    [BsonElement("seq")]
    public long Seq { get; set; }

    [BsonElement("entryType")]
    public SagaEntryType EntryType { get; set; }

    [BsonElement("fromState")]
    public string? FromState { get; set; }

    [BsonElement("toState")]
    public string? ToState { get; set; }

    [BsonElement("messageType")]
    public string? MessageType { get; set; }

    [BsonElement("messageId")]
    public string? MessageId { get; set; }

    [BsonElement("payloadJson")]
    public string? PayloadJson { get; set; }

    [BsonElement("errorMessage")]
    public string? ErrorMessage { get; set; }

    [BsonElement("traceId")]
    public string? TraceId { get; set; }

    [BsonElement("spanId")]
    public string? SpanId { get; set; }

    [BsonElement("occurredAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime OccurredAt { get; set; }

    [BsonElement("occurredAtTicks")]
    public long OccurredAtTicks { get; set; }

    [BsonElement("sourceService")]
    public string? SourceService { get; set; }

    [BsonElement("destinationService")]
    public string? DestinationService { get; set; }

    [BsonElement("causationId")]
    public string? CausationId { get; set; }

    [BsonElement("sv")]
    public int SchemaVersion { get; set; } = SagaInstanceDocument.CurrentSchemaVersion;

    public static SagaEventLogDocument From(SagaLogEntry entry, long seq, string? payloadJson) => new()
    {
        SagaType = entry.SagaType,
        CorrelationId = MongoIds.CorrelationText(entry.CorrelationId),
        Seq = seq,
        EntryType = entry.EntryType,
        FromState = entry.FromState,
        ToState = entry.ToState,
        MessageType = entry.MessageType,
        MessageId = entry.MessageId,
        PayloadJson = payloadJson,
        ErrorMessage = entry.ErrorMessage,
        TraceId = entry.TraceId,
        SpanId = entry.SpanId,
        OccurredAt = MongoTimestamps.ToDate(entry.OccurredAtUtc),
        OccurredAtTicks = MongoTimestamps.ToTicks(entry.OccurredAtUtc),
        SourceService = entry.SourceService,
        DestinationService = entry.DestinationService,
        CausationId = entry.CausationId,
    };

    public SagaLogEntry ToEntry() =>
        new(Seq, Guid.Parse(CorrelationId), SagaType, EntryType, FromState, ToState, MessageType, MessageId, PayloadJson,
            ErrorMessage, TraceId, SpanId, MongoTimestamps.FromTicks(OccurredAtTicks), SourceService, DestinationService, CausationId);
}

/// <summary>
/// A counter: the per-instance timeline sequence (keyed by the instance id, in <c>sagaSequences</c>) and
/// the row-id counters for timeouts and outbox rows (keyed by collection name, in <c>sagaCounters</c>).
/// A sequence document is durable, correctness-bearing state -- deleting one restarts its timeline's
/// <c>seq</c> at 1 mid-timeline, which silently corrupts the order compensation is derived from.
/// </summary>
[BsonIgnoreExtraElements]
internal sealed class CounterDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    [BsonElement("seq")]
    public long Seq { get; set; }
}

[BsonIgnoreExtraElements]
internal sealed class SagaTimeoutDocument
{
    [BsonId]
    public long Id { get; set; }

    [BsonElement("sagaType")]
    public string SagaType { get; set; } = string.Empty;

    [BsonElement("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    [BsonElement("forState")]
    public string ForState { get; set; } = string.Empty;

    [BsonElement("dueAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime DueAt { get; set; }

    [BsonElement("dueAtTicks")]
    public long DueAtTicks { get; set; }

    [BsonElement("status")]
    public SagaTimeoutStatus Status { get; set; }

    [BsonElement("sv")]
    public int SchemaVersion { get; set; } = SagaInstanceDocument.CurrentSchemaVersion;

    public SagaTimeout ToTimeout() =>
        new(Id, Guid.Parse(CorrelationId), SagaType, ForState, MongoTimestamps.FromTicks(DueAtTicks), Status);
}

/// <summary>
/// An outbox row, keyed on its message id: both <c>MarkDispatchedAsync</c> and <c>DiscardPendingAsync</c>
/// are message-id-keyed, and no store-generated id exists at enqueue time. <c>destination</c> is written
/// as an explicit null when there is none -- the dispatcher branches on it to choose a broadcast over an
/// addressed send -- and the numeric <c>id</c> exists only because <c>SagaOutboxMessage</c> carries one.
/// </summary>
[BsonIgnoreExtraElements]
internal sealed class SagaOutboxDocument
{
    [BsonId]
    public string MessageId { get; set; } = string.Empty;

    [BsonElement("id")]
    public long Id { get; set; }

    [BsonElement("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    [BsonElement("sagaType")]
    public string SagaType { get; set; } = string.Empty;

    [BsonElement("messageTypeName")]
    public string MessageTypeName { get; set; } = string.Empty;

    [BsonElement("body")]
    public byte[] Body { get; set; } = [];

    [BsonElement("destination")]
    public string? Destination { get; set; }

    /// <summary>The envelope headers as a JSON object, as EF Core stores them: header names are an open set containing dots and dashes, which BSON field names cannot safely carry.</summary>
    [BsonElement("headersJson")]
    public string HeadersJson { get; set; } = "{}";

    [BsonElement("status")]
    public SagaOutboxStatus Status { get; set; }

    [BsonElement("createdAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    [BsonElement("createdAtTicks")]
    public long CreatedAtTicks { get; set; }

    [BsonElement("sv")]
    public int SchemaVersion { get; set; } = SagaInstanceDocument.CurrentSchemaVersion;
}

/// <summary>One (service, message type) consumer binding; the composite string id gives the upsert its idempotency.</summary>
[BsonIgnoreExtraElements]
internal sealed class ConsumerRegistrationDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    [BsonElement("serviceName")]
    public string ServiceName { get; set; } = string.Empty;

    [BsonElement("messageType")]
    public string MessageType { get; set; } = string.Empty;

    [BsonElement("queueName")]
    public string QueueName { get; set; } = string.Empty;

    [BsonElement("lastSeenAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime LastSeenAt { get; set; }

    [BsonElement("lastSeenAtTicks")]
    public long LastSeenAtTicks { get; set; }

    [BsonElement("sv")]
    public int SchemaVersion { get; set; } = SagaInstanceDocument.CurrentSchemaVersion;
}

/// <summary>The schema marker, one document per database, written by the bootstrapper once the indexes are in place.</summary>
[BsonIgnoreExtraElements]
internal sealed class SchemaMarkerDocument
{
    public const string MarkerId = "schema";

    [BsonId]
    public string Id { get; set; } = MarkerId;

    [BsonElement("sv")]
    public int SchemaVersion { get; set; }
}
