using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

public sealed class EncounterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 5, 30, 0, TimeSpan.Zero);

    private static Encounter NewEncounter() => new()
    {
        FacilityId = Guid.CreateVersion7(),
        PatientId = Guid.CreateVersion7(),
        PractitionerId = Guid.CreateVersion7(),
        Type = EncounterType.OutPatient,
        StartedAt = Now,
    };

    private static EncounterContent Content(string? complaint = null, string? assessment = null, Guid? referralPractitioner = null,
        string? referralReason = null) =>
        new(complaint, null, null, assessment, null, null, null, null, referralPractitioner, null, referralReason);

    [Fact]
    public void New_encounter_is_editable()
    {
        var encounter = NewEncounter();

        encounter.Status.ShouldBe(EncounterStatus.InProgress);
        encounter.Update(Content("  Fever for 3 days  ")).IsSuccess.ShouldBeTrue();
        encounter.ChiefComplaint.ShouldBe("Fever for 3 days");
    }

    [Fact]
    public void Empty_note_cannot_be_signed()
    {
        var encounter = NewEncounter();

        encounter.Sign(Guid.CreateVersion7(), null, Now).Error!.Code.ShouldBe("encounter.empty");
        encounter.Status.ShouldBe(EncounterStatus.InProgress);
    }

    [Fact]
    public void Signing_locks_the_note_and_raises_an_event()
    {
        var encounter = NewEncounter();
        var userId = Guid.CreateVersion7();
        encounter.Update(Content("Fever", "Viral fever"));

        encounter.Sign(userId, null, Now).IsSuccess.ShouldBeTrue();

        encounter.Status.ShouldBe(EncounterStatus.Signed);
        encounter.SignedAt.ShouldBe(Now);
        encounter.SignedByUserId.ShouldBe(userId);
        encounter.SignedByPractitionerId.ShouldBe(encounter.PractitionerId);
        encounter.DomainEvents.OfType<EncounterSigned>().ShouldHaveSingleItem().EncounterId.ShouldBe(encounter.Id);
        encounter.Update(Content("Changed")).Error!.Code.ShouldBe("encounter.signed");
        encounter.Sign(userId, null, Now).Error!.Code.ShouldBe("encounter.already_signed");
    }

    [Fact]
    public void Unsigned_note_is_edited_not_amended()
    {
        var encounter = NewEncounter();

        encounter.Amend(Content("Fever"), "Correcting the complaint", null, Now).Error!.Code.ShouldBe("encounter.not_signed");
    }

    [Fact]
    public void Amendment_needs_a_reason()
    {
        var encounter = NewEncounter();
        encounter.Update(Content("Fever"));
        encounter.Sign(null, null, Now);

        var error = encounter.Amend(Content("Fever"), " ", null, Now).Error!;
        error.Type.ShouldBe(ErrorType.Validation);
        encounter.Amendments.ShouldBeEmpty();
    }

    [Fact]
    public void Amendment_keeps_the_previous_text_and_is_attested()
    {
        var encounter = NewEncounter();
        encounter.Update(Content("Fever", "Viral fever"));
        encounter.Sign(null, null, Now);
        var amender = Guid.CreateVersion7();

        encounter.Amend(Content("Fever", "Dengue fever (NS1 positive)"), "NS1 report received", amender, Now.AddHours(4)).IsSuccess.ShouldBeTrue();

        encounter.Status.ShouldBe(EncounterStatus.Amended);
        encounter.Assessment.ShouldBe("Dengue fever (NS1 positive)");
        encounter.AmendmentReason.ShouldBe("NS1 report received");
        var amendment = encounter.Amendments.ShouldHaveSingleItem();
        amendment.AmendedByUserId.ShouldBe(amender);
        JsonDocument.Parse(amendment.PreviousContent).RootElement.GetProperty("assessment").GetString().ShouldBe("Viral fever");
        encounter.DomainEvents.OfType<EncounterAmended>().ShouldHaveSingleItem();

        encounter.Amend(Content("Fever", "Dengue without warning signs"), "Classification refined", amender, Now.AddHours(5)).IsSuccess.ShouldBeTrue();
        encounter.Amendments.Count.ShouldBe(2);
    }

    [Fact]
    public void Referral_reason_is_dropped_when_there_is_no_referral()
    {
        var encounter = NewEncounter();

        encounter.Update(Content("Chest pain", referralReason: "Cardiology opinion"));
        encounter.ReferralReason.ShouldBeNull();

        var cardiologist = Guid.CreateVersion7();
        encounter.Update(Content("Chest pain", referralPractitioner: cardiologist, referralReason: "Cardiology opinion"));
        encounter.ReferralPractitionerId.ShouldBe(cardiologist);
        encounter.ReferralReason.ShouldBe("Cardiology opinion");
    }
}
