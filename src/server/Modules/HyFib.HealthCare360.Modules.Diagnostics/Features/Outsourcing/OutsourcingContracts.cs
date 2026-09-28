using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.Outsourcing;

/// <summary>An outsourced test with its dispatch/receipt tracking (LIS-08).</summary>
public sealed record OutsourcedItem(
    Guid ItemId,
    Guid LabOrderId,
    string AccessionNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    string TestName,
    LabOrderItemStatus Status,
    Guid? SampleId,
    string? Barcode,
    Guid? ReferralLabId,
    string? ReferralLabName,
    DateTimeOffset? CollectedAt,
    DateTimeOffset? SentOutAt,
    string? DispatchReference,
    DateTimeOffset? ExternalReportReceivedAt,
    DateTimeOffset? DueAt,
    bool IsOverdue);

public sealed record SendOutRequest(Guid ReferralLabId, string? DispatchReference);

public sealed record ReceiveExternalReportRequest(string? ExternalReference, Guid? ReportFileId);

public sealed class SendOutRequestValidator : AbstractValidator<SendOutRequest>
{
    public SendOutRequestValidator()
    {
        RuleFor(x => x.ReferralLabId).NotEmpty();
        RuleFor(x => x.DispatchReference).MaximumLength(64);
    }
}

public sealed class ReceiveExternalReportRequestValidator : AbstractValidator<ReceiveExternalReportRequest>
{
    public ReceiveExternalReportRequestValidator() => RuleFor(x => x.ExternalReference).MaximumLength(64);
}
