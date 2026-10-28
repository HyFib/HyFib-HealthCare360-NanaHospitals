using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.LabOrders;

public sealed record LabOrderListItem(
    Guid Id,
    string AccessionNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    PatientClass PatientClass,
    LabOrderSource Source,
    OrderPriority Priority,
    LabOrderStatus Status,
    DateTimeOffset OrderedAt,
    int TestCount,
    int ReleasedCount,
    IReadOnlyList<string> Tests,
    Guid? ReportId,
    LabReportStatus? ReportStatus);

public sealed record LabOrderItemDto(
    Guid Id,
    Guid LabTestId,
    string TestCode,
    string TestName,
    LabSection Section,
    LabOrderItemStatus Status,
    OrderPriority Priority,
    Guid? SampleId,
    Guid? ServiceItemId,
    Guid? ChargeId,
    string? ChargeError,

    // Set for a health-checkup package test: charged once under the package, so no per-test charge exists.
    Guid? PackageBookingId,
    int Quantity,
    Guid? ReferralLabId,
    DateTimeOffset? SentOutAt,
    DateTimeOffset? ExternalReportReceivedAt,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset? ReleasedAt,
    string? CancelReason,

    // For an HIV test: on what it is done (the patient's consent, a representative's, a court's order) and when it was recorded.
    ConsentBasis? ConsentBasis = null,
    DateTimeOffset? ConsentRecordedAt = null);

public sealed record LabSampleDto(
    Guid Id,
    string Barcode,
    SpecimenType SpecimenType,
    ContainerType Container,
    LabSection Section,
    OrderPriority Priority,
    SampleStatus Status,
    DateTimeOffset? CollectedAt,
    string? CollectedBy,
    string? CollectionSite,
    DateTimeOffset? ReceivedAt,
    string? ReceivedBy,
    SampleRejectionReason? RejectionReason,
    string? RejectionRemarks,
    DateTimeOffset? RejectedAt,
    Guid? RecollectionOfSampleId,
    Guid? RecollectedAsSampleId,
    IReadOnlyList<string> Tests);

public sealed record LabOrderDetail(
    Guid Id,
    string AccessionNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    Guid? VisitId,
    Guid? AdmissionId,
    PatientClass PatientClass,
    LabOrderSource Source,
    string? SourceOrderNumber,
    OrderPriority Priority,
    LabOrderStatus Status,
    Guid? OrderingPractitionerId,
    string? OrderingDoctor,
    string? ReferringDoctorName,
    string? ClinicalNotes,
    DateTimeOffset OrderedAt,
    DateTimeOffset? CompletedAt,
    Guid? ReportId,
    LabReportStatus? ReportStatus,
    IReadOnlyList<LabOrderItemDto> Items,
    IReadOnlyList<LabSampleDto> Samples,
    uint Version);

/// <summary>Walk-in / outside-referral registration at the lab counter (clinical orders arrive from the Clinical module).</summary>
public sealed record CreateLabOrderRequest(
    Guid PatientId,
    Guid? VisitId,
    IReadOnlyList<Guid> TestIds,
    OrderPriority Priority,
    LabOrderSource Source,
    Guid? OrderingPractitionerId,
    string? ReferringDoctorName,
    string? ClinicalNotes);

public sealed record CreatedSample(Guid Id, string Barcode, ContainerType Container, LabSection Section, IReadOnlyList<string> Tests);

public sealed record LabOrderCreated(Guid Id, string AccessionNumber, IReadOnlyList<CreatedSample> Samples, IReadOnlyList<string> ChargeWarnings);

public sealed record CancelLabItemRequest(string Reason);

public sealed record ChargeRetryOutcome(int Posted, IReadOnlyList<string> Failures);

public sealed record PatientLabHistoryPoint(
    Guid ResultId, Guid LabOrderId, string AccessionNumber, DateTimeOffset ResultAt, string? Value, decimal? NumericValue, string? Unit, ResultFlag? Flag, string? ReferenceText);

public sealed record PatientLabHistory(Guid PatientId, Guid LabTestId, string TestName, string? Unit, IReadOnlyList<PatientLabHistoryPoint> Points);

public sealed record LabTatRow(
    Guid LabTestId,
    string TestCode,
    string TestName,
    LabSection Section,
    int TargetHours,
    int Released,
    int WithinTarget,
    double? WithinTargetPercent,
    TatSummary OrderToRelease,
    TatSummary ReceiptToVerification);

public sealed record RejectionStat(SampleRejectionReason Reason, int Count);

public sealed record LabTatReport(
    DateOnly From, DateOnly To, TatSummary Overall, IReadOnlyList<LabTatRow> Tests, int Samples, int Rejections, IReadOnlyList<RejectionStat> RejectionsByReason);

public sealed class CreateLabOrderRequestValidator : AbstractValidator<CreateLabOrderRequest>
{
    public CreateLabOrderRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.TestIds).NotEmpty().WithMessage("Choose at least one test.")
            .Must(ids => ids.Count <= 50).WithMessage("At most 50 tests per order.");
        RuleForEach(x => x.TestIds).NotEmpty();
        RuleFor(x => x.Priority).IsInEnum();
        RuleFor(x => x.Source).IsInEnum()
            .NotEqual(LabOrderSource.ClinicalOrder).WithMessage("Clinical orders are placed from the doctor's order entry.");
        RuleFor(x => x.ReferringDoctorName).NotEmpty().When(x => x.Source == LabOrderSource.OutsideReferral)
            .WithMessage("Enter the referring doctor for an outside referral.");
        RuleFor(x => x.ReferringDoctorName).MaximumLength(200);
        RuleFor(x => x.ClinicalNotes).SafeText(1000);
    }
}

public sealed class CancelLabItemRequestValidator : AbstractValidator<CancelLabItemRequest>
{
    public CancelLabItemRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(300);
}
