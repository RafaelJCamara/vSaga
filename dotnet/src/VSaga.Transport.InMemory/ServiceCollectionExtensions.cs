using VSaga.Abstractions.Transport;
using VSaga.Transport.Common;
using Microsoft.Extensions.DependencyInjection;

namespace VSaga.Transport.InMemory;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-process, no-broker transport used for local dev and VSaga.Testing, wrapped in
    /// the same outbound/inbound middleware pipeline every broker adapter uses — so
    /// <c>AddVSagaChaos</c>'s fault injection actually runs here too, which is where a developer tries
    /// it first. Register <see cref="IOutboundMessageMiddleware"/>/<see cref="IInboundMessageMiddleware"/>
    /// implementations in any order relative to this call; the factory below resolves them lazily.
    /// </summary>
    public static IServiceCollection AddVSagaInMemoryTransport(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryMessageTransport>();

        // A factory registration, not an instance one: TopologyRecordingServiceCollectionExtensions
        // re-wraps the last IMessageTransport descriptor and throws unless it has an ImplementationFactory.
        //
        // The wrap is unconditional, exactly like every broker adapter. Skipping it when no middleware
        // happens to be registered would save nothing (an empty pipeline is a pure pass-through) and
        // would make the resolved type depend on the caller's middleware — so anything holding an
        // InMemoryMessageTransport-typed reference to it would start failing the moment someone added
        // AddVSagaChaos. Code that needs the concrete transport (GetPublished()/Reset()) resolves
        // InMemoryMessageTransport by its own type instead; it is the same singleton instance wrapped here.
        services.AddSingleton<IMessageTransport>(sp => new MiddlewarePipelineTransport(
            sp.GetRequiredService<InMemoryMessageTransport>(),
            sp.GetServices<IOutboundMessageMiddleware>(),
            sp.GetServices<IInboundMessageMiddleware>()));

        return services;
    }
}
