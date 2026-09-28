namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

/// <summary>Chooses the reference interval that applies to a patient (pure; unit-tested).</summary>
internal static class ReferenceRangeSelector
{
    /// <summary>
    /// A sex-specific interval beats a sex-neutral one, then the narrowest age band wins. For example, a 10-year-old
    /// girl gets the "6–12 years" interval, not the "all ages" fallback. Returns null when nothing applies, for
    /// example an unknown age with only age-banded intervals. The result is then left unflagged and cannot be
    /// auto-validated, rather than being judged against the wrong interval.
    /// </summary>
    public static ReferenceRange? Select(IEnumerable<ReferenceRange> ranges, Gender gender, int? ageDays) =>
        ranges
            .Where(r => r.Applies(gender, ageDays))
            .OrderByDescending(r => r.IsSexSpecific)
            .ThenBy(r => (long)(r.AgeToDays ?? int.MaxValue) - r.AgeFromDays)
            .ThenByDescending(r => r.AgeFromDays)
            .FirstOrDefault();

    /// <summary>Age in days on the day the specimen was taken (reference intervals depend on age at collection).</summary>
    public static int? AgeInDays(DateOnly? dateOfBirth, DateOnly onDate) =>
        dateOfBirth is { } dob ? Age.InDays(dob, onDate) : null;
}
