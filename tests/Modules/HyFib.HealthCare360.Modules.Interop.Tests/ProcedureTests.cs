using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Search;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Testing.Abdm;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// Gap #26: procedures as FHIR <c>Procedure</c>, as the ABDM guide's Procedure profile reads them. The guide fixes a
/// procedure's code system to SNOMED CT, so a procedure HC360 coded in ICD-10-PCS goes by its name, never under the
/// wrong system.
/// </summary>
public sealed class ProcedureMapperTests
{
    private const string NrcesProcedure = "https://nrces.in/ndhm/fhir/r4/StructureDefinition/Procedure";

    private static readonly Guid ProcedureId = Guid.Parse("01923a4b-0000-7000-8000-00000000f001");
    private static readonly Guid EncounterId = Guid.Parse("01923a4b-0000-7000-8000-00000000f002");

    /// <summary>An appendicectomy recorded on the chart by the doctor who did it, coded from the procedure catalogue.</summary>
    private static ProcedureRow Appendicectomy() => new()
    {
        Id = ProcedureId,
        PatientId = TestData.PatientId,
        EncounterId = EncounterId,
        PerformedAt = new DateTimeOffset(2026, 9, 24, 4, 30, 0, TimeSpan.Zero),
        Code = "0DTJ4ZZ",
        CodeSystem = CodeReleases.Icd10Pcs,
        Display = "Laparoscopic appendicectomy",
        PerformerPractitionerId = TestData.DoctorId,
        Side = ProcedureSide.NotApplicable,
        BodySite = "Abdomen",
        Outcome = "Uneventful",
        Notes = "Drain removed on day 2",
        Source = ProcedureSource.Recorded,
        Status = ProcedureStatus.Completed,
        CreatedAt = TestData.Now,
    };

    /// <summary>A knee replacement the theatre completed, done by a visiting surgeon with no practitioner record here.</summary>
    private static ProcedureRow KneeReplacement() => Appendicectomy() with
    {
        Id = Guid.Parse("01923a4b-0000-7000-8000-00000000f003"),
        EncounterId = null,
        Code = null,
        CodeSystem = null,
        Display = "Total knee replacement",
        PerformerPractitionerId = null,
        PerformerName = "Dr. Menon (visiting)",
        Side = ProcedureSide.Left,
        BodySite = "Knee",
        Outcome = null,
        Notes = null,
        Source = ProcedureSource.OperationTheatre,
    };

    [Fact]
    public void A_procedure_on_the_chart_goes_by_its_name_with_who_did_it_when_and_how_it_went()
    {
        var procedure = ProcedureMapper.Map(Appendicectomy(), TestData.Directory);

        procedure.Id.ShouldBe(ProcedureId.ToString("D"));
        procedure.Status.ShouldBe(R4.EventStatus.Completed);
        procedure.Code.Text.ShouldBe("Laparoscopic appendicectomy");
        procedure.Code.Coding.ShouldBeEmpty("an ICD-10-PCS code is never sent under SNOMED CT, the only system the guide allows");
        procedure.Category.ShouldBeNull("a procedure on the chart may be minor or diagnostic; only the theatre's are surgical");
        procedure.Subject.Reference.ShouldBe($"Patient/{TestData.PatientId:D}");
        procedure.Encounter.Reference.ShouldBe($"Encounter/enc-{EncounterId:D}");
        ((R4.FhirDateTime)procedure.Performed).Value.ShouldBe("2026-09-24T10:00:00+05:30");
        var performer = procedure.Performer.ShouldHaveSingleItem().Actor;
        (performer.Display, performer.Type, performer.Identifier.Value).ShouldBe(("Dr. Asha Rao", "Practitioner", AbdmTestData.HprId));
        procedure.BodySite.ShouldHaveSingleItem().Text.ShouldBe("Abdomen");
        procedure.Outcome.Text.ShouldBe("Uneventful");
        procedure.Note.ShouldHaveSingleItem().Text.ShouldBe("Drain removed on day 2");
    }

    [Fact]
    public void An_operation_the_theatre_completed_is_surgical_and_a_visiting_surgeon_goes_by_name()
    {
        var procedure = ProcedureMapper.Map(KneeReplacement(), TestData.Directory);

        var category = procedure.Category.Coding.ShouldHaveSingleItem();
        (category.System, category.Code, category.Display).ShouldBe(("http://snomed.info/sct", "387713003", "Surgical procedure"));
        var performer = procedure.Performer.ShouldHaveSingleItem().Actor;
        (performer.Display, performer.Reference, performer.Identifier).ShouldBe(("Dr. Menon (visiting)", (string?)null, (R4.Identifier?)null));
        procedure.BodySite.ShouldHaveSingleItem().Text.ShouldBe("Knee, left");
        procedure.Encounter.ShouldBeNull("no encounter, visit or stay was recorded");
        procedure.Outcome.ShouldBeNull();
        procedure.Note.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ProcedureSide.NotApplicable, "Abdomen", "Abdomen")]
    [InlineData(ProcedureSide.Left, "Knee", "Knee, left")]
    [InlineData(ProcedureSide.Right, " Axilla ", "Axilla, right")]
    [InlineData(ProcedureSide.Bilateral, "Breast", "Breast, both sides")]
    [InlineData(ProcedureSide.Right, null, "Right side")]
    [InlineData(ProcedureSide.Left, "  ", "Left side")]
    [InlineData(ProcedureSide.Bilateral, null, "Both sides")]
    public void The_body_site_is_written_with_its_side(ProcedureSide side, string? site, string expected)
    {
        var bodySite = ProcedureMapper.BodySite(side, site).ShouldNotBeNull();

        bodySite.Text.ShouldBe(expected);
        bodySite.Coding.ShouldBeEmpty();
    }

    [Fact]
    public void No_body_site_is_sent_when_none_was_written_and_no_side_named() =>
        ProcedureMapper.BodySite(ProcedureSide.NotApplicable, " ").ShouldBeNull();

    [Fact]
    public void A_corrected_record_says_why_and_one_in_error_is_retracted()
    {
        var corrected = ProcedureMapper.Map(Appendicectomy() with { Status = ProcedureStatus.Amended, StatusReason = "Wrong side recorded", Notes = null },
            TestData.Directory);
        corrected.Status.ShouldBe(R4.EventStatus.Completed, "a corrected procedure was still done");
        corrected.Note.ShouldHaveSingleItem().Text.ShouldBe("Corrected: Wrong side recorded");

        var inError = ProcedureMapper.Map(Appendicectomy() with { Status = ProcedureStatus.EnteredInError, StatusReason = "Wrong patient" },
            TestData.Directory);
        inError.Status.ShouldBe(R4.EventStatus.EnteredInError);
        inError.Note.Select(n => n.Text).ShouldBe(["Drain removed on day 2", "Entered in error: Wrong patient"]);
    }

    [Fact]
    public void A_stay_without_a_clinician_encounter_is_the_encounter_of_its_in_patient_visit()
    {
        var admissionId = Guid.Parse("01923a4b-0000-7000-8000-00000000f004");
        var visitId = Guid.Parse("01923a4b-0000-7000-8000-00000000f005");
        var refs = new ReferenceDirectory { AdmissionVisits = new Dictionary<Guid, Guid> { [admissionId] = visitId } };

        var procedure = ProcedureMapper.Map(Appendicectomy() with { EncounterId = null, AdmissionId = admissionId }, refs);

        procedure.Encounter.Reference.ShouldBe($"Encounter/visit-{visitId:D}");
    }

    [Fact]
    public void Meets_the_abdm_guides_procedure_profile()
    {
        foreach (var row in new[]
                 {
                     Appendicectomy(),
                     KneeReplacement(),
                     Appendicectomy() with { Status = ProcedureStatus.Amended, StatusReason = "Wrong side recorded" },
                     KneeReplacement() with { Status = ProcedureStatus.EnteredInError, StatusReason = "Wrong patient", BodySite = null },
                 })
        {
            var validation = AbdmProfileValidator.Validate(ProcedureMapper.Map(row, TestData.Directory), NrcesProcedure);

            validation.Errors.ShouldBeEmpty(validation.Describe());
        }
    }

    [Fact]
    public void Searches_leave_out_records_in_error_unless_asked_for_and_find_the_day_it_was_done_in_hospital_time()
    {
        R4.Resource[] procedures =
        [
            ProcedureMapper.Map(Appendicectomy(), TestData.Directory),

            // 19:00 UTC on the 24th is 00:30 on the 25th in the hospital.
            ProcedureMapper.Map(KneeReplacement() with { PerformedAt = new DateTimeOffset(2026, 9, 24, 19, 0, 0, TimeSpan.Zero) }, TestData.Directory),
            ProcedureMapper.Map(Appendicectomy() with { Id = Guid.Parse("01923a4b-0000-7000-8000-00000000f006"), Status = ProcedureStatus.EnteredInError },
                TestData.Directory),
        ];
        int Total(string query) => ResourceSearch.Execute(procedures, TestData.Search("Procedure", $"patient={TestData.PatientId:D}&{query}")).Total;

        Total("_count=10").ShouldBe(2);
        Total("status=entered-in-error").ShouldBe(1);
        Total("status=completed,entered-in-error").ShouldBe(3);
        Total("date=2026-09-24").ShouldBe(1);
        Total("date=2026-09-25").ShouldBe(1);
        Total("date=gt2026-09-25").ShouldBe(0);
    }

    [Fact]
    public void A_consent_reads_a_procedure_as_one_and_dates_it_by_the_day_it_was_done()
    {
        var procedure = ProcedureMapper.Map(KneeReplacement() with { PerformedAt = new DateTimeOffset(2026, 9, 24, 19, 0, 0, TimeSpan.Zero) },
            TestData.Directory);

        SharedRecords.KindOf(procedure).ShouldBe(SharedRecordKind.Procedures);
        SharedRecords.DateOf(procedure).ShouldBe(new DateOnly(2026, 9, 25), "00:30 on the 25th in the hospital");
    }
}
