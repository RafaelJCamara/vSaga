using System.Text;
using VSaga.Abstractions.Persistence;

namespace VSaga.Core.Tests;

/// <summary>
/// <see cref="SagaStateSnapshot"/> is the one definition of the <see cref="SagaEntryType.StatePersisted"/>
/// entry and its size marker, shared by the engine and the dashboard. The SPA recognises the marker by its
/// <c>$vsagaStateOmitted</c> key and shows its <c>bytes</c>/<c>limit</c>, so the text is pinned exactly.
/// </summary>
public sealed class SagaStateSnapshotTests
{
    [Fact]
    public void ToPayload_UnderTheCap_ReturnsTheBlobUnchanged()
    {
        const string stateJson = "{\"Sku\":\"A\",\"Version\":3}";

        Assert.Equal(stateJson, SagaStateSnapshot.ToPayload(stateJson, 1024));
    }

    [Fact]
    public void ToPayload_ExactlyAtTheCap_ReturnsTheBlobUnchanged()
    {
        const string stateJson = "{\"Sku\":\"A\",\"Version\":3}";

        Assert.Equal(stateJson, SagaStateSnapshot.ToPayload(stateJson, Encoding.UTF8.GetByteCount(stateJson)));
    }

    [Fact]
    public void ToPayload_OneByteOverTheCap_ReturnsTheMarker()
    {
        const string stateJson = "{\"Sku\":\"A\",\"Version\":3}";
        var limit = stateJson.Length - 1;

        Assert.Equal($"{{\"$vsagaStateOmitted\":true,\"bytes\":{stateJson.Length},\"limit\":{limit}}}",
            SagaStateSnapshot.ToPayload(stateJson, limit));
    }

    /// <summary>
    /// The cap counts UTF-8 bytes, not UTF-16 chars: one e-acute is a single char but two bytes, so a
    /// blob whose char count equals the cap is still one byte over it.
    /// </summary>
    [Fact]
    public void ToPayload_CountsUtf8BytesRatherThanChars()
    {
        var stateJson = "{\"Name\":\"Ren" + (char)0x00E9 + "\"}";
        Assert.Equal(15, stateJson.Length);

        Assert.Equal("{\"$vsagaStateOmitted\":true,\"bytes\":16,\"limit\":15}", SagaStateSnapshot.ToPayload(stateJson, 15));
        Assert.Equal(stateJson, SagaStateSnapshot.ToPayload(stateJson, 16));
    }

    [Fact]
    public void ToPayload_WithACapOfZero_RecordsASizeOnlyMarker()
    {
        Assert.Equal("{\"$vsagaStateOmitted\":true,\"bytes\":2,\"limit\":0}", SagaStateSnapshot.ToPayload("{}", 0));
    }

    [Fact]
    public void CreateEntry_BuildsAStatePersistedEntryWithTheMessageIdentityAndNoStates()
    {
        var correlationId = Guid.NewGuid();
        const string stateJson = "{\"Sku\":\"A\"}";

        var entry = SagaStateSnapshot.CreateEntry(correlationId, "OrderSaga", stateJson, SagaStateSnapshot.DefaultMaxBytes,
            "ReserveInventory", "m-1");

        Assert.Equal(SagaEntryType.StatePersisted, entry.EntryType);
        Assert.Equal(0, entry.SequenceNumber);
        Assert.Equal(correlationId, entry.CorrelationId);
        Assert.Equal("OrderSaga", entry.SagaType);
        Assert.Equal("ReserveInventory", entry.MessageType);
        Assert.Equal("m-1", entry.MessageId);
        Assert.Equal(stateJson, entry.PayloadJson);
        Assert.Null(entry.FromState);
        Assert.Null(entry.ToState);
        Assert.Null(entry.ErrorMessage);
        Assert.Null(entry.CausationId);
    }

    [Fact]
    public void CreateEntry_WithoutAMessageIdentity_AndOverTheCap_CarriesTheMarker()
    {
        var entry = SagaStateSnapshot.CreateEntry(Guid.NewGuid(), "OrderSaga", "{\"Sku\":\"A\"}", 4);

        Assert.Null(entry.MessageType);
        Assert.Null(entry.MessageId);
        Assert.Equal("{\"$vsagaStateOmitted\":true,\"bytes\":11,\"limit\":4}", entry.PayloadJson);
    }

    [Fact]
    public void DefaultMaxBytes_Is256KiB()
    {
        Assert.Equal(256 * 1024, SagaStateSnapshot.DefaultMaxBytes);
    }
}
