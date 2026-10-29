namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

/// <summary>Chooses the reference interval that applies to a patient (pure; unit-tested).</summary>
internal static class ReferenceRangeSelector
{
    /// <summary>What a result's printed reference text holds (<c>LabResult.ReferenceText</c>).</summary>
    private const int MaxReferenceText = 500;

    /// <summary>
    /// A sex-specific interval beats a sex-neutral one, then the narrowest age band wins. For example, a 10-year-old
    /// girl gets the "6–12 years" interval, not the "all ages" fallback. A patient recorded as neither male nor female
    /// (transgender, or the sex not known) gets a sex-neutral interval when there is one, and when only a male and a
    /// female interval apply at that age, the result is read against both (see <see cref="ReadsBothSexes"/>). Returns
    /// null when nothing applies, for example an unknown age with only age-banded intervals. The result is then left
    /// unflagged and cannot be auto-validated, rather than being judged against the wrong interval.
    /// </summary>
    /// <param name="decimals">The test's decimals, for the printed text when both intervals are read.</param>
    public static ReferenceRange? Select(IEnumerable<ReferenceRange> ranges, Gender gender, int? ageDays, int decimals = 1)
    {
        var all = ranges.ToList();
        return Best(all, gender, ageDays) ?? (IsNeitherSex(gender) ? BothSexes(all, ageDays, decimals) : null);
    }

    /// <summary>
    /// Whether the result is read against both the male and the female interval: the patient is recorded as neither
    /// male nor female, and no sex-neutral interval applies at that age. A person then verifies the result.
    /// </summary>
    public static bool ReadsBothSexes(IEnumerable<ReferenceRange> ranges, Gender gender, int? ageDays)
    {
        var all = ranges.ToList();
        return IsNeitherSex(gender) && Best(all, gender, ageDays) is null && BothSexes(all, ageDays, 0) is not null;
    }

    /// <summary>Age in days on the day the specimen was taken (reference intervals depend on age at collection).</summary>
    public static int? AgeInDays(DateOnly? dateOfBirth, DateOnly onDate) =>
        dateOfBirth is { } dob ? Age.InDays(dob, onDate) : null;

    private static bool IsNeitherSex(Gender gender) => gender is not (Gender.Male or Gender.Female);

    private static ReferenceRange? Best(IEnumerable<ReferenceRange> ranges, Gender gender, int? ageDays) =>
        ranges
            .Where(r => r.Applies(gender, ageDays))
            .OrderByDescending(r => r.IsSexSpecific)
            .ThenBy(r => (long)(r.AgeToDays ?? int.MaxValue) - r.AgeFromDays)
            .ThenByDescending(r => r.AgeFromDays)
            .FirstOrDefault();

    /// <summary>
    /// The male and the female interval read together: low, high or abnormal only outside both, and critical past either's
    /// critical limit or for an answer critical for either (a critical value is never missed, even when an interval has
    /// nothing to print). A low or high limit, or a list of normal answers, that only one of them has is left out: the
    /// other sex's result would not be flagged by it. Each interval that has a text is printed.
    /// </summary>
    private static ReferenceRange? BothSexes(IReadOnlyList<ReferenceRange> ranges, int? ageDays, int decimals)
    {
        if (Best(ranges.Where(r => r.Gender == Gender.Male), Gender.Male, ageDays) is not { } male
            || Best(ranges.Where(r => r.Gender == Gender.Female), Gender.Female, ageDays) is not { } female)
        {
            return null;
        }

        return new ReferenceRange
        {
            LabTestId = male.LabTestId,
            Low = male.Low is { } maleLow && female.Low is { } femaleLow ? Math.Min(maleLow, femaleLow) : null,
            High = male.High is { } maleHigh && female.High is { } femaleHigh ? Math.Max(maleHigh, femaleHigh) : null,
            CriticalLow = (male.CriticalLow, female.CriticalLow) switch
            {
                ({ } m, { } f) => Math.Max(m, f),
                var (m, f) => m ?? f,
            },
            CriticalHigh = (male.CriticalHigh, female.CriticalHigh) switch
            {
                ({ } m, { } f) => Math.Min(m, f),
                var (m, f) => m ?? f,
            },
            Text = Printed(ResultInterpreter.ReferenceText(male, decimals), ResultInterpreter.ReferenceText(female, decimals)),
            NormalValues = male.NormalValues.Count > 0 && female.NormalValues.Count > 0
                ? [.. male.NormalValues.Union(female.NormalValues, StringComparer.OrdinalIgnoreCase)]
                : [],
            CriticalValues = [.. male.CriticalValues.Union(female.CriticalValues, StringComparer.OrdinalIgnoreCase)],
        };
    }

    /// <summary>
    /// "Male 13.0 - 17.0; Female 12.0 - 15.0", or the one of them that has a text, within what a result holds. A long text
    /// is shortened rather than the other interval cut off: the shorter text is kept whole when it fits in half the room.
    /// </summary>
    private static string? Printed(string? male, string? female)
    {
        const string Male = "Male ", Female = "Female ", Separator = "; ";
        if (male is null || female is null)
        {
            return male is not null ? Clip(Male + male, MaxReferenceText)
                : female is not null ? Clip(Female + female, MaxReferenceText)
                : null;
        }

        var room = MaxReferenceText - Male.Length - Separator.Length - Female.Length;
        if (male.Length + female.Length > room)
        {
            var half = room / 2;
            var maleRoom = male.Length <= half ? male.Length : female.Length <= half ? room - female.Length : half;
            (male, female) = (Clip(male, maleRoom), Clip(female, room - maleRoom));
        }

        return Male + male + Separator + Female + female;
    }

    private static string Clip(string text, int length) =>
        text.Length <= length ? text : string.Concat(text.AsSpan(0, length - 1), "…");
}
