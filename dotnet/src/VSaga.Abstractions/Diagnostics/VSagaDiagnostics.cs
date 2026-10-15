using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace VSaga.Abstractions.Diagnostics;

/// <summary>
/// Shared OpenTelemetry ActivitySource/Meter, defined once here so Core, Persistence, and Transport
/// packages all emit against the same names without depending on each other. VSaga.Observability
/// just wires these into an app's OTel exporters; the dashboard reads the persisted event log
/// instead, so it works even when no OTel collector is configured.
/// </summary>
public static class VSagaDiagnostics
{
    public const string ActivitySourceName = "VSaga.Saga";
    public const string MeterName = "VSaga.Saga";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");
    public static readonly Meter Meter = new(MeterName, "1.0.0");

    public static readonly Counter<long> SagasStarted = Meter.CreateCounter<long>("vsaga.saga.started");
    public static readonly Counter<long> SagasCompleted = Meter.CreateCounter<long>("vsaga.saga.completed");
    public static readonly Counter<long> SagasFailed = Meter.CreateCounter<long>("vsaga.saga.failed");
    public static readonly Counter<long> StepRetries = Meter.CreateCounter<long>("vsaga.saga.step.retries");
    public static readonly Histogram<double> StepDuration = Meter.CreateHistogram<double>("vsaga.saga.step.duration", "ms");
    public static readonly Histogram<double> SagaDuration = Meter.CreateHistogram<double>("vsaga.saga.duration", "ms");

    // Deliberately NOT an UpDownCounter<long> "vsaga.saga.running" -- production-readiness.md §6/§8.18
    // calls that a trap: an UpDownCounter is process-local and non-idempotent, so a restart, a
    // redelivery, or a second replica desynchronizes it permanently with no way to self-correct. "How
    // many sagas are running right now" needs an ObservableGauge backed by
    // `COUNT(*) WHERE Status = Running` against the store instead, which needs scoped-store access from
    // a meter callback that doesn't exist yet -- a named follow-up for VSaga.Observability, not built here.

    public const string TagSagaType = "saga.type";
    public const string TagSagaKind = "saga.kind";
    public const string TagCorrelationId = "saga.correlation_id";
    public const string TagFromState = "saga.from_state";
    public const string TagToState = "saga.to_state";

    /// <summary>The <c>StuckCause</c> a <c>vsaga.saga.stuck</c> measurement counts, by name (e.g. <c>TimeoutOverdue</c>).</summary>
    public const string TagStuckCause = "saga.stuck_cause";

    /// <summary>Tagged on a consumer span only when this delivery is a retry (production-readiness.md §6's "redelivery keeps the same trace" -- a retried delivery gets this tag instead of a fresh linked span).</summary>
    public const string TagDeliveryAttempt = "delivery.attempt";

    /// <summary>The W3C Trace Context header carrying trace id, parent span id, and trace flags. Bare name, not `x-vsaga-`-prefixed -- interoperability with non-vSaga consumers is the point.</summary>
    public const string TraceParentHeader = "traceparent";

    /// <summary>The W3C Trace Context header carrying vendor-specific trace state, alongside <see cref="TraceParentHeader"/>.</summary>
    public const string TraceStateHeader = "tracestate";

    private const string TraceParentVersion = "00";

    // "00-{32 hex trace id}-{16 hex span id}-{2 hex flags}" = 2 + 1 + 32 + 1 + 16 + 1 + 2.
    private const int TraceParentLength = 55;
    private const int TraceIdOffset = 3;
    private const int TraceIdHexLength = 32;
    private const int SpanIdOffset = 36;
    private const int SpanIdHexLength = 16;
    private const int FlagsOffset = 53;
    private const int FlagsHexLength = 2;

    /// <summary>
    /// Writes <paramref name="context"/> onto <paramref name="headers"/> as the standard W3C
    /// `traceparent` header (plus `tracestate`, if the context carries one). Hand-rolled rather than
    /// pulled from OpenTelemetry.Api, so this dependency-free leaf stays that way -- `traceparent` is
    /// a fixed 55-character string, cheap to format directly. A default/no-op <paramref name="context"/>
    /// (no current <see cref="Activity"/>) writes nothing.
    /// </summary>
    public static void Inject(ActivityContext context, IDictionary<string, string> headers)
    {
        if (context.TraceId == default || context.SpanId == default)
            return;

        headers[TraceParentHeader] =
            $"{TraceParentVersion}-{context.TraceId.ToHexString()}-{context.SpanId.ToHexString()}-{(byte)context.TraceFlags:x2}";

        if (!string.IsNullOrEmpty(context.TraceState))
            headers[TraceStateHeader] = context.TraceState;
    }

    /// <summary>
    /// Parses a `traceparent`/`tracestate` header pair back into an <see cref="ActivityContext"/>.
    /// Returns <see langword="false"/> -- and never throws -- when the header is absent or malformed,
    /// since most inbound messages today carry none.
    /// </summary>
    public static bool TryExtractActivityContext(IReadOnlyDictionary<string, string> headers, out ActivityContext context)
    {
        context = default;

        if (!headers.TryGetValue(TraceParentHeader, out var traceParent) || traceParent is null)
            return false;

        if (traceParent.Length != TraceParentLength)
            return false;

        if (traceParent[2] != '-' || traceParent[TraceIdOffset + TraceIdHexLength] != '-' ||
            traceParent[SpanIdOffset + SpanIdHexLength] != '-')
            return false;

        if (!traceParent.AsSpan(0, 2).SequenceEqual(TraceParentVersion)) // only this version is defined today; a future version's extra fields need different parsing
            return false;

        var traceIdSpan = traceParent.AsSpan(TraceIdOffset, TraceIdHexLength);
        var spanIdSpan = traceParent.AsSpan(SpanIdOffset, SpanIdHexLength);
        var flagsSpan = traceParent.AsSpan(FlagsOffset, FlagsHexLength);

        if (!IsLowercaseHex(traceIdSpan) || !IsLowercaseHex(spanIdSpan) || !IsLowercaseHex(flagsSpan))
            return false;

        ActivityTraceId traceId;
        ActivitySpanId spanId;
        try
        {
            // All-hex-zero is the W3C spec's own invalid-id sentinel; CreateFromString throws for it
            // rather than returning a zero id, so that case is caught here alongside genuinely malformed input.
            traceId = ActivityTraceId.CreateFromString(traceIdSpan);
            spanId = ActivitySpanId.CreateFromString(spanIdSpan);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        if (traceId == default || spanId == default)
            return false;

        if (!byte.TryParse(flagsSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flagsByte))
            return false;

        var traceState = headers.TryGetValue(TraceStateHeader, out var ts) ? ts : null;
        context = new ActivityContext(traceId, spanId, (ActivityTraceFlags)flagsByte, traceState, isRemote: true);
        return true;
    }

    private static bool IsLowercaseHex(ReadOnlySpan<char> span)
    {
        foreach (var c in span)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        }

        return true;
    }
}
