using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace VSaga.Persistence.MongoDB.Tests;

/// <summary>The id encodings are injective, the timestamps exact, the search pattern literal, and the document shape is what the indexes and the probe assume -- none of it needs a server.</summary>
public sealed class MongoDocumentTests
{
    /// <summary>A saga type may contain the separator; the GUID's fixed width is what keeps the instance id unambiguous.</summary>
    [Fact]
    public void InstanceId_RoundTrips_EvenWhenTheSagaTypeContainsTheSeparator()
    {
        var id = Guid.NewGuid();

        Assert.Equal((id, "Order|Saga|v2"), MongoIds.ParseInstance(MongoIds.Instance(id, "Order|Saga|v2")));
    }

    /// <summary>Length-prefixing makes the registration id injective: moving the separator between the two components must change the id.</summary>
    [Fact]
    public void RegistrationId_WithUserSuppliedComponents_NeverCollides() =>
        Assert.NotEqual(MongoIds.Registration("a|b", "c"), MongoIds.Registration("a", "b|c"), StringComparer.Ordinal);

    [Fact]
    public void Timestamps_KeepEveryTick()
    {
        var instant = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1_234_567);

        Assert.Equal(instant, MongoTimestamps.FromTicks(MongoTimestamps.ToTicks(instant)));
        Assert.Equal(DateTimeKind.Utc, MongoTimestamps.ToDate(instant).Kind);
    }

    /// <summary>The term is matched literally: regex metacharacters in a saga type or a pasted id are escaped, and the term is lower-cased to meet the lower-cased fields.</summary>
    [Fact]
    public void SearchPattern_IsALiteralLowerCasedSubstring()
    {
        var pattern = MongoListQuery.SearchPattern("Order.Saga (v2)");

        Assert.Equal(@"order\.saga\ \(v2\)", pattern.Pattern);
        Assert.Equal(string.Empty, pattern.Options);
    }

    /// <summary>The instance document's shape: field names the indexes name, absent business key and parent when null, enums as numbers, the pair for every timestamp, and the layout version.</summary>
    [Fact]
    public void InstanceDocument_SerialisesToTheIndexedShape()
    {
        var state = new ConformanceStateForShape
        {
            CorrelationId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"),
            SagaType = "OrderSaga",
            Kind = SagaKind.Choreographed,
            CurrentState = "AwaitingPayment",
            Status = SagaStatus.Compensating,
            Version = 3,
            CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UpdatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1_234_567),
        };

        var bson = SagaInstanceDocument.From(state, "{\"x\":1}").ToBsonDocument();

        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e|OrderSaga", bson["_id"].AsString);
        Assert.Equal("ordersaga", bson["sagaTypeLower"].AsString);
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", bson["correlationId"].AsString);
        Assert.Equal(1, bson["kind"].AsInt32);
        Assert.Equal(3, bson["status"].AsInt32);
        Assert.False(bson.Contains("businessKey"));
        Assert.False(bson.Contains("parentSagaType"));
        Assert.Equal(BsonType.DateTime, bson["updatedAt"].BsonType);
        Assert.Equal(state.UpdatedAtUtc.UtcTicks, bson["updatedAtTicks"].AsInt64);
        Assert.Equal(1, bson["sv"].AsInt32);
    }

    /// <summary>The outbox row keeps an explicit null destination and never omits it: the dispatcher branches on the field.</summary>
    [Fact]
    public void OutboxDocument_WritesANullDestinationExplicitly()
    {
        var row = new MongoStagedOutboxRow("m1", Guid.NewGuid(), "OrderSaga", "Reserved", [1, 2], Destination: null, "{}", DateTimeOffset.UtcNow);

        var bson = row.ToDocument(7).ToBsonDocument();

        Assert.True(bson["destination"].IsBsonNull);
        Assert.Equal("m1", bson["_id"].AsString);
        Assert.Equal(7L, bson["id"].AsInt64);
        Assert.Equal((int)SagaOutboxStatus.Pending, bson["status"].AsInt32);
    }

    /// <summary>
    /// No serializer is registered globally: mapping the documents leaves the process-wide registry at the
    /// driver's defaults (a <c>DateTimeOffset</c> still serialises as the driver's subdocument, a bare
    /// <c>Guid</c> still refuses to serialise without a representation), or a host's own MongoDB usage
    /// would change by merely referencing this package.
    /// </summary>
    [Fact]
    public void Documents_RegisterNoGlobalSerializer()
    {
        _ = SagaInstanceDocument.From(new ConformanceStateForShape { SagaType = "OrderSaga" }, "{}").ToBsonDocument();
        _ = new MongoStagedOutboxRow("m1", Guid.NewGuid(), "OrderSaga", "Reserved", [], null, "{}", DateTimeOffset.UtcNow).ToDocument(1).ToBsonDocument();

        Assert.Equal(BsonType.Document, new HostDocument { When = DateTimeOffset.UtcNow }.ToBsonDocument()["When"].BsonType);
        Assert.Throws<BsonSerializationException>(() => new HostGuidDocument { Id = Guid.NewGuid() }.ToBsonDocument());
    }

    private sealed class HostDocument
    {
        public DateTimeOffset When { get; set; }
    }

    private sealed class HostGuidDocument
    {
        public Guid Id { get; set; }
    }

    private sealed class ConformanceStateForShape : SagaState;
}
