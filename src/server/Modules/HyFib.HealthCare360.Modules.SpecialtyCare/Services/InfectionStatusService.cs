using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Dialysis;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>The infection status asked for, on the lab result that supports it.</summary>
public sealed record InfectionStatusPayload(Guid PatientId, SeroStatus From, SeroStatus To, Guid LabResultId, DateOnly TestedOn, string Reason);

/// <summary>A change made at once (<see cref="Made"/>), or asked for (<see cref="Asked"/>).</summary>
public sealed record InfectionStatusOutcome(bool Made, ApprovalRequestDto? Asked);

/// <summary>
/// A dialysis patient's infection status (DIA-02), which decides the machine they are dialysed on and their isolation: it
/// changes only with a verified lab result of the patient, for the infection concerned, from the last 90 days. A
/// sero-negative patient found positive is isolated at once; taking a patient off a positive status (to sero-negative, or
/// to another positive status) waits for a second person who approves infection status changes, never the one who asked
/// (maker-checker, slice 15). A hospital lets one person make that change only by switching
/// <see cref="ApprovalSetting"/> off, which waits for a second administrator; the lab result is required either way.
/// </summary>
public sealed class InfectionStatusService(
    HospitalDbContext db, IApprovals approvals, ISettingsService settings, IAuditLogger audit, ICurrentUser currentUser, TimeProvider time)
{
    public const string ChangeKind = "dialysis.infection_status";

    /// <summary>Facility setting (bool, on unless false): taking a patient off a positive status waits for a second person.</summary>
    public const string ApprovalSetting = "dialysis.infectionStatusApproval";

    public async Task<Result<InfectionStatusOutcome>> ChangeAsync(Guid patientId, ChangeInfectionStatusRequest request, CancellationToken ct)
    {
        var profile = await db.Set<DialysisPatientProfile>().FirstOrDefaultAsync(p => p.PatientId == patientId, ct);
        if (profile is null)
        {
            return Error.NotFound("Dialysis profile", patientId);
        }

        db.SetExpectedVersion(profile, request.Version);
        if (profile.SeroStatus == request.SeroStatus)
        {
            return Error.Rule("dialysis.infection_status_same", $"The patient is already {SeroSegregation.Describe(profile.SeroStatus)}.");
        }

        var evidence = await EvidenceAsync(profile, request.SeroStatus, request.LabResultId, ct);
        if (evidence.IsFailure)
        {
            return evidence.Error!;
        }

        var name = Name();
        var waits = InfectionStatusChange.Waits(profile.SeroStatus, request.SeroStatus);
        if (!waits || await settings.GetAsync<bool?>(ApprovalSetting, profile.FacilityId, ct) == false)
        {
            var from = profile.SeroStatus;
            Apply(profile, request.SeroStatus, request.LabResultId, evidence.Value, name, null);
            Audit(profile, waits
                ? $"Dialysis infection status changed by {name} alone (a second person is not required here): {SeroSegregation.Describe(from)} → {SeroSegregation.Describe(request.SeroStatus)}, on {evidence.Value.Words} — {request.Reason.Trim()}"
                : $"Dialysis patient isolated at once: {SeroSegregation.Describe(from)} → {SeroSegregation.Describe(request.SeroStatus)}, on {evidence.Value.Words} — {request.Reason.Trim()}",
                null, waits ? AuditSeverity.Warning : AuditSeverity.Info);
            await db.SaveChangesAsync(ct);
            return new InfectionStatusOutcome(true, null);
        }

        List<ApprovalDetail> details =
        [
            new("Infection status", $"{SeroSegregation.Describe(profile.SeroStatus)} → {SeroSegregation.Describe(request.SeroStatus)}"),
            new("Lab result", evidence.Value.Words),
            new("Machine", $"From now on dialysed only on machines dedicated to {SeroSegregation.Describe(request.SeroStatus)} patients"),
            new("Reason", request.Reason.Trim()),
            new("Who decides", "Someone who approves infection status changes, never the one who asked."),
        ];
        var asked = await approvals.RequestAsync(ChangeKind,
            new ApprovalSubject(nameof(DialysisPatientProfile), profile.Id, profile.Version, profile.FacilityId, profile.PatientId),
            new InfectionStatusPayload(patientId, profile.SeroStatus, request.SeroStatus, request.LabResultId, evidence.Value.TestedOn, request.Reason.Trim()),
            $"Change a dialysis patient's infection status: {SeroSegregation.Describe(profile.SeroStatus)} → {SeroSegregation.Describe(request.SeroStatus)}",
            details, request.Reason, ct);
        return asked.IsFailure ? asked.Error! : new InfectionStatusOutcome(false, asked.Value);
    }

    /// <summary>Makes an approved change in the approval's transaction: only over the profile as it was asked for, on a result still recent.</summary>
    internal async Task<Result<string>> ChangeApprovedAsync(ApprovalRequest approval, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<InfectionStatusPayload>(approval.Payload);
        var profile = await db.Set<DialysisPatientProfile>().FirstOrDefaultAsync(p => p.PatientId == asked.PatientId, ct);
        if (profile is null)
        {
            return Error.NotFound("Dialysis profile", asked.PatientId);
        }

        if (profile.Version != approval.SubjectVersion || profile.SeroStatus != asked.From)
        {
            return Error.Rule("approval.stale", "The patient's dialysis profile changed after this was asked for, so nothing was changed. Ask again.");
        }

        var evidence = await EvidenceAsync(profile, asked.To, asked.LabResultId, ct);
        if (evidence.IsFailure)
        {
            return Error.Rule("approval.stale", $"The lab result no longer supports the change, so nothing was changed: {evidence.Error!.Message}");
        }

        var name = Name();
        Apply(profile, asked.To, asked.LabResultId, evidence.Value, name, approval.RequestedByName);
        var outcome = $"Dialysis infection status changed: {SeroSegregation.Describe(asked.From)} → {SeroSegregation.Describe(asked.To)}";
        Audit(profile, $"{outcome}, on {evidence.Value.Words}. Asked for by {approval.RequestedByName}, approved by {name}: {asked.Reason}", approval, AuditSeverity.Warning);
        return outcome;
    }

    private sealed record Evidence(DateOnly TestedOn, string Words);

    /// <summary>The supporting result: the patient's own, verified, for the infection concerned, from the last 90 days.</summary>
    private async Task<Result<Evidence>> EvidenceAsync(DialysisPatientProfile profile, SeroStatus asked, Guid labResultId, CancellationToken ct)
    {
        var result = await db.Set<LabResult>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .FirstOrDefaultAsync(r => r.Id == labResultId && r.PatientId == profile.PatientId, ct);
        if (result is null)
        {
            return Error.Field("labResultId", "Choose one of this patient's lab results.");
        }

        if (result.Status != LabResultStatus.Verified || result.VerifiedAt is not { } verifiedAt)
        {
            return Error.Rule("dialysis.lab_result_unverified", $"{result.TestName} is not verified yet; the infection status changes on a verified result.");
        }

        var tested = InfectionStatusChange.Tested(profile.SeroStatus, asked);
        if (!InfectionStatusChange.TestsFor(tested, result.TestCode, result.TestName))
        {
            return Error.Rule("dialysis.lab_result_mismatch",
                $"{result.TestName} does not test for {Infection(tested)}. Choose the patient's {Infection(tested)} result.");
        }

        var on = HospitalClock.ToLocalDate(verifiedAt);
        if (!InfectionStatusChange.Recent(on, time.Today()))
        {
            return Error.Rule("dialysis.lab_result_old", $"{result.TestName} was verified on {on:dd MMM yyyy}; use a result from the last {InfectionStatusChange.MaxResultAgeDays} days.");
        }

        var value = result.ValueText ?? result.NumericValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—";
        return new Evidence(on, $"{result.TestName}: {value}{(result.Flag is { } flag ? $" ({flag})" : string.Empty)}, verified {on:dd MMM yyyy}");
    }

    private static void Apply(DialysisPatientProfile profile, SeroStatus to, Guid labResultId, Evidence evidence, string by, string? requestedBy)
    {
        profile.SeroStatus = to;
        profile.SeroTestedOn = evidence.TestedOn;
        profile.SeroLabResultId = labResultId;
        profile.SeroChangedByName = by;
        profile.SeroChangeRequestedByName = requestedBy;
    }

    private void Audit(DialysisPatientProfile profile, string summary, ApprovalRequest? approval, AuditSeverity severity) =>
        audit.Add(new AuditEntry(AuditCategories.Data, "dialysis.infection_status_changed", summary.Length <= 1000 ? summary : summary[..999] + "…")
        {
            PatientId = profile.PatientId,
            EntityType = nameof(DialysisPatientProfile),
            EntityId = profile.Id.ToString(),
            Severity = severity,
            Details = new { profile.SeroStatus, profile.SeroLabResultId, approval = approval?.Id, requestedBy = approval?.RequestedBy, changedBy = currentUser.UserId },
        });

    private string Name() => currentUser.DisplayName ?? currentUser.UserName ?? "Staff";

    private static string Infection(SeroStatus status) => status switch
    {
        SeroStatus.HBsAg => "hepatitis B (HBsAg)",
        SeroStatus.HCV => "hepatitis C (anti-HCV)",
        SeroStatus.HIV => "HIV",
        _ => "the infection",
    };
}

/// <summary>A dialysis patient taken off a positive infection status: one asks with the lab result, a second person approves.</summary>
internal sealed class InfectionStatusApproval(InfectionStatusService service) : IApprovalAction
{
    public string Kind => InfectionStatusService.ChangeKind;

    public string Title => "Dialysis infection status change";

    public string ApprovePermission => SpecialtyCarePermissions.DialysisInfectionStatusApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => service.ChangeApprovedAsync(request, ct);
}
