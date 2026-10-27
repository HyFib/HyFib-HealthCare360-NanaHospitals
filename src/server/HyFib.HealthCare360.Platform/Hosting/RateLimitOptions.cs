namespace HyFib.HealthCare360.Platform.Hosting;

/// <summary>
/// The request limits (SRS SEC-06), in <c>HealthCare360:RateLimits</c> of the settings file rather than in code (SRS MNT-04).
/// The defaults suit a hospital; raise one for many users behind one address, or for a load test, and restart the
/// service. A module's own limits sit in the same section under the module's name (<c>Ai</c>, <c>RadiologyImages</c>).
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = PlatformOptions.SectionName + ":RateLimits";

    /// <summary>Signing in, a second factor, a one-time code, a token refresh, unlocking, a password given again: per address.</summary>
    public FixedWindowLimit SignIn { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) };

    /// <summary>Asking for a one-time code: per address.</summary>
    public FixedWindowLimit OneTimeCode { get; set; } = new() { PermitLimit = 5, Window = TimeSpan.FromMinutes(15) };

    /// <summary>Links, QR codes, webhooks and workstation enrolment: per address.</summary>
    public FixedWindowLimit Public { get; set; } = new() { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) };

    /// <summary>
    /// Calls from the hospital's partners (SMS and WhatsApp delivery receipts, the payment gateway, the ABDM gateway): per
    /// address. A campaign brings one receipt a message from the provider's few addresses.
    /// </summary>
    public FixedWindowLimit Partner { get; set; } = new() { PermitLimit = 1200, Window = TimeSpan.FromMinutes(1) };

    /// <summary>Machines with a credential of their own (RPM gateways, biometric terminals): per credential.</summary>
    public TokenBucketLimit Device { get; set; } = new() { TokenLimit = 120, TokensPerPeriod = 60, ReplenishmentPeriod = TimeSpan.FromSeconds(30) };

    /// <summary>HC360 Desktop's self-update: per device credential.</summary>
    public FixedWindowLimit DesktopUpdate { get; set; } = new() { PermitLimit = 30, Window = TimeSpan.FromMinutes(15) };

    /// <summary>Every request: per signed-in user, or per address before signing in.</summary>
    public TokenBucketLimit PerUser { get; set; } = new() { TokenLimit = 400, TokensPerPeriod = 200, ReplenishmentPeriod = TimeSpan.FromSeconds(10) };

    /// <summary>What is wrong with the limits, in words for whoever edits the settings file.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        Check(SignIn, nameof(SignIn), problems);
        Check(OneTimeCode, nameof(OneTimeCode), problems);
        Check(Public, nameof(Public), problems);
        Check(Partner, nameof(Partner), problems);
        Check(Device, nameof(Device), problems);
        Check(DesktopUpdate, nameof(DesktopUpdate), problems);
        Check(PerUser, nameof(PerUser), problems);
        return problems;
    }

    internal static void Check(FixedWindowLimit limit, string name, List<string> problems)
    {
        if (limit.PermitLimit < 1 || limit.Window <= TimeSpan.Zero)
        {
            problems.Add($"{SectionName}:{name} allows {limit.PermitLimit} in {limit.Window}; give a PermitLimit of 1 or more and a Window longer than zero.");
        }
    }

    internal static void Check(TokenBucketLimit limit, string name, List<string> problems)
    {
        if (limit.TokenLimit < 1 || limit.TokensPerPeriod < 1 || limit.ReplenishmentPeriod <= TimeSpan.Zero)
        {
            problems.Add($"{SectionName}:{name} needs a TokenLimit and TokensPerPeriod of 1 or more and a ReplenishmentPeriod longer than zero.");
        }
    }
}

/// <summary>At most <see cref="PermitLimit"/> requests in each fixed <see cref="Window"/>.</summary>
public sealed class FixedWindowLimit
{
    public int PermitLimit { get; set; }

    public TimeSpan Window { get; set; }
}

/// <summary>Up to <see cref="TokenLimit"/> requests at once, refilled by <see cref="TokensPerPeriod"/> each <see cref="ReplenishmentPeriod"/>.</summary>
public sealed class TokenBucketLimit
{
    public int TokenLimit { get; set; }

    public int TokensPerPeriod { get; set; }

    public TimeSpan ReplenishmentPeriod { get; set; }
}

/// <summary>At most <see cref="PermitLimit"/> requests in any <see cref="Window"/>, counted in <see cref="SegmentsPerWindow"/> parts.</summary>
public sealed class SlidingWindowLimit
{
    public int PermitLimit { get; set; }

    public TimeSpan Window { get; set; }

    public int SegmentsPerWindow { get; set; } = 1;
}

/// <summary>At most <see cref="PermitLimit"/> requests in flight at once; the next <see cref="QueueLimit"/> wait their turn.</summary>
public sealed class ConcurrencyLimit
{
    public int PermitLimit { get; set; }

    public int QueueLimit { get; set; }
}
