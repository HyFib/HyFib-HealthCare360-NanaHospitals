using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>The correction asked for, exactly as it will be made once approved.</summary>
public sealed record LabAmendmentPayload(Guid ReportId, AmendReportRequest Request);

/// <summary>
/// A correction of a released lab report, where the lab requires a second person to verify (R-6,
/// <see cref="LabResultService.SeparateVerifierSetting"/>). Whoever enters a value does not also verify it, and a correction
/// enters new values into a report doctors may already have acted on: one pathologist asks for it, and another, never the
/// same person, releases it by approving the request. A lab with one pathologist corrects at once.
/// </summary>
internal sealed class LabAmendmentApproval(LabReportService reports) : IApprovalAction
{
    public const string Key = "diagnostics.lab_amendment";

    public string Kind => Key;

    public string Title => "Lab report correction";

    public string ApprovePermission => DiagnosticsPermissions.LabResultsVerify;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => reports.AmendApprovedAsync(request, ct);
}
