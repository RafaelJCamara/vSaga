using VSaga.Abstractions.Persistence;
using MongoDB.Driver;

namespace VSaga.Persistence.MongoDB;

/// <summary>One document per (serviceName, messageType) binding; the upsert is one <c>replaceOne</c> where EF Core needs two round trips.</summary>
public sealed class MongoServiceTopologyStore(MongoCollections collections) : IServiceTopologyStore
{
    private static readonly ReplaceOptions Upsert = new() { IsUpsert = true };

    public Task RecordAsync(string serviceName, string messageType, string queueName, DateTimeOffset seenAtUtc, CancellationToken cancellationToken = default)
    {
        var id = MongoIds.Registration(serviceName, messageType);
        var document = new ConsumerRegistrationDocument
        {
            Id = id,
            ServiceName = serviceName,
            MessageType = messageType,
            QueueName = queueName,
            LastSeenAt = MongoTimestamps.ToDate(seenAtUtc),
            LastSeenAtTicks = MongoTimestamps.ToTicks(seenAtUtc),
        };

        return collections.Registrations.ReplaceOneAsync(Builders<ConsumerRegistrationDocument>.Filter.Eq(d => d.Id, id), document, Upsert, cancellationToken);
    }

    public async Task<IReadOnlyList<ServiceTopologyEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var documents = await collections.Registrations.Find(Builders<ConsumerRegistrationDocument>.Filter.Empty).ToListAsync(cancellationToken);
        return documents
            .Select(d => new ServiceTopologyEntry(d.ServiceName, d.MessageType, d.QueueName, MongoTimestamps.FromTicks(d.LastSeenAtTicks)))
            .ToList();
    }
}
