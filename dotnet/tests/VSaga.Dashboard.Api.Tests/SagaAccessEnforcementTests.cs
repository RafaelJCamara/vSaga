using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Dashboard.Api.Auth;
using VSaga.Dashboard.Api.Endpoints;
using VSaga.Dashboard.Identity;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Stores;
using VSaga.Persistence.InMemory;
using VSaga.Transport.InMemory;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// The saga endpoints enforce the permissions of docs/design/dashboard-usability-and-access.md §8.5: a route's
/// saga type out of scope is 403 before anything is read, retry needs <c>sagas.retry</c> for that type, the
/// cross-type lookups only return visible types, a caller without <c>sagas.data</c> gets no payloads, error
/// messages or state, and a retry records who asked.
/// </summary>
public sealed class SagaAccessEnforcementTests : IAsyncLifetime, IAsyncDisposable
{
    private const string Password = "correct horse battery";
    private const string Order = "OrderSaga";
    private const string Payment = "PaymentSaga";
    private const string ErrorText = "Card 4242 of customer C-17 was declined";

    private readonly DashboardApiFactory _factory = new();
    private readonly WebApplicationFactory<Program> _host;

    public SagaAccessEnforcementTests()
    {
        _host = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISagaSummaryReader>();
            services.AddSingleton(sp => new RecordingSummaryReader(sp.GetRequiredService<InMemorySagaStore>()));
            services.AddSingleton<ISagaSummaryReader>(sp => sp.GetRequiredService<RecordingSummaryReader>());
            services.RemoveAll<ISagaEventLogStore>();
            services.AddSingleton(sp => new RecordingEventLog(sp.GetRequiredService<InMemorySagaStore>()));
            services.AddSingleton<ISagaEventLogStore>(sp => sp.GetRequiredService<RecordingEventLog>());
        }));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // xunit 2 calls IAsyncLifetime.DisposeAsync, never a test class's IAsyncDisposable.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    // The factory stops every host derived from it, _host included.
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private RecordingSummaryReader Reads => _host.Services.GetRequiredService<RecordingSummaryReader>();

    private RecordingEventLog TimelineReads => _host.Services.GetRequiredService<RecordingEventLog>();

    [Fact]
    public async Task AViewer_CannotRetry()
    {
        var correlationId = await SeedRetryableAsync(_host, Order);
        using var viewer = await SignInAsync("viewer", TestSessions.AllTypes(BuiltInRoles.ViewerId));

        using var response = await viewer.PostAsync(RetryPath(Order, correlationId));

        await AssertForbiddenAsync(response, Permissions.SagasRetry, Order);
        Assert.DoesNotContain(await TimelineAsync(_host, Order, correlationId), e => e.EntryType == SagaEntryType.ManualRetryRequested);
        Assert.Empty(_host.Services.GetRequiredService<InMemoryMessageTransport>().GetPublished());
    }

    [Theory]
    [InlineData("")]
    [InlineData("/timeline")]
    [InlineData("/map")]
    [InlineData("/children")]
    [InlineData("/retry-plan")]
    public async Task ARouteTypeOutOfScope_Is403BeforeAnythingIsRead(string route)
    {
        var payment = await SeedRetryableAsync(_host, Payment);
        var order = await SeedRetryableAsync(_host, Order);
        using var viewer = await SignInAsync("scoped", TestSessions.ForTypes(BuiltInRoles.ViewerId, Order));

        using var outOfScope = await viewer.GetAsync($"/api/sagas/{Payment}/{payment}{route}");

        // A blank route type is decided for that blank value, which no grant names, not for "any type".
        using var blankType = await viewer.GetAsync($"/api/sagas/%20/{payment}{route}");
        using var inScope = await viewer.GetAsync($"/api/sagas/{Order}/{order}{route}");

        await AssertForbiddenAsync(outOfScope, Permissions.SagasView, Payment);
        await AssertForbiddenAsync(blankType, Permissions.SagasView, sagaType: " ");
        Assert.DoesNotContain(Reads.Calls, call => call.CorrelationId == payment);
        Assert.DoesNotContain(TimelineReads.Calls, call => call.CorrelationId == payment);
        Assert.Equal(HttpStatusCode.OK, inScope.StatusCode);
    }

    [Fact]
    public async Task ACallerWithoutSagasViewAnywhere_Is403OnTheListAndTheLookups()
    {
        var correlationId = await SeedAsync(_host, Order, SagaStatus.Running);
        var managers = await CreateRoleAsync("Managers", Permissions.AccessManage);
        using var manager = await SignInAsync("manager", TestSessions.AllTypes(managers));

        foreach (var path in new[] { "/api/sagas", "/api/saga-types", $"/api/correlations/{correlationId}" })
        {
            using var response = await manager.GetAsync(path);
            await AssertForbiddenAsync(response, Permissions.SagasView, sagaType: null);
        }
    }

    [Fact]
    public async Task ARetryOfATypeOutOfScope_Is403EvenForAScopedOperator_WhileItsOwnTypeRetries()
    {
        var order = await SeedRetryableAsync(_host, Order);
        var payment = await SeedRetryableAsync(_host, Payment);
        using var scopedOperator = await SignInAsync("scoped-operator", TestSessions.ForTypes(BuiltInRoles.OperatorId, Order));

        using var outOfScope = await scopedOperator.PostAsync(RetryPath(Payment, payment));
        using var inScope = await scopedOperator.PostAsync(RetryPath(Order, order));

        await AssertForbiddenAsync(outOfScope, Permissions.SagasRetry, Payment);

        // Nothing of the out-of-scope saga was read; checked before this test reads its timeline itself.
        Assert.DoesNotContain(Reads.Calls, call => call.CorrelationId == payment);
        Assert.DoesNotContain(TimelineReads.Calls, call => call.CorrelationId == payment);
        Assert.DoesNotContain(await TimelineAsync(_host, Payment, payment), e => e.EntryType == SagaEntryType.ManualRetryRequested);
        Assert.Equal(HttpStatusCode.Accepted, inScope.StatusCode);
        var published = Assert.Single(_host.Services.GetRequiredService<InMemoryMessageTransport>().GetPublished());
        Assert.Equal(order, published.Envelope.CorrelationId);
    }

    [Fact]
    public async Task SagaTypesCorrelationsAndChildren_OnlyListVisibleTypes()
    {
        var shared = Guid.NewGuid();
        await SeedAsync(_host, Order, SagaStatus.Running, correlationId: shared);
        await SeedAsync(_host, Payment, SagaStatus.Running, correlationId: shared);
        var parent = await SeedAsync(_host, Order, SagaStatus.Running);
        var orderChild = await SeedAsync(_host, Order, SagaStatus.Running, parent: (Order, parent));
        await SeedAsync(_host, Payment, SagaStatus.Running, parent: (Order, parent));
        using var viewer = await SignInAsync("scoped", TestSessions.ForTypes(BuiltInRoles.ViewerId, Order));
        using var everyType = await SignInAsync("everything", TestSessions.AllTypes(BuiltInRoles.ViewerId));

        var types = await ReadAsync(await viewer.GetAsync("/api/saga-types"));
        var correlations = await ReadAsync(await viewer.GetAsync($"/api/correlations/{shared}"));
        var children = await ReadAsync(await viewer.GetAsync($"/api/sagas/{Order}/{parent}/children"));

        Assert.Equal([Order], types.EnumerateArray().Select(t => t.GetProperty("sagaType").GetString()), StringComparer.Ordinal);
        Assert.Equal([Order], correlations.EnumerateArray().Select(s => s.GetProperty("sagaType").GetString()), StringComparer.Ordinal);
        Assert.Equal([orderChild], children.EnumerateArray().Select(s => s.GetProperty("correlationId").GetGuid()));

        // The same lookups unscoped still see both types, so the filter is what removed them.
        Assert.Equal(2, (await ReadAsync(await everyType.GetAsync("/api/saga-types"))).GetArrayLength());
        Assert.Equal(2, (await ReadAsync(await everyType.GetAsync($"/api/correlations/{shared}"))).GetArrayLength());
        Assert.Equal(2, (await ReadAsync(await everyType.GetAsync($"/api/sagas/{Order}/{parent}/children"))).GetArrayLength());
    }

    [Fact]
    public async Task TheList_ShowsOnlyVisibleTypes_AndABlankTypeFilterIsNoFilter()
    {
        await SeedAsync(_host, Order, SagaStatus.Running);
        await SeedAsync(_host, Order, SagaStatus.Failed);
        await SeedAsync(_host, Payment, SagaStatus.Running);
        using var viewer = await SignInAsync("scoped", TestSessions.ForTypes(BuiltInRoles.ViewerId, Order));

        var all = await ReadAsync(await viewer.GetAsync("/api/sagas"));
        var blank = await ReadAsync(await viewer.GetAsync("/api/sagas?sagaType=%20"));
        var other = await ReadAsync(await viewer.GetAsync($"/api/sagas?sagaType={Payment}"));

        foreach (var page in new[] { all, blank })
        {
            Assert.Equal(2, page.GetProperty("totalCount").GetInt32());
            Assert.All(page.GetProperty("items").EnumerateArray(), s => Assert.Equal(Order, s.GetProperty("sagaType").GetString()));
        }

        Assert.Equal(0, other.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, other.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task TheList_PastItsBound_Is400WithTheErrorAndTheLastReachablePage()
    {
        // Eleven types that have all run: a status filter can merge ten, and narrowing the names to the
        // types that ran keeps all eleven.
        var types = Enumerable.Range(0, ScopedSagaLister.MaxUnrankedTypes + 1).Select(i => $"Saga{i:00}").ToArray();
        foreach (var type in types)
            await SeedAsync(_host, type, SagaStatus.Failed);
        using var viewer = await SignInAsync("wide", TestSessions.ForTypes(BuiltInRoles.ViewerId, types));

        using var tooManyTypes = await viewer.GetAsync("/api/sagas?status=Failed");
        using var tooDeep = await viewer.GetAsync("/api/sagas?pageSize=100&page=101");

        Assert.Equal(HttpStatusCode.BadRequest, tooManyTypes.StatusCode);
        var body = await ReadAsync(tooManyTypes);
        Assert.Contains("sagaType", body.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, body.GetProperty("maxPage").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, tooDeep.StatusCode);
        Assert.Equal(100, (await ReadAsync(tooDeep)).GetProperty("maxPage").GetInt32());
    }

    [Fact]
    public async Task WithoutSagasData_PayloadsErrorMessagesAndStateAreRedacted_OnEveryPath()
    {
        var correlationId = await SeedRetryableAsync(_host, Order);
        var observers = await CreateViewOnlyRoleAsync();
        using var observer = await SignInAsync("observer", TestSessions.AllTypes(observers));
        Reads.Calls.Clear();

        var detail = await ReadAsync(await observer.GetAsync($"/api/sagas/{Order}/{correlationId}"));
        var timeline = await ReadAsync(await observer.GetAsync($"/api/sagas/{Order}/{correlationId}/timeline"));
        var map = await ReadAsync(await observer.GetAsync($"/api/sagas/{Order}/{correlationId}/map"));

        Assert.Equal(JsonValueKind.Null, detail.GetProperty("dataJson").ValueKind);
        Assert.DoesNotContain(Reads.Calls, call => string.Equals(call.Method, nameof(ISagaSummaryReader.GetDataJsonAsync), StringComparison.Ordinal));
        Assert.Contains(Reads.Calls, call => string.Equals(call.Method, nameof(ISagaSummaryReader.GetAsync), StringComparison.Ordinal));

        Assert.Contains(timeline.EnumerateArray(), IsStepFailed);
        Assert.All(timeline.EnumerateArray(), e =>
        {
            Assert.Equal(JsonValueKind.Null, e.GetProperty("payloadJson").ValueKind);
            Assert.Equal(JsonValueKind.Null, e.GetProperty("errorMessage").ValueKind);
        });

        var events = map.GetProperty("events").EnumerateArray().ToList();
        Assert.Contains(events, IsStepFailed);
        Assert.All(events, e => Assert.Equal(JsonValueKind.Null, e.GetProperty("errorMessage").ValueKind));
    }

    [Fact]
    public async Task WithSagasData_ForTheSagasType_PayloadsErrorMessagesAndStateAreServed()
    {
        var order = await SeedRetryableAsync(_host, Order);
        var payment = await SeedRetryableAsync(_host, Payment);
        var observers = await CreateViewOnlyRoleAsync();

        // View everywhere, data only for OrderSaga: what decides is sagas.data for the route's own type.
        using var caller = await SignInAsync(
            "mixed", TestSessions.AllTypes(observers), TestSessions.ForTypes(BuiltInRoles.ViewerId, Order));

        var detail = await ReadAsync(await caller.GetAsync($"/api/sagas/{Order}/{order}"));
        var timeline = await ReadAsync(await caller.GetAsync($"/api/sagas/{Order}/{order}/timeline"));
        var map = await ReadAsync(await caller.GetAsync($"/api/sagas/{Order}/{order}/map"));
        var otherTimeline = await ReadAsync(await caller.GetAsync($"/api/sagas/{Payment}/{payment}/timeline"));

        Assert.Equal(JsonValueKind.String, detail.GetProperty("dataJson").ValueKind);
        var failed = timeline.EnumerateArray().Single(IsStepFailed);
        Assert.Equal(ErrorText, failed.GetProperty("errorMessage").GetString());
        Assert.Equal("{\"Sku\":\"A\"}", failed.GetProperty("payloadJson").GetString());
        Assert.Contains(map.GetProperty("events").EnumerateArray(),
            e => string.Equals(e.GetProperty("errorMessage").GetString(), ErrorText, StringComparison.Ordinal));

        Assert.Contains(otherTimeline.EnumerateArray(), IsStepFailed);
        Assert.All(otherTimeline.EnumerateArray(), e =>
        {
            Assert.Equal(JsonValueKind.Null, e.GetProperty("errorMessage").ValueKind);
            Assert.Equal(JsonValueKind.Null, e.GetProperty("payloadJson").ValueKind);
        });
    }

    [Fact]
    public async Task TheDefaultViewerApiKey_ReadsEveryTypeWithData_ButCannotRetry()
    {
        await using var host = _factory.WithWebHostBuilder(builder => builder.UseSetting(DashboardSecuritySettings.ApiKeyRoleKey, ""));
        var correlationId = await SeedRetryableAsync(host, Payment);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, DashboardApiFactory.TestApiKey);

        var detail = await ReadAsync(await client.GetAsync($"/api/sagas/{Payment}/{correlationId}"));
        using var retry = await client.PostAsync(RetryPath(Payment, correlationId), content: null);

        Assert.Equal(JsonValueKind.String, detail.GetProperty("dataJson").ValueKind);
        await AssertForbiddenAsync(retry, Permissions.SagasRetry, Payment);
        Assert.DoesNotContain(await TimelineAsync(host, Payment, correlationId), e => e.EntryType == SagaEntryType.ManualRetryRequested);
        Assert.Empty(host.Services.GetRequiredService<InMemoryMessageTransport>().GetPublished());
    }

    [Fact]
    public async Task ARetry_IsAttributedToTheSignedInUser()
    {
        var correlationId = await SeedRetryableAsync(_host, Order);
        using var alice = await SignInAsync("alice", TestSessions.AllTypes(BuiltInRoles.OperatorId));

        using var response = await alice.PostAsync(RetryPath(Order, correlationId));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var retry = Assert.Single(await TimelineAsync(_host, Order, correlationId), e => e.EntryType == SagaEntryType.ManualRetryRequested);
        Assert.Equal("dashboard:alice", retry.SourceService);
    }

    [Fact]
    public async Task ARetry_IsAttributedToTheApiKey()
    {
        var correlationId = await SeedRetryableAsync(_host, Order);
        using var client = _host.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, DashboardApiFactory.TestApiKey);

        using var response = await client.PostAsync(RetryPath(Order, correlationId), content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var retry = Assert.Single(await TimelineAsync(_host, Order, correlationId), e => e.EntryType == SagaEntryType.ManualRetryRequested);
        Assert.Equal("dashboard:api-key", retry.SourceService);
    }

    private static string RetryPath(string sagaType, Guid correlationId) => $"/api/sagas/{sagaType}/{correlationId}/retry";

    private static bool IsStepFailed(JsonElement entry) =>
        string.Equals(entry.GetProperty("entryType").GetString(), nameof(SagaEntryType.StepFailed), StringComparison.Ordinal);

    private async Task<SignInClient> SignInAsync(string username, params AccessGrant[] grants)
    {
        await TestSessions.CreateUserWithPasswordAsync(_host.Services, username, Password, grants: grants);
        var client = await SignInClient.StartAsync(_host);
        using var login = await client.LoginAsync(username, Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    /// <summary>A custom role holding only <c>sagas.view</c>: the built-in Viewer includes <c>sagas.data</c>.</summary>
    private Task<Guid> CreateViewOnlyRoleAsync() => CreateRoleAsync("Observers", Permissions.SagasView);

    private async Task<Guid> CreateRoleAsync(string name, params string[] permissions)
    {
        var role = new DashboardRole(Guid.NewGuid(), name, null, IsBuiltIn: false, permissions);
        await using var scope = _host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDashboardIdentityStore>().CreateRoleAsync(role, CancellationToken.None);
        return role.Id;
    }

    private static async Task<Guid> SeedAsync(
        WebApplicationFactory<Program> host, string sagaType, SagaStatus status, Guid? correlationId = null, (string Type, Guid Id)? parent = null)
    {
        var id = correlationId ?? Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await host.Services.GetRequiredService<ISagaSnapshotStore<DashboardTestState>>().InsertAsync(new DashboardTestState
        {
            CorrelationId = id,
            SagaType = sagaType,
            CurrentState = "AwaitingInventory",
            Status = status,
            ParentSagaType = parent?.Type,
            ParentCorrelationId = parent?.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        return id;
    }

    /// <summary>A Failed saga whose step threw: retryable, with payloads and an error message in its timeline.</summary>
    private static async Task<Guid> SeedRetryableAsync(WebApplicationFactory<Program> host, string sagaType)
    {
        var id = await SeedAsync(host, sagaType, SagaStatus.Failed);
        var log = host.Services.GetRequiredService<ISagaEventLogStore>();
        await log.AppendAsync(SagaLogEntry.Create(id, sagaType, SagaEntryType.MessageReceived,
            messageType: "ReserveInventory", messageId: "m1", payloadJson: "{\"Sku\":\"A\"}", sourceService: "InventoryService"));
        await log.AppendAsync(SagaLogEntry.Create(id, sagaType, SagaEntryType.StepFailed,
            fromState: "AwaitingInventory", messageType: "ReserveInventory", messageId: "m1", payloadJson: "{\"Sku\":\"A\"}", errorMessage: ErrorText));
        return id;
    }

    private static Task<IReadOnlyList<SagaLogEntry>> TimelineAsync(WebApplicationFactory<Program> host, string sagaType, Guid correlationId) =>
        host.Services.GetRequiredService<ISagaEventLogStore>().GetTimelineAsync(sagaType, correlationId);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.BadRequest, $"Unexpected {(int)response.StatusCode}.");
            return await SignInClient.ReadJsonAsync(response);
        }
    }

    private static async Task AssertForbiddenAsync(HttpResponseMessage response, string permission, string? sagaType)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await SignInClient.ReadJsonAsync(response);
        Assert.Equal(AuthProblems.ForbiddenCode, problem.GetProperty("code").GetString());
        Assert.Equal(permission, problem.GetProperty("permission").GetString());
        Assert.Equal(sagaType, problem.GetProperty("sagaType").GetString());
    }

    /// <summary>The in-memory reader, recording each per-instance read so a test can tell that nothing was read.</summary>
    private sealed class RecordingSummaryReader(InMemorySagaStore inner) : ISagaSummaryReader
    {
        public ConcurrentBag<(string Method, string SagaType, Guid CorrelationId)> Calls { get; } = [];

        public Task<PagedResult<SagaSummary>> ListAsync(SagaListFilter filter, CancellationToken cancellationToken = default) =>
            inner.ListAsync(filter, cancellationToken);

        public Task<SagaSummary?> GetAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default)
        {
            Calls.Add((nameof(GetAsync), sagaType, correlationId));
            return inner.GetAsync(sagaType, correlationId, cancellationToken);
        }

        public Task<string?> GetDataJsonAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default)
        {
            Calls.Add((nameof(GetDataJsonAsync), sagaType, correlationId));
            return inner.GetDataJsonAsync(sagaType, correlationId, cancellationToken);
        }

        public Task<IReadOnlyList<SagaSummary>> FindByCorrelationIdAsync(Guid correlationId, CancellationToken cancellationToken = default) =>
            inner.FindByCorrelationIdAsync(correlationId, cancellationToken);

        public Task<IReadOnlyList<SagaSummary>> FindChildrenAsync(string parentSagaType, Guid parentCorrelationId, CancellationToken cancellationToken = default)
        {
            Calls.Add((nameof(FindChildrenAsync), parentSagaType, parentCorrelationId));
            return inner.FindChildrenAsync(parentSagaType, parentCorrelationId, cancellationToken);
        }

        public Task<IReadOnlyList<SagaTypeInfo>> GetSagaTypesAsync(CancellationToken cancellationToken = default) =>
            inner.GetSagaTypesAsync(cancellationToken);
    }

    /// <summary>The in-memory event log, recording each timeline read so a test can tell that no timeline was read.</summary>
    private sealed class RecordingEventLog(InMemorySagaStore inner) : ISagaEventLogStore
    {
        public ConcurrentBag<(string SagaType, Guid CorrelationId)> Calls { get; } = [];

        public Task<long> AppendAsync(SagaLogEntry entry, CancellationToken cancellationToken = default) =>
            inner.AppendAsync(entry, cancellationToken);

        public Task<IReadOnlyList<SagaLogEntry>> GetTimelineAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default)
        {
            Calls.Add((sagaType, correlationId));
            return inner.GetTimelineAsync(sagaType, correlationId, cancellationToken);
        }

        public Task<bool> IsDuplicateAsync(string sagaType, Guid correlationId, string messageId, CancellationToken cancellationToken = default) =>
            inner.IsDuplicateAsync(sagaType, correlationId, messageId, cancellationToken);
    }
}
