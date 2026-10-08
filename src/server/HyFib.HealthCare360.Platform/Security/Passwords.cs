using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Security;

/// <summary>
/// Password rules aligned with NIST SP 800-63B / OWASP ASVS (SRS SEC-01): length over complexity, no password seen in a
/// data breach (<see cref="BreachedPasswords"/>) or on the common-password list, and none of the user's own names, the
/// hospital's or HC360's. No composition rules or forced periodic rotation.
/// </summary>
public sealed class PasswordPolicy(IOptions<PlatformOptions> options)
{
    private const int MaxLength = 128;
    private const int MinContextWordLength = 4;
    private static readonly Lazy<HashSet<string>> CommonPasswords = new(LoadCommonPasswords);

    /// <summary>The product's own names, refused anywhere in a password (ASVS V6.2.11).</summary>
    private static readonly string[] ProductWords = ["hc360", "healthcare360", "hyfib"];

    public int MinLength => options.Value.Security.MinPasswordLength;

    /// <summary>
    /// Whether attackers try this password first: it was published from a data breach or is on the common-password list.
    /// Checked at sign-in too, so an existing password found there has to be changed.
    /// </summary>
    public static bool IsKnownToAttackers(string password)
    {
        var lowered = password.ToLowerInvariant();
        return CommonPasswords.Value.Contains(lowered)
            || CommonPasswords.Value.Contains(StripDigitsAndSymbols(lowered))
            || BreachedPasswords.Embedded.Contains(lowered);
    }

    /// <param name="contextWords">
    /// Names attackers would try with this hospital, such as its facilities' names: each is refused anywhere in the
    /// password, in any case and without its spaces and punctuation ("Nana Hospitals" refuses "NanaHospitals@2026").
    /// </param>
    public IReadOnlyList<string> Validate(
        string? password, string? userName = null, string? displayName = null, IEnumerable<string?>? contextWords = null)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(password))
        {
            errors.Add("Password is required.");
            return errors;
        }

        if (password.Length < MinLength)
        {
            errors.Add($"Use at least {MinLength} characters. A short sentence works well.");
        }

        if (password.Length > MaxLength)
        {
            errors.Add($"Use at most {MaxLength} characters.");
        }

        var lowered = password.ToLowerInvariant();
        if (CommonPasswords.Value.Contains(lowered) || CommonPasswords.Value.Contains(StripDigitsAndSymbols(lowered)))
        {
            errors.Add("This password is too common. Choose something less predictable.");
        }
        else if (BreachedPasswords.Embedded.Contains(lowered))
        {
            errors.Add("This password has been published from a data breach, so attackers try it. Choose another.");
        }

        if (!string.IsNullOrWhiteSpace(userName) && userName.Length >= 4 && lowered.Contains(userName.ToLowerInvariant(), StringComparison.Ordinal))
        {
            errors.Add("Password must not contain your username.");
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            foreach (var part in displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p.Length >= 4))
            {
                if (lowered.Contains(part.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    errors.Add("Password must not contain your name.");
                    break;
                }
            }
        }

        var squashed = Squash(password);
        if (ContextWords(contextWords).Any(word => squashed.Contains(word, StringComparison.Ordinal)))
        {
            errors.Add("Password must not contain the hospital's name or HC360.");
        }

        if (password.Distinct().Count() < 5)
        {
            errors.Add("Use more varied characters.");
        }

        return errors;
    }

    private static string StripDigitsAndSymbols(string value) => new(value.Where(char.IsLetter).ToArray());

    private static IEnumerable<string> ContextWords(IEnumerable<string?>? names) =>
        ProductWords.Concat((names ?? []).Select(name => Squash(name ?? string.Empty)).Where(word => word.Length >= MinContextWordLength));

    /// <summary>Lower case, letters and digits only.</summary>
    private static string Squash(string value) => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static HashSet<string> LoadCommonPasswords()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var stream = typeof(PasswordPolicy).Assembly.GetManifestResourceStream(
            "HyFib.HealthCare360.Platform.Security.Resources.common-passwords.txt");
        if (stream is null)
        {
            return set;
        }

        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
            {
                set.Add(trimmed.ToLowerInvariant());
            }
        }

        return set;
    }
}

public readonly record struct PasswordCheck(bool Succeeded, bool NeedsRehash);

/// <summary>PBKDF2-HMAC-SHA512 hashing (ASP.NET Core Identity v3 format) with a configurable work factor.</summary>
public sealed class PasswordHashing
{
    private static readonly object Subject = new();
    private readonly PasswordHasher<object> _hasher;

    public PasswordHashing(IOptions<PlatformOptions> options)
    {
        _hasher = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = options.Value.Security.PasswordHashIterations,
        }));
    }

    public string Hash(string secret) => _hasher.HashPassword(Subject, secret);

    public PasswordCheck Verify(string hash, string secret) =>
        _hasher.VerifyHashedPassword(Subject, hash, secret) switch
        {
            PasswordVerificationResult.Success => new PasswordCheck(true, false),
            PasswordVerificationResult.SuccessRehashNeeded => new PasswordCheck(true, true),
            _ => new PasswordCheck(false, false),
        };
}
