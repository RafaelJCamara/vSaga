using VSaga.Abstractions.Persistence;

namespace VSaga.Dashboard.Api.Endpoints;

/// <summary>
/// The one place the dashboard decides what of a saga's recorded data a caller may read. Two fields
/// count as data: <see cref="SagaLogEntry.PayloadJson"/> (message bodies and state snapshots) and
/// <see cref="SagaLogEntry.ErrorMessage"/>, the exception text saga code threw, which often names an
/// order, a customer or a declined card. Redaction nulls both together and never drops an entry, so
/// sequence numbers, the SPA's step fold and the map's join are the same for every caller; entry types,
/// states, message types and ids stay visible, so a redacted timeline still shows that a step failed.
/// The caller decides <c>includeData</c>; until the authentication work supplies it from the
/// <c>sagas.data</c> permission, every endpoint passes <see langword="true"/>.
/// </summary>
internal static class SagaTimelineRedaction
{
    /// <summary>
    /// Returns <paramref name="timeline"/> itself when <paramref name="includeData"/> is set, otherwise a
    /// copy of the same length and order with every entry passed through <see cref="WithoutData"/>.
    /// </summary>
    internal static IReadOnlyList<SagaLogEntry> Apply(IReadOnlyList<SagaLogEntry> timeline, bool includeData) =>
        includeData ? timeline : timeline.Select(WithoutData).ToList();

    /// <summary>
    /// The map counterpart of <see cref="Apply"/>: the map carries no payloads, but each
    /// <see cref="SagaMapEvent"/> copies its entry's error message, so that is nulled the same way.
    /// The summary, nodes, edges and <see cref="SagaMap.FailureEventIndex"/> are passed through unchanged;
    /// events keep their order and every field except the error message.
    /// </summary>
    internal static SagaMap ApplyToMap(SagaMap map, bool includeData) =>
        includeData
            ? map
            : map with { Events = map.Events.Select(e => e.ErrorMessage is null ? e : e with { ErrorMessage = null }).ToList() };

    /// <summary>
    /// <paramref name="entry"/> with <see cref="SagaLogEntry.PayloadJson"/> and
    /// <see cref="SagaLogEntry.ErrorMessage"/> nulled; the same instance when both already are.
    /// </summary>
    internal static SagaLogEntry WithoutData(SagaLogEntry entry) =>
        entry.PayloadJson is null && entry.ErrorMessage is null
            ? entry
            : entry with { PayloadJson = null, ErrorMessage = null };
}
