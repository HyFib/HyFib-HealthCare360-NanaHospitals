using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>
/// Who signs off another doctor's work (maker-checker, slice 15), as pure rules: the discharge summary by the stay's care
/// team as themselves; a signed encounter amended at once only by its author; another doctor's critical result acknowledged
/// only by a covering doctor.
/// </summary>
public sealed class ClinicalSignOffTests
{
    private static readonly Guid Consultant = Guid.CreateVersion7();
    private static readonly Guid CrossConsultant = Guid.CreateVersion7();
    private static readonly Guid Stranger = Guid.CreateVersion7();
    private static readonly Guid Patient = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 6, 0, 0, TimeSpan.Zero);

    private static StayCareTeam Team => new(Guid.CreateVersion7(), Patient, Consultant, new HashSet<Guid> { CrossConsultant });

    [Fact]
    public void A_discharge_summary_is_signed_by_the_consultant_or_a_doctor_on_the_care_team_never_by_anyone_else_or_no_doctor()
    {
        ClinicalSignOff.MaySignDischargeSummary(Consultant, Team).ShouldBeTrue();
        ClinicalSignOff.MaySignDischargeSummary(CrossConsultant, Team).ShouldBeTrue();
        ClinicalSignOff.MaySignDischargeSummary(Stranger, Team).ShouldBeFalse();
        ClinicalSignOff.MaySignDischargeSummary(null, Team).ShouldBeFalse("an account not linked to a doctor never signs in the consultant's name");
        ClinicalSignOff.MaySignDischargeSummary(Consultant, null).ShouldBeFalse("a stay HC360 does not know has no care team");
    }

    [Fact]
    public void Only_the_author_amends_a_signed_encounter_without_approval()
    {
        var authorUser = Guid.CreateVersion7();
        var encounter = new Encounter { PractitionerId = Consultant };
        encounter.Update(new EncounterContent("Fever for three days", null, null, null, null, null, null, null, null, null, null));
        encounter.Sign(authorUser, Consultant, Now).IsSuccess.ShouldBeTrue();

        ClinicalSignOff.AmendNeedsApproval(encounter, authorUser, null).ShouldBeFalse("the account that signed it");
        ClinicalSignOff.AmendNeedsApproval(encounter, Guid.CreateVersion7(), Consultant).ShouldBeFalse("the doctor it was signed as");
        ClinicalSignOff.AmendNeedsApproval(encounter, Guid.CreateVersion7(), CrossConsultant).ShouldBeTrue();
        ClinicalSignOff.AmendNeedsApproval(encounter, Guid.CreateVersion7(), null).ShouldBeTrue();
    }

    [Fact]
    public void Another_doctors_critical_result_is_acknowledged_only_by_the_care_team_or_on_a_handover()
    {
        ClinicalSignOff.MayAcknowledgeCritical(Consultant, Consultant, Patient, null, [], Now).ShouldBeTrue("their own");
        ClinicalSignOff.MayAcknowledgeCritical(null, Stranger, Patient, null, [], Now).ShouldBeTrue("no doctor ordered it");
        ClinicalSignOff.MayAcknowledgeCritical(Consultant, null, Patient, Team, [], Now).ShouldBeFalse("not a doctor");
        ClinicalSignOff.MayAcknowledgeCritical(Consultant, CrossConsultant, Patient, Team, [], Now).ShouldBeTrue("the same stay's care team");
        ClinicalSignOff.MayAcknowledgeCritical(Consultant, Stranger, Patient, Team, [], Now).ShouldBeFalse();
        ClinicalSignOff.MayAcknowledgeCritical(Stranger, CrossConsultant, Patient, Team, [], Now).ShouldBeFalse("the ordering doctor is not on the team");
        ClinicalSignOff.MayAcknowledgeCritical(Consultant, CrossConsultant, Guid.CreateVersion7(), Team, [], Now).ShouldBeFalse("another patient's stay");
    }

    [Fact]
    public void A_handover_covers_its_patient_or_all_until_it_ends()
    {
        var all = Handover(null, Now.AddHours(8));
        var one = Handover(Patient, Now.AddHours(8));
        var other = Handover(Guid.CreateVersion7(), Now.AddHours(8));

        ClinicalSignOff.MayAcknowledgeCritical(Consultant, Stranger, Patient, null, [all], Now.AddHours(1)).ShouldBeTrue();
        ClinicalSignOff.MayAcknowledgeCritical(Consultant, Stranger, Patient, null, [one], Now.AddHours(1)).ShouldBeTrue();
        ClinicalSignOff.MayAcknowledgeCritical(Consultant, Stranger, Patient, null, [other], Now.AddHours(1)).ShouldBeFalse("another patient's handover");
        ClinicalSignOff.MayAcknowledgeCritical(Consultant, Stranger, Patient, null, [all], Now.AddHours(9)).ShouldBeFalse("over");
        all.End(Now.AddHours(2)).IsSuccess.ShouldBeTrue();
        ClinicalSignOff.MayAcknowledgeCritical(Consultant, Stranger, Patient, null, [all], Now.AddHours(3)).ShouldBeFalse("ended early");
    }

    [Fact]
    public void A_handover_goes_to_another_doctor_for_up_to_a_week()
    {
        DoctorHandover.Record(Guid.Empty, Consultant, Consultant, null, Now.AddHours(1), null, Now).Error!.Fields!.Keys.ShouldContain("toPractitionerId");
        DoctorHandover.Record(Guid.Empty, Consultant, Stranger, null, Now.AddMinutes(-1), null, Now).Error!.Fields!.Keys.ShouldContain("until");
        DoctorHandover.Record(Guid.Empty, Consultant, Stranger, null, Now.AddDays(8), null, Now).Error!.Fields!.Keys.ShouldContain("until");
        DoctorHandover.Record(Guid.Empty, Consultant, Stranger, null, Now.AddDays(7), " Night cover ", Now).Value.Note.ShouldBe("Night cover");
    }

    private static DoctorHandover Handover(Guid? patientId, DateTimeOffset until) =>
        DoctorHandover.Record(Guid.Empty, Consultant, Stranger, patientId, until, null, Now).Value;
}
