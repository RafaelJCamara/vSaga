using System.Security.Cryptography;

namespace VSaga.Dashboard.Identity.Services;

/// <summary>
/// Security stamps: 128 random bits as 32 upper-case hex characters. A session carries the stamp it was
/// issued with and is rejected once the user's stamp differs, so rotating it ends every existing session.
/// It rotates on a password change, an administrator's reset, when the account is disabled or enabled, and
/// when a wrong current password on a password change locks the account.
/// </summary>
public static class SecurityStamps
{
    /// <summary>A fresh stamp from the cryptographic random number generator.</summary>
    public static string New() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}
