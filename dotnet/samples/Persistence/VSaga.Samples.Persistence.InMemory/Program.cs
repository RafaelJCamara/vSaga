// Persistence sample: VSaga.Persistence.InMemory
//
// The checkout saga (VSaga.Samples.Persistence.Common) on the in-memory store: no database, no Docker,
// nothing to start first. Every [InMemory] block below is specific to this provider; the rest of the
// file is identical in all four persistence samples.
//
//   dotnet run --project dotnet/samples/Persistence/VSaga.Samples.Persistence.InMemory

using Microsoft.Extensions.Hosting;
using VSaga.Persistence.InMemory;
using VSaga.Samples.Persistence.Common;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.AddCheckoutSample();

// [InMemory] Register the provider. It takes no options: one process-wide InMemorySagaStore singleton
// backs all seven store contracts. Dev/test only — state is gone when the process exits, claims are
// safe only within this one process, and outbox rows commit immediately instead of with the snapshot.
builder.Services.AddVSagaInMemoryPersistence();

using var host = builder.Build();
await host.StartAsync();
try
{
    // [InMemory] No readiness wait: the store is ready the moment the container is built.
    await CheckoutDemo.RunAsync(host.Services, "VSaga.Persistence.InMemory", CancellationToken.None);

    // [InMemory] Nothing survives this process. Run the sample again and the count starts at 0 — the
    // durable samples (EFCore.Postgres, MongoDB, Redis) keep counting up.
    Console.WriteLine("In-memory: everything above is discarded when this process exits.");
}
finally
{
    await host.StopAsync();
}
