using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.LabSamples;

public sealed record PendingCollectionItem(
    Guid SampleId,
    string Barcode,
    Guid LabOrderId,
    string AccessionNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    PatientClass PatientClass,
    OrderPriority Priority,
    ContainerType Container,
    SpecimenType SpecimenType,
    LabSection Section,
    IReadOnlyList<string> Tests,
    IReadOnlyList<string> Preparation,
    DateTimeOffset OrderedAt,
    bool IsRecollection);

/// <param name="Consents">
/// For HIV tests on the tubes: on what each is done, recorded before its sample is drawn (HIV and AIDS Act 2017, s.5).
/// </param>
public sealed record CollectSamplesRequest(
    IReadOnlyList<Guid>? SampleIds, DateTimeOffset? CollectedAt, string? CollectionSite, IReadOnlyList<TestConsentInput>? Consents = null);

/// <summary>
/// The consent to an HIV test, after pre-test counselling: the patient's, or their representative's (named, with the
/// relation), or a court's order. <paramref name="Reference"/> is the signed form's number, or the order's.
/// </summary>
public sealed record TestConsentInput(Guid ItemId, ConsentBasis Basis, string? GivenBy, string? Relation, string? Reference);

/// <summary>Data for the 50 × 25 mm barcode label (printed through the Desktop bridge as ZPL/TSPL).</summary>
public sealed record SampleLabel(
    Guid SampleId,
    string Barcode,
    string AccessionNumber,
    string Uhid,
    string PatientName,
    string AgeSex,
    ContainerType Container,
    SpecimenType SpecimenType,
    LabSection Section,
    OrderPriority Priority,
    IReadOnlyList<string> Tests,
    DateTimeOffset? CollectedAt);

public sealed record ReceiveSamplesRequest(IReadOnlyList<string> Barcodes);

public sealed record ReceiveOutcome(string Barcode, bool Received, Guid? SampleId, string? Message);

public sealed record RejectSampleRequest(SampleRejectionReason Reason, string? Remarks, bool Recollect);

public sealed record SampleRejected(Guid SampleId, Guid? RecollectionSampleId, string? RecollectionBarcode);

public sealed record SampleLookup(
    Guid Id,
    string Barcode,
    SampleStatus Status,
    Guid LabOrderId,
    string AccessionNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    string AgeSex,
    ContainerType Container,
    SpecimenType SpecimenType,
    LabSection Section,
    OrderPriority Priority,
    IReadOnlyList<string> Tests,
    DateTimeOffset? CollectedAt,
    DateTimeOffset? ReceivedAt,
    SampleRejectionReason? RejectionReason);

public sealed class CollectSamplesRequestValidator : AbstractValidator<CollectSamplesRequest>
{
    public CollectSamplesRequestValidator()
    {
        RuleFor(x => x.SampleIds).Must(ids => ids is null || ids.Count <= 50).WithMessage("At most 50 samples at a time.");
        RuleFor(x => x.CollectionSite).SafeText(100);
        RuleFor(x => x.Consents).Must(c => c is null || c.Count <= 50).WithMessage("At most 50 consents at a time.");
        RuleForEach(x => x.Consents).ChildRules(consent =>
        {
            consent.RuleFor(c => c.ItemId).NotEmpty();
            consent.RuleFor(c => c.Basis).IsInEnum();
            consent.RuleFor(c => c.GivenBy).NotEmpty().When(c => c.Basis == ConsentBasis.Representative)
                .WithMessage("Name the representative who consented.");
            consent.RuleFor(c => c.GivenBy).SafeText(200);
            consent.RuleFor(c => c.Relation).NotEmpty().When(c => c.Basis == ConsentBasis.Representative)
                .WithMessage("Give the representative's relation to the patient.");
            consent.RuleFor(c => c.Relation).SafeText(64);
            consent.RuleFor(c => c.Reference).NotEmpty().When(c => c.Basis == ConsentBasis.CourtOrder)
                .WithMessage("Give the number of the court order.");
            consent.RuleFor(c => c.Reference).SafeText(64);
        });
    }
}

public sealed class ReceiveSamplesRequestValidator : AbstractValidator<ReceiveSamplesRequest>
{
    public ReceiveSamplesRequestValidator()
    {
        RuleFor(x => x.Barcodes).NotEmpty().WithMessage("Scan at least one barcode.")
            .Must(b => b.Count <= 200).WithMessage("At most 200 barcodes at a time.");
        RuleForEach(x => x.Barcodes).NotEmpty().MaximumLength(20);
    }
}

public sealed class RejectSampleRequestValidator : AbstractValidator<RejectSampleRequest>
{
    public RejectSampleRequestValidator()
    {
        RuleFor(x => x.Reason).IsInEnum();
        RuleFor(x => x.Remarks).SafeText(500);
        RuleFor(x => x.Remarks).NotEmpty().When(x => x.Reason == SampleRejectionReason.Other).WithMessage("Describe why the sample is rejected.");
    }
}
