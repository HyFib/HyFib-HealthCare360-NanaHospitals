using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Testing.Abdm;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// Gap #25: a vaccine dose given here as an ABDM immunisation record (the guide's ImmunizationRecord), with the next dose
/// when one is due. The sample document is in Fixtures/Abdm with the other kinds (<see cref="AbdmDocumentTests"/>, which
/// validates it against the guide); these tests say what it holds, and what every kind of ABDM record now shares: its
/// name as a care context, its HI type, and a consent's kinds of record.
/// </summary>
public sealed class AbdmImmunizationRecordTests
{
    private static readonly DateTimeOffset RecordedAt = new(2026, 9, 25, 5, 0, 0, TimeSpan.Zero);

    private static R4.Bundle Build(ImmunizationData data) => AbdmDocumentBuilder.Build(AbdmRecords.Immunization(data));

    private static R4.Bundle Build(ImmunizationRow dose) => Build(AbdmTestData.ImmunizationRecord(dose));

    private static T Single<T>(R4.Bundle bundle)
        where T : R4.Resource =>
        bundle.Entry.Select(e => e.Resource).OfType<T>().Single();

    private static string FullUrl(R4.Bundle bundle, R4.Resource resource) => bundle.Entry.Single(e => ReferenceEquals(e.Resource, resource)).FullUrl!;

    private static void ShouldMeetTheGuide(R4.Bundle bundle)
    {
        var validation = AbdmProfileValidator.Validate(bundle, AbdmProfileValidator.DocumentBundle);
        validation.Errors.ShouldBeEmpty(validation.Describe());
    }

    [Fact]
    public void A_dose_given_here_is_the_hospitals_immunisation_record_with_the_next_dose_due()
    {
        var document = AbdmRecords.Immunization(AbdmTestData.ImmunizationRecord());
        var bundle = AbdmDocumentBuilder.Build(document);
        var composition = (R4.Composition)bundle.Entry[0].Resource;
        var hospital = Single<R4.Organization>(bundle);
        var dose = Single<R4.Immunization>(bundle);
        var next = Single<R4.ImmunizationRecommendation>(bundle);

        // Saved under the UHID, the card code and the day it was given: three vaccines are given at birth.
        document.FileName.ShouldBe($"immunization-{AbdmTestData.Patient().Uhid}-TD-2026-09-25-abdm.json");
        composition.Title.ShouldBe("Immunisation record");
        composition.Status.ShouldBe(R4.CompositionStatus.Final);
        composition.Author.ShouldHaveSingleItem().Reference.ShouldBe(FullUrl(bundle, hospital));
        composition.Custodian.Reference.ShouldBe(FullUrl(bundle, hospital));
        composition.Encounter.Reference.ShouldBe(FullUrl(bundle, Single<R4.Encounter>(bundle)));
        var section = composition.Section.ShouldHaveSingleItem();
        var code = section.Code.Coding.ShouldHaveSingleItem();
        (code.System, code.Code, code.Display).ShouldBe(("http://snomed.info/sct", "41000179103", "Immunization record"));
        section.Entry.Select(e => (e.Type, e.Reference)).ShouldBe(
            [("Immunization", FullUrl(bundle, dose)), ("ImmunizationRecommendation", FullUrl(bundle, next))]);

        // The dose as the FHIR API serves it, with the guide's profile, in the document's patient and encounter.
        dose.Meta.Profile.ShouldBe([AbdmProfiles.Immunization]);
        dose.Status.ShouldBe(R4.Immunization.ImmunizationStatusCodes.Completed);
        dose.PrimarySource.ShouldBe(true);
        var vaccine = dose.VaccineCode.Coding.ShouldHaveSingleItem();
        (vaccine.System, vaccine.Code, vaccine.Display).ShouldBe(("http://snomed.info/sct", "871827009", "Low dose diphtheria and tetanus vaccine"));
        dose.Patient.Reference.ShouldBe(composition.Subject.Reference);
        dose.Encounter.Reference.ShouldBe(composition.Encounter.Reference);
        dose.LotNumber.ShouldBe("TD2608-114");
        dose.Performer.ShouldHaveSingleItem().Actor.Display.ShouldBe("Sr. Meena Pillai");

        // The next dose: the same vaccine, due on the day staff wrote, the second of the series, on the strength of this one,
        // recommended by the hospital when the dose was recorded.
        next.Meta.Profile.ShouldBe([AbdmProfiles.ImmunizationRecommendation]);
        next.Patient.Reference.ShouldBe(composition.Subject.Reference);
        next.Authority.Reference.ShouldBe(FullUrl(bundle, hospital));
        next.Date.ShouldBe(dose.Recorded);
        var recommendation = next.Recommendation.ShouldHaveSingleItem();
        recommendation.VaccineCode.ShouldHaveSingleItem().Coding.ShouldHaveSingleItem().Code.ShouldBe("871827009");
        var status = recommendation.ForecastStatus.Coding.ShouldHaveSingleItem();
        (status.System, status.Code, status.Display).ShouldBe(("http://terminology.hl7.org/CodeSystem/immunization-recommendation-status", "due", "Due"));
        var due = recommendation.DateCriterion.ShouldHaveSingleItem();
        var criterion = due.Code.Coding.ShouldHaveSingleItem();
        (criterion.System, criterion.Code, criterion.Display).ShouldBe(("http://loinc.org", "30980-7", "Date vaccine due"));
        due.Value.ShouldBe("2026-10-23");
        recommendation.DoseNumber.ShouldBeOfType<R4.PositiveInt>().Value.ShouldBe(2);
        recommendation.SupportingImmunization.ShouldHaveSingleItem().Reference.ShouldBe(FullUrl(bundle, dose));

        // What a person reads: the dose as its card has it, and when the next one is due.
        section.Text.Div.ShouldContain("<p>Td, dose 1: given 2026-09-25, batch TD2608-114, Intramuscular route, Left deltoid</p>");
        section.Text.Div.ShouldContain("<p>Next dose due 2026-10-23: Td, dose 2</p>");
        ShouldMeetTheGuide(bundle);
    }

    [Fact]
    public void A_dose_with_no_next_dose_due_is_the_dose_alone()
    {
        var bundle = Build(AbdmTestData.TdDose() with { NextDueOn = null });

        bundle.Entry.Select(e => e.Resource).OfType<R4.ImmunizationRecommendation>().ShouldBeEmpty();
        ((R4.Composition)bundle.Entry[0].Resource).Section.ShouldHaveSingleItem().Entry.ShouldHaveSingleItem().Type.ShouldBe("Immunization");
        ShouldMeetTheGuide(bundle);
    }

    [Fact]
    public void A_vaccine_hc360_has_no_code_for_and_a_dose_of_no_known_number_go_by_name_alone()
    {
        var bundle = Build(AbdmTestData.TdDose() with { VaccineCode = null, VaccineName = "Tdap booster (private)", DoseNumber = null });

        Single<R4.Immunization>(bundle).VaccineCode.Coding.ShouldBeEmpty("HC360 never guesses a code");
        var recommendation = Single<R4.ImmunizationRecommendation>(bundle).Recommendation.ShouldHaveSingleItem();
        var vaccine = recommendation.VaccineCode.ShouldHaveSingleItem();
        vaccine.Coding.ShouldBeEmpty();
        vaccine.Text.ShouldBe("Tdap booster (private)");
        recommendation.DoseNumber.ShouldBeNull("the next dose's number is not known when this one's is not");
        ((R4.Composition)bundle.Entry[0].Resource).Section[0].Text.Div.ShouldContain("<p>Next dose due 2026-10-23: Tdap booster (private)</p>");
        ShouldMeetTheGuide(bundle);
    }

    [Fact]
    public void A_dose_whose_encounter_the_document_does_not_hold_points_to_none()
    {
        // Given outside an encounter, or in one the export could not open: the FHIR API still names the encounter, but a
        // document refers only to what it holds.
        foreach (var dose in new[] { AbdmTestData.TdDose() with { EncounterId = null }, AbdmTestData.TdDose() })
        {
            var data = AbdmTestData.ImmunizationRecord(dose);
            var bundle = Build(data with { Context = data.Context with { Encounter = null } });

            ((R4.Composition)bundle.Entry[0].Resource).Encounter.ShouldBeNull();
            Single<R4.Immunization>(bundle).Encounter.ShouldBeNull();
            bundle.Entry.Select(e => e.Resource).OfType<R4.Encounter>().ShouldBeEmpty();
            ShouldMeetTheGuide(bundle);
        }
    }

    [Fact]
    public void Only_a_dose_given_here_and_not_in_error_is_exported()
    {
        AbdmRecordLoader.ImmunizationNotExportable(isExternal: false, isEnteredInError: false).ShouldBeNull();
        AbdmRecordLoader.ImmunizationNotExportable(isExternal: true, isEnteredInError: false).ShouldNotBeNull().ShouldContain("vaccination card");
        AbdmRecordLoader.ImmunizationNotExportable(isExternal: false, isEnteredInError: true).ShouldBe("A dose marked as entered in error is not exported.");
        AbdmRecordLoader.ImmunizationNotExportable(isExternal: true, isEnteredInError: true).ShouldBe("A dose marked as entered in error is not exported.");
    }

    [Fact]
    public void A_dose_is_named_for_the_patient_by_its_vaccine_and_number()
    {
        AbdmCareContexts.Describe(AbdmRecordType.Immunization, AbdmTestData.DoseId, RecordedAt, AbdmRecordLoader.DoseLabel("Td", 1)).Display
            .ShouldBe("Immunisation record: Td, dose 1, 25 Sep 2026");
        AbdmCareContexts.Describe(AbdmRecordType.LabReport, AbdmTestData.DoseId, RecordedAt).Display.ShouldBe("Lab report, 25 Sep 2026");
        AbdmRecordLoader.DoseLabel(" BCG ", null).ShouldBe("BCG");
        AbdmRecordLoader.DoseLabel("OPV", 0).ShouldBe("OPV");

        // A long name is shortened so the name fits where ABDM keeps it.
        var display = AbdmCareContexts.Describe(AbdmRecordType.Immunization, AbdmTestData.DoseId, RecordedAt, new string('x', 200)).Display;
        display.Length.ShouldBeLessThanOrEqualTo(200);
        display.ShouldEndWith("…, 25 Sep 2026");
    }

    [Fact]
    public void Every_kind_of_record_has_its_own_care_context_name_and_an_abdm_hi_type()
    {
        var kinds = Enum.GetValues<AbdmRecordType>();
        var id = AbdmTestData.DoseId;

        kinds.Select(k => AbdmLinkingService.ReferenceNumber(k, id).Split('/')[0]).ShouldBeUnique();
        kinds.Select(AbdmBundleService.Describe).ShouldBeUnique();
        kinds.Select(k => AbdmRecordLoader.NotFound(k, id).Code).ShouldBeUnique();
        kinds.ShouldAllBe(k => AbdmConsentRequestService.HiTypes.Contains(AbdmGatewayMessages.HiType(k)));
        (AbdmGatewayMessages.HiType(AbdmRecordType.Immunization), AbdmLinkingService.ReferenceNumber(AbdmRecordType.Immunization, id))
            .ShouldBe(("ImmunizationRecord", $"immunization/{id:D}"));
    }

    [Fact]
    public void A_kind_of_record_hc360_does_not_export_is_refused_rather_than_named_as_another()
    {
        var unknown = (AbdmRecordType)99;

        Should.Throw<ArgumentOutOfRangeException>(() => AbdmGatewayMessages.HiType(unknown));
        Should.Throw<ArgumentOutOfRangeException>(() => AbdmLinkingService.ReferenceNumber(unknown, Guid.Empty));
        Should.Throw<ArgumentOutOfRangeException>(() => AbdmBundleService.Describe(unknown));
        Should.Throw<ArgumentOutOfRangeException>(() => AbdmRecordLoader.NotFound(unknown, Guid.Empty));
    }

    [Theory]
    [InlineData("OPConsultation,Prescription", AbdmRecordType.OpConsultation, true)]
    [InlineData("OPConsultation,Prescription", AbdmRecordType.Immunization, false)]
    [InlineData("ImmunizationRecord", AbdmRecordType.Immunization, true)]
    [InlineData(" immunizationrecord , Prescription", AbdmRecordType.Immunization, true)]
    [InlineData("DiagnosticReport", AbdmRecordType.CardiologyReport, true)]
    [InlineData("DiagnosticReport", AbdmRecordType.DischargeSummary, false)]
    [InlineData("", AbdmRecordType.Prescription, false)]
    [InlineData(null, AbdmRecordType.Prescription, false)]
    public void A_consent_covers_only_the_kinds_of_record_it_names(string? hiTypes, AbdmRecordType type, bool covered) =>
        AbdmDataFlowService.Covers(hiTypes, type).ShouldBe(covered);
}
