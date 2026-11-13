using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// Which clinical events link which record to the patient's ABHA without staff, and which patients' records are left
/// to staff: the rules of <see cref="AbdmAutoLinking"/> that need no database.
/// </summary>
public sealed class AbdmAutoLinkingTests
{
    private static readonly Guid Record = Guid.Parse("01933a4b-0000-7000-8000-0000000000a1");
    private static readonly Guid Patient = Guid.Parse("01933a4b-0000-7000-8000-0000000000b2");
    private static readonly Guid Facility = Guid.Parse("01933a4b-0000-7000-8000-0000000000c3");
    private static readonly DateTimeOffset At = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static EncounterSigned Signed(EncounterType type, Guid? admissionId = null) =>
        new(Record, Patient, Facility, null, admissionId, Guid.NewGuid(), type, null, null, null, At);

    private static DiagnosticReportReleased Released(DiagnosticKind kind) => new(Record, kind, Patient, null, null, "CBC", false, At);

    private static PrescriptionSigned Prescribed(bool inpatient) =>
        new(Record, "RX-1", Patient, null, inpatient ? Guid.NewGuid() : null, Facility, Guid.NewGuid(), inpatient, [], At);

    [Theory]
    [InlineData(EncounterType.OutPatient)]
    [InlineData(EncounterType.Emergency)]
    [InlineData(EncounterType.Telehealth)]
    [InlineData(EncounterType.HealthCheckup)]
    public void A_signed_out_patient_note_links_its_consultation(EncounterType type) =>
        AbdmAutoLinking.Completed(Signed(type)).ShouldBe((AbdmRecordType.OpConsultation, Record, Patient, (Guid?)Facility));

    [Fact]
    public void An_in_patient_note_links_nothing_as_the_stays_discharge_summary_carries_it()
    {
        AbdmAutoLinking.Completed(Signed(EncounterType.InPatient)).ShouldBeNull();
        AbdmAutoLinking.Completed(Signed(EncounterType.OutPatient, admissionId: Guid.NewGuid())).ShouldBeNull();
    }

    [Fact]
    public void A_signed_discharge_summary_links_itself() =>
        AbdmAutoLinking.Completed(new DischargeSummarySigned(Record, Guid.NewGuid(), Patient, Facility, At))
            .ShouldBe((AbdmRecordType.DischargeSummary, Record, Patient, (Guid?)Facility));

    [Fact]
    public void A_released_lab_or_radiology_report_links_itself_and_a_cardiology_report_does_not()
    {
        AbdmAutoLinking.Completed(Released(DiagnosticKind.Laboratory)).ShouldBe((AbdmRecordType.LabReport, Record, Patient, (Guid?)null));
        AbdmAutoLinking.Completed(Released(DiagnosticKind.Radiology)).ShouldBe((AbdmRecordType.RadiologyReport, Record, Patient, (Guid?)null));
        AbdmAutoLinking.Completed(Released(DiagnosticKind.Cardiology)).ShouldBeNull();
    }

    [Fact]
    public void An_out_patient_prescription_links_itself_and_a_ward_prescription_does_not()
    {
        AbdmAutoLinking.Completed(Prescribed(inpatient: false)).ShouldBe((AbdmRecordType.Prescription, Record, Patient, (Guid?)Facility));
        AbdmAutoLinking.Completed(Prescribed(inpatient: true)).ShouldBeNull();
    }

    [Fact]
    public void Other_events_link_nothing()
    {
        AbdmAutoLinking.Completed(new EncounterAmended(Record, Patient, "typo", At)).ShouldBeNull();
        AbdmAutoLinking.Completed(new PrescriptionCancelled(Record, Patient, "wrong drug")).ShouldBeNull();
    }

    [Fact]
    public void A_patient_with_an_abha_address_and_a_date_of_birth_is_linked_automatically() =>
        AbdmAutoLinking.NotLinkedAutomatically("meera.k@sbx", new DateOnly(1984, 5, 1), null, false, SensitiveCategory.None).ShouldBeNull();

    [Theory]
    [InlineData(null, true, false, false, SensitiveCategory.None, "no ABHA address")]
    [InlineData("  ", true, false, false, SensitiveCategory.None, "no ABHA address")]
    [InlineData("meera.k@sbx", false, false, false, SensitiveCategory.None, "no date of birth")]
    [InlineData("meera.k@sbx", true, true, false, SensitiveCategory.None, "merged")]
    [InlineData("meera.k@sbx", true, false, true, SensitiveCategory.None, "staff link it")]
    [InlineData("meera.k@sbx", true, false, false, SensitiveCategory.Hiv, "staff link it")]
    [InlineData("meera.k@sbx", true, false, false, SensitiveCategory.MentalHealth, "staff link it")]
    [InlineData("meera.k@sbx", true, false, false, SensitiveCategory.Mtp, "staff link it")]
    public void Other_patients_records_are_left_to_staff(string? address, bool hasBirthDate, bool merged, bool vip, SensitiveCategory categories, string why) =>
        AbdmAutoLinking.NotLinkedAutomatically(address, hasBirthDate ? new DateOnly(1984, 5, 1) : null, merged ? Guid.NewGuid() : null, vip, categories)
            .ShouldNotBeNull().ShouldContain(why);
}
