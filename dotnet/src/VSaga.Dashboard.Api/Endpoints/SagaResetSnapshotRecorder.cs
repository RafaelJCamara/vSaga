using System.Globalization;
using System.Text.Json;
using VSaga.Abstractions.Persistence;

namespace VSaga.Dashboard.Api.Endpoints;

/// <summary>
/// The dashboard's own state-snapshot setting, read once from configuration while composing and validated
/// there. <paramref name="MaxBytes"/> caps, in UTF-8 bytes, the StatePersisted entry the dashboard writes
/// after a retry reset; a larger state is recorded as the <c>$vsagaStateOmitted</c> size marker, and 0
/// records size-only markers. It is an upper bound only: a smaller cap the engine host recorded in the
/// saga's own timeline wins (<see cref="SagaResetSnapshotRecorder"/>).
/// </summary>
internal sealed record DashboardStateSnapshotOptions(int MaxBytes)
{
    internal const string MaxBytesKey = "Dashboard:StateSnapshots:MaxBytes";

    /// <summary>
    /// Reads <c>Dashboard:StateSnapshots:MaxBytes</c>: empty means <see cref="SagaStateSnapshot.DefaultMaxBytes"/>,
    /// anything else must be a non-negative integer, or composition fails here, naming the key and the
    /// value, rather than at the first retry.
    /// </summary>
    internal static DashboardStateSnapshotOptions Read(IConfiguration configuration)
    {
        var value = configuration[MaxBytesKey];
        if (string.IsNullOrWhiteSpace(value))
            return new DashboardStateSnapshotOptions(SagaStateSnapshot.DefaultMaxBytes);

        if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var maxBytes))
        {
            throw new InvalidOperationException(
                $"Invalid {MaxBytesKey} '{value}': expected a non-negative whole number of bytes "
                + $"(0 records size-only markers), or empty for the default of {SagaStateSnapshot.DefaultMaxBytes}.");
        }

        return new DashboardStateSnapshotOptions(maxBytes);
    }
}

/// <summary>
/// Records the state a dashboard retry reset leaves behind. A reset rewrites the stored state outside the
/// engine, so no engine snapshot describes it; without this entry the timeline's last StatePersisted would
/// still show the failed state while the saga already runs again from an earlier one. The redrive that
/// follows records its own snapshots through the engine as usual.
/// <para>
/// Best effort: <see cref="RecordAsync"/> never throws. The reset is committed by then and the redrive must
/// still be published, so any failure is logged and swallowed, and the only effect is a missing snapshot.
/// </para>
/// </summary>
internal sealed class SagaResetSnapshotRecorder(
    ISagaSummaryReader reader,
    ISagaEventLogStore log,
    DashboardStateSnapshotOptions options,
    ILogger<SagaResetSnapshotRecorder> logger)
{
    private const string OmittedKey = "$vsagaStateOmitted";

    /// <summary>
    /// Appends a StatePersisted entry with the saga's stored state, with no message identity, when all of
    /// these hold:
    /// <list type="bullet">
    /// <item><paramref name="timeline"/> (the timeline read before the reset) already holds a StatePersisted
    /// entry. The dashboard never writes a saga's first snapshot, so an engine host that records none
    /// (<c>RecordStateSnapshots = false</c>) is honoured without a second switch.</item>
    /// <item>The stored state exists and its <c>Version</c> is <paramref name="expectedVersion"/>, the version
    /// the reset wrote. Otherwise a step has already moved the saga on and its own snapshot records that
    /// state.</item>
    /// </list>
    /// The cap is <see cref="DashboardStateSnapshotOptions.MaxBytes"/>, lowered to the <c>limit</c> of the
    /// timeline's most recent <c>$vsagaStateOmitted</c> marker when there is one: a host that capped its
    /// snapshots, to 0 to keep state out of the log for instance, is not overridden by the dashboard. Budget
    /// markers (a <c>budget</c> key instead of <c>limit</c>) say the saga's allowance ran out, not what the
    /// host's cap is, so they are passed over.
    /// </summary>
    public async Task RecordAsync(string sagaType, Guid correlationId, int expectedVersion, IReadOnlyList<SagaLogEntry> timeline,
        CancellationToken cancellationToken)
    {
        if (!timeline.Any(e => e.EntryType == SagaEntryType.StatePersisted))
            return;

        try
        {
            var stateJson = await reader.GetDataJsonAsync(sagaType, correlationId, cancellationToken);
            if (stateJson is null || ReadVersion(stateJson) != expectedVersion)
                return;

            var maxBytes = Math.Min(options.MaxBytes, HostLimit(timeline) ?? int.MaxValue);
            await log.AppendAsync(SagaStateSnapshot.CreateEntry(correlationId, sagaType, stateJson, maxBytes), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not record the state left by the retry reset of saga {SagaType} correlation {CorrelationId} at version {Version}; the reset is committed and the redrive continues",
                sagaType, correlationId, expectedVersion);
        }
    }

    /// <summary>The stored state's top-level <c>Version</c>, or null when the blob has none.</summary>
    private static int? ReadVersion(string stateJson)
    {
        using var document = JsonDocument.Parse(stateJson);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("Version", out var version)
            && version.TryGetInt32(out var value)
                ? value
                : null;
    }

    /// <summary>
    /// The <c>limit</c> of the most recent StatePersisted size marker in <paramref name="timeline"/>, never
    /// below 0, or null when the timeline holds none.
    /// </summary>
    private static int? HostLimit(IReadOnlyList<SagaLogEntry> timeline)
    {
        for (var i = timeline.Count - 1; i >= 0; i--)
        {
            if (timeline[i] is { EntryType: SagaEntryType.StatePersisted, PayloadJson: { } payload }
                && MarkerLimit(payload) is { } limit)
            {
                return Math.Max(limit, 0);
            }
        }

        return null;
    }

    /// <summary>
    /// The <c>limit</c> of a <c>{"$vsagaStateOmitted":true,...,"limit":L}</c> marker, or null for a recorded
    /// state, a budget marker or anything unreadable. Only payloads that mention the marker key are parsed,
    /// so the full state blobs a timeline mostly holds are not.
    /// </summary>
    private static int? MarkerLimit(string payload)
    {
        if (!payload.Contains(OmittedKey, StringComparison.Ordinal))
            return null;

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(OmittedKey, out var omitted) && omitted.ValueKind == JsonValueKind.True
                && root.TryGetProperty("limit", out var limit) && limit.TryGetInt32(out var value)
                    ? value
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
