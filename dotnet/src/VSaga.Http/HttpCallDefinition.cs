using System.Net.Http.Headers;
using System.Text.Json;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace VSaga.Http;

/// <summary>
/// The shared execution engine behind both <c>.CallHttp(...)</c> and <c>ctx.CallHttpAsync(...)</c>:
/// everything from the URL down to the retry policy, minus whatever supplies the request body. Message-
/// type-agnostic on purpose -- the declarative form has a <typeparamref name="TState"/>/message-typed
/// body factory to adapt into <see cref="Func{TResult}"/>, and the imperative form's body is already a
/// plain captured value, so this is the one piece both can share without either depending on the other.
/// </summary>
internal sealed class HttpCallExecutor<TState>(
    string url,
    IReadOnlyDictionary<int, IHttpOutcomeAction<TState>> statusActions,
    IHttpOutcomeAction<TState>? successAction,
    IHttpOutcomeAction<TState>? failureAction,
    int maxAttempts,
    TimeSpan retryDelay)
    where TState : SagaState, new()
{
    private readonly string _host = DeriveDisplayHost(url);

    private static string DeriveDisplayHost(string url)
    {
        var uri = new Uri(url, UriKind.Absolute);
        var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        var firstSegment = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return firstSegment is null ? host : $"{host}/{firstSegment}";
    }

    /// <param name="context">The saga context this call executes within.</param>
    /// <param name="body">
    /// Null means no <c>.Body(...)</c> was configured at all (no request content, matching the
    /// pre-refactor <c>bodyFactory is null</c> check). Non-null is invoked once per retry attempt --
    /// see <c>ctx.CallHttpAsync</c>'s own remarks on why an eagerly-captured value passed as a
    /// <see cref="Func{TResult}"/> here still preserves per-attempt invocation semantics literally.
    /// </param>
    /// <param name="cancellationToken">Cancellation token observed by the call and its retries.</param>
    public async Task ExecuteAsync(ISagaContext<TState> context, Func<object?>? body, CancellationToken cancellationToken)
    {
        var httpClient = context.Services.GetRequiredService<IHttpClientFactory>().CreateClient(ServiceCollectionExtensions.ClientName);
        var log = (ISagaContextLogSink)context;
        var sagaType = context.Saga.SagaType;

        // This call's own outbound leg, minted independently of whatever ISagaContext.PublishAfterCommitAsync
        // logs for a loopback outcome below -- the whole point of §5.3's fix. Logged before sending so the
        // Timeline reads as "sent" then "received" even if the call never returns.
        var callId = Guid.NewGuid().ToString("N");
        await log.LogAsync(SagaLogEntry.Create(context.CorrelationId, sagaType, SagaEntryType.MessagePublished,
            messageType: $"POST {url}", messageId: callId, sourceService: sagaType, destinationService: _host), cancellationToken);

        var (statusCode, responseBody, transportError) = await SendWithRetryAsync(httpClient, body, cancellationToken);
        var action = ResolveAction(statusCode);

        await log.LogAsync(SagaLogEntry.Create(context.CorrelationId, sagaType, SagaEntryType.MessageReceived,
            messageType: action.DescribeReply(), messageId: Guid.NewGuid().ToString("N"),
            sourceService: _host, causationId: callId, errorMessage: transportError is null ? null : SagaErrorText.Describe(transportError)), cancellationToken);

        await action.ApplyAsync(context, responseBody, cancellationToken);
    }

    private async Task<(int? StatusCode, ReadOnlyMemory<byte> Body, Exception? TransportError)> SendWithRetryAsync(
        HttpClient httpClient, Func<object?>? body, CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            if (body is not null)
            {
                var value = body();
                request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, value!.GetType()));
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            try
            {
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                return ((int)response.StatusCode, responseBody, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                lastError = ex;
                if (attempt < maxAttempts)
                    await Task.Delay(retryDelay, cancellationToken);
            }
        }

        return (null, ReadOnlyMemory<byte>.Empty, lastError);
    }

    /// <summary>Exact status first, then the 2xx/everything-else buckets — see docs/design/http-based-sagas.md §5.4's mapping table. Unconfigured is a DSL error, not a silent no-op.</summary>
    private IHttpOutcomeAction<TState> ResolveAction(int? statusCode)
    {
        if (statusCode is { } code && statusActions.TryGetValue(code, out var exact))
            return exact;

        if (statusCode is >= 200 and <= 299)
        {
            return successAction
                   ?? throw new InvalidOperationException($".CallHttp to '{url}' received status {statusCode} but no .OnSuccess(...) was configured.");
        }

        return failureAction ?? throw new InvalidOperationException(statusCode is { } failedStatus
            ? $".CallHttp to '{url}' received status {failedStatus} but no .OnFailure(...) was configured."
            : $".CallHttp to '{url}' failed (network error or timeout) but no .OnFailure(...) was configured.");
    }
}

/// <summary>
/// The immutable, built form of one <c>.CallHttp(...)</c> configuration -- everything
/// <see cref="HttpCallBuilder{TState,TMessage}"/> collected, executed once per step invocation. A thin,
/// message-typed adapter over <see cref="HttpCallExecutor{TState}"/>: its only job is turning the eager
/// <c>bodyFactory(context, message)</c> shape into the shared executor's <see cref="Func{TResult}"/>, re-
/// closing over <c>message</c> fresh on every call so the factory still runs once per retry
/// attempt exactly as it always has.
/// </summary>
internal sealed class HttpCallDefinition<TState, TMessage>(
    string url,
    Func<ISagaContext<TState>, TMessage, object>? bodyFactory,
    IReadOnlyDictionary<int, IHttpOutcomeAction<TState>> statusActions,
    IHttpOutcomeAction<TState>? successAction,
    IHttpOutcomeAction<TState>? failureAction,
    int maxAttempts,
    TimeSpan retryDelay)
    where TState : SagaState, new()
    where TMessage : notnull
{
    private readonly HttpCallExecutor<TState> _executor = new(url, statusActions, successAction, failureAction, maxAttempts, retryDelay);

    public Task ExecuteAsync(ISagaContext<TState> context, TMessage message, CancellationToken cancellationToken) =>
        _executor.ExecuteAsync(context, bodyFactory is null ? null : () => bodyFactory(context, message), cancellationToken);
}
