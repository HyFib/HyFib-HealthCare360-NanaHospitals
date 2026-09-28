using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

public sealed class MedicationRequestMapperTests
{
    private static readonly Guid ItemId = Guid.Parse("01923a4b-0000-7000-8000-00000000f001");
    private static readonly Guid VisitId = Guid.Parse("01923a4b-0000-7000-8000-00000000f002");

    /// <summary>Paracetamol twice a day for 5 days, signed yesterday (IST).</summary>
    private static PrescriptionItemRow Paracetamol() => new()
    {
        Id = ItemId,
        PatientId = TestData.PatientId,
        PrescriptionId = Guid.Parse("01923a4b-0000-7000-8000-00000000f003"),
        PrescriptionNumber = "NHRX260000101",
        VisitId = VisitId,
        PractitionerId = TestData.DoctorId,
        Status = PrescriptionStatus.Signed,
        PrescribedAt = TestData.Now.AddDays(-1),
        SignedAt = TestData.Now.AddDays(-1),
        RefillsAllowed = 1,
        Sequence = 1,
        DrugName = "Dolo 650",
        GenericName = "Paracetamol",
        Strength = "650 mg",
        DosageForm = "Tablet",
        Route = "Oral",
        Dose = "1 tab",
        Frequency = DoseFrequency.BD,
        DurationDays = 5,
        Quantity = 10m,
        Instructions = "After food",
        CreatedAt = TestData.Now.AddDays(-1),
    };

    private static R4.MedicationRequest Map(PrescriptionItemRow row) => MedicationRequestMapper.Map(row, TestData.Directory, TestData.Today);

    [Fact]
    public void Maps_a_signed_prescription_line()
    {
        var request = Map(Paracetamol());

        request.Id.ShouldBe(ItemId.ToString("D"));
        request.Status.ShouldBe(R4.MedicationRequest.MedicationrequestStatus.Active);
        request.Intent.ShouldBe(R4.MedicationRequest.MedicationRequestIntent.Order);
        request.Category.ShouldHaveSingleItem().Coding.ShouldHaveSingleItem().Code.ShouldBe("outpatient");
        request.Medication.ShouldBeOfType<R4.CodeableConcept>().Text.ShouldBe("Dolo 650 Tablet (Paracetamol)");
        request.Subject.Reference.ShouldBe($"Patient/{TestData.PatientId:D}");
        request.Encounter.Reference.ShouldBe($"Encounter/visit-{VisitId:D}");
        request.AuthoredOn.ShouldBe("2026-09-24T10:30:00+05:30");
        request.Requester.Display.ShouldBe("Dr. Asha Rao");
        request.GroupIdentifier.System.ShouldBe("urn:hc360:prescription-number");
        request.GroupIdentifier.Value.ShouldBe("NHRX260000101");
        request.DispenseRequest.Quantity.Value.ShouldBe(10m);
        request.DispenseRequest.NumberOfRepeatsAllowed.ShouldBe(1);
        request.DispenseRequest.ExpectedSupplyDuration.Value.ShouldBe(5m);
    }

    [Fact]
    public void Writes_the_dosage_as_text_and_structured_timing()
    {
        var dosage = Map(Paracetamol()).DosageInstruction.ShouldHaveSingleItem();

        dosage.Text.ShouldBe("1 tab, twice a day, for 5 days. After food");
        dosage.PatientInstruction.ShouldBe("After food");
        dosage.Route.Text.ShouldBe("Oral");
        dosage.Timing.Code.Coding.ShouldHaveSingleItem().Code.ShouldBe("BID");
        dosage.Timing.Repeat.Frequency.ShouldBe(2);
        dosage.Timing.Repeat.Period.ShouldBe(1m);
        dosage.Timing.Repeat.PeriodUnit.ShouldBe(R4.Timing.UnitsOfTime.D);
        dosage.Timing.Repeat.Bounds.ShouldBeOfType<R4.Duration>().Value.ShouldBe(5m);
        dosage.AsNeeded.ShouldBeNull();
    }

    [Theory]
    [InlineData(DoseFrequency.OD, "QD", 1)]
    [InlineData(DoseFrequency.BD, "BID", 2)]
    [InlineData(DoseFrequency.TDS, "TID", 3)]
    [InlineData(DoseFrequency.QID, "QID", 4)]
    [InlineData(DoseFrequency.HS, "BED", 1)]
    public void Codes_daily_frequencies(DoseFrequency frequency, string code, int perDay)
    {
        var timing = MedicationRequestMapper.Timing(frequency, 5).ShouldNotBeNull();

        timing.Code.Coding[0].Code.ShouldBe(code);
        timing.Repeat.Frequency.ShouldBe(perDay);
        timing.Repeat.PeriodUnit.ShouldBe(R4.Timing.UnitsOfTime.D);
    }

    [Fact]
    public void Bedtime_doses_are_timed_at_bedtime() =>
        MedicationRequestMapper.Timing(DoseFrequency.HS, 5)!.Repeat.When.ShouldBe([R4.Timing.EventTiming.HS]);

    [Fact]
    public void A_weekly_dose_repeats_every_week()
    {
        var timing = MedicationRequestMapper.Timing(DoseFrequency.Weekly, 28).ShouldNotBeNull();

        timing.Code.Coding[0].Code.ShouldBe("WK");
        timing.Repeat.PeriodUnit.ShouldBe(R4.Timing.UnitsOfTime.Wk);
    }

    [Fact]
    public void A_stat_dose_is_given_once()
    {
        var timing = MedicationRequestMapper.Timing(DoseFrequency.STAT, 1).ShouldNotBeNull();

        timing.Code.ShouldBeNull();
        timing.Repeat.Count.ShouldBe(1);
        timing.Repeat.Bounds.ShouldBeNull();
    }

    [Fact]
    public void An_sos_medicine_is_as_needed()
    {
        var dosage = Map(Paracetamol() with { Frequency = DoseFrequency.SOS }).DosageInstruction[0];

        dosage.AsNeeded.ShouldBeOfType<R4.FhirBoolean>().Value.ShouldBe(true);
        dosage.Timing.Code.ShouldBeNull();
        dosage.Text.ShouldContain("only when required");
    }

    [Fact]
    public void A_custom_pattern_is_kept_in_the_text() =>
        Map(Paracetamol() with { Frequency = DoseFrequency.Custom, CustomFrequency = "1-0-1" }).DosageInstruction[0].Text.ShouldBe("1 tab, 1-0-1, for 5 days. After food");

    [Fact]
    public void Is_completed_after_the_last_day_of_the_course()
    {
        MedicationRequestMapper.Status(Paracetamol(), TestData.Today.AddDays(3)).ShouldBe(R4.MedicationRequest.MedicationrequestStatus.Active);
        MedicationRequestMapper.Status(Paracetamol(), TestData.Today.AddDays(4)).ShouldBe(R4.MedicationRequest.MedicationrequestStatus.Completed);
    }

    [Fact]
    public void A_signed_prescription_cancelled_later_is_cancelled_with_its_reason()
    {
        var request = Map(Paracetamol() with { Status = PrescriptionStatus.Cancelled, CancelledAt = TestData.Now, CancelReason = "Duplicate" });

        request.Status.ShouldBe(R4.MedicationRequest.MedicationrequestStatus.Cancelled);
        request.StatusReason.Text.ShouldBe("Duplicate");
    }

    [Fact]
    public void In_patient_lines_are_in_the_inpatient_category() =>
        Map(Paracetamol() with { IsInpatient = true }).Category[0].Coding[0].Code.ShouldBe("inpatient");

    [Fact]
    public void An_in_patient_line_without_a_visit_refers_to_the_admission_encounter()
    {
        var admissionId = Guid.Parse("01923a4b-0000-7000-8000-00000000f004");
        var directory = new ReferenceDirectory { AdmissionVisits = new Dictionary<Guid, Guid> { [admissionId] = VisitId } };

        var request = MedicationRequestMapper.Map(Paracetamol() with { VisitId = null, AdmissionId = admissionId }, directory, TestData.Today);

        request.Encounter.Reference.ShouldBe($"Encounter/visit-{VisitId:D}");
    }

    [Theory]
    [InlineData("Dolo 650", "650 mg", "Tablet", "Paracetamol", "Dolo 650 Tablet (Paracetamol)")]
    [InlineData("Amoxicillin", "500 mg", "Capsule", "Amoxicillin", "Amoxicillin 500 mg Capsule")]
    [InlineData("Pan-D", null, null, null, "Pan-D")]
    public void Names_the_medicine_without_repeating_itself(string drug, string? strength, string? form, string? generic, string expected) =>
        MedicationRequestMapper.MedicationText(Paracetamol() with { DrugName = drug, Strength = strength, DosageForm = form, GenericName = generic }).ShouldBe(expected);
}
