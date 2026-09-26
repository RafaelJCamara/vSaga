using Testcontainers.Redis;

namespace VSaga.Persistence.Redis.Tests;

/// <summary>
/// The probe against servers configured the way the provider must refuse to trust: each starts its
/// own container, since the shared fixture's is deliberately correct. Every verdict names the broken
/// guarantee -- or says it could not be verified, which is never reported as healthy.
/// </summary>
public sealed class RedisMisconfigurationTests
{
    [Fact]
    public async Task Probe_OnAnEvictingInstance_IsUnhealthyNamingThePolicy()
    {
        var failures = await ProbeAsync("redis-server", "--appendonly", "yes", "--maxmemory-policy", "allkeys-lru");

        var failure = Assert.Single(failures);
        Assert.Contains("maxmemory-policy is 'allkeys-lru'", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_WithoutTheAppendOnlyFile_IsUnhealthyNamingTheGuarantee()
    {
        var failures = await ProbeAsync("redis-server", "--appendonly", "no", "--maxmemory-policy", "noeviction");

        var failure = Assert.Single(failures);
        Assert.Contains("appendonly is 'no'", failure, StringComparison.Ordinal);
    }

    /// <summary>Several managed tiers disable CONFIG. That makes the guarantees unverifiable, and unverifiable is reported as Unhealthy, not passed over.</summary>
    [Fact]
    public async Task Probe_WhenConfigGetIsDisabled_IsUnhealthyAsUnverifiable()
    {
        var failures = await ProbeAsync("redis-server", "--appendonly", "yes", "--maxmemory-policy", "noeviction", "--rename-command", "CONFIG", "");

        var failure = Assert.Single(failures);
        Assert.Contains("CONFIG GET is unavailable", failure, StringComparison.Ordinal);
        Assert.Contains("unverifiable", failure, StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyList<string>> ProbeAsync(params string[] command)
    {
        await using var container = new RedisBuilder(RedisProviderFixture.Image).WithCommand(command).Build();
        await container.StartAsync();

        var options = new VSagaRedisOptions { ConnectionString = container.GetConnectionString(), Namespace = "misconfigured" };
        await using var connection = new RedisConnection(options);
        var keys = new RedisKeySpace(options.Namespace);
        var db = await connection.GetDatabaseAsync();
        await db.HashSetAsync(keys.Meta, RedisPersistenceBootstrapper.SchemaVersionField, RedisPersistenceBootstrapper.SchemaVersion);

        var report = await new RedisServerProbe(connection, keys, options).ProbeAsync();
        return report.Failures;
    }
}
