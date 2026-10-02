using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using VSaga.Dashboard.Identity;

namespace VSaga.Dashboard.Api.Auth;

/// <summary>
/// The limits on the endpoints that check a password (design §8.4), taken by the endpoints themselves
/// rather than the rate-limiting middleware because two of the keys come from the request body.
/// <list type="bullet">
/// <item>Sign-in and password change: a fixed window of <see cref="DashboardSecuritySettings.AuthPerMinute"/>
/// attempts a minute per client address and username (hashed, after the username's normalisation), so one
/// noisy client cannot use up everyone's attempts behind a shared proxy address, and guesses against one
/// account from one address stay bounded. Lockout remains the per-account control.</item>
/// <item>First-run setup: the same number a minute per client address.</item>
/// <item>Password hashing: at most <see cref="DefaultHashingPermits"/> verifications at once across the API,
/// with a short queue, so a flood of sign-ins (unknown usernames are hashed too) cannot starve the rest of
/// the API; past the queue the answer is 429.</item>
/// </list>
/// </summary>
public sealed class AuthRateLimits : IDisposable
{
    /// <summary>The window the per-minute limits count in.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>What <c>Retry-After</c> says when the hashing queue is full.</summary>
    public static readonly TimeSpan BusyRetryAfter = TimeSpan.FromSeconds(1);

    private const int MaxKeyedUsernameLength = 256;

    private readonly PartitionedRateLimiter<string> _signIn;
    private readonly PartitionedRateLimiter<string> _setup;
    private readonly ConcurrencyLimiter _hashing;

    public AuthRateLimits(DashboardSecuritySettings settings)
        : this(settings?.AuthPerMinute ?? throw new ArgumentNullException(nameof(settings)), DefaultHashingPermits)
    {
    }

    /// <param name="perMinute">Attempts per window for one key.</param>
    /// <param name="hashingPermits">Password verifications that may run at once; as many again may queue.</param>
    public AuthRateLimits(int perMinute, int hashingPermits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(perMinute, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(hashingPermits, 1);
        _signIn = FixedWindowPerKey(perMinute);
        _setup = FixedWindowPerKey(perMinute);
        _hashing = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = hashingPermits,
            QueueLimit = hashingPermits,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    }

    /// <summary>Half the processor count, and never fewer than two.</summary>
    public static int DefaultHashingPermits => Math.Max(2, Environment.ProcessorCount / 2);

    /// <summary>One sign-in or password-change attempt by <paramref name="username"/> from <paramref name="clientAddress"/>.</summary>
    public RateLimitLease AcquireSignIn(string? clientAddress, string? username) =>
        _signIn.AttemptAcquire(AddressKey(clientAddress) + "|" + UsernameKey(username));

    /// <summary>One first-run setup attempt from <paramref name="clientAddress"/>.</summary>
    public RateLimitLease AcquireSetup(string? clientAddress) => _setup.AttemptAcquire(AddressKey(clientAddress));

    /// <summary>A permit to hash or verify a password; not acquired when the queue is full.</summary>
    public ValueTask<RateLimitLease> AcquireHashingAsync(CancellationToken cancellationToken) =>
        _hashing.AcquireAsync(1, cancellationToken);

    /// <summary>How long a refused caller should wait: the window's own answer, else <see cref="BusyRetryAfter"/>.</summary>
    public static TimeSpan RetryAfter(RateLimitLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : BusyRetryAfter;
    }

    public void Dispose()
    {
        _signIn.Dispose();
        _setup.Dispose();
        _hashing.Dispose();
    }

    private static PartitionedRateLimiter<string> FixedWindowPerKey(int perMinute) =>
        PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = perMinute,
            Window = Window,
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    private static string AddressKey(string? clientAddress) => string.IsNullOrEmpty(clientAddress) ? "-" : clientAddress;

    // Normalised as usernames are compared, and hashed so the limiter holds no submitted text; an absurdly
    // long value is cut first, since no username is that long anyway.
    private static string UsernameKey(string? username)
    {
        var normalized = (username ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length > MaxKeyedUsernameLength)
            normalized = normalized[..MaxKeyedUsernameLength];

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)), 0, 16);
    }
}
