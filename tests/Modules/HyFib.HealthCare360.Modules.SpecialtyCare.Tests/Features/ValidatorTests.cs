using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Dialysis;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Features;

public sealed class ValidatorTests
{
    private static readonly FixedTimeProvider Time = new(TestData.Now);

    private static CreateSurgeryBookingRequest Booking(Guid? theatre = null, DateTimeOffset? start = null, DateTimeOffset? end = null) => new(
        Guid.CreateVersion7(), null, null, null, null, "Appendicectomy", Laterality.NotApplicable, null, Guid.CreateVersion7(), null, Guid.CreateVersion7(),
        AnaesthesiaType.GA, SurgeryPriority.Emergency, null, theatre, start, end, null);

    [Fact]
    public void Booking_is_either_a_request_or_a_complete_slot()
    {
        var validator = new CreateSurgeryBookingRequestValidator();

        validator.Validate(Booking()).IsValid.ShouldBeTrue();
        validator.Validate(Booking(Guid.CreateVersion7(), TestData.Now, TestData.Now.AddHours(1))).IsValid.ShouldBeTrue();
        validator.Validate(Booking(Guid.CreateVersion7(), TestData.Now, null)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Implant_entry_requires_lot_or_serial()
    {
        var validator = new AddSurgeryMaterialRequestValidator();
        var implant = new AddSurgeryMaterialRequest(MaterialKind.Implant, null, "Femoral component", "Acme", null, null, null, 1, 85000);

        validator.Validate(implant).IsValid.ShouldBeFalse();
        validator.Validate(implant with { SerialNumber = "SN123" }).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Dialysis_start_needs_exactly_one_dialyzer_choice()
    {
        var validator = new StartDialysisRequestValidator();
        var existing = new StartDialysisRequest(Guid.CreateVersion7(), Guid.CreateVersion7(), null, 2000, 500);

        validator.Validate(existing).IsValid.ShouldBeTrue();
        validator.Validate(existing with { DialyzerId = null }).IsValid.ShouldBeFalse();
        validator.Validate(existing with { NewDialyzer = new RegisterDialyzerRequest("FX8-9", null, null, null) }).IsValid.ShouldBeFalse();
        validator.Validate(existing with { DialyzerId = null, NewDialyzer = new RegisterDialyzerRequest("FX8-9", null, null, 1.8m) }).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Implausible_vitals_are_rejected()
    {
        var validator = new PreDialysisRequestValidator();

        validator.Validate(new PreDialysisRequest(62m, 150, 90, 80, 36.9m, 2500)).IsValid.ShouldBeTrue();
        validator.Validate(new PreDialysisRequest(62m, 1500, 90, 80, 36.9m, 2500)).IsValid.ShouldBeFalse();
        validator.Validate(new PreDialysisRequest(0m, 150, 90, 80, null, null)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Donor_needs_an_indian_mobile_for_recall()
    {
        var validator = new SaveDonorRequestValidator(Time);
        var donor = new SaveDonorRequest("Ravi", "Kumar", Gender.Male, new DateOnly(1990, 1, 1), BloodGroup.OPositive, "9876543210", null, null, null, true, null);

        validator.Validate(donor).IsValid.ShouldBeTrue();
        validator.Validate(donor with { Phone = "" }).IsValid.ShouldBeFalse();
        validator.Validate(donor with { Phone = "12345" }).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Replacement_donation_names_the_patient()
    {
        var validator = new CollectDonationRequestValidator(Time);
        var donation = new CollectDonationRequest(Guid.CreateVersion7(), "NH-BAG-1", BloodBagType.Triple, 450, DonationType.Replacement, null, null, null, null);

        validator.Validate(donation).IsValid.ShouldBeFalse();
        validator.Validate(donation with { ReplacementForPatientId = Guid.CreateVersion7() }).IsValid.ShouldBeTrue();
        validator.Validate(donation with { ReplacementForPatientId = Guid.CreateVersion7(), VolumeMl = 400 }).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Transfusion_reaction_cannot_be_dated_in_the_future()
    {
        var validator = new ReportReactionRequestValidator(Time);
        var report = new ReportReactionRequest(TestData.Now, [ReactionSymptom.Fever], null, true, 50, null, ReactionSeverity.Mild);

        validator.Validate(report).IsValid.ShouldBeTrue();
        validator.Validate(report with { OnsetAt = TestData.Now.AddHours(1) }).IsValid.ShouldBeFalse();
        validator.Validate(report with { Symptoms = [] }).IsValid.ShouldBeFalse();
    }
}
