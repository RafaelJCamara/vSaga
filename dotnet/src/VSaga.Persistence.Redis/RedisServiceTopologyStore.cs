using System.Text.Json;
using VSaga.Abstractions.Persistence;

namespace VSaga.Persistence.Redis;

/// <summary>One hash, a field per (serviceName, messageType) binding -- an upsert is a plain <c>HSET</c>.</summary>
public sealed class RedisServiceTopologyStore(RedisConnection connection, RedisKeySpace keys) : IServiceTopologyStore
{
    public async Task RecordAsync(string serviceName, string messageType, string queueName, DateTimeOffset seenAtUtc, CancellationToken cancellationToken = default)
    {
        var db = await connection.GetDatabaseAsync(cancellationToken);
        var value = new TopologyValue(serviceName, messageType, queueName, RedisTimestamps.ToMicroseconds(seenAtUtc));
        await db.HashSetAsync(keys.Topology, RedisKeySpace.TopologyField(serviceName, messageType), JsonSerializer.Serialize(value));
    }

    public async Task<IReadOnlyList<ServiceTopologyEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var db = await connection.GetDatabaseAsync(cancellationToken);
        var entries = await db.HashGetAllAsync(keys.Topology);

        return entries
            .Select(e => JsonSerializer.Deserialize<TopologyValue>(e.Value.ToString())!)
            .Select(v => new ServiceTopologyEntry(v.ServiceName, v.MessageType, v.QueueName, RedisTimestamps.FromMicroseconds(v.LastSeenMicroseconds)))
            .ToList();
    }

    private sealed record TopologyValue(string ServiceName, string MessageType, string QueueName, long LastSeenMicroseconds);
}
