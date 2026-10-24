using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// MRD chart tracking (MRD-03/04/05): charts per admission/encounter, deficiencies (manual and detected by a scan of
/// the record), completion with retention dates, archival, and record requests with issue/return tracking.
/// </summary>
public sealed class MedicalRecordService(
    HospitalDbContext db,
    INumberSeries numbers,
    ClinicalAccess access,
    ICurrentUser currentUser,
    ISettingsService settings,
    TimeProvider time)
{
    /// <summary>In-patient charts are due for completion this many days after discharge (common Indian hospital policy).</summary>
    public const int ChartCompletionDays = 7;

    public async Task<Result<MedicalRecordDto>> CreateAsync(CreateMedicalRecordRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var allowed = await access.EnsureCanOpenAsync(request.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        if (request.Paper is { } paper)
        {
            return await CreatePaperAsync(facilityId, request, paper, ct);
        }

        // The chart files the patient's own episode: an encounter or visit of someone else would put their record in it.
        if ((request.EncounterId is { } encounterId && !await db.Set<Encounter>().AnyAsync(e => e.Id == encounterId && e.PatientId == request.PatientId, ct))
            || (request.VisitId is { } ownVisitId && !await db.Set<Visit>().AnyAsync(v => v.Id == ownVisitId && v.PatientId == request.PatientId, ct)))
        {
            return Error.Field(request.EncounterId is null ? "visitId" : "encounterId", "This is not an episode of the patient.");
        }

        var exists = await Charts().AnyAsync(m => (request.AdmissionId != null && m.AdmissionId == request.AdmissionId)
                                                  || (request.EncounterId != null && m.EncounterId == request.EncounterId), ct);
        if (exists)
        {
            return Error.Conflict("chart.exists", "A chart already exists for this admission or encounter.");
        }

        var isMlc = request.IsMlc || (request.VisitId is { } visitId && await db.Set<Visit>().AnyAsync(v => v.Id == visitId && v.IsMlc, ct));
        var chart = new MedicalRecord
        {
            FacilityId = facilityId,
            PatientId = request.PatientId,
            EncounterId = request.EncounterId,
            AdmissionId = request.AdmissionId,
            VisitId = request.VisitId,
            AssignedPractitionerId = request.AssignedPractitionerId,
            DueDate = request.DueDate,
            IsMlc = isMlc,
        };
        db.Add(chart);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(chart, ct);
    }

    /// <summary>
    /// A chart of a file that exists only on paper. Its number is unique in the facility (it is how the file is found on
    /// the shelf); the file's own number is kept as written, in capitals.
    /// </summary>
    private async Task<Result<MedicalRecordDto>> CreatePaperAsync(Guid facilityId, CreateMedicalRecordRequest request, PaperChartDetails paper, CancellationToken ct)
    {
        var number = paper.FileNumber.Trim().ToUpperInvariant();
        if (await Charts().AnyAsync(m => m.FacilityId == facilityId && m.PaperFileNumber == number, ct))
        {
            return Error.Conflict("chart.paper_exists", $"A chart for paper file {number} already exists.");
        }

        var chart = new MedicalRecord
        {
            FacilityId = facilityId,
            PatientId = request.PatientId,
            PaperFileNumber = number,
            IsPaperInpatient = paper.IsInpatient,
            PaperEpisodeFrom = paper.EpisodeFrom,
            PaperEpisodeTo = paper.IsInpatient ? paper.EpisodeTo : null,
            PaperNote = string.IsNullOrWhiteSpace(paper.Note) ? null : paper.Note.Trim(),
            AssignedPractitionerId = request.AssignedPractitionerId,
            DueDate = request.DueDate,
            IsMlc = request.IsMlc,
        };
        db.Add(chart);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(chart, ct);
    }

    /// <summary>Chart for an admission (created at admission, updated at discharge). The caller saves.</summary>
    public async Task<MedicalRecord> EnsureForAdmissionAsync(Guid admissionId, Guid patientId, Guid facilityId, Guid? visitId, Guid? practitionerId, CancellationToken ct)
    {
        var chart = await Charts().Include(m => m.Deficiencies).FirstOrDefaultAsync(m => m.AdmissionId == admissionId, ct);
        if (chart is not null)
        {
            return chart;
        }

        var isMlc = visitId is { } vid && await db.Set<Visit>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AnyAsync(v => v.Id == vid && v.IsMlc, ct);
        chart = new MedicalRecord
        {
            FacilityId = facilityId,
            PatientId = patientId,
            AdmissionId = admissionId,
            VisitId = visitId,
            AssignedPractitionerId = practitionerId,
            IsMlc = isMlc,
        };
        db.Add(chart);
        return chart;
    }

    public Task<Result<MedicalRecordDto>> AddDeficiencyAsync(Guid chartId, AddDeficiencyRequest request, CancellationToken ct) =>
        ChangeAsync(chartId, chart => chart.AddDeficiency(request.Type, request.Description, request.AssignedPractitionerId, request.DueDate, null,
            currentUser.UserId, systemDetected: false, time.GetUtcNow()), ct);

    public async Task<Result<MedicalRecordDto>> ResolveDeficiencyAsync(Guid deficiencyId, string? note, CancellationToken ct)
    {
        var chartId = await db.Set<ChartDeficiency>().Where(d => d.Id == deficiencyId).Select(d => (Guid?)d.MedicalRecordId).FirstOrDefaultAsync(ct);
        return chartId is null
            ? Error.NotFound("Deficiency", deficiencyId)
            : await ChangeAsync(chartId.Value, chart => chart.ResolveDeficiency(deficiencyId, currentUser.UserId, note, time.GetUtcNow()), ct);
    }

    /// <summary>
    /// Completes the chart; its retention date follows the schedule in force (SRS PRV-02, set in Privacy), counted from
    /// discharge, a paper file's episode, or (an out-patient chart of the record) today.
    /// </summary>
    public async Task<Result<MedicalRecordDto>> CompleteAsync(Guid chartId, CancellationToken ct)
    {
        var outPatientYears = await RecordRetention.YearsAsync(settings, inpatient: false, ct);
        var inPatientYears = await RecordRetention.YearsAsync(settings, inpatient: true, ct);
        return await ChangeAsync(chartId, chart => chart.Complete(currentUser.UserId, time.GetUtcNow(), RetentionFrom(chart), outPatientYears, inPatientYears), ct);
    }

    private DateOnly RetentionFrom(MedicalRecord chart) =>
        chart.DischargedAt is { } discharged ? ClinicalClock.LocalDate(discharged)
        : chart.IsPaper ? chart.PaperEpisodeTo ?? chart.PaperEpisodeFrom ?? time.Today()
        : time.Today();

    public Task<Result<MedicalRecordDto>> ArchiveAsync(Guid chartId, string location, CancellationToken ct) =>
        ChangeAsync(chartId, chart => chart.Archive(location, time.GetUtcNow()), ct);

    /// <summary>
    /// Scans the episode for gaps (unsigned encounters/notes, missing or unsigned discharge summary, no diagnosis,
    /// pending coding), raises a deficiency for each new one and closes system-detected deficiencies that are fixed.
    /// </summary>
    public async Task<Result<MedicalRecordDto>> ScanAsync(Guid chartId, CancellationToken ct)
    {
        var chart = await Charts().Include(m => m.Deficiencies).FirstOrDefaultAsync(m => m.Id == chartId, ct);
        if (chart is null)
        {
            return Error.NotFound(nameof(MedicalRecord), chartId);
        }

        if (chart.Status == MedicalRecordStatus.Archived)
        {
            return Error.Rule("chart.archived", "This chart is archived.");
        }

        if (chart.IsPaper)
        {
            return Error.Rule("chart.paper", "A paper chart has no electronic record to scan. Check the file and raise its deficiencies by hand.");
        }

        var now = time.GetUtcNow();
        var found = await DetectAsync(chart, ct);
        foreach (var gap in found.Where(g => !chart.HasOpenDeficiency(g.Type, g.Reference)))
        {
            chart.AddDeficiency(gap.Type, gap.Description, gap.PractitionerId, chart.DueDate, gap.Reference, currentUser.UserId, systemDetected: true, now);
        }

        foreach (var fixedGap in chart.Deficiencies.Where(d => d.IsOpen && d.IsSystemDetected && !found.Any(g => g.Type == d.Type && g.Reference == d.Reference)).ToList())
        {
            chart.ResolveDeficiency(fixedGap.Id, currentUser.UserId, "Resolved automatically: no longer found by the chart scan.", now);
        }

        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(chart, ct);
    }

    public IQueryable<MedicalRecord> Charts() => db.Set<MedicalRecord>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]);

    public async Task<MedicalRecordDto> ToDtoAsync(MedicalRecord m, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().Where(p => p.Id == m.PatientId).Select(p => new { p.Uhid, p.FullName }).FirstOrDefaultAsync(ct);
        var names = await PrintQueries.PractitionerNamesAsync(db, m.Deficiencies.Select(d => d.AssignedPractitionerId), ct);
        var today = time.Today();
        return new MedicalRecordDto(m.Id, m.PatientId, patient?.Uhid, patient?.FullName, m.EncounterId, m.AdmissionId, m.VisitId, m.AdmittedAt,
            m.DischargedAt, m.Status, m.AssignedPractitionerId, m.DueDate,
            m.DueDate is { } due && due < today && m.Status is MedicalRecordStatus.Incomplete or MedicalRecordStatus.Deficient,
            m.IsMlc, m.CompletedAt, m.ArchivedAt, m.ArchiveLocation, m.RetainUntil, m.OpenDeficiencyCount,
            m.Deficiencies.OrderBy(d => d.ResolvedAt is not null).ThenBy(d => d.RaisedAt)
                .Select(d => new ChartDeficiencyDto(d.Id, d.Type, d.Description, d.Reference, d.AssignedPractitionerId,
                    d.AssignedPractitionerId is { } p ? names.GetValueOrDefault(p) : null, d.DueDate, d.RaisedAt, d.IsSystemDetected, d.ResolvedAt,
                    d.ResolutionNote))
                .ToList(),
            m.Version,
            Paper(m));
    }

    public static PaperChartDto? Paper(MedicalRecord m) =>
        m.PaperFileNumber is { } number ? new PaperChartDto(number, m.IsPaperInpatient, m.PaperEpisodeFrom, m.PaperEpisodeTo, m.PaperNote) : null;

    // ------------------------------------------------------------------ record requests

    public async Task<Result<RecordRequestDto>> CreateRequestAsync(CreateRecordRequestRequest request, bool isPatientRequest, Guid? facilityId, CancellationToken ct)
    {
        if ((facilityId ?? currentUser.FacilityId) is not { } facility)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        if (!await db.Set<Patient>().AnyAsync(p => p.Id == request.PatientId, ct))
        {
            return Error.NotFound(nameof(Patient), request.PatientId);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var entry = new RecordRequest
        {
            FacilityId = facility,
            RequestNumber = await numbers.NextAsync(ClinicalNumberSeries.RecordRequest, facility, ct),
            PatientId = request.PatientId,
            MedicalRecordId = request.MedicalRecordId,
            RequestedByName = request.RequestedByName.Trim(),
            RequestedByUserId = currentUser.UserId,
            RequestingDepartment = request.RequestingDepartment?.Trim(),
            Purpose = request.Purpose,
            Details = request.Details?.Trim(),
            IsPatientRequest = isPatientRequest,
            RequestedAt = time.GetUtcNow(),
        };
        db.Add(entry);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await ToDtoAsync(entry, ct);
    }

    public Task<Result<RecordRequestDto>> IssueAsync(Guid id, DateOnly? dueBackOn, CancellationToken ct) =>
        ChangeRequestAsync(id, r => r.Issue(dueBackOn ?? time.Today().AddDays(7), currentUser.UserId, time.GetUtcNow()), ct);

    public Task<Result<RecordRequestDto>> ReturnAsync(Guid id, CancellationToken ct) =>
        ChangeRequestAsync(id, r => r.Return(currentUser.UserId, time.GetUtcNow()), ct);

    public Task<Result<RecordRequestDto>> CancelRequestAsync(Guid id, string reason, CancellationToken ct) =>
        ChangeRequestAsync(id, r => r.Cancel(reason), ct);

    public async Task<RecordRequestDto> ToDtoAsync(RecordRequest r, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().Where(p => p.Id == r.PatientId).Select(p => new { p.Uhid, p.FullName }).FirstOrDefaultAsync(ct);
        return ToDto(r, patient?.Uhid, patient?.FullName, time.Today());
    }

    public static RecordRequestDto ToDto(RecordRequest r, string? uhid, string? patientName, DateOnly today) => new(
        r.Id, r.RequestNumber, r.PatientId, uhid, patientName, r.MedicalRecordId, r.RequestedByName, r.RequestingDepartment, r.Purpose, r.Details,
        r.IsPatientRequest, r.EffectiveStatus(today), r.RequestedAt, r.IssuedAt, r.DueBackOn, r.ReturnedAt, r.CancelReason);

    private async Task<Result<RecordRequestDto>> ChangeRequestAsync(Guid id, Func<RecordRequest, Result> change, CancellationToken ct)
    {
        var request = await db.Set<RecordRequest>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
        {
            return Error.NotFound(nameof(RecordRequest), id);
        }

        var result = change(request);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(request, ct);
    }

    private async Task<Result<MedicalRecordDto>> ChangeAsync(Guid chartId, Func<MedicalRecord, Result> change, CancellationToken ct)
    {
        var chart = await Charts().Include(m => m.Deficiencies).FirstOrDefaultAsync(m => m.Id == chartId, ct);
        if (chart is null)
        {
            return Error.NotFound(nameof(MedicalRecord), chartId);
        }

        var result = change(chart);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(chart, ct);
    }

    private sealed record Gap(DeficiencyType Type, string Description, string? Reference, Guid? PractitionerId);

    private async Task<List<Gap>> DetectAsync(MedicalRecord chart, CancellationToken ct)
    {
        var encounters = await db.Set<Encounter>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(e => (chart.AdmissionId != null && e.AdmissionId == chart.AdmissionId) || e.Id == chart.EncounterId)
            .Select(e => new { e.Id, e.Status, e.PractitionerId, e.StartedAt })
            .ToListAsync(ct);
        var encounterIds = encounters.Select(e => (Guid?)e.Id).ToList();
        var gaps = encounters.Where(e => e.Status == EncounterStatus.InProgress)
            .Select(e => new Gap(DeficiencyType.UnsignedEncounter, $"Encounter of {ClinicalClock.LocalDate(e.StartedAt):dd-MM-yyyy} is not signed.", $"encounter:{e.Id}", e.PractitionerId))
            .ToList();

        var unsignedNotes = await db.Set<ClinicalNote>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(n => !n.IsSigned && ((chart.AdmissionId != null && n.AdmissionId == chart.AdmissionId) || encounterIds.Contains(n.EncounterId)))
            .Select(n => new { n.Id, n.Type, n.AuthorPractitionerId })
            .ToListAsync(ct);
        gaps.AddRange(unsignedNotes.Select(n => new Gap(DeficiencyType.UnsignedNote, $"{n.Type} note is not signed.", $"note:{n.Id}", n.AuthorPractitionerId)));

        if (!await db.Set<Diagnosis>().AnyAsync(d => encounterIds.Contains(d.EncounterId), ct))
        {
            gaps.Add(new Gap(DeficiencyType.MissingDiagnosis, "No diagnosis is recorded for this episode.", "diagnosis", chart.AssignedPractitionerId));
        }

        if (chart.AdmissionId is { } admissionId && chart.DischargedAt is not null)
        {
            var summary = await db.Set<DischargeSummary>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(s => s.AdmissionId == admissionId).Select(s => new { s.Status, s.PractitionerId }).FirstOrDefaultAsync(ct);
            if (summary is null || summary.Status == DischargeSummaryStatus.Draft)
            {
                gaps.Add(new Gap(DeficiencyType.MissingDischargeSummary, summary is null ? "Discharge summary is missing." : "Discharge summary is not signed.",
                    "discharge-summary", summary?.PractitionerId ?? chart.AssignedPractitionerId));
            }

            var coded = await db.Set<EncounterCoding>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .AnyAsync(c => c.AdmissionId == admissionId && c.Status != CodingStatus.Pending, ct);
            if (!coded)
            {
                gaps.Add(new Gap(DeficiencyType.PendingCoding, "ICD coding of the admission is pending.", "coding", null));
            }
        }

        return gaps;
    }
}
