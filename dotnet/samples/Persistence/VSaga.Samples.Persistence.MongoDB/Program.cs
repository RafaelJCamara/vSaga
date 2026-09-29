// Persistence sample: VSaga.Persistence.MongoDB
//
// The checkout saga (VSaga.Samples.Persistence.Common) on MongoDB, through the native driver. Every
// [MongoDB] block below is specific to this provider; the rest of the file is identical in all four
// persistence samples. Start the replica set first (see README.md):
//
//   docker compose -f dotnet/samples/Persistence/VSaga.Samples.Persistence.MongoDB/docker-compose.yml up -d --wait
//   dotnet run --project dotnet/samples/Persistence/VSaga.Samples.Persistence.MongoDB

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VSaga.Persistence.MongoDB;
using VSaga.Samples.Persistence.Common;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.AddCheckoutSample();

// [MongoDB] Register the provider, binding VSagaMongoOptions from the "MongoDb" section (connection
// string and database; one database per service). The server must be a replica set — a single member
// is enough — because a persist that carries outbox rows commits them and the snapshot in one
// multi-document transaction, and this sample's SagaOutboxMode.All makes every publish take that path.
// A second, optional parameter hands you the driver's MongoClientSettings (TLS, pool size, timeouts).
builder.Services.AddVSagaMongoDb(o => builder.Configuration.GetSection("MongoDb").Bind(o));

using var host = builder.Build();
await host.StartAsync();
try
{
    // [MongoDB] Wait for the provider's health check. MongoPersistenceBootstrapper runs in the
    // background: it creates the named indexes (ux_sagaType_businessKey among them), writes the schema
    // marker and probes the topology, retrying until the server is reachable. The check turns Healthy
    // only once the member is a writable primary of a replica set, on MongoDB 6.0 or later, with every
    // index in place.
    await PersistenceReadiness.WaitUntilHealthyAsync(
        host.Services.GetRequiredService<MongoPersistenceHealthCheck>(), TimeSpan.FromSeconds(60), CancellationToken.None);

    await CheckoutDemo.RunAsync(host.Services, "VSaga.Persistence.MongoDB", CancellationToken.None);

    // [MongoDB] The documents outlive this process. Run the sample again and the count keeps growing.
    // README.md has the mongosh queries for the collections and indexes.
    Console.WriteLine("MongoDB: these sagas are still in the database — run the sample again, or query them with mongosh (see README.md).");
}
finally
{
    await host.StopAsync();
}
