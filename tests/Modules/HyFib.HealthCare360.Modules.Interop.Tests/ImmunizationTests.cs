using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Search;
using HyFib.HealthCare360.Testing.Abdm;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>Gap #25: vaccine doses as FHIR <c>Immunization</c>, coded the way other providers' systems read them.</summary>
public sealed class ImmunizationMapperTests
{
    private const string NrcesImmunization = "https://nrces.in/ndhm/fhir/r4/StructureDefinition/Immunization";

    private static readonly Guid DoseId = Guid.Parse("01923a4b-0000-7000-8000-00000000e001");
    private static readonly Guid EncounterId = Guid.Parse("01923a4b-0000-7000-8000-00000000e002");

    /// <summary>BCG given here at birth, by a nurse on the ward, from a batch that expires next year.</summary>
    private static ImmunizationRow Bcg() => new()
    {
        Id = DoseId,
        PatientId = TestData.PatientId,
        EncounterId = EncounterId,
        VaccineName = "BCG",
        VaccineCode = "BCG",
        DoseNumber = 1,
        AdministeredOn = new DateOnly(2026, 9, 24),
        Route = "ID",
        Site = "Left upper arm",
        BatchNumber = "B2611-07",
        Manufacturer = "Serum Institute of India",
        ExpiryDate = new DateOnly(2027, 3, 31),
        AdministeredByUserName = "Sr. Kavitha R",
        IsExternal = false,
        NextDueOn = new DateOnly(2026, 11, 5),
        CreatedAt = TestData.Now,
    };

    /// <summary>A hepatitis B dose copied from the patient's card, with a code HC360 does not know.</summary>
    private static ImmunizationRow FromCard() => Bcg() with
    {
        VaccineName = "Hepatitis B (adult)",
        VaccineCode = "HB-ADULT",
        DoseNumber = 2,
        Route = "Deltoid injection",
        Site = null,
        BatchNumber = null,
        Manufacturer = null,
        ExpiryDate = null,
        AdministeredByUserName = null,
        AdministeredBy = "PHC Thiruvallur",
        IsExternal = true,
        EncounterId = null,
    };

    [Fact]
    public void A_dose_given_here_is_coded_from_its_card_code_and_says_who_gave_it()
    {
        var dose = ImmunizationMapper.Map(Bcg());

        dose.Id.ShouldBe(DoseId.ToString("D"));
        dose.Status.ShouldBe(R4.Immunization.ImmunizationStatusCodes.Completed);
        var vaccine = dose.VaccineCode.Coding.ShouldHaveSingleItem();
        (vaccine.System, vaccine.Code, vaccine.Display).ShouldBe(("http://snomed.info/sct", "1861000221106", "BCG (Bacillus Calmette-Guerin) vaccine"));
        dose.VaccineCode.Text.ShouldBe("BCG");
        dose.Patient.Reference.ShouldBe($"Patient/{TestData.PatientId:D}");
        dose.Encounter.Reference.ShouldBe($"Encounter/enc-{EncounterId:D}");
        dose.Occurrence.ShouldBeOfType<R4.FhirDateTime>().Value.ShouldBe("2026-09-24");
        dose.Recorded.ShouldBe("2026-09-25T10:30:00+05:30");
        dose.PrimarySource.ShouldBe(true);
        dose.ReportOrigin.ShouldBeNull();
        dose.LotNumber.ShouldBe("B2611-07");
        dose.ExpirationDate.ShouldBe("2027-03-31");
        dose.Manufacturer.Display.ShouldBe("Serum Institute of India");
        dose.Site.Text.ShouldBe("Left upper arm");
        var route = dose.Route.Coding.ShouldHaveSingleItem();
        (route.System, route.Code, route.Display).ShouldBe(("http://snomed.info/sct", "372464004", "Intradermal route"));
        var performer = dose.Performer.ShouldHaveSingleItem();
        performer.Actor.Display.ShouldBe("Sr. Kavitha R");
        performer.Function.Coding.ShouldHaveSingleItem().Code.ShouldBe("AP");
        dose.ProtocolApplied.ShouldHaveSingleItem().DoseNumber.ShouldBeOfType<R4.PositiveInt>().Value.ShouldBe(1);
        dose.Note.ShouldBeEmpty();
    }

    [Fact]
    public void A_dose_copied_from_a_card_is_not_the_hospitals_own_record_and_an_unknown_code_is_sent_by_name()
    {
        var dose = ImmunizationMapper.Map(FromCard());

        dose.PrimarySource.ShouldBe(false);
        var origin = dose.ReportOrigin.Coding.ShouldHaveSingleItem();
        (origin.System, origin.Code).ShouldBe(("http://terminology.hl7.org/CodeSystem/immunization-origin", "record"));
        dose.VaccineCode.Coding.ShouldBeEmpty("HC360 never guesses a code");
        dose.VaccineCode.Text.ShouldBe("Hepatitis B (adult)");
        ImmunizationMapper.Map(Bcg() with { VaccineCode = null }).VaccineCode.Coding.ShouldBeEmpty("a vaccine's name is not its code");
        dose.Route.Coding.ShouldBeEmpty();
        dose.Route.Text.ShouldBe("Deltoid injection");
        dose.Performer.ShouldHaveSingleItem().Actor.Display.ShouldBe("PHC Thiruvallur");
        dose.Encounter.ShouldBeNull();
        dose.ProtocolApplied.ShouldHaveSingleItem().DoseNumber.ShouldBeOfType<R4.PositiveInt>().Value.ShouldBe(2);
    }

    [Fact]
    public void A_dose_recorded_by_mistake_is_sent_as_entered_in_error_with_the_reason()
    {
        var dose = ImmunizationMapper.Map(Bcg() with { IsEnteredInError = true, EnteredInErrorReason = "Recorded for the twin", Notes = "Cried well" });

        dose.Status.ShouldBe(R4.Immunization.ImmunizationStatusCodes.EnteredInError);
        dose.Note.Select(n => n.Text).ShouldBe(["Cried well", "Entered in error: Recorded for the twin"]);
    }

    [Fact]
    public void A_dose_without_a_dose_number_or_a_vaccinator_leaves_them_out()
    {
        var dose = ImmunizationMapper.Map(Bcg() with { DoseNumber = null, AdministeredByUserName = null, AdministeredBy = null });

        dose.ProtocolApplied.ShouldBeEmpty();
        dose.Performer.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("ID", "372464004")]
    [InlineData("intradermal", "372464004")]
    [InlineData("IM", "78421000")]
    [InlineData("I.M.", "78421000")]
    [InlineData("Intramuscular", "78421000")]
    [InlineData("SC", "34206005")]
    [InlineData("s.c.", "34206005")]
    [InlineData("Sub-cutaneous", "34206005")]
    [InlineData("SQ", "34206005")]
    [InlineData("Oral", "26643006")]
    [InlineData("oral drops", "26643006")]
    [InlineData("PO", "26643006")]
    public void Codes_the_routes_staff_write(string recorded, string code)
    {
        var route = ImmunizationMapper.Route(recorded).ShouldNotBeNull();

        var coding = route.Coding.ShouldHaveSingleItem();
        (coding.System, coding.Code).ShouldBe(("http://snomed.info/sct", code));
        route.Text.ShouldBe(recorded);
    }

    [Theory]
    [InlineData("Intranasal")]
    [InlineData("left thigh")]
    [InlineData("IV")]
    public void Sends_any_other_route_as_written(string recorded) => ImmunizationMapper.Route(recorded)!.Coding.ShouldBeEmpty();

    [Fact]
    public void Meets_the_abdm_guides_immunisation_profile()
    {
        foreach (var dose in new[] { Bcg(), FromCard(), Bcg() with { IsEnteredInError = true, EnteredInErrorReason = "Wrong patient" } })
        {
            var validation = AbdmProfileValidator.Validate(ImmunizationMapper.Map(dose), NrcesImmunization);

            validation.Errors.ShouldBeEmpty(validation.Describe());
        }
    }

    [Fact]
    public void Searches_leave_out_doses_in_error_unless_asked_for_and_find_a_vaccine_by_its_code()
    {
        R4.Resource[] doses =
        [
            ImmunizationMapper.Map(Bcg()),
            ImmunizationMapper.Map(FromCard() with { Id = Guid.Parse("01923a4b-0000-7000-8000-00000000e003") }),
            ImmunizationMapper.Map(Bcg() with { Id = Guid.Parse("01923a4b-0000-7000-8000-00000000e004"), VaccineCode = "OPV", IsEnteredInError = true }),
        ];
        int Total(string query) => ResourceSearch.Execute(doses, TestData.Search("Immunization", $"patient={TestData.PatientId:D}&{query}")).Total;

        Total("_count=10").ShouldBe(2);
        Total("status=entered-in-error").ShouldBe(1);
        Total("status=completed,entered-in-error").ShouldBe(3);
        Total("vaccine-code=http://snomed.info/sct|1861000221106").ShouldBe(1);
        Total("date=2026-09-24").ShouldBe(2);
        Total("date=gt2026-09-24").ShouldBe(0);
    }
}

/// <summary>
/// The card codes HC360 sends as SNOMED CT: each must be one of the ABDM guide's own vaccine codes, with the guide's
/// display (ValueSet ndhm-vaccine-codes in the vendored ndhm.in 6.5.0 package).
/// </summary>
public sealed class VaccineCodeTests
{
    private static readonly IReadOnlyDictionary<string, string> GuideCodes = ReadGuideCodes();

    [Fact]
    public void Every_card_code_is_one_of_the_guides_vaccine_codes_with_its_display()
    {
        GuideCodes.Count.ShouldBe(179, "the guide's value set lists its codes one by one");
        foreach (var vaccine in VaccineCodes.All)
        {
            GuideCodes.ShouldContainKey(vaccine.Snomed, vaccine.Code);
            GuideCodes[vaccine.Snomed].ShouldBe(vaccine.SnomedDisplay, vaccine.Code);
        }
    }

    [Fact]
    public void Card_codes_are_unique_and_fit_the_column_as_stored()
    {
        VaccineCodes.All.Select(v => v.Code).ShouldBeUnique();
        VaccineCodes.All.ShouldAllBe(v => v.Code == v.Code.ToUpperInvariant() && v.Code.Length <= 32);
    }

    [Theory]
    [InlineData("BCG")]
    [InlineData("HEPB")]
    [InlineData("OPV")]
    [InlineData("PENTA")]
    [InlineData("ROTA")]
    [InlineData("FIPV")]
    [InlineData("PCV")]
    [InlineData("MR")]
    [InlineData("JE")]
    [InlineData("DPT")]
    [InlineData("TD")]
    public void Lists_every_vaccine_of_the_national_schedule(string code) => VaccineCodes.Find(code).ShouldNotBeNull();

    [Fact]
    public void Finds_a_code_in_any_case_and_nothing_else()
    {
        VaccineCodes.Find(" penta ")!.Snomed.ShouldBe("871886002");
        VaccineCodes.Find("PENTA-1").ShouldBeNull();
        VaccineCodes.Find(null).ShouldBeNull();
        VaccineCodes.Find("  ").ShouldBeNull();
    }

    private static Dictionary<string, string> ReadGuideCodes()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AbdmProfileValidator.PackageDirectory, "ValueSet-ndhm-vaccine-codes.json")));
        return document.RootElement.GetProperty("compose").GetProperty("include").EnumerateArray()
            .Where(include => include.GetProperty("system").GetString() == "http://snomed.info/sct")
            .SelectMany(include => include.GetProperty("concept").EnumerateArray())
            .ToDictionary(concept => concept.GetProperty("code").GetString()!, concept => concept.GetProperty("display").GetString()!);
    }
}
