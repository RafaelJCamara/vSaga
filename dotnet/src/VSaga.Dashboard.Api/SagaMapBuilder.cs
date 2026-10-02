using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;

namespace VSaga.Dashboard.Api;

public enum SagaMapNodeKind
{
    Initiator,
    Orchestrator,
    Participant,
    Unresolved,
}

public sealed record SagaMapNode(
    string Id, string DisplayName, SagaMapNodeKind Kind,
    string Status, int MessagesIn, int MessagesOut);

public sealed record SagaMapEdge(
    string Id, string FromNodeId, string ToNodeId,
    string MessageType, string? MessageId,
    bool IsCompensation, bool Failed, bool Unanswered,
    DateTimeOffset OccurredAtUtc);

public sealed record SagaMapEvent(
    long SequenceNumber, string? EdgeId, string? NodeId,
    SagaEntryType EntryType, string? MessageType,
    string? ErrorMessage, DateTimeOffset OccurredAtUtc);

public sealed record SagaMap(
    SagaSummary Summary,
    IReadOnlyList<SagaMapNode> Nodes,
    IReadOnlyList<SagaMapEdge> Edges,
    IReadOnlyList<SagaMapEvent> Events,
    int? FailureEventIndex);

/// <summary>
/// Pure, unit-testable translation from a saga's raw event log into a service map: nodes are the
/// services observed (via SourceService/DestinationService stamped on each log entry), edges are the
/// messages that flowed between them, stitched together by matching an outbound entry's MessageId to a
/// later inbound entry's CausationId. An unstitched outbound entry — nothing ever replied — resolves its
/// destination from the topology registry (or renders as an unresolved placeholder if even that doesn't
/// know it) and is marked Unanswered rather than dropped, since that's often the most useful thing the
/// map shows (e.g. a hung downstream service). <see cref="SagaEntryType.StatePersisted"/> snapshots are
/// skipped entirely: they carry no message flow, so the map's events are the timeline's other entries.
/// </summary>
public static class SagaMapBuilder
{
    public static SagaMap Build(SagaSummary summary, IReadOnlyList<SagaLogEntry> timeline, IReadOnlyList<ServiceTopologyEntry> topology) =>
        new Builder(summary, timeline, topology).BuildMap();

    private readonly record struct DestinationNode(string Id, string DisplayName, SagaMapNodeKind Kind);

    private sealed class NodeAccumulator(string id, string displayName, SagaMapNodeKind kind)
    {
        public string Id { get; } = id;
        public string DisplayName { get; } = displayName;
        public SagaMapNodeKind Kind { get; } = kind;
        public int MessagesIn { get; set; }
        public int MessagesOut { get; set; }
    }

    private sealed class Builder(SagaSummary summary, IReadOnlyList<SagaLogEntry> timeline, IReadOnlyList<ServiceTopologyEntry> topology)
    {
        private readonly string _orchestratorId = summary.SagaType;
        private readonly Dictionary<string, NodeAccumulator> _nodes = new(StringComparer.Ordinal);
        private readonly List<SagaMapEdge> _edges = [];
        private readonly List<SagaMapEvent> _events = [];
        private string? _initiatorId;
        private Dictionary<string, SagaLogEntry> _inboundByCausation = new(StringComparer.Ordinal);
        private HashSet<string> _failedMessageIds = new(StringComparer.Ordinal);
        private bool _compensating;
        private int? _failureEventIndex;

        public SagaMap BuildMap()
        {
            // A StatePersisted snapshot records state, not a message: filtering it here keeps it out of
            // initiator resolution, stitching, failed-id resolution, Events and FailureEventIndex alike.
            var ordered = timeline
                .Where(e => e.EntryType != SagaEntryType.StatePersisted)
                .OrderBy(e => e.SequenceNumber)
                .ToList();
            _initiatorId = ResolveInitiatorId(ordered);
            _inboundByCausation = BuildInboundByCausation(ordered);
            _failedMessageIds = ResolveFailedMessageIds(ordered);

            EnsureNode(_orchestratorId, SagaMapNodeKind.Orchestrator);

            foreach (var entry in ordered)
                ProcessEntry(entry);

            var nodes = _nodes.Values
                .Select(n => new SagaMapNode(n.Id, n.DisplayName, n.Kind, ComputeStatus(n), n.MessagesIn, n.MessagesOut))
                .OrderBy(NodeSortKey)
                .ThenBy(n => n.Id, StringComparer.Ordinal)
                .ToList();

            return new SagaMap(summary, nodes, _edges, _events, _failureEventIndex);
        }

        private static int NodeSortKey(SagaMapNode node) => node.Kind switch
        {
            SagaMapNodeKind.Orchestrator => 0,
            SagaMapNodeKind.Initiator => 1,
            SagaMapNodeKind.Participant => 2,
            _ => 3,
        };

        private void ProcessEntry(SagaLogEntry entry)
        {
            switch (entry.EntryType)
            {
                case SagaEntryType.CompensationStarted:
                    _compensating = true;
                    AddPlainEvent(entry);
                    break;
                case SagaEntryType.SagaStarted or SagaEntryType.MessageReceived:
                    ProcessInboundEntry(entry);
                    break;
                case SagaEntryType.MessagePublished or SagaEntryType.MessageSent
                    or SagaEntryType.ChildSagaStarted or SagaEntryType.ChildSagaFinished:
                    ProcessOutboundEntry(entry);
                    break;
                default:
                    AddPlainEvent(entry);
                    break;
            }

            if (_failureEventIndex is null && entry.EntryType is SagaEntryType.StepFailed or SagaEntryType.TimeoutFired or SagaEntryType.DeliveryExhausted)
                _failureEventIndex = _events.Count - 1;
        }

        private void ProcessInboundEntry(SagaLogEntry entry)
        {
            var from = entry.SourceService ?? _orchestratorId;
            string? edgeId = null;

            if (!string.Equals(from, _orchestratorId, StringComparison.Ordinal))
            {
                EnsureNode(from, KindFor(from));
                var failed = entry.MessageId is not null && _failedMessageIds.Contains(entry.MessageId);
                var edge = new SagaMapEdge($"e{entry.SequenceNumber}", from, _orchestratorId,
                    entry.MessageType ?? string.Empty, entry.MessageId, IsCompensation: _compensating, Failed: failed, Unanswered: false, entry.OccurredAtUtc);
                AddEdge(edge, from, _orchestratorId);
                edgeId = edge.Id;
            }

            _events.Add(new SagaMapEvent(entry.SequenceNumber, edgeId, edgeId is null ? from : null, entry.EntryType, entry.MessageType, entry.ErrorMessage, entry.OccurredAtUtc));
        }

        private void ProcessOutboundEntry(SagaLogEntry entry)
        {
            var from = entry.SourceService ?? _orchestratorId;
            EnsureNode(from, KindFor(from));

            string? primaryEdgeId = null;

            if (entry.MessageId is not null && _inboundByCausation.TryGetValue(entry.MessageId, out var reply))
            {
                var to = reply.SourceService ?? _orchestratorId;
                EnsureNode(to, KindFor(to));
                var edge = MakeEdge(entry, from, to, unanswered: false);
                AddEdge(edge, from, to);
                primaryEdgeId = edge.Id;
            }
            else
            {
                foreach (var destination in ResolveUnstitchedDestinations(entry))
                {
                    EnsureNode(destination.Id, destination.Kind, destination.DisplayName);
                    var edge = MakeEdge(entry, from, destination.Id, unanswered: true);
                    AddEdge(edge, from, destination.Id);
                    primaryEdgeId ??= edge.Id;
                }
            }

            _events.Add(new SagaMapEvent(entry.SequenceNumber, primaryEdgeId, null, entry.EntryType, entry.MessageType, entry.ErrorMessage, entry.OccurredAtUtc));
        }

        private SagaMapEdge MakeEdge(SagaLogEntry entry, string from, string to, bool unanswered) =>
            new($"e{entry.SequenceNumber}-{to}", from, to, entry.MessageType ?? string.Empty, entry.MessageId,
                IsCompensation: _compensating, Failed: false, Unanswered: unanswered, entry.OccurredAtUtc);

        private void AddEdge(SagaMapEdge edge, string from, string to)
        {
            _edges.Add(edge);
            _nodes[from].MessagesOut++;
            _nodes[to].MessagesIn++;
        }

        private void AddPlainEvent(SagaLogEntry entry)
        {
            var nodeId = entry.EntryType is SagaEntryType.StepFailed or SagaEntryType.TimeoutFired or SagaEntryType.DeliveryExhausted
                ? _orchestratorId
                : null;
            _events.Add(new SagaMapEvent(entry.SequenceNumber, null, nodeId, entry.EntryType, entry.MessageType, entry.ErrorMessage, entry.OccurredAtUtc));
        }

        private IReadOnlyList<DestinationNode> ResolveUnstitchedDestinations(SagaLogEntry entry)
        {
            // SendAsync's explicit destination is stored as the raw queue name at capture time (see
            // SagaContext); resolve it to a service via the registry. PublishAsync always stores a null
            // DestinationService and is resolved by message type instead, so genuine fan-out (more than
            // one consumer subscribed to the same message type) produces one edge per consumer.
            if (entry.EntryType == SagaEntryType.MessageSent && entry.DestinationService is { Length: > 0 } queueName)
            {
                var byQueue = ResolveByPredicate(t => string.Equals(t.QueueName, queueName, StringComparison.Ordinal));
                return byQueue.Count > 0 ? byQueue : [UnresolvedFor(queueName)];
            }

            if (entry.MessageType is { Length: > 0 } messageType)
            {
                var byType = ResolveByPredicate(t => string.Equals(t.MessageType, messageType, StringComparison.Ordinal));
                if (byType.Count > 0)
                    return byType;
            }

            return [UnresolvedFor(entry.MessageType ?? "unknown")];
        }

        private List<DestinationNode> ResolveByPredicate(Func<ServiceTopologyEntry, bool> predicate) =>
            topology.Where(predicate)
                .Select(t => t.ServiceName)
                .Distinct(StringComparer.Ordinal)
                .Select(name => new DestinationNode(name, name, KindFor(name)))
                .ToList();

        private static DestinationNode UnresolvedFor(string key) => new($"unresolved:{key}", "?", SagaMapNodeKind.Unresolved);

        private SagaMapNodeKind KindFor(string nodeId)
        {
            if (string.Equals(nodeId, _orchestratorId, StringComparison.Ordinal))
                return SagaMapNodeKind.Orchestrator;
            if (_initiatorId is not null && string.Equals(nodeId, _initiatorId, StringComparison.Ordinal))
                return SagaMapNodeKind.Initiator;
            return SagaMapNodeKind.Participant;
        }

        private void EnsureNode(string id, SagaMapNodeKind kind, string? displayName = null)
        {
            if (!_nodes.ContainsKey(id))
                _nodes[id] = new NodeAccumulator(id, displayName ?? id, kind);
        }

        private string ComputeStatus(NodeAccumulator node)
        {
            if (string.Equals(node.Id, _orchestratorId, StringComparison.Ordinal))
                return summary.Status is SagaStatus.Failed or SagaStatus.TimedOut ? "failed" : "ok";

            if (_edges.Any(e => string.Equals(e.ToNodeId, node.Id, StringComparison.Ordinal) && e.Unanswered))
                return "unanswered";
            if (_edges.Any(e => string.Equals(e.FromNodeId, node.Id, StringComparison.Ordinal) && e.Failed))
                return "failed";
            return "ok";
        }

        /// <summary>
        /// Message ids whose inbound edge should render as "the failing hop". Covers two distinct
        /// failure shapes: a StepFailed entry (an action threw), and a business failure reached through
        /// a normal, successful step transition (e.g. "payment declined") — which never logs
        /// StepFailed, so the signal there is instead "the last inbound message before a SagaCompleted
        /// entry on a saga that ended Failed/TimedOut". A StepFailed/timeout-driven failure never logs
        /// SagaCompleted at all, so the two cases can't double-mark the same edge.
        /// </summary>
        private HashSet<string> ResolveFailedMessageIds(List<SagaLogEntry> ordered)
        {
            var ids = ordered
                .Where(e => e.EntryType == SagaEntryType.StepFailed && e.MessageId is not null)
                .Select(e => e.MessageId!)
                .ToHashSet(StringComparer.Ordinal);

            if (summary.Status is SagaStatus.Failed or SagaStatus.TimedOut)
            {
                var completedIndex = ordered.FindLastIndex(e => e.EntryType == SagaEntryType.SagaCompleted);
                var trigger = completedIndex < 0
                    ? null
                    : ordered.Take(completedIndex).LastOrDefault(e => e.EntryType is SagaEntryType.SagaStarted or SagaEntryType.MessageReceived && e.MessageId is not null);

                if (trigger is not null)
                    ids.Add(trigger.MessageId!);
            }

            return ids;
        }

        private string? ResolveInitiatorId(IReadOnlyList<SagaLogEntry> ordered)
        {
            var candidate = ordered.FirstOrDefault(e => e.EntryType == SagaEntryType.SagaStarted)?.SourceService;
            return candidate is not null && !string.Equals(candidate, _orchestratorId, StringComparison.Ordinal) ? candidate : null;
        }

        private static Dictionary<string, SagaLogEntry> BuildInboundByCausation(IReadOnlyList<SagaLogEntry> ordered) =>
            ordered.Where(e => e.EntryType == SagaEntryType.MessageReceived && e.CausationId is not null)
                .GroupBy(e => e.CausationId!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }
}
