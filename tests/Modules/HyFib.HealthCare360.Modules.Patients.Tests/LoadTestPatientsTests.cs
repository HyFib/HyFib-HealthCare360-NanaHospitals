using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Seed;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

public sealed class LoadTestPatientsTests
{
    private static readonly DateOnly Today = new(2026, 10, 12);
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 4, 0, 0, TimeSpan.Zero);
    private static readonly Guid Facility = Guid.NewGuid();

    [Fact]
    public void The_same_number_makes_the_same_patient()
    {
        var a = LoadTestPatients.Create(12_345, Facility, Today, Now);
        var b = LoadTestPatients.Create(12_345, Facility, Today, Now);

        a.Uhid.ShouldBe("LT000012345");
        (b.Uhid, b.FullName, b.Phone, b.DateOfBirth, b.Gender).ShouldBe((a.Uhid, a.FullName, a.Phone, a.DateOfBirth, a.Gender));
        a.Phone.ShouldBe("+919000012345");
        a.Notes.ShouldBe(LoadTestPatients.Note);
        a.RegisteredFacilityId.ShouldBe(Facility);
    }

    [Fact]
    public void Synthetic_patients_look_like_registered_ones()
    {
        var patients = Enumerable.Range(1, 5_000).Select(n => LoadTestPatients.Create(n, Facility, Today, Now)).ToList();

        patients.Select(p => p.Uhid).Distinct().Count().ShouldBe(patients.Count);
        patients.Select(p => p.Phone).Distinct().Count().ShouldBe(patients.Count);
        patients.ShouldAllBe(p => PatientRules.ValidName(p.FirstName) && PatientRules.ValidName(p.MiddleName) && PatientRules.ValidName(p.LastName));
        patients.ShouldAllBe(p => p.FullName == Patient.ComposeFullName(p.FirstName, p.MiddleName, p.LastName));
        patients.ShouldAllBe(p => p.DateOfBirth <= Today && p.DateOfBirth > Today.AddYears(-91));
        patients.ShouldAllBe(p => p.RegisteredAt <= Now && p.RegisteredAt > Now.AddYears(-6));
        patients.Select(p => p.FullName).Distinct().Count().ShouldBeGreaterThan(3_000, "a search by name finds a page of people, not thousands");
        patients.Select(p => p.Gender).Distinct().Count().ShouldBe(2);
        patients.Count(p => p.Category == PatientCategory.SeniorCitizen).ShouldBeInRange(1_000, 2_000, "about a third are 60 or older");
    }

    [Fact]
    public void Each_given_name_has_the_gender_it_is_listed_with()
    {
        foreach (var number in Enumerable.Range(1, LoadTestPatients.FirstNames.Count))
        {
            var patient = LoadTestPatients.Create(number, Facility, Today, Now);
            LoadTestPatients.FirstNames.ShouldContain((patient.FirstName, patient.Gender));
        }
    }
}
