using System.Globalization;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using StackExchange.Redis;

namespace VSaga.Persistence.Redis;

/// <summary>
/// The snapshot hash: the projected fields <c>ISagaSummaryReader</c> reads plus <c>dataJson</c>, the
/// serialised <c>TState</c>. The blob is authoritative and the other fields are a projection of it,
/// written together by the one persist script so the two can never disagree. Enums are stored as their
/// numeric values, because the dashboard's Status sort is over <see cref="SagaStatus"/>'s declared order.
/// </summary>
internal sealed record RedisSnapshotRow(
    string SagaType,
    Guid CorrelationId,
    SagaKind Kind,
    string CurrentState,
    SagaStatus Status,
    int Version,
    string DataJson,
    string? ParentSagaType,
    Guid? ParentCorrelationId,
    string? BusinessKey,
    long CreatedMicroseconds,
    long UpdatedMicroseconds)
{
    public const string VersionField = "version";
    public const string DataJsonField = "dataJson";

    /// <summary>Every field, in the order <see cref="FromValues"/> reads them back.</summary>
    public static readonly RedisValue[] Fields =
    [
        "sagaType", "correlationId", "kind", "currentState", "status", VersionField, DataJsonField,
        "parentSagaType", "parentCorrelationId", "businessKey", "createdMicros", "updatedMicros",
    ];

    public string Member => RedisKeySpace.Member(CorrelationId, SagaType);

    public static RedisSnapshotRow FromState<TState>(TState state, string dataJson) where TState : SagaState =>
        new(state.SagaType, state.CorrelationId, state.Kind, state.CurrentState, state.Status, state.Version, dataJson,
            state.ParentSagaType, state.ParentCorrelationId, state.BusinessKey,
            RedisTimestamps.ToMicroseconds(state.CreatedAtUtc), RedisTimestamps.ToMicroseconds(state.UpdatedAtUtc));

    /// <summary>The row an <c>HMGET</c> of <see cref="Fields"/> returned, or null when the hash does not exist.</summary>
    public static RedisSnapshotRow? FromValues(RedisValue[] values)
    {
        if (values[0].IsNull)
            return null;

        return new RedisSnapshotRow(
            values[0].ToString(),
            Guid.Parse(values[1].ToString()),
            (SagaKind)(int)values[2],
            values[3].ToString(),
            (SagaStatus)(int)values[4],
            (int)values[5],
            values[6].ToString(),
            (string?)values[7],
            values[8].IsNull ? null : Guid.Parse(values[8].ToString()),
            (string?)values[9],
            (long)values[10],
            (long)values[11]);
    }

    /// <summary>Field/value pairs for <c>HSET</c>; null optional fields are omitted, and the script deletes the hash first so a field that became null disappears.</summary>
    public RedisValue[] ToHashArguments()
    {
        var values = new List<RedisValue>(24)
        {
            Fields[0], SagaType,
            Fields[1], CorrelationId.ToString("D"),
            Fields[2], (int)Kind,
            Fields[3], CurrentState,
            Fields[4], (int)Status,
            Fields[5], Version,
            Fields[6], DataJson,
            Fields[10], CreatedMicroseconds,
            Fields[11], UpdatedMicroseconds,
        };

        if (ParentSagaType is not null)
        {
            values.Add(Fields[7]);
            values.Add(ParentSagaType);
        }

        if (ParentCorrelationId is { } parentCorrelationId)
        {
            values.Add(Fields[8]);
            values.Add(parentCorrelationId.ToString("D"));
        }

        if (BusinessKey is not null)
        {
            values.Add(Fields[9]);
            values.Add(BusinessKey);
        }

        return values.ToArray();
    }

    public SagaSummary ToSummary() =>
        new(CorrelationId, SagaType, Kind, CurrentState, Status,
            RedisTimestamps.FromMicroseconds(CreatedMicroseconds), RedisTimestamps.FromMicroseconds(UpdatedMicroseconds),
            Version, ParentSagaType, ParentCorrelationId);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{SagaType}/{CorrelationId:D} v{Version} {Status}");
}
