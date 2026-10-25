using HyFib.HealthCare360.Modules.Clinical.Domain.Stewardship;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>
/// The AWaRe classification matched on generic and brand names, the hospital's restricted list, and an approval's rules:
/// first doses only while it waits, a decision by someone other than the prescriber, and the review at 72 hours or when
/// the culture comes back, which asks for a reason to continue an antibiotic the isolate resists.
/// </summary>
public sealed class StewardshipTests
{
    private static readonly DateTimeOffset Prescribed = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Prescriber = Guid.NewGuid();
    private static readonly Guid Steward = Guid.NewGuid();

    [Theory]
    [InlineData("Meropenem", "Meropenem", AwareGroup.Watch, true)]
    [InlineData("Ceftazidime + Avibactam", "Ceftazidime–avibactam", AwareGroup.Reserve, true)]
    [InlineData("CEFTAZIDIME", "Ceftazidime", AwareGroup.Watch, false)]
    [InlineData("Piperacillin and Tazobactam", "Piperacillin–tazobactam", AwareGroup.Watch, true)]
    [InlineData("Levofloxacin", "Levofloxacin", AwareGroup.Watch, false)]
    [InlineData("Ofloxacin", "Ofloxacin", AwareGroup.Watch, false)]
    [InlineData("Colistimethate (Colistin)", "Colistin", AwareGroup.Reserve, true)]
    [InlineData("Amoxicillin + Clavulanic acid", "Amoxicillin–clavulanate", AwareGroup.Access, false)]
    public void Generic_names_find_their_aware_group(string name, string antimicrobial, AwareGroup group, bool restricted)
    {
        var found = Aware.Find(name).ShouldNotBeNull();
        (found.Name, found.Group, Aware.IsRestricted(found, [])).ShouldBe((antimicrobial, group, restricted));
    }

    [Fact]
    public void The_hospital_adds_to_the_restricted_list_and_other_medicines_are_no_antibiotic()
    {
        var ceftriaxone = Aware.Find("Ceftriaxone").ShouldNotBeNull();
        Aware.IsRestricted(ceftriaxone, []).ShouldBeFalse();
        Aware.IsRestricted(ceftriaxone, ["ceftriaxone"]).ShouldBeTrue();
        Aware.Find("Paracetamol").ShouldBeNull();
        Aware.Find(null).ShouldBeNull();
    }

    [Fact]
    public void The_antibiogram_is_read_for_the_antibiotic_given()
    {
        var meropenem = Aware.Find("Meropenem")!;
        Aware.Susceptibility(meropenem, [("Ceftriaxone", "Resistant"), ("Meropenem (MIC)", "Sensitive")]).ShouldBe("Sensitive");
        Aware.Susceptibility(meropenem, [("Amikacin", "Sensitive")]).ShouldBeNull();
    }

    [Fact]
    public void First_doses_go_while_it_waits_and_none_once_rejected()
    {
        var approval = Meropenem();
        approval.IssueRefusal(Prescribed.AddHours(6)).ShouldBeNull("The first doses do not wait for the team.");
        approval.IssueRefusal(Prescribed.AddHours(25)).ShouldNotBeNull();
        approval.ApprovalOverdue(Prescribed.AddHours(25)).ShouldBeTrue();

        approval.Decide(false, null, null, Steward, "Dr. Lakshmi", Prescribed.AddHours(2)).Error!.Fields!.Keys.ShouldContain("notes");
        approval.Decide(false, null, "ESBL not suspected; ceftriaxone instead", Steward, "Dr. Lakshmi", Prescribed.AddHours(2)).IsSuccess.ShouldBeTrue();
        approval.IssueRefusal(Prescribed.AddHours(3)).ShouldNotBeNull().ShouldContain("ceftriaxone instead");
    }

    [Fact]
    public void The_prescriber_cannot_approve_their_own_antibiotic()
    {
        var approval = Meropenem();
        approval.Decide(true, 5, null, Prescriber, "Dr. Rao", Prescribed.AddHours(1)).Error!.Code.ShouldBe("ams.own_prescription");
        approval.Decide(true, 60, null, Steward, "Dr. Lakshmi", Prescribed.AddHours(1)).Error!.Fields!.Keys.ShouldContain("days");
        approval.Decide(true, 5, null, Steward, "Dr. Lakshmi", Prescribed.AddHours(1)).IsSuccess.ShouldBeTrue();
        (approval.Status, approval.ApprovedDays).ShouldBe((ApprovalStatus.Approved, 5));
        approval.IssueRefusal(Prescribed.AddDays(2)).ShouldBeNull();
        approval.Decide(false, null, "Again", Steward, "Dr. Lakshmi", Prescribed.AddHours(2)).Error!.Code.ShouldBe("ams.decided");
    }

    [Fact]
    public void The_course_is_reviewed_at_72_hours_or_when_the_culture_returns()
    {
        var approval = Meropenem();
        approval.Decide(true, 7, null, Steward, "Dr. Lakshmi", Prescribed.AddHours(1));
        approval.ReviewDue(Prescribed.AddHours(48)).ShouldBeFalse();
        approval.Review(StewardshipDecision.Continue, null, "Dr. Lakshmi", Prescribed.AddHours(48)).Error!.Code.ShouldBe("ams.review_not_due");
        approval.ReviewDue(Prescribed.AddHours(73)).ShouldBeTrue("The 72-hour time-out.");

        approval.CultureReleased(Guid.NewGuid(), "Klebsiella pneumoniae", "Resistant", Prescribed.AddHours(40));
        approval.ReviewDue(Prescribed.AddHours(41)).ShouldBeTrue("The culture brings the review forward.");
        approval.Review(StewardshipDecision.Continue, null, "Dr. Lakshmi", Prescribed.AddHours(41)).Error!.Fields!.Keys.ShouldContain("notes");
        approval.Review(StewardshipDecision.Escalate, " Colistin per antibiogram ", "Dr. Lakshmi", Prescribed.AddHours(41)).IsSuccess.ShouldBeTrue();
        (approval.ReviewDecision, approval.ReviewNotes).ShouldBe((StewardshipDecision.Escalate, "Colistin per antibiogram"));
        approval.ReviewDue(Prescribed.AddHours(80)).ShouldBeFalse();
    }

    [Fact]
    public void The_justification_is_the_prescribers_before_the_decision()
    {
        var approval = Meropenem();
        approval.Justify(InfectionSite.UrinaryTract, " ", true, true).Error!.Fields!.Keys.ShouldContain("indication");
        approval.Justify(InfectionSite.UrinaryTract, "Urosepsis, prior ESBL", true, true).IsSuccess.ShouldBeTrue();
        approval.Decide(true, 5, null, Steward, "Dr. Lakshmi", Prescribed.AddHours(1));
        approval.Justify(InfectionSite.Bloodstream, "Changed", true, true).Error!.Code.ShouldBe("ams.decided");
    }

    private static AntimicrobialApproval Meropenem() => new()
    {
        DrugName = "Meropenem 1 g Injection",
        Antimicrobial = "Meropenem",
        Group = AwareGroup.Watch,
        PrescribedByUserId = Prescriber,
        PrescribedByName = "Dr. Rao",
        PrescribedAt = Prescribed,
    };
}
