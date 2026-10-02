using HyFib.HealthCare360.Modules.Diagnostics.Documents;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Cardiology (CAR-01..04): ECG, 2D echo, TMT, Holter and ABPM studies from clinicians' orders. The technician
/// performs the study and attaches the device output (encrypted file store). The structured report is written and
/// signed off by the cardiologist, with a documented call for critical findings. Release publishes it to the EHR and
/// portal and notifies the patient.
/// </summary>
internal sealed class CardiologyService(
    HospitalDbContext db,
    INumberSeries numbers,
    DiagnosticsLookups lookups,
    ChargeService charges,
    CriticalValueNotifier critical,
    ReportDelivery delivery,
    DiagnosticsRealtime realtime,
    ICurrentUser currentUser,
    TimeProvider time,
    ILogger<CardiologyService> logger)
{
    public const string VerificationPathPrefix = "/api/v1/cardiology/reports/verify/";

    public async Task CreateFromClinicalOrderAsync(ServiceOrderPlaced placed, CancellationToken ct)
    {
        if (db.Set<CardiologyStudy>().Local.Any(s => s.SourceOrderItemId == placed.OrderItemId)
            || await db.Set<CardiologyStudy>().AnyAsync(s => s.SourceOrderItemId == placed.OrderItemId, ct))
        {
            return;
        }

        var study = new CardiologyStudy
        {
            FacilityId = placed.FacilityId,
            AccessionNumber = await numbers.NextAsync(DiagnosticsNumberSeries.CardiologyAccession, placed.FacilityId, ct),
            PatientId = placed.PatientId,
            VisitId = placed.VisitId,
            AdmissionId = placed.AdmissionId,
            PatientClass = await lookups.PatientClassAsync(placed.VisitId, placed.AdmissionId, ct),
            SourceOrderId = placed.OrderId,
            SourceOrderItemId = placed.OrderItemId,
            SourceOrderNumber = TextLimits.ClipOrNull(placed.OrderNumber, 32),

            // Health-checkup components are ordinary studies; Billing prices the package, so they are never charged per study.
            PackageBookingId = placed.PackageBookingId,
            Quantity = Math.Max(1, placed.Quantity),
            ServiceItemId = placed.ServiceItemId,
            ServiceCode = TextLimits.Clip(placed.ServiceCode, 32),
            StudyName = TextLimits.Clip(placed.ServiceName, 200),
            StudyType = CardiologyStudyTypes.Resolve(placed.ServiceCode, placed.ServiceName),
            Priority = placed.Priority,
            OrderingPractitionerId = placed.OrderingPractitionerId,
            ClinicalNotes = TextLimits.ClipOrNull(placed.ClinicalNotes, 1000),
            OrderedAt = placed.OrderedAt,
            Status = CardiologyStudyStatus.Ordered,
        };
        db.Add(study);
        await charges.PostCardiologyAsync(study, ct);
        realtime.PublishAfterCommit(study.FacilityId, "cardiology.study.changed", new { study.Id, study.Status });
    }

    public async Task<bool> HandleClinicalCancelAsync(ServiceOrderCancelled cancelled, CancellationToken ct)
    {
        var study = db.Set<CardiologyStudy>().Local.FirstOrDefault(s => s.SourceOrderItemId == cancelled.OrderItemId)
                    ?? await db.Set<CardiologyStudy>().FirstOrDefaultAsync(s => s.SourceOrderItemId == cancelled.OrderItemId, ct);
        if (study is null)
        {
            return false;
        }

        var reason = TextLimits.Clip(string.IsNullOrWhiteSpace(cancelled.Reason) ? "Cancelled by the ordering clinician" : cancelled.Reason.Trim(), 300);
        var result = study.Cancel(reason, time.GetUtcNow());
        if (result.IsFailure)
        {
            logger.LogWarning("Clinical cancellation of cardiology study {StudyId} could not be applied: {Error}", study.Id, result.Error!.Message);
            realtime.AlertStaffAfterCommit(new DeferredStaffAlert(
                DiagnosticsPermissions.CardiologyReportsVerify,
                study.FacilityId,
                $"Cancelled order already reported: {study.StudyName}",
                $"The clinician cancelled {study.StudyName} ({study.AccessionNumber}) after it was reported. Review the report and the bill.",
                $"/cardiology/studies/{study.Id}",
                "warning"));
            return true;
        }

        await charges.CancelCardiologyAsync(study, reason, ct);
        realtime.PublishAfterCommit(study.FacilityId, "cardiology.study.changed", new { study.Id, study.Status });
        return true;
    }

    public Task<Result<CardiologyStudyDetail>> ScheduleAsync(Guid id, ScheduleCardiologyRequest request, CancellationToken ct) =>
        UpdateAsync(id, (study, _) => Task.FromResult(study.Schedule(request.ScheduledAt, request.Room)), ct);

    public Task<Result<CardiologyStudyDetail>> StartAsync(Guid id, StartCardiologyRequest request, CancellationToken ct) =>
        UpdateAsync(id, (study, now) => Task.FromResult(study.Start(currentUser.UserId, request.DeviceName, now)), ct);

    public Task<Result<CardiologyStudyDetail>> CompleteAsync(Guid id, CompleteCardiologyRequest request, CancellationToken ct) =>
        UpdateAsync(id, (study, now) => Task.FromResult(study.Complete(currentUser.UserId, request.Notes, now)), ct);

    /// <summary>
    /// Links device output already uploaded to the encrypted file store. Each file must belong to this patient (or to
    /// nobody yet), so a mixed-up upload cannot attach another patient's ECG.
    /// </summary>
    public Task<Result<CardiologyStudyDetail>> AttachFilesAsync(Guid id, AttachDeviceFilesRequest request, CancellationToken ct) =>
        UpdateAsync(id, async (study, _) =>
        {
            var ids = request.FileIds.Distinct().ToList();
            var files = await db.Set<StoredFile>().Where(f => ids.Contains(f.Id)).ToListAsync(ct);
            if (files.Count != ids.Count)
            {
                return Error.Field("fileIds", "Upload the files before attaching them.");
            }

            if (files.Any(f => f.PatientId is { } owner && owner != study.PatientId))
            {
                return Error.Rule("cardiology.file_other_patient", "One of the files belongs to a different patient.");
            }

            foreach (var file in files)
            {
                file.PatientId ??= study.PatientId;
                file.OwnerModule ??= "diagnostics";
            }

            return study.AttachFiles(ids);
        }, ct);

    public async Task<Result<CardiologyStudyDetail>> CancelAsync(Guid id, string reason, CancellationToken ct)
    {
        var study = await db.Set<CardiologyStudy>().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (study is null)
        {
            return Error.NotFound(nameof(CardiologyStudy), id);
        }

        var cancelled = study.Cancel(reason.Trim(), time.GetUtcNow());
        if (cancelled.IsFailure)
        {
            return cancelled.Error!;
        }

        await charges.CancelCardiologyAsync(study, reason.Trim(), ct);
        await db.SaveChangesAsync(ct);
        await realtime.PublishAsync(study.FacilityId, "cardiology.study.changed", new { study.Id, study.Status }, ct);
        return (await DetailAsync(id, ct))!;
    }

    // ------------------------------------------------------------------ structured report (CAR-03/04)

    public async Task<Result<CardiologyStudyDetail>> SaveReportAsync(Guid studyId, SaveCardiologyReportRequest request, CancellationToken ct)
    {
        var study = await db.Set<CardiologyStudy>().FirstOrDefaultAsync(s => s.Id == studyId, ct);
        if (study is null)
        {
            return Error.NotFound(nameof(CardiologyStudy), studyId);
        }

        var reportable = study.EnsureReportable();
        if (reportable.IsFailure)
        {
            return reportable.Error!;
        }

        var report = await db.Set<CardiologyReport>().FirstOrDefaultAsync(r => r.CardiologyStudyId == studyId, ct);
        if (report is null)
        {
            report = new CardiologyReport
            {
                FacilityId = study.FacilityId,
                CardiologyStudyId = study.Id,
                PatientId = study.PatientId,
                StudyType = study.StudyType,
                Status = DiagnosticReportStatus.Draft,
            };
            db.Add(report);
        }
        else if (request.Version is { } version)
        {
            db.SetExpectedVersion(report, version);
        }

        var saved = report.Save(new CardiologyReportContent(
            request.Ecg, request.Echo, request.Tmt, request.Holter, request.Abpm, request.Conclusion, request.Recommendations,
            request.IsCriticalFinding, request.CriticalFindingNote));
        if (saved.IsFailure)
        {
            return saved.Error!;
        }

        await db.SaveChangesAsync(ct);
        await realtime.PublishAsync(study.FacilityId, "cardiology.study.changed", new { study.Id, study.Status }, ct);
        return (await DetailAsync(studyId, ct))!;
    }

    public async Task<Result<CardiologyStudyDetail>> SubmitReportAsync(Guid studyId, CancellationToken ct)
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
        await realtime.PublishAsync(study.FacilityId, "cardiology.study.changed", new { study.Id, study.Status }, ct);
        return (await DetailAsync(studyId, ct))!;
    }

    /// <summary>Cardiologist sign-off; a critical finding (for example an acute STEMI pattern) needs a documented call first.</summary>
    public async Task<Result<CardiologyStudyDetail>> VerifyReportAsync(Guid studyId, SignCardiologyReportRequest request, CancellationToken ct)
    {
        var (study, report, error) = await LoadAsync(studyId, ct);
        if (error is not null)
        {
            return error;
        }

        var now = time.GetUtcNow();
        if (report!.IsCriticalFinding && !await db.Set<CriticalCallLog>().AnyAsync(c => c.CardiologyStudyId == study!.Id, ct))
        {
            if (request.CriticalCall is null)
            {
                return Error.Rule("cardiology.critical_call_required", "This report has a critical finding. Document who was informed before signing it.");
            }

            var log = CriticalCallLog.Record(
                new CriticalFindingRef(DiagnosticKind.Cardiology, study!.FacilityId, study.PatientId, $"{study.StudyName}: {report.CriticalFindingNote}", CardiologyStudyId: study.Id),
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
            ? [new CriticalAlert(DiagnosticKind.Cardiology, study.FacilityId, study.PatientId, study.OrderingPractitionerId, study.StudyName,
                report.CriticalFindingNote ?? "Critical finding", $"/cardiology/studies/{study.Id}")]
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
        await realtime.PublishAsync(study.FacilityId, "cardiology.study.changed", new { study.Id, study.Status }, ct);
        return (await DetailAsync(studyId, ct))!;
    }

    public async Task<Result<CardiologyStudyDetail>> ReleaseReportAsync(Guid studyId, CancellationToken ct)
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
        await realtime.PublishAsync(study!.FacilityId, "cardiology.study.changed", new { study.Id, study.Status }, ct);
        return (await DetailAsync(studyId, ct))!;
    }

    // ------------------------------------------------------------------ read models

    public async Task<CardiologyStudyDetail?> DetailAsync(Guid id, CancellationToken ct)
    {
        var s = await db.Set<CardiologyStudy>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null)
        {
            return null;
        }

        var report = await db.Set<CardiologyReport>().AsNoTracking().FirstOrDefaultAsync(r => r.CardiologyStudyId == id, ct);
        var patient = await lookups.PatientAsync(s.PatientId, ct);
        var doctors = await lookups.PractitionerNamesAsync([s.OrderingPractitionerId], ct);
        var users = await lookups.UserNamesAsync([s.TechnicianUserId, report?.ReportedBy, report?.VerifiedBy], ct);
        var files = await FilesAsync(s.DeviceFileIds, ct);

        return new CardiologyStudyDetail(
            s.Id, s.AccessionNumber, s.PatientId, patient?.Uhid ?? string.Empty, patient?.FullName ?? string.Empty, patient?.Gender ?? Gender.Unknown,
            patient?.AgeOn(DiagnosticsTime.Today(time)), s.VisitId, s.AdmissionId, s.PatientClass, s.SourceOrderNumber, s.ServiceCode, s.StudyName,
            s.StudyType, s.Priority, s.OrderingPractitionerId, s.OrderingPractitionerId is { } pid ? doctors.GetValueOrDefault(pid) : null,
            s.ClinicalNotes, s.OrderedAt, s.Status, s.ScheduledAt, s.Room, s.StartedAt, s.AcquiredAt,
            s.TechnicianUserId is { } tech ? users.GetValueOrDefault(tech) : null, s.DeviceName, s.TechnicianNotes, s.ReportedAt, s.VerifiedAt,
            s.ReleasedAt, s.CancelReason, s.ChargeError, s.PackageBookingId, s.Quantity, files, report is null ? null : ToDto(report, users), s.Version);
    }

    /// <summary>Printable report with only the measurements that belong to the study type.</summary>
    public async Task<CardiologyReportView?> ViewAsync(Guid studyId, bool releasedOnly, CancellationToken ct)
    {
        var study = await db.Set<CardiologyStudy>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == studyId, ct);
        var report = await db.Set<CardiologyReport>().AsNoTracking().FirstOrDefaultAsync(r => r.CardiologyStudyId == studyId, ct);
        if (study is null || report is null || (releasedOnly && report.Status != DiagnosticReportStatus.Released))
        {
            return null;
        }

        var patient = await lookups.PatientAsync(study.PatientId, ct);
        var facility = await lookups.FacilityAsync(study.FacilityId, ct);
        var referredBy = study.OrderingPractitionerId is { } pid ? (await lookups.PractitionerNamesAsync([pid], ct)).GetValueOrDefault(pid) : null;
        var signatories = await lookups.SignatoriesAsync([report.ReportedBy, report.VerifiedBy], ct);
        var files = await FilesAsync(study.DeviceFileIds, ct);
        var type = study.StudyType;

        return new CardiologyReportView(
            report.Id, study.Id, LabReportBuilder.Letterhead(facility),
            new ReportPatient(study.PatientId, patient?.Uhid ?? string.Empty, patient?.FullName ?? string.Empty, patient?.Gender ?? Gender.Unknown,
                patient?.AgeOn(DiagnosticsTime.Today(time)), patient?.DateOfBirth),
            study.AccessionNumber, study.StudyName, type, referredBy, study.OrderedAt, study.AcquiredAt,
            type is CardiologyStudyType.Ecg or CardiologyStudyType.Tmt or CardiologyStudyType.Other ? report.Ecg : null,
            type is CardiologyStudyType.Echo or CardiologyStudyType.Other ? report.Echo : null,
            type is CardiologyStudyType.Tmt or CardiologyStudyType.Other ? report.Tmt : null,
            type is CardiologyStudyType.Holter or CardiologyStudyType.Other ? report.Holter : null,
            type is CardiologyStudyType.Abpm or CardiologyStudyType.Other ? report.Abpm : null,
            report.Conclusion, report.Recommendations, report.IsCriticalFinding, report.Status, report.VerifiedAt, report.ReleasedAt,
            signatories.Select(s => new ReportSignatory(s.UserId, s.Name, s.Qualification, s.RegistrationNumber, s.SignatureFileId)).ToList(),
            files, report.VerificationCode, report.VerificationCode is { } code ? VerificationPathPrefix + code : null,
            await lookups.VerificationUrlAsync(study.FacilityId, DiagnosticKind.Cardiology, report.VerificationCode, ct));
    }

    private async Task<Result> ReleaseCoreAsync(CardiologyStudy study, CardiologyReport report, DateTimeOffset now, CancellationToken ct)
    {
        var released = report.Release(study, currentUser.UserId, now);
        if (released.IsFailure)
        {
            return released;
        }

        await delivery.QueueReportReadyAsync(
            study.PatientId, study.AccessionNumber, new NotificationDocument(CardiologyReportDocumentSource.DocumentKind, study.Id), study.FacilityId, ct);
        return Result.Success();
    }

    private async Task<(CardiologyStudy? Study, CardiologyReport? Report, Error? Error)> LoadAsync(Guid studyId, CancellationToken ct)
    {
        var study = await db.Set<CardiologyStudy>().FirstOrDefaultAsync(s => s.Id == studyId, ct);
        if (study is null)
        {
            return (null, null, Error.NotFound(nameof(CardiologyStudy), studyId));
        }

        var report = await db.Set<CardiologyReport>().FirstOrDefaultAsync(r => r.CardiologyStudyId == studyId, ct);
        return report is null
            ? (study, null, Error.Rule("cardiology.no_report", "Write the report first."))
            : (study, report, null);
    }

    private async Task<Result<CardiologyStudyDetail>> UpdateAsync(Guid id, Func<CardiologyStudy, DateTimeOffset, Task<Result>> change, CancellationToken ct)
    {
        var study = await db.Set<CardiologyStudy>().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (study is null)
        {
            return Error.NotFound(nameof(CardiologyStudy), id);
        }

        var result = await change(study, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(ct);
        await realtime.PublishAsync(study.FacilityId, "cardiology.study.changed", new { study.Id, study.Status }, ct);
        return (await DetailAsync(id, ct))!;
    }

    private async Task<IReadOnlyList<DeviceFileDto>> FilesAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken ct)
    {
        if (fileIds.Count == 0)
        {
            return [];
        }

        var ids = fileIds.ToList();
        var files = await db.Set<StoredFile>().AsNoTracking().Where(f => ids.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, f => new DeviceFileDto(f.Id, f.FileName, f.ContentType, f.SizeBytes), ct);
        return ids.Select(id => files.GetValueOrDefault(id) ?? new DeviceFileDto(id, null, null, null)).ToList();
    }

    private static CardiologyReportDto ToDto(CardiologyReport r, IReadOnlyDictionary<Guid, string> users) => new(
        r.Id, r.StudyType, r.Ecg, r.Echo, r.Tmt, r.Holter, r.Abpm, r.Conclusion, r.Recommendations, r.IsCriticalFinding, r.CriticalFindingNote, r.Status,
        r.ReportedBy is { } reporter ? users.GetValueOrDefault(reporter) : null, r.ReportedAt,
        r.VerifiedBy is { } verifier ? users.GetValueOrDefault(verifier) : null, r.VerifiedAt, r.ReleasedAt, r.Version);
}
