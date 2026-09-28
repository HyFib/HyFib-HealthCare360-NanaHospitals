using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

public sealed class PrescriptionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 6, 0, 0, TimeSpan.Zero);

    private static PrescriptionItem Item(string name = "Dolo 650", decimal quantity = 15, bool controlled = false,
        DoseFrequency frequency = DoseFrequency.TDS, string? custom = null, int days = 5) => new()
    {
        DrugName = name,
        GenericName = "Paracetamol",
        Strength = "650 mg",
        Dose = "1 tab",
        Frequency = frequency,
        CustomFrequency = custom,
        DurationDays = days,
        Quantity = quantity,
        IsControlledDrug = controlled,
        ControlledSchedule = controlled ? "H1" : null,
    };

    private static Prescription Draft(params PrescriptionItem[] items)
    {
        var prescription = new Prescription
        {
            FacilityId = Guid.CreateVersion7(),
            PrescriptionNumber = "NHRX260000042",
            PatientId = Guid.CreateVersion7(),
            PractitionerId = Guid.CreateVersion7(),
            RefillsAllowed = 2,
        };
        prescription.ReplaceItems(items);
        return prescription;
    }

    private static PrescriptionSignature Signature(bool confirmControlled = false) =>
        new(Guid.CreateVersion7(), "token-abcdefghijklmnop", null, "[]", confirmControlled);

    [Fact]
    public void Items_are_numbered_in_order()
    {
        var prescription = Draft(Item("A"), Item("B"));

        prescription.Items.Select(i => i.Sequence).ShouldBe([1, 2]);
        prescription.Items.ShouldAllBe(i => i.PrescriptionId == prescription.Id);
    }

    [Fact]
    public void Signing_sends_the_prescription_to_the_pharmacy()
    {
        var prescription = Draft(Item(), Item("Pan 40", frequency: DoseFrequency.Custom, custom: "1-0-0"));

        prescription.Sign(Signature(), Now).IsSuccess.ShouldBeTrue();

        prescription.Status.ShouldBe(PrescriptionStatus.Signed);
        prescription.SignedAt.ShouldBe(Now);
        prescription.VerificationToken.ShouldBe("token-abcdefghijklmnop");
        prescription.RefillsRemaining.ShouldBe(2);
        var signed = prescription.DomainEvents.OfType<PrescriptionSigned>().ShouldHaveSingleItem();
        signed.PrescriptionNumber.ShouldBe("NHRX260000042");
        signed.Lines.Count.ShouldBe(2);
        signed.Lines[0].Frequency.ShouldBe("TDS");
        signed.Lines[1].Frequency.ShouldBe("1-0-0");
        signed.Lines[0].Quantity.ShouldBe(15m);
    }

    [Fact]
    public void Empty_prescription_or_missing_quantity_cannot_be_signed()
    {
        Draft().Sign(Signature(), Now).Error!.Code.ShouldBe("prescription.empty");

        var noQuantity = Draft(Item(quantity: 0));
        var error = noQuantity.Sign(Signature(), Now).Error!;
        error.Code.ShouldBe("prescription.quantity_missing");
        error.Message.ShouldContain("Dolo 650");
    }

    [Fact]
    public void Controlled_drugs_need_explicit_confirmation()
    {
        var prescription = Draft(Item("Alprax 0.25", quantity: 10, controlled: true));

        prescription.Sign(Signature(confirmControlled: false), Now).Error!.Code.ShouldBe("prescription.controlled_confirmation_required");
        prescription.Status.ShouldBe(PrescriptionStatus.Draft);

        prescription.Sign(Signature(confirmControlled: true), Now).IsSuccess.ShouldBeTrue();
        prescription.ControlledDrugsConfirmed.ShouldBeTrue();
    }

    [Fact]
    public void Signed_prescriptions_are_immutable()
    {
        var prescription = Draft(Item());
        prescription.Sign(Signature(), Now);

        prescription.ReplaceItems([Item("Other")]).Error!.Code.ShouldBe("prescription.not_draft");
        prescription.Sign(Signature(), Now).Error!.Code.ShouldBe("prescription.not_draft");
    }

    [Fact]
    public void Cancelling_a_signed_prescription_withdraws_it_from_the_pharmacy()
    {
        var prescription = Draft(Item());
        prescription.Sign(Signature(), Now);

        prescription.Cancel("  Wrong patient  ", Now).IsSuccess.ShouldBeTrue();

        prescription.Status.ShouldBe(PrescriptionStatus.Cancelled);
        prescription.CancelReason.ShouldBe("Wrong patient");
        prescription.RefillsRemaining.ShouldBe(0);
        prescription.DomainEvents.OfType<PrescriptionCancelled>().ShouldHaveSingleItem().Reason.ShouldBe("Wrong patient");
        prescription.Cancel("Again", Now).Error!.Code.ShouldBe("prescription.cancelled");
    }

    [Fact]
    public void Cancelling_a_draft_raises_no_pharmacy_event()
    {
        var prescription = Draft(Item());

        prescription.Cancel("Not needed", Now).IsSuccess.ShouldBeTrue();
        prescription.DomainEvents.OfType<PrescriptionCancelled>().ShouldBeEmpty();
    }

    [Fact]
    public void Refills_are_consumed_until_none_remain()
    {
        var prescription = Draft(Item());
        prescription.UseRefill().Error!.Code.ShouldBe("prescription.not_signed");
        prescription.Sign(Signature(), Now);

        prescription.UseRefill().IsSuccess.ShouldBeTrue();
        prescription.UseRefill().IsSuccess.ShouldBeTrue();
        prescription.RefillsRemaining.ShouldBe(0);
        prescription.UseRefill().Error!.Code.ShouldBe("prescription.no_refills");
    }

    [Fact]
    public void Course_end_counts_the_first_day()
    {
        var prescription = Draft(Item(days: 5), Item("Long course", days: 30));

        prescription.CourseEndsOn(new DateOnly(2026, 9, 26)).ShouldBe(new DateOnly(2026, 10, 25));
    }

    [Theory]
    [InlineData(DoseFrequency.BD, "Twice a day", "दिन में दो बार")]
    [InlineData(DoseFrequency.HS, "At bedtime", "सोते समय")]
    [InlineData(DoseFrequency.Weekly, "Once a week", "सप्ताह में एक बार")]
    public void Directions_are_bilingual(DoseFrequency frequency, string english, string hindi) =>
        FrequencyLabels.For(frequency, null).ShouldBe((english, hindi));

    [Fact]
    public void Custom_directions_are_printed_as_written() =>
        FrequencyLabels.For(DoseFrequency.Custom, "1-0-1").ShouldBe(("1-0-1", "1-0-1"));
}
