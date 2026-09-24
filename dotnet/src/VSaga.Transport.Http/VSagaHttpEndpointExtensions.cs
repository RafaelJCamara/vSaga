using VSaga.Abstractions.Diagnostics;
using VSaga.Abstractions.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace VSaga.Transport.Http;

public static class VSagaHttpEndpointExtensions
{
    /// <summary>
    /// Maps this service's inbound receive endpoint at HttpTransportOptions.InboundPath. Returns the
    /// RouteHandlerBuilder rather than mapping it unauthenticated, so callers chain
    /// <c>.RequireAuthorization()</c> themselves -- vSaga ships no auth opinion for this endpoint.
    /// </summary>
    public static RouteHandlerBuilder MapVSagaHttp(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<HttpTransportOptions>();
        return endpoints.MapPost(options.InboundPath, HandleInboundAsync);
    }

    private static async Task HandleInboundAsync(HttpContext context, HttpInboundDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var messageTypeName = context.Request.Headers[HttpMessageTransport.MessageTypeHeader].ToString();
        var messageId = context.Request.Headers[HttpMessageTransport.MessageIdHeader].ToString();
        var correlationIdText = context.Request.Headers[HttpMessageTransport.CorrelationIdHeader].ToString();

        if (string.IsNullOrEmpty(messageTypeName) || string.IsNullOrEmpty(messageId) || !Guid.TryParse(correlationIdText, out var correlationId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var bodyStream = new MemoryStream();
        await context.Request.Body.CopyToAsync(bodyStream, cancellationToken);
        var body = bodyStream.ToArray();

        var headers = ExtractVSagaHeaders(context.Request.Headers);

        // The one ack context in this adapter that cannot honour requeue: true (docs/design/http-based-sagas.md
        // §4.4), and deliberately says so at error level instead of pretending. A delivery that arrives
        // as an inbound HTTP request is inseparable from that request -- it is dispatched inline under
        // the ambient SyncReplyCollector, and the status/body this peer gets back is decided by that
        // dispatch's own outcome. Re-enqueuing a copy onto the local channel (which is what the
        // transport's own EnqueueLocalDelivery deliveries do) would not redeliver *this* delivery: the
        // copy would run later off the pump with no collector installed and the response already
        // written, so a participant's reply publish -- the entire point of an inbound request on this
        // transport -- would find nothing to capture it and throw unroutable instead. The peer that
        // POSTed the message is the only party that can retry it, and it has already been told the
        // outcome. Both nack forms therefore log at error and drop, which is at least diagnosable.
        var ack = dispatcher.CreateInboundRequestAck(messageTypeName, correlationId, messageId);
        var received = new ReceivedMessage(messageTypeName, correlationId, messageId, body, headers, ack);

        // CancellationToken.None, not the request's RequestAborted -- exactly RabbitMqTransport's own
        // choice for the same call (DispatchReceivedAsync passes CancellationToken.None to the handler).
        // The handler's own outbound publishes (e.g. OrderShipped's fan-out back to the saga host) run
        // their own independent HTTP round trips with their own RequestTimeout-bound token; tying them
        // to *this* inbound connection's lifetime meant a client-side timeout, a proxy closing the
        // connection, or Kestrel's own keep-alive recycling could tear down a nested outbound call that
        // has nothing to do with the original request -- caught live: ~90% of ShipOrder handling failed
        // with a cancelled-socket exception on the nested OrderShipped POST until this was fixed.
        var result = await dispatcher.DispatchInlineAsync(received, CancellationToken.None);

        if (result.Reply is { } reply)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            HttpMessageTransport.ApplyVSagaHeaders((key, value) => context.Response.Headers[key] = value, reply.MessageTypeName, reply.Envelope);
            await context.Response.Body.WriteAsync(reply.Body, cancellationToken);
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
        }
    }

    /// <summary>Everything <c>x-vsaga-</c>-prefixed, plus the two bare W3C trace context headers (never prefixed -- interoperability with non-vSaga consumers is the point).</summary>
    private static IReadOnlyDictionary<string, string> ExtractVSagaHeaders(IHeaderDictionary headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in headers)
        {
            if (key.StartsWith("x-vsaga-", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, VSagaDiagnostics.TraceParentHeader, StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, VSagaDiagnostics.TraceStateHeader, StringComparison.OrdinalIgnoreCase))
                result[key] = StringValues.IsNullOrEmpty(values) ? string.Empty : values.ToString();
        }

        return result;
    }
}
