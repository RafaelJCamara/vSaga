using VSaga.Dashboard.Api.Auth;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// The windows are per client address and normalised username (sign-in) or per address (setup), and the
/// hashing limit refuses once its permits and its short queue are taken.
/// </summary>
public sealed class AuthRateLimitsTests
{
    [Fact]
    public void SignIn_CountsPerAddressAndNormalisedUsername()
    {
        using var limits = new AuthRateLimits(perMinute: 1, hashingPermits: 2);

        using var first = limits.AcquireSignIn("10.0.0.1", "alice");
        using var sameUserOtherCase = limits.AcquireSignIn("10.0.0.1", " ALICE ");
        using var otherUser = limits.AcquireSignIn("10.0.0.1", "bob");
        using var otherAddress = limits.AcquireSignIn("10.0.0.2", "alice");

        Assert.True(first.IsAcquired);
        Assert.False(sameUserOtherCase.IsAcquired);
        Assert.True(AuthRateLimits.RetryAfter(sameUserOtherCase) > TimeSpan.Zero);
        Assert.True(otherUser.IsAcquired);
        Assert.True(otherAddress.IsAcquired);
    }

    [Fact]
    public void Setup_CountsPerAddress_ApartFromSignIn()
    {
        using var limits = new AuthRateLimits(perMinute: 1, hashingPermits: 2);

        using var signIn = limits.AcquireSignIn("10.0.0.1", "alice");
        using var first = limits.AcquireSetup("10.0.0.1");
        using var second = limits.AcquireSetup("10.0.0.1");
        using var otherAddress = limits.AcquireSetup("10.0.0.2");

        Assert.True(first.IsAcquired);
        Assert.False(second.IsAcquired);
        Assert.True(otherAddress.IsAcquired);
    }

    [Fact]
    public async Task Hashing_RefusesOncePermitsAndQueueAreTaken()
    {
        using var limits = new AuthRateLimits(perMinute: 20, hashingPermits: 1);

        using var running = await limits.AcquireHashingAsync(CancellationToken.None);
        var queued = limits.AcquireHashingAsync(CancellationToken.None);
        using var refused = await limits.AcquireHashingAsync(CancellationToken.None);

        Assert.True(running.IsAcquired);
        Assert.False(queued.IsCompleted);
        Assert.False(refused.IsAcquired);
        Assert.Equal(AuthRateLimits.BusyRetryAfter, AuthRateLimits.RetryAfter(refused));

        running.Dispose();
        using var dequeued = await queued;
        Assert.True(dequeued.IsAcquired);
    }

    [Fact]
    public void TheDefaultHashingPermits_AreHalfTheProcessors_AtLeastTwo() =>
        Assert.Equal(Math.Max(2, Environment.ProcessorCount / 2), AuthRateLimits.DefaultHashingPermits);
}
