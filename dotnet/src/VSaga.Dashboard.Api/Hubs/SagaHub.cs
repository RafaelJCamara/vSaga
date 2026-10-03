using VSaga.Abstractions.Persistence;
using Microsoft.AspNetCore.SignalR;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;

namespace VSaga.Dashboard.Api.Hubs;

public interface ISagaHubClient
{
    Task SagaUpdated(SagaSummary summary);

    Task TimelineEntryAdded(string sagaType, Guid correlationId, SagaLogEntry entry);
}

/// <summary>
/// Two subscription granularities, each checked against the caller's <c>sagas.view</c> access, resolved
/// afresh from the identity store on every subscription (design §8.6). The list view joins
/// <see cref="ListGroup"/> when the caller may view every saga type, or one
/// <see cref="ListGroupForType"/> group per saga type the caller may view; a detail view additionally
/// joins <see cref="GroupForSaga"/> for that one saga's timeline, only when that type is in scope. Neither
/// subscription throws when access is refused: it answers false and joins nothing, as a malformed id
/// already did, because the SPA invokes them fire-and-forget. Connections are recorded in
/// <see cref="HubConnectionRegistry"/>, which drops them when access changes.
/// </summary>
public sealed class SagaHub(ICallerAccessResolver resolver, HubConnectionRegistry registry) : Hub<ISagaHubClient>
{
    public const string ListGroup = "saga:list";

    /// <summary>
    /// The prefix of the per-type list groups. It cannot collide with <see cref="ListGroup"/> or an instance
    /// group, which both start with <c>saga:</c>.
    /// </summary>
    public const string TypeListGroupPrefix = "saga-list:";

    /// <summary>The <see cref="HubCallerContext.Items"/> key holding the list groups this connection joined.</summary>
    internal const string ListGroupsItem = "vsaga:list-groups";

    /// <summary>The list group of one saga type, for callers whose view is scoped to named types.</summary>
    public static string ListGroupForType(string sagaType) => TypeListGroupPrefix + sagaType;

    /// <summary>
    /// Keyed by the full saga instance identity, not the correlation id alone: two saga types may
    /// track the same correlation id, and a detail view subscribed to one of them must not receive
    /// the other's timeline entries.
    /// </summary>
    public static string GroupForSaga(string sagaType, Guid correlationId) => $"saga:{sagaType}:{correlationId}";

    /// <summary>
    /// Sends a changed saga to every group that may see it: the unscoped list, its type's list and its
    /// instance. Used by the in-process notifier and by the poller alike.
    /// </summary>
    public static async Task PushSagaUpdatedAsync(IHubContext<SagaHub, ISagaHubClient> hub, SagaSummary summary)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(summary);
        await hub.Clients.Group(ListGroup).SagaUpdated(summary);
        await hub.Clients.Group(ListGroupForType(summary.SagaType)).SagaUpdated(summary);
        await hub.Clients.Group(GroupForSaga(summary.SagaType, summary.CorrelationId)).SagaUpdated(summary);
    }

    public override Task OnConnectedAsync()
    {
        registry.Add(Context);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Joins the list groups the caller's view covers: <see cref="ListGroup"/> for every saga type, else one
    /// <see cref="ListGroupForType"/> group per named type, types that have not run yet included. The connection
    /// leaves any group an earlier call joined that the caller's access no longer covers. False, and no group,
    /// when the caller may view no saga type.
    /// </summary>
    public async Task<bool> SubscribeToList()
    {
        var scope = (await ResolveCallerAsync())?.Access.ScopeFor(Permissions.SagasView) ?? SagaTypeScope.None;
        string[] groups = scope.IsAll ? [ListGroup] : [.. scope.SagaTypes.Order(StringComparer.Ordinal).Select(ListGroupForType)];

        foreach (var stale in JoinedListGroups().Except(groups, StringComparer.Ordinal))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, stale);
        foreach (var group in groups)
            await Groups.AddToGroupAsync(Context.ConnectionId, group);

        Context.Items[ListGroupsItem] = groups;
        return groups.Length > 0;
    }

    /// <summary>Leaves the list groups <see cref="SubscribeToList"/> joined on this connection.</summary>
    public async Task UnsubscribeFromList()
    {
        foreach (var group in JoinedListGroups())
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);

        Context.Items.Remove(ListGroupsItem);
    }

    // correlationId is a string, not a Guid, deliberately: SignalR's default model binder rejects a
    // non-Guid-shaped argument by failing the whole hub invocation before this method body ever runs,
    // which surfaces client-side as "Failed to invoke 'SubscribeToSaga' due to an error on the
    // server" -- needless server-side noise for what's usually just a stale or hand-edited detail-page
    // URL. Parsing it ourselves lets that case join no group instead, matching how the REST endpoint
    // for the same malformed id already degrades (a clean 404, not a crash).
    /// <summary>
    /// Joins one saga's instance group when the caller holds <c>sagas.view</c> for its type; false, and no
    /// group, otherwise or when the correlation id is malformed.
    /// </summary>
    public async Task<bool> SubscribeToSaga(string? sagaType, string correlationId)
    {
        if (sagaType is null || !Guid.TryParse(correlationId, out var parsed))
            return false;

        if ((await ResolveCallerAsync())?.Access.Has(Permissions.SagasView, sagaType) != true)
            return false;

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupForSaga(sagaType, parsed));
        return true;
    }

    public Task UnsubscribeFromSaga(string sagaType, string correlationId) =>
        Guid.TryParse(correlationId, out var parsed)
            ? Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupForSaga(sagaType, parsed))
            : Task.CompletedTask;

    /// <summary>The caller as the store says now, not as it was when the connection opened; null when refused.</summary>
    private Task<CallerAccess?> ResolveCallerAsync() =>
        Context.User is { } user ? resolver.ResolveAsync(user, Context.ConnectionAborted) : Task.FromResult<CallerAccess?>(null);

    private string[] JoinedListGroups() =>
        Context.Items.TryGetValue(ListGroupsItem, out var joined) && joined is string[] groups ? groups : [];
}
