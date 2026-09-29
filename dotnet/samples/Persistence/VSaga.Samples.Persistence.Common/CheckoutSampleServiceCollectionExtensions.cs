using Microsoft.Extensions.DependencyInjection;
using VSaga.Core;
using VSaga.Core.Runtime;
using VSaga.Transport.InMemory;

namespace VSaga.Samples.Persistence.Common;

public static class CheckoutSampleServiceCollectionExtensions
{
    /// <summary>
    /// Everything except persistence: the in-memory transport, the saga engine running
    /// <see cref="CheckoutSaga"/>, and the <see cref="PaymentGateway"/> participant. Each
    /// VSaga.Samples.Persistence.&lt;Provider&gt; host calls this and then registers exactly one
    /// persistence provider — the only line that differs between the four samples.
    /// </summary>
    /// <remarks>
    /// The transport is in-memory in every sample so that no broker is needed and the persistence
    /// provider is the only moving part. <see cref="SagaOutboxMode.All"/> routes the saga's publishes
    /// through the outbox, so each provider's atomic snapshot-plus-outbox commit actually runs: EF Core's
    /// SaveChanges, MongoDB's multi-document transaction, Redis's persist script. The in-memory store
    /// commits outbox rows immediately instead (see docs/persistence.md, "In-memory").
    /// </remarks>
    public static IServiceCollection AddCheckoutSample(this IServiceCollection services)
    {
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(o => o
            .ConfigureOutbox(outbox => outbox.Mode = SagaOutboxMode.All)
            .AddSaga<CheckoutSaga, CheckoutState>());
        services.AddHostedService<PaymentGateway>();
        return services;
    }
}
