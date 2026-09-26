using StackExchange.Redis;

namespace VSaga.Persistence.Redis;

/// <summary>
/// The one <see cref="ConnectionMultiplexer"/> the provider's stores share, connected on first use rather
/// than at registration so the host starts even while Redis is unreachable -- the same posture as the
/// EF Core host's non-fatal startup migration. <c>AbortOnConnectFail</c> defaults to false for the same
/// reason; the caller's <c>configureConnection</c> callback may override it.
/// </summary>
public sealed class RedisConnection : IAsyncDisposable, IDisposable
{
    private readonly ConfigurationOptions _configuration;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConnectionMultiplexer? _multiplexer;

    public RedisConnection(VSagaRedisOptions options, Action<ConfigurationOptions>? configureConnection = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _configuration = ConfigurationOptions.Parse(options.ConnectionString);
        _configuration.AbortOnConnectFail = false;
        // INFO and CONFIG GET, which the configuration probe depends on, are admin commands to the client.
        _configuration.AllowAdmin = true;
        _configuration.ClientName ??= "vsaga";
        configureConnection?.Invoke(_configuration);
    }

    /// <summary>The database every store writes to. Reads are never routed to a replica: the client's default routing is the primary.</summary>
    public async ValueTask<IDatabase> GetDatabaseAsync(CancellationToken cancellationToken = default) =>
        (await GetMultiplexerAsync(cancellationToken)).GetDatabase();

    /// <summary>
    /// The shared multiplexer, connecting on the first call. A connect that throws (a malformed
    /// configuration, say) is not cached, so the next call tries again rather than failing forever.
    /// </summary>
    public async ValueTask<IConnectionMultiplexer> GetMultiplexerAsync(CancellationToken cancellationToken = default)
    {
        if (_multiplexer is { } connected)
            return connected;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _multiplexer ??= await ConnectionMultiplexer.ConnectAsync(_configuration);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_multiplexer is { } multiplexer)
            await multiplexer.DisposeAsync();

        _gate.Dispose();
    }

    public void Dispose()
    {
        _multiplexer?.Dispose();
        _gate.Dispose();
    }
}
