using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Search;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// A patient's birth date is served as the day, or as the year when it was estimated from a stated age, and a
/// <c>birthdate</c> search matches what is served: the database filter agrees with FHIR's rules for a period
/// (<see cref="DateParameter.Matches"/>) for every prefix and precision, a search value with a time included.
/// </summary>
public sealed class BirthDateSearchTests
{
    private static readonly string[] Prefixes = ["", "ne", "gt", "lt", "ge", "le"];

    private static readonly string[] Values =
    [
        "1980", "1981", "1979", "1980-06", "1980-11", "1980-01", "1980-12", "1980-06-15", "1980-06-14", "1980-06-16",
        "1980-01-01", "1980-12-31", "1979-12-31", "1981-01-01", "1980-11-06", "1980-06-15T10:00+05:30",
        "1980-06-15T00:00+05:30", "1980-06-14T23:30Z",
    ];

    private static readonly (DateOnly Born, bool Estimated)[] People =
    [
        (new DateOnly(1980, 6, 15), false),
        (new DateOnly(1980, 1, 1), false),
        (new DateOnly(1980, 12, 31), false),
        (new DateOnly(1979, 12, 31), false),
        (new DateOnly(1981, 1, 1), false),
        (new DateOnly(1980, 11, 6), true),
        (new DateOnly(1980, 1, 1), true),
        (new DateOnly(1979, 3, 3), true),
    ];

    [Fact]
    public void The_database_filter_matches_the_served_birth_date_as_FHIR_matches_a_period()
    {
        var disagreements = new List<string>();
        foreach (var prefix in Prefixes)
        {
            foreach (var value in Values)
            {
                DateParameter.TryParse(prefix + value, out var search, out var error).ShouldBeTrue(error);
                var filter = PatientSearchQuery.BirthDate(search!).Compile();
                foreach (var (born, estimated) in People)
                {
                    var patient = new Patient { DateOfBirth = born, IsDateOfBirthEstimated = estimated };
                    var expected = search!.Matches(Served(born, estimated));
                    if (filter(patient) != expected)
                    {
                        disagreements.Add($"{prefix}{value} against {born:yyyy-MM-dd}{(estimated ? " (estimated)" : "")}: expected {expected}");
                    }
                }
            }
        }

        disagreements.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("1980", true)]
    [InlineData("1980-11", false)]
    [InlineData("1980-11-06", false)]
    [InlineData("lt1980-02", true)]
    [InlineData("gt1980-11-30", true)]
    [InlineData("ne1980-11-06", true)]
    [InlineData("ge1981", false)]
    public void An_estimated_birth_date_is_searched_as_its_year_not_the_day_stored_behind_it(string value, bool matches)
    {
        DateParameter.TryParse(value, out var search, out _).ShouldBeTrue();
        var stored = new Patient { DateOfBirth = new DateOnly(1980, 11, 6), IsDateOfBirthEstimated = true };

        PatientSearchQuery.BirthDate(search!).Compile()(stored).ShouldBe(matches);
    }

    [Theory]
    [InlineData("1980")]
    [InlineData("ne1980")]
    [InlineData("lt2100")]
    public void A_patient_without_a_birth_date_matches_no_birth_date_search(string value)
    {
        DateParameter.TryParse(value, out var search, out _).ShouldBeTrue();

        PatientSearchQuery.BirthDate(search!).Compile()(new Patient()).ShouldBeFalse();
    }

    /// <summary>The birth date as served: the day, or the year of one estimated from an age, as IST midnights.</summary>
    private static DateRange Served(DateOnly born, bool estimated)
    {
        var start = estimated ? new DateOnly(born.Year, 1, 1) : born;
        var end = estimated ? start.AddYears(1) : start.AddDays(1);
        return new DateRange(Midnight(start), Midnight(end));
    }

    private static DateTimeOffset Midnight(DateOnly date) => new(date.Year, date.Month, date.Day, 0, 0, 0, FhirTime.Ist);
}
