using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

public sealed class WorklistRestrictionTests
{
    private static readonly Guid Vip = Guid.CreateVersion7();
    private static readonly Guid Other = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_restricted_patient_is_unnamed_and_their_clinical_text_left_out_everywhere_on_the_worklist()
    {
        var masked = WorklistRestriction.Mask(Worklist(), new HashSet<Guid> { Vip });

        masked.Queue.Single(q => q.PatientId == Vip).ShouldSatisfyAllConditions(
            q => q.PatientName.ShouldBe("Restricted patient"),
            q => q.ChiefComplaint.ShouldBeNull(),
            q => q.IsRestricted.ShouldBeTrue(),
            q => q.Uhid.ShouldBe("NH26000901", "The UHID still says who to call."),
            q => q.HasActiveAllergies.ShouldBeTrue("Safety flags stay."));
        masked.Appointments.Single(a => a.PatientId == Vip).ShouldSatisfyAllConditions(
            a => a.PatientName.ShouldBe("Restricted patient"),
            a => a.Reason.ShouldBeNull(),
            a => a.IsRestricted.ShouldBeTrue());
        masked.OpenEncounters.ShouldHaveSingleItem().ChiefComplaint.ShouldBeNull();
        masked.UnsignedNotes.ShouldHaveSingleItem().Title.ShouldBeNull();
        masked.DraftPrescriptions.ShouldHaveSingleItem().PatientName.ShouldBe("Restricted patient");
        masked.ResultsToReview.ShouldHaveSingleItem().Title.ShouldBe("Restricted record");
        masked.ChartDeficiencies.ShouldHaveSingleItem().Description.ShouldBe("Restricted record");
    }

    [Fact]
    public void Other_patients_are_left_as_they_are()
    {
        var worklist = Worklist();

        var masked = WorklistRestriction.Mask(worklist, new HashSet<Guid> { Vip });

        masked.Queue.Single(q => q.PatientId == Other).ShouldBe(worklist.Queue.Single(q => q.PatientId == Other));
        masked.Appointments.Single(a => a.PatientId == Other).ShouldBe(worklist.Appointments.Single(a => a.PatientId == Other));
    }

    [Fact]
    public void Every_patient_on_the_worklist_is_checked() =>
        WorklistRestriction.PatientIds(Worklist()).ShouldBe([Vip, Other], ignoreOrder: true);

    private static EmrWorklist Worklist() => new(
        new DateOnly(2026, 10, 2), Guid.CreateVersion7(), Guid.CreateVersion7(),
        [Token(Vip, "NH26000901"), Token(Other, "NH26000902")],
        [Booking(Vip), Booking(Other)],
        [new WorklistEncounter(Guid.CreateVersion7(), Vip, "NH26000901", "Leela Rao", EncounterType.OutPatient, Now, "Chest pain")],
        [new WorklistNote(Guid.CreateVersion7(), Vip, "NH26000901", "Leela Rao", ClinicalNoteType.ProgressNote, "HIV counselling", Now)],
        [new WorklistPrescription(Guid.CreateVersion7(), "RX-1", Vip, "NH26000901", "Leela Rao", Now, 2)],
        [new WorklistResult(Guid.CreateVersion7(), Guid.CreateVersion7(), DiagnosticKind.Laboratory, "HIV ELISA", false, Now, Vip, "NH26000901", "Leela Rao")],
        [new WorklistDeficiency(Guid.CreateVersion7(), Guid.CreateVersion7(), DeficiencyType.MissingDiagnosis, "Diagnosis for the HIV test", null, Vip, "NH26000901", "Leela Rao")]);

    private static WorklistQueueEntry Token(Guid patientId, string uhid) => new(
        Guid.CreateVersion7(), "GM-001", TokenStatus.Waiting, TokenPriority.Normal, Guid.CreateVersion7(), patientId, uhid, "Leela Rao", Gender.Female,
        "52 y", "Chest pain", false, Now, 10, null, null, true, false);

    private static WorklistAppointment Booking(Guid patientId) => new(
        Guid.CreateVersion7(), "APT-1", Now, Now.AddMinutes(15), AppointmentStatus.Booked, AppointmentVisitType.New, false, "Chest pain", patientId,
        "NH26000901", "Leela Rao", Gender.Female, "52 y", Guid.CreateVersion7(), "Dr Rao", null, null, null, null, null, false, false);
}
