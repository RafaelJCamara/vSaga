using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Dashboard.Api.Endpoints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VSaga.Dashboard.Api.Tests;

public sealed class DashboardTestState : SagaState;

public sealed class SagaEndpointsTests : IAsyncDisposable
{
    // Must match Program.cs's ConfigureHttpJsonOptions (enums as strings) so the client can parse
    // what the server actually sends back.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DashboardApiFactory _factory = new();
    private readonly HttpClient _client;

    public SagaEndpointsTests()
    {
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Api-Key", DashboardApiFactory.TestApiKey);
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return _factory.DisposeAsync();
    }

    /// <summary>
    /// A saga instance is identified by (SagaType, CorrelationId), so seeding has to hand both back —
    /// the correlation id alone can't address the per-instance routes.
    /// </summary>
    private sealed record SeededSaga(string SagaType, Guid CorrelationId);

    private async Task<SeededSaga> SeedSagaAsync(string sagaType, string currentState, SagaStatus status, SagaKind kind = SagaKind.Orchestrated, DateTimeOffset? updatedAtUtc = null)
    {
        var correlationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var store = _factory.Services.GetRequiredService<ISagaSnapshotStore<DashboardTestState>>();

        await store.InsertAsync(new DashboardTestState
        {
            CorrelationId = correlationId,
            SagaType = sagaType,
            Kind = kind,
            CurrentState = currentState,
            Status = status,
            CreatedAtUtc = now,
            UpdatedAtUtc = updatedAtUtc ?? now,
        });

        return new SeededSaga(sagaType, correlationId);
    }

    private Task AppendLogAsync(SagaLogEntry entry) =>
        _factory.Services.GetRequiredService<ISagaEventLogStore>().AppendAsync(entry);

    private Task RecordTopologyAsync(string serviceName, string messageType, string queueName) =>
        _factory.Services.GetRequiredService<IServiceTopologyStore>().RecordAsync(serviceName, messageType, queueName, DateTimeOffset.UtcNow);

    [Fact]
    public async Task ListSagas_WithNoData_ReturnsEmptyPage()
    {
        var result = await _client.GetFromJsonAsync<PagedResult<SagaSummary>>("/api/sagas", JsonOptions);

        Assert.NotNull(result);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task ListSagas_FiltersByStatusKindAndSagaType()
    {
        var sagaType = $"OrderSaga-{Guid.NewGuid():N}";
        await SeedSagaAsync(sagaType, "Completed", SagaStatus.Completed);
        await SeedSagaAsync(sagaType, "Failed", SagaStatus.Failed);
        await SeedSagaAsync($"Other-{Guid.NewGuid():N}", "Failed", SagaStatus.Failed, SagaKind.Choreographed);

        var byType = await _client.GetFromJsonAsync<PagedResult<SagaSummary>>($"/api/sagas?sagaType={sagaType}", JsonOptions);
        Assert.Equal(2, byType!.TotalCount);

        var byStatus = await _client.GetFromJsonAsync<PagedResult<SagaSummary>>($"/api/sagas?sagaType={sagaType}&status=Failed", JsonOptions);
        Assert.Equal(1, byStatus!.TotalCount);
        Assert.Equal(SagaStatus.Failed, byStatus.Items[0].Status);

        var byKind = await _client.GetFromJsonAsync<PagedResult<SagaSummary>>("/api/sagas?kind=Choreographed", JsonOptions);
        Assert.Contains(byKind!.Items, s => s.Kind == SagaKind.Choreographed);
    }

    [Fact]
    public async Task ListSagas_RespectsPaging()
    {
        var sagaType = $"PagingSaga-{Guid.NewGuid():N}";
        for (var i = 0; i < 5; i++)
            await SeedSagaAsync(sagaType, "Running", SagaStatus.Running);

        var page1 = await _client.GetFromJsonAsync<PagedResult<SagaSummary>>($"/api/sagas?sagaType={sagaType}&page=1&pageSize=2", JsonOptions);
        Assert.Equal(2, page1!.Items.Count);
        Assert.Equal(5, page1.TotalCount);

        var page3 = await _client.GetFromJsonAsync<PagedResult<SagaSummary>>($"/api/sagas?sagaType={sagaType}&page=3&pageSize=2", JsonOptions);
        Assert.Single(page3!.Items); // 5 items, page size 2 -> last page has 1
    }

    [Fact]
    public async Task ListSagas_SortsByUpdatedAt_AscendingAndDescending()
    {
        var sagaType = $"SortSaga-{Guid.NewGuid():N}";
        var older = await SeedSagaAsync(sagaType, "Running", SagaStatus.Running, updatedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10));
        var newer = await SeedSagaAsync(sagaType, "Running", SagaStatus.Running, updatedAtUtc: DateTimeOffset.UtcNow);

        var ascending = await _client.GetFromJsonAsync<PagedResult<SagaSummary>>($"/api/sagas?sagaType={sagaType}&sortBy=UpdatedAt&sortDescending=false", JsonOptions);
        Assert.Equal([older.CorrelationId, newer.CorrelationId], ascending!.Items.Select(s => s.CorrelationId));

        var descending = await _client.GetFromJsonAsync<PagedResult<SagaSummary>>($"/api/sagas?sagaType={sagaType}&sortBy=UpdatedAt&sortDescending=true", JsonOptions);
        Assert.Equal([newer.CorrelationId, older.CorrelationId], descending!.Items.Select(s => s.CorrelationId));
    }

    [Fact]
    public async Task ListSagas_SortsByStatus_UsingDomainProgressionNotAlphabetical()
    {
        var sagaType = $"SortSaga-{Guid.NewGuid():N}";
        var failed = await SeedSagaAsync(sagaType, "Failed", SagaStatus.Failed);
        var running = await SeedSagaAsync(sagaType, "Running", SagaStatus.Running);
        var completed = await SeedSagaAsync(sagaType, "Completed", SagaStatus.Completed);

        // Domain progression (Running, Completed, Failed, ...) — alphabetical would put Completed first.
        var result = await _client.GetFromJsonAsync<PagedResult<SagaSummary>>($"/api/sagas?sagaType={sagaType}&sortBy=Status", JsonOptions);

        Assert.Equal([running.CorrelationId, completed.CorrelationId, failed.CorrelationId], result!.Items.Select(s => s.CorrelationId));
    }

    [Fact]
    public async Task ListSagas_SortAppliesAcrossTheWholeResultSet_NotJustTheCurrentPage()
    {
        // Regression test: sorting used to only reorder whatever page the client already had loaded
        // (a frontend-only sort), so page 2 kept showing rows in their original, unsorted server order.
        var sagaType = $"SortSaga-{Guid.NewGuid():N}";
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
            ids.Add((await SeedSagaAsync(sagaType, "Running", SagaStatus.Running, updatedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-i))).CorrelationId);
        // ids[0] is newest (seeded first), ids[4] is oldest (seeded last) — insertion order is the
        // reverse of ascending-by-UpdatedAt order, so a pass here can't be explained by insertion order.

        var page1 = await _client.GetFromJsonAsync<PagedResult<SagaSummary>>($"/api/sagas?sagaType={sagaType}&sortBy=UpdatedAt&sortDescending=false&page=1&pageSize=2", JsonOptions);
        var page2 = await _client.GetFromJsonAsync<PagedResult<SagaSummary>>($"/api/sagas?sagaType={sagaType}&sortBy=UpdatedAt&sortDescending=false&page=2&pageSize=2", JsonOptions);

        Assert.Equal([ids[4], ids[3]], page1!.Items.Select(s => s.CorrelationId));
        Assert.Equal([ids[2], ids[1]], page2!.Items.Select(s => s.CorrelationId));
    }

    [Fact]
    public async Task GetSaga_Existing_ReturnsSummaryAndDataJson()
    {
        var (sagaType, correlationId) = await SeedSagaAsync("OrderSaga", "Submitted", SagaStatus.Running);

        var detail = await _client.GetFromJsonAsync<SagaDetail>($"/api/sagas/{sagaType}/{correlationId}", JsonOptions);

        Assert.NotNull(detail);
        Assert.Equal(correlationId, detail.Summary.CorrelationId);
        Assert.Equal("Submitted", detail.Summary.CurrentState);
        Assert.NotNull(detail.DataJson);
    }

    [Fact]
    public async Task GetSaga_Missing_Returns404()
    {
        var response = await _client.GetAsync($"/api/sagas/OrderSaga/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FindByCorrelationId_ReturnsEverySagaTypeTrackingThatId()
    {
        var correlationId = Guid.NewGuid();
        var store = _factory.Services.GetRequiredService<ISagaSnapshotStore<DashboardTestState>>();
        var now = DateTimeOffset.UtcNow;

        // The same business correlation id tracked by two different saga types — the case that makes
        // a bare correlation id ambiguous, and the reason this endpoint returns a list.
        foreach (var (sagaType, kind) in new[] { ("OrderSaga", SagaKind.Orchestrated), ("ShippingChoreography", SagaKind.Choreographed) })
        {
            await store.InsertAsync(new DashboardTestState
            {
                CorrelationId = correlationId,
                SagaType = sagaType,
                Kind = kind,
                CurrentState = "Running",
                Status = SagaStatus.Running,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        }

        var response = await _client.GetAsync($"/api/correlations/{correlationId}");
        response.EnsureSuccessStatusCode();

        var matches = await response.Content.ReadFromJsonAsync<List<SagaSummary>>(JsonOptions);

        Assert.NotNull(matches);
        Assert.Equal(2, matches.Count);
        Assert.All(matches, m => Assert.Equal(correlationId, m.CorrelationId));
        Assert.Contains(matches, m => string.Equals(m.SagaType, "OrderSaga", StringComparison.Ordinal) && m.Kind == SagaKind.Orchestrated);
        Assert.Contains(matches, m => string.Equals(m.SagaType, "ShippingChoreography", StringComparison.Ordinal) && m.Kind == SagaKind.Choreographed);
    }

    [Fact]
    public async Task GetChildren_ReturnsTheSagasThisOneStarted_UnderTheirOwnCorrelationIds()
    {
        var parent = await SeedSagaAsync("PostShipmentChoreography", "Invoiced", SagaStatus.Running, SagaKind.Choreographed);
        var store = _factory.Services.GetRequiredService<ISagaSnapshotStore<DashboardTestState>>();
        var now = DateTimeOffset.UtcNow;
        var childId = Guid.NewGuid();

        await store.InsertAsync(new DashboardTestState
        {
            CorrelationId = childId,
            SagaType = "InvoiceDeliverySaga",
            CurrentState = "AwaitingDelivery",
            Status = SagaStatus.Running,
            ParentSagaType = parent.SagaType,
            ParentCorrelationId = parent.CorrelationId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });

        var response = await _client.GetAsync($"/api/sagas/{parent.SagaType}/{parent.CorrelationId}/children");
        response.EnsureSuccessStatusCode();

        var children = await response.Content.ReadFromJsonAsync<List<SagaSummary>>(JsonOptions);

        Assert.NotNull(children);
        var child = Assert.Single(children);
        Assert.Equal("InvoiceDeliverySaga", child.SagaType);
        Assert.Equal(childId, child.CorrelationId);
        Assert.Equal(parent.SagaType, child.ParentSagaType);
        Assert.Equal(parent.CorrelationId, child.ParentCorrelationId);

        // The child is not reachable through /api/correlations — it holds a different id. The two
        // endpoints answer different questions, and conflating them is exactly the mistake the
        // dashboard's separate "started by" / "started" strips exist to avoid.
        var byCorrelation = await _client.GetFromJsonAsync<List<SagaSummary>>($"/api/correlations/{parent.CorrelationId}", JsonOptions);
        Assert.NotNull(byCorrelation);
        Assert.DoesNotContain(byCorrelation, s => s.CorrelationId == childId);
    }

    [Fact]
    public async Task GetChildren_ForASagaThatStartedNothing_ReturnsEmptyListRatherThan404()
    {
        // A childless saga and an unknown one both legitimately have no children; distinguishing them
        // is what GET /{sagaType}/{correlationId} is for, so this route deliberately does not 404.
        var parent = await SeedSagaAsync("OrderSaga", "AwaitingPayment", SagaStatus.Running);

        foreach (var url in new[] { $"/api/sagas/{parent.SagaType}/{parent.CorrelationId}/children", $"/api/sagas/OrderSaga/{Guid.NewGuid()}/children" })
        {
            var response = await _client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            Assert.Empty((await response.Content.ReadFromJsonAsync<List<SagaSummary>>(JsonOptions))!);
        }
    }

    [Fact]
    public async Task GetSaga_ForARootSaga_ReportsNoParent()
    {
        var saga = await SeedSagaAsync("OrderSaga", "Submitted", SagaStatus.Running);

        var detail = await _client.GetFromJsonAsync<SagaDetail>($"/api/sagas/{saga.SagaType}/{saga.CorrelationId}", JsonOptions);

        Assert.NotNull(detail);
        Assert.Null(detail.Summary.ParentSagaType);
        Assert.Null(detail.Summary.ParentCorrelationId);
    }

    [Fact]
    public async Task FindByCorrelationId_UnknownId_ReturnsEmptyList()
    {
        var response = await _client.GetAsync($"/api/correlations/{Guid.NewGuid()}");
        response.EnsureSuccessStatusCode();

        var matches = await response.Content.ReadFromJsonAsync<List<SagaSummary>>(JsonOptions);

        Assert.NotNull(matches);
        Assert.Empty(matches);
    }

    [Fact]
    public async Task GetTimeline_ReturnsEntriesInOrder()
    {
        var (sagaType, correlationId) = await SeedSagaAsync("OrderSaga", "AwaitingPayment", SagaStatus.Running);
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.SagaStarted, toState: "Submitted"));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.StepSucceeded, fromState: "Submitted", toState: "AwaitingInventory"));

        var timeline = await _client.GetFromJsonAsync<List<SagaLogEntry>>($"/api/sagas/{sagaType}/{correlationId}/timeline", JsonOptions);

        Assert.NotNull(timeline);
        Assert.Equal(2, timeline.Count);
        Assert.Equal(SagaEntryType.SagaStarted, timeline[0].EntryType);
        Assert.Equal(SagaEntryType.StepSucceeded, timeline[1].EntryType);
    }

    [Fact]
    public async Task GetSagaTypes_ReturnsDistinctSeenTypes()
    {
        var sagaType = $"TypeSaga-{Guid.NewGuid():N}";
        await SeedSagaAsync(sagaType, "A", SagaStatus.Running, SagaKind.Orchestrated);
        await SeedSagaAsync(sagaType, "B", SagaStatus.Completed, SagaKind.Orchestrated);

        var types = await _client.GetFromJsonAsync<List<SagaTypeInfo>>("/api/saga-types", JsonOptions);

        Assert.NotNull(types);
        Assert.Contains(types, t => string.Equals(t.SagaType, sagaType, StringComparison.Ordinal) && t.Kind == SagaKind.Orchestrated);
        // Only one entry per (SagaType, Kind) pair even though two instances share it.
        Assert.Single(types, t => string.Equals(t.SagaType, sagaType, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retry_MissingSaga_Returns404()
    {
        var response = await _client.PostAsync($"/api/sagas/OrderSaga/{Guid.NewGuid()}/retry", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Retry_SagaNotFailedOrTimedOut_Returns409()
    {
        var (sagaType, correlationId) = await SeedSagaAsync("OrderSaga", "AwaitingPayment", SagaStatus.Running);

        var response = await _client.PostAsync($"/api/sagas/{sagaType}/{correlationId}/retry", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Retry_WithNoTimelineHistory_Returns422()
    {
        var (sagaType, correlationId) = await SeedSagaAsync("OrderSaga", "Failed", SagaStatus.Failed);

        var response = await _client.PostAsync($"/api/sagas/{sagaType}/{correlationId}/retry", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Retry_TechnicalFailure_RedrivesTheExactFailedMessageWithAFreshId()
    {
        var (sagaType, correlationId) = await SeedSagaAsync("OrderSaga", "AwaitingInventory", SagaStatus.Failed);
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.SagaStarted,
            toState: "Submitted", messageType: "OrderSubmitted", messageId: "m0", payloadJson: "{\"OrderId\":\"X\"}"));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.StepFailed,
            fromState: "AwaitingInventory", messageType: "ReserveInventory", messageId: "m1",
            payloadJson: "{\"OrderId\":\"X\"}", errorMessage: "boom"));

        var response = await _client.PostAsync($"/api/sagas/{sagaType}/{correlationId}/retry", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains(_factory.Transport.GetPublished(), p =>
            string.Equals(p.MessageTypeName, "ReserveInventory", StringComparison.Ordinal) &&
            p.Envelope.CorrelationId == correlationId &&
            !string.Equals(p.Envelope.MessageId, "m1", StringComparison.Ordinal)); // fresh id, not a re-delivery of the original

        var timeline = await _client.GetFromJsonAsync<List<SagaLogEntry>>($"/api/sagas/{sagaType}/{correlationId}/timeline", JsonOptions);
        Assert.Contains(timeline!, e => e.EntryType == SagaEntryType.ManualRetryRequested);
    }

    [Fact]
    public async Task Retry_BusinessFailureWithNoStepFailed_ResetsToInitialStateAndReplaysTheStartingMessage()
    {
        // No StepFailed entry at all — this saga reached Failed via a normal, successful business
        // transition (e.g. "payment declined"), exactly the case that originally 422'd before the fix.
        var (sagaType, correlationId) = await SeedSagaAsync("OrderSaga", "Failed", SagaStatus.Failed);
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.SagaStarted,
            toState: "Submitted", messageType: "OrderSubmitted", messageId: "m0", payloadJson: "{\"OrderId\":\"X\"}"));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.StepSucceeded,
            fromState: "AwaitingInventory", toState: "Failed", messageType: "InventoryReservationFailed", messageId: "m1"));

        var response = await _client.PostAsync($"/api/sagas/{sagaType}/{correlationId}/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var detail = await _client.GetFromJsonAsync<SagaDetail>($"/api/sagas/{sagaType}/{correlationId}", JsonOptions);
        Assert.Equal("Submitted", detail!.Summary.CurrentState);
        Assert.Equal(SagaStatus.Running, detail.Summary.Status);

        Assert.Contains(_factory.Transport.GetPublished(), p =>
            string.Equals(p.MessageTypeName, "OrderSubmitted", StringComparison.Ordinal) &&
            p.Envelope.CorrelationId == correlationId &&
            !string.Equals(p.Envelope.MessageId, "m0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retry_TimedOutSaga_IsAllowed()
    {
        var (sagaType, correlationId) = await SeedSagaAsync("OrderSaga", "Failed", SagaStatus.TimedOut);
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.SagaStarted,
            toState: "Submitted", messageType: "OrderSubmitted", messageId: "m0", payloadJson: "{\"OrderId\":\"X\"}"));

        var response = await _client.PostAsync($"/api/sagas/{sagaType}/{correlationId}/retry", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Retry_WhenTheResetLosesAConcurrentWriteRace_Returns409()
    {
        // The stub stands in for a saga that advanced between the handler's summary read and its
        // reset. Neither shipped provider throws SagaConcurrencyException from ResetStateAsync yet
        // (the conformance fixes F3/F4 land that), but the endpoint's mapping contract exists now —
        // without it, the first provider to honour the version guard would turn a raced retry into
        // an unhandled 500.
        await using var raced = _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<ISagaAdminStore>();
            services.AddSingleton<ISagaAdminStore, RacedAdminStore>();
        }));
        using var client = raced.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", DashboardApiFactory.TestApiKey);

        var correlationId = Guid.NewGuid();
        await raced.Services.GetRequiredService<ISagaSnapshotStore<DashboardTestState>>().InsertAsync(new DashboardTestState
        {
            CorrelationId = correlationId,
            SagaType = "OrderSaga",
            CurrentState = "Failed",
            Status = SagaStatus.Failed,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        // Only a SagaStarted entry, no StepFailed: the business-failure shape, whose retry resets to
        // the initial state and therefore actually reaches ResetStateAsync.
        await raced.Services.GetRequiredService<ISagaEventLogStore>().AppendAsync(
            SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.SagaStarted,
                toState: "Submitted", messageType: "OrderSubmitted", messageId: "m0", payloadJson: "{\"OrderId\":\"X\"}"));

        var response = await client.PostAsync($"/api/sagas/OrderSaga/{correlationId}/retry", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        // The not-Failed status guard also returns 409 — the body text is what proves this one came
        // from the concurrency mapping.
        Assert.Contains("modified concurrently", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private sealed class RacedAdminStore : ISagaAdminStore
    {
        public Task ResetStateAsync(string sagaType, Guid correlationId, string currentState, SagaStatus status, int expectedVersion, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default) =>
            throw new SagaConcurrencyException(sagaType, correlationId, expectedVersion);
    }

    [Fact]
    public async Task GetMap_UnknownSaga_Returns404()
    {
        var response = await _client.GetAsync($"/api/sagas/OrderSaga/{Guid.NewGuid()}/map");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetMap_WithoutApiKey_Returns401()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/sagas/OrderSaga/{Guid.NewGuid()}/map");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetMap_BuildsStitchedAndUnansweredEdgesWithFailureIndex()
    {
        var (sagaType, correlationId) = await SeedSagaAsync("OrderSaga", "Failed", SagaStatus.Failed);

        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.SagaStarted,
            toState: "Submitted", messageType: "OrderSubmitted", messageId: "m0", payloadJson: "{}", sourceService: "OrderSubmitter"));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.MessagePublished,
            messageType: "ReserveInventory", messageId: "out-1", sourceService: "OrderSaga", causationId: "m0"));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.MessageReceived,
            messageType: "InventoryReserved", messageId: "m1", sourceService: "InventoryService", causationId: "out-1"));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.MessagePublished,
            messageType: "ChargePayment", messageId: "out-2", sourceService: "OrderSaga", causationId: "m1"));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.TimeoutFired, fromState: "AwaitingPayment"));

        await RecordTopologyAsync("PaymentService", "ChargePayment", "vsaga.participant.payment");

        var map = await _client.GetFromJsonAsync<SagaMap>($"/api/sagas/{sagaType}/{correlationId}/map", JsonOptions);

        Assert.NotNull(map);
        Assert.Contains(map.Nodes, n => string.Equals(n.Id, "OrderSaga", StringComparison.Ordinal) && n.Kind == SagaMapNodeKind.Orchestrator);
        Assert.Contains(map.Nodes, n => string.Equals(n.Id, "OrderSubmitter", StringComparison.Ordinal) && n.Kind == SagaMapNodeKind.Initiator);
        Assert.Contains(map.Nodes, n => string.Equals(n.Id, "InventoryService", StringComparison.Ordinal) && n.Kind == SagaMapNodeKind.Participant);
        Assert.Contains(map.Nodes, n => string.Equals(n.Id, "PaymentService", StringComparison.Ordinal) && n.Kind == SagaMapNodeKind.Participant);

        var stitched = Assert.Single(map.Edges, e => string.Equals(e.MessageType, "InventoryReserved", StringComparison.Ordinal));
        Assert.Equal("InventoryService", stitched.FromNodeId);
        Assert.Equal("OrderSaga", stitched.ToNodeId);
        Assert.False(stitched.Unanswered);

        var unanswered = Assert.Single(map.Edges, e => string.Equals(e.MessageType, "ChargePayment", StringComparison.Ordinal));
        Assert.Equal("OrderSaga", unanswered.FromNodeId);
        Assert.Equal("PaymentService", unanswered.ToNodeId);
        Assert.True(unanswered.Unanswered);

        Assert.NotNull(map.FailureEventIndex);
        var failureEvent = map.Events[map.FailureEventIndex.Value];
        Assert.Equal(SagaEntryType.TimeoutFired, failureEvent.EntryType);
    }

    [Fact]
    public async Task GetMap_BusinessFailureWithNoStepFailedEntry_StillMarksTheTriggeringEdgeFailed()
    {
        // "Declined payment" shape: the saga reaches Failed via a normal, successful step transition
        // (PaymentFailed -> Compensate -> Finalize(Failed)) — there's no StepFailed entry at all, so
        // the failing hop must be found a different way (the last inbound message before SagaCompleted).
        var (sagaType, correlationId) = await SeedSagaAsync("OrderSaga", "Failed", SagaStatus.Failed);

        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.SagaStarted,
            toState: "Submitted", messageType: "OrderSubmitted", messageId: "m0", payloadJson: "{}", sourceService: "OrderSubmitter"));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.MessagePublished,
            messageType: "ChargePayment", messageId: "out-1", sourceService: "OrderSaga", causationId: "m0"));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.MessageReceived,
            messageType: "PaymentFailed", messageId: "m1", sourceService: "PaymentService", causationId: "out-1"));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.CompensationStarted));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.MessagePublished,
            messageType: "ReleaseInventory", messageId: "out-2", sourceService: "OrderSaga", causationId: "m1"));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.CompensationStepSucceeded, fromState: "AwaitingInventory"));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.SagaCompleted, toState: "Failed"));

        var map = await _client.GetFromJsonAsync<SagaMap>($"/api/sagas/{sagaType}/{correlationId}/map", JsonOptions);

        var failedEdge = Assert.Single(map!.Edges, e => string.Equals(e.MessageType, "PaymentFailed", StringComparison.Ordinal));
        Assert.True(failedEdge.Failed);

        var compensationEdge = Assert.Single(map.Edges, e => string.Equals(e.MessageType, "ReleaseInventory", StringComparison.Ordinal));
        Assert.False(compensationEdge.Failed);
        Assert.True(compensationEdge.IsCompensation);
        Assert.Null(map.FailureEventIndex); // no StepFailed/TimeoutFired/DeliveryExhausted entry exists for this failure shape
    }

    /// <summary>
    /// docs/design/mixed-sagas.md §6: a compensating REST call's reply is an inbound entry (MessageReceived),
    /// and before this fix ProcessInboundEntry hardcoded IsCompensation: false for every inbound edge --
    /// invisible until now because no compensation in this repo produced an inbound timeline entry (a
    /// broker participant never replies to a compensating command in the existing sample).
    /// </summary>
    [Fact]
    public async Task GetMap_CompensatingReplyLoggedAfterCompensationStarted_RendersTheInboundEdgeAsCompensation()
    {
        var (sagaType, correlationId) = await SeedSagaAsync("OrderSaga", "Failed", SagaStatus.Failed);

        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.SagaStarted,
            toState: "Submitted", messageType: "OrderSubmitted", messageId: "m0", payloadJson: "{}"));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.CompensationStarted));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.MessagePublished,
            messageType: "POST /payments/void", messageId: "out-void-1", sourceService: "OrderSaga", destinationService: "PaymentGateway"));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.MessageReceived,
            messageType: "PaymentVoided", messageId: "in-void-1", sourceService: "PaymentGateway", causationId: "out-void-1"));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.CompensationStepSucceeded, fromState: "AwaitingInventory"));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.SagaCompleted, toState: "Failed"));

        var map = await _client.GetFromJsonAsync<SagaMap>($"/api/sagas/{sagaType}/{correlationId}/map", JsonOptions);

        var outboundEdge = Assert.Single(map!.Edges, e => string.Equals(e.MessageType, "POST /payments/void", StringComparison.Ordinal));
        Assert.True(outboundEdge.IsCompensation);

        var inboundEdge = Assert.Single(map.Edges, e => string.Equals(e.MessageType, "PaymentVoided", StringComparison.Ordinal));
        Assert.True(inboundEdge.IsCompensation); // the fix: previously hardcoded false for every inbound edge
    }

    [Fact]
    public async Task GetMap_UnstitchedDestinationWithNoTopologyEntry_RendersAsUnresolvedPlaceholder()
    {
        var (sagaType, correlationId) = await SeedSagaAsync("OrderSaga", "Failed", SagaStatus.Failed);
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.SagaStarted,
            toState: "Submitted", messageType: "OrderSubmitted", messageId: "m0", payloadJson: "{}"));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.MessagePublished,
            messageType: "ReserveInventory", messageId: "out-1", sourceService: "OrderSaga", causationId: "m0"));

        var map = await _client.GetFromJsonAsync<SagaMap>($"/api/sagas/{sagaType}/{correlationId}/map", JsonOptions);

        var edge = Assert.Single(map!.Edges);
        Assert.True(edge.Unanswered);
        var destination = map.Nodes.Single(n => string.Equals(n.Id, edge.ToNodeId, StringComparison.Ordinal));
        Assert.Equal("?", destination.DisplayName);
        Assert.Equal(SagaMapNodeKind.Unresolved, destination.Kind);
    }

    [Fact]
    public async Task GetMap_ChildSagaStartedAndFinished_RenderAsEdgesLikeOrdinaryPublishes()
    {
        // Slice 2b's two dedicated entry types are still, mechanically, outbound publishes — they must
        // stitch into edges the same way MessagePublished/MessageSent already do, not fall through to
        // the generic AddPlainEvent default that unrecognized entry types get.
        var (sagaType, correlationId) = await SeedSagaAsync("OrderSaga", "Failed", SagaStatus.Failed);
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.SagaStarted,
            toState: "Submitted", messageType: "OrderSubmitted", messageId: "m0", payloadJson: "{}"));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.ChildSagaStarted,
            messageType: "DeliverInvoice", messageId: "out-1", sourceService: "OrderSaga", causationId: "m0"));

        await RecordTopologyAsync("InvoiceDeliverySaga", "DeliverInvoice", "vsaga.saga.InvoiceDeliverySaga");

        var map = await _client.GetFromJsonAsync<SagaMap>($"/api/sagas/{sagaType}/{correlationId}/map", JsonOptions);

        var edge = Assert.Single(map!.Edges, e => string.Equals(e.MessageType, "DeliverInvoice", StringComparison.Ordinal));
        Assert.Equal("OrderSaga", edge.FromNodeId);
        Assert.Equal("InvoiceDeliverySaga", edge.ToNodeId);
        var startedEvent = Assert.Single(map.Events, e => e.EntryType == SagaEntryType.ChildSagaStarted);
        Assert.Equal(edge.Id, startedEvent.EdgeId);
    }

    // The registration half of the Saga Map's topology, used by participants that can't write to the
    // store directly -- @vsaga/participant's httpTopologyReporter is the reason it exists.
    [Fact]
    public async Task RecordTopology_UpsertsRegistrationsAndResolvesThemOnTheMap()
    {
        var response = await _client.PostAsJsonAsync("/api/topology/registrations", new[]
        {
            new TopologyRegistration("NotificationService", "OrderShipped", "vsaga.participant.notification"),
            new TopologyRegistration("NotificationService", "SendInvoiceEmail", "vsaga.participant.notification"),
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var recorded = await _factory.Services.GetRequiredService<IServiceTopologyStore>().GetAllAsync();
        Assert.Equal(2, recorded.Count(e => string.Equals(e.ServiceName, "NotificationService", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RecordTopology_IsIdempotent_SoAParticipantMayReReportOnEveryRestart()
    {
        var registrations = new[] { new TopologyRegistration("NotificationService", "OrderShipped", "vsaga.participant.notification") };

        await _client.PostAsJsonAsync("/api/topology/registrations", registrations);
        await _client.PostAsJsonAsync("/api/topology/registrations", registrations);

        var recorded = await _factory.Services.GetRequiredService<IServiceTopologyStore>().GetAllAsync();
        Assert.Single(recorded, e => string.Equals(e.ServiceName, "NotificationService", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecordTopology_EmptyBatch_Returns204()
    {
        var response = await _client.PostAsJsonAsync("/api/topology/registrations", Array.Empty<TopologyRegistration>());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task RecordTopology_BlankField_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/topology/registrations", new[]
        {
            new TopologyRegistration("NotificationService", "", "vsaga.participant.notification"),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RecordTopology_WithoutApiKey_Returns401()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/topology/registrations", new[]
        {
            new TopologyRegistration("NotificationService", "OrderShipped", "vsaga.participant.notification"),
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Every 401 carries a ProblemDetails body naming the accepted credentials: a bare status code left
    // the most common setup mistake (no key at all) with nothing to go on from curl.
    [Fact]
    public async Task MissingApiKey_ReturnsProblemDetailsNamingTheAcceptedCredentials()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/sagas");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("X-Api-Key", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    // Identical for a wrong key and a missing one: a differing response would let the endpoint be used
    // to probe whether a guessed key was close to correct.
    [Fact]
    public async Task WrongApiKey_ReturnsTheSameBodyAsAMissingOne()
    {
        using var missingKeyClient = _factory.CreateClient();
        using var wrongKeyClient = _factory.CreateClient();
        wrongKeyClient.DefaultRequestHeaders.Add("X-Api-Key", "not-the-configured-key");

        var missing = await (await missingKeyClient.GetAsync("/api/sagas")).Content.ReadAsStringAsync();
        var wrong = await (await wrongKeyClient.GetAsync("/api/sagas")).Content.ReadAsStringAsync();

        Assert.Equal(missing, wrong);
    }
}
