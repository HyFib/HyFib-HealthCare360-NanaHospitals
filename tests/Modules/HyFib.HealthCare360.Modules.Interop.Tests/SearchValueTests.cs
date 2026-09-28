using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Search;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

public sealed class DateRangeTests
{
    private static readonly TimeSpan Ist = TimeSpan.FromMinutes(330);

    [Theory]
    [InlineData("2026", "2026-01-01T00:00:00+05:30", "2027-01-01T00:00:00+05:30")]
    [InlineData("2026-02", "2026-02-01T00:00:00+05:30", "2026-03-01T00:00:00+05:30")]
    [InlineData("2026-09-25", "2026-09-25T00:00:00+05:30", "2026-09-26T00:00:00+05:30")]
    [InlineData("2026-09-25T10:15", "2026-09-25T10:15:00+05:30", "2026-09-25T10:16:00+05:30")]
    [InlineData("2026-09-25T10:15:30Z", "2026-09-25T10:15:30+00:00", "2026-09-25T10:15:31+00:00")]
    [InlineData("2026-09-25T10:15:30.250+05:30", "2026-09-25T10:15:30.25+05:30", "2026-09-25T10:15:30.251+05:30")]
    public void Covers_the_implicit_range_of_its_precision(string value, string start, string end)
    {
        DateRange.TryParse(value, out var range).ShouldBeTrue();

        range.Start.ShouldBe(DateTimeOffset.Parse(start, System.Globalization.CultureInfo.InvariantCulture));
        range.End.ShouldBe(DateTimeOffset.Parse(end, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-9-1")]
    [InlineData("2026-02-30")]
    [InlineData("2026-09-25T25:00")]
    [InlineData("25/09/2026")]
    public void Rejects_invalid_values(string value) => DateRange.TryParse(value, out _).ShouldBeFalse();

    [Fact]
    public void Dates_without_a_zone_are_ist()
    {
        DateRange.TryParse("2026-09-25", out var range).ShouldBeTrue();

        range.Start.Offset.ShouldBe(Ist);
        range.Start.UtcDateTime.ShouldBe(new DateTime(2026, 9, 24, 18, 30, 0, DateTimeKind.Utc));
    }
}

public sealed class DateParameterTests
{
    private static DateParameter Param(string value)
    {
        DateParameter.TryParse(value, out var parameter, out var error).ShouldBeTrue(error);
        return parameter!;
    }

    private static DateRange Target(string value)
    {
        DateRange.TryParse(value, out var range).ShouldBeTrue();
        return range;
    }

    [Theory]
    [InlineData("2026-09-25", "2026-09-25T10:00:00+05:30", true)]
    [InlineData("2026-09-25", "2026-09-26T00:00:00+05:30", false)]
    [InlineData("2026-09", "2026-09-25", true)]
    [InlineData("eq2026-09-25", "2026-09-24T23:59:59+05:30", false)]
    [InlineData("ne2026-09-25", "2026-09-24T23:59:59+05:30", true)]
    [InlineData("ge2026-09-25", "2026-09-25T00:00:00+05:30", true)]
    [InlineData("ge2026-09-25", "2026-09-24T23:59:59+05:30", false)]
    [InlineData("gt2026-09-25", "2026-09-25T23:00:00+05:30", false)]
    [InlineData("gt2026-09-25", "2026-09-26T00:00:00+05:30", true)]
    [InlineData("le2026-09-25", "2026-09-25T23:59:59+05:30", true)]
    [InlineData("le2026-09-25", "2026-09-26T00:00:00+05:30", false)]
    [InlineData("lt2026-09-25", "2026-09-24T23:59:59+05:30", true)]
    [InlineData("lt2026-09-25", "2026-09-25T00:00:00+05:30", false)]
    public void Matches_with_fhir_prefix_semantics(string search, string value, bool matches) =>
        Param(search).Matches(Target(value)).ShouldBe(matches);

    [Fact]
    public void An_ongoing_period_is_after_any_date() =>
        Param("ge2027-01-01").Matches(DateRange.From(Target("2026-09-01").Start)).ShouldBeTrue();

    [Fact]
    public void Gives_sql_bounds_for_stored_instants()
    {
        var day = Target("2026-09-25");

        Param("2026-09-25").InstantBounds().ShouldBe((day.Start, day.End, false));
        Param("ne2026-09-25").InstantBounds().ShouldBe((day.Start, day.End, true));
        Param("ge2026-09-25").InstantBounds().ShouldBe((day.Start, (DateTimeOffset?)null, false));
        Param("gt2026-09-25").InstantBounds().ShouldBe((day.End, (DateTimeOffset?)null, false));
        Param("le2026-09-25").InstantBounds().ShouldBe(((DateTimeOffset?)null, day.End, false));
        Param("lt2026-09-25").InstantBounds().ShouldBe(((DateTimeOffset?)null, day.Start, false));
    }

    [Theory]
    [InlineData("sa2026")]
    [InlineData("eb2026")]
    [InlineData("ap2026")]
    [InlineData("xx2026")]
    public void Rejects_unsupported_prefixes(string value) => DateParameter.TryParse(value, out _, out _).ShouldBeFalse();
}

public sealed class TokenValueTests
{
    [Theory]
    [InlineData("8867-4", null, "8867-4")]
    [InlineData("http://loinc.org|8867-4", "http://loinc.org", "8867-4")]
    [InlineData("|final", "", "final")]
    [InlineData("http://loinc.org|", "http://loinc.org", null)]
    [InlineData(@"urn:x|a\|b", "urn:x", "a|b")]
    public void Parses_the_token_forms(string raw, string? system, string? code) =>
        TokenValue.Parse(raw).ShouldBe(new TokenValue(system, code));

    [Theory]
    [InlineData("E11.9", "http://hl7.org/fhir/sid/icd-10", "E11.9", true)]
    [InlineData("e11.9", "http://hl7.org/fhir/sid/icd-10", "E11.9", true)]
    [InlineData("http://hl7.org/fhir/sid/icd-10|E11.9", "http://hl7.org/fhir/sid/icd-10", "E11.9", true)]
    [InlineData("http://snomed.info/sct|E11.9", "http://hl7.org/fhir/sid/icd-10", "E11.9", false)]
    [InlineData("http://hl7.org/fhir/sid/icd-10|", "http://hl7.org/fhir/sid/icd-10", "I10", true)]
    [InlineData("|final", null, "final", true)]
    [InlineData("|final", "http://hl7.org/fhir/observation-status", "final", false)]
    public void Matches_system_and_code(string raw, string? system, string code, bool matches) =>
        TokenValue.Parse(raw).Matches(system, code).ShouldBe(matches);

    [Fact]
    public void Splits_alternatives_on_unescaped_commas() =>
        TokenValue.SplitOr(@"a,b\,c,,d").ShouldBe(["a", @"b\,c", "d"]);

    [Fact]
    public void Ids_round_trip_with_their_prefix()
    {
        var id = Guid.CreateVersion7();

        FhirIds.TryParse(FhirIds.Of(FhirIds.Visit, id), FhirIds.Visit, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(id);
        FhirIds.TryParse(FhirIds.Of(FhirIds.Visit, id), FhirIds.ClinicalEncounter, out _).ShouldBeFalse();
        FhirIds.TryParse($"vs-{id:D}-bp", FhirIds.Vitals, out var vitals, out var suffix).ShouldBeTrue();
        vitals.ShouldBe(id);
        suffix.ShouldBe("bp");
        FhirIds.TryParse("vs-not-a-guid-bp", FhirIds.Vitals, out _, out _).ShouldBeFalse();
    }
}
