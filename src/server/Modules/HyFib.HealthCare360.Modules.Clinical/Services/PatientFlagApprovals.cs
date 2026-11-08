using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>An allergy's status and severity: what the prescribing checks and the banner read.</summary>
public sealed record AllergyStanding(AllergyStatus Status, AllergySeverity Severity);

/// <summary>An allergy change as asked: its standing before, and the status (with its reason) and severity asked for.</summary>
public sealed record AllergyChangePayload(Guid AllergyId, Guid PatientId, AllergyStanding Before, AllergyStanding Asked, string? StatusReason);

/// <summary>A DNR alert to take off, with the reason.</summary>
public sealed record DnrLiftPayload(Guid AlertId, Guid PatientId, string Reason);

/// <summary>A change to a patient flag waiting for a second person: the request, what it does in words, who asked and when.</summary>
public sealed record PendingFlagChange(Guid ApprovalId, string Summary, string RequestedBy, DateTimeOffset RequestedAt);

/// <summary>
/// Which allergy changes need a second clinician (maker-checker, slice 7). An active allergy made inactive, resolved or
/// entered in error drops out of the prescribing checks, and a lower severity softens the alert a prescriber sees: both
/// wait. Recording an allergy, raising its severity, making one active again, or changing the reaction or notes are made
/// at once. An allergy that is not active is in no check, so its severity changes at once.
/// </summary>
public static class AllergyChangeRule
{
    /// <summary>What of <paramref name="asked"/> waits, given the allergy's <paramref name="now"/>: null when nothing does.</summary>
    public static AllergyStanding? Waits(AllergyStanding now, AllergyStanding asked)
    {
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(asked);
        if (now.Status != AllergyStatus.Active)
        {
            return null;
        }

        var deactivated = asked.Status != AllergyStatus.Active;
        var lowered = asked.Severity < now.Severity;
        return deactivated || lowered ? asked with { Severity = lowered ? asked.Severity : now.Severity } : null;
    }

    /// <summary>"Active, severe → entered in error, severe" for the checker and the audit trail.</summary>
    public static string Describe(AllergyStanding from, AllergyStanding to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        List<string> parts = [];
        if (from.Status != to.Status)
        {
            parts.Add($"{Words(from.Status)} → {Words(to.Status)}");
        }

        if (from.Severity != to.Severity)
        {
            parts.Add($"severity {Words(from.Severity)} → {Words(to.Severity)}");
        }

        return parts.Count == 0 ? "unchanged" : string.Join("; ", parts);
    }

    public static string Words(AllergyStatus status) => status switch
    {
        AllergyStatus.Active => "active",
        AllergyStatus.Inactive => "inactive",
        AllergyStatus.Resolved => "resolved",
        AllergyStatus.EnteredInError => "entered in error",
        _ => status.ToString(),
    };

    public static string Words(AllergySeverity severity) => severity switch
    {
        AllergySeverity.Anaphylaxis => "anaphylaxis",
        _ => severity.ToString().ToLowerInvariant(),
    };
}

/// <summary>
/// The patient banner's safety flags that need a second clinician (maker-checker, slice 7): an allergy taken out of the
/// prescribing checks or lowered in severity (<see cref="AllergyChangeRule"/>), decided by a prescriber, and a DNR order
/// taken off, decided by a second doctor. Until then the flag stays as it is on the banner, the wristband and in every
/// check. The approver is never the one who asked; each is made only over the flag as it was asked
/// (<c>approval.stale</c>) and audited with both names.
/// </summary>
/// <remarks>
/// DNR is put on at once, and only by a doctor (<see cref="ClinicalPermissions.EncountersSign"/>): it records the treating
/// doctor's own documented decision, after the conversation with the patient or family, as a signed note does. Taking it
/// off overrides a documented end-of-life decision, so a second doctor agrees first; until then the banner keeps it.
/// </remarks>
public sealed class PatientFlagApprovals(HospitalDbContext db, ICurrentUser current, IAuditLogger audit, IApprovals approvals, IRealtimePublisher realtime, TimeProvider time)
{
    public const string AllergyKind = "clinical.allergy_change";
    public const string DnrKind = "clinical.dnr_lift";

    /// <summary>Refused: another change of this kind is waiting (the approvals' own code).</summary>
    public const string WaitingCode = "approval.already_requested";

    /// <summary>The changes of <paramref name="kind"/> waiting on these records, by record.</summary>
    public async Task<IReadOnlyDictionary<Guid, PendingFlagChange>> PendingAsync(string kind, string subjectType, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, PendingFlagChange>();
        }

        var now = time.GetUtcNow();
        var waiting = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == kind && r.SubjectType == subjectType && ids.Contains(r.SubjectId) && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .Select(r => new { r.SubjectId, r.Id, r.Summary, r.RequestedByName, r.RequestedAt })
            .ToListAsync(ct);
        return waiting.GroupBy(r => r.SubjectId).ToDictionary(g => g.Key, g =>
        {
            var r = g.OrderBy(x => x.RequestedAt).First();
            return new PendingFlagChange(r.Id, r.Summary, r.RequestedByName, r.RequestedAt);
        });
    }

    /// <summary>The allergy change waiting on this allergy, if any.</summary>
    public async Task<(string RequestedBy, AllergyChangePayload Payload)?> WaitingAllergyAsync(Guid allergyId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var request = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == AllergyKind && r.SubjectType == nameof(Allergy) && r.SubjectId == allergyId && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .Select(r => new { r.RequestedByName, r.Payload })
            .FirstOrDefaultAsync(ct);
        return request is null ? null : (request.RequestedByName, ApprovalPayload.Read<AllergyChangePayload>(request.Payload));
    }

    public async Task<Result<ApprovalRequestDto>> AskAllergyAsync(Allergy allergy, AllergyStanding asked, string? statusReason, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(allergy);
        ArgumentNullException.ThrowIfNull(asked);
        var before = new AllergyStanding(allergy.Status, allergy.Severity);
        var patient = await PatientAsync(allergy.PatientId, ct);
        var words = AllergyChangeRule.Describe(before, asked);
        return await approvals.RequestAsync(
            AllergyKind,
            new ApprovalSubject(nameof(Allergy), allergy.Id, allergy.Version, current.FacilityId ?? patient.RegisteredFacilityId, allergy.PatientId),
            new AllergyChangePayload(allergy.Id, allergy.PatientId, before, asked, string.IsNullOrWhiteSpace(statusReason) ? null : statusReason.Trim()),
            $"Allergy to {allergy.Allergen} of {patient.Uhid}: {words}",
            [
                new("Patient", Label(patient)),
                new("Allergy", $"{allergy.Allergen} ({allergy.Type.ToString().ToLowerInvariant()}){(allergy.Reaction is { Length: > 0 } reaction ? $", {reaction}" : string.Empty)}"),
                new("Change", words),
                new("What it reaches", asked.Status != AllergyStatus.Active
                    ? "The allergy leaves the prescribing and dispensing checks and the patient banner and wristband. Until it is approved, it stays active in all of them."
                    : "Prescribers see a milder allergy alert. Until it is approved, the present severity stays in every check and on the banner."),
            ],
            reason,
            ct);
    }

    public async Task<Result<ApprovalRequestDto>> AskDnrLiftAsync(PatientAlert alert, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var patient = await PatientAsync(alert.PatientId, ct);
        return await approvals.RequestAsync(
            DnrKind,
            new ApprovalSubject(nameof(PatientAlert), alert.Id, alert.Version, current.FacilityId ?? patient.RegisteredFacilityId, alert.PatientId),
            new DnrLiftPayload(alert.Id, alert.PatientId, reason.Trim()),
            $"Take Do Not Resuscitate off {patient.Uhid}",
            [
                new("Patient", Label(patient)),
                new("Order", $"{alert.Title}, since {alert.EffectiveFrom.ToOffset(TimeSpan.FromHours(5.5)):d MMM yyyy}{(alert.Detail is { Length: > 0 } detail ? $": {detail}" : string.Empty)}"),
                new("What it reaches",
                    "Staff will attempt resuscitation in an arrest: it overrides a documented end-of-life decision. Until it is approved, the banner and wristband keep the DNR order."),
            ],
            reason,
            ct);
    }

    internal async Task<Result<string>> ApplyAllergyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<AllergyChangePayload>(request.Payload);
        var allergy = await db.Set<Allergy>().FirstOrDefaultAsync(a => a.Id == asked.AllergyId, ct);
        if (allergy is null)
        {
            return Error.Rule("allergy.gone", "The allergy no longer exists, so nothing was changed.");
        }

        var now = new AllergyStanding(allergy.Status, allergy.Severity);
        if (now != asked.Before)
        {
            return Error.Rule("approval.stale",
                $"The allergy to {allergy.Allergen} changed after this was asked for (now {AllergyChangeRule.Describe(asked.Before, now)}), so nothing was changed. Ask again.");
        }

        var changed = allergy.ChangeStatus(asked.Asked.Status, asked.Asked.Status == AllergyStatus.Active ? allergy.StatusReason : asked.StatusReason ?? request.Reason);
        if (changed.IsFailure)
        {
            return changed.Error!;
        }

        allergy.Severity = asked.Asked.Severity;
        var words = AllergyChangeRule.Describe(asked.Before, asked.Asked);
        Audited(request, asked.PatientId, nameof(Allergy), allergy.Id, "allergy.changed_with_approval", $"Allergy to {allergy.Allergen}: {words}");
        await realtime.PublishAsync(Topics.Patient(allergy.PatientId), "clinical.flags.changed", new { allergy.Id }, ct);
        return words;
    }

    internal async Task<Result<string>> ApplyDnrLiftAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<DnrLiftPayload>(request.Payload);
        var alert = await db.Set<PatientAlert>().FirstOrDefaultAsync(a => a.Id == asked.AlertId, ct);
        if (alert is null || !alert.IsActive)
        {
            return Error.Rule("approval.stale", "The DNR order is no longer active, so nothing was changed.");
        }

        var lifted = alert.Deactivate($"{asked.Reason} (agreed by {Approver})", request.RequestedBy, time.GetUtcNow());
        if (lifted.IsFailure)
        {
            return lifted.Error!;
        }

        Audited(request, asked.PatientId, nameof(PatientAlert), alert.Id, "alert.dnr_lifted", "Do Not Resuscitate taken off");
        await realtime.PublishAsync(Topics.Patient(alert.PatientId), "clinical.flags.changed", new { alert.Id }, ct);
        return "DNR taken off";
    }

    private void Audited(ApprovalRequest request, Guid patientId, string entityType, Guid entityId, string action, string summary) =>
        audit.Add(new AuditEntry(AuditCategories.Clinical, action, Fit($"{summary}: {request.Reason} (asked for by {request.RequestedByName}, approved by {Approver})", 1000))
        {
            PatientId = patientId,
            EntityType = entityType,
            EntityId = entityId.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { approval = request.Id, requestedBy = request.RequestedBy, approvedBy = current.UserId },
        });

    private async Task<Patient> PatientAsync(Guid id, CancellationToken ct) =>
        await db.Set<Patient>().AsNoTracking().FirstAsync(p => p.Id == id, ct);

    private string Approver => current.DisplayName ?? current.UserName ?? "a second clinician";

    private static string Label(Patient patient) => $"{patient.Uhid} · {(patient.IsVip ? PatientNames.Restricted(patient.FullName) : patient.FullName)}";

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>An allergy taken out of the checks or lowered: decided by a prescriber who did not ask.</summary>
internal sealed class AllergyChangeApproval(PatientFlagApprovals flags) : IApprovalAction
{
    public string Kind => PatientFlagApprovals.AllergyKind;

    public string Title => "Allergy removed or lowered";

    public string ApprovePermission => ClinicalPermissions.PrescriptionsSign;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => flags.ApplyAllergyAsync(request, ct);
}

/// <summary>A DNR order taken off: decided by a second doctor.</summary>
internal sealed class DnrLiftApproval(PatientFlagApprovals flags) : IApprovalAction
{
    public string Kind => PatientFlagApprovals.DnrKind;

    public string Title => "Do Not Resuscitate taken off";

    public string ApprovePermission => ClinicalPermissions.EncountersSign;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => flags.ApplyDnrLiftAsync(request, ct);
}

/// <summary>Whether a patient's death is certified, for the Patients module (a death certificate not entered in error).</summary>
internal sealed class DeathCertificateLookup(HospitalDbContext db) : IDeathCertificateLookup
{
    public Task<string?> CertifiedDeathAsync(Guid patientId, CancellationToken ct) =>
        db.Set<DeathRecord>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(d => d.PatientId == patientId && d.Status != VitalReportStatus.EnteredInError)
            .Select(d => (string?)d.SerialNumber)
            .FirstOrDefaultAsync(ct);
}
