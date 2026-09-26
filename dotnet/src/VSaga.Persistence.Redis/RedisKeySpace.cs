using System.Globalization;
using System.Text;
using VSaga.Abstractions.Sagas;
using StackExchange.Redis;

namespace VSaga.Persistence.Redis;

/// <summary>
/// Every key the provider owns, computed in C# and handed to Lua as <c>KEYS</c> -- no script builds a key
/// of its own except the two claim scripts and the cancel script, which append a member read from a
/// sorted set to a prefix this class supplies (<see cref="TimeoutRowPrefix"/>, <see cref="OutboxRowPrefix"/>).
/// </summary>
/// <remarks>
/// <para>
/// Every key starts with <c>{vsaga:&lt;namespace&gt;}:</c>. The braces are a Redis hash tag, so the whole
/// key space hashes to one slot: Cluster is unsupported (the bootstrapper refuses it), but the tag costs
/// nothing and means a future Cluster story starts from "already one slot" rather than a key migration.
/// </para>
/// <para>
/// Composite keys are injective. A saga instance is <c>{correlationId:D}|{sagaType}</c> -- the GUID is
/// fixed-width, so the saga type is everything after position 36 whatever characters it holds. Where a
/// user-supplied string is not last in the key it is length-prefixed with its UTF-8 byte count
/// (<c>{len}:{value}</c>), so <c>a|b</c> + <c>c</c> and <c>a</c> + <c>b|c</c> never collide.
/// </para>
/// <para>
/// No key here ever carries a TTL, and the provider sets none: the event log is the compensation set
/// and the redelivery dedupe, so nothing the provider owns can expire without breaking correctness.
/// </para>
/// </remarks>
public sealed class RedisKeySpace
{
    /// <summary>The fixed length of a GUID in its <c>D</c> format, which leads every instance member string.</summary>
    private const int GuidLength = 36;

    public RedisKeySpace(string ns)
    {
        if (string.IsNullOrWhiteSpace(ns) || ns.Contains('{', StringComparison.Ordinal) || ns.Contains('}', StringComparison.Ordinal))
            throw new ArgumentException("The Redis namespace must be non-empty and must not contain braces, which delimit the key space's hash tag.", nameof(ns));

        Prefix = "{vsaga:" + ns + "}:";
        TimeoutRowPrefix = Prefix + "to:row:";
        OutboxRowPrefix = Prefix + "ob:row:";
    }

    /// <summary>The namespace prefix every key starts with, hash tag included.</summary>
    public string Prefix { get; }

    /// <summary>A hash holding <c>sv</c>, the key-space schema version the bootstrapper checks.</summary>
    public RedisKey Meta => Prefix + "meta";

    /// <summary>
    /// The torn-write sentinel: a persist script writes its instance here first and deletes it last, so a
    /// field that survives names a saga whose write set a script abort left half-applied.
    /// </summary>
    public RedisKey Torn => Prefix + "torn";

    /// <summary>A placeholder for a <c>KEYS</c> slot the script must not touch (no business key, no parent). Never written.</summary>
    public RedisKey Nil => Prefix + "nil";

    /// <summary>The instance member string every summary index holds: both fields <c>Search</c> matches, so a search never reads a snapshot.</summary>
    public static string Member(Guid correlationId, string sagaType) => correlationId.ToString("D") + "|" + sagaType;

    /// <summary>Splits a member string back into the instance identity it encodes.</summary>
    public static (Guid CorrelationId, string SagaType) ParseMember(string member) =>
        (Guid.ParseExact(member.AsSpan(0, GuidLength), "D"), member[(GuidLength + 1)..]);

    /// <summary>The snapshot hash: projected fields plus <c>dataJson</c>.</summary>
    public RedisKey Saga(Guid correlationId, string sagaType) => Prefix + "saga:" + Member(correlationId, sagaType);

    /// <summary>The business-key reservation, a string holding the reserving correlation id.</summary>
    public RedisKey BusinessKey(string sagaType, string businessKey) => Prefix + "bk:" + LengthPrefixed(sagaType) + "|" + businessKey;

    /// <summary>The timeline: a list of one JSON element per <see cref="Abstractions.Persistence.SagaLogEntry"/>.</summary>
    public RedisKey Log(Guid correlationId, string sagaType) => Prefix + "log:" + Member(correlationId, sagaType);

    /// <summary>Inbound message ids only -- the O(1) redelivery dedupe.</summary>
    public RedisKey Dedupe(Guid correlationId, string sagaType) => Prefix + "dedupe:" + Member(correlationId, sagaType);

    public RedisKey TimeoutSequence => Prefix + "to:seq";

    /// <summary>Prefix of every timeout row hash; the claim and cancel scripts append the row id to it.</summary>
    public string TimeoutRowPrefix { get; }

    public RedisKey TimeoutRow(long id) => TimeoutRowPrefix + PadId(id);

    /// <summary>Pending timeouts, scored by due microseconds; a claim removes what it fires, so only Pending rows are ever members.</summary>
    public RedisKey TimeoutDue => Prefix + "to:due";

    /// <summary>The pending row ids of one (sagaType, correlationId, forState) scope, so a cancel is a lookup rather than a scan.</summary>
    public RedisKey TimeoutFor(string sagaType, Guid correlationId, string forState) =>
        Prefix + "to:for:" + correlationId.ToString("D") + "|" + LengthPrefixed(sagaType) + "|" + forState;

    /// <summary>Prefix of every outbox row hash; the claim script appends the message id to it.</summary>
    public string OutboxRowPrefix { get; }

    public RedisKey OutboxRow(string messageId) => OutboxRowPrefix + messageId;

    /// <summary>Publishable outbox rows, scored by created microseconds. A row hash absent from here is garbage, never a phantom publish.</summary>
    public RedisKey OutboxPending => Prefix + "ob:pending";

    /// <summary>The counter behind <c>SagaOutboxMessage.Id</c>, assigned inside the persist script as each row is written.</summary>
    public RedisKey OutboxSequence => Prefix + "ob:seq";

    public RedisKey IndexUpdated => Prefix + "ix:updated";

    public RedisKey IndexStatus(SagaStatus status) => Prefix + "ix:status:" + ((int)status).ToString(CultureInfo.InvariantCulture);

    public RedisKey IndexKind(SagaKind kind) => Prefix + "ix:kind:" + ((int)kind).ToString(CultureInfo.InvariantCulture);

    public RedisKey IndexType(string sagaType) => Prefix + "ix:type:" + sagaType;

    /// <summary>Children of one parent, scored by created microseconds (oldest first).</summary>
    public RedisKey IndexParent(string parentSagaType, Guid parentCorrelationId) =>
        Prefix + "ix:parent:" + parentCorrelationId.ToString("D") + "|" + parentSagaType;

    /// <summary>The saga types tracking one correlation id.</summary>
    public RedisKey IndexCorrelation(Guid correlationId) => Prefix + "ix:corr:" + correlationId.ToString("D");

    /// <summary>Saga type to kind, for <c>GetSagaTypesAsync</c>.</summary>
    public RedisKey IndexTypes => Prefix + "ix:types";

    /// <summary>Saga type to instance count, so the type list could drop a type if deletion is ever added.</summary>
    public RedisKey IndexTypeCounts => Prefix + "ix:typecount";

    /// <summary>The service topology: one hash, field per (serviceName, messageType).</summary>
    public RedisKey Topology => Prefix + "topo";

    public static string TopologyField(string serviceName, string messageType) => LengthPrefixed(serviceName) + "|" + messageType;

    /// <summary>Row ids are zero-padded so their lexicographic order as sorted-set members equals their numeric order.</summary>
    public static string PadId(long id) => id.ToString("D19", CultureInfo.InvariantCulture);

    private static string LengthPrefixed(string value) => Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture) + ":" + value;
}
