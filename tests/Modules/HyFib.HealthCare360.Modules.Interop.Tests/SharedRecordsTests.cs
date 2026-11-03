using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Patients.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// Gap #28: the kind and the day of each record the FHIR API serves, as a record-sharing consent's scope reads them,
/// taken from the resource as it would leave.
/// </summary>
public sealed class SharedRecordsTests
{
    [Fact]
    public void Each_resource_is_of_the_kind_a_patient_would_name()
    {
        SharedRecords.KindOf(new R4.Encounter()).ShouldBe(SharedRecordKind.Visits);
        SharedRecords.KindOf(new R4.Condition()).ShouldBe(SharedRecordKind.Visits);
        SharedRecords.KindOf(new R4.AllergyIntolerance()).ShouldBe(SharedRecordKind.Allergies);
        SharedRecords.KindOf(new R4.MedicationRequest()).ShouldBe(SharedRecordKind.Medicines);
        SharedRecords.KindOf(new R4.DocumentReference()).ShouldBe(SharedRecordKind.Documents);
        SharedRecords.KindOf(new R4.Immunization()).ShouldBe(SharedRecordKind.Immunisations);
    }

    [Theory]
    [InlineData(VitalSignObservations.Laboratory, SharedRecordKind.LabResults)]
    [InlineData(VitalSignObservations.VitalSigns, SharedRecordKind.VitalSigns)]
    [InlineData(VitalSignObservations.Survey, SharedRecordKind.VitalSigns)]
    [InlineData(VitalSignObservations.Exam, SharedRecordKind.VitalSigns)]
    public void An_observation_is_a_lab_result_or_part_of_the_vital_signs(string category, SharedRecordKind kind) =>
        SharedRecords.KindOf(new R4.Observation { Category = [VitalSignObservations.Category(category)] }).ShouldBe(kind);

    [Theory]
    [InlineData("LAB", SharedRecordKind.LabResults)]
    [InlineData("RAD", SharedRecordKind.ScansAndHeartTests)]
    [InlineData("CUS", SharedRecordKind.ScansAndHeartTests)]
    [InlineData("CG", SharedRecordKind.ScansAndHeartTests)]
    public void A_report_is_a_lab_report_or_a_scan_or_heart_test(string section, SharedRecordKind kind) =>
        SharedRecords.KindOf(new R4.DiagnosticReport
        {
            Category = [new R4.CodeableConcept { Coding = [new R4.Coding("http://terminology.hl7.org/CodeSystem/v2-0074", section)] }],
        }).ShouldBe(kind);

    [Fact]
    public void A_record_is_dated_by_the_hospitals_day_when_it_happened()
    {
        // 23:30 UTC is 05:00 the next morning in India.
        SharedRecords.DateOf(new R4.Encounter { Period = new R4.Period { Start = "2026-09-21T23:30:00+00:00" } }).ShouldBe(new DateOnly(2026, 9, 22));
        SharedRecords.DateOf(new R4.Observation { Effective = new R4.FhirDateTime("2026-09-21T10:15:00+05:30") }).ShouldBe(new DateOnly(2026, 9, 21));
        SharedRecords.DateOf(new R4.DiagnosticReport { Effective = new R4.Period { Start = "2026-09-20T09:00:00+05:30" } }).ShouldBe(new DateOnly(2026, 9, 20));
        SharedRecords.DateOf(new R4.MedicationRequest { AuthoredOn = "2026-09-19T18:45:00+05:30" }).ShouldBe(new DateOnly(2026, 9, 19));
        SharedRecords.DateOf(new R4.AllergyIntolerance { RecordedDate = "2026-09-18T08:00:00+05:30" }).ShouldBe(new DateOnly(2026, 9, 18));
        SharedRecords.DateOf(new R4.DocumentReference { Date = new DateTimeOffset(2026, 9, 17, 20, 0, 0, TimeSpan.Zero) }).ShouldBe(new DateOnly(2026, 9, 18));
        SharedRecords.DateOf(new R4.Immunization { Occurrence = new R4.FhirDateTime("2026-09-16") }).ShouldBe(new DateOnly(2026, 9, 16), "the day the vaccine was given");
    }

    [Fact]
    public void A_diagnosis_is_dated_when_it_was_recorded_not_when_the_illness_began()
    {
        var condition = new R4.Condition { RecordedDate = "2026-09-21T10:00:00+05:30", Onset = new R4.FhirDateTime("2010-03-15") };

        SharedRecords.DateOf(condition).ShouldBe(new DateOnly(2026, 9, 21));
        SharedRecords.DateOf(new R4.Condition { Onset = new R4.FhirDateTime("2026-01-05") }).ShouldBe(new DateOnly(2026, 1, 5), "a calendar date stays the date it names");
    }

    [Fact]
    public void A_result_without_its_collection_time_is_dated_when_it_was_issued() =>
        SharedRecords.DateOf(new R4.Observation { Issued = new DateTimeOffset(2026, 9, 21, 4, 0, 0, TimeSpan.Zero) }).ShouldBe(new DateOnly(2026, 9, 21));

    [Fact]
    public void A_record_with_no_date_has_none() => SharedRecords.DateOf(new R4.Encounter()).ShouldBeNull();
}
