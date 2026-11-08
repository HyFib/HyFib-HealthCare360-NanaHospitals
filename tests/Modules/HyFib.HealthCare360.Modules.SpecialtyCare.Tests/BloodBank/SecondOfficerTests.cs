using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.BloodBank;

/// <summary>
/// The blood bank's second officer (maker-checker, slice 3): screening results verified by someone other than whoever
/// entered them before units leave quarantine, an issue that does not rest on the issuer's checks alone, and the patient's
/// group compared with their earlier requests.
/// </summary>
public sealed class SecondOfficerTests
{
    private static readonly TtiPanel Clear = new()
    {
        Hiv = TtiResult.NonReactive,
        HbsAg = TtiResult.NonReactive,
        Hcv = TtiResult.NonReactive,
        Syphilis = TtiResult.NonReactive,
        Malaria = TtiResult.NonReactive,
    };

    private static readonly Guid Santosh = Guid.CreateVersion7();
    private static readonly Guid Priya = Guid.CreateVersion7();
    private static readonly Guid DrRao = Guid.CreateVersion7();

    private static StaffStamp Officer(Guid userId, string name) => new(userId, name, TestData.Now);

    private static Donation Entered(TtiPanel? tti = null)
    {
        var donation = new Donation { BagNumber = "NHBB26000412" };
        donation.RecordLabResults(BloodGroup.BPositive, tti ?? Clear, Officer(Santosh, "Mr. Santosh Gaikwad")).IsSuccess.ShouldBeTrue();
        return donation;
    }

    [Fact]
    public void Results_entered_by_one_officer_wait_for_another_to_verify_them()
    {
        var donation = Entered();
        (donation.AwaitingVerification, donation.IsVerified, donation.LabResultsByUserId, donation.LabResultsByName)
            .ShouldBe((true, false, (Guid?)Santosh, "Mr. Santosh Gaikwad"));

        donation.Verify(Officer(Santosh, "Mr. Santosh Gaikwad"), separateVerifier: true).Error!.Code.ShouldBe("bloodbank.verify_own_results");
        donation.IsVerified.ShouldBeFalse();

        donation.Verify(Officer(Priya, "Ms. Priya Nair"), separateVerifier: true).IsSuccess.ShouldBeTrue();
        (donation.IsVerified, donation.AwaitingVerification, donation.VerifiedByUserId, donation.VerifiedByEnterer)
            .ShouldBe((true, false, (Guid?)Priya, false));
        donation.Verify(Officer(Priya, "Ms. Priya Nair"), separateVerifier: true).Error!.Code.ShouldBe("bloodbank.already_verified");
    }

    [Fact]
    public void One_officer_on_duty_verifies_their_own_results_only_where_the_facility_has_switched_the_second_officer_off()
    {
        var donation = Entered();

        donation.Verify(Officer(Santosh, "Mr. Santosh Gaikwad"), separateVerifier: false).IsSuccess.ShouldBeTrue();

        (donation.IsVerified, donation.VerifiedByEnterer).ShouldBe((true, true), "released on one officer's word, for the medical officer's review");
    }

    [Fact]
    public void Only_results_that_clear_the_donation_are_verified()
    {
        Entered(Clear with { Malaria = TtiResult.Pending }).Verify(Officer(Priya, "Ms. Priya Nair"), true).Error!.Code
            .ShouldBe("bloodbank.nothing_to_verify");

        var reactive = Entered(Clear with { Hiv = TtiResult.Reactive });
        reactive.Status.ShouldBe(DonationStatus.Discarded, "a reactive result discards at once, with no second officer");
        reactive.Verify(Officer(Priya, "Ms. Priya Nair"), true).Error!.Code.ShouldBe("bloodbank.nothing_to_verify");
        reactive.RecordLabResults(BloodGroup.BPositive, Clear, Officer(Santosh, "Mr. Santosh Gaikwad")).Error!.Code.ShouldBe("bloodbank.results_final");
    }

    [Fact]
    public void Complete_results_are_corrected_before_verification_only_by_the_officer_who_entered_them()
    {
        var donation = Entered();

        donation.RecordLabResults(BloodGroup.BNegative, Clear, Officer(Priya, "Ms. Priya Nair")).Error!.Code.ShouldBe("bloodbank.results_entered_by_other");
        donation.RecordLabResults(BloodGroup.BNegative, Clear, Officer(Santosh, "Mr. Santosh Gaikwad")).IsSuccess.ShouldBeTrue();
        donation.BloodGroup.ShouldBe(BloodGroup.BNegative);

        // Markers still running are completed by whoever has the result; that officer has then entered them.
        var partial = Entered(Clear with { Malaria = TtiResult.Pending });
        partial.RecordLabResults(BloodGroup.BPositive, Clear, Officer(Priya, "Ms. Priya Nair")).IsSuccess.ShouldBeTrue();
        partial.LabResultsByUserId.ShouldBe(Priya);
        partial.Verify(Officer(Priya, "Ms. Priya Nair"), true).Error!.Code.ShouldBe("bloodbank.verify_own_results");
        partial.Verify(Officer(Santosh, "Mr. Santosh Gaikwad"), true).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Red_cells_are_not_issued_by_the_officer_who_both_grouped_the_patient_and_cross_matched_the_unit()
    {
        var bothBySantosh = new IssueChecks(GroupedBy: Santosh, CrossMatchedBy: Santosh, CrossMatched: true, EmergencyRelease: false, ReleaseAuthorisedBy: null);

        IssueSeparation.OwnChecksReason(Santosh, bothBySantosh).ShouldNotBeNull();
        IssueSeparation.OwnChecksReason(Priya, bothBySantosh).ShouldBeNull("someone else issues what one officer grouped and cross-matched");
        IssueSeparation.OwnChecksReason(Santosh, bothBySantosh with { CrossMatchedBy = Priya }).ShouldBeNull("another officer cross-matched");
        IssueSeparation.OwnChecksReason(Santosh, bothBySantosh with { GroupedBy = Priya }).ShouldBeNull("another officer grouped");
        IssueSeparation.OwnChecksReason(Santosh, bothBySantosh with { GroupedBy = null }).ShouldBeNull("grouped before officers were recorded");
    }

    [Fact]
    public void Plasma_goes_on_the_group_alone_so_the_officer_who_grouped_does_not_also_issue_it()
    {
        var plasma = new IssueChecks(GroupedBy: Santosh, CrossMatchedBy: null, CrossMatched: false, EmergencyRelease: false, ReleaseAuthorisedBy: null);

        IssueSeparation.OwnChecksReason(Santosh, plasma).ShouldNotBeNull();
        IssueSeparation.OwnChecksReason(Priya, plasma).ShouldBeNull();
    }

    [Fact]
    public void An_uncross_matched_release_is_issued_by_someone_other_than_the_doctor_who_authorised_it()
    {
        var release = new IssueChecks(GroupedBy: Santosh, CrossMatchedBy: null, CrossMatched: false, EmergencyRelease: true, ReleaseAuthorisedBy: DrRao);

        IssueSeparation.OwnChecksReason(Santosh, release).ShouldBeNull("the doctor's authorisation is the second person");
        IssueSeparation.OwnChecksReason(DrRao, release).ShouldNotBeNull();
    }

    [Fact]
    public void A_grouping_is_compared_with_the_patients_earlier_confirmed_groups()
    {
        GroupingHistory.Disagreement(BloodGroup.BPositive, []).ShouldBeNull("a first request has nothing to compare with");
        GroupingHistory.Disagreement(BloodGroup.APositive, [BloodGroup.APositive, BloodGroup.APositive]).ShouldBeNull();
        GroupingHistory.Disagreement(BloodGroup.BPositive, [BloodGroup.APositive]).ShouldBe(BloodGroup.APositive);
        GroupingHistory.Disagreement(BloodGroup.ANegative, [BloodGroup.APositive]).ShouldBe(BloodGroup.APositive, "the Rh(D) type counts too");
        GroupingHistory.Disagreement(BloodGroup.APositive, [BloodGroup.Unknown, BloodGroup.APositive]).ShouldBeNull();
    }

    [Fact]
    public void A_grouping_that_disagrees_with_earlier_requests_holds_the_request_until_another_officer_groups_a_new_sample()
    {
        var request = new BloodRequest { UnitsRequested = 1, Status = BloodRequestStatus.Pending };

        request.RecordGrouping(BloodGroup.BPositive, Officer(Santosh, "Mr. Santosh Gaikwad"), earlierGroup: BloodGroup.APositive).IsSuccess.ShouldBeTrue();
        (request.GroupingHeld, request.EarlierGroup, request.PatientBloodGroup).ShouldBe((true, (BloodGroup?)BloodGroup.APositive, BloodGroup.BPositive));

        // The same officer does not regroup their own sample's request, and a third group is a discrepancy.
        request.Regroup(BloodGroup.APositive, Officer(Santosh, "Mr. Santosh Gaikwad")).Error!.Code.ShouldBe("bloodbank.regroup_own_sample");
        request.Regroup(BloodGroup.OPositive, Officer(Priya, "Ms. Priya Nair")).Error!.Code.ShouldBe("bloodbank.grouping_discrepancy");
        request.GroupingHeld.ShouldBeTrue();

        // Another officer groups a new sample A+, as the earlier requests: the first sample was the wrong blood in the tube.
        request.Regroup(BloodGroup.APositive, Officer(Priya, "Ms. Priya Nair")).IsSuccess.ShouldBeTrue();
        (request.GroupingHeld, request.PatientBloodGroup, request.FirstSampleGroup, request.GroupedByUserId)
            .ShouldBe((false, BloodGroup.APositive, (BloodGroup?)BloodGroup.BPositive, (Guid?)Priya));
    }

    [Fact]
    public void A_grouping_that_agrees_with_earlier_requests_holds_nothing_and_a_regroup_is_only_for_a_held_request()
    {
        var request = new BloodRequest { UnitsRequested = 1, Status = BloodRequestStatus.Pending };

        request.RecordGrouping(BloodGroup.APositive, Officer(Santosh, "Mr. Santosh Gaikwad"), earlierGroup: null).IsSuccess.ShouldBeTrue();

        (request.GroupingHeld, request.EarlierGroup).ShouldBe((false, (BloodGroup?)null));
        request.Regroup(BloodGroup.APositive, Officer(Priya, "Ms. Priya Nair")).Error!.Code.ShouldBe("bloodbank.regroup_not_needed");
    }
}
