using System.Security.Cryptography;
using System.Text;

namespace VSaga.Dashboard.Identity.Services;

/// <summary>
/// One-time setup codes: 16 characters from an alphabet with no look-alikes (no 0, O, 1 or I), 80 random
/// bits, written in four groups of four so a person can copy one from a log line. Codes are compared in
/// their <see cref="Canonical"/> form, so case, spaces and hyphens do not matter.
/// </summary>
public static class SetupCodes
{
    /// <summary>The fewest characters a preset code may have, not counting spaces and hyphens: as many as a generated one.</summary>
    public const int MinPresetLength = GroupCount * GroupLength;

    /// <summary>The most characters a preset code may have, not counting spaces and hyphens.</summary>
    public const int MaxPresetLength = 128;

    private const int GroupCount = 4;
    private const int GroupLength = 4;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>A fresh code from the cryptographic random number generator, such as <c>K7QD-M2XH-9TPA-W4RC</c>.</summary>
    public static string Generate()
    {
        var characters = RandomNumberGenerator.GetItems<char>(Alphabet, GroupCount * GroupLength);
        return string.Join('-', characters.Chunk(GroupLength).Select(group => new string(group)));
    }

    /// <summary>Upper-cased with spaces and hyphens left out: the form codes are compared in.</summary>
    public static string Canonical(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var builder = new StringBuilder(code.Length);
        foreach (var c in code.Where(c => c != '-' && !char.IsWhiteSpace(c)))
            builder.Append(char.ToUpperInvariant(c));
        return builder.ToString();
    }

    /// <summary>
    /// True when <paramref name="submitted"/> is <paramref name="expected"/> in canonical form. The comparison
    /// takes the same time wherever the two differ: both are hashed first, so even their lengths do not show.
    /// </summary>
    public static bool Matches(string expected, string? submitted)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(expected)));
        var submittedHash = SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(submitted ?? string.Empty)));
        return CryptographicOperations.FixedTimeEquals(expectedHash, submittedHash);
    }
}
