using HyFib.HealthCare360.Modules.Clinical.Domain.Stewardship;
using HyFib.HealthCare360.Modules.Clinical.Features.Stewardship;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// Antimicrobial stewardship: the restricted antibiotics on signed prescriptions, the team's approval, and the review of
/// each course at 72 hours or when the culture comes back. The list is the WHO AWaRe classification with the hospital's
/// policy (<see cref="Aware"/>); <c>ams.restricted.extra</c> adds antibiotics to it.
/// </summary>
internal sealed class StewardshipService(HospitalDbContext db, PatientAccessGuard guard, ICurrentUser currentUser, TimeProvider time)
{
    public const string ExtraRestrictedSetting = "ams.restricted.extra";
    public const string AlertType = "clinical.ams-alert";
    private const string Restricted = "Restricted";

    public Task<AntimicrobialApproval?> FindAsync(Guid id, CancellationToken ct) =>
        db.Set<AntimicrobialApproval>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);

    /// <summary>The queue: waiting for a decision, due for review, or all of the last 30 days; <paramref name="mine"/> keeps the user's own prescriptions.</summary>
    public async Task<IReadOnlyList<AntimicrobialApprovalDto>> ListAsync(ApprovalQueue queue, bool mine, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var query = db.Set<AntimicrobialApproval>().AsNoTracking();
        if (mine)
        {
            var me = currentUser.UserId;
            query = query.Where(a => a.PrescribedByUserId == me);
        }

        query = queue switch
        {
            ApprovalQueue.Pending => query.Where(a => a.Status == ApprovalStatus.Pending),
            ApprovalQueue.ReviewDue => query.Where(a => a.Status != ApprovalStatus.Rejected && a.ReviewDecision == null
                                                        && (a.CultureReleasedAt != null || a.PrescribedAt <= now - AntimicrobialApproval.ReviewAt)),
            _ => query.Where(a => a.PrescribedAt >= now.AddDays(-30)),
        };
        var approvals = await query.OrderBy(a => a.PrescribedAt).Take(500).ToListAsync(ct);
        return await DtosAsync(approvals, ct);
    }

    public async Task<AntimicrobialApprovalDto> DtoAsync(AntimicrobialApproval approval, CancellationToken ct) => (await DtosAsync([approval], ct))[0];

    public async Task<Result<AntimicrobialApprovalDto>> JustifyAsync(Guid id, JustifyRequest request, CancellationToken ct)
    {
        var approval = await db.Set<AntimicrobialApproval>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (approval is null)
        {
            return Error.NotFound("Antibiotic approval", id);
        }

        if (approval.PrescribedByUserId != currentUser.UserId)
        {
            return Error.Forbidden("ams.not_prescriber", "The prescribing doctor gives the justification.");
        }

        db.SetExpectedVersion(approval, request.Version);
        var justified = approval.Justify(request.Site, request.Indication, request.Empirical, request.CultureSent);
        return justified.IsFailure ? justified.Error! : await SaveAsync(approval, ct);
    }

    public async Task<Result<AntimicrobialApprovalDto>> DecideAsync(Guid id, DecideRequest request, CancellationToken ct)
    {
        var approval = await db.Set<AntimicrobialApproval>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (approval is null)
        {
            return Error.NotFound("Antibiotic approval", id);
        }

        db.SetExpectedVersion(approval, request.Version);
        var decided = approval.Decide(request.Approve, request.Days, request.Notes, currentUser.UserId, Me, time.GetUtcNow());
        return decided.IsFailure ? decided.Error! : await SaveAsync(approval, ct);
    }

    /// <summary>The review of a course: by the stewardship team, or by the prescriber.</summary>
    public async Task<Result<AntimicrobialApprovalDto>> ReviewAsync(Guid id, ReviewRequest request, bool steward, CancellationToken ct)
    {
        var approval = await db.Set<AntimicrobialApproval>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (approval is null)
        {
            return Error.NotFound("Antibiotic approval", id);
        }

        if (!steward && approval.PrescribedByUserId != currentUser.UserId)
        {
            return Error.Forbidden("ams.not_prescriber", "The prescribing doctor or the stewardship team reviews the course.");
        }

        db.SetExpectedVersion(approval, request.Version);
        var reviewed = approval.Review(request.Decision, request.Notes, Me, time.GetUtcNow());
        return reviewed.IsFailure ? reviewed.Error! : await SaveAsync(approval, ct);
    }

    public async Task<StewardshipReport> ReportAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5));
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5));
        var approvals = await db.Set<AntimicrobialApproval>().AsNoTracking().Where(a => a.PrescribedAt >= start && a.PrescribedAt < end).ToListAsync(ct);
        var now = time.GetUtcNow();
        var hours = approvals.Where(a => a.DecidedAt is not null).Select(a => (a.DecidedAt!.Value - a.PrescribedAt).TotalHours).Order().ToList();
        var justified = approvals.Where(a => a.CultureSent is not null).ToList();
        return new StewardshipReport(
            from, to,
            [.. Enum.GetValues<AwareGroup>().Select(g => approvals.Where(a => a.Group == g).ToList())
                .Select((list, i) => new StewardshipGroupCount((AwareGroup)i, list.Count, list.Count(a => a.Status == ApprovalStatus.Approved),
                    list.Count(a => a.Status == ApprovalStatus.Rejected), list.Count(a => a.Status == ApprovalStatus.Pending)))],
            hours.Count == 0 ? null : Math.Round(hours.Count % 2 == 1 ? hours[hours.Count / 2] : (hours[(hours.Count / 2) - 1] + hours[hours.Count / 2]) / 2, 1),
            approvals.Count(a => (a.DecidedAt ?? now) > a.ApprovalDueBy),
            justified.Count == 0 ? null : (int)Math.Round(100.0 * justified.Count(a => a.CultureSent == true) / justified.Count),
            approvals.Count(a => a.ReviewDecision is not null),
            Enum.GetValues<StewardshipDecision>().ToDictionary(d => d, d => approvals.Count(a => a.ReviewDecision == d)));
    }

    private string Me => currentUser.DisplayName ?? currentUser.UserName ?? "Staff";

    private async Task<Result<AntimicrobialApprovalDto>> SaveAsync(AntimicrobialApproval approval, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        return await DtoAsync(approval, ct);
    }

    /// <summary>The approvals with their patients; a restricted patient the user may not open shows initials.</summary>
    private async Task<List<AntimicrobialApprovalDto>> DtosAsync(IReadOnlyCollection<AntimicrobialApproval> approvals, CancellationToken ct)
    {
        var ids = approvals.Select(a => a.PatientId).Distinct().ToList();
        var patients = await db.Set<Patient>().AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var vip = patients.Values.Where(p => p.IsVip).Select(p => p.Id).ToList();
        var openable = vip.Count == 0 ? new HashSet<Guid>() : (await guard.OpenableAsync(vip, ct)).ToHashSet();
        var now = time.GetUtcNow();
        return approvals.Select(a =>
        {
            var p = patients.GetValueOrDefault(a.PatientId);
            var hidden = p is { IsVip: true } && !openable.Contains(p.Id);
            return new AntimicrobialApprovalDto(
                a.Id, a.PatientId, hidden ? PatientNames.Restricted(p!.FullName) : p?.FullName ?? string.Empty, hidden ? Restricted : p?.Uhid ?? string.Empty,
                a.PrescriptionNumber, a.AdmissionId is not null, a.DrugName, a.Antimicrobial, a.Group, a.Dose, a.Route, a.DurationDays, a.PrescribedByName,
                a.PrescribedAt, a.Site, a.Indication, a.Empirical, a.CultureSent, a.Status, a.ApprovalDueBy, a.ApprovalOverdue(now), a.DecidedByName,
                a.DecidedAt, a.ApprovedDays, a.DecisionNotes, a.Organism, a.Susceptibility, a.CultureReleasedAt, a.ReviewDue(now), a.ReviewDecision,
                a.ReviewedByName, a.ReviewedAt, a.ReviewNotes, a.PrescribedByUserId is not null && a.PrescribedByUserId == currentUser.UserId, a.Version);
        }).ToList();
    }
}

/// <summary>
/// A signed prescription with a restricted antibiotic asks the stewardship team for approval, in the prescriber's own
/// transaction, and alerts the team.
/// </summary>
internal sealed class AntimicrobialApprovalOpener(
    HospitalDbContext db, ISettingsService settings, IOutbox outbox, ICurrentUser currentUser) : IDomainEventHandler<PrescriptionSigned>
{
    public async Task HandleAsync(PrescriptionSigned signed, CancellationToken cancellationToken)
    {
        var extra = (await settings.GetAsync<string?>(StewardshipService.ExtraRestrictedSetting, signed.FacilityId, cancellationToken) ?? string.Empty)
            .Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var lineIds = signed.Lines.Select(l => l.LineId).ToList();
        var known = await db.Set<AntimicrobialApproval>().Where(a => lineIds.Contains(a.PrescriptionLineId)).Select(a => a.PrescriptionLineId)
            .ToListAsync(cancellationToken);
        var opened = new List<string>();
        foreach (var line in signed.Lines.Where(l => !known.Contains(l.LineId)))
        {
            if ((Aware.Find(line.GenericName) ?? Aware.Find(line.DrugName)) is not { } antimicrobial || !Aware.IsRestricted(antimicrobial, extra))
            {
                continue;
            }

            db.Add(new AntimicrobialApproval
            {
                FacilityId = signed.FacilityId,
                PatientId = signed.PatientId,
                PrescriptionId = signed.PrescriptionId,
                PrescriptionNumber = signed.PrescriptionNumber,
                PrescriptionLineId = line.LineId,
                AdmissionId = signed.AdmissionId,
                DrugName = line.DrugName,
                Antimicrobial = antimicrobial.Name,
                Group = antimicrobial.Group,
                Dose = line.Dose,
                Route = line.Route,
                DurationDays = line.DurationDays,
                PrescribedByUserId = currentUser.UserId,
                PrescribedByName = currentUser.DisplayName ?? currentUser.UserName ?? "Doctor",
                PrescribedAt = signed.SignedAt,
            });
            opened.Add($"{line.DrugName} ({antimicrobial.Group})");
        }

        if (opened.Count > 0)
        {
            outbox.Enqueue(StewardshipService.AlertType, new StewardshipAlert(
                signed.FacilityId, "Restricted antibiotic waiting for approval",
                $"{string.Join(", ", opened)} on prescription {signed.PrescriptionNumber}: approve or reject within 24 hours.", "/ams"),
                signed.FacilityId, expiresIn: TimeSpan.FromDays(1));
        }
    }
}

/// <summary>
/// A culture result released for a patient on a restricted antibiotic: the course's review falls due, with the isolate
/// and this antibiotic's result on its antibiogram. Called by the laboratory in the release's own transaction.
/// </summary>
public sealed class AntimicrobialCultureReview(HospitalDbContext db, IOutbox outbox)
{
    public async Task CultureReleasedAsync(
        Guid facilityId, Guid patientId, Guid resultId, string organism, IReadOnlyList<(string Antibiotic, string Interpretation)> antibiogram,
        DateTimeOffset releasedAt, CancellationToken ct)
    {
        var running = await db.Set<AntimicrobialApproval>()
            .Where(a => a.PatientId == patientId && a.Status != ApprovalStatus.Rejected && a.ReviewDecision == null
                        && a.PrescribedAt >= releasedAt.AddDays(-30))
            .ToListAsync(ct);
        foreach (var approval in running)
        {
            var antimicrobial = Aware.All.FirstOrDefault(a => a.Name == approval.Antimicrobial);
            approval.CultureReleased(resultId, organism, antimicrobial is null ? null : Aware.Susceptibility(antimicrobial, antibiogram), releasedAt);
        }

        if (running.Count > 0)
        {
            outbox.Enqueue(StewardshipService.AlertType, new StewardshipAlert(
                facilityId, "Culture result for a patient on a restricted antibiotic",
                $"{organism} isolated. Review {string.Join(", ", running.Select(a => a.DrugName).Distinct())}: continue, narrow or stop.", "/ams"),
                facilityId, expiresIn: TimeSpan.FromDays(1));
        }
    }
}

/// <summary>The pharmacy's question before it issues medicines: may these prescription lines go out now?</summary>
internal sealed class AntimicrobialStewardship(HospitalDbContext db, TimeProvider time) : IAntimicrobialStewardship
{
    public async Task<string?> IssueRefusalAsync(IReadOnlyCollection<Guid> prescriptionLineIds, CancellationToken cancellationToken)
    {
        if (prescriptionLineIds.Count == 0)
        {
            return null;
        }

        var approvals = await db.Set<AntimicrobialApproval>().AsNoTracking()
            .Where(a => prescriptionLineIds.Contains(a.PrescriptionLineId) && a.Status != ApprovalStatus.Approved)
            .ToListAsync(cancellationToken);
        var now = time.GetUtcNow();
        return approvals.Select(a => a.IssueRefusal(now)).FirstOrDefault(r => r is not null);
    }
}

internal sealed record StewardshipAlert(Guid FacilityId, string Title, string Body, string Link);

internal sealed class StewardshipAlertOutboxHandler(INotificationService notifications) : IOutboxHandler
{
    public string Type => StewardshipService.AlertType;

    public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var alert = message.Read<StewardshipAlert>();
        return notifications.NotifyPermissionHoldersAsync(
            ClinicalPermissions.AmsApprove, alert.FacilityId, alert.Title, alert.Body, alert.Link, "ams", "warning", cancellationToken);
    }
}
