using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Dashboard.Api.Endpoints;
using VSaga.Persistence.InMemory;
using VSaga.Transport.InMemory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// The StatePersisted entry a dashboard retry reset records (SagaResetSnapshotRecorder): the stored state
/// after the reset, with no message identity, sequenced after ManualRetryRequested, for every kind of retry;
/// nothing when the reset lost its race, the saga has no snapshot yet or a step already moved it on; the
/// dashboard's cap and the engine host's smaller one; and a failed append that still lets the redrive go out.
/// Each test runs in its own host, so DI and settings overrides cannot leak between them.
/// </summary>
public sealed class RetryStateSnapshotTests : IAsyncLifetime, IAsyncDisposable
{
    private const string SagaType = "OrderSaga";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DashboardApiFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    // xunit 2 calls IAsyncLifetime.DisposeAsync, never a test class's IAsyncDisposable.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Retry_ThatResetsTheSaga_RecordsTheStoredStateAfterManualRetryRequested()
    {
        await using var host = Host();
        var correlationId = await SeedBusinessFailureAsync(host, withSnapshot: true);

        var response = await PostRetryAsync(host, correlationId);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var detail = await GetDetailAsync(host, correlationId);
        var timeline = await ReadTimelineAsync(host, correlationId);
        var retry = Assert.Single(timeline, e => e.EntryType == SagaEntryType.ManualRetryRequested);
        var snapshot = timeline[^1];
        Assert.Equal(SagaEntryType.StatePersisted, snapshot.EntryType);
        Assert.True(snapshot.SequenceNumber > retry.SequenceNumber);
        Assert.Equal(detail.DataJson, snapshot.PayloadJson);
        Assert.Equal(1, ReadVersion(snapshot.PayloadJson!));
        Assert.Null(snapshot.MessageType);
        Assert.Null(snapshot.MessageId);
        Assert.Null(snapshot.FromState);
        Assert.Null(snapshot.ToState);
        Assert.Equal(2, timeline.Count(e => e.EntryType == SagaEntryType.StatePersisted));
    }

    [Fact]
    public async Task Retry_OfATechnicalFailure_ResetsTooAndRecordsTheSnapshotAfterManualRetryRequested()
    {
        // The state does not change, but the reset still runs (it is the concurrency guard and takes the
        // saga out of Failed), so the version moves and the snapshot records it.
        await using var host = Host();
        var correlationId = await SeedSagaAsync(host, "AwaitingInventory",
            SagaLogEntry.Create(Guid.Empty, SagaType, SagaEntryType.SagaStarted,
                toState: "Submitted", messageType: "OrderSubmitted", messageId: "m0", payloadJson: "{\"OrderId\":\"X\"}"),
            SagaLogEntry.Create(Guid.Empty, SagaType, SagaEntryType.StatePersisted, payloadJson: "{\"Version\":0}"),
            SagaLogEntry.Create(Guid.Empty, SagaType, SagaEntryType.StepFailed,
                fromState: "AwaitingInventory", messageType: "ReserveInventory", messageId: "m1",
                payloadJson: "{\"OrderId\":\"X\"}", errorMessage: "boom"));

        var response = await PostRetryAsync(host, correlationId);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var timeline = await ReadTimelineAsync(host, correlationId);
        var retry = Assert.Single(timeline, e => e.EntryType == SagaEntryType.ManualRetryRequested);
        var snapshot = timeline[^1];
        Assert.Equal(SagaEntryType.StatePersisted, snapshot.EntryType);
        Assert.True(snapshot.SequenceNumber > retry.SequenceNumber);
        Assert.Equal(1, ReadVersion(snapshot.PayloadJson!));
        Assert.Equal(2, timeline.Count(e => e.EntryType == SagaEntryType.StatePersisted));
    }

    [Fact]
    public async Task Retry_WhoseResetLosesTheRace_RecordsNoSnapshot()
    {
        await using var host = Host(services =>
        {
            services.RemoveAll<ISagaAdminStore>();
            services.AddSingleton<ISagaAdminStore, RacedAdminStore>();
        });
        var correlationId = await SeedBusinessFailureAsync(host, withSnapshot: true);

        var response = await PostRetryAsync(host, correlationId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var timeline = await ReadTimelineAsync(host, correlationId);
        Assert.Single(timeline, e => e.EntryType == SagaEntryType.StatePersisted);
    }

    [Fact]
    public async Task Retry_OfASagaWithNoSnapshotYet_RecordsNone()
    {
        // The engine host records none (RecordStateSnapshots = false, or a saga older than snapshots): the
        // dashboard never writes a saga's first one.
        await using var host = Host();
        var correlationId = await SeedBusinessFailureAsync(host, withSnapshot: false);

        var response = await PostRetryAsync(host, correlationId);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var timeline = await ReadTimelineAsync(host, correlationId);
        Assert.DoesNotContain(timeline, e => e.EntryType == SagaEntryType.StatePersisted);
    }

    [Fact]
    public async Task Retry_WhenAStepMovedTheSagaOnAfterTheReset_RecordsNoSnapshot()
    {
        // The stored version is no longer the one the reset wrote: that step records its own snapshot.
        await using var host = Host(services =>
        {
            services.RemoveAll<ISagaAdminStore>();
            services.AddSingleton<ISagaAdminStore>(sp => new AdvancingAdminStore(sp.GetRequiredService<InMemorySagaStore>()));
        });
        var correlationId = await SeedBusinessFailureAsync(host, withSnapshot: true);

        var response = await PostRetryAsync(host, correlationId);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(2, ReadVersion((await GetDetailAsync(host, correlationId)).DataJson!));
        var timeline = await ReadTimelineAsync(host, correlationId);
        Assert.Single(timeline, e => e.EntryType == SagaEntryType.StatePersisted);
    }

    [Fact]
    public async Task Retry_WithAStateAboveTheDashboardCap_RecordsTheSizeMarker()
    {
        await using var host = Host(maxBytes: "16");
        var correlationId = await SeedBusinessFailureAsync(host, withSnapshot: true);

        var response = await PostRetryAsync(host, correlationId);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var detail = await GetDetailAsync(host, correlationId);
        var snapshot = (await ReadTimelineAsync(host, correlationId))[^1];
        Assert.Equal(SagaEntryType.StatePersisted, snapshot.EntryType);
        Assert.Equal(Marker(Encoding.UTF8.GetByteCount(detail.DataJson!), 16), snapshot.PayloadJson);
    }

    [Fact]
    public async Task Retry_AfterTheEngineRecordedASmallerCapMarker_UsesTheEngineCap()
    {
        // A host with MaxStateSnapshotBytes = 0 keeps state out of the log; the dashboard's larger default
        // cap must not write the full blob into that saga's timeline.
        await using var host = Host();
        var correlationId = await SeedBusinessFailureAsync(host, withSnapshot: true, snapshotPayload: Marker(900, 0));

        var response = await PostRetryAsync(host, correlationId);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var detail = await GetDetailAsync(host, correlationId);
        var snapshot = (await ReadTimelineAsync(host, correlationId))[^1];
        Assert.Equal(SagaEntryType.StatePersisted, snapshot.EntryType);
        Assert.Equal(Marker(Encoding.UTF8.GetByteCount(detail.DataJson!), 0), snapshot.PayloadJson);
    }

    [Fact]
    public async Task Retry_AfterTwoLimitMarkers_UsesTheMostRecentLimitEvenWhenItIsLarger()
    {
        // The host's cap changed between the two markers: the newer one is the cap in force, so neither the
        // oldest marker nor the smallest limit decides.
        await using var host = Host();
        var correlationId = await SeedBusinessFailureAsync(host, withSnapshot: true, snapshotPayload: Marker(900, 0),
            SagaLogEntry.Create(Guid.Empty, SagaType, SagaEntryType.StatePersisted, payloadJson: Marker(900, 24)));

        var response = await PostRetryAsync(host, correlationId);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var detail = await GetDetailAsync(host, correlationId);
        var snapshot = (await ReadTimelineAsync(host, correlationId))[^1];
        Assert.Equal(SagaEntryType.StatePersisted, snapshot.EntryType);
        Assert.Equal(Marker(Encoding.UTF8.GetByteCount(detail.DataJson!), 24), snapshot.PayloadJson);
    }

    [Fact]
    public async Task Retry_AfterABudgetMarker_TakesTheCapFromTheMostRecentLimitMarkerOnly()
    {
        // A budget marker says the saga's allowance ran out, not what the host's cap is: the limit marker
        // before it is the one that counts.
        await using var host = Host();
        var correlationId = await SeedBusinessFailureAsync(host, withSnapshot: true, snapshotPayload: Marker(900, 24),
            SagaLogEntry.Create(Guid.Empty, SagaType, SagaEntryType.StatePersisted,
                payloadJson: "{\"$vsagaStateOmitted\":true,\"bytes\":40,\"budget\":1048576}"));

        var response = await PostRetryAsync(host, correlationId);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var detail = await GetDetailAsync(host, correlationId);
        var snapshot = (await ReadTimelineAsync(host, correlationId))[^1];
        Assert.Equal(Marker(Encoding.UTF8.GetByteCount(detail.DataJson!), 24), snapshot.PayloadJson);
    }

    [Fact]
    public async Task Retry_AfterOnlyABudgetMarker_RecordsTheFullState()
    {
        await using var host = Host();
        var correlationId = await SeedBusinessFailureAsync(host, withSnapshot: true,
            snapshotPayload: "{\"$vsagaStateOmitted\":true,\"bytes\":40,\"budget\":1048576}");

        var response = await PostRetryAsync(host, correlationId);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var detail = await GetDetailAsync(host, correlationId);
        var snapshot = (await ReadTimelineAsync(host, correlationId))[^1];
        Assert.Equal(detail.DataJson, snapshot.PayloadJson);
    }

    [Fact]
    public async Task Retry_WhenTheSnapshotAppendFails_StillAnswers202AndPublishesTheRedrive()
    {
        await using var host = Host(services =>
        {
            services.RemoveAll<ISagaEventLogStore>();
            services.AddSingleton<ISagaEventLogStore>(sp => new SnapshotRefusingLog(sp.GetRequiredService<InMemorySagaStore>()));
        });
        var correlationId = await SeedBusinessFailureAsync(host, withSnapshot: true);

        var response = await PostRetryAsync(host, correlationId);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains(host.Services.GetRequiredService<InMemoryMessageTransport>().GetPublished(), p =>
            string.Equals(p.MessageTypeName, "InventoryReservationFailed", StringComparison.Ordinal) && p.Envelope.CorrelationId == correlationId);
        var timeline = await ReadTimelineAsync(host, correlationId);
        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.ManualRetryRequested);
        Assert.Single(timeline, e => e.EntryType == SagaEntryType.StatePersisted);
    }

    [Fact]
    public void Options_WithNoValue_DefaultToTheSharedCap()
    {
        Assert.Equal(SagaStateSnapshot.DefaultMaxBytes, DashboardStateSnapshotOptions.Read(Configuration(null)).MaxBytes);
        Assert.Equal(SagaStateSnapshot.DefaultMaxBytes, DashboardStateSnapshotOptions.Read(Configuration(" ")).MaxBytes);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData(" 4096 ", 4096)]
    public void Options_WithANonNegativeWholeNumber_ReadIt(string value, int expected)
    {
        Assert.Equal(expected, DashboardStateSnapshotOptions.Read(Configuration(value)).MaxBytes);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("lots")]
    [InlineData("99999999999")]
    public void Options_WithAnInvalidValue_FailNamingTheKeyAndTheValue(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DashboardStateSnapshotOptions.Read(Configuration(value)));

        Assert.Contains(DashboardStateSnapshotOptions.MaxBytesKey, ex.Message, StringComparison.Ordinal);
        Assert.Contains($"'{value}'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Composition_WithANegativeCap_Fails()
    {
        using var host = Host(maxBytes: "-1");

        var ex = Assert.Throws<InvalidOperationException>(() => host.CreateClient());

        Assert.Contains(DashboardStateSnapshotOptions.MaxBytesKey, ex.Message, StringComparison.Ordinal);
    }

    private WebApplicationFactory<Program> Host(Action<IServiceCollection>? services = null, string? maxBytes = null) =>
        _factory.WithWebHostBuilder(builder =>
        {
            if (maxBytes is not null)
                builder.UseSetting(DashboardStateSnapshotOptions.MaxBytesKey, maxBytes);
            if (services is not null)
                builder.ConfigureServices(services);
        });

    /// <summary>
    /// A saga that failed for a business reason: its InventoryReservationFailed step (id m1) moved it from
    /// AwaitingInventory to Failed, so the retry resets it to AwaitingInventory, which is what reaches
    /// ResetStateAsync and the recorder. <paramref name="laterEntries"/> follow the seeded snapshot.
    /// </summary>
    private static Task<Guid> SeedBusinessFailureAsync(WebApplicationFactory<Program> host, bool withSnapshot,
        string snapshotPayload = "{\"Version\":0,\"CurrentState\":\"Failed\"}", params SagaLogEntry[] laterEntries)
    {
        var entries = new List<SagaLogEntry>
        {
            SagaLogEntry.Create(Guid.Empty, SagaType, SagaEntryType.SagaStarted,
                toState: "Submitted", messageType: "OrderSubmitted", messageId: "m0", payloadJson: "{\"OrderId\":\"X\"}"),
            SagaLogEntry.Create(Guid.Empty, SagaType, SagaEntryType.MessageReceived,
                messageType: "InventoryReservationFailed", messageId: "m1", payloadJson: "{\"OrderId\":\"X\"}"),
            SagaLogEntry.Create(Guid.Empty, SagaType, SagaEntryType.StepSucceeded,
                fromState: "AwaitingInventory", toState: "Failed", messageType: "InventoryReservationFailed", messageId: "m1"),
            SagaLogEntry.Create(Guid.Empty, SagaType, SagaEntryType.SagaCompleted, toState: "Failed", messageId: "m1"),
        };
        if (withSnapshot)
            entries.Add(SagaLogEntry.Create(Guid.Empty, SagaType, SagaEntryType.StatePersisted, payloadJson: snapshotPayload));
        entries.AddRange(laterEntries);

        return SeedSagaAsync(host, "Failed", [.. entries]);
    }

    /// <summary>Inserts a Failed saga at version 0 and appends <paramref name="entries"/> under its correlation id.</summary>
    private static async Task<Guid> SeedSagaAsync(WebApplicationFactory<Program> host, string currentState, params SagaLogEntry[] entries)
    {
        var correlationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await host.Services.GetRequiredService<ISagaSnapshotStore<DashboardTestState>>().InsertAsync(new DashboardTestState
        {
            CorrelationId = correlationId,
            SagaType = SagaType,
            CurrentState = currentState,
            Status = SagaStatus.Failed,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });

        // Straight to the store, past any event-log decorator a test installs.
        var store = host.Services.GetRequiredService<InMemorySagaStore>();
        foreach (var entry in entries)
            await store.AppendAsync(entry with { CorrelationId = correlationId });

        return correlationId;
    }

    private static async Task<HttpResponseMessage> PostRetryAsync(WebApplicationFactory<Program> host, Guid correlationId)
    {
        using var client = Client(host);
        return await client.PostAsync($"/api/sagas/{SagaType}/{correlationId}/retry", null);
    }

    private static async Task<SagaDetail> GetDetailAsync(WebApplicationFactory<Program> host, Guid correlationId)
    {
        using var client = Client(host);
        return (await client.GetFromJsonAsync<SagaDetail>($"/api/sagas/{SagaType}/{correlationId}", JsonOptions))!;
    }

    private static async Task<List<SagaLogEntry>> ReadTimelineAsync(WebApplicationFactory<Program> host, Guid correlationId)
    {
        using var client = Client(host);
        return (await client.GetFromJsonAsync<List<SagaLogEntry>>($"/api/sagas/{SagaType}/{correlationId}/timeline", JsonOptions))!;
    }

    private static HttpClient Client(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", DashboardApiFactory.TestApiKey);
        return client;
    }

    private static int ReadVersion(string stateJson)
    {
        using var document = JsonDocument.Parse(stateJson);
        return document.RootElement.GetProperty("Version").GetInt32();
    }

    private static string Marker(int bytes, int limit) =>
        "{\"$vsagaStateOmitted\":true,\"bytes\":" + bytes.ToString(CultureInfo.InvariantCulture)
        + ",\"limit\":" + limit.ToString(CultureInfo.InvariantCulture) + "}";

    private static IConfiguration Configuration(string? maxBytes)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (maxBytes is not null)
            values[DashboardStateSnapshotOptions.MaxBytesKey] = maxBytes;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private sealed class RacedAdminStore : ISagaAdminStore
    {
        public Task ResetStateAsync(string sagaType, Guid correlationId, string currentState, SagaStatus status, int expectedVersion, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default) =>
            throw new SagaConcurrencyException(sagaType, correlationId, expectedVersion);
    }

    /// <summary>Resets, then moves the saga on by one more version, as a step committing right after the reset would.</summary>
    private sealed class AdvancingAdminStore(InMemorySagaStore inner) : ISagaAdminStore
    {
        public async Task ResetStateAsync(string sagaType, Guid correlationId, string currentState, SagaStatus status, int expectedVersion, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
        {
            await inner.ResetStateAsync(sagaType, correlationId, currentState, status, expectedVersion, updatedAtUtc, cancellationToken);
            await inner.ResetStateAsync(sagaType, correlationId, currentState, status, expectedVersion + 1, updatedAtUtc, cancellationToken);
        }
    }

    /// <summary>The in-memory event log, refusing StatePersisted appends.</summary>
    private sealed class SnapshotRefusingLog(InMemorySagaStore inner) : ISagaEventLogStore
    {
        public Task<long> AppendAsync(SagaLogEntry entry, CancellationToken cancellationToken = default) =>
            entry.EntryType == SagaEntryType.StatePersisted
                ? throw new InvalidOperationException("event log unavailable")
                : inner.AppendAsync(entry, cancellationToken);

        public Task<IReadOnlyList<SagaLogEntry>> GetTimelineAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
            inner.GetTimelineAsync(sagaType, correlationId, cancellationToken);

        public Task<bool> IsDuplicateAsync(string sagaType, Guid correlationId, string messageId, CancellationToken cancellationToken = default) =>
            inner.IsDuplicateAsync(sagaType, correlationId, messageId, cancellationToken);
    }
}
