using System.Globalization;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Privacy.Services;

/// <summary>
/// The Data Protection Officer (DPDP Act 2023 s.10(2)(a)): based in India, responsible to the board of directors, and the
/// point of contact for grievances; published on the privacy notice (Rules 2025, rule 9).
/// </summary>
public sealed record DpoContact(string? Name, string? Designation, string? Email, string? Phone, string? Address, bool BasedInIndia)
{
    public bool IsComplete => Name is not null && Email is not null && Phone is not null && BasedInIndia;
}

/// <summary>
/// Whether the Central Government has notified the hospital as a Significant Data Fiduciary (s.10(1)), from when, and its
/// Data Protection Officer. <paramref name="Version"/>: none until first saved.
/// </summary>
public sealed record SdfSettings(bool Designated, DateOnly? NotifiedOn, DpoContact Dpo, uint? Version = null);

public sealed record DpiaListItem(Guid Id, string Number, string Activity, DpiaStatus Status, RiskLevel? HighestRisk, DateOnly ReviewDue, bool ReviewOverdue, int DaysToReview,
    DateTimeOffset? ApprovedAt, string? ApprovedByName, DateTimeOffset? AwaitingApprovalSince);

public sealed record DpiaDetail(Guid Id, string Number, string Activity, string Description, IReadOnlyList<string> DataCategories, string DataPrincipals, string Purpose,
    IReadOnlyList<DpiaRisk> Risks, IReadOnlyList<DpiaMitigation> Mitigations, DateOnly ReviewDue, DpiaStatus Status, RiskLevel? HighestRisk,
    Guid? ApprovedBy, string? ApprovedByName, DateTimeOffset? ApprovedAt, DateTimeOffset? AwaitingApprovalSince, uint Version);

public sealed record SaveDpiaRequest(string Activity, string Description, IReadOnlyList<string> DataCategories, string DataPrincipals, string Purpose,
    IReadOnlyList<DpiaRisk> Risks, IReadOnlyList<DpiaMitigation> Mitigations, DateOnly ReviewDue, uint? Version = null);

public sealed record SubmitDpiaRequest(string Reason, uint Version);

public sealed record RetireDpiaRequest(string Reason, uint Version);

public sealed record DataAuditDto(Guid Id, DateOnly PeriodFrom, DateOnly PeriodTo, string Auditor, string Scope, string? Findings, string? SignificantObservations,
    DateOnly? CompletedOn, DateOnly? ReportedToBoardOn, string? ReportReference, DateOnly NextDue, bool Overdue, uint Version);

public sealed record SaveDataAuditRequest(DateOnly PeriodFrom, DateOnly PeriodTo, string Auditor, string Scope, string? Findings, string? SignificantObservations,
    DateOnly? CompletedOn, DateOnly? ReportedToBoardOn, string? ReportReference, DateOnly? NextDue, uint? Version = null);

/// <summary>The Significant Data Fiduciary page: the designation, the DPO, and what is due.</summary>
public sealed record SdfOverview(SdfSettings Settings, int Dpias, int DpiasDueSoon, int DpiasOverdue, DateOnly? NextAuditDue, bool AuditOverdue);

public sealed class SdfSettingsValidator : AbstractValidator<SdfSettings>
{
    public SdfSettingsValidator()
    {
        RuleFor(x => x.Dpo.Name).SafeText(200);
        RuleFor(x => x.Dpo.Designation).SafeText(100);
        RuleFor(x => x.Dpo.Email).MaximumLength(200).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Dpo.Email));
        RuleFor(x => x.Dpo.Phone).PhoneNumber();
        RuleFor(x => x.Dpo.Address).SafeText(500);
        RuleFor(x => x.Dpo).Must(d => d.IsComplete).When(x => x.Designated)
            .WithMessage("A Significant Data Fiduciary names a Data Protection Officer based in India, with an e-mail address and a phone number (s.10(2)(a)).");
    }
}

public sealed class SaveDpiaRequestValidator : AbstractValidator<SaveDpiaRequest>
{
    public SaveDpiaRequestValidator()
    {
        RuleFor(x => x.Activity).NotEmpty().WithMessage("Name the processing activity.").SafeText(200);
        RuleFor(x => x.Description).NotEmpty().SafeText(4000);
        RuleFor(x => x.DataCategories).NotEmpty().WithMessage("List the personal data it processes.").Must(c => c.Count <= 30);
        RuleForEach(x => x.DataCategories).NotEmpty().SafeText(100);
        RuleFor(x => x.DataPrincipals).NotEmpty().WithMessage("Say whose data it is.").SafeText(300);
        RuleFor(x => x.Purpose).NotEmpty().SafeText(1000);
        RuleFor(x => x.Risks).NotEmpty().WithMessage("Assess at least one risk to the people whose data it is.").Must(r => r.Count <= 30);
        RuleForEach(x => x.Risks).ChildRules(r =>
        {
            r.RuleFor(x => x.Description).NotEmpty().SafeText(1000);
            r.RuleFor(x => x.Likelihood).IsInEnum();
            r.RuleFor(x => x.Impact).IsInEnum();
        });
        RuleFor(x => x.Mitigations).NotEmpty().WithMessage("Record what is done about the risks.").Must(m => m.Count <= 30);
        RuleForEach(x => x.Mitigations).ChildRules(m =>
        {
            m.RuleFor(x => x.Description).NotEmpty().SafeText(1000);
            m.RuleFor(x => x.Owner).SafeText(200);
        });
    }
}

public sealed class SubmitDpiaRequestValidator : AbstractValidator<SubmitDpiaRequest>
{
    public SubmitDpiaRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Say what the approver should know.").SafeText(500);
        RuleFor(x => x.Version).SeenVersion();
    }
}

public sealed class RetireDpiaRequestValidator : AbstractValidator<RetireDpiaRequest>
{
    public RetireDpiaRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().SafeText(500);
        RuleFor(x => x.Version).SeenVersion();
    }
}

public sealed class SaveDataAuditRequestValidator : AbstractValidator<SaveDataAuditRequest>
{
    public SaveDataAuditRequestValidator()
    {
        RuleFor(x => x.PeriodTo).GreaterThanOrEqualTo(x => x.PeriodFrom).WithMessage("The period ends on or after it starts.");
        RuleFor(x => x.Auditor).NotEmpty().WithMessage("Name the independent data auditor.").SafeText(200);
        RuleFor(x => x.Scope).NotEmpty().SafeText(2000);
        RuleFor(x => x.Findings).SafeText(4000);
        RuleFor(x => x.SignificantObservations).SafeText(4000);
        RuleFor(x => x.ReportReference).SafeText(100);
        RuleFor(x => x.CompletedOn).GreaterThanOrEqualTo(x => x.PeriodFrom).When(x => x.CompletedOn is not null);
        RuleFor(x => x.ReportedToBoardOn).NotNull().WithMessage("Say when the significant observations were reported to the Board (rule 13).")
            .When(x => !string.IsNullOrWhiteSpace(x.SignificantObservations) && x.CompletedOn is not null);
    }
}

/// <summary>
/// The duties of a Significant Data Fiduciary (DPDP Act 2023 s.10; DPDP Rules 2025, rule 13): the Data Protection Officer,
/// the register of Data Protection Impact Assessments (each approved by someone other than whoever wrote it), and the
/// yearly data audit, with reminders before each review date. Kept whether or not the hospital has been notified yet:
/// a DPIA is good practice for a hospital either way, and the designation only changes what System health insists on.
/// </summary>
public sealed class SdfService(
    HospitalDbContext db,
    ISettingsService settings,
    INumberSeries numbers,
    IApprovals approvals,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public const string SettingsKey = "privacy.sdf";
    public const string ApprovalKind = "privacy.dpia";

    /// <summary>The latest review date allowed: twelve months after the day it is set (rule 13).</summary>
    public const int MaxReviewMonths = 12;

    public async Task<SdfSettings> SettingsAsync(CancellationToken ct)
    {
        var json = await settings.GetAsync<JsonElement?>(SettingsKey, null, ct);
        var version = await SettingVersions.OfAsync(db, SettingsKey, null, ct);
        if (json is not { ValueKind: JsonValueKind.Object } value)
        {
            return new SdfSettings(false, null, new DpoContact(null, null, null, null, null, false), version);
        }

        var dpo = value.TryGetProperty("dpo", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
        return new SdfSettings(
            value.TryGetProperty("designated", out var designated) && designated.ValueKind == JsonValueKind.True,
            value.TryGetProperty("notifiedOn", out var notified) && notified.ValueKind == JsonValueKind.String && DateOnly.TryParse(notified.GetString(), CultureInfo.InvariantCulture, out var on) ? on : null,
            new DpoContact(Text(dpo, "name"), Text(dpo, "designation"), Text(dpo, "email"), Text(dpo, "phone"), Text(dpo, "address"),
                dpo.ValueKind == JsonValueKind.Object && dpo.TryGetProperty("basedInIndia", out var india) && india.ValueKind == JsonValueKind.True),
            version);
    }

    public async Task SaveSettingsAsync(SdfSettings value, CancellationToken ct)
    {
        await settings.SetAsync(SettingsKey, new
        {
            designated = value.Designated,
            notifiedOn = value.NotifiedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            dpo = new
            {
                name = Clean(value.Dpo.Name),
                designation = Clean(value.Dpo.Designation),
                email = Clean(value.Dpo.Email),
                phone = Phone.Normalize(value.Dpo.Phone),
                address = Clean(value.Dpo.Address),
                basedInIndia = value.Dpo.BasedInIndia,
            },
        }, null, false, value.Version, ct);
    }

    /// <summary>The DPO as the privacy notice shows them: only once named in full.</summary>
    public async Task<DpoContact?> PublishedDpoAsync(CancellationToken ct) => (await SettingsAsync(ct)).Dpo is { Name: not null } dpo && (dpo.Email ?? dpo.Phone) is not null ? dpo : null;

    public async Task<SdfOverview> OverviewAsync(CancellationToken ct)
    {
        var today = time.Today();
        var soon = today.AddDays(30);
        var open = db.Set<Dpia>().AsNoTracking().Where(d => d.Status != DpiaStatus.Retired);
        var nextAudit = await db.Set<DataAudit>().AsNoTracking().OrderByDescending(a => a.PeriodTo).Select(a => (DateOnly?)a.NextDue).FirstOrDefaultAsync(ct);
        var sdf = await SettingsAsync(ct);
        return new SdfOverview(sdf, await open.CountAsync(ct), await open.CountAsync(d => d.ReviewDue >= today && d.ReviewDue <= soon, ct),
            await open.CountAsync(d => d.ReviewDue < today, ct), nextAudit, nextAudit is { } due ? due < today : sdf.Designated);
    }

    public async Task<IReadOnlyList<DpiaListItem>> ListAsync(bool includeRetired, CancellationToken ct)
    {
        var today = time.Today();
        var rows = await db.Set<Dpia>().AsNoTracking().Where(d => includeRetired || d.Status != DpiaStatus.Retired).OrderBy(d => d.ReviewDue).ToListAsync(ct);
        var waiting = await approvals.WaitingSinceAsync(ApprovalKind, nameof(Dpia), [.. rows.Select(r => r.Id)], ct);
        return [.. rows.Select(d => new DpiaListItem(d.Id, d.Number, d.Activity, d.Status, d.HighestRisk, d.ReviewDue, d.Status != DpiaStatus.Retired && d.ReviewDue < today,
            d.ReviewDue.DayNumber - today.DayNumber, d.ApprovedAt, d.ApprovedByName, waiting.TryGetValue(d.Id, out var since) ? since : null))];
    }

    public async Task<DpiaDetail?> DetailAsync(Guid id, CancellationToken ct)
    {
        var d = await db.Set<Dpia>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null)
        {
            return null;
        }

        var waiting = await approvals.WaitingSinceAsync(ApprovalKind, nameof(Dpia), [id], ct);
        return Detail(d, waiting.TryGetValue(id, out var since) ? since : null);
    }

    public async Task<Result<DpiaDetail>> CreateAsync(SaveDpiaRequest request, CancellationToken ct)
    {
        if (ReviewDateProblem(request.ReviewDue) is { } problem)
        {
            return problem;
        }

        var dpia = new Dpia { Number = await numbers.NextAsync(PrivacyNumberSeries.Dpia, null, ct), Status = DpiaStatus.Draft };
        Apply(dpia, request);
        db.Add(dpia);
        audit.Add(new AuditEntry(AuditCategories.Admin, "privacy.dpia_created", $"DPIA {dpia.Number} written: {dpia.Activity}")
        {
            EntityType = nameof(Dpia),
            EntityId = dpia.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        return Detail(dpia, null);
    }

    /// <summary>
    /// Changes a DPIA. An approved one becomes a draft again (its approval covered what it said before), to be sent for
    /// approval anew; a change waiting for approval makes that approval stale.
    /// </summary>
    public async Task<Result<DpiaDetail>> UpdateAsync(Guid id, SaveDpiaRequest request, CancellationToken ct)
    {
        var dpia = await db.Set<Dpia>().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (dpia is null)
        {
            return Error.NotFound(nameof(Dpia), id);
        }

        if (dpia.Status == DpiaStatus.Retired)
        {
            return Error.Rule("privacy.dpia_retired", "This DPIA is retired: write a new one for the processing.");
        }

        if (ReviewDateProblem(request.ReviewDue) is { } problem)
        {
            return problem;
        }

        db.Entry(dpia).Property(d => d.Version).OriginalValue = request.Version ?? dpia.Version;
        var wasApproved = dpia.Status == DpiaStatus.Approved;
        Apply(dpia, request);
        dpia.Status = DpiaStatus.Draft;
        dpia.ApprovedAt = null;
        dpia.ApprovedBy = null;
        dpia.ApprovedByName = null;
        dpia.RemindedDaysBefore = null;
        audit.Add(new AuditEntry(AuditCategories.Admin, "privacy.dpia_changed",
            $"DPIA {dpia.Number} changed{(wasApproved ? ": it is a draft again until approved" : string.Empty)}")
        {
            EntityType = nameof(Dpia),
            EntityId = dpia.Id.ToString(),
            Severity = wasApproved ? AuditSeverity.Warning : AuditSeverity.Info,
        });
        await db.SaveChangesAsync(ct);
        return Detail(dpia, null);
    }

    /// <summary>Sends a draft for approval (202): someone else who holds <see cref="PrivacyPermissions.SdfApprove"/> approves it as it is now.</summary>
    public async Task<Result<ApprovalRequestDto>> SubmitAsync(Guid id, SubmitDpiaRequest request, CancellationToken ct)
    {
        var dpia = await db.Set<Dpia>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (dpia is null)
        {
            return Error.NotFound(nameof(Dpia), id);
        }

        if (request.Version != dpia.Version)
        {
            return Error.ConcurrencyConflict;
        }

        if (dpia.Status != DpiaStatus.Draft)
        {
            return Error.Rule("privacy.dpia_not_draft", dpia.Status == DpiaStatus.Approved ? "This DPIA is already approved." : "This DPIA is retired.");
        }

        return await approvals.RequestAsync(
            ApprovalKind,
            new ApprovalSubject(nameof(Dpia), dpia.Id, dpia.Version, currentUser.FacilityId ?? Guid.Empty, null),
            new DpiaApprovalPayload(dpia.Id, dpia.Version),
            $"DPIA {dpia.Number}: {dpia.Activity}",
            [
                new("Processing", dpia.Activity),
                new("Data", string.Join(", ", dpia.DataCategories)),
                new("Whose data", dpia.DataPrincipals),
                new("Highest risk", dpia.HighestRisk?.ToString() ?? "—"),
                new("Risks", string.Create(CultureInfo.InvariantCulture, $"{dpia.Risks.Count} assessed, {dpia.Mitigations.Count} measure(s)")),
                new("Review by", dpia.ReviewDue.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)),
            ],
            request.Reason.Trim(),
            ct);
    }

    internal async Task<Result<string>> ApplyApprovalAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<DpiaApprovalPayload>(request.Payload);
        var dpia = await db.Set<Dpia>().FirstOrDefaultAsync(d => d.Id == asked.DpiaId, ct);
        if (dpia is null)
        {
            return Error.Rule("privacy.dpia_gone", "The DPIA no longer exists, so nothing was approved.");
        }

        if (dpia.Version != asked.Version || dpia.Status != DpiaStatus.Draft)
        {
            return Error.Rule("approval.stale", $"DPIA {dpia.Number} changed after it was sent for approval, so nothing was approved. Send it again.");
        }

        if (dpia.ReviewDue < time.Today())
        {
            return Error.Rule("privacy.dpia_review_passed", $"DPIA {dpia.Number}'s review date has passed: set a new one and send it again.");
        }

        dpia.Status = DpiaStatus.Approved;
        dpia.ApprovedAt = time.GetUtcNow();
        dpia.ApprovedBy = currentUser.UserId;
        dpia.ApprovedByName = currentUser.DisplayName ?? currentUser.UserName;
        dpia.RemindedDaysBefore = null;
        audit.Add(new AuditEntry(AuditCategories.Admin, "privacy.dpia_approved",
            $"DPIA {dpia.Number} approved by {dpia.ApprovedByName} (asked for by {request.RequestedByName}), to be reviewed by {dpia.ReviewDue:yyyy-MM-dd}")
        {
            EntityType = nameof(Dpia),
            EntityId = dpia.Id.ToString(),
            Details = new { approval = request.Id, requestedBy = request.RequestedBy, approvedBy = currentUser.UserId },
        });
        return dpia.Number;
    }

    public async Task<Result> RetireAsync(Guid id, RetireDpiaRequest request, CancellationToken ct)
    {
        var dpia = await db.Set<Dpia>().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (dpia is null)
        {
            return Error.NotFound(nameof(Dpia), id);
        }

        db.Entry(dpia).Property(d => d.Version).OriginalValue = request.Version;
        dpia.Status = DpiaStatus.Retired;
        audit.Add(new AuditEntry(AuditCategories.Admin, "privacy.dpia_retired", $"DPIA {dpia.Number} retired: {request.Reason.Trim()}")
        {
            EntityType = nameof(Dpia),
            EntityId = dpia.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<IReadOnlyList<DataAuditDto>> AuditsAsync(CancellationToken ct)
    {
        var today = time.Today();
        var rows = await db.Set<DataAudit>().AsNoTracking().OrderByDescending(a => a.PeriodTo).ToListAsync(ct);
        var latest = rows.FirstOrDefault()?.Id;
        return [.. rows.Select(a => AuditDto(a, a.Id == latest && a.NextDue < today))];
    }

    public async Task<Result<DataAuditDto>> SaveAuditAsync(Guid? id, SaveDataAuditRequest request, CancellationToken ct)
    {
        DataAudit? entity;
        if (id is { } existing)
        {
            entity = await db.Set<DataAudit>().FirstOrDefaultAsync(a => a.Id == existing, ct);
            if (entity is null)
            {
                return Error.NotFound(nameof(DataAudit), existing);
            }

            db.Entry(entity).Property(a => a.Version).OriginalValue = request.Version ?? entity.Version;
        }
        else
        {
            entity = new DataAudit();
            db.Add(entity);
        }

        var latestNextDue = request.PeriodTo.AddMonths(MaxReviewMonths);
        if (request.NextDue is { } next && next > latestNextDue)
        {
            return Error.Validation("privacy.audit_next_due", "The next audit is due at most twelve months after this one's period ends (rule 13).");
        }

        entity.PeriodFrom = request.PeriodFrom;
        entity.PeriodTo = request.PeriodTo;
        entity.Auditor = request.Auditor.Trim();
        entity.Scope = request.Scope.Trim();
        entity.Findings = Clean(request.Findings);
        entity.SignificantObservations = Clean(request.SignificantObservations);
        entity.CompletedOn = request.CompletedOn;
        entity.ReportedToBoardOn = request.ReportedToBoardOn;
        entity.ReportReference = Clean(request.ReportReference);
        entity.NextDue = request.NextDue ?? latestNextDue;
        entity.RemindedDaysBefore = null;
        audit.Add(new AuditEntry(AuditCategories.Admin, "privacy.data_audit_saved",
            $"Data audit {entity.PeriodFrom:yyyy-MM-dd} to {entity.PeriodTo:yyyy-MM-dd} by {entity.Auditor} recorded; next due {entity.NextDue:yyyy-MM-dd}")
        {
            EntityType = nameof(DataAudit),
            EntityId = entity.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        return AuditDto(entity, entity.NextDue < time.Today());
    }

    /// <summary>
    /// Reminds the holders of <see cref="PrivacyPermissions.SdfManage"/> 30 and 7 days before a DPIA's review date or the
    /// next data audit, and once more when it is overdue. Each reminder is sent once.
    /// </summary>
    public async Task<int> RemindAsync(INotificationService notifications, CancellationToken ct)
    {
        var today = time.Today();
        var sent = 0;
        foreach (var dpia in await db.Set<Dpia>().Where(d => d.Status != DpiaStatus.Retired && d.ReviewDue <= today.AddDays(30)).ToListAsync(ct))
        {
            if (Due(dpia.ReviewDue, today, dpia.RemindedDaysBefore) is { } threshold)
            {
                dpia.RemindedDaysBefore = threshold;
                await notifications.NotifyPermissionHoldersAsync(PrivacyPermissions.SdfManage, null,
                    threshold == 0 ? $"DPIA {dpia.Number} is overdue for review" : $"DPIA {dpia.Number} is due for review by {dpia.ReviewDue:dd MMM yyyy}",
                    $"{dpia.Activity}. Review the risks and measures, and send it for approval again (DPDP Rules 2025, rule 13).",
                    "/privacy/sdf", "privacy", threshold == 0 ? "warning" : "info", ct);
                sent++;
            }
        }

        var audit = await db.Set<DataAudit>().OrderByDescending(a => a.PeriodTo).FirstOrDefaultAsync(ct);
        if (audit is not null && Due(audit.NextDue, today, audit.RemindedDaysBefore) is { } auditThreshold)
        {
            audit.RemindedDaysBefore = auditThreshold;
            await notifications.NotifyPermissionHoldersAsync(PrivacyPermissions.SdfManage, null,
                auditThreshold == 0 ? "The yearly data audit is overdue" : $"The yearly data audit is due by {audit.NextDue:dd MMM yyyy}",
                "Arrange the independent data audit and record it under Privacy › Significant data fiduciary (DPDP Act s.10(2)(b)).",
                "/privacy/sdf", "privacy", auditThreshold == 0 ? "warning" : "info", ct);
            sent++;
        }

        await db.SaveChangesAsync(ct);
        return sent;
    }

    /// <summary>The reminder due for a date: 30 or 7 days before, or 0 once past; null when none is due or it was already sent.</summary>
    internal static int? Due(DateOnly due, DateOnly today, int? alreadySent)
    {
        var days = due.DayNumber - today.DayNumber;
        int? threshold = days < 0 ? 0 : days <= 7 ? 7 : days <= 30 ? 30 : null;
        return threshold is { } t && (alreadySent is null || t < alreadySent) ? t : null;
    }

    private Error? ReviewDateProblem(DateOnly reviewDue)
    {
        var today = time.Today();
        if (reviewDue <= today)
        {
            return Error.Validation("privacy.dpia_review_due", "Set a review date after today.");
        }

        return reviewDue > today.AddMonths(MaxReviewMonths)
            ? Error.Validation("privacy.dpia_review_due", "Review it within twelve months: a Significant Data Fiduciary assesses its processing every twelve months (rule 13).")
            : null;
    }

    private static void Apply(Dpia dpia, SaveDpiaRequest request)
    {
        dpia.Activity = request.Activity.Trim();
        dpia.Description = request.Description.Trim();
        dpia.DataCategories = [.. request.DataCategories.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
        dpia.DataPrincipals = request.DataPrincipals.Trim();
        dpia.Purpose = request.Purpose.Trim();
        dpia.Risks = [.. request.Risks.Select(r => r with { Description = r.Description.Trim() })];
        dpia.Mitigations = [.. request.Mitigations.Select(m => m with { Description = m.Description.Trim(), Owner = Clean(m.Owner) })];
        dpia.ReviewDue = request.ReviewDue;
    }

    private static DpiaDetail Detail(Dpia d, DateTimeOffset? waiting) => new(d.Id, d.Number, d.Activity, d.Description, d.DataCategories, d.DataPrincipals, d.Purpose,
        d.Risks, d.Mitigations, d.ReviewDue, d.Status, d.HighestRisk, d.ApprovedBy, d.ApprovedByName, d.ApprovedAt, waiting, d.Version);

    private static DataAuditDto AuditDto(DataAudit a, bool overdue) => new(a.Id, a.PeriodFrom, a.PeriodTo, a.Auditor, a.Scope, a.Findings, a.SignificantObservations,
        a.CompletedOn, a.ReportedToBoardOn, a.ReportReference, a.NextDue, overdue, a.Version);

    private static string? Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? Clean(p.GetString()) : null;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>A DPIA as sent for approval: approved only while it is still that version.</summary>
public sealed record DpiaApprovalPayload(Guid DpiaId, uint Version);

/// <summary>A DPIA approved by someone other than whoever sent it (the approvals framework refuses the maker: approval.self).</summary>
internal sealed class DpiaApproval(SdfService sdf) : IApprovalAction
{
    public string Kind => SdfService.ApprovalKind;

    public string Title => "Data protection impact assessment";

    public string ApprovePermission => PrivacyPermissions.SdfApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => sdf.ApplyApprovalAsync(request, ct);
}

/// <summary>System health: the DPO, overdue DPIAs and the data audit, for a hospital notified as a Significant Data Fiduciary.</summary>
internal sealed class SdfHealthWarnings(SdfService sdf) : IHealthWarningSource
{
    public async Task<IReadOnlyList<HealthWarning>> WarningsAsync(CancellationToken cancellationToken)
    {
        var overview = await sdf.OverviewAsync(cancellationToken);
        if (!overview.Settings.Designated)
        {
            return [];
        }

        var warnings = new List<HealthWarning>();
        if (!overview.Settings.Dpo.IsComplete)
        {
            warnings.Add(new HealthWarning("privacy.dpo_missing", "warning",
                "The hospital is a Significant Data Fiduciary but has not named a Data Protection Officer based in India. Name one under Privacy › Significant data fiduciary."));
        }

        if (overview.DpiasOverdue > 0)
        {
            warnings.Add(new HealthWarning("privacy.dpia_overdue", "warning",
                string.Create(CultureInfo.InvariantCulture, $"{overview.DpiasOverdue} data protection impact assessment(s) are past their review date.")));
        }

        if (overview.AuditOverdue)
        {
            warnings.Add(new HealthWarning("privacy.data_audit_due", "warning",
                overview.NextAuditDue is null ? "No yearly data audit is recorded yet." : "The yearly data audit is overdue."));
        }

        return warnings;
    }
}

/// <summary>Sends the review reminders once a day, after 09:00 IST, on the nodes that run jobs.</summary>
internal sealed class SdfReminderJob(IServiceScopeFactory scopeFactory, IOptions<PlatformOptions> options, TimeProvider time, ILogger<SdfReminderJob> logger) : BackgroundService
{
    internal const string LastRunSetting = "privacy.sdf.reminders_last_run";
    internal const long LockKey = 4_247_603;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15), time);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The privacy review reminders failed; they will be tried again");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        await using (var sweep = scopeFactory.CreateAsyncScope())
        {
            // Consent artefacts past their validity, consents withdrawn at the desk, and withdrawals to send again: every round.
            await sweep.ServiceProvider.GetRequiredService<ConsentManagerService>().SweepAsync(ct);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var now = PatientQueries.IndiaTime(time);
        var today = DateOnly.FromDateTime(now.DateTime);
        if (now.Hour < 9 || await settings.GetAsync<DateOnly?>(LastRunSetting, null, ct) == today)
        {
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({LockKey}) AS \"Value\"").SingleAsync(ct))
        {
            return;
        }

        var sent = await scope.ServiceProvider.GetRequiredService<SdfService>().RemindAsync(scope.ServiceProvider.GetRequiredService<INotificationService>(), ct);
        await settings.SetAsync(LastRunSetting, today, null, false, ct);
        await transaction.CommitAsync(ct);
        if (sent > 0)
        {
            logger.LogInformation("Sent {Count} privacy review reminder(s)", sent);
        }
    }
}
