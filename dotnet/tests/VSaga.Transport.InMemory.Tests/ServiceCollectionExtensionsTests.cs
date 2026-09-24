using VSaga.Abstractions.Transport;
using VSaga.Transport.Common;
using Microsoft.Extensions.DependencyInjection;

namespace VSaga.Transport.InMemory.Tests;

/// <summary>
/// docs/transports/index.md and docs/chaos.md claim every adapter is wrapped in
/// <see cref="MiddlewarePipelineTransport"/> so chaos works identically everywhere with zero
/// adapter-specific code. The in-memory transport silently failed that claim — it registered itself
/// directly as <see cref="IMessageTransport"/>, so <c>AddVSagaChaos</c>'s middlewares were never
/// invoked on the one transport a developer tries chaos on first.
/// </summary>
public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void ConcreteTransportStaysResolvableOnItsOwn()
    {
        var services = new ServiceCollection();
        services.AddVSagaInMemoryTransport();
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<InMemoryMessageTransport>());
    }

    /// <summary>
    /// TopologyRecordingServiceCollectionExtensions re-wraps the last <see cref="IMessageTransport"/>
    /// descriptor and throws unless it carries an <c>ImplementationFactory</c> — an instance or
    /// open-type registration here would break <c>AddVSagaTopologyRecording</c> with no compile error.
    /// </summary>
    [Fact]
    public void RegistersIMessageTransportAsAFactoryDescriptor()
    {
        var services = new ServiceCollection();
        services.AddVSagaInMemoryTransport();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IMessageTransport));
        Assert.NotNull(descriptor.ImplementationFactory);
    }

    /// <summary>
    /// The wrap is unconditional, not "only once some middleware exists". A registration that handed
    /// back the bare transport while no middleware happened to be registered would make the resolved
    /// type depend on the caller's service collection — so a <c>SagaTestHarness</c> test would change
    /// shape underneath itself the moment it added <c>AddVSagaChaos</c>, which is the exact
    /// combination the pipeline is here to support.
    /// </summary>
    [Fact]
    public void WithNoMiddleware_StillWrapsInTheMiddlewarePipeline()
    {
        var services = new ServiceCollection();
        services.AddVSagaInMemoryTransport();
        using var provider = services.BuildServiceProvider();

        var transport = provider.GetRequiredService<IMessageTransport>();
        Assert.IsType<MiddlewarePipelineTransport>(transport);
        Assert.NotSame(provider.GetRequiredService<InMemoryMessageTransport>(), transport);
    }

    /// <summary>
    /// What makes resolving <see cref="InMemoryMessageTransport"/> by its own type a valid substitute
    /// for the old cast: the pipeline wraps that very singleton, so traffic sent through
    /// <see cref="IMessageTransport"/> lands in the concrete transport's own
    /// <c>GetPublished()</c> — the list <c>SagaTestHarness</c> and the <c>VSaga.Core.Tests</c>
    /// fixtures assert on.
    /// </summary>
    [Fact]
    public async Task PublishThroughTheWrapperIsRecordedOnTheConcreteSingleton()
    {
        var services = new ServiceCollection();
        services.AddVSagaInMemoryTransport();
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IMessageTransport>()
            .PublishAsync(new PingMessage("hello"), MessageEnvelope.New(Guid.NewGuid()));

        var published = Assert.Single(provider.GetRequiredService<InMemoryMessageTransport>().GetPublished());
        Assert.Equal(new PingMessage("hello"), Assert.IsType<PingMessage>(published.Message));
    }

    [Fact]
    public void WithMiddlewareRegistered_WrapsInTheMiddlewarePipeline()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOutboundMessageMiddleware>(new RecordingOutboundMiddleware());
        services.AddVSagaInMemoryTransport();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<MiddlewarePipelineTransport>(provider.GetRequiredService<IMessageTransport>());
    }

    /// <summary>Registration order must not matter: the factory resolves middleware lazily, so a middleware added after AddVSagaInMemoryTransport is still picked up.</summary>
    [Fact]
    public async Task OutboundMiddlewareRunsOnPublish_EvenWhenRegisteredAfterTheTransport()
    {
        var outbound = new RecordingOutboundMiddleware();

        var services = new ServiceCollection();
        services.AddVSagaInMemoryTransport();
        services.AddSingleton<IOutboundMessageMiddleware>(outbound);
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IMessageTransport>()
            .PublishAsync(new PingMessage("hello"), MessageEnvelope.New(Guid.NewGuid()));

        Assert.Equal(1, outbound.InvokeCount);
        Assert.Equal("publish", outbound.LastDestinationHint);
    }

    [Fact]
    public async Task OutboundMiddlewareSeesTheDestinationOnSend()
    {
        var outbound = new RecordingOutboundMiddleware();

        var services = new ServiceCollection();
        services.AddSingleton<IOutboundMessageMiddleware>(outbound);
        services.AddVSagaInMemoryTransport();
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IMessageTransport>()
            .SendAsync("vsaga.test.inventory", new PingMessage("hello"), MessageEnvelope.New(Guid.NewGuid()));

        Assert.Equal(1, outbound.InvokeCount);
        Assert.Equal("vsaga.test.inventory", outbound.LastDestinationHint);
    }

    [Fact]
    public async Task InboundMiddlewareRunsOnDelivery()
    {
        var inbound = new RecordingInboundMiddleware();

        var services = new ServiceCollection();
        services.AddSingleton<IInboundMessageMiddleware>(inbound);
        services.AddVSagaInMemoryTransport();
        using var provider = services.BuildServiceProvider();

        var transport = provider.GetRequiredService<IMessageTransport>();
        var delivered = 0;

        var subscription = new TransportSubscription("TestConsumer", [typeof(PingMessage)], "vsaga.test.inbound-queue");
        using var handle = await transport.SubscribeAsync(subscription, (_, _) =>
        {
            delivered++;
            return Task.CompletedTask;
        });

        await transport.PublishAsync(new PingMessage("hello"), MessageEnvelope.New(Guid.NewGuid()));

        Assert.Equal(1, inbound.InvokeCount);
        Assert.Equal(1, delivered);
    }

    /// <summary>The behaviour an unwrapped transport made impossible: chaos's DropInboundMiddleware suppressing a delivery.</summary>
    [Fact]
    public async Task InboundMiddlewareCanSuppressDelivery()
    {
        var inbound = new RecordingInboundMiddleware(suppress: true);

        var services = new ServiceCollection();
        services.AddSingleton<IInboundMessageMiddleware>(inbound);
        services.AddVSagaInMemoryTransport();
        using var provider = services.BuildServiceProvider();

        var transport = provider.GetRequiredService<IMessageTransport>();
        var delivered = 0;

        var subscription = new TransportSubscription("TestConsumer", [typeof(PingMessage)], "vsaga.test.suppressed-queue");
        using var handle = await transport.SubscribeAsync(subscription, (_, _) =>
        {
            delivered++;
            return Task.CompletedTask;
        });

        await transport.PublishAsync(new PingMessage("hello"), MessageEnvelope.New(Guid.NewGuid()));

        Assert.Equal(1, inbound.InvokeCount);
        Assert.Equal(0, delivered);
    }
}
