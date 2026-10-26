using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// Whether a patient found by the mobile number ABDM verified is the person the ABHA describes (user-initiated linking):
/// gender, year of birth and name must agree, and an ABHA's shortened name ("Meera K") is the same person.
/// </summary>
public sealed class AbdmDemographicsTests
{
    private static readonly Patient Meera = new()
    {
        FullName = "Meera Krishnan",
        Gender = Gender.Female,
        DateOfBirth = new DateOnly(1984, 3, 9),
    };

    [Theory]
    [InlineData("Meera Krishnan", "Meera Krishnan", true)]
    [InlineData("Meera Krishnan", "MEERA K", true)]
    [InlineData("Meera Krishnan", "meera", true)]
    [InlineData("Meera Krishnan", "Meera  K.", true)]
    [InlineData("Meera Krishnan", "Meena Krishnan", false)]
    [InlineData("Meera Krishnan", "Meera Sharma", false)]
    [InlineData("Meera Krishnan", "", false)]
    [InlineData("Meera Krishnan", null, false)]
    public void Names_agree_on_the_first_name_and_the_last_initial(string ours, string? theirs, bool same) =>
        Demographics.NamesMatch(ours, theirs).ShouldBe(same);

    [Theory]
    [InlineData("F", 1984, true)]
    [InlineData("f", 1986, true)]
    [InlineData("F", 1987, false)]
    [InlineData("M", 1984, false)]
    [InlineData("F", null, true)]
    public void Gender_and_year_of_birth_must_agree(string gender, int? year, bool same) =>
        Demographics.Match(Meera, Wanted("Meera K", gender, year)).ShouldBe(same);

    [Fact]
    public void An_estimated_date_of_birth_allows_five_years()
    {
        var estimated = new Patient { FullName = "Meera Krishnan", Gender = Gender.Female, DateOfBirth = new DateOnly(1984, 1, 1), IsDateOfBirthEstimated = true };

        Demographics.Match(estimated, Wanted("Meera K", "F", 1989)).ShouldBeTrue();
        Demographics.Match(estimated, Wanted("Meera K", "F", 1990)).ShouldBeFalse();
    }

    private static AbdmGatewayMessages.DiscoverPatient Wanted(string name, string gender, int? year) =>
        new("meera.k@sbx", name, gender, year, [new("MOBILE", "+919800083232")], []);
}
