using MongoDB.Driver;

namespace VSaga.Persistence.MongoDB;

/// <summary>
/// The provider's collection handles over the one <see cref="MongoConnection"/>. Every handle inherits the
/// database's pinned settings -- primary reads, local read concern, majority write concern -- so the
/// event-log append, the one write the redelivery net rests on, is acknowledged by a majority whether or
/// not it runs inside a transaction.
/// </summary>
public sealed class MongoCollections
{
    public const string InstancesName = "sagaInstances";
    public const string EventLogName = "sagaEventLog";
    public const string SequencesName = "sagaSequences";
    public const string CountersName = "sagaCounters";
    public const string TimeoutsName = "sagaTimeouts";
    public const string OutboxName = "sagaOutboxMessages";
    public const string RegistrationsName = "sagaConsumerRegistrations";
    public const string MetaName = "vsagaMeta";

    /// <summary>Every collection the provider owns, in the order the bootstrapper creates their indexes.</summary>
    public static readonly IReadOnlyList<string> Names =
        [InstancesName, EventLogName, SequencesName, CountersName, TimeoutsName, OutboxName, RegistrationsName, MetaName];

    public MongoCollections(MongoConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Connection = connection;
        Instances = connection.Database.GetCollection<SagaInstanceDocument>(InstancesName);
        EventLog = connection.Database.GetCollection<SagaEventLogDocument>(EventLogName);
        Sequences = connection.Database.GetCollection<CounterDocument>(SequencesName);
        Counters = connection.Database.GetCollection<CounterDocument>(CountersName);
        Timeouts = connection.Database.GetCollection<SagaTimeoutDocument>(TimeoutsName);
        Outbox = connection.Database.GetCollection<SagaOutboxDocument>(OutboxName);
        Registrations = connection.Database.GetCollection<ConsumerRegistrationDocument>(RegistrationsName);
        Meta = connection.Database.GetCollection<SchemaMarkerDocument>(MetaName);
    }

    public MongoConnection Connection { get; }

    internal IMongoCollection<SagaInstanceDocument> Instances { get; }

    internal IMongoCollection<SagaEventLogDocument> EventLog { get; }

    internal IMongoCollection<CounterDocument> Sequences { get; }

    internal IMongoCollection<CounterDocument> Counters { get; }

    internal IMongoCollection<SagaTimeoutDocument> Timeouts { get; }

    internal IMongoCollection<SagaOutboxDocument> Outbox { get; }

    internal IMongoCollection<ConsumerRegistrationDocument> Registrations { get; }

    internal IMongoCollection<SchemaMarkerDocument> Meta { get; }
}
