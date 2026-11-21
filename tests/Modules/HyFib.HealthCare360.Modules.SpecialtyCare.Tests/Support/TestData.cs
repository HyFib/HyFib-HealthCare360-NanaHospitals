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

    /// <summary>When <see cref="NewBooking"/> books the operation: the day before. A consent counts from the booking.</summary>
    public static readonly DateTimeOffset BookedAt = Now.AddDays(-1);

    /// <summary>The patient's consents to the procedure and to anaesthesia, recorded at the front office since the booking.</summary>
    public static IReadOnlyCollection<ConsentOnRecord> Consents { get; } =
        [Consent(SurgicalConsentKind.Procedure), Consent(SurgicalConsentKind.Anaesthesia)];

    /// <summary>Nothing recorded under the patient's consents.</summary>
    public static IReadOnlyCollection<ConsentOnRecord> NoConsents { get; } = [];

    /// <summary>A consent as the front office records it: given (or refused), two hours ago unless said, perhaps withdrawn.</summary>
    public static ConsentOnRecord Consent(SurgicalConsentKind kind, DateTimeOffset? capturedAt = null, bool granted = true, DateTimeOffset? revokedAt = null) =>
        new(Guid.CreateVersion7(), kind, granted, capturedAt ?? Now.AddHours(-2), revokedAt);

    public static SurgeryBooking NewBooking(AnaesthesiaType anaesthesia = AnaesthesiaType.GA) => new()
    {
        CreatedAt = BookedAt,
        PatientId = Guid.CreateVersion7(),
        SurgeonId = Guid.CreateVersion7(),
        AnaesthetistId = anaesthesia is AnaesthesiaType.Local or AnaesthesiaType.Topical ? null : Guid.CreateVersion7(),
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
        booking.RecordPreOp(AllDone(PreOpChecklist.Items), Staff(), Consents).IsSuccess.ShouldBeTrue();
        return booking;
    }

    /// <summary>A count of <paramref name="category"/>, counted by one nurse and witnessed by another.</summary>
    public static SurgicalCount Count(CountCategory category, CountStage stage, int number, DateTimeOffset? at = null) =>
        new(Guid.CreateVersion7(), category, stage, number, Guid.CreateVersion7(), "Sr. Anita Joseph", Guid.CreateVersion7(), "Sr. Leela Thomas", at ?? Now, null);

    /// <summary>Swabs counted at the start and at the end of an operation in progress, and tallying.</summary>
    public static SurgeryBooking CountsTallied(SurgeryBooking booking)
    {
        booking.RecordCount(Count(CountCategory.Swabs, CountStage.Initial, 10)).IsSuccess.ShouldBeTrue();
        booking.RecordCount(Count(CountCategory.Swabs, CountStage.Final, 10)).Value.ShouldBeNull();
        return booking;
    }

    /// <summary>Sign-in and time-out done, knife to skin.</summary>
    public static SurgeryBooking InProgress()
    {
        var booking = ReadyForSignIn();
        booking.ConfirmSafety(SafetyPhase.SignIn, AllDone(SurgicalSafetyChecklist.Phases[SafetyPhase.SignIn]), Staff(Now.AddMinutes(60)), Consents).IsSuccess.ShouldBeTrue();
        booking.ConfirmSafety(SafetyPhase.TimeOut, AllDone(SurgicalSafetyChecklist.Phases[SafetyPhase.TimeOut]), Staff(Now.AddMinutes(75)), Consents).IsSuccess.ShouldBeTrue();
        booking.Start(Now.AddMinutes(80), Consents).IsSuccess.ShouldBeTrue();
        return booking;
    }
}
