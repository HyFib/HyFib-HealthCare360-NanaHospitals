using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Security;

/// <summary>
/// Password rules aligned with NIST SP 800-63B / OWASP ASVS: length over complexity, a deny list of
/// common passwords, and no user-name reuse. No composition rules or forced periodic rotation.
/// </summary>
public sealed class PasswordPolicy(IOptions<PlatformOptions> options)
{
    private const int MaxLength = 128;
    private static readonly Lazy<HashSet<string>> CommonPasswords = new(LoadCommonPasswords);

    public int MinLength => options.Value.Security.MinPasswordLength;

    public IReadOnlyList<string> Validate(string? password, string? userName = null, string? displayName = null)
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

        if (password.Distinct().Count() < 5)
        {
            errors.Add("Use more varied characters.");
        }

        return errors;
    }

    private static string StripDigitsAndSymbols(string value) => new(value.Where(char.IsLetter).ToArray());

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
