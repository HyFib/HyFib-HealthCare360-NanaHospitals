using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;

public sealed record WorklistItem(
    Guid SampleId,
    string Barcode,
    Guid LabOrderId,
    string AccessionNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    string? Age,
    Gender Gender,
    PatientClass PatientClass,
    OrderPriority Priority,
    LabSection Section,
    SampleStatus Status,
    DateTimeOffset? ReceivedAt,
    int Pending,
    int Entered,
    int Verified,
    int Critical,
    IReadOnlyList<string> Tests);

public sealed record SusceptibilityDto(string Antibiotic, SusceptibilityInterpretation Interpretation, string? Mic);

/// <summary>One analyte line on the result-entry / verification sheet, with the reference interval for this patient.</summary>
public sealed record ResultSheetLine(
    Guid ResultId,
    Guid LabOrderItemId,
    string OrderedTest,
    Guid LabTestId,
    string TestCode,
    string TestName,
    string? GroupName,
    int DisplayOrder,
    LabResultType ResultType,
    LabResultStatus Status,
    string? Value,
    string? Unit,
    int Decimals,
    IReadOnlyList<string> Options,
    ResultFlag? Flag,
    bool IsCritical,
    string? ReferenceText,
    decimal? PreviousValue,
    DateTimeOffset? PreviousResultAt,
    decimal? DeltaPercent,
    bool DeltaExceeded,
    bool AutoValidated,
    string? InstrumentCode,
    string? Method,
    string? Remarks,
    string? Organism,
    string? ColonyCount,
    IReadOnlyList<SusceptibilityDto> Susceptibilities,
    DateTimeOffset? EnteredAt,
    DateTimeOffset? VerifiedAt,
    bool HasCriticalCall,
    uint Version);

public sealed record ResultSheet(
    Guid SampleId,
    string Barcode,
    SampleStatus SampleStatus,
    Guid LabOrderId,
    string AccessionNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    string AgeSex,
    string? ClinicalNotes,
    IReadOnlyList<ResultSheetLine> Lines);

public sealed record ResultEntry(
    Guid ResultId,
    string? Value,
    string? Remarks,
    string? Organism,
    string? ColonyCount,
    IReadOnlyList<SusceptibilityDto>? Susceptibilities);

public sealed record EnterResultsRequest(IReadOnlyList<ResultEntry> Entries);

/// <summary>
/// Pathologist sign-off. <see cref="QcOverrideReason"/> is needed only when QC is failing for an assay;
/// <see cref="CriticalCall"/> only when a critical result has no documented call yet; <see cref="Release"/> releases
/// the verified tests straight away (one step instead of two at the reporting desk).
/// </summary>
public sealed record VerifyResultsRequest(IReadOnlyList<Guid> ResultIds, string? QcOverrideReason, CriticalCallDetails? CriticalCall, bool Release);

public sealed record VerificationOutcome(int Verified, int CriticalCallsRecorded, int QcOverrides, IReadOnlyList<Guid> ReleasedItemIds);

public sealed record ReopenResultRequest(string Reason);

public sealed record CriticalCallDto(
    Guid Id,
    DiagnosticKind Kind,
    Guid PatientId,
    string Uhid,
    string PatientName,
    string Finding,
    string InformedPersonName,
    string? InformedPersonRole,
    CommunicationMethod Method,
    DateTimeOffset InformedAt,
    bool ReadBackConfirmed,
    string? CalledBy,
    string? Notes,
    Guid? LabResultId,
    Guid? ImagingStudyId,
    Guid? CardiologyStudyId);

/// <summary>Shared by lab, radiology and cardiology sign-off requests.</summary>
public sealed class CriticalCallDetailsValidator : AbstractValidator<CriticalCallDetails>
{
    public CriticalCallDetailsValidator()
    {
        RuleFor(x => x.InformedPersonName).NotEmpty().WithMessage("Enter who was informed.").MaximumLength(200);
        RuleFor(x => x.InformedPersonRole).MaximumLength(100);
        RuleFor(x => x.Method).IsInEnum();
        RuleFor(x => x.InformedAt).NotEqual(default(DateTimeOffset)).WithMessage("Enter when the call was made.");
        RuleFor(x => x.ReadBackConfirmed).Equal(true).WithMessage("The person informed must read the value back.");
        RuleFor(x => x.Notes).SafeText(1000);
    }
}

public sealed class EnterResultsRequestValidator : AbstractValidator<EnterResultsRequest>
{
    public EnterResultsRequestValidator()
    {
        RuleFor(x => x.Entries).NotEmpty().Must(e => e.Count <= 200).WithMessage("At most 200 results at a time.");
        RuleForEach(x => x.Entries).ChildRules(e =>
        {
            e.RuleFor(r => r.ResultId).NotEmpty();
            e.RuleFor(r => r.Value).SafeText(2000);
            e.RuleFor(r => r.Remarks).SafeText(1000);
            e.RuleFor(r => r.Organism).SafeText(200);
            e.RuleFor(r => r.ColonyCount).SafeText(100);
            e.RuleFor(r => r.Susceptibilities).Must(s => s is null || s.Count <= 60).WithMessage("At most 60 antibiotics.");
            e.RuleForEach(r => r.Susceptibilities).ChildRules(s =>
            {
                s.RuleFor(a => a.Antibiotic).NotEmpty().MaximumLength(100);
                s.RuleFor(a => a.Interpretation).IsInEnum();
                s.RuleFor(a => a.Mic).MaximumLength(32);
            });
        });
    }
}

public sealed class VerifyResultsRequestValidator : AbstractValidator<VerifyResultsRequest>
{
    public VerifyResultsRequestValidator()
    {
        RuleFor(x => x.ResultIds).NotEmpty().Must(ids => ids.Count <= 200).WithMessage("At most 200 results at a time.");
        RuleForEach(x => x.ResultIds).NotEmpty();
        RuleFor(x => x.QcOverrideReason)
            .MinimumLength(10).WithMessage("Explain why results are released despite failed QC (at least 10 characters).")
            .SafeText(500)
            .When(x => x.QcOverrideReason is not null);
        RuleFor(x => x.CriticalCall!).SetValidator(new CriticalCallDetailsValidator()).When(x => x.CriticalCall is not null);
    }
}

public sealed class ReopenResultRequestValidator : AbstractValidator<ReopenResultRequest>
{
    public ReopenResultRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).SafeText(300);
}
