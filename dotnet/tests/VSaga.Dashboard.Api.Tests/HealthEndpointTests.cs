using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// Verifies the composition root actually resolves — every AddVSagaXxx registration, SignalR, CORS,
/// and the endpoint mappings — without needing a live Postgres/RabbitMQ (neither connects eagerly at
/// startup), and that /health's persistence and RabbitMQ checks actually detect an unreachable dependency.
/// Points both connection strings at port 1 (nothing listens there, so the OS returns
/// connection-refused immediately — no risk of the test hanging on a real timeout) instead of relying
/// on "no local infra happens to be running" being true. This is the one check in this suite that
/// doesn't need Docker; DB/broker-backed endpoints need a real docker-compose stack, covered by the
/// end-to-end checkpoint instead.
/// </summary>
public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:VSaga"] = "Host=localhost;Port=1;Database=vsaga;Username=postgres;Password=postgres;Timeout=1",
                ["RabbitMq:ConnectionString"] = "amqp://guest:guest@localhost:1/",
            })));

    [Fact]
    public async Task Health_WithUnreachableDependencies_Returns503AndReportsBothChecksUnhealthy()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"unhealthy\"", body, StringComparison.Ordinal);
        Assert.Contains("\"persistence\"", body, StringComparison.Ordinal);
        Assert.Contains("\"rabbitmq\"", body, StringComparison.Ordinal);
    }
}

/// <summary>
/// The other arm of the Persistence:Provider switch: the Redis composition root resolves (no DbContext,
/// no migration at startup, the provider's own health check under the same "persistence" name) and its
/// probe reports an unreachable server as Unhealthy rather than passing over what it could not verify.
/// The short connect timeout keeps the deliberately-unreachable port from stalling the test.
/// </summary>
public sealed class RedisHealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RedisHealthEndpointTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(builder => builder
            // UseSetting, not ConfigureAppConfiguration: Program.cs reads Persistence:Provider from
            // builder.Configuration while composing services, and under minimal hosting the factory applies
            // ConfigureAppConfiguration callbacks only at Build(), after that read. Host settings land first.
            .UseSetting("Persistence:Provider", "Redis")
            .UseSetting("Redis:ConnectionString", "localhost:1,connectTimeout=500,connectRetry=0")
            .UseSetting("Redis:Namespace", "dashboard-tests")
            .UseSetting("RabbitMq:ConnectionString", "amqp://guest:guest@localhost:1/"));

    [Fact]
    public async Task Health_WithAnUnreachableRedis_Returns503AndReportsThePersistenceCheckUnhealthy()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"persistence\"", body, StringComparison.Ordinal);
        // The exact wording depends on how the client surfaces the refused connection (no connected
        // primary, or the connect itself throwing); either way the description names Redis.
        Assert.Contains("Redis", body, StringComparison.Ordinal);
    }
}
