using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.MongoDb;
using Testcontainers.Redis;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Dashboard.Api.Endpoints;
using VSaga.Dashboard.Identity.Services;
using VSaga.Persistence.MongoDB;
using VSaga.Persistence.Redis;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// <see cref="ScopedSagaLister"/> over the real Redis and MongoDB readers, registered the way a host registers them
/// (<c>AddVSagaRedis</c>, <c>AddVSagaMongoDb</c>) against a server in a container, with the rows inserted through the
/// providers' own snapshot stores. <see cref="ScopedSagaListerTests"/> covers the lister's rules against a fake
/// reader; these check the part only a real provider decides: which saga types "have run" (the narrowing of a
/// scope longer than the shape's bound), and that the bounds, <c>maxPage</c> included, come out the same on top of
/// each provider. The lister counts the types in the API, so the refusal is the same on EF Core; the live run of
/// 2026-10-14 saw it on Postgres.
/// </summary>
public abstract class ScopedSagaListerProviderTests<TProvider>(TProvider provider) : IClassFixture<TProvider>
    where TProvider : ProviderHost
{
    [Fact]
    public async Task MoreTypesThanAnUnrankedShapeCanMerge_AreRefusedWithMaxPageZero_AndASagaTypeFilterStillWorks()
    {
        await using var host = await provider.StartAsync();
        var types = await host.SeedAsync(typeCount: ScopedSagaLister.MaxUnrankedTypes + 2, perType: 2);
        var scope = SagaTypeScope.Of(types);

        foreach (var shape in UnrankedShapes())
        {
            var refused = await Assert.ThrowsAsync<ScopedSagaListBoundExceededException>(() => host.Lister.ListAsync(shape, scope, CancellationToken.None));
            Assert.Equal(0, refused.MaxPage);
            Assert.Contains($"{ScopedSagaLister.MaxUnrankedTypes + 2} saga types", refused.Message, StringComparison.Ordinal);
        }

        var one = await host.Lister.ListAsync(new SagaListFilter { SagaType = types[3], SortBy = SagaSortColumn.Status, PageSize = 25 }, scope, CancellationToken.None);
        Assert.Equal(2, one.TotalCount);
        Assert.All(one.Items, s => Assert.Equal(types[3], s.SagaType));
    }

    [Fact]
    public async Task TheDefaultShape_ServesTwelveTypes_AndRefusesMoreThanFifty()
    {
        await using var host = await provider.StartAsync();
        var types = await host.SeedAsync(typeCount: ScopedSagaLister.MaxMergedTypes + 1, perType: 1);

        var twelve = await host.Lister.ListAsync(new SagaListFilter { PageSize = 100 }, SagaTypeScope.Of(types.Take(12)), CancellationToken.None);
        Assert.Equal(12, twelve.TotalCount);
        Assert.Equal(12, twelve.Items.Count);

        var refused = await Assert.ThrowsAsync<ScopedSagaListBoundExceededException>(() =>
            host.Lister.ListAsync(new SagaListFilter { PageSize = 100 }, SagaTypeScope.Of(types), CancellationToken.None));
        Assert.Equal(0, refused.MaxPage);
        Assert.Contains($"{ScopedSagaLister.MaxMergedTypes + 1} saga types", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALongScope_IsNarrowedToTheTypesTheProviderSaysHaveRun()
    {
        await using var host = await provider.StartAsync();
        var ran = await host.SeedAsync(typeCount: ScopedSagaLister.MaxUnrankedTypes - 2, perType: 3);

        // Sixty names are more than any shape can merge as they stand, but only eight of them have rows.
        var names = ran.Concat(Enumerable.Range(0, 52).Select(i => $"NeverRan{i:00}")).ToList();
        var scope = SagaTypeScope.Of(names);

        var status = await host.Lister.ListAsync(new SagaListFilter { Status = SagaStatus.Failed, PageSize = 100 }, scope, CancellationToken.None);
        var recent = await host.Lister.ListAsync(new SagaListFilter { PageSize = 100 }, scope, CancellationToken.None);

        Assert.Equal(ran.Count * 3, recent.TotalCount);
        Assert.Equal(host.Seeded.Count(r => r.Status == SagaStatus.Failed), status.TotalCount);
        Assert.All(status.Items, s => Assert.Equal(SagaStatus.Failed, s.Status));
        Assert.All(recent.Items, s => Assert.Contains(s.SagaType, ran, StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheDepthBounds_NameTheLastReachablePage()
    {
        await using var host = await provider.StartAsync();
        var types = await host.SeedAsync(typeCount: 3, perType: 2);
        var scope = SagaTypeScope.Of(types);

        var unranked = await Assert.ThrowsAsync<ScopedSagaListBoundExceededException>(() =>
            host.Lister.ListAsync(new SagaListFilter { Status = SagaStatus.Failed, Page = 21, PageSize = 25 }, scope, CancellationToken.None));
        var ranked = await Assert.ThrowsAsync<ScopedSagaListBoundExceededException>(() =>
            host.Lister.ListAsync(new SagaListFilter { Page = 101, PageSize = 100 }, scope, CancellationToken.None));

        Assert.Equal(20, unranked.MaxPage);
        Assert.Equal(100, ranked.MaxPage);
    }

    private static IEnumerable<SagaListFilter> UnrankedShapes() =>
    [
        new() { SortBy = SagaSortColumn.Status, PageSize = 25 },
        new() { Status = SagaStatus.Failed, PageSize = 25 },
        new() { Kind = SagaKind.Orchestrated, PageSize = 25 },
        new() { Search = "Type", PageSize = 25 },
    ];
}

/// <summary>A provider under test: a server in a container, and a fresh key space or database per case.</summary>
public abstract class ProviderHost : IAsyncLifetime
{
    public abstract Task InitializeAsync();

    public abstract Task DisposeAsync();

    /// <summary>A lister over a new, empty namespace or database of the shared server, started the way a host starts it.</summary>
    public abstract Task<ListerHost> StartAsync();
}

/// <summary>One case's provider registration, with its lister and the rows it holds.</summary>
public sealed class ListerHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly IReadOnlyList<IHostedService> _started;
    private readonly AsyncServiceScope _readerScope;

    public ListerHost(ServiceProvider services, IReadOnlyList<IHostedService> started)
    {
        _services = services;
        _started = started;
        _readerScope = services.CreateAsyncScope();
        Lister = new ScopedSagaLister(_readerScope.ServiceProvider.GetRequiredService<ISagaSummaryReader>(), new SagaTypeNameCache(TimeProvider.System));
    }

    public ScopedSagaLister Lister { get; }

    /// <summary>Every row inserted so far, as inserted.</summary>
    private readonly List<SeedState> _seeded = [];

    public IReadOnlyList<SeedState> Seeded => _seeded;

    /// <summary>
    /// <paramref name="typeCount"/> saga types of <paramref name="perType"/> rows each, inserted through the
    /// provider's snapshot store, a third of them <see cref="SagaStatus.Failed"/>. The type names are returned in
    /// ordinal order.
    /// </summary>
    public async Task<IReadOnlyList<string>> SeedAsync(int typeCount, int perType)
    {
        await using var scope = _services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISagaSnapshotStore<SeedState>>();
        var names = new List<string>();
        for (var t = 0; t < typeCount; t++)
        {
            var name = $"Type{t:00}";
            names.Add(name);
            for (var n = 0; n < perType; n++)
            {
                var state = new SeedState
                {
                    CorrelationId = Guid.NewGuid(),
                    SagaType = name,
                    CurrentState = "Done",
                    Status = (t + n) % 3 == 0 ? SagaStatus.Failed : SagaStatus.Completed,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
                await store.InsertAsync(state);
                _seeded.Add(state);
            }
        }

        return names;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var service in _started)
            await service.StopAsync(CancellationToken.None);
        await _readerScope.DisposeAsync();
        await _services.DisposeAsync();
    }
}

public sealed class SeedState : SagaState;

public sealed class RedisListerProvider : ProviderHost
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7.4-alpine")
        .WithCommand("redis-server", "--appendonly", "yes", "--appendfsync", "everysec", "--maxmemory-policy", "noeviction")
        .Build();

    private int _cases;

    public override Task InitializeAsync() => _container.StartAsync();

    public override Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public override async Task<ListerHost> StartAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddVSagaRedis(o =>
        {
            o.ConnectionString = _container.GetConnectionString();
            o.Namespace = "lister-" + Interlocked.Increment(ref _cases);
        });

        var provider = services.BuildServiceProvider();
        var started = new List<IHostedService>(provider.GetServices<IHostedService>());
        foreach (var service in started)
            await service.StartAsync(CancellationToken.None);
        return new ListerHost(provider, started);
    }
}

public sealed class MongoListerProvider : ProviderHost
{
    private readonly MongoDbContainer _container = new MongoDbBuilder("mongo:8.0").WithReplicaSet("rs0").Build();
    private int _cases;

    public override Task InitializeAsync() => _container.StartAsync();

    public override Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public override async Task<ListerHost> StartAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddVSagaMongoDb(o =>
        {
            o.ConnectionString = _container.GetConnectionString();
            o.DatabaseName = "lister-" + Interlocked.Increment(ref _cases);
        });

        var provider = services.BuildServiceProvider();
        var started = new List<IHostedService>(provider.GetServices<IHostedService>());
        foreach (var service in started)
            await service.StartAsync(CancellationToken.None);
        return new ListerHost(provider, started);
    }
}

public sealed class ScopedSagaListerOverRedisTests(RedisListerProvider provider) : ScopedSagaListerProviderTests<RedisListerProvider>(provider);

public sealed class ScopedSagaListerOverMongoTests(MongoListerProvider provider) : ScopedSagaListerProviderTests<MongoListerProvider>(provider);
