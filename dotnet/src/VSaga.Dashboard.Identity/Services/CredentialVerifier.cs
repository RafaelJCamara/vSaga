using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using VSaga.Dashboard.Identity.Model;
using VSaga.Dashboard.Identity.Stores;

namespace VSaga.Dashboard.Identity.Services;

/// <summary>Why a sign-in failed. The caller answers every failure identically; this is for audit only.</summary>
public enum CredentialFailure
{
    /// <summary>The credentials were accepted.</summary>
    None,

    /// <summary>Missing, or longer than a username or password can be; refused before any lookup or hashing.</summary>
    Malformed,

    /// <summary>No user has that username.</summary>
    UnknownUser,

    /// <summary>The account is disabled.</summary>
    Disabled,

    /// <summary>The account was already locked; the password was not checked.</summary>
    LockedOut,

    /// <summary>The password was wrong and the failure was counted; <see cref="CredentialVerification.LockedUntilUtc"/> says whether it locked the account.</summary>
    WrongPassword,

    /// <summary>
    /// The password matched the hash that was read, but the stored hash changed before the sign-in was recorded
    /// (a reset or a change committed meanwhile). Nothing was counted or written.
    /// </summary>
    PasswordChanged,
}

/// <summary>The outcome of <see cref="CredentialVerifier.VerifyAsync"/>.</summary>
/// <param name="User">The signed-in user as it stands after the sign-in was recorded; null on any failure.</param>
/// <param name="Failure">Why it failed, or <see cref="CredentialFailure.None"/>.</param>
/// <param name="LockedUntilUtc">The lockout in force after a counted failure, or null.</param>
public sealed record CredentialVerification(DashboardUser? User, CredentialFailure Failure, DateTimeOffset? LockedUntilUtc)
{
    public bool Succeeded => User is not null;

    internal static CredentialVerification Failed(CredentialFailure failure, DateTimeOffset? lockedUntilUtc = null) =>
        new(null, failure, lockedUntilUtc);
}

/// <summary>
/// A password hash computed once, at start, from a random password nobody knows. Sign-in verifies against it
/// for an unknown, disabled or locked account, so those paths do the same hashing work as a real
/// verification with the same hasher settings, and the response time does not tell them apart.
/// </summary>
public sealed class DummyPasswordHash
{
    /// <summary>A user value for the hasher, which reads nothing from it.</summary>
    internal static readonly DashboardUser Placeholder = new(
        Guid.Empty, string.Empty, string.Empty, string.Empty, string.Empty, IsEnabled: false, MustChangePassword: false,
        FailedSignInCount: 0, LockoutEndUtc: null, LastSignInAtUtc: null, default, default, []);

    public DummyPasswordHash(IPasswordHasher<DashboardUser> hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        Value = hasher.HashPassword(Placeholder, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    }

    public string Value { get; }
}

/// <summary>
/// Checks a username and password (design §8.4). Unknown, disabled and locked accounts verify the
/// <see cref="DummyPasswordHash"/> and count nothing. A wrong password is counted atomically and locks the
/// account at <see cref="DashboardSecuritySettings.LockoutMaxFailedAttempts"/>. A right one clears the count
/// and stores a rehash when the hasher asks for one. A username over 64 or a password over 128 characters is
/// refused before any lookup or hashing. The minimum response time for failures is the endpoint's job.
/// <para>
/// The hashing runs with no lock held, so a success is recorded inside an exclusive scope after reading the
/// user again: when the hash it verified is no longer the stored one, or the account was disabled or locked
/// meanwhile, the sign-in fails and nothing is written. Otherwise a sign-in that raced an administrator's
/// reset would store a rehash of the old password over the new one and undo the reset.
/// </para>
/// </summary>
public sealed class CredentialVerifier(
    IDashboardIdentityStore store,
    IPasswordHasher<DashboardUser> hasher,
    DummyPasswordHash dummyHash,
    DashboardSecuritySettings settings,
    TimeProvider timeProvider)
{
    /// <summary>The longest username any account can have.</summary>
    public const int MaxUsernameLength = 64;

    public async Task<CredentialVerification> VerifyAsync(string? username, string? password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username)
            || string.IsNullOrEmpty(password)
            || username.Length > MaxUsernameLength
            || password.Length > PasswordPolicy.MaxLength)
        {
            return CredentialVerification.Failed(CredentialFailure.Malformed);
        }

        var user = await store.FindUserByNameAsync(username, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var refusal = Refusal(user, now);
        if (refusal != CredentialFailure.None)
        {
            hasher.VerifyHashedPassword(DummyPasswordHash.Placeholder, dummyHash.Value, password);
            return CredentialVerification.Failed(refusal);
        }

        return await VerifyKnownAsync(user!, password, now, cancellationToken);
    }

    /// <summary>Why the account cannot sign in whatever the password, or <see cref="CredentialFailure.None"/>.</summary>
    private static CredentialFailure Refusal(DashboardUser? user, DateTimeOffset now) => user switch
    {
        null => CredentialFailure.UnknownUser,
        { IsEnabled: false } => CredentialFailure.Disabled,
        _ when user.LockoutEndUtc > now => CredentialFailure.LockedOut,
        _ => CredentialFailure.None,
    };

    private async Task<CredentialVerification> VerifyKnownAsync(DashboardUser user, string password, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        try
        {
            if (result == PasswordVerificationResult.Failed)
            {
                var lockedUntil = await store.RecordFailedSignInAsync(
                    user.Id, settings.LockoutMaxFailedAttempts, now, settings.LockoutDuration, cancellationToken);
                return CredentialVerification.Failed(CredentialFailure.WrongPassword, lockedUntil);
            }

            var rehash = result == PasswordVerificationResult.SuccessRehashNeeded ? hasher.HashPassword(user, password) : null;
            return await RecordSuccessAsync(user.Id, user.PasswordHash, rehash, now, cancellationToken);
        }
        catch (IdentityNotFoundException)
        {
            // Deleted between the lookup and the write: the same answer as for a name nobody has.
            return CredentialVerification.Failed(CredentialFailure.UnknownUser);
        }
    }

    /// <summary>
    /// Records the sign-in under the write lock, only when the user read again there still has the hash that
    /// was verified and can still sign in; the hashing itself stays outside the lock.
    /// </summary>
    private async Task<CredentialVerification> RecordSuccessAsync(
        Guid userId, string verifiedHash, string? rehash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginExclusiveAsync(cancellationToken);
        var current = await store.FindUserAsync(userId, cancellationToken);
        var refusal = Refusal(current, now);
        if (refusal != CredentialFailure.None)
            return CredentialVerification.Failed(refusal);
        if (!string.Equals(current!.PasswordHash, verifiedHash, StringComparison.Ordinal))
            return CredentialVerification.Failed(CredentialFailure.PasswordChanged);

        await store.RecordSignInAsync(userId, now, rehash, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        var signedIn = current with
        {
            PasswordHash = rehash ?? current.PasswordHash,
            FailedSignInCount = 0,
            LockoutEndUtc = null,
            LastSignInAtUtc = now,
        };
        return new CredentialVerification(signedIn, CredentialFailure.None, null);
    }
}
