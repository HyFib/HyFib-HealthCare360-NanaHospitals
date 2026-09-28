using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;
using HyFib.HealthCare360.Platform.Notifications;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Radiology reporting (IMG-03). A radiologist drafts the report, usually from a template, then submits and signs it off
/// (verification). A critical finding needs a documented call before sign-off. Release puts the report in the EHR and
/// the portal and notifies the patient. Corrections after sign-off are signed addenda, which are re-announced to the
/// EHR and pushed to the ordering doctor.
/// </summary>
internal sealed class RadiologyReportService(
    HospitalDbContext db,
    CriticalValueNotifier critical,
    ReportDelivery delivery,
    DiagnosticsLookups lookups,
    DiagnosticsRealtime realtime,
    INotificationService notifications,
    ICurrentUser currentUser,
    TimeProvider time,
    ILogger<RadiologyReportService> logger)
{
    public const string VerificationPathPrefix = "/api/v1/radiology/reports/verify/";

    public async Task<RadiologyReportDto?> GetAsync(Guid studyId, CancellationToken ct)
    {
        var report = await db.Set<RadiologyReport>().AsNoTracking().Include(r => r.Addenda).FirstOrDefaultAsync(r => r.ImagingStudyId == studyId, ct);
        return report is null ? null : await ToDtoAsync(report, ct);
    }

    public async Task<Result<RadiologyReportDto>> SaveDraftAsync(Guid studyId, SaveRadiologyReportRequest request, CancellationToken ct)
    {
        var study = await db.Set<ImagingStudy>().FirstOrDefaultAsync(s => s.Id == studyId, ct);
        if (study is null)
        {
            return Error.NotFound(nameof(ImagingStudy), studyId);
        }

        var reportable = study.EnsureReportable();
        if (reportable.IsFailure)
        {
            return reportable.Error!;
        }

        if (request.TemplateId is { } templateId && !await db.Set<ReportTemplate>().AnyAsync(t => t.Id == templateId && t.IsActive, ct))
        {
            return Error.Field("templateId", "Choose an active report template.");
        }

        var report = await db.Set<RadiologyReport>().Include(r => r.Addenda).FirstOrDefaultAsync(r => r.ImagingStudyId == studyId, ct);
        if (report is null)
        {
            report = new RadiologyReport { FacilityId = study.FacilityId, ImagingStudyId = study.Id, PatientId = study.PatientId, Status = DiagnosticReportStatus.Draft };
            db.Add(report);
        }
        else if (request.Version is { } version)
        {
            db.SetExpectedVersion(report, version);
        }

        var saved = report.Save(new RadiologyReportContent(
            request.TemplateId, request.Technique, request.ClinicalHistory, request.Comparison, request.Findings, request.Impression,
            request.IsCriticalFinding, request.CriticalFindingNote));
        if (saved.IsFailure)
        {
            return saved.Error!;
        }

        await db.SaveChangesAsync(ct);
        await realtime.PublishAsync(study.FacilityId, "radiology.study.changed", new { study.Id, study.Status }, ct);
        return await ToDtoAsync(report, ct);
    }

    public async Task<Result<RadiologyReportDto>> SubmitAsync(Guid studyId, CancellationToken ct)
    {
        var (study, report, error) = await LoadAsync(studyId, ct);
        if (error is not null)
        {
            return error;
        }

        var now = time.GetUtcNow();
        var submitted = report!.Submit(currentUser.UserId, now);
        if (submitted.IsFailure)
        {
            return submitted.Error!;
        }

        study!.MarkReported(now);
        await db.SaveChangesAsync(ct);
        await realtime.PublishAsync(study.FacilityId, "radiology.study.changed", new { study.Id, study.Status }, ct);
        return await ToDtoAsync(report, ct);
    }

    public async Task<Result<RadiologyReportDto>> VerifyAsync(Guid studyId, SignReportRequest request, CancellationToken ct)
    {
        var (study, report, error) = await LoadAsync(studyId, ct);
        if (error is not null)
        {
            return error;
        }

        var now = time.GetUtcNow();
        if (report!.IsCriticalFinding && !await db.Set<CriticalCallLog>().AnyAsync(c => c.ImagingStudyId == study!.Id, ct))
        {
            if (request.CriticalCall is null)
            {
                return Error.Rule("radiology.critical_call_required", "This report has a critical finding. Document who was informed before signing it.");
            }

            var log = CriticalCallLog.Record(
                new CriticalFindingRef(DiagnosticKind.Radiology, study!.FacilityId, study.PatientId, $"{study.StudyName}: {report.CriticalFindingNote}", ImagingStudyId: study.Id),
                request.CriticalCall,
                currentUser.UserId,
                now);
            if (log.IsFailure)
            {
                return log.Error!;
            }

            db.Add(log.Value);
        }

        var verified = report.Verify(currentUser.UserId, now);
        if (verified.IsFailure)
        {
            return verified.Error!;
        }

        study!.MarkVerified(now);
        IReadOnlyList<CriticalAlert> alerts = report.IsCriticalFinding
            ? [new CriticalAlert(DiagnosticKind.Radiology, study.FacilityId, study.PatientId, study.OrderingPractitionerId, study.StudyName,
                report.CriticalFindingNote ?? "Critical finding", $"/radiology/studies/{study.Id}")]
            : [];
        await critical.QueueDoctorSmsAsync(alerts, ct);

        if (request.Release)
        {
            var released = await ReleaseCoreAsync(study, report, now, ct);
            if (released.IsFailure)
            {
                return released.Error!;
            }
        }

        await db.SaveChangesAsync(ct);
        await critical.NotifyVerifiedAsync(alerts, ct);
        await realtime.PublishAsync(study.FacilityId, "radiology.study.changed", new { study.Id, study.Status }, ct);
        return await ToDtoAsync(report, ct);
    }

    public async Task<Result<RadiologyReportDto>> ReleaseAsync(Guid studyId, CancellationToken ct)
    {
        var (study, report, error) = await LoadAsync(studyId, ct);
        if (error is not null)
        {
            return error;
        }

        var released = await ReleaseCoreAsync(study!, report!, time.GetUtcNow(), ct);
        if (released.IsFailure)
        {
            return released.Error!;
        }

        await db.SaveChangesAsync(ct);
        await realtime.PublishAsync(study!.FacilityId, "radiology.study.changed", new { study.Id, study.Status }, ct);
        return await ToDtoAsync(report!, ct);
    }

    public async Task<Result<RadiologyReportDto>> AddAddendumAsync(Guid studyId, AddAddendumRequest request, CancellationToken ct)
    {
        var (study, report, error) = await LoadAsync(studyId, ct);
        if (error is not null)
        {
            return error;
        }

        var now = time.GetUtcNow();
        IReadOnlyList<CriticalAlert> alerts = [];
        if (request.IsCriticalFinding)
        {
            // A new critical finding in an addendum is a new communication and must be documented on its own.
            if (request.CriticalCall is null)
            {
                return Error.Rule("radiology.critical_call_required", "The addendum reports a critical finding. Document who was informed.");
            }

            var log = CriticalCallLog.Record(
                new CriticalFindingRef(DiagnosticKind.Radiology, study!.FacilityId, study.PatientId, $"{study.StudyName} (addendum): {TextLimits.Clip(request.Text, 200)}", ImagingStudyId: study.Id),
                request.CriticalCall,
                currentUser.UserId,
                now);
            if (log.IsFailure)
            {
                return log.Error!;
            }

            db.Add(log.Value);
            alerts = [new CriticalAlert(DiagnosticKind.Radiology, study.FacilityId, study.PatientId, study.OrderingPractitionerId, $"{study.StudyName} (addendum)",
                TextLimits.Clip(request.Text, 200), $"/radiology/studies/{study.Id}")];
        }

        var added = report!.AddAddendum(study!, request.Text, request.Reason, request.IsCriticalFinding, currentUser.UserId, now);
        if (added.IsFailure)
        {
            return added.Error!;
        }

        await critical.QueueDoctorSmsAsync(alerts, ct);
        await db.SaveChangesAsync(ct);
        await critical.NotifyVerifiedAsync(alerts, ct);

        if (study!.OrderingPractitionerId is { } practitionerId)
        {
            var users = await lookups.UserIdsForPractitionerAsync(practitionerId, ct);
            await AfterCommit.RunAsync(logger, "addendum alert", () => notifications.NotifyUsersAsync(
                users, $"Addendum to {study.StudyName}", $"{study.AccessionNumber}: {TextLimits.Clip(request.Text, 300)}",
                $"/radiology/studies/{study.Id}", "diagnostics", "warning", ct));
        }

        await realtime.PublishAsync(study.FacilityId, "radiology.study.changed", new { study.Id, study.Status }, ct);
        return await ToDtoAsync(report, ct);
    }

    /// <summary>Printable report; portal copies exist only once the report is released.</summary>
    public async Task<RadiologyReportView?> ViewAsync(Guid studyId, bool releasedOnly, CancellationToken ct)
    {
        var study = await db.Set<ImagingStudy>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == studyId, ct);
        var report = await db.Set<RadiologyReport>().AsNoTracking().Include(r => r.Addenda).FirstOrDefaultAsync(r => r.ImagingStudyId == studyId, ct);
        if (study is null || report is null || (releasedOnly && report.Status != DiagnosticReportStatus.Released))
        {
            return null;
        }

        var patient = await lookups.PatientAsync(study.PatientId, ct);
        var facility = await lookups.FacilityAsync(study.FacilityId, ct);
        var referredBy = study.ReferringDoctorName
                         ?? (study.OrderingPractitionerId is { } pid ? (await lookups.PractitionerNamesAsync([pid], ct)).GetValueOrDefault(pid) : null);
        var signatories = await lookups.SignatoriesAsync([report.ReportedBy, report.VerifiedBy], ct);
        var dto = await ToDtoAsync(report, ct);

        return new RadiologyReportView(
            report.Id, study.Id, LabReportBuilder.Letterhead(facility),
            new ReportPatient(study.PatientId, patient?.Uhid ?? string.Empty, patient?.FullName ?? string.Empty, patient?.Gender ?? Gender.Unknown,
                patient?.AgeOn(DiagnosticsTime.Today(time)), patient?.DateOfBirth),
            study.AccessionNumber, study.StudyName, study.Modality, study.BodyPart, referredBy, study.OrderedAt, study.AcquiredAt,
            report.Technique, report.ClinicalHistory, report.Comparison, report.Findings, report.Impression, report.IsCriticalFinding, report.Status,
            report.VerifiedAt, report.ReleasedAt,
            signatories.Select(s => new ReportSignatory(s.UserId, s.Name, s.Qualification, s.RegistrationNumber, s.SignatureFileId)).ToList(),
            dto.Addenda, report.VerificationCode, report.VerificationCode is { } code ? VerificationPathPrefix + code : null,
            await lookups.VerificationUrlAsync(study.FacilityId, DiagnosticKind.Radiology, report.VerificationCode, ct));
    }

    private async Task<Result> ReleaseCoreAsync(ImagingStudy study, RadiologyReport report, DateTimeOffset now, CancellationToken ct)
    {
        var released = report.Release(study, currentUser.UserId, now);
        if (released.IsFailure)
        {
            return released;
        }

        await delivery.QueueReportReadyAsync(study.PatientId, study.AccessionNumber, ct);
        return Result.Success();
    }

    private async Task<(ImagingStudy? Study, RadiologyReport? Report, Error? Error)> LoadAsync(Guid studyId, CancellationToken ct)
    {
        var study = await db.Set<ImagingStudy>().FirstOrDefaultAsync(s => s.Id == studyId, ct);
        if (study is null)
        {
            return (null, null, Error.NotFound(nameof(ImagingStudy), studyId));
        }

        var report = await db.Set<RadiologyReport>().Include(r => r.Addenda).FirstOrDefaultAsync(r => r.ImagingStudyId == studyId, ct);
        return report is null
            ? (study, null, Error.Rule("radiology.no_report", "Write the report first."))
            : (study, report, null);
    }

    private async Task<RadiologyReportDto> ToDtoAsync(RadiologyReport report, CancellationToken ct)
    {
        var users = await lookups.UserNamesAsync(
            new[] { report.ReportedBy, report.VerifiedBy }.Concat(report.Addenda.Select(a => a.AddedBy)), ct);
        return new RadiologyReportDto(
            report.Id, report.ImagingStudyId, report.TemplateId, report.Technique, report.ClinicalHistory, report.Comparison, report.Findings,
            report.Impression, report.IsCriticalFinding, report.CriticalFindingNote, report.Status,
            report.ReportedBy is { } reporter ? users.GetValueOrDefault(reporter) : null, report.ReportedAt,
            report.VerifiedBy is { } verifier ? users.GetValueOrDefault(verifier) : null, report.VerifiedAt, report.ReleasedAt,
            report.Addenda.OrderBy(a => a.AddedAt).Select(a => new RadiologyAddendumDto(
                a.Id, a.Text, a.Reason, a.IsCriticalFinding, a.AddedAt, a.AddedBy is { } by ? users.GetValueOrDefault(by) : null)).ToList(),
            report.Version);
    }
}
