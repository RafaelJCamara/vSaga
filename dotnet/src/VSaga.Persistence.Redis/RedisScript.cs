using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace VSaga.Persistence.Redis;

/// <summary>
/// One reviewed Lua script and the discipline for running it: <c>EVALSHA</c> first, and on
/// <c>NOSCRIPT</c> -- the script cache is emptied by a restart, a <c>SCRIPT FLUSH</c>, or a replica taking
/// over as primary -- a single retry as <c>EVAL</c>, which both runs the script and reloads the cache.
/// Handled here rather than left to the client, because a <c>NOSCRIPT</c> that escaped would not be a
/// domain exception: it would reach the engine's infrastructure-failure path and redeliver every
/// in-flight message at once, at the exact moment a failover has already degraded the system.
/// </summary>
internal sealed class RedisScript
{
    private const string NoScriptPrefix = "NOSCRIPT";

    public RedisScript(string body)
    {
        Body = body;
#pragma warning disable CA5350, S4790 // SHA-1 is not a security choice here: it is the digest Redis itself keys its script cache by (EVALSHA), so nothing else identifies the script
        Sha1 = SHA1.HashData(Encoding.UTF8.GetBytes(body));
#pragma warning restore CA5350, S4790
    }

    public string Body { get; }

    public byte[] Sha1 { get; }

    public async Task<RedisResult> EvaluateAsync(IDatabase db, RedisKey[] keys, RedisValue[] values)
    {
        try
        {
            return await db.ScriptEvaluateAsync(Sha1, keys, values);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith(NoScriptPrefix, StringComparison.Ordinal))
        {
            var args = new List<object>(2 + keys.Length + values.Length) { Body, keys.Length };
            args.AddRange(keys.Cast<object>());
            args.AddRange(values.Cast<object>());
            return await db.ExecuteAsync("EVAL", args);
        }
    }
}
