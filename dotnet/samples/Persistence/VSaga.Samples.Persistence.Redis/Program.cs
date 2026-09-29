// Persistence sample: VSaga.Persistence.Redis
//
// The checkout saga (VSaga.Samples.Persistence.Common) on Redis: core data types and server-side Lua,
// no modules, so Valkey works too. Every [Redis] block below is specific to this provider; the rest of
// the file is identical in all four persistence samples. Start Redis first (see README.md):
//
//   docker compose -f dotnet/samples/Persistence/VSaga.Samples.Persistence.Redis/docker-compose.yml up -d --wait
//   dotnet run --project dotnet/samples/Persistence/VSaga.Samples.Persistence.Redis

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VSaga.Persistence.Redis;
using VSaga.Samples.Persistence.Common;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.AddCheckoutSample();

// [Redis] Register the provider, binding VSagaRedisOptions from the "Redis" section. ConnectionString is
// StackExchange.Redis's own format (host:port,password=...), not an ADO.NET one. Namespace prefixes
// every key as {vsaga:<Namespace>}: — the braces are a hash tag that keeps the whole key space on one
// slot. A prefix is not a security boundary: the server must be dedicated, never a shared cache.
// A second, optional parameter hands you StackExchange.Redis's ConfigurationOptions (TLS, retries).
builder.Services.AddVSagaRedis(o => builder.Configuration.GetSection("Redis").Bind(o));

using var host = builder.Build();
await host.StartAsync();
try
{
    // [Redis] Wait for the provider's health check. RedisPersistenceBootstrapper runs in the background,
    // retrying until Redis is reachable; the check turns Healthy only once the server's configuration
    // has been verified: appendonly yes, maxmemory-policy noeviction, not Cluster, a primary, Lua
    // available. Its description names the durability tier the server is running at (see README.md).
    await PersistenceReadiness.WaitUntilHealthyAsync(
        host.Services.GetRequiredService<RedisPersistenceHealthCheck>(), TimeSpan.FromSeconds(60), CancellationToken.None);

    await CheckoutDemo.RunAsync(host.Services, "VSaga.Persistence.Redis", CancellationToken.None);

    // [Redis] The keys outlive this process (the AOF is on a volume). Run the sample again and the
    // count keeps growing. README.md has the redis-cli commands for the key space.
    Console.WriteLine("Redis: these sagas are still in Redis — run the sample again, or inspect the keys with redis-cli (see README.md).");
}
finally
{
    await host.StopAsync();
}
