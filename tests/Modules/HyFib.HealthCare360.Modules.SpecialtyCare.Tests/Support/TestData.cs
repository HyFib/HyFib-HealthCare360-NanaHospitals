using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

/// <summary>Fixed instants and small builders so domain tests read as clinical scenarios.</summary>
internal static class TestData
{
    /// <summary>25 Sep 2026, 10:00 IST.</summary>
    public static readonly DateTimeOffset Now = new(2026, 9, 25, 4, 30, 0, TimeSpan.Zero);

    public static readonly DateOnly Today = new(2026, 9, 25);

    public static StaffStamp Staff(DateTimeOffset? at = null, string name = "Sr. Anita Joseph") => new(Guid.CreateVersion7(), name, at ?? Now);

    public static IReadOnlyList<ChecklistInput> AllDone(IEnumerable<ChecklistItemDefinition> items) =>
        items.Select(i => new ChecklistInput(i.Key, ChecklistItemState.Done, null)).ToList();

    public static SurgeryBooking NewBooking(AnaesthesiaType anaesthesia = AnaesthesiaType.GA) => new()
    {
        PatientId = Guid.CreateVersion7(),
        SurgeonId = Guid.CreateVersion7(),
        AnaesthetistId = anaesthesia == AnaesthesiaType.Local ? null : Guid.CreateVersion7(),
        AnaesthesiaType = anaesthesia,
        ProcedureName = "Laparoscopic cholecystectomy",
        Status = SurgeryStatus.Requested,
    };

    public static SurgeryBooking Scheduled()
    {
        var booking = NewBooking();
        booking.Schedule(Guid.CreateVersion7(), Now.AddHours(1), Now.AddHours(3), Now).IsSuccess.ShouldBeTrue();
        return booking;
    }

    /// <summary>Scheduled, received in pre-op, pre-op checklist complete.</summary>
    public static SurgeryBooking ReadyForSignIn()
    {
        var booking = Scheduled();
        booking.ReceiveInPreOp(Now).IsSuccess.ShouldBeTrue();
        booking.RecordPreOp(AllDone(PreOpChecklist.Items), Staff()).IsSuccess.ShouldBeTrue();
        return booking;
    }

    /// <summary>Sign-in and time-out done, knife to skin.</summary>
    public static SurgeryBooking InProgress()
    {
        var booking = ReadyForSignIn();
        booking.ConfirmSafety(SafetyPhase.SignIn, AllDone(SurgicalSafetyChecklist.Phases[SafetyPhase.SignIn]), Staff(Now.AddMinutes(60))).IsSuccess.ShouldBeTrue();
        booking.ConfirmSafety(SafetyPhase.TimeOut, AllDone(SurgicalSafetyChecklist.Phases[SafetyPhase.TimeOut]), Staff(Now.AddMinutes(75))).IsSuccess.ShouldBeTrue();
        booking.Start(Now.AddMinutes(80)).IsSuccess.ShouldBeTrue();
        return booking;
    }
}
