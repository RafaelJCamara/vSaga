using System.Net;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Abstractions.Transport;
using VSaga.Dashboard.Api.Endpoints;
using VSaga.Persistence.InMemory;
using VSaga.Transport.InMemory;
using Microsoft.AspNetCore.Mvc.Testing;
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

    /// <summary>
    /// Fix F8: a page size above <see cref="SagaEndpoints.MaxPageSize"/> is clamped to it, and the
    /// response reports the size actually applied — so a caller paging with an oversized request can
    /// see why its pages are shorter than asked, rather than silently missing rows. The maximum itself
    /// is served unchanged, and a size just above it is what gets clamped.
    /// </summary>
    [Fact]
    public async Task ListSagas_ClampsPageSizeToTheServerMaximum()
    {
        var atMaximum = await _client.GetFromJsonAsync<PagedResult<SagaSummary>>($"/api/sagas?pageSize={SagaEndpoints.MaxPageSize}", JsonOptions);
        Assert.Equal(SagaEndpoints.MaxPageSize, atMaximum!.PageSize);

        var aboveMaximum = await _client.GetFromJsonAsync<PagedResult<SagaSummary>>($"/api/sagas?pageSize={SagaEndpoints.MaxPageSize + 1}", JsonOptions);
        Assert.Equal(SagaEndpoints.MaxPageSize, aboveMaximum!.PageSize);

        var farAbove = await _client.GetFromJsonAsync<PagedResult<SagaSummary>>("/api/sagas?pageSize=1000000", JsonOptions);
        Assert.Equal(SagaEndpoints.MaxPageSize, farAbove!.PageSize);
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
    public async Task Retry_WithNoFailedStepInTheTimeline_Returns422WithTheReason()
    {
        // Only the start is recorded: the reset-to-start retry this endpoint used to make is gone.
        var correlationId = await SeedInAsync(_factory, "Failed", SagaStatus.Failed,
            Log(SagaEntryType.SagaStarted, toState: "Submitted", messageType: nameof(OrderSubmitted), messageId: "m0", payload: "{\"OrderId\":\"X\"}"));

        var response = await _client.PostAsync($"/api/sagas/OrderSaga/{correlationId}/retry", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(SagaRetryPlanner.NoStepReason, await ReadErrorAsync(response));
        Assert.Empty(_factory.Transport.GetPublished());
        Assert.Equal(SagaStatus.Failed, (await GetDetailAsync(_factory, correlationId)).Summary.Status);
    }

    [Fact]
    public async Task Retry_OfASagaRecordedBeforeStepBodiesWereStored_Returns422WithThePlainWordsReason()
    {
        var correlationId = await SeedInAsync(_factory, "Failed", SagaStatus.Failed,
            Log(SagaEntryType.MessageReceived, messageType: nameof(PaymentFailed), messageId: "c3"),
            Log(SagaEntryType.StepSucceeded, fromState: "Gathering", toState: "Failed", messageType: nameof(PaymentFailed), messageId: "c3"),
            Log(SagaEntryType.SagaCompleted, toState: "Failed"));

        var response = await _client.PostAsync($"/api/sagas/OrderSaga/{correlationId}/retry", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            "This saga was recorded before vSaga stored the message of every step, so the PaymentFailed message that ran the step to re-run cannot be replayed.",
            await ReadErrorAsync(response));
        Assert.DoesNotContain(await ReadTimelineAsync(_factory, correlationId), e => e.EntryType == SagaEntryType.ManualRetryRequested);
    }

    [Fact]
    public async Task Retry_TechnicalFailure_ResetsAtTheReadVersionAndRepublishesTheFailedMessageTargetedAtTheSagaType()
    {
        await using var host = Host(services => ReplaceAdminStore<RecordingAdminStore>(services));
        var correlationId = await SeedInAsync(host, "AwaitingInventory", SagaStatus.Failed,
            Log(SagaEntryType.SagaStarted, toState: "Submitted", messageType: nameof(OrderSubmitted), messageId: "m0", payload: "{\"OrderId\":\"X\"}"),
            Log(SagaEntryType.MessageReceived, messageType: nameof(ReserveInventory), messageId: "m1", payload: "{\"Sku\":\"A\"}"),
            Log(SagaEntryType.StepFailed, fromState: "AwaitingInventory", messageType: nameof(ReserveInventory), messageId: "m1",
                payload: "{\"Sku\":\"A\"}", error: "boom"));
        await AdvanceAsync(host, correlationId, times: 2);
        var received = new ConcurrentQueue<ReceivedMessage>();
        using var subscription = await CaptureRedrivesAsync(host, received);

        var response = await PostRetryAsync(host, correlationId);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Always reset, even though the state does not change: the version the handler read (2, not the
        // insert's 0) is the concurrency guard.
        var reset = Assert.Single(host.Services.GetRequiredService<RecordingAdminStore>().Calls);
        Assert.Equal(("OrderSaga", correlationId, "AwaitingInventory", SagaStatus.Running, 2), reset);

        var redrive = Assert.Single(received);
        Assert.Equal(nameof(ReserveInventory), redrive.MessageTypeName);
        Assert.Equal(correlationId, redrive.CorrelationId);
        Assert.NotEqual("m1", redrive.MessageId, StringComparer.Ordinal); // a fresh id, so the engine's dedupe lets it through
        Assert.Equal("{\"Sku\":\"A\"}", Encoding.UTF8.GetString(redrive.Body.Span));
        Assert.Equal("OrderSaga", redrive.Headers[MessageEnvelope.TargetSagaTypeHeader]);

        var retry = Assert.Single(await ReadTimelineAsync(host, correlationId), e => e.EntryType == SagaEntryType.ManualRetryRequested);
        Assert.Equal(("AwaitingInventory", "AwaitingInventory", nameof(ReserveInventory), "m1"),
            (retry.FromState, retry.ToState, retry.MessageType, retry.MessageId));
    }

    [Fact]
    public async Task Retry_BusinessFailure_ResetsToTheFailingStepsFromStateAndReplaysOnlyThatStep()
    {
        var correlationId = await SeedInAsync(_factory, "Failed", SagaStatus.Failed,
            Log(SagaEntryType.SagaStarted, toState: "Submitted", messageType: nameof(OrderSubmitted), messageId: "m0", payload: "{\"OrderId\":\"X\"}"),
            Log(SagaEntryType.MessageReceived, messageType: nameof(OrderSubmitted), messageId: "m0", payload: "{\"OrderId\":\"X\"}"),
            Log(SagaEntryType.StepSucceeded, fromState: "Submitted", toState: "Gathering", messageType: nameof(OrderSubmitted), messageId: "m0"),
            Log(SagaEntryType.MessageReceived, messageType: nameof(PaymentFailed), messageId: "c3", payload: "{\"Reason\":\"declined\"}"),
            Log(SagaEntryType.StepSucceeded, fromState: "Gathering", toState: "Failed", messageType: nameof(PaymentFailed), messageId: "c3"),
            Log(SagaEntryType.SagaCompleted, toState: "Failed", messageId: "c3"));
        var received = new ConcurrentQueue<ReceivedMessage>();
        using var subscription = await CaptureRedrivesAsync(_factory, received);

        var response = await _client.PostAsync($"/api/sagas/OrderSaga/{correlationId}/retry", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var detail = await GetDetailAsync(_factory, correlationId);
        Assert.Equal(("Gathering", SagaStatus.Running, 1), (detail.Summary.CurrentState, detail.Summary.Status, detail.Summary.Version));

        var redrive = Assert.Single(received);
        Assert.Equal(nameof(PaymentFailed), redrive.MessageTypeName);
        Assert.Equal("{\"Reason\":\"declined\"}", Encoding.UTF8.GetString(redrive.Body.Span));
        Assert.Equal("OrderSaga", redrive.Headers[MessageEnvelope.TargetSagaTypeHeader]);
    }

    [Fact]
    public async Task Retry_TimedOutSaga_ResetsToTheStateBeforeTheStepThatEnteredTheTimedOutState()
    {
        var correlationId = await SeedInAsync(_factory, "Abandoned", SagaStatus.TimedOut,
            Log(SagaEntryType.SagaStarted, toState: "Requested", messageType: nameof(InvoiceIssued), messageId: "e5", payload: "{\"Invoice\":1}"),
            Log(SagaEntryType.MessageReceived, messageType: nameof(InvoiceIssued), messageId: "e5", payload: "{\"Invoice\":1}"),
            Log(SagaEntryType.StepSucceeded, fromState: "Requested", toState: "AwaitingArchival", messageType: nameof(InvoiceIssued), messageId: "e5"),
            Log(SagaEntryType.TimeoutFired, fromState: "AwaitingArchival"),
            Log(SagaEntryType.StepSucceeded, fromState: "AwaitingArchival", toState: "Abandoned"));
        var received = new ConcurrentQueue<ReceivedMessage>();
        using var subscription = await CaptureRedrivesAsync(_factory, received);

        var response = await _client.PostAsync($"/api/sagas/OrderSaga/{correlationId}/retry", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var detail = await GetDetailAsync(_factory, correlationId);
        Assert.Equal(("Requested", SagaStatus.Running), (detail.Summary.CurrentState, detail.Summary.Status));
        Assert.Equal(nameof(InvoiceIssued), Assert.Single(received).MessageTypeName);
    }

    [Fact]
    public async Task Retry_WhenTheResetLosesAConcurrentWriteRace_Returns409()
    {
        // The stub stands in for a saga that advanced between the handler's summary read and its reset:
        // every retry now resets, so a StepFailed retry reaches it too.
        await using var host = Host(services => ReplaceAdminStore<RacedAdminStore>(services));
        var correlationId = await SeedInAsync(host, "AwaitingInventory", SagaStatus.Failed, TechnicalFailure());

        var response = await PostRetryAsync(host, correlationId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        // The not-Failed status guard also returns 409 — the body text is what proves this one came
        // from the concurrency mapping.
        Assert.Contains("modified concurrently", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(host.Services.GetRequiredService<InMemoryMessageTransport>().GetPublished());
    }

    [Fact]
    public async Task Retry_WhenTheRedriveCannotBePublished_RestoresTheStateAndStatusAndAnswers502()
    {
        await using var host = Host(services => ReplaceTransport(services));
        var correlationId = await SeedInAsync(host, "AwaitingInventory", SagaStatus.Failed,
            [.. TechnicalFailure(), Log(SagaEntryType.StatePersisted, payload: "{\"Version\":0}")]);

        var response = await PostRetryAsync(host, correlationId);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("restored").GetBoolean());
        Assert.Contains("restored to state 'AwaitingInventory' with status 'Failed'", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);

        var detail = await GetDetailAsync(host, correlationId);
        Assert.Equal(("AwaitingInventory", SagaStatus.Failed, 2), (detail.Summary.CurrentState, detail.Summary.Status, detail.Summary.Version));

        // The reset's snapshot (version 1) and the restored state's (version 2), both after ManualRetryRequested.
        var timeline = await ReadTimelineAsync(host, correlationId);
        var retry = Assert.Single(timeline, e => e.EntryType == SagaEntryType.ManualRetryRequested);
        var recorded = timeline.Where(e => e.EntryType == SagaEntryType.StatePersisted && e.SequenceNumber > retry.SequenceNumber).ToList();
        Assert.Equal([1, 2], recorded.Select(e => ReadVersion(e.PayloadJson!)));
        Assert.Contains("\"Status\":2", recorded[^1].PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retry_WhenTheTransportThrowsAnUnwrappedException_StillRestoresAtTheResetVersionAndAnswers502()
    {
        // RabbitMqTransport lets a TaskCanceledException out of opening a channel on a broker that stopped
        // answering. The reset is committed by then, so it must be undone as for a refused publish, or the
        // saga stays Running with no redrive and the status guard refuses every further retry.
        await using var host = Host(services =>
        {
            ReplaceTransport<UnwrappedFailureTransport>(services);
            ReplaceAdminStore<RecordingAdminStore>(services);
        });
        var correlationId = await SeedInAsync(host, "AwaitingInventory", SagaStatus.Failed,
            [.. TechnicalFailure(), Log(SagaEntryType.StatePersisted, payload: "{\"Version\":0}")]);
        await AdvanceAsync(host, correlationId, times: 2);

        var response = await PostRetryAsync(host, correlationId);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("restored").GetBoolean());
        Assert.Contains("(TaskCanceledException)", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Contains("restored to state 'AwaitingInventory' with status 'Failed'", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);

        // The reset at the read version 2, then the restore at 3, the version the reset wrote.
        Assert.Equal(
            [("AwaitingInventory", SagaStatus.Running, 2), ("AwaitingInventory", SagaStatus.Failed, 3)],
            host.Services.GetRequiredService<RecordingAdminStore>().Calls.Select(c => (c.State, c.Status, c.ExpectedVersion)));
        var detail = await GetDetailAsync(host, correlationId);
        Assert.Equal(("AwaitingInventory", SagaStatus.Failed, 4), (detail.Summary.CurrentState, detail.Summary.Status, detail.Summary.Version));

        var timeline = await ReadTimelineAsync(host, correlationId);
        var retry = Assert.Single(timeline, e => e.EntryType == SagaEntryType.ManualRetryRequested);
        var recorded = timeline.Where(e => e.EntryType == SagaEntryType.StatePersisted && e.SequenceNumber > retry.SequenceNumber);
        Assert.Equal([3, 4], recorded.Select(e => ReadVersion(e.PayloadJson!)));

        // Past the reset nothing listens to the request's token, so a client that hangs up cannot cut the
        // redrive or the restore short.
        Assert.False(Assert.Single(host.Services.GetRequiredService<UnwrappedFailureTransport>().TokensSeen).CanBeCanceled);
    }

    [Fact]
    public async Task Retry_WhenTheRestoreAfterAFailedPublishLosesARace_Answers502SayingTheSagaWasNotRestored()
    {
        await using var host = Host(services =>
        {
            ReplaceTransport(services);
            ReplaceAdminStore<FirstResetOnlyAdminStore>(services);
        });
        var correlationId = await SeedInAsync(host, "Failed", SagaStatus.Failed,
            Log(SagaEntryType.MessageReceived, messageType: nameof(PaymentFailed), messageId: "c3", payload: "{}"),
            Log(SagaEntryType.StepSucceeded, fromState: "Gathering", toState: "Failed", messageType: nameof(PaymentFailed), messageId: "c3"),
            Log(SagaEntryType.SagaCompleted, toState: "Failed", messageId: "c3"));

        var response = await PostRetryAsync(host, correlationId);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(problem.GetProperty("restored").GetBoolean());
        Assert.Contains("could not be restored", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        var detail = await GetDetailAsync(host, correlationId);
        Assert.Equal(("Gathering", SagaStatus.Running), (detail.Summary.CurrentState, detail.Summary.Status));
    }

    [Fact]
    public async Task Retry_WhenTheRestoreAfterAFailedPublishFailsForAnotherReason_Answers502SayingWhereTheSagaWasLeft()
    {
        // A store outage during the restore must not escape as a bare 500: the caller still learns the saga
        // was not restored, and the detail does not blame a concurrent modification or leak the exception.
        await using var host = Host(services =>
        {
            ReplaceTransport(services);
            ReplaceAdminStore<FirstResetThenOutageAdminStore>(services);
        });
        var correlationId = await SeedInAsync(host, "Failed", SagaStatus.Failed,
            Log(SagaEntryType.MessageReceived, messageType: nameof(PaymentFailed), messageId: "c3", payload: "{}"),
            Log(SagaEntryType.StepSucceeded, fromState: "Gathering", toState: "Failed", messageType: nameof(PaymentFailed), messageId: "c3"),
            Log(SagaEntryType.SagaCompleted, toState: "Failed", messageId: "c3"));

        var response = await PostRetryAsync(host, correlationId);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(problem.GetProperty("restored").GetBoolean());
        var text = problem.GetProperty("detail").GetString();
        Assert.Contains("could not be restored to its previous state; it is Running in state 'Gathering' with no redrive in flight", text, StringComparison.Ordinal);
        Assert.DoesNotContain("concurrently", text, StringComparison.Ordinal);
        Assert.DoesNotContain(FirstResetThenOutageAdminStore.InternalText, text, StringComparison.Ordinal);
        var detail = await GetDetailAsync(host, correlationId);
        Assert.Equal(("Gathering", SagaStatus.Running), (detail.Summary.CurrentState, detail.Summary.Status));
    }

    [Fact]
    public async Task GetRetryPlan_UnknownSaga_Returns404()
    {
        var response = await _client.GetAsync($"/api/sagas/OrderSaga/{Guid.NewGuid()}/retry-plan");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetRetryPlan_RetryableSaga_ReturnsThePlanInCamelCaseWithoutTheMessageBody()
    {
        var correlationId = await SeedInAsync(_factory, "AwaitingInventory", SagaStatus.Failed, TechnicalFailure());
        var timeline = await ReadTimelineAsync(_factory, correlationId);
        var received = timeline.Single(e => e.EntryType == SagaEntryType.MessageReceived).SequenceNumber;
        var failed = timeline.Single(e => e.EntryType == SagaEntryType.StepFailed).SequenceNumber;

        var body = await _client.GetStringAsync($"/api/sagas/OrderSaga/{correlationId}/retry-plan");

        var plan = JsonDocument.Parse(body).RootElement;
        Assert.True(plan.GetProperty("retryable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("reason").ValueKind);
        Assert.Equal("StepFailed", plan.GetProperty("failureKind").GetString());
        Assert.Equal(failed, plan.GetProperty("failureSequenceNumber").GetInt64());
        var step = plan.GetProperty("step");
        Assert.Equal(received, step.GetProperty("sequenceNumber").GetInt64());
        Assert.Equal(nameof(ReserveInventory), step.GetProperty("messageType").GetString());
        Assert.Equal("m1", step.GetProperty("messageId").GetString());
        Assert.Equal("AwaitingInventory", step.GetProperty("fromState").GetString());
        Assert.Equal(5, plan.EnumerateObject().Count());
        Assert.DoesNotContain("Sku", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRetryPlan_SagaThatCannotBeRetried_Returns200WithTheReason()
    {
        var correlationId = await SeedInAsync(_factory, "AwaitingInventory", SagaStatus.Running, TechnicalFailure());

        var response = await _client.GetAsync($"/api/sagas/OrderSaga/{correlationId}/retry-plan");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var plan = await response.Content.ReadFromJsonAsync<SagaRetryPlan>(JsonOptions);
        Assert.Equal(new SagaRetryPlan(false, SagaRetryPlanner.NotFailedReason, null, null, null), plan);
    }

    // Message types the redrive tests subscribe to: the in-memory transport matches subscriptions by type name.
    private sealed record OrderSubmitted(string Id);

    private sealed record ReserveInventory(string Id);

    private sealed record PaymentFailed(string Id);

    private sealed record InvoiceIssued(string Id);

    /// <summary>A step that threw on ReserveInventory (id m1) in AwaitingInventory, with its message recorded.</summary>
    private static SagaLogEntry[] TechnicalFailure() =>
    [
        Log(SagaEntryType.MessageReceived, messageType: nameof(ReserveInventory), messageId: "m1", payload: "{\"Sku\":\"A\"}"),
        Log(SagaEntryType.StepFailed, fromState: "AwaitingInventory", messageType: nameof(ReserveInventory), messageId: "m1",
            payload: "{\"Sku\":\"A\"}", error: "boom"),
    ];

    private static SagaLogEntry Log(SagaEntryType type, string? fromState = null, string? toState = null, string? messageType = null,
        string? messageId = null, string? payload = null, string? error = null) =>
        SagaLogEntry.Create(Guid.Empty, "OrderSaga", type, fromState, toState, messageType, messageId, payload, error);

    private WebApplicationFactory<Program> Host(Action<IServiceCollection> services) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services));

    private static void ReplaceAdminStore<TStore>(IServiceCollection services)
        where TStore : class, ISagaAdminStore
    {
        services.RemoveAll<ISagaAdminStore>();
        services.AddSingleton<TStore>();
        services.AddSingleton<ISagaAdminStore>(sp => sp.GetRequiredService<TStore>());
    }

    private static void ReplaceTransport(IServiceCollection services) => ReplaceTransport<RefusingTransport>(services);

    private static void ReplaceTransport<TTransport>(IServiceCollection services)
        where TTransport : class, IMessageTransport
    {
        services.RemoveAll<IMessageTransport>();
        services.AddSingleton<TTransport>();
        services.AddSingleton<IMessageTransport>(sp => sp.GetRequiredService<TTransport>());
    }

    /// <summary>Moves the seeded OrderSaga on by <paramref name="times"/> versions through ordinary updates.</summary>
    private static async Task AdvanceAsync(WebApplicationFactory<Program> host, Guid correlationId, int times)
    {
        var store = host.Services.GetRequiredService<ISagaSnapshotStore<DashboardTestState>>();
        for (var i = 0; i < times; i++)
        {
            var state = (await store.FindAsync("OrderSaga", correlationId))!;
            await store.UpdateAsync(state, state.Version);
        }
    }

    /// <summary>Inserts an OrderSaga at version 0 and appends <paramref name="entries"/> under its correlation id.</summary>
    private static async Task<Guid> SeedInAsync(WebApplicationFactory<Program> host, string currentState, SagaStatus status, params SagaLogEntry[] entries)
    {
        var correlationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await host.Services.GetRequiredService<ISagaSnapshotStore<DashboardTestState>>().InsertAsync(new DashboardTestState
        {
            CorrelationId = correlationId,
            SagaType = "OrderSaga",
            CurrentState = currentState,
            Status = status,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });

        var log = host.Services.GetRequiredService<ISagaEventLogStore>();
        foreach (var entry in entries)
            await log.AppendAsync(entry with { CorrelationId = correlationId });

        return correlationId;
    }

    private static Task<IDisposable> CaptureRedrivesAsync(WebApplicationFactory<Program> host, ConcurrentQueue<ReceivedMessage> received) =>
        host.Services.GetRequiredService<InMemoryMessageTransport>().SubscribeAsync(
            new TransportSubscription("retry-test",
                [typeof(OrderSubmitted), typeof(ReserveInventory), typeof(PaymentFailed), typeof(InvoiceIssued)], "retry-test"),
            (message, _) =>
            {
                received.Enqueue(message);
                return Task.CompletedTask;
            });

    private static async Task<HttpResponseMessage> PostRetryAsync(WebApplicationFactory<Program> host, Guid correlationId)
    {
        using var client = AuthenticatedClient(host);
        return await client.PostAsync($"/api/sagas/OrderSaga/{correlationId}/retry", null);
    }

    private static async Task<SagaDetail> GetDetailAsync(WebApplicationFactory<Program> host, Guid correlationId)
    {
        using var client = AuthenticatedClient(host);
        return (await client.GetFromJsonAsync<SagaDetail>($"/api/sagas/OrderSaga/{correlationId}", JsonOptions))!;
    }

    private static async Task<List<SagaLogEntry>> ReadTimelineAsync(WebApplicationFactory<Program> host, Guid correlationId)
    {
        using var client = AuthenticatedClient(host);
        return (await client.GetFromJsonAsync<List<SagaLogEntry>>($"/api/sagas/OrderSaga/{correlationId}/timeline", JsonOptions))!;
    }

    private static HttpClient AuthenticatedClient(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", DashboardApiFactory.TestApiKey);
        return client;
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    private static int ReadVersion(string stateJson)
    {
        using var document = JsonDocument.Parse(stateJson);
        return document.RootElement.GetProperty("Version").GetInt32();
    }

    private sealed class RacedAdminStore : ISagaAdminStore
    {
        public Task ResetStateAsync(string sagaType, Guid correlationId, string currentState, SagaStatus status, int expectedVersion, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default) =>
            throw new SagaConcurrencyException(sagaType, correlationId, expectedVersion);
    }

    /// <summary>The in-memory reset, recording each call's arguments.</summary>
    private sealed class RecordingAdminStore(InMemorySagaStore inner) : ISagaAdminStore
    {
        private readonly ConcurrentQueue<(string, Guid, string, SagaStatus, int)> _calls = new();

        public IReadOnlyCollection<(string SagaType, Guid CorrelationId, string State, SagaStatus Status, int ExpectedVersion)> Calls => _calls;

        public Task ResetStateAsync(string sagaType, Guid correlationId, string currentState, SagaStatus status, int expectedVersion, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
        {
            _calls.Enqueue((sagaType, correlationId, currentState, status, expectedVersion));
            return inner.ResetStateAsync(sagaType, correlationId, currentState, status, expectedVersion, updatedAtUtc, cancellationToken);
        }
    }

    /// <summary>Lets the retry's reset through, then refuses the restore as a saga that moved on after the reset would.</summary>
    private sealed class FirstResetOnlyAdminStore(InMemorySagaStore inner) : ISagaAdminStore
    {
        private int _calls;

        public Task ResetStateAsync(string sagaType, Guid correlationId, string currentState, SagaStatus status, int expectedVersion, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref _calls) == 1
                ? inner.ResetStateAsync(sagaType, correlationId, currentState, status, expectedVersion, updatedAtUtc, cancellationToken)
                : throw new SagaConcurrencyException(sagaType, correlationId, expectedVersion);
    }

    /// <summary>Lets the retry's reset through, then fails the restore as a store that stopped answering would.</summary>
    private sealed class FirstResetThenOutageAdminStore(InMemorySagaStore inner) : ISagaAdminStore
    {
        public const string InternalText = "store-internal-detail";

        private int _calls;

        public Task ResetStateAsync(string sagaType, Guid correlationId, string currentState, SagaStatus status, int expectedVersion, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref _calls) == 1
                ? inner.ResetStateAsync(sagaType, correlationId, currentState, status, expectedVersion, updatedAtUtc, cancellationToken)
                : throw new TimeoutException(InternalText);
    }

    /// <summary>A transport whose broker refuses every publish.</summary>
    private sealed class RefusingTransport : IMessageTransport
    {
        public Task PublishAsync<TMessage>(TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default)
            where TMessage : notnull =>
            throw Refused(message.GetType().Name, envelope);

        public Task SendAsync<TMessage>(string destination, TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default)
            where TMessage : notnull =>
            throw Refused(message.GetType().Name, envelope);

        public Task PublishRawAsync(string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken cancellationToken = default) =>
            throw Refused(messageTypeName, envelope);

        public Task<IDisposable> SubscribeAsync(TransportSubscription subscription, Func<ReceivedMessage, CancellationToken, Task> handler, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static MessageTransportPublishException Refused(string messageTypeName, MessageEnvelope envelope) =>
            new(messageTypeName, envelope.CorrelationId, isUnroutable: false, detail: "refused by the test", new InvalidOperationException("refused"));
    }

    /// <summary>
    /// A transport whose raw publish fails with an exception it does not wrap in MessageTransportPublishException,
    /// as RabbitMqTransport does when opening a channel times out. Records the token each publish was given.
    /// </summary>
    private sealed class UnwrappedFailureTransport : IMessageTransport
    {
        private readonly ConcurrentQueue<CancellationToken> _tokens = new();

        public IReadOnlyCollection<CancellationToken> TokensSeen => _tokens;

        public Task PublishAsync<TMessage>(TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default)
            where TMessage : notnull =>
            throw new NotSupportedException();

        public Task SendAsync<TMessage>(string destination, TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default)
            where TMessage : notnull =>
            throw new NotSupportedException();

        public Task PublishRawAsync(string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        {
            _tokens.Enqueue(cancellationToken);
            throw new TaskCanceledException("opening a channel timed out in the test");
        }

        public Task<IDisposable> SubscribeAsync(TransportSubscription subscription, Func<ReceivedMessage, CancellationToken, Task> handler, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
    public async Task GetMap_TimelineWithStateSnapshots_LeavesThemOutOfEventsAndFailureIndex()
    {
        var (sagaType, correlationId) = await SeedSagaAsync("OrderSaga", "Failed", SagaStatus.Failed);

        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.SagaStarted,
            toState: "Submitted", messageType: "OrderSubmitted", messageId: "m0", payloadJson: "{}", sourceService: "OrderSubmitter"));
        await AppendLogAsync(SagaStateSnapshot.CreateEntry(correlationId, "OrderSaga", """{"CurrentState":"Submitted"}""",
            SagaStateSnapshot.DefaultMaxBytes, messageType: "OrderSubmitted", messageId: "m0"));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.MessageReceived,
            messageType: "PaymentFailed", messageId: "m1", sourceService: "PaymentService"));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.StepFailed,
            fromState: "Submitted", messageType: "PaymentFailed", messageId: "m1", errorMessage: "boom"));
        await AppendLogAsync(SagaStateSnapshot.CreateEntry(correlationId, "OrderSaga", """{"CurrentState":"Submitted"}""",
            SagaStateSnapshot.DefaultMaxBytes, messageType: "PaymentFailed", messageId: "m1"));

        var map = await _client.GetFromJsonAsync<SagaMap>($"/api/sagas/{sagaType}/{correlationId}/map", JsonOptions);

        Assert.NotNull(map);
        Assert.Equal(
            [SagaEntryType.SagaStarted, SagaEntryType.MessageReceived, SagaEntryType.StepFailed],
            map.Events.Select(e => e.EntryType));
        Assert.Equal(2, map.FailureEventIndex);
        Assert.Equal("boom", map.Events[map.FailureEventIndex!.Value].ErrorMessage);
        Assert.Equal(2, map.Edges.Count);
        Assert.True(Assert.Single(map.Edges, e => string.Equals(e.MessageId, "m1", StringComparison.Ordinal)).Failed);
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
