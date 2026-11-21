using HyFib.HealthCare360.Modules.Analytics.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.Services;

/// <summary>
/// Whether the patient journey says an operation goes ahead, or went ahead, on consent waived in an emergency
/// (<see cref="JourneyQueries.ConsentWaived"/>), from the booking as the journeys read it: once the incision is made, when it
/// relied on no consent of a kind it needed; before it, only while the waiver stands and a consent needed is not recorded.
/// </summary>
public sealed class SurgeryJourneyConsentTests
{
    /// <summary>25 Sep 2026, 10:00 IST.</summary>
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 4, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset BookedAt = Now.AddDays(-1);

    private static readonly IReadOnlyCollection<ConsentOnRecord> NoConsents = [];

    private static ConsentOnRecord Consent(SurgicalConsentKind kind, bool granted = true, DateTimeOffset? revokedAt = null) =>
        new(Guid.CreateVersion7(), kind, granted, Now.AddHours(-2), revokedAt);

    /// <summary>An emergency laparotomy under general anaesthesia, booked yesterday, in pre-op: no consent recorded yet.</summary>
    private static SurgeryBooking Emergency(AnaesthesiaType anaesthesia = AnaesthesiaType.GA) => new()
    {
        CreatedAt = BookedAt,
        PatientId = Guid.CreateVersion7(),
        SurgeonId = Guid.CreateVersion7(),
        AnaesthetistId = Guid.CreateVersion7(),
        AnaesthesiaType = anaesthesia,
        ProcedureName = "Laparotomy for a ruptured spleen",
        Priority = SurgeryPriority.Emergency,
        Status = SurgeryStatus.InPreOp,
    };

    /// <summary>The operating surgeon waived consent an hour ago, a second doctor agreeing, as the booking keeps it.</summary>
    private static SurgeryBooking Waived(SurgeryBooking booking)
    {
        booking.ConsentWaivedAt = Now.AddHours(-1);
        (booking.ConsentWaivedByUserId, booking.ConsentWaivedByName) = (Guid.CreateVersion7(), "Dr. Arjun Rao");
        (booking.ConsentWaiverConcurredByUserId, booking.ConsentWaiverConcurredByName) = (Guid.CreateVersion7(), "Dr. Kavita Shah");
        booking.ConsentWaiverReason = "Ruptured spleen, in shock; no relative reachable";
        return booking;
    }

    /// <summary>Knife to skin, relying on the consents named (none for a kind that was waived).</summary>
    private static SurgeryBooking Started(SurgeryBooking booking, Guid? procedureConsentId, Guid? anaesthesiaConsentId)
    {
        (booking.Status, booking.StartedAt) = (SurgeryStatus.InProgress, Now);
        (booking.ProcedureConsentId, booking.AnaesthesiaConsentId) = (procedureConsentId, anaesthesiaConsentId);
        return booking;
    }

    [Fact]
    public void Before_the_incision_an_operation_goes_ahead_on_the_waiver_only_while_it_stands()
    {
        var booking = Waived(Emergency());

        JourneyQueries.ConsentWaived(booking, NoConsents).ShouldBeTrue();
        JourneyQueries.ConsentWaived(booking, [Consent(SurgicalConsentKind.Procedure)]).ShouldBeTrue("the consent to anaesthesia is still waived");
        JourneyQueries.ConsentWaived(booking, [Consent(SurgicalConsentKind.Procedure), Consent(SurgicalConsentKind.Anaesthesia)])
            .ShouldBeFalse("the consents recorded since the waiver: it goes ahead on them");
        JourneyQueries.ConsentWaived(booking, [Consent(SurgicalConsentKind.Procedure, granted: false)]).ShouldBeFalse("the patient refused since");
        JourneyQueries.ConsentWaived(booking, [Consent(SurgicalConsentKind.Anaesthesia, revokedAt: Now.AddMinutes(-30))])
            .ShouldBeFalse("the patient withdrew since");

        booking.Priority = SurgeryPriority.Urgent;
        JourneyQueries.ConsentWaived(booking, NoConsents).ShouldBeFalse("no longer an emergency, the waiver does not stand");
        JourneyQueries.ConsentWaived(Emergency(), NoConsents).ShouldBeFalse("nothing was waived");
    }

    [Fact]
    public void An_operation_whose_consent_to_anaesthesia_alone_was_waived_stays_waived_after_the_incision()
    {
        var procedure = Guid.CreateVersion7();

        JourneyQueries.ConsentWaived(Started(Waived(Emergency()), procedure, null), NoConsents)
            .ShouldBeTrue("the incision relied on the consent to the procedure, but on none to anaesthesia");
        JourneyQueries.ConsentWaived(Started(Waived(Emergency()), null, null), NoConsents).ShouldBeTrue();

        // Consents recorded or withdrawn after the incision do not change what it relied on.
        JourneyQueries.ConsentWaived(Started(Waived(Emergency()), procedure, null), [Consent(SurgicalConsentKind.Anaesthesia)]).ShouldBeTrue();
        JourneyQueries.ConsentWaived(Started(Waived(Emergency()), procedure, Guid.CreateVersion7()), [Consent(SurgicalConsentKind.Procedure, granted: false)])
            .ShouldBeFalse();
    }

    [Fact]
    public void An_incision_that_relied_on_every_consent_it_needed_did_not_go_ahead_on_the_waiver()
    {
        var procedure = Guid.CreateVersion7();

        // A waiver on the booking, but the consents were recorded before the incision, which relied on them.
        JourneyQueries.ConsentWaived(Started(Waived(Emergency()), procedure, Guid.CreateVersion7()), NoConsents).ShouldBeFalse();

        // Local anaesthesia, given by the surgeon, needs no consent to anaesthesia.
        JourneyQueries.ConsentWaived(Started(Waived(Emergency(AnaesthesiaType.Local)), procedure, null), NoConsents).ShouldBeFalse();

        // Unless the Sign-in was done under an anaesthetic given by the anaesthetist: then it stayed needed.
        var switched = Waived(Emergency(AnaesthesiaType.Local));
        switched.AnaesthesiaConsentRequiredAtSignIn = true;
        JourneyQueries.ConsentWaived(Started(switched, procedure, null), NoConsents).ShouldBeTrue();

        // An operation started before the theatre read consents relied on none, and on no waiver either.
        JourneyQueries.ConsentWaived(Started(Emergency(), null, null), NoConsents).ShouldBeFalse();
    }
}
