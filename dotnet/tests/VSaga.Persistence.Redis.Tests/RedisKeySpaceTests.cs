using VSaga.Abstractions.Sagas;
using StackExchange.Redis;

namespace VSaga.Persistence.Redis.Tests;

/// <summary>The key encoding is injective and every key carries the namespace's hash tag -- the two properties the whole key space rests on.</summary>
public sealed class RedisKeySpaceTests
{
    private readonly RedisKeySpace _keys = new("unit");

    [Fact]
    public void EveryKey_StartsWithTheNamespacesHashTag()
    {
        var id = Guid.NewGuid();
        RedisKey[] keys =
        [
            _keys.Meta, _keys.Torn, _keys.Nil, _keys.Saga(id, "OrderSaga"), _keys.BusinessKey("OrderSaga", "ORD-1"), _keys.Log(id, "OrderSaga"),
            _keys.Dedupe(id, "OrderSaga"), _keys.TimeoutSequence, _keys.TimeoutRow(7), _keys.TimeoutDue, _keys.TimeoutFor("OrderSaga", id, "Waiting"),
            _keys.OutboxRow("m1"), _keys.OutboxPending, _keys.OutboxSequence, _keys.IndexUpdated, _keys.IndexStatus(SagaStatus.Failed),
            _keys.IndexKind(SagaKind.Choreographed), _keys.IndexType("OrderSaga"), _keys.IndexParent("Parent", id), _keys.IndexCorrelation(id),
            _keys.IndexTypes, _keys.IndexTypeCounts, _keys.Topology,
        ];

        Assert.All(keys, key => Assert.StartsWith("{vsaga:unit}:", key.ToString(), StringComparison.Ordinal));
        Assert.Equal(keys.Length, keys.Select(k => k.ToString()).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>A saga type may contain the separator; the GUID's fixed width is what keeps the member string unambiguous.</summary>
    [Fact]
    public void Member_RoundTrips_EvenWhenTheSagaTypeContainsTheSeparator()
    {
        var id = Guid.NewGuid();
        var member = RedisKeySpace.Member(id, "Order|Saga|v2");

        Assert.Equal((id, "Order|Saga|v2"), RedisKeySpace.ParseMember(member));
    }

    /// <summary>Length-prefixing makes the composite keys injective: moving the separator between the two components must change the key.</summary>
    [Fact]
    public void CompositeKeys_WithUserSuppliedComponents_NeverCollide()
    {
        var id = Guid.NewGuid();

        Assert.NotEqual(_keys.BusinessKey("a|b", "c"), _keys.BusinessKey("a", "b|c"));
        Assert.NotEqual(_keys.TimeoutFor("a|b", id, "c"), _keys.TimeoutFor("a", id, "b|c"));
        Assert.NotEqual(RedisKeySpace.TopologyField("a|b", "c"), RedisKeySpace.TopologyField("a", "b|c"), StringComparer.Ordinal);
    }

    /// <summary>Row ids are members of a sorted set, whose ties order lexicographically; zero-padding keeps that equal to numeric order.</summary>
    [Fact]
    public void PadId_OrdersLexicographicallyAsNumerically()
    {
        long[] ids = [1, 9, 10, 99, 100, 1_000_000_000_000];
        var padded = ids.Select(RedisKeySpace.PadId).ToList();

        Assert.Equal(padded, padded.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a{b")]
    [InlineData("a}b")]
    public void Constructor_RejectsANamespaceThatWouldBreakTheHashTag(string ns) =>
        Assert.Throws<ArgumentException>(() => new RedisKeySpace(ns));

    [Fact]
    public void Timestamps_StoreMicroseconds_FlooredAndReadBackExactly()
    {
        var instant = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1_234_567);

        var stored = RedisTimestamps.ToMicroseconds(instant);
        var read = RedisTimestamps.FromMicroseconds(stored);

        Assert.True(read <= instant);
        Assert.True(instant - read < TimeSpan.FromMicroseconds(1));
        Assert.Equal(read, RedisTimestamps.FromMicroseconds(RedisTimestamps.ToMicroseconds(read)));
    }

    /// <summary>Search matches each of the member's two fields on its own; a term straddling the separator matches neither.</summary>
    [Fact]
    public void Search_MatchesEachFieldIndependently_CaseInsensitively()
    {
        var member = RedisKeySpace.Member(Guid.Parse("12345678-9012-3456-7890-123456789012"), "OrderSaga");

        Assert.True(RedisListQuery.Matches(member, "ordersaga"));
        Assert.True(RedisListQuery.Matches(member, "9012-3456"));
        Assert.False(RedisListQuery.Matches(member, "9012|Order"));
        Assert.False(RedisListQuery.Matches(member, "Invoice"));
    }
}
