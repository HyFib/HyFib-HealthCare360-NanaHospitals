using FluentValidation;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Features.Breaches;
using HyFib.HealthCare360.Modules.Privacy.Features.Portal;
using HyFib.HealthCare360.Modules.Privacy.Features.Requests;
using HyFib.HealthCare360.Modules.Privacy.Features.Settings;
using HyFib.HealthCare360.Modules.Privacy.Services;

namespace HyFib.HealthCare360.Modules.Privacy.Tests;

public sealed class ValidatorTests
{
    private static CreateDataRequestRequest Desk(
        DataRequestType type = DataRequestType.Grievance,
        DataRequestChannel channel = DataRequestChannel.FrontDesk,
        Guid? patientId = null,
        string? principalName = "Asha Rao",
        string? phone = "9876543210",
        RequesterRelation requestedBy = RequesterRelation.Self,
        string? requesterName = null,
        string? details = "Stop sending me offers",
        IReadOnlyList<CorrectionInput>? corrections = null,
        IdentityCheck? check = null,
        string? note = null) =>
        new(type, channel, patientId, principalName, phone, null, requestedBy, requesterName, details, corrections, check, note);

    private static IEnumerable<string> Errors<T>(IValidator<T> validator, T request) =>
        validator.Validate(request).Errors.Select(e => e.PropertyName);

    [Fact]
    public void A_desk_request_is_valid() => new CreateDataRequestRequestValidator().Validate(Desk()).IsValid.ShouldBeTrue();

    [Fact]
    public void Staff_cannot_record_a_portal_request() =>
        Errors(new CreateDataRequestRequestValidator(), Desk(channel: DataRequestChannel.Portal)).ShouldContain("Channel");

    [Fact]
    public void Without_a_patient_the_request_names_the_person_and_how_to_reply()
    {
        Errors(new CreateDataRequestRequestValidator(), Desk(principalName: null)).ShouldContain("PrincipalName");
        Errors(new CreateDataRequestRequestValidator(), Desk(phone: null)).ShouldContain("contactPhone");
        Errors(new CreateDataRequestRequestValidator(), Desk(patientId: Guid.CreateVersion7(), principalName: null, phone: null)).ShouldBeEmpty();
    }

    [Fact]
    public void Someone_acting_for_the_patient_gives_their_name() =>
        Errors(new CreateDataRequestRequestValidator(), Desk(requestedBy: RequesterRelation.Nominee)).ShouldContain("RequesterName");

    [Fact]
    public void Only_a_correction_lists_changes_and_each_change_is_to_a_known_detail_once()
    {
        var name = new CorrectionInput(CorrectableFields.Name, "Asha R. Rao");
        Errors(new CreateDataRequestRequestValidator(), Desk(type: DataRequestType.Erasure, corrections: [name])).ShouldContain("Corrections");
        new CreateDataRequestRequestValidator().Validate(Desk(type: DataRequestType.Correction, details: null, corrections: [name])).IsValid.ShouldBeTrue();
        new CreateDataRequestRequestValidator().Validate(Desk(type: DataRequestType.Correction, corrections: [new CorrectionInput("salary", "x")]))
            .IsValid.ShouldBeFalse();
        new CreateDataRequestRequestValidator().Validate(Desk(type: DataRequestType.Correction, corrections: [name, name])).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Staff_say_how_they_checked_identity()
    {
        Errors(new CreateDataRequestRequestValidator(), Desk(check: IdentityCheck.PortalSignIn)).ShouldContain("IdentityCheck");
        Errors(new CreateDataRequestRequestValidator(), Desk(check: IdentityCheck.Other)).ShouldContain("IdentityNote");
        new VerifyIdentityRequestValidator().Validate(new VerifyIdentityRequest(IdentityCheck.PhotoId, null)).IsValid.ShouldBeTrue();
        new VerifyIdentityRequestValidator().Validate(new VerifyIdentityRequest(IdentityCheck.PortalSignIn, null)).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData(DataRequestType.Correction, true)]
    [InlineData(DataRequestType.Erasure, true)]
    [InlineData(DataRequestType.Grievance, true)]
    [InlineData(DataRequestType.Access, false)]
    public void The_portal_sends_corrections_erasures_and_concerns(DataRequestType type, bool valid) =>
        new PortalDataRequestCreateValidator().Validate(new PortalDataRequestCreate(Guid.CreateVersion7(), type, RequesterRelation.Self, null, "Please", null))
            .IsValid.ShouldBe(valid);

    [Fact]
    public void The_portal_acts_for_the_account_holder_or_their_family_only() =>
        new PortalDataRequestCreateValidator().Validate(new PortalDataRequestCreate(
                Guid.CreateVersion7(), DataRequestType.Erasure, RequesterRelation.AuthorisedRepresentative, "Lawyer", "Erase", null))
            .IsValid.ShouldBeFalse();

    [Fact]
    public void Decisions_accept_or_decline()
    {
        new DecideCorrectionsRequestValidator().Validate(new DecideCorrectionsRequest([new CorrectionDecisionInput(0, CorrectionDecision.Pending, null)]))
            .IsValid.ShouldBeFalse();
        new DecideCorrectionsRequestValidator().Validate(new DecideCorrectionsRequest([new CorrectionDecisionInput(0, CorrectionDecision.Accepted, null)]))
            .IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Closing_needs_an_outcome_and_a_reply()
    {
        new CloseDataRequestRequestValidator().Validate(new CloseDataRequestRequest(DataRequestStatus.InProgress, "x")).IsValid.ShouldBeFalse();
        new CloseDataRequestRequestValidator().Validate(new CloseDataRequestRequest(DataRequestStatus.Declined, "")).IsValid.ShouldBeFalse();
        new CloseDataRequestRequestValidator().Validate(new CloseDataRequestRequest(DataRequestStatus.PartlyCompleted, "Done in part.")).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Erasure_takes_items_from_the_list() =>
        new EraseDataRequestValidator().Validate(new EraseDataRequest(["messages", "records"])).IsValid.ShouldBeFalse();

    [Theory]
    [InlineData(1, true)]
    [InlineData(90, true)]
    [InlineData(91, false)]
    [InlineData(0, false)]
    public void The_response_period_is_at_most_90_days(int days, bool valid) =>
        new SavePrivacySettingsRequestValidator().Validate(new SavePrivacySettingsRequest("Grievance Officer", null, "privacy@hospital.in", null, null, days))
            .IsValid.ShouldBe(valid);

    [Fact]
    public void A_nominee_can_be_reached()
    {
        new SaveNomineeRequestValidator().Validate(new SaveNomineeRequest("Ravi Rao", "Husband", null, null)).IsValid.ShouldBeFalse();
        new SaveNomineeRequestValidator().Validate(new SaveNomineeRequest("Ravi Rao", "Husband", "9812345678", null)).IsValid.ShouldBeTrue();
        new PortalNomineeRequestValidator().Validate(new PortalNomineeRequest(Guid.CreateVersion7(), "Ravi Rao", "Husband", null, "ravi@example.in"))
            .IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_breach_needs_a_title_and_a_description_and_a_sensible_period()
    {
        var aware = new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);
        var valid = new SaveBreachRequest("Laptop stolen", "A laptop was stolen.", aware, aware.AddDays(-1), aware.AddHours(-20), [BreachNature.Confidentiality],
            null, [BreachDataCategory.Health], 120, null, null, null, null, null, null, null);

        new SaveBreachRequestValidator().Validate(valid).IsValid.ShouldBeTrue();
        new SaveBreachRequestValidator().Validate(valid with { Title = "" }).IsValid.ShouldBeFalse();
        new SaveBreachRequestValidator().Validate(valid with { OccurredTo = aware.AddDays(-2) }).IsValid.ShouldBeFalse();
        new SaveBreachRequestValidator().Validate(valid with { PrincipalNotice = new string('x', 321) }).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Affected_patients_are_chosen_or_pasted() =>
        new AddAffectedRequestValidator().Validate(new AddAffectedRequest([], [])).IsValid.ShouldBeFalse();
}
