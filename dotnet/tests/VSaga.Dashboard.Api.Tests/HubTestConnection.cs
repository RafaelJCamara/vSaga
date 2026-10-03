using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using VSaga.Dashboard.Api.Auth;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// A real connection to <c>/hubs/saga</c> over TestServer's WebSocket client, speaking just enough of the SignalR
/// JSON protocol to negotiate, connect, invoke a hub method and read pushes, so the hub tests go through
/// authentication, the origin guard and the connection registry as the SPA's client does, without a client
/// package. Every wait is bounded, so a missing message fails the test instead of hanging it.
/// </summary>
internal sealed class HubTestConnection : IAsyncDisposable
{
    /// <summary>How long a test waits for a message, or for the server to close the connection.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string NegotiatePath = "/hubs/saga/negotiate?negotiateVersion=1";
    private const int InvocationType = 1;
    private const int CompletionType = 3;
    private const int CloseType = 7;

    // The protocol's record separator, built from its code so no escape sequence is involved.
    private static readonly char RecordSeparator = (char)0x1E;

    // What ReceiveAsync answers for a close frame: the protocol's own close message, so callers handle one case.
    private static readonly JsonElement ClosedByServer = JsonSerializer.Deserialize<JsonElement>("""{"type":7}""");

    private readonly WebSocket _socket;
    private readonly Queue<JsonElement> _received = new();
    private readonly Queue<JsonElement> _pushes = new();
    private readonly StringBuilder _partial = new();
    private int _lastInvocationId;

    private HubTestConnection(WebSocket socket) => _socket = socket;

    /// <summary>
    /// POSTs the negotiate request with <paramref name="cookieHeader"/>, <paramref name="origin"/> and
    /// <paramref name="apiKey"/> (as the <c>X-Api-Key</c> header), each only when given.
    /// </summary>
    public static async Task<HttpResponseMessage> NegotiateAsync(
        WebApplicationFactory<Program> host, string? cookieHeader, string? origin = null, string path = NegotiatePath, string? apiKey = null)
    {
        using var http = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (cookieHeader is not null)
            request.Headers.Add("Cookie", cookieHeader);
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        if (apiKey is not null)
            request.Headers.Add(ApiKeyAuthenticationDefaults.HeaderName, apiKey);

        return await http.SendAsync(request);
    }

    /// <summary>
    /// Negotiates and opens a WebSocket connection authenticated by <paramref name="cookieHeader"/> (a session) or by
    /// <paramref name="apiKey"/> (the API key, sent as a header on both requests), then completes the handshake.
    /// </summary>
    public static async Task<HubTestConnection> ConnectAsync(WebApplicationFactory<Program> host, string? cookieHeader, string? apiKey = null)
    {
        using var negotiate = await NegotiateAsync(host, cookieHeader, apiKey: apiKey);
        negotiate.EnsureSuccessStatusCode();
        var body = await SignInClient.ReadJsonAsync(negotiate);
        var token = body.GetProperty("connectionToken").GetString();

        var client = host.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            if (cookieHeader is not null)
                request.Headers.Cookie = cookieHeader;
            if (apiKey is not null)
                request.Headers[ApiKeyAuthenticationDefaults.HeaderName] = apiKey;
        };
        var socket = await client.ConnectAsync(new Uri($"ws://localhost/hubs/saga?id={Uri.EscapeDataString(token!)}"), CancellationToken.None);

        var connection = new HubTestConnection(socket);
        await connection.SendRecordAsync("""{"protocol":"json","version":1}""");
        var handshake = await connection.ReceiveAsync();
        Assert.False(handshake.TryGetProperty("error", out var error), $"The hub refused the handshake: {error}");
        return connection;
    }

    /// <summary>Invokes <paramref name="target"/> and returns the completion's result; pushes that arrive first are kept for <see cref="NextPushAsync"/>.</summary>
    public async Task<JsonElement> InvokeAsync(string target, params object?[] arguments)
    {
        var invocationId = (++_lastInvocationId).ToString(CultureInfo.InvariantCulture);
        await SendRecordAsync(JsonSerializer.Serialize(new { type = InvocationType, invocationId, target, arguments }));

        while (true)
        {
            var message = await ReceiveAsync();
            switch (Type(message))
            {
                case CompletionType when string.Equals(message.GetProperty("invocationId").GetString(), invocationId, StringComparison.Ordinal):
                    Assert.False(message.TryGetProperty("error", out var error), $"{target} failed on the server: {error}");
                    return message.TryGetProperty("result", out var result) ? result : default;
                case InvocationType:
                    _pushes.Enqueue(message);
                    break;
                case CloseType:
                    throw new InvalidOperationException($"The hub closed the connection while {target} was pending: {message}");
            }
        }
    }

    /// <summary>The next push the server sent, as <c>(target, arguments)</c>.</summary>
    public async Task<(string Target, JsonElement Arguments)> NextPushAsync()
    {
        while (_pushes.Count == 0)
        {
            var message = await ReceiveAsync();
            if (Type(message) == InvocationType)
                _pushes.Enqueue(message);
            else if (Type(message) == CloseType)
                throw new InvalidOperationException($"The hub closed the connection while a push was awaited: {message}");
        }

        var push = _pushes.Dequeue();
        return (push.GetProperty("target").GetString()!, push.GetProperty("arguments"));
    }

    /// <summary>
    /// The close message the server ends the connection with, once it does so within <paramref name="within"/>
    /// (<see cref="Timeout"/> by default): the protocol's own message (<c>{"type":7}</c>, with
    /// <c>allowReconnect</c> when the client may reconnect). When the socket ends without one (a close frame or
    /// a broken socket) the answer is a bare <c>{"type":7}</c>, so it never claims <c>allowReconnect</c>. Null
    /// when the connection is still open then. Messages read meanwhile are discarded.
    /// </summary>
    public async Task<JsonElement?> WaitForCloseAsync(TimeSpan? within = null)
    {
        using var timeout = new CancellationTokenSource(within ?? Timeout);
        try
        {
            while (true)
            {
                var message = await ReceiveAsync(timeout.Token);
                if (Type(message) == CloseType)
                    return message;
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException
            || (ex is OperationCanceledException && !timeout.IsCancellationRequested))
        {
            return ClosedByServer;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Asserts that <paramref name="close"/> (what <see cref="WaitForCloseAsync"/> returned) is a close message that
    /// allows the client to reconnect, as SignalR sends for an expired ticket; <c>Abort()</c> would send one that does not.
    /// </summary>
    public static void AssertClosedForReconnect(JsonElement? close)
    {
        Assert.NotNull(close);
        Assert.True(
            close.Value.TryGetProperty("allowReconnect", out var allowed) && allowed.GetBoolean(),
            $"The connection was not closed with a message that allows reconnecting: {close}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(Timeout);
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
            {
                // Already going away; nothing to tidy.
            }
        }

        _socket.Dispose();
    }

    private static int Type(JsonElement message) =>
        message.TryGetProperty("type", out var type) ? type.GetInt32() : 0;

    private Task SendRecordAsync(string json) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(json + RecordSeparator), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    private async Task<JsonElement> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = cancellationToken.CanBeCanceled ? null : new CancellationTokenSource(Timeout);
        var token = timeout?.Token ?? cancellationToken;
        var buffer = new byte[16 * 1024];
        while (_received.Count == 0)
        {
            var result = await _socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close)
                return ClosedByServer;

            _partial.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            var text = _partial.ToString();
            var end = text.LastIndexOf(RecordSeparator);
            if (end < 0)
                continue;

            foreach (var record in text[..end].Split(RecordSeparator))
                _received.Enqueue(JsonSerializer.Deserialize<JsonElement>(record));

            _partial.Clear().Append(text[(end + 1)..]);
        }

        return _received.Dequeue();
    }

}
