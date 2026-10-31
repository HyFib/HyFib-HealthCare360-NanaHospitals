using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.UnmatchedResults;

/// <summary>
/// A result an analyser sent that could not go on a test, as the lab's list shows it: what the analyser said, why the
/// LIS kept it aside, the sample and patient when the barcode is known, and what was done with it.
/// </summary>
public sealed record UnmatchedResultDto(
    Guid Id,
    string InstrumentCode,
    string? SampleBarcode,
    Guid? SampleId,
    Guid? PatientId,
    string? Uhid,
    string? PatientName,
    Guid? LabResultId,
    string? TestName,
    string ChannelCode,
    string Value,
    string? InstrumentUnit,
    string? InstrumentFlags,
    string? InstrumentStatus,
    string? InstrumentComment,
    DateTimeOffset? ResultedAt,
    DateTimeOffset ReceivedAt,
    UnmatchedResultReason Reason,
    string Detail,
    UnmatchedResultStatus Status,
    Guid? AssignedResultId,
    string? DiscardReason,
    string? ResolvedBy,
    DateTimeOffset? ResolvedAt);

/// <summary>The result (a test's line on a sample's result sheet) the analyser's value belongs on.</summary>
public sealed record AssignUnmatchedResultRequest(Guid ResultId);

public sealed record DiscardUnmatchedResultRequest(string Reason);

public sealed class AssignUnmatchedResultRequestValidator : AbstractValidator<AssignUnmatchedResultRequest>
{
    public AssignUnmatchedResultRequestValidator() => RuleFor(x => x.ResultId).NotEmpty().WithMessage("Choose the test the result belongs to.");
}

public sealed class DiscardUnmatchedResultRequestValidator : AbstractValidator<DiscardUnmatchedResultRequest>
{
    public DiscardUnmatchedResultRequestValidator() =>
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).WithMessage("Say why the result is set aside (at least 5 characters).")
            .SafeText(UnmatchedLabResult.DiscardReasonLength);
}
