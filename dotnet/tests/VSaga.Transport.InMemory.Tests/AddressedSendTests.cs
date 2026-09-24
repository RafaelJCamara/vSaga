using System.Text.Json;
using VSaga.Abstractions.Transport;

namespace VSaga.Transport.InMemory.Tests;

/// <summary>
/// docs/transports/index.md claims every adapter does a real addressed send. This transport used to
/// match on message type alone, so <c>SendAsync</c>/<c>SendRawAsync</c> fanned out to every subscriber
/// of the type — meaning a send-isolation test written against <c>SagaTestHarness</c> passed here and
/// failed against every real broker. These pin the narrowing: a send reaches only the subscription
/// whose <see cref="TransportSubscription.QueueNameHint"/> is the destination.
/// </summary>
public sealed class AddressedSendTests
{
    private const string InventoryQueue = "vsaga.test.inventory";
    private const string ShippingQueue = "vsaga.test.shipping";

    [Fact]
    public async Task Send_DeliversOnlyToTheSubscriptionNamingThatQueue()
    {
        var transport = new InMemoryMessageTransport();
        var inventory = 0;
        var shipping = 0;

        using var inventoryHandle = await Subscribe(transport, InventoryQueue, () => inventory++);
        using var shippingHandle = await Subscribe(transport, ShippingQueue, () => shipping++);

        await transport.SendAsync(InventoryQueue, new PingMessage("addressed"), MessageEnvelope.New(Guid.NewGuid()));

        Assert.Equal(1, inventory);
        Assert.Equal(0, shipping);
    }

    [Fact]
    public async Task SendRaw_DeliversOnlyToTheSubscriptionNamingThatQueue()
    {
        var transport = new InMemoryMessageTransport();
        var inventory = 0;
        var shipping = 0;

        using var inventoryHandle = await Subscribe(transport, InventoryQueue, () => inventory++);
        using var shippingHandle = await Subscribe(transport, ShippingQueue, () => shipping++);

        var body = JsonSerializer.SerializeToUtf8Bytes(new PingMessage("addressed-raw"));
        await transport.SendRawAsync(ShippingQueue, nameof(PingMessage), body, MessageEnvelope.New(Guid.NewGuid()));

        Assert.Equal(0, inventory);
        Assert.Equal(1, shipping);
    }

    /// <summary>
    /// No broker here to return the message as unroutable the way <c>RabbitMqTransport</c> does, so an
    /// unmatched destination is a silent no-op rather than a throw — <c>VSaga.Core.Tests</c>' Mode=All
    /// suite sends to an "inventory" nobody subscribes and expects the call to succeed.
    /// </summary>
    [Fact]
    public async Task Send_ToAQueueNobodySubscribed_DeliversNothingAndDoesNotThrow()
    {
        var transport = new InMemoryMessageTransport();
        var inventory = 0;

        using var inventoryHandle = await Subscribe(transport, InventoryQueue, () => inventory++);

        await transport.SendAsync("vsaga.test.nobody-here", new PingMessage("lost"), MessageEnvelope.New(Guid.NewGuid()));

        Assert.Equal(0, inventory);

        // Still recorded, so GetPublished()-based assertions (and the outbox's own row shape) are unaffected.
        var published = Assert.Single(transport.GetPublished());
        Assert.Equal("vsaga.test.nobody-here", published.Destination);
    }

    /// <summary>The other half of the contract: a publish is still a broadcast, destination or not.</summary>
    [Fact]
    public async Task Publish_StillFansOutToEverySubscriberOfTheType()
    {
        var transport = new InMemoryMessageTransport();
        var inventory = 0;
        var shipping = 0;

        using var inventoryHandle = await Subscribe(transport, InventoryQueue, () => inventory++);
        using var shippingHandle = await Subscribe(transport, ShippingQueue, () => shipping++);

        await transport.PublishAsync(new PingMessage("broadcast"), MessageEnvelope.New(Guid.NewGuid()));

        Assert.Equal(1, inventory);
        Assert.Equal(1, shipping);
    }

    private static Task<IDisposable> Subscribe(InMemoryMessageTransport transport, string queueName, Action onReceived) =>
        transport.SubscribeAsync(
            new TransportSubscription($"Consumer:{queueName}", [typeof(PingMessage)], queueName),
            (_, _) =>
            {
                onReceived();
                return Task.CompletedTask;
            });
}
