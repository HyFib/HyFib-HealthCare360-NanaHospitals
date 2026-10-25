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
/// and scheduled; modalities read them from the DICOM modality worklist (<see cref="ModalityWorklistSource"/>); images are
/// linked with PACS by accession number, or by study UID when DICOM cannot carry the accession number (PACS is optional,
/// so its absence is reported and never crashes a workflow); TAT and repeat metrics are reported; and external AI
/// findings are kept as preliminary annotations.
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
    /// <summary>Studies examined per linking batch (keeps each run's PACS round-trips bounded).</summary>
    public const int ReconcileBatchSize = 50;

    /// <summary>How far back unlinked studies are looked for in PACS.</summary>
    public static readonly TimeSpan ReconcileWindow = TimeSpan.FromDays(3);

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
            AccessionNumber = await numbers.NextAsync(DiagnosticsNumberSeries.RadiologyAccession, placed.FacilityId, ct),
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
            RequiresFormF = procedure?.RequiresFormF ?? false,
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

    public async Task<Result<ImagingStudyDetail>> StartAsync(Guid id, CancellationToken ct) =>
        await FormFDeclaredAsync(id, ct) is { } missing ? missing : await UpdateAsync(id, (study, now) => study.Start(currentUser.UserId, now), ct);

    public async Task<Result<ImagingStudyDetail>> AcquireAsync(Guid id, AcquireStudyRequest request, CancellationToken ct) =>
        await FormFDeclaredAsync(id, ct) is { } missing
            ? missing
            : await UpdateAsync(id, (study, now) => study.Acquire(
                new AcquisitionDetails(request.ContrastUsed, request.ContrastAgent, request.ContrastVolumeMl, request.DoseNote, request.Notes),
                currentUser.UserId,
                now), ct);

    /// <summary>
    /// An ultrasound in pregnancy is not performed before the woman's Form F declaration is recorded (PCPNDT Form F,
    /// Section D): the refusal, or null when the study may go ahead.
    /// </summary>
    private async Task<Error?> FormFDeclaredAsync(Guid studyId, CancellationToken ct) =>
        await db.Set<ImagingStudy>().AnyAsync(s => s.Id == studyId && s.RequiresFormF, ct)
        && !await db.Set<FormF>().AnyAsync(f => f.ImagingStudyId == studyId, ct)
            ? Error.Rule("form_f.required", "This is an ultrasound in pregnancy: record the woman's Form F details and declaration before the scan.")
            : null;

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

    /// <summary>
    /// Links the signed-in facility's recent studies whose images have reached PACS ("Link recent studies" on the
    /// worklist screen): finished scans first, then studies under way, then those still waiting.
    /// </summary>
    public async Task<Result<PacsReconciliationSummary>> ReconcileRecentAsync(CancellationToken ct)
    {
        if (Pacs is null)
        {
            return PacsNotConfigured;
        }

        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var ids = await PacsLinkCandidates(facilityId, time.GetUtcNow())
            .OrderBy(s => s.Status == ImagingStudyStatus.Acquired ? 0
                : s.Status == ImagingStudyStatus.InProgress ? 1
                : s.Status == ImagingStudyStatus.Arrived ? 2
                : s.Status == ImagingStudyStatus.Scheduled ? 3 : 4)
            .ThenBy(s => s.OrderedAt)
            .Select(s => s.Id)
            .Take(ReconcileBatchSize)
            .ToListAsync(ct);
        var outcome = await LinkBatchAsync(facilityId, ids, ct);
        return new PacsReconciliationSummary(outcome.Checked, outcome.Linked, outcome.Messages);
    }

    /// <summary>
    /// A facility's studies that may have images in PACS but are not linked yet: ordered in the last
    /// <see cref="ReconcileWindow"/>, not cancelled or reported, and not waiting for an appointment still to come. The
    /// facility is filtered explicitly, because background linking runs without a signed-in facility.
    /// </summary>
    internal IQueryable<ImagingStudy> PacsLinkCandidates(Guid facilityId, DateTimeOffset now)
    {
        var since = now - ReconcileWindow;
        return db.Set<ImagingStudy>().AsNoTracking()
            .Where(s => s.FacilityId == facilityId && s.PacsLinkedAt == null && s.OrderedAt >= since)
            .Where(s => s.Status == ImagingStudyStatus.Ordered || s.Status == ImagingStudyStatus.Arrived || s.Status == ImagingStudyStatus.InProgress
                        || s.Status == ImagingStudyStatus.Acquired || (s.Status == ImagingStudyStatus.Scheduled && s.ScheduledAt <= now));
    }

    /// <summary>
    /// Looks for the given studies of one facility in PACS and links those found. The batch stops at the first answer
    /// that says PACS cannot be used (not configured, off or unreachable), so an outage costs one call, not fifty.
    /// </summary>
    internal async Task<PacsLinkOutcome> LinkBatchAsync(Guid facilityId, IReadOnlyCollection<Guid> studyIds, CancellationToken ct)
    {
        if (Pacs is not { } pacs || studyIds.Count == 0)
        {
            return PacsLinkOutcome.None;
        }

        var studies = await db.Set<ImagingStudy>()
            .Where(s => s.FacilityId == facilityId && studyIds.Contains(s.Id))
            .ToListAsync(ct);

        var messages = new List<string>();
        var mismatches = new List<PacsMismatch>();
        var checkedIds = new List<Guid>();
        var linked = 0;
        string? unavailable = null;
        var now = time.GetUtcNow();
        var order = studyIds.Distinct().Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        foreach (var study in studies.OrderBy(s => order[s.Id]))
        {
            var found = await FindInPacsAsync(pacs, study, ct);
            if (found.Error is { Code: "pacs.unreachable" } outage)
            {
                unavailable = outage.Message;
                break;
            }

            checkedIds.Add(study.Id);
            if (found.IsSuccess && study.LinkPacs(found.Value.StudyInstanceUid, found.Value.SeriesCount, found.Value.InstanceCount, now).IsSuccess)
            {
                linked++;
            }
            else if (found.Error is { Code: not PacsStudyMatch.NotFound } error)
            {
                messages.Add($"{study.AccessionNumber}: {error.Message}");
                if (PacsStudyMatch.IsPatientSafety(error.Code))
                {
                    mismatches.Add(new PacsMismatch(study.Id, study.AccessionNumber, study.StudyName, error.Message));
                }
            }
        }

        if (linked > 0)
        {
            await db.SaveChangesAsync(ct);
            await realtime.PublishAsync(facilityId, "radiology.worklist.changed", new { Linked = linked }, ct);
        }

        if (unavailable is not null)
        {
            messages.Add(unavailable);
        }

        return new PacsLinkOutcome(checkedIds.Count, linked, messages, checkedIds, mismatches, unavailable);
    }

    /// <summary>
    /// The PACS viewer for the study. Once linked, the template can use the study UID and the accession number.
    /// Before that, PACS is asked for the study first, exactly as linking does (<see cref="FindInPacsAsync"/>), and
    /// the link is offered only when PACS holds exactly one study under the accession, with the patient's UHID as its
    /// patient ID: a viewer opened by accession number alone could show another patient's images.
    /// </summary>
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

        // Say which value the configured link lacks, so the message says what to do.
        if (pacs.BuildViewerUrl(SampleUid, SampleAccession) is null)
        {
            return Error.Rule("pacs.viewer_not_configured", "No image viewer is configured for the PACS integration.");
        }

        var accession = DicomConventions.AccessionFitsDicom(study.AccessionNumber) ? study.AccessionNumber : null;
        if (accession is null && pacs.BuildViewerUrl(SampleUid, null) is null)
        {
            return Error.Rule("pacs.accession_too_long",
                $"The viewer link needs the accession number, and {study.AccessionNumber} is longer than the 16 characters DICOM allows. Ask the administrator for a viewer link with {{studyUid}}.");
        }

        var studyUid = study.StudyInstanceUid;
        if (study.PacsLinkedAt is null)
        {
            var found = await FindInPacsAsync(pacs, study, ct);
            if (found.IsFailure)
            {
                return found.Error!;
            }

            studyUid = found.Value.StudyInstanceUid;
        }

        return pacs.BuildViewerUrl(studyUid, accession) is { Length: > 0 } url
            ? new ViewerLink(study.Id, url, studyUid)
            : Error.Rule("pacs.viewer_not_configured", "The PACS viewer link could not be built for this study.");
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
        var formF = await db.Set<FormF>().AsNoTracking().Where(f => f.ImagingStudyId == s.Id).Select(f => (FormFStatus?)f.Status).FirstOrDefaultAsync(ct);

        return new ImagingStudyDetail(
            s.Id, s.AccessionNumber, s.PatientId, patient?.Uhid ?? string.Empty, patient?.FullName ?? string.Empty, patient?.Gender ?? Gender.Unknown,
            patient?.AgeOn(DiagnosticsTime.Today(time)), s.VisitId, s.AdmissionId, s.PatientClass, s.SourceOrderNumber, s.ServiceCode, s.StudyName,
            s.Modality, s.BodyPart, s.Priority, s.OrderingPractitionerId,
            s.OrderingPractitionerId is { } pid ? doctors.GetValueOrDefault(pid) : null, s.ClinicalNotes, s.OrderedAt, s.Status, s.ScheduledAt, s.Room,
            s.StationAeTitle, s.ArrivedAt, s.StartedAt, s.AcquiredAt, s.TechnicianUserId is { } tech ? users.GetValueOrDefault(tech) : null,
            s.ContrastUsed, s.ContrastAgent, s.ContrastVolumeMl, s.DoseNote, s.TechnicianNotes, s.StudyInstanceUid,
            DicomConventions.AccessionFitsDicom(s.AccessionNumber), DicomConventions.ProcedureId(s.AccessionNumber, s.Id), s.PacsLinkedAt, s.SeriesCount,
            s.InstanceCount, s.ReportedAt, s.VerifiedAt, s.ReleasedAt, s.CancelReason, s.ChargeError, s.PackageBookingId, s.Quantity, preparation,
            s.Repeats.OrderBy(r => r.RecordedAt).Select(r => new ImagingRepeatDto(r.Id, r.Reason, r.Note, r.RecordedAt)).ToList(),
            s.Version,
            s.RequiresFormF,
            formF);
    }

    internal static Error PacsNotConfigured =>
        Error.Rule("pacs.not_configured", "PACS is not configured. Set up the PACS integration in Administration → Integrations.");

    // Values that fill any viewer template, to learn which value a configured link needs.
    private const string SampleUid = "2.25.1";
    private const string SampleAccession = "A";

    /// <summary>PACS is an optional integration: resolved per call so that an unconfigured PACS is reported, not fatal.</summary>
    private IPacsClient? Pacs => services.GetService(typeof(IPacsClient)) as IPacsClient;

    /// <summary>
    /// Finds the study in PACS by accession number, or by its study UID when DICOM cannot carry the accession number (the
    /// worklist then sends the modality the UID HC360 assigned, without the accession number). PACS is searched
    /// without the patient ID, so every study under the accession is seen, and the answer must be exactly one study
    /// whose patient ID is the UHID (<see cref="PacsStudyMatch"/>): a mistyped accession at the modality never links or
    /// opens another patient's images. The facility's own PACS settings apply.
    /// </summary>
    private async Task<Result<ImagingStudyInfo>> FindInPacsAsync(IPacsClient pacs, ImagingStudy study, CancellationToken ct)
    {
        var byAccession = DicomConventions.AccessionFitsDicom(study.AccessionNumber);
        var uhid = (await lookups.PatientAsync(study.PatientId, ct))?.Uhid;
        var query = byAccession
            ? new PacsStudyQuery(AccessionNumber: study.AccessionNumber, FacilityId: study.FacilityId,
                ExpectedPatientId: uhid)
            : new PacsStudyQuery(StudyInstanceUid: study.StudyInstanceUid, FacilityId: study.FacilityId,
                ExpectedPatientId: uhid);
        IReadOnlyList<ImagingStudyInfo> found;
        try
        {
            found = await pacs.FindStudiesAsync(query, ct);
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

        return PacsStudyMatch.Pick(found, study, byAccession, uhid);
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

/// <summary>
/// A study whose accession PACS holds under another patient ID than its UHID, without a patient ID, or as several
/// studies (<see cref="PacsStudyMatch.IsPatientSafety"/>): it is not linked, and the department is told why.
/// </summary>
internal sealed record PacsMismatch(Guid StudyId, string AccessionNumber, string StudyName, string Reason);

/// <summary>
/// What one linking batch did: the studies looked for (<see cref="CheckedIds"/>), how many were linked, problems to
/// report, and why the batch stopped early when PACS could not be used (<see cref="Unavailable"/>).
/// </summary>
internal sealed record PacsLinkOutcome(
    int Checked, int Linked, IReadOnlyList<string> Messages, IReadOnlyList<Guid> CheckedIds, IReadOnlyList<PacsMismatch> Mismatches, string? Unavailable)
{
    public static PacsLinkOutcome None { get; } = new(0, 0, [], [], [], null);
}
