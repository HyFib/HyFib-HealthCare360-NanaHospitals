using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Radiology / medical imaging workflow (IMG-01, IMG-02, IMG-04, IMG-05). Studies are created from clinicians' orders
/// and scheduled; the DICOM modality worklist is published for the MWL gateway; images are reconciled with PACS by
/// accession number (PACS is optional, so its absence is reported and never crashes a workflow); TAT and repeat metrics
/// are reported; and external AI findings are kept as preliminary annotations.
/// </summary>
internal sealed class ImagingService(
    HospitalDbContext db,
    INumberSeries numbers,
    DiagnosticsLookups lookups,
    ChargeService charges,
    DiagnosticsRealtime realtime,
    INotificationService notifications,
    IServiceProvider services,
    ICurrentUser currentUser,
    TimeProvider time,
    ILogger<ImagingService> logger)
{
    public const string AccessionSeries = "radiology.accession";
    public const string AccessionFormat = "{FAC}RD{YY}{SEQ:7}";

    /// <summary>Studies examined per batch reconciliation call (keeps each PACS round-trip bounded).</summary>
    private const int ReconcileBatchSize = 50;

    // ------------------------------------------------------------------ clinical orders

    public async Task CreateFromClinicalOrderAsync(ServiceOrderPlaced placed, CancellationToken ct)
    {
        if (db.Set<ImagingStudy>().Local.Any(s => s.SourceOrderItemId == placed.OrderItemId)
            || await db.Set<ImagingStudy>().AnyAsync(s => s.SourceOrderItemId == placed.OrderItemId, ct))
        {
            return;
        }

        var procedure = await db.Set<ImagingProcedure>().AsNoTracking()
            .Where(p => p.IsActive && (p.ServiceItemId == placed.ServiceItemId || p.Code == placed.ServiceCode))
            .OrderByDescending(p => p.ServiceItemId == placed.ServiceItemId)
            .FirstOrDefaultAsync(ct);

        var study = new ImagingStudy
        {
            FacilityId = placed.FacilityId,
            AccessionNumber = await numbers.NextAsync(AccessionSeries, placed.FacilityId, AccessionFormat, NumberResetPolicy.Yearly, ct),
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
            Modality = procedure?.Modality ?? ModalityResolver.Resolve(placed.ServiceCode, placed.ServiceName),
            BodyPart = TextLimits.ClipOrNull(procedure?.BodyPart ?? ModalityResolver.BodyPartFromCode(placed.ServiceCode), 100),
            Priority = placed.Priority,
            OrderingPractitionerId = placed.OrderingPractitionerId,
            ClinicalNotes = TextLimits.ClipOrNull(placed.ClinicalNotes, 1000),
            OrderedAt = placed.OrderedAt,
            Status = ImagingStudyStatus.Ordered,

            // Sent to the modality through the worklist, so images arrive in PACS already carrying the right UID.
            StudyInstanceUid = DicomConventions.NewUid(),
        };
        db.Add(study);
        await charges.PostImagingAsync(study, ct);
        realtime.PublishAfterCommit(study.FacilityId, "radiology.study.changed", new { study.Id, study.Status });
    }

    public async Task<bool> HandleClinicalCancelAsync(ServiceOrderCancelled cancelled, CancellationToken ct)
    {
        var study = db.Set<ImagingStudy>().Local.FirstOrDefault(s => s.SourceOrderItemId == cancelled.OrderItemId)
                    ?? await db.Set<ImagingStudy>().FirstOrDefaultAsync(s => s.SourceOrderItemId == cancelled.OrderItemId, ct);
        if (study is null)
        {
            return false;
        }

        var reason = TextLimits.Clip(string.IsNullOrWhiteSpace(cancelled.Reason) ? "Cancelled by the ordering clinician" : cancelled.Reason.Trim(), 300);
        var result = study.Cancel(reason, time.GetUtcNow());
        if (result.IsFailure)
        {
            logger.LogWarning("Clinical cancellation of imaging study {StudyId} could not be applied: {Error}", study.Id, result.Error!.Message);
            realtime.AlertStaffAfterCommit(new DeferredStaffAlert(
                DiagnosticsPermissions.RadiologyReportsVerify,
                study.FacilityId,
                $"Cancelled order already reported: {study.StudyName}",
                $"The clinician cancelled {study.StudyName} ({study.AccessionNumber}) after it was reported. Review the report and the bill.",
                $"/radiology/studies/{study.Id}",
                "warning"));
            return true;
        }

        await charges.CancelImagingAsync(study, reason, ct);
        realtime.PublishAfterCommit(study.FacilityId, "radiology.study.changed", new { study.Id, study.Status });
        return true;
    }

    // ------------------------------------------------------------------ workflow

    public Task<Result<ImagingStudyDetail>> ScheduleAsync(Guid id, ScheduleStudyRequest request, CancellationToken ct) =>
        UpdateAsync(id, (study, _) => study.Schedule(request.ScheduledAt, request.Room, request.StationAeTitle), ct);

    public Task<Result<ImagingStudyDetail>> ArriveAsync(Guid id, CancellationToken ct) =>
        UpdateAsync(id, (study, now) => study.Arrive(now), ct);

    public Task<Result<ImagingStudyDetail>> StartAsync(Guid id, CancellationToken ct) =>
        UpdateAsync(id, (study, now) => study.Start(currentUser.UserId, now), ct);

    public Task<Result<ImagingStudyDetail>> AcquireAsync(Guid id, AcquireStudyRequest request, CancellationToken ct) =>
        UpdateAsync(id, (study, now) => study.Acquire(
            new AcquisitionDetails(request.ContrastUsed, request.ContrastAgent, request.ContrastVolumeMl, request.DoseNote, request.Notes),
            currentUser.UserId,
            now), ct);

    public Task<Result<ImagingStudyDetail>> RecordRepeatAsync(Guid id, RecordRepeatRequest request, CancellationToken ct) =>
        UpdateAsync(id, (study, now) => study.RecordRepeat(request.Reason, request.Note, now), ct);

    public async Task<Result<ImagingStudyDetail>> CancelAsync(Guid id, string reason, CancellationToken ct)
    {
        var study = await db.Set<ImagingStudy>().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (study is null)
        {
            return Error.NotFound(nameof(ImagingStudy), id);
        }

        var cancelled = study.Cancel(reason.Trim(), time.GetUtcNow());
        if (cancelled.IsFailure)
        {
            return cancelled.Error!;
        }

        await charges.CancelImagingAsync(study, reason.Trim(), ct);
        await db.SaveChangesAsync(ct);
        await realtime.PublishAsync(study.FacilityId, "radiology.study.changed", new { study.Id, study.Status }, ct);
        return (await DetailAsync(id, ct))!;
    }

    // ------------------------------------------------------------------ PACS (IMG-02)

    public async Task<Result<ImagingStudyDetail>> ReconcileAsync(Guid id, CancellationToken ct)
    {
        if (Pacs is not { } pacs)
        {
            return PacsNotConfigured;
        }

        var study = await db.Set<ImagingStudy>().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (study is null)
        {
            return Error.NotFound(nameof(ImagingStudy), id);
        }

        var found = await FindInPacsAsync(pacs, study, ct);
        if (found.IsFailure)
        {
            return found.Error!;
        }

        var linked = study.LinkPacs(found.Value.StudyInstanceUid, found.Value.SeriesCount, found.Value.InstanceCount, time.GetUtcNow());
        if (linked.IsFailure)
        {
            return linked.Error!;
        }

        await db.SaveChangesAsync(ct);
        await realtime.PublishAsync(study.FacilityId, "radiology.study.changed", new { study.Id, study.Status }, ct);
        return (await DetailAsync(id, ct))!;
    }

    /// <summary>Links recent studies whose images have reached PACS (run from the worklist screen or an operations job).</summary>
    public async Task<Result<PacsReconciliationSummary>> ReconcileRecentAsync(CancellationToken ct)
    {
        if (Pacs is not { } pacs)
        {
            return PacsNotConfigured;
        }

        var since = time.GetUtcNow().AddDays(-3);
        var studies = await db.Set<ImagingStudy>()
            .Where(s => s.OrderedAt >= since
                        && (s.Status == ImagingStudyStatus.Scheduled || s.Status == ImagingStudyStatus.Arrived || s.Status == ImagingStudyStatus.InProgress
                            || (s.Status == ImagingStudyStatus.Acquired && s.PacsLinkedAt == null)))
            .OrderBy(s => s.OrderedAt)
            .Take(ReconcileBatchSize)
            .ToListAsync(ct);

        var messages = new List<string>();
        var linked = 0;
        var now = time.GetUtcNow();
        foreach (var study in studies)
        {
            var found = await FindInPacsAsync(pacs, study, ct);
            if (found.IsSuccess && study.LinkPacs(found.Value.StudyInstanceUid, found.Value.SeriesCount, found.Value.InstanceCount, now).IsSuccess)
            {
                linked++;
            }
            else if (found.Error is { Code: not "pacs.study_not_found" } error)
            {
                messages.Add($"{study.AccessionNumber}: {error.Message}");
            }
        }

        await db.SaveChangesAsync(ct);
        foreach (var facility in studies.GroupBy(s => s.FacilityId))
        {
            await realtime.PublishAsync(facility.Key, "radiology.worklist.changed", new { Linked = linked }, ct);
        }

        return new PacsReconciliationSummary(studies.Count, linked, messages);
    }

    public async Task<Result<ViewerLink>> ViewerAsync(Guid id, CancellationToken ct)
    {
        if (Pacs is not { } pacs)
        {
            return PacsNotConfigured;
        }

        var study = await db.Set<ImagingStudy>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (study is null)
        {
            return Error.NotFound(nameof(ImagingStudy), id);
        }

        if (study.PacsLinkedAt is null)
        {
            return Error.Rule("pacs.not_linked", "The images of this study are not linked yet. Reconcile the study with PACS first.");
        }

        var url = pacs.BuildViewerUrl(study.StudyInstanceUid, study.AccessionNumber);
        return string.IsNullOrWhiteSpace(url)
            ? Error.Rule("pacs.viewer_not_configured", "No image viewer is configured for the PACS integration.")
            : new ViewerLink(study.Id, url, study.StudyInstanceUid);
    }

    // ------------------------------------------------------------------ modality worklist (IMG-01/02)

    /// <summary>Scheduled procedure steps for a day (and patients already arrived or on the table) in DICOM MWL format.</summary>
    public async Task<IReadOnlyList<ModalityWorklistItem>> WorklistAsync(DateOnly? date, string? station, Modality? modality, CancellationToken ct)
    {
        var day = date ?? DiagnosticsTime.Today(time);
        var start = DiagnosticsTime.StartOfDayUtc(day);
        var end = DiagnosticsTime.StartOfDayUtc(day.AddDays(1));
        var recent = start.AddDays(-2);
        var ae = string.IsNullOrWhiteSpace(station) ? null : station.Trim().ToUpperInvariant();

        var studies = await db.Set<ImagingStudy>().AsNoTracking()
            .Where(s => (s.Status == ImagingStudyStatus.Scheduled && s.ScheduledAt >= start && s.ScheduledAt < end)
                        || ((s.Status == ImagingStudyStatus.Arrived || s.Status == ImagingStudyStatus.InProgress) && s.OrderedAt >= recent))
            .Where(s => modality == null || s.Modality == modality)
            .Where(s => ae == null || s.StationAeTitle == null || s.StationAeTitle == ae)
            .OrderBy(s => s.ScheduledAt ?? s.ArrivedAt ?? s.OrderedAt)
            .Take(500)
            .ToListAsync(ct);

        var patients = await lookups.PatientsAsync(studies.Select(s => s.PatientId).Distinct().ToList(), ct);
        var doctors = await lookups.PractitionerNamesAsync(studies.Select(s => s.OrderingPractitionerId), ct);
        return studies.Select(s =>
        {
            var patient = patients.GetValueOrDefault(s.PatientId);
            var when = DiagnosticsTime.LocalTime(s.ScheduledAt ?? s.ArrivedAt ?? s.OrderedAt);
            return new ModalityWorklistItem(
                s.Id,
                s.AccessionNumber,
                s.AccessionNumber,
                s.AccessionNumber,
                s.StudyInstanceUid,
                patient?.Uhid ?? string.Empty,
                patient is null ? string.Empty : DicomConventions.PersonName(patient.FirstName, patient.MiddleName, patient.LastName),
                patient?.DateOfBirth is { } dob ? DicomConventions.Date(dob) : null,
                patient is null ? string.Empty : DicomConventions.Sex(patient.Gender),
                DicomConventions.ModalityCode(s.Modality),
                s.StationAeTitle,
                DicomConventions.Date(DateOnly.FromDateTime(when.DateTime)),
                DicomConventions.Time(TimeOnly.FromDateTime(when.DateTime)),
                TextLimits.Clip(s.StudyName, 64),
                s.BodyPart is null ? null : TextLimits.Clip(new string(s.BodyPart.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray()), 16),
                s.OrderingPractitionerId is { } pid && doctors.GetValueOrDefault(pid) is { } doctor ? DicomConventions.PersonName(doctor, null, null) : null,
                s.Priority switch { OrderPriority.Stat => "STAT", OrderPriority.Urgent => "HIGH", _ => "ROUTINE" });
        }).ToList();
    }

    // ------------------------------------------------------------------ TAT and repeats (IMG-04)

    public async Task<RadiologyTatReport> TatAsync(DateOnly? from, DateOnly? to, Modality? modality, CancellationToken ct)
    {
        var (start, end) = DiagnosticsTime.Range(from, to, time);
        var rows = await db.Set<ImagingStudy>().AsNoTracking()
            .Where(s => s.OrderedAt >= start && s.OrderedAt < end && s.Status != ImagingStudyStatus.Cancelled && (modality == null || s.Modality == modality))
            .Select(s => new { s.Id, s.Modality, s.OrderedAt, s.AcquiredAt, s.ReportedAt, s.VerifiedAt })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var repeats = await db.Set<ImagingRepeat>().AsNoTracking()
            .Where(r => ids.Contains(r.ImagingStudyId))
            .Select(r => new { r.ImagingStudyId, r.Reason })
            .ToListAsync(ct);

        var modalities = rows.GroupBy(r => r.Modality).OrderBy(g => g.Key).Select(g =>
        {
            var studyIds = g.Select(r => r.Id).ToHashSet();
            var repeatCount = repeats.Count(r => studyIds.Contains(r.ImagingStudyId));
            return new RadiologyTatRow(
                g.Key,
                g.Count(),
                g.Count(r => r.VerifiedAt != null),
                TatStatistics.Summarize(g.Select(r => TatStatistics.Minutes(r.OrderedAt, r.AcquiredAt)).OfType<double>()),
                TatStatistics.Summarize(g.Select(r => TatStatistics.Minutes(r.AcquiredAt, r.VerifiedAt ?? r.ReportedAt)).OfType<double>()),
                TatStatistics.Summarize(g.Select(r => TatStatistics.Minutes(r.OrderedAt, r.VerifiedAt)).OfType<double>()),
                repeatCount,
                (double?)Math.Round(100.0 * repeatCount / g.Count(), 1));
        }).ToList();

        var reasons = repeats.GroupBy(r => r.Reason).OrderByDescending(g => g.Count()).Select(g => new RepeatReasonStat(g.Key, g.Count())).ToList();
        return new RadiologyTatReport(DiagnosticsTime.LocalDate(start), DiagnosticsTime.LocalDate(end).AddDays(-1), modalities, reasons);
    }

    // ------------------------------------------------------------------ AI findings (IMG-05)

    public async Task<Result<AiFindingDto>> SubmitAiFindingAsync(Guid studyId, SubmitAiFindingRequest request, CancellationToken ct)
    {
        var study = await db.Set<ImagingStudy>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == studyId, ct);
        if (study is null)
        {
            return Error.NotFound(nameof(ImagingStudy), studyId);
        }

        if (study.Status == ImagingStudyStatus.Cancelled)
        {
            return Error.Rule("imaging.cancelled", "The study was cancelled.");
        }

        var finding = new ImagingAiFinding
        {
            FacilityId = study.FacilityId,
            ImagingStudyId = study.Id,
            Engine = request.Engine.Trim(),
            EngineVersion = request.EngineVersion?.Trim(),
            Finding = request.Finding.Trim(),
            Confidence = request.Confidence,
            IsUrgent = request.IsUrgent,
            ReceivedAt = time.GetUtcNow(),
            Status = AiFindingStatus.Preliminary,
        };
        db.Add(finding);
        await db.SaveChangesAsync(ct);

        if (finding.IsUrgent)
        {
            // Triage only: it prioritises the reporting worklist, the finding itself stays preliminary.
            await AfterCommit.RunAsync(logger, "AI triage alert", () => notifications.NotifyPermissionHoldersAsync(
                DiagnosticsPermissions.RadiologyReportsWrite,
                study.FacilityId,
                $"AI triage: urgent finding on {study.StudyName}",
                $"{study.AccessionNumber}: {TextLimits.Clip(finding.Finding, 200)} (preliminary, needs radiologist review)",
                $"/radiology/studies/{study.Id}",
                "diagnostics",
                "warning",
                ct));
        }

        await realtime.PublishAsync(study.FacilityId, "radiology.ai.finding", new { StudyId = study.Id, finding.Id, finding.IsUrgent }, ct);
        return ToDto(finding, new Dictionary<Guid, string>());
    }

    public async Task<Result<AiFindingDto>> ReviewAiFindingAsync(Guid findingId, ReviewAiFindingRequest request, CancellationToken ct)
    {
        var finding = await db.Set<ImagingAiFinding>().FirstOrDefaultAsync(f => f.Id == findingId, ct);
        if (finding is null)
        {
            return Error.NotFound(nameof(ImagingAiFinding), findingId);
        }

        var reviewed = finding.Review(request.Acknowledge, request.Note, currentUser.UserId, time.GetUtcNow());
        if (reviewed.IsFailure)
        {
            return reviewed.Error!;
        }

        await db.SaveChangesAsync(ct);
        return ToDto(finding, await lookups.UserNamesAsync([finding.ReviewedBy], ct));
    }

    public async Task<IReadOnlyList<AiFindingDto>> AiFindingsAsync(Guid studyId, CancellationToken ct)
    {
        var findings = await db.Set<ImagingAiFinding>().AsNoTracking().Where(f => f.ImagingStudyId == studyId).OrderByDescending(f => f.ReceivedAt).ToListAsync(ct);
        var users = await lookups.UserNamesAsync(findings.Select(f => f.ReviewedBy), ct);
        return findings.Select(f => ToDto(f, users)).ToList();
    }

    // ------------------------------------------------------------------ read models

    public async Task<ImagingStudyDetail?> DetailAsync(Guid id, CancellationToken ct)
    {
        var s = await db.Set<ImagingStudy>().AsNoTracking().Include(x => x.Repeats).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null)
        {
            return null;
        }

        var patient = await lookups.PatientAsync(s.PatientId, ct);
        var doctors = await lookups.PractitionerNamesAsync([s.OrderingPractitionerId], ct);
        var users = await lookups.UserNamesAsync([s.TechnicianUserId], ct);
        var preparation = await db.Set<ImagingProcedure>().AsNoTracking()
            .Where(p => p.ServiceItemId == s.ServiceItemId || p.Code == s.ServiceCode)
            .Select(p => p.PatientPreparation)
            .FirstOrDefaultAsync(ct);

        return new ImagingStudyDetail(
            s.Id, s.AccessionNumber, s.PatientId, patient?.Uhid ?? string.Empty, patient?.FullName ?? string.Empty, patient?.Gender ?? Gender.Unknown,
            patient?.AgeOn(DiagnosticsTime.Today(time)), s.VisitId, s.AdmissionId, s.PatientClass, s.SourceOrderNumber, s.ServiceCode, s.StudyName,
            s.Modality, s.BodyPart, s.Priority, s.OrderingPractitionerId,
            s.OrderingPractitionerId is { } pid ? doctors.GetValueOrDefault(pid) : null, s.ClinicalNotes, s.OrderedAt, s.Status, s.ScheduledAt, s.Room,
            s.StationAeTitle, s.ArrivedAt, s.StartedAt, s.AcquiredAt, s.TechnicianUserId is { } tech ? users.GetValueOrDefault(tech) : null,
            s.ContrastUsed, s.ContrastAgent, s.ContrastVolumeMl, s.DoseNote, s.TechnicianNotes, s.StudyInstanceUid, s.PacsLinkedAt, s.SeriesCount,
            s.InstanceCount, s.ReportedAt, s.VerifiedAt, s.ReleasedAt, s.CancelReason, s.ChargeError, s.PackageBookingId, s.Quantity, preparation,
            s.Repeats.OrderBy(r => r.RecordedAt).Select(r => new ImagingRepeatDto(r.Id, r.Reason, r.Note, r.RecordedAt)).ToList(),
            s.Version);
    }

    private static Error PacsNotConfigured =>
        Error.Rule("pacs.not_configured", "PACS is not configured. Set up the PACS integration in Administration → Integrations.");

    /// <summary>PACS is an optional integration: resolved per call so that an unconfigured PACS is reported, not fatal.</summary>
    private IPacsClient? Pacs => services.GetService(typeof(IPacsClient)) as IPacsClient;

    /// <summary>
    /// Finds the study in PACS by accession number. The returned patient id must match the UHID, so that a mistyped
    /// accession at the modality never links another patient's images.
    /// </summary>
    private async Task<Result<ImagingStudyInfo>> FindInPacsAsync(IPacsClient pacs, ImagingStudy study, CancellationToken ct)
    {
        IReadOnlyList<ImagingStudyInfo> found;
        try
        {
            found = await pacs.FindStudiesAsync(new PacsStudyQuery(AccessionNumber: study.AccessionNumber), ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "PACS query for accession {Accession} failed", study.AccessionNumber);
            return Error.Rule("pacs.unreachable", "PACS could not be reached. Try again shortly.");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "PACS query for accession {Accession} timed out", study.AccessionNumber);
            return Error.Rule("pacs.unreachable", "PACS did not respond in time. Try again shortly.");
        }

        var match = found.FirstOrDefault(s => string.Equals(s.AccessionNumber?.Trim(), study.AccessionNumber, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return Error.Rule("pacs.study_not_found", $"No images for accession {study.AccessionNumber} have reached PACS yet.");
        }

        if (!DicomConventions.IsValidUid(match.StudyInstanceUid))
        {
            return Error.Rule("pacs.invalid_uid", "PACS returned an invalid Study Instance UID for this accession.");
        }

        if (!string.IsNullOrWhiteSpace(match.PatientId))
        {
            var patient = await lookups.PatientAsync(study.PatientId, ct);
            if (patient is not null && !string.Equals(match.PatientId.Trim(), patient.Uhid, StringComparison.OrdinalIgnoreCase))
            {
                return Error.Rule("pacs.patient_mismatch",
                    $"PACS holds accession {study.AccessionNumber} under patient ID {match.PatientId}, not {patient.Uhid}. Correct the study at the modality/PACS before linking.");
            }
        }

        return match;
    }

    private async Task<Result<ImagingStudyDetail>> UpdateAsync(Guid id, Func<ImagingStudy, DateTimeOffset, Result> change, CancellationToken ct)
    {
        var study = await db.Set<ImagingStudy>().Include(s => s.Repeats).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (study is null)
        {
            return Error.NotFound(nameof(ImagingStudy), id);
        }

        var result = change(study, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(ct);
        await realtime.PublishAsync(study.FacilityId, "radiology.study.changed", new { study.Id, study.Status }, ct);
        return (await DetailAsync(id, ct))!;
    }

    private static AiFindingDto ToDto(ImagingAiFinding f, IReadOnlyDictionary<Guid, string> users) => new(
        f.Id, f.Engine, f.EngineVersion, f.Finding, f.Confidence, f.IsUrgent, f.ReceivedAt, f.Status,
        f.ReviewedBy is { } by ? users.GetValueOrDefault(by) : null, f.ReviewedAt, f.ReviewNote);
}
