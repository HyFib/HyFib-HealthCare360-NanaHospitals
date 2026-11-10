using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabMasters;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>What a loosening asked for changes once approved.</summary>
public enum LabSafetyTarget
{
    /// <summary>A test's reference intervals, with a critical limit loosened.</summary>
    TestRanges = 0,

    /// <summary>Automatic verification switched on for a test.</summary>
    TestAutoValidation,

    /// <summary>Automatic verification switched on for an analyser.</summary>
    InstrumentAutoValidation,
}

/// <summary>The loosening asked for, exactly as it will be made once approved.</summary>
public sealed record LabSafetyPayload(LabSafetyTarget Target, Guid Id, SetRangesRequest? Ranges);

/// <summary>
/// The lab's own safety limits where the lab asks for a second pathologist (<see cref="LabResultService.SeparateVerifierSetting"/>):
/// a test's critical limits, which make the lab call the doctor before a result is verified (LIS-05), and automatic
/// verification, which releases an analyser's result with nobody looking at it (LIS-04). One pathologist who loosens them
/// lets results through that the second pathologist was there to see. So loosening waits for another holder of
/// <c>lab.results.verify</c> (<see cref="LabSafetyApproval"/>), over the record as it was asked; making them stricter
/// (a critical limit narrowed or added, automatic verification switched off) is made at once. The test master is the
/// whole hospital's: it is guarded when the hospital, or any facility, asks for a second pathologist; an analyser when its
/// own facility does.
/// </summary>
internal sealed class LabSafetyLimits(
    HospitalDbContext db,
    ISettingsService settings,
    IApprovals approvals,
    IAuditLogger audit,
    ICurrentUser currentUser)
{
    /// <summary>Whether the hospital or any facility asks for a second pathologist (the test master is shared).</summary>
    public async Task<bool> GuardedForTestsAsync(CancellationToken ct)
    {
        if (await settings.GetAsync<bool?>(LabResultService.SeparateVerifierSetting, null, ct) == true)
        {
            return true;
        }

        var facilities = await db.Set<Facility>().AsNoTracking().Select(f => f.Id).ToListAsync(ct);
        foreach (var facility in facilities)
        {
            if (await settings.GetAsync<bool?>(LabResultService.SeparateVerifierSetting, facility, ct) == true)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the analyser's facility asks for a second pathologist.</summary>
    public async Task<bool> GuardedForFacilityAsync(Guid facilityId, CancellationToken ct) =>
        await settings.GetAsync<bool?>(LabResultService.SeparateVerifierSetting, facilityId, ct) == true;

    /// <summary>Asks for a loosening; nothing changes until another pathologist approves it.</summary>
    public async Task<Result<ApprovalRequestDto>> AskAsync(
        LabSafetyPayload payload, string subjectType, uint? version, Guid? facilityId, string summary, IReadOnlyList<ApprovalDetail> details,
        string? reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
        {
            return Error.Field("reason", "Say why (at least 10 characters): loosening this waits for a second pathologist, who reads it.");
        }

        if ((facilityId ?? currentUser.FacilityId) is not { } facility)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        return await approvals.RequestAsync(
            LabSafetyApproval.Key, new ApprovalSubject(subjectType, payload.Id, version, facility, null), payload, summary, details, reason, ct);
    }

    /// <summary>
    /// Makes an approved loosening, in the approval's transaction: on the record as it was when it was asked for, checked
    /// again, or not at all (<c>approval.stale</c>).
    /// </summary>
    internal async Task<Result<string>> ApplyAsync(ApprovalRequest approval, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<LabSafetyPayload>(approval.Payload);
        switch (asked.Target)
        {
            case LabSafetyTarget.TestRanges:
            {
                var test = await db.Set<LabTest>().Include(t => t.Ranges).FirstOrDefaultAsync(t => t.Id == asked.Id, ct);
                if (test is null)
                {
                    return Error.NotFound(nameof(LabTest), asked.Id);
                }

                if (test.Version != approval.SubjectVersion || asked.Ranges is null)
                {
                    return Stale(test.Name);
                }

                var built = LabMasterEndpoints.BuildRanges(test, asked.Ranges);
                if (built.IsFailure)
                {
                    return built.Error!;
                }

                var loosened = CriticalLimitChanges.Loosened(test.Ranges, built.Value, test.Decimals);
                LabMasterEndpoints.ReplaceRanges(db, test, built.Value);
                Audit("lab.master.critical_limits", test.Id, nameof(LabTest),
                    $"Critical limits of {test.Name} loosened, asked for by {approval.RequestedByName}: {string.Join("; ", loosened)}", approval);
                return $"Reference intervals of {test.Name} saved";
            }

            case LabSafetyTarget.TestAutoValidation:
            {
                var test = await db.Set<LabTest>().FirstOrDefaultAsync(t => t.Id == asked.Id, ct);
                if (test is null)
                {
                    return Error.NotFound(nameof(LabTest), asked.Id);
                }

                if (test.Version != approval.SubjectVersion || test.AllowAutoValidation)
                {
                    return Stale(test.Name);
                }

                test.AllowAutoValidation = true;
                Audit("lab.master.auto_validation", test.Id, nameof(LabTest),
                    $"Automatic verification of {test.Name} switched on, asked for by {approval.RequestedByName}", approval);
                return $"Automatic verification of {test.Name} on";
            }

            case LabSafetyTarget.InstrumentAutoValidation:
            {
                var instrument = await db.Set<LabInstrument>().FirstOrDefaultAsync(i => i.Id == asked.Id, ct);
                if (instrument is null)
                {
                    return Error.NotFound(nameof(LabInstrument), asked.Id);
                }

                if (instrument.Version != approval.SubjectVersion || instrument.AutoValidationEnabled)
                {
                    return Stale(instrument.Name);
                }

                instrument.AutoValidationEnabled = true;
                Audit("lab.master.auto_validation", instrument.Id, nameof(LabInstrument),
                    $"Automatic verification of analyser {instrument.Code} switched on, asked for by {approval.RequestedByName}", approval);
                return $"Automatic verification of analyser {instrument.Code} on";
            }

            default:
                return Error.Rule("approval.stale", "This kind of change is no longer made.");
        }
    }

    private static Error Stale(string name) => Error.Rule("approval.stale",
        $"{name} changed after this was asked for. Ask again, against it as it is now.");

    private void Audit(string action, Guid id, string type, string message, ApprovalRequest approval) =>
        audit.Add(new AuditEntry(AuditCategories.Clinical, action, message)
        {
            EntityType = type,
            EntityId = id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { requestedBy = approval.RequestedBy, requestedByName = approval.RequestedByName, approvedBy = currentUser.UserId, reason = approval.Reason },
        });
}

/// <summary>
/// A lab safety limit loosened where the lab asks for a second pathologist: a critical limit, or automatic verification
/// switched on (<see cref="LabSafetyLimits"/>). Another holder of <c>lab.results.verify</c>, never the one who asked,
/// approves it.
/// </summary>
internal sealed class LabSafetyApproval(LabSafetyLimits limits) : IApprovalAction
{
    public const string Key = "lab.safety_limits";

    public string Kind => Key;

    public string Title => "Lab safety limit loosened";

    public string ApprovePermission => DiagnosticsPermissions.LabResultsVerify;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => limits.ApplyAsync(request, ct);
}
