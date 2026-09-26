using System.Net;
using VSaga.Abstractions.Persistence;
using VSaga.Persistence.Redis;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// The Redis provider refuses a search that would scan more index members than its bound, and that
/// refusal is the caller's to act on: the list endpoint maps it to 400 with the provider's message,
/// rather than letting it surface as a 500. Driven through a reader stub so the case needs no Redis.
/// </summary>
public sealed class SearchScanLimitTests : IAsyncDisposable
{
    private readonly DashboardApiFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task ListSagas_WhenTheProviderRefusesTheSearchScan_Returns400WithItsMessage()
    {
        using var client = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISagaSummaryReader>();
            services.AddSingleton<ISagaSummaryReader>(new RefusingSummaryReader());
        })).CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", DashboardApiFactory.TestApiKey);

        var response = await client.GetAsync("/api/sagas?search=order");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("MaxSearchScanMembers", body, StringComparison.Ordinal);
        Assert.Contains("250000", body, StringComparison.Ordinal);
    }

    private sealed class RefusingSummaryReader : ISagaSummaryReader
    {
        public Task<PagedResult<SagaSummary>> ListAsync(SagaListFilter filter, CancellationToken cancellationToken = default) =>
            throw new RedisSearchScanLimitExceededException(candidates: 250_000, limit: 100_000);

        public Task<SagaSummary?> GetAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SagaSummary?>(null);

        public Task<string?> GetDataJsonAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyList<SagaSummary>> FindByCorrelationIdAsync(Guid correlationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SagaSummary>>([]);

        public Task<IReadOnlyList<SagaSummary>> FindChildrenAsync(string parentSagaType, Guid parentCorrelationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SagaSummary>>([]);

        public Task<IReadOnlyList<SagaTypeInfo>> GetSagaTypesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SagaTypeInfo>>([]);
    }
}
