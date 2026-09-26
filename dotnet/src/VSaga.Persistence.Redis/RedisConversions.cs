using StackExchange.Redis;

namespace VSaga.Persistence.Redis;

/// <summary>Reads a script's reply with the shape the provider's Lua promises, throwing a protocol error -- never returning a default -- where it does not hold.</summary>
internal static class RedisConversions
{
    public static RedisResult[] AsArray(this RedisResult result) =>
        (RedisResult[]?)result ?? throw new RedisPersistenceProtocolException("A script returned nil where an array was expected; the Lua and the C# disagree.");

    public static string AsString(this RedisResult result) =>
        (string?)result ?? throw new RedisPersistenceProtocolException("A script returned nil where a string was expected; the Lua and the C# disagree.");
}
