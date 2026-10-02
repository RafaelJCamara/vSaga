using Microsoft.AspNetCore.Identity;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Services;
using static VSaga.Dashboard.Identity.Tests.Services.ServiceTestContext;

namespace VSaga.Dashboard.Identity.Tests.Services;

public sealed class CredentialVerifierTests : IAsyncLifetime
{
    private ServiceTestContext _context = null!;

    public async Task InitializeAsync() => _context = await CreateAsync();

    public async Task DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task Verify_RightPassword_SignsInAndRecordsIt()
    {
        var alice = await _context.SeedUserAsync("Alice");

        var result = await _context.NewVerifier().VerifyAsync("  alice ", StrongPassword, None);

        Assert.True(result.Succeeded);
        Assert.Equal(CredentialFailure.None, result.Failure);
        Assert.Equal(alice.Id, result.User!.Id);
        Assert.Equal(T0, result.User.LastSignInAtUtc);
        Assert.Equal(T0, (await _context.ReadUserAsync(alice.Id)).LastSignInAtUtc);
    }

    [Fact]
    public async Task Verify_UnknownUser_FailsAndWritesNothing()
    {
        var alice = await _context.SeedUserAsync("alice");

        var result = await _context.NewVerifier().VerifyAsync("bob", StrongPassword, None);

        Assert.False(result.Succeeded);
        Assert.Equal(CredentialFailure.UnknownUser, result.Failure);
        Assert.Equal(alice, await _context.ReadUserAsync(alice.Id), UserEquality.Instance);
    }

    [Fact]
    public async Task Verify_WrongPassword_CountsTheFailure()
    {
        var alice = await _context.SeedUserAsync("alice");

        var result = await _context.NewVerifier().VerifyAsync("alice", "not the password", None);

        Assert.Equal(CredentialFailure.WrongPassword, result.Failure);
        Assert.Null(result.LockedUntilUtc);
        Assert.Equal(1, (await _context.ReadUserAsync(alice.Id)).FailedSignInCount);
    }

    [Fact]
    public async Task Verify_LocksAtTheThreshold_ThenRefusesEvenTheRightPasswordWithoutCounting()
    {
        var alice = await _context.SeedUserAsync("alice");
        var verifier = _context.NewVerifier();

        for (var i = 1; i < 5; i++)
            Assert.Null((await verifier.VerifyAsync("alice", "wrong " + i, None)).LockedUntilUtc);
        var fifth = await verifier.VerifyAsync("alice", "wrong 5", None);

        Assert.Equal(CredentialFailure.WrongPassword, fifth.Failure);
        Assert.Equal(T0.AddMinutes(15), fifth.LockedUntilUtc);

        _context.Time.Advance(TimeSpan.FromMinutes(14));
        var whileLocked = await verifier.VerifyAsync("alice", StrongPassword, None);
        var wrongWhileLocked = await verifier.VerifyAsync("alice", "wrong 6", None);

        Assert.Equal(CredentialFailure.LockedOut, whileLocked.Failure);
        Assert.Equal(CredentialFailure.LockedOut, wrongWhileLocked.Failure);
        var stored = await _context.ReadUserAsync(alice.Id);
        Assert.Equal(0, stored.FailedSignInCount);
        Assert.Equal(T0.AddMinutes(15), stored.LockoutEndUtc);
        Assert.Null(stored.LastSignInAtUtc);
    }

    [Fact]
    public async Task Verify_AfterTheLockoutExpires_SignsInAndClearsIt()
    {
        var alice = await _context.SeedUserAsync("alice");
        var verifier = _context.NewVerifier();
        for (var i = 0; i < 5; i++)
            await verifier.VerifyAsync("alice", "wrong", None);

        _context.Time.Advance(TimeSpan.FromMinutes(15));
        var result = await verifier.VerifyAsync("alice", StrongPassword, None);

        Assert.True(result.Succeeded);
        var stored = await _context.ReadUserAsync(alice.Id);
        Assert.Null(stored.LockoutEndUtc);
        Assert.Equal(0, stored.FailedSignInCount);
        Assert.Equal(T0.AddMinutes(15), stored.LastSignInAtUtc);
    }

    [Fact]
    public async Task Verify_Success_ResetsTheFailureCount()
    {
        var alice = await _context.SeedUserAsync("alice");
        var verifier = _context.NewVerifier();
        await verifier.VerifyAsync("alice", "wrong", None);
        await verifier.VerifyAsync("alice", "wrong", None);

        await verifier.VerifyAsync("alice", StrongPassword, None);
        await verifier.VerifyAsync("alice", "wrong", None);

        Assert.Equal(1, (await _context.ReadUserAsync(alice.Id)).FailedSignInCount);
    }

    [Fact]
    public async Task Verify_WithLockoutDisabled_NeverLocks()
    {
        await using var context = await CreateAsync(DashboardSecuritySettings.Default with { LockoutMaxFailedAttempts = 0 });
        var alice = await context.SeedUserAsync("alice");
        var verifier = context.NewVerifier();

        for (var i = 0; i < 8; i++)
            Assert.Null((await verifier.VerifyAsync("alice", "wrong", None)).LockedUntilUtc);

        Assert.True((await verifier.VerifyAsync("alice", StrongPassword, None)).Succeeded);
        Assert.Null((await context.ReadUserAsync(alice.Id)).LockoutEndUtc);
    }

    [Fact]
    public async Task Verify_DisabledUser_IsRefusedWithoutCounting()
    {
        var alice = await _context.SeedUserAsync("alice", isEnabled: false);

        var right = await _context.NewVerifier().VerifyAsync("alice", StrongPassword, None);
        var wrong = await _context.NewVerifier().VerifyAsync("alice", "wrong", None);

        Assert.Equal(CredentialFailure.Disabled, right.Failure);
        Assert.Equal(CredentialFailure.Disabled, wrong.Failure);
        Assert.Equal(0, (await _context.ReadUserAsync(alice.Id)).FailedSignInCount);
    }

    [Fact]
    public async Task Verify_UserWhoMustChangeTheirPassword_SignsIn()
    {
        await _context.SeedUserAsync("alice", mustChangePassword: true);

        var result = await _context.NewVerifier().VerifyAsync("alice", StrongPassword, None);

        Assert.True(result.Succeeded);
        Assert.True(result.User!.MustChangePassword);
    }

    /// <summary>Over-long input is refused before the lookup: even an existing account with such a name is not found.</summary>
    [Fact]
    public async Task Verify_OverLongInput_IsRefusedBeforeAnyLookupOrHashing()
    {
        var longName = new string('a', 65);
        await _context.SeedUserAsync(longName);
        var hasher = new CountingHasher(_context.Hasher);
        var verifier = _context.NewVerifier(hasher);
        var callsAfterStartUp = hasher.Calls;

        var longUser = await verifier.VerifyAsync(longName, StrongPassword, None);
        var longPassword = await verifier.VerifyAsync("alice", new string('p', 129), None);
        var blank = await verifier.VerifyAsync(" ", StrongPassword, None);

        Assert.All([longUser, longPassword, blank], r => Assert.Equal(CredentialFailure.Malformed, r.Failure));
        Assert.Equal(callsAfterStartUp, hasher.Calls);
    }

    /// <summary>Unknown, disabled and locked accounts do the same hashing work as a real check, against the start-up dummy hash.</summary>
    [Fact]
    public async Task Verify_RefusedAccounts_VerifyTheDummyHashOnce()
    {
        await _context.SeedUserAsync("disabled", isEnabled: false);
        var locked = await _context.SeedUserAsync("locked");
        await _context.NewStore().RecordFailedSignInAsync(locked.Id, 1, T0, TimeSpan.FromMinutes(15), None);
        var hasher = new CountingHasher(_context.Hasher);
        var verifier = _context.NewVerifier(hasher);

        foreach (var name in new[] { "nobody", "disabled", "locked" })
        {
            hasher.Verified.Clear();
            var result = await verifier.VerifyAsync(name, StrongPassword, None);

            Assert.False(result.Succeeded);
            var hash = Assert.Single(hasher.Verified);
            Assert.Equal(hasher.FirstHash, hash);
        }
    }

    [Fact]
    public async Task Verify_RehashesWhenTheStoredHashIsWeakerThanTheHasher()
    {
        var alice = await _context.SeedUserAsync("alice");
        var stronger = NewHasher(2000);

        var result = await _context.NewVerifier(stronger).VerifyAsync("alice", StrongPassword, None);

        Assert.True(result.Succeeded);
        var stored = await _context.ReadUserAsync(alice.Id);
        Assert.NotEqual(alice.PasswordHash, stored.PasswordHash, StringComparer.Ordinal);
        Assert.Equal(stored.PasswordHash, result.User!.PasswordHash);
        Assert.Equal(PasswordVerificationResult.Success, stronger.VerifyHashedPassword(stored, stored.PasswordHash, StrongPassword));
    }

    [Fact]
    public async Task Verify_DoesNotRehashACurrentHash()
    {
        var alice = await _context.SeedUserAsync("alice");

        await _context.NewVerifier().VerifyAsync("alice", StrongPassword, None);

        Assert.Equal(alice.PasswordHash, (await _context.ReadUserAsync(alice.Id)).PasswordHash);
    }

    /// <summary>
    /// A sign-in that needs a rehash, raced by an administrator's reset committed after its check: storing
    /// the rehash of the old password would undo the reset, so the sign-in fails and writes nothing.
    /// </summary>
    [Fact]
    public async Task Verify_AResetCommittedAfterTheCheck_IsNotUndoneByTheRehash()
    {
        await _context.SeedAdministratorAsync();
        var alice = await _context.SeedUserAsync("alice");
        var hasher = new InterleavingHasher(NewHasher(2000), async () =>
            await _context.NewAdministration().ResetPasswordAsync(alice.Id, "the reset password", mustChangePassword: false, Admin, None));

        var result = await _context.NewVerifier(hasher).VerifyAsync("alice", StrongPassword, None);

        Assert.False(result.Succeeded);
        Assert.Equal(CredentialFailure.PasswordChanged, result.Failure);
        var stored = await _context.ReadUserAsync(alice.Id);
        Assert.Equal(PasswordVerificationResult.Success, _context.Hasher.VerifyHashedPassword(stored, stored.PasswordHash, "the reset password"));
        Assert.Equal(PasswordVerificationResult.Failed, _context.Hasher.VerifyHashedPassword(stored, stored.PasswordHash, StrongPassword));
        Assert.Null(stored.LastSignInAtUtc);
        Assert.Equal(0, stored.FailedSignInCount);
    }

    [Fact]
    public async Task Verify_AnAccountDisabledAfterTheCheck_IsRefused()
    {
        await _context.SeedAdministratorAsync();
        var alice = await _context.SeedUserAsync("alice");
        var hasher = new InterleavingHasher(_context.Hasher, async () =>
            await _context.NewAdministration().UpdateUserAsync(alice.Id, new UserChanges(null, false, null), Admin, None));

        var result = await _context.NewVerifier(hasher).VerifyAsync("alice", StrongPassword, None);

        Assert.Equal(CredentialFailure.Disabled, result.Failure);
        Assert.Null((await _context.ReadUserAsync(alice.Id)).LastSignInAtUtc);
    }

    /// <summary>A lockout committed after the check stands: the right password does not clear it.</summary>
    [Fact]
    public async Task Verify_AnAccountLockedAfterTheCheck_IsRefusedAndStaysLocked()
    {
        var alice = await _context.SeedUserAsync("alice");
        var hasher = new InterleavingHasher(_context.Hasher, async () =>
            await _context.NewStore().RecordFailedSignInAsync(alice.Id, 1, T0, TimeSpan.FromMinutes(15), None));

        var result = await _context.NewVerifier(hasher).VerifyAsync("alice", StrongPassword, None);

        Assert.Equal(CredentialFailure.LockedOut, result.Failure);
        var stored = await _context.ReadUserAsync(alice.Id);
        Assert.Equal(T0.AddMinutes(15), stored.LockoutEndUtc);
        Assert.Null(stored.LastSignInAtUtc);
    }

    /// <summary>Counts the hashing work and records which hashes were verified.</summary>
    private sealed class CountingHasher(IPasswordHasher<DashboardUser> inner) : IPasswordHasher<DashboardUser>
    {
        public int Calls { get; private set; }

        public string? FirstHash { get; private set; }

        public List<string> Verified { get; } = [];

        public string HashPassword(DashboardUser user, string password)
        {
            Calls++;
            var hash = inner.HashPassword(user, password);
            FirstHash ??= hash;
            return hash;
        }

        public PasswordVerificationResult VerifyHashedPassword(DashboardUser user, string hashedPassword, string providedPassword)
        {
            Calls++;
            Verified.Add(hashedPassword);
            return inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }
}

/// <summary>Every scalar of a user, the grant count included.</summary>
internal sealed class UserEquality : IEqualityComparer<DashboardUser>
{
    public static readonly UserEquality Instance = new();

    public bool Equals(DashboardUser? x, DashboardUser? y) =>
        x is not null && y is not null && string.Equals(Describe(x), Describe(y), StringComparison.Ordinal);

    public int GetHashCode(DashboardUser obj) => obj.Id.GetHashCode();

    private static string Describe(DashboardUser u) => u + " " + u.PasswordHash + " " + u.SecurityStamp;
}
