using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.BloodBank;

/// <summary>Issue gate, cross-match validity and request lifecycle (BB-06/07).</summary>
public sealed class IssueCompatibilityTests
{
    private static Result<bool> Check(BloodComponentType component, BloodGroup unit, BloodGroup patient, bool crossmatched = false, bool emergency = false) =>
        IssueCompatibility.Check(new IssueContext(component, unit, patient, crossmatched, emergency));

    [Fact]
    public void Red_cells_need_a_valid_compatible_crossmatch() =>
        Check(BloodComponentType.PRBC, BloodGroup.OPositive, BloodGroup.OPositive).Error!.Code.ShouldBe("bloodbank.crossmatch_required");

    [Fact]
    public void Crossmatched_compatible_red_cells_are_issued_normally()
    {
        var result = Check(BloodComponentType.PRBC, BloodGroup.ONegative, BloodGroup.ABPositive, crossmatched: true);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeFalse("not an emergency release");
    }

    [Fact]
    public void Abo_incompatible_unit_is_refused_even_with_a_crossmatch_on_record() =>
        Check(BloodComponentType.PRBC, BloodGroup.APositive, BloodGroup.OPositive, crossmatched: true).Error!.Code.ShouldBe("bloodbank.abo_incompatible");

    [Fact]
    public void Emergency_release_allows_only_uncrossmatched_o_negative_packed_cells()
    {
        var oNegative = Check(BloodComponentType.PRBC, BloodGroup.ONegative, BloodGroup.Unknown, emergency: true);
        oNegative.IsSuccess.ShouldBeTrue();
        oNegative.Value.ShouldBeTrue("flagged as emergency release");

        Check(BloodComponentType.PRBC, BloodGroup.OPositive, BloodGroup.Unknown, emergency: true).IsFailure.ShouldBeTrue();
        Check(BloodComponentType.WholeBlood, BloodGroup.ONegative, BloodGroup.Unknown, emergency: true).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Plasma_needs_a_grouped_patient_and_plasma_compatibility()
    {
        Check(BloodComponentType.FFP, BloodGroup.APositive, BloodGroup.Unknown).Error!.Code.ShouldBe("bloodbank.patient_not_grouped");
        Check(BloodComponentType.FFP, BloodGroup.OPositive, BloodGroup.APositive).Error!.Code.ShouldBe("bloodbank.abo_incompatible");
        Check(BloodComponentType.FFP, BloodGroup.ABNegative, BloodGroup.APositive).IsSuccess.ShouldBeTrue();
        Check(BloodComponentType.PlateletConcentrate, BloodGroup.APositive, BloodGroup.ONegative).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Emergency_release_of_plasma_before_grouping_is_limited_to_group_ab()
    {
        Check(BloodComponentType.FFP, BloodGroup.ABPositive, BloodGroup.Unknown, emergency: true).Value.ShouldBeTrue();
        Check(BloodComponentType.FFP, BloodGroup.APositive, BloodGroup.Unknown, emergency: true).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Compatible_crossmatch_is_refused_when_the_groups_are_incompatible()
    {
        CrossMatch.EnsureConsistent(CrossmatchResult.Compatible, BloodComponentType.PRBC, BloodGroup.BPositive, BloodGroup.APositive)
            .Error!.Code.ShouldBe("bloodbank.abo_incompatible");
        CrossMatch.EnsureConsistent(CrossmatchResult.Incompatible, BloodComponentType.PRBC, BloodGroup.BPositive, BloodGroup.APositive).IsSuccess.ShouldBeTrue();
        CrossMatch.EnsureConsistent(CrossmatchResult.Compatible, BloodComponentType.PRBC, BloodGroup.OPositive, BloodGroup.Unknown)
            .Error!.Code.ShouldBe("bloodbank.patient_not_grouped");
    }

    [Fact]
    public void Crossmatch_is_valid_for_72_hours()
    {
        var crossMatch = new CrossMatch { Result = CrossmatchResult.Compatible, PerformedAt = TestData.Now };

        crossMatch.IsValidCompatibleAt(TestData.Now.AddHours(72)).ShouldBeTrue();
        crossMatch.IsValidCompatibleAt(TestData.Now.AddHours(72).AddMinutes(1)).ShouldBeFalse();
        new CrossMatch { Result = CrossmatchResult.Incompatible, PerformedAt = TestData.Now }.IsValidCompatibleAt(TestData.Now).ShouldBeFalse();
    }

    [Fact]
    public void Second_grouping_that_disagrees_is_refused_as_possible_wrong_blood_in_tube()
    {
        var request = new BloodRequest { UnitsRequested = 2, Status = BloodRequestStatus.Pending };

        request.RecordGrouping(BloodGroup.BPositive, "Tech 1", TestData.Now).IsSuccess.ShouldBeTrue();
        request.Status.ShouldBe(BloodRequestStatus.InProcess);

        request.RecordGrouping(BloodGroup.BPositive, "Tech 2", TestData.Now).IsSuccess.ShouldBeTrue();
        request.RecordGrouping(BloodGroup.OPositive, "Tech 2", TestData.Now).Error!.Code.ShouldBe("bloodbank.grouping_discrepancy");
    }

    [Fact]
    public void Request_status_follows_reservations_and_issues()
    {
        var request = new BloodRequest { UnitsRequested = 2, Status = BloodRequestStatus.Pending };
        request.RecordGrouping(BloodGroup.APositive, "Tech", TestData.Now).IsSuccess.ShouldBeTrue();

        request.OnUnitReserved();
        request.OnUnitReserved();
        request.Status.ShouldBe(BloodRequestStatus.Ready);

        request.OnUnitIssued(wasReserved: true);
        request.Status.ShouldBe(BloodRequestStatus.PartiallyIssued);

        request.OnUnitIssued(wasReserved: true);
        request.Status.ShouldBe(BloodRequestStatus.Issued);
        request.IsOpen.ShouldBeFalse();

        request.OnUnitReturned();
        request.Status.ShouldBe(BloodRequestStatus.PartiallyIssued);
    }

    [Fact]
    public void Emergency_release_can_only_be_authorised_for_emergency_requests()
    {
        new BloodRequest { Urgency = BloodRequestUrgency.Urgent }.AuthoriseEmergencyRelease("Dr. Rao", "Massive haemorrhage", TestData.Now)
            .Error!.Code.ShouldBe("bloodbank.not_emergency");

        var emergency = new BloodRequest { Urgency = BloodRequestUrgency.Emergency };
        emergency.AuthoriseEmergencyRelease("Dr. Rao", "Massive haemorrhage", TestData.Now).IsSuccess.ShouldBeTrue();
        emergency.EmergencyReleaseAuthorised.ShouldBeTrue();
    }

    [Fact]
    public void Request_with_issued_units_cannot_be_cancelled()
    {
        var request = new BloodRequest { UnitsRequested = 2 };
        request.OnUnitIssued(wasReserved: false);

        request.Cancel("No longer needed", TestData.Now).Error!.Code.ShouldBe("bloodbank.request_partly_issued");
    }

    [Fact]
    public void Serious_reaction_needs_classification_and_findings_to_close()
    {
        var reaction = new TransfusionReaction { Severity = ReactionSeverity.Severe, Status = ReactionStatus.Reported };
        reaction.IsSerious.ShouldBeTrue();

        reaction.Close(ReactionType.Unclassified, ReactionSeverity.Severe, "DAT positive", true, "Dr. BB", TestData.Now).IsFailure.ShouldBeTrue();
        reaction.Close(ReactionType.AcuteHaemolytic, ReactionSeverity.Severe, " ", true, "Dr. BB", TestData.Now).IsFailure.ShouldBeTrue();
        reaction.Close(ReactionType.AcuteHaemolytic, ReactionSeverity.Severe, "DAT positive; clerical error at bedside", true, "Dr. BB", TestData.Now)
            .IsSuccess.ShouldBeTrue();

        reaction.Status.ShouldBe(ReactionStatus.Closed);
        reaction.StartInvestigation().IsFailure.ShouldBeTrue();
    }
}
