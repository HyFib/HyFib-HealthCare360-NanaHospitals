namespace HyFib.HealthCare360.Modules.Pharmacy.Tests;

/// <summary>
/// A line the prescriber signed over a blocking safety alert waits in the queue for a pharmacist's acknowledgement, from
/// their own sign-in and never the prescriber's: the alert and the reason travel with the signed prescription.
/// </summary>
public sealed class SafetyOverrideTests
{
    private static readonly DateTimeOffset Now = QueueEntryTests.Now;
    private static readonly Guid Prescriber = Guid.CreateVersion7();
    private static readonly Guid Pharmacist = Guid.CreateVersion7();

    private static PharmacyQueueEntry Overridden()
    {
        var signed = QueueEntryTests.Prescription();
        var lines = signed.Lines.ToList();
        lines[0] = lines[0] with { SafetyOverride = "ALLERGY: Metformin contains metformin; the patient is allergic to metformin." };
        return PharmacyQueueEntry.FromPrescription(
            signed with { Lines = lines, SafetyOverrideReason = "Tolerated it for years; the allergy entry is a mistake", SignedByUserId = Prescriber }, Now);
    }

    [Fact]
    public void The_alert_and_the_prescribers_reason_come_with_the_signed_prescription()
    {
        var entry = Overridden();
        entry.SafetyOverrideReason.ShouldBe("Tolerated it for years; the allergy entry is a mistake");
        entry.Lines[0].NeedsOverrideAcknowledgement.ShouldBeTrue();
        entry.Lines[1].NeedsOverrideAcknowledgement.ShouldBeFalse("only the line the alert was about waits");
        entry.HasUnacknowledgedOverride.ShouldBeTrue();
    }

    [Fact]
    public void A_line_alert_without_an_override_reason_does_not_hold_the_line()
    {
        var signed = QueueEntryTests.Prescription();
        var lines = signed.Lines.ToList();
        lines[0] = lines[0] with { SafetyOverride = "ALLERGY: …" };
        PharmacyQueueEntry.FromPrescription(signed with { Lines = lines }, Now).HasUnacknowledgedOverride.ShouldBeFalse();
    }

    [Fact]
    public void A_pharmacist_acknowledges_it_once_and_the_line_is_then_dispensed()
    {
        var entry = Overridden();
        var line = entry.Lines[0];

        entry.AcknowledgeOverride(line.Id, Pharmacist, "Asha Pharmacist", "Spoke to the doctor", Now).IsSuccess.ShouldBeTrue();
        (line.NeedsOverrideAcknowledgement, line.OverrideAcknowledgedByName, line.OverrideAcknowledgementNote).ShouldBe((false, "Asha Pharmacist", "Spoke to the doctor"));
        entry.AcknowledgeOverride(line.Id, Pharmacist, "Asha Pharmacist", null, Now).Error!.Code.ShouldBe("pharmacy.override_acknowledged");
        entry.HasUnacknowledgedOverride.ShouldBeFalse();
    }

    [Fact]
    public void The_prescriber_does_not_acknowledge_their_own_override()
    {
        var entry = Overridden();
        var refused = entry.AcknowledgeOverride(entry.Lines[0].Id, Prescriber, "Dr Rao", null, Now).Error.ShouldNotBeNull();
        (refused.Code, refused.Type).ShouldBe(("pharmacy.override_own_prescription", ErrorType.Forbidden));
        entry.Lines[0].NeedsOverrideAcknowledgement.ShouldBeTrue();
    }

    [Fact]
    public void A_line_with_no_override_has_nothing_to_acknowledge()
    {
        var entry = Overridden();
        entry.AcknowledgeOverride(entry.Lines[1].Id, Pharmacist, "Asha Pharmacist", null, Now).Error!.Code.ShouldBe("pharmacy.no_override");
    }

    [Fact]
    public void A_refill_is_acknowledged_again_by_whoever_dispenses_it()
    {
        var entry = Overridden();
        entry.AcknowledgeOverride(entry.Lines[0].Id, Pharmacist, "Asha Pharmacist", null, Now);
        foreach (var line in entry.Lines)
        {
            entry.RecordDispensed(line.Id, line.RequestedQuantity, Now);
        }

        entry.AuthorizeRefills(1, DateOnly.FromDateTime(Now.Date).AddDays(60), "Dr Rao, phone", DateOnly.FromDateTime(Now.Date)).IsSuccess.ShouldBeTrue();
        var refill = entry.CreateRefill(DateOnly.FromDateTime(Now.Date), Now).Value;
        refill.Lines[0].NeedsOverrideAcknowledgement.ShouldBeTrue();
        (refill.SafetyOverrideReason, refill.SignedByUserId).ShouldBe((entry.SafetyOverrideReason, Prescriber));
    }
}
