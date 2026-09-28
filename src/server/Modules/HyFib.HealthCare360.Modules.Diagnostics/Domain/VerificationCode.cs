using System.Security.Cryptography;

namespace HyFib.HealthCare360.Modules.Diagnostics.Domain;

/// <summary>
/// Unguessable code printed as a QR on released reports (LIS-07). Scanning it opens the public verification page,
/// which confirms that the report is genuine without revealing any result values. Twenty symbols from a 32-symbol
/// alphabet give 100 bits of entropy, so codes cannot be enumerated even without the rate limit.
/// </summary>
public static class VerificationCode
{
    public const int Length = 20;

    /// <summary>No 0/O or 1/I, so a code typed from a printed report is not misread.</summary>
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string New() => RandomNumberGenerator.GetString(Alphabet, Length);

    public static bool IsWellFormed(string? code) =>
        code is { Length: Length } && code.All(c => Alphabet.Contains(c, StringComparison.Ordinal));
}
