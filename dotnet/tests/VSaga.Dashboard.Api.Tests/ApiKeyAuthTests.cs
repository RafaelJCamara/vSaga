using System.Net;
using System.Net.Http.Headers;

namespace VSaga.Dashboard.Api.Tests;

public sealed class ApiKeyAuthTests : IAsyncLifetime, IAsyncDisposable
{
    private readonly DashboardApiFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    // xunit 2 calls IAsyncLifetime.DisposeAsync, never a test class's IAsyncDisposable.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task ListSagas_WithoutApiKey_Returns401()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/sagas");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ListSagas_WithWrongApiKey_Returns401()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "not-the-right-key");

        var response = await client.GetAsync("/api/sagas");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ListSagas_WithCorrectApiKey_Returns200()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", DashboardApiFactory.TestApiKey);

        var response = await client.GetAsync("/api/sagas");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The SignalR JS client's accessTokenFactory sends the token as `Authorization: Bearer` on the
    // negotiate HTTP call — it can't set custom headers on the WebSocket upgrade itself, but negotiate
    // is a plain POST. Regression coverage for the 401 this handler used to return on that call.
    [Fact]
    public async Task HubNegotiate_WithBearerApiKey_Returns200()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DashboardApiFactory.TestApiKey);

        var response = await client.PostAsync("/hubs/saga/negotiate?negotiateVersion=1", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HubNegotiate_WithoutApiKey_Returns401()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync("/hubs/saga/negotiate?negotiateVersion=1", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HubNegotiate_WithWrongBearerApiKey_Returns401()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-right-key");

        var response = await client.PostAsync("/hubs/saga/negotiate?negotiateVersion=1", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The query string fallback is what the WebSocket/SSE upgrade itself uses (it can't carry a
    // custom Authorization header), but negotiate accepts it too since it's just another HTTP request.
    [Fact]
    public async Task HubNegotiate_WithQueryStringApiKey_Returns200()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync($"/hubs/saga/negotiate?negotiateVersion=1&access_token={DashboardApiFactory.TestApiKey}", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_WithoutApiKey_StillReachable()
    {
        // /health is deliberately left open for infra probes even though every other endpoint requires a key.
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
