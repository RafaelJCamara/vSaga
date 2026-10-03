using System.Security.Claims;
using VSaga.Abstractions.Persistence;
using VSaga.Dashboard.Api.Hubs;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// Hand-written recording doubles for the SignalR surface, matching this repo's existing convention of
/// purpose-built fakes over a mocking library (see <c>FlakyEventLogStore</c> /
/// <c>RaceInjectingSnapshotStore</c> in VSaga.Core.Tests). Only the members the hub and notifier
/// actually use are implemented; the rest throw, so a future code path that starts addressing clients
/// some other way fails loudly here instead of being silently unasserted.
/// </summary>
internal sealed record SagaUpdatedCall(string Group, SagaSummary Summary);

internal sealed record TimelineEntryCall(string Group, string SagaType, Guid CorrelationId, SagaLogEntry Entry);

internal sealed class RecordingHubClient(string group, RecordingHubClients parent) : ISagaHubClient
{
    public Task SagaUpdated(SagaSummary summary)
    {
        parent.SagaUpdates.Add(new SagaUpdatedCall(group, summary));
        return Task.CompletedTask;
    }

    public Task TimelineEntryAdded(string sagaType, Guid correlationId, SagaLogEntry entry)
    {
        parent.TimelineEntries.Add(new TimelineEntryCall(group, sagaType, correlationId, entry));
        return Task.CompletedTask;
    }
}

internal sealed class RecordingHubClients : IHubClients<ISagaHubClient>
{
    public List<SagaUpdatedCall> SagaUpdates { get; } = [];

    public List<TimelineEntryCall> TimelineEntries { get; } = [];

    public ISagaHubClient Group(string groupName) => new RecordingHubClient(groupName, this);

    public ISagaHubClient All => throw new NotSupportedException("Production code addresses groups only.");

    public ISagaHubClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

    public ISagaHubClient Client(string connectionId) => throw new NotSupportedException();

    public ISagaHubClient Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();

    public ISagaHubClient Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();

    public ISagaHubClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

    public ISagaHubClient User(string userId) => throw new NotSupportedException();

    public ISagaHubClient Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
}

internal sealed class RecordingHubContext : IHubContext<SagaHub, ISagaHubClient>
{
    public RecordingHubClients Recorder { get; } = new();

    public IHubClients<ISagaHubClient> Clients => Recorder;

    public IGroupManager Groups { get; } = new RecordingGroupManager();
}

internal sealed record GroupMembershipChange(string ConnectionId, string GroupName);

internal sealed class RecordingGroupManager : IGroupManager
{
    public List<GroupMembershipChange> Added { get; } = [];

    public List<GroupMembershipChange> Removed { get; } = [];

    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        Added.Add(new GroupMembershipChange(connectionId, groupName));
        return Task.CompletedTask;
    }

    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        Removed.Add(new GroupMembershipChange(connectionId, groupName));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Minimal <see cref="HubCallerContext"/>: the connection id, the principal the connection authenticated as
/// (none by default), the per-connection items, and whether <see cref="Abort"/> was called.
/// </summary>
internal sealed class TestHubCallerContext(string connectionId, ClaimsPrincipal? user = null) : HubCallerContext
{
    private int _aborts;

    public override string ConnectionId { get; } = connectionId;

    public override string? UserIdentifier => null;

    public override ClaimsPrincipal? User { get; } = user;

    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

    public override IFeatureCollection Features { get; } = new FeatureCollection();

    public override CancellationToken ConnectionAborted => CancellationToken.None;

    /// <summary>How many times the connection was aborted.</summary>
    public int Aborts => Volatile.Read(ref _aborts);

    public override void Abort() => Interlocked.Increment(ref _aborts);
}

/// <summary>
/// An <see cref="ICallerAccessResolver"/> that answers <see cref="Caller"/>, whatever the principal, and counts
/// the calls, so a test can change the answer between two subscriptions and see that each one asked again.
/// </summary>
internal sealed class StubCallerAccessResolver(CallerAccess? caller) : ICallerAccessResolver
{
    private int _calls;

    public CallerAccess? Caller { get; set; } = caller;

    public int Calls => Volatile.Read(ref _calls);

    /// <summary>A signed-in user holding <paramref name="grants"/> of the built-in roles.</summary>
    public static CallerAccess UserWith(params AccessGrant[] grants) =>
        new(CallerKind.User, Guid.NewGuid(), "someone", "Someone", MustChangePassword: false, AccessEvaluator.EvaluateGrants(grants, BuiltInRoles.All));

    /// <summary>A resolver whose caller may view every saga type.</summary>
    public static StubCallerAccessResolver FullAccess() =>
        new(UserWith(new AccessGrant(BuiltInRoles.ViewerId, AllSagaTypes: true, [])));

    public Task<CallerAccess?> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(Caller);
    }
}
