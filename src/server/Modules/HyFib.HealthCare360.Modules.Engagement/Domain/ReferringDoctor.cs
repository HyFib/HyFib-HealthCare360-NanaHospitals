using System.Text;

namespace HyFib.HealthCare360.Modules.Engagement.Domain;

/// <summary>
/// An external doctor who refers patients (CRM-05). Visits record the referrer as free text ("Dr. R. Kumar,
/// Anna Nagar"), so referral counts come from matching that text against the doctor's name and known aliases.
/// </summary>
public sealed class ReferringDoctor : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Clinic { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Specialty { get; set; }
    public string? City { get; set; }
    public string? RegistrationNumber { get; set; }

    /// <summary>Other spellings seen on registration desks ("Dr RK", "Ravi Kumar").</summary>
    public List<string> AlternateNames { get; set; } = [];

    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;

    public IEnumerable<string> AllNames => AlternateNames.Prepend(Name);
}

/// <summary>Matches free-text "referred by" values to referring doctors.</summary>
public static class ReferralNameMatcher
{
    private static readonly HashSet<string> Honorifics = new(StringComparer.Ordinal) { "dr", "doctor", "prof", "mr", "mrs", "ms" };

    /// <summary>Lower-case letters and digits only, honorifics removed, single spaces: "Dr. R. Kumar" → "r kumar".</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var cleaned = new StringBuilder(name.Length);
        foreach (var c in name.ToLowerInvariant())
        {
            cleaned.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return string.Join(' ', cleaned.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !Honorifics.Contains(token)));
    }

    /// <summary>
    /// True when the referrer text is one of the doctor's names, or contains one as consecutive words
    /// ("dr r kumar anna nagar" matches "R. Kumar"). Single-word names must match the whole text to avoid false hits.
    /// </summary>
    public static bool Matches(string? referredBy, IEnumerable<string> doctorNames)
    {
        var text = Normalize(referredBy);
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var name in doctorNames.Select(Normalize).Where(n => n.Length > 0))
        {
            if (text == name)
            {
                return true;
            }

            if (name.Contains(' ', StringComparison.Ordinal) && $" {text} ".Contains($" {name} ", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The most distinctive word of a name, used to pre-filter candidate visits in the database.</summary>
    public static string? KeyToken(string name) =>
        Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderByDescending(t => t.Length).FirstOrDefault();
}

/// <summary>A distinct "referred by" text from visits, with how often and when it was last used.</summary>
public sealed record ReferrerText(string Text, int Visits, DateTimeOffset LastVisitAt);

/// <summary>A referring doctor's names, as the attribution needs them.</summary>
public sealed record ReferrerNames(Guid DoctorId, IReadOnlyList<string> Names);

public sealed record ReferralTally(int Visits, DateTimeOffset? LastReferralAt);

/// <summary>
/// Attributes free-text referrals to referring doctors (CRM-05). Each text counts for one doctor only, so totals never
/// double-count: when several doctors match, the longest (most specific) matching name wins, then the lowest doctor
/// id for a stable tie-break. Texts that match nobody are returned so the CRM team can add the doctor or an alias.
/// </summary>
public static class ReferralAttribution
{
    public static (IReadOnlyDictionary<Guid, ReferralTally> ByDoctor, IReadOnlyList<ReferrerText> Unmatched) Attribute(
        IReadOnlyList<ReferrerNames> doctors,
        IEnumerable<ReferrerText> texts)
    {
        var byDoctor = new Dictionary<Guid, ReferralTally>();
        var unmatched = new List<ReferrerText>();
        foreach (var text in texts)
        {
            if (DoctorFor(text.Text, doctors) is not { } doctorId)
            {
                unmatched.Add(text);
                continue;
            }

            var current = byDoctor.GetValueOrDefault(doctorId, new ReferralTally(0, null));
            byDoctor[doctorId] = new ReferralTally(
                current.Visits + text.Visits,
                current.LastReferralAt is { } last && last > text.LastVisitAt ? last : text.LastVisitAt);
        }

        return (byDoctor, unmatched.OrderByDescending(u => u.Visits).ToList());
    }

    /// <summary>The one doctor a referrer text is attributed to, or null when it matches nobody.</summary>
    public static Guid? DoctorFor(string referredBy, IReadOnlyList<ReferrerNames> doctors) =>
        doctors
            .SelectMany(d => d.Names.Select(name => (d.DoctorId, Name: name, Length: ReferralNameMatcher.Normalize(name).Length)))
            .Where(c => c.Length > 0 && ReferralNameMatcher.Matches(referredBy, [c.Name]))
            .OrderByDescending(c => c.Length)
            .ThenBy(c => c.DoctorId)
            .Select(c => (Guid?)c.DoctorId)
            .FirstOrDefault();
}
