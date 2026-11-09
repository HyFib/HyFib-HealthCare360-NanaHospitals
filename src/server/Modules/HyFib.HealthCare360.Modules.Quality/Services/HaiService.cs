using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Domain.Hai;
using HyFib.HealthCare360.Modules.Quality.Features.Hai;
using HyFib.HealthCare360.Modules.Quality.Services.Indicators;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Quality.Services;

/// <summary>
/// Hospital-acquired infection surveillance (NABH HIC): the device register the ward and ICU nurses keep (central lines,
/// urinary catheters, ventilation), the cases the infection control team reviews against the NHSN definitions, and the
/// monthly rates per 1,000 device days. A verified culture with an organism, on a patient with the matching device (or
/// after an operation), raises a suspected case and alerts the team (<see cref="ScanCulturesAsync"/>).
/// </summary>
public sealed class HaiService(
    HospitalDbContext db,
    INumberSeries numbers,
    ICurrentUser currentUser,
    INotificationService notifications,
    IAuditLogger audit,
    ISurgeryAndTransfusionFacts facts,
    IndicatorMeasures measures,
    TimeProvider time)
{
    /// <summary>How far back the culture scan looks for verified results it has not seen.</summary>
    public static readonly TimeSpan CultureLookBack = TimeSpan.FromDays(3);

    public const string CasesLink = "/quality/hai";

    // ------------------------------------------------------------------ device register

    public async Task<IReadOnlyList<DeviceEpisodeDto>> ListDevicesAsync(bool? inPlace, Guid? admissionId, InvasiveDevice? device, CancellationToken ct)
    {
        var query = db.Set<DeviceEpisode>().AsNoTracking().Where(e => (inPlace == null || (inPlace == true ? e.RemovedAt == null : e.RemovedAt != null))
                                                                       && (admissionId == null || e.AdmissionId == admissionId)
                                                                       && (device == null || e.Device == device));
        var rows = await query.OrderByDescending(e => e.InsertedAt).Take(500).ToListAsync(ct);
        return await DtosAsync(rows, ct);
    }

    public async Task<Result<DeviceEpisodeDto>> InsertDeviceAsync(InsertDeviceRequest request, CancellationToken ct)
    {
        var number = request.AdmissionNumber?.Trim().ToUpperInvariant();
        var admission = await db.Set<Admission>().AsNoTracking()
            .Where(a => request.AdmissionId != null ? a.Id == request.AdmissionId : a.AdmissionNumber == number)
            .Select(a => new { a.Id, a.FacilityId, a.PatientId, a.WardId, a.Status, a.AdmittedAt, a.DischargedAt })
            .FirstOrDefaultAsync(ct);
        if (admission is null || admission.Status == AdmissionStatus.Cancelled)
        {
            return Error.Field("admissionNumber", "No admission of this facility has that number.");
        }

        if (request.InsertedAt < admission.AdmittedAt.AddHours(-24) || (admission.DischargedAt is { } left && request.InsertedAt > left))
        {
            return Error.Field("insertedAt", "The insertion must be during the admission (or on the day before it, in casualty or theatre).");
        }

        if (await db.Set<DeviceEpisode>().AnyAsync(e => e.AdmissionId == admission.Id && e.Device == request.Device && e.RemovedAt == null
                                                         && request.Device == InvasiveDevice.Ventilator, ct))
        {
            return Error.Rule("hai.already_ventilated", "The patient is already recorded as ventilated. Record the extubation first.");
        }

        var episode = new DeviceEpisode
        {
            FacilityId = admission.FacilityId,
            PatientId = admission.PatientId,
            AdmissionId = admission.Id,
            Device = request.Device,
            Detail = Clean(request.Detail),
            WardId = admission.WardId,
            InsertedAt = request.InsertedAt,
            InsertedByName = currentUser.DisplayName,
            Notes = Clean(request.Notes),
        };
        db.Add(episode);
        await db.SaveChangesAsync(ct);
        return (await DtosAsync([episode], ct))[0];
    }

    public async Task<Result<DeviceEpisodeDto>> RemoveDeviceAsync(Guid id, RemoveDeviceRequest request, CancellationToken ct)
    {
        var episode = await db.Set<DeviceEpisode>().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (episode is null)
        {
            return Error.NotFound("Device episode", id);
        }

        db.SetExpectedVersion(episode, request.Version);
        var removed = episode.Remove(request.RemovedAt, request.Reason, currentUser.DisplayName, time.GetUtcNow());
        if (removed.IsFailure)
        {
            return removed.Error!;
        }

        await db.SaveChangesAsync(ct);
        return (await DtosAsync([episode], ct))[0];
    }

    // ------------------------------------------------------------------ cases

    public async Task<IReadOnlyList<HaiCaseListItem>> ListCasesAsync(HaiCaseStatus? status, HaiType? type, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var cases = await db.Set<HaiCase>().AsNoTracking()
            .Where(c => (status == null || c.Status == status) && (type == null || c.Type == type) && (from == null || c.EventOn >= from) && (to == null || c.EventOn <= to))
            .OrderBy(c => c.Status).ThenByDescending(c => c.EventOn)
            .Take(500)
            .ToListAsync(ct);
        var uhids = await UhidsAsync(cases.Select(c => c.PatientId), ct);
        var episodes = await EpisodesAsync(cases.Where(c => c.DeviceEpisodeId != null).Select(c => c.DeviceEpisodeId!.Value), ct);
        return cases.Select(c => new HaiCaseListItem(c.Id, c.CaseNumber, c.Type, c.Origin, c.Status, uhids.GetValueOrDefault(c.PatientId), c.EventOn, c.Organism, c.Specimen,
                Evaluate(c, c.DeviceEpisodeId is { } e ? episodes.GetValueOrDefault(e) : null).Meets, c.CreatedAt))
            .ToList();
    }

    public async Task<Result<HaiCaseDetail>> GetCaseAsync(Guid id, CancellationToken ct)
    {
        var c = await db.Set<HaiCase>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null)
        {
            return Error.NotFound("HAI case", id);
        }

        await audit.PatientRecordAccessedAsync(c.PatientId, $"Viewed infection surveillance case {c.CaseNumber}", ct);
        return await DetailAsync(c, ct);
    }

    public async Task<Result<HaiCaseDetail>> RaiseAsync(RaiseHaiCaseRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        if (!await db.Set<Patient>().AnyAsync(p => p.Id == request.PatientId, ct))
        {
            return Error.NotFound(nameof(Patient), request.PatientId);
        }

        DeviceEpisode? episode = null;
        if (request.DeviceEpisodeId is { } episodeId)
        {
            episode = await db.Set<DeviceEpisode>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == episodeId && e.PatientId == request.PatientId, ct);
            if (episode is null || episode.Device != HaiCriteria.DeviceFor(request.Type))
            {
                return Error.Field("deviceEpisodeId", "Choose the patient's device of the kind this infection is associated with.");
            }
        }

        var row = new HaiCase
        {
            FacilityId = facilityId,
            CaseNumber = await numbers.NextAsync(QualityNumberSeries.Hai, facilityId, ct),
            Type = request.Type,
            Origin = HaiCaseOrigin.Manual,
            PatientId = request.PatientId,
            AdmissionId = episode?.AdmissionId ?? await CurrentAdmissionAsync(request.PatientId, ct),
            DeviceEpisodeId = episode?.Id,
            EventOn = request.EventOn,
            Organism = Clean(request.Organism),
            Notes = Clean(request.Notes),
            Status = HaiCaseStatus.Suspected,
        };
        db.Add(row);
        await db.SaveChangesAsync(ct);
        return await DetailAsync(row, ct);
    }

    public async Task<Result<HaiCaseDetail>> ReviewAsync(Guid id, ReviewHaiCaseRequest request, CancellationToken ct)
    {
        var c = await db.Set<HaiCase>().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null)
        {
            return Error.NotFound("HAI case", id);
        }

        db.SetExpectedVersion(c, request.Version);
        if (c.Status != HaiCaseStatus.Suspected)
        {
            return Error.Rule("hai.reviewed", "This case has been reviewed. Reopen it to change the checklist.");
        }

        var known = HaiCriteria.For(c.Type).Select(x => x.Key).ToHashSet();
        var unknown = request.CriteriaMet.Where(k => !known.Contains(k)).ToList();
        if (unknown.Count > 0)
        {
            return Error.Field("criteriaMet", $"Not part of the {c.Type} checklist: {string.Join(", ", unknown)}.");
        }

        if (request.DeviceEpisodeId is { } episodeId)
        {
            var episode = await db.Set<DeviceEpisode>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == episodeId && e.PatientId == c.PatientId, ct);
            if (episode is null || episode.Device != HaiCriteria.DeviceFor(c.Type))
            {
                return Error.Field("deviceEpisodeId", "Choose the patient's device of the kind this infection is associated with.");
            }

            c.AdmissionId = episode.AdmissionId;
        }

        if (c.Type == HaiType.Ssi && request.OperatedOn is { } operated && operated > request.EventOn)
        {
            return Error.Field("operatedOn", "The operation must be on or before the date of event.");
        }

        c.CriteriaMet = [.. request.CriteriaMet.Distinct()];
        c.EventOn = request.EventOn;
        c.DeviceEpisodeId = c.Type == HaiType.Ssi ? null : request.DeviceEpisodeId;
        c.SurgeryBookingId = c.Type == HaiType.Ssi ? request.SurgeryBookingId : null;
        c.SurgeryReference = c.Type == HaiType.Ssi ? Clean(request.SurgeryReference) : null;
        c.OperatedOn = c.Type == HaiType.Ssi ? request.OperatedOn : null;
        c.Notes = Clean(request.Notes);
        await db.SaveChangesAsync(ct);
        return await DetailAsync(c, ct);
    }

    public async Task<Result<HaiCaseDetail>> DecideAsync(Guid id, HaiCaseStatus decision, DecideHaiCaseRequest request, CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Forbidden("auth.required", "Sign in first.");
        }

        var c = await db.Set<HaiCase>().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null)
        {
            return Error.NotFound("HAI case", id);
        }

        db.SetExpectedVersion(c, request.Version);
        var now = time.GetUtcNow();
        var episode = c.DeviceEpisodeId is { } e ? await db.Set<DeviceEpisode>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == e, ct) : null;
        var result = decision switch
        {
            HaiCaseStatus.Confirmed => c.Confirm(Evaluate(c, episode), userId, currentUser.DisplayName, request.Note, now),
            HaiCaseStatus.RuledOut => c.RuleOut(userId, currentUser.DisplayName, request.Note ?? string.Empty, now),
            _ => c.Reopen(request.Note ?? string.Empty),
        };
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await DetailAsync(c, ct);
    }

    public async Task<Result<IReadOnlyList<SurgeryOption>>> SurgeriesAsync(Guid id, CancellationToken ct)
    {
        var c = await db.Set<HaiCase>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null)
        {
            return Error.NotFound("HAI case", id);
        }

        var since = QualityClock.StartOfDayUtc(c.EventOn.AddDays(-HaiCriteria.SsiImplantWindowDays));
        var operations = await facts.PatientSurgeriesAsync(c.PatientId, since, ct);
        return operations.Select(o => new SurgeryOption(o.BookingId, $"{o.BookingNumber} {o.ProcedureName}", QualityClock.LocalDate(o.StartedAt), o.Elective)).ToList();
    }

    // ------------------------------------------------------------------ rates

    public async Task<Result<IReadOnlyList<HaiRateRow>>> RatesAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var today = time.LocalToday();
        var last = new DateOnly((to ?? today).Year, (to ?? today).Month, 1);
        var first = from is { } f ? new DateOnly(f.Year, f.Month, 1) : last.AddMonths(-11);
        if (first > last || last.DayNumber - first.DayNumber > 800)
        {
            return Error.Field("from", "Choose up to 24 months, the first before the last.");
        }

        var cases = await db.Set<HaiCase>().AsNoTracking().Where(c => c.EventOn >= first && c.EventOn < last.AddMonths(1))
            .Select(c => new { c.Type, c.Status, c.EventOn })
            .ToListAsync(ct);
        var rows = new List<HaiRateRow>();
        for (var month = first; month <= last; month = month.AddMonths(1))
        {
            var p = new MeasurePeriod(facilityId, month, Min(month.AddMonths(1).AddDays(-1), today));
            if (p.To < p.From)
            {
                break;
            }

            var patientDays = (await measures.PatientDaysAsync(p, ct)).Days;
            var line = (await measures.DeviceDaysAsync(p, InvasiveDevice.CentralLine, ct)).Days;
            var catheter = (await measures.DeviceDaysAsync(p, InvasiveDevice.UrinaryCatheter, ct)).Days;
            var vent = (await measures.DeviceDaysAsync(p, InvasiveDevice.Ventilator, ct)).Days;
            var operations = facts.Available ? (await facts.SurgeriesAsync(facilityId, p.Start, p.End, ct)).Count : 0;
            var inMonth = cases.Where(c => c.EventOn >= p.From && c.EventOn <= p.To).ToList();
            int Confirmed(HaiType t) => inMonth.Count(c => c.Type == t && c.Status == HaiCaseStatus.Confirmed);
            rows.Add(new HaiRateRow(month, IndicatorMath.PeriodLabel(month, IndicatorFrequency.Monthly), patientDays, line, catheter, vent,
                Confirmed(HaiType.Clabsi), Confirmed(HaiType.Cauti), Confirmed(HaiType.Vap), Confirmed(HaiType.Ssi), operations,
                IndicatorMath.Compute(Confirmed(HaiType.Clabsi), line, 1000), IndicatorMath.Compute(Confirmed(HaiType.Cauti), catheter, 1000),
                IndicatorMath.Compute(Confirmed(HaiType.Vap), vent, 1000), IndicatorMath.Compute(Confirmed(HaiType.Ssi), operations, 100),
                Utilisation(line, patientDays), Utilisation(catheter, patientDays), Utilisation(vent, patientDays),
                inMonth.Count(c => c.Status == HaiCaseStatus.Suspected)));
        }

        return rows;
    }

    /// <summary>Device utilisation ratio (NHSN DUR): device days ÷ patient days, to 2 decimals.</summary>
    private static decimal? Utilisation(int deviceDays, int patientDays) => IndicatorMath.Compute(deviceDays, patientDays, 1);

    // ------------------------------------------------------------------ culture alerts

    /// <summary>
    /// Raises a suspected case for each verified culture with an organism, collected from a patient with the matching device
    /// in place on the day or removed the day before (blood: central line; urine: urinary catheter; sputum: ventilator), or
    /// from a wound, tissue or body fluid within 90 days of an operation (SSI); and alerts the infection control team. A result
    /// raises a case of a type once. Returns the cases raised.
    /// </summary>
    public async Task<int> ScanCulturesAsync(CancellationToken ct)
    {
        var since = time.GetUtcNow() - CultureLookBack;
        var cases = db.Set<HaiCase>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]);
        var samples = db.Set<LabSample>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]);
        var results = await (from r in db.Set<LabResult>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                             join t in db.Set<LabTest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on r.LabTestId equals t.Id
                             join o in db.Set<LabOrder>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on r.LabOrderId equals o.Id
                             where r.ResultType == LabResultType.Culture && r.Status == LabResultStatus.Verified && r.Organism != null && r.VerifiedAt >= since
                                   && !cases.Any(c => c.CultureResultId == r.Id)
                             let collected = samples.Where(s => s.Id == r.SampleId).Select(s => s.CollectedAt).FirstOrDefault()
                             select new { r.Id, o.FacilityId, r.PatientId, r.TestName, r.Organism, t.SpecimenType, Collected = collected ?? o.OrderedAt })
            .ToListAsync(ct);

        var raised = 0;
        foreach (var r in results)
        {
            var eventDay = QualityClock.LocalDate(r.Collected).DayNumber;
            var types = r.SpecimenType switch
            {
                SpecimenType.WholeBlood => [HaiType.Clabsi],
                SpecimenType.Urine => [HaiType.Cauti],
                SpecimenType.Sputum => [HaiType.Vap],
                SpecimenType.Swab or SpecimenType.Tissue or SpecimenType.BodyFluid => new[] { HaiType.Ssi },
                _ => [],
            };
            foreach (var type in types)
            {
                HaiCase? row = null;
                if (HaiCriteria.DeviceFor(type) is { } device)
                {
                    var episodes = await db.Set<DeviceEpisode>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                        .Where(e => e.PatientId == r.PatientId && e.Device == device && e.InsertedAt <= r.Collected)
                        .ToListAsync(ct);
                    var episode = episodes.Select(e => (Episode: e, Span: DeviceDays.Span(e)))
                        .Where(x => x.Span.LastDay is null || x.Span.LastDay >= eventDay - 1)
                        .OrderByDescending(x => x.Episode.InsertedAt)
                        .Select(x => x.Episode)
                        .FirstOrDefault();
                    if (episode is not null)
                    {
                        row = NewAlertCase(r.FacilityId, type, r.PatientId, episode.AdmissionId, r.Id, r.Organism, r.TestName, r.Collected);
                        row.DeviceEpisodeId = episode.Id;
                    }
                }
                else if (facts.Available)
                {
                    var operation = (await facts.PatientSurgeriesAsync(r.PatientId, r.Collected.AddDays(-HaiCriteria.SsiImplantWindowDays), ct))
                        .Where(o => o.CompletedAt <= r.Collected)
                        .OrderByDescending(o => o.CompletedAt)
                        .FirstOrDefault();
                    if (operation is not null)
                    {
                        row = NewAlertCase(r.FacilityId, type, r.PatientId, operation.AdmissionId, r.Id, r.Organism, r.TestName, r.Collected);
                        row.SurgeryBookingId = operation.BookingId;
                        row.SurgeryReference = $"{operation.BookingNumber} {operation.ProcedureName}";
                        row.OperatedOn = QualityClock.LocalDate(operation.StartedAt);
                    }
                }

                if (row is null)
                {
                    continue;
                }

                row.CaseNumber = await numbers.NextAsync(QualityNumberSeries.Hai, r.FacilityId, ct);
                db.Add(row);
                await db.SaveChangesAsync(ct);
                raised++;
                await notifications.NotifyPermissionHoldersAsync(QualityPermissions.HaiManage, r.FacilityId, $"Possible {Name(type)}: {row.CaseNumber}",
                    $"{r.TestName} grew {r.Organism} on a patient {(type == HaiType.Ssi ? "after an operation" : "with a " + DeviceName(type))}. Review it against the definition.",
                    $"{CasesLink}?case={row.Id}", "quality", "warning", ct);
            }
        }

        return raised;
    }

    private static HaiCase NewAlertCase(Guid facilityId, HaiType type, Guid patientId, Guid? admissionId, Guid resultId, string? organism, string specimen, DateTimeOffset collected) =>
        new()
        {
            FacilityId = facilityId,
            Type = type,
            Origin = HaiCaseOrigin.CultureAlert,
            PatientId = patientId,
            AdmissionId = admissionId,
            CultureResultId = resultId,
            Organism = organism,
            Specimen = specimen,
            SpecimenCollectedAt = collected,
            EventOn = QualityClock.LocalDate(collected),
            Status = HaiCaseStatus.Suspected,
        };

    // ------------------------------------------------------------------ helpers

    /// <summary>The checklist against the definition, with the device's eligibility and the days since the operation worked out from the records.</summary>
    internal static HaiEvaluation Evaluate(HaiCase c, DeviceEpisode? episode)
    {
        var eventDay = c.EventOn.DayNumber;
        var span = episode is null ? (DeviceSpan?)null : DeviceDays.Span(episode);
        var eligible = span is { } s && DeviceDays.Eligible(s.FirstDay, s.LastDay, eventDay);
        var inPlace = span is not { LastDay: { } removed } || removed >= eventDay;
        return HaiCriteria.Evaluate(c.Type, new HaiEvidence(c.CriteriaMet.ToHashSet(), eligible, inPlace, c.DaysAfterOperation));
    }

    private async Task<HaiCaseDetail> DetailAsync(HaiCase c, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(p => p.Id == c.PatientId)
            .Select(p => new { p.Uhid, p.FullName }).FirstOrDefaultAsync(ct);
        var admissionNumber = c.AdmissionId is { } a
            ? await db.Set<Admission>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(x => x.Id == a).Select(x => x.AdmissionNumber).FirstOrDefaultAsync(ct)
            : null;
        var device = HaiCriteria.DeviceFor(c.Type);
        var patientEpisodes = device is null
            ? []
            : await db.Set<DeviceEpisode>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(e => e.PatientId == c.PatientId && e.Device == device).OrderByDescending(e => e.InsertedAt).Take(20).ToListAsync(ct);
        var dtos = await DtosAsync(patientEpisodes, ct);
        var episode = patientEpisodes.FirstOrDefault(e => e.Id == c.DeviceEpisodeId);
        var evaluation = Evaluate(c, episode);
        var met = c.CriteriaMet.ToHashSet();
        var span = episode is null ? (DeviceSpan?)null : DeviceDays.Span(episode);
        return new HaiCaseDetail(c.Id, c.CaseNumber, c.Type, c.Origin, c.Status, c.PatientId, patient?.Uhid, patient?.FullName, c.AdmissionId, admissionNumber,
            dtos.FirstOrDefault(d => d.Id == c.DeviceEpisodeId), dtos, c.CultureResultId, c.Organism, c.Specimen, c.SpecimenCollectedAt, c.SurgeryBookingId, c.SurgeryReference,
            c.OperatedOn, c.DaysAfterOperation, c.EventOn,
            device is null ? null : span is { } s && DeviceDays.Eligible(s.FirstDay, s.LastDay, c.EventOn.DayNumber),
            span is { } sp ? DeviceDays.DayOfDevice(sp.FirstDay, c.EventOn.DayNumber) : null,
            HaiCriteria.For(c.Type).Select(x => new HaiCriterionDto(x.Key, x.Group, x.Text, met.Contains(x.Key))).ToList(),
            evaluation.Meets, evaluation.Missing, c.ReviewedByName, c.ReviewedAt, c.DecisionNote, c.Notes, c.Version);
    }

    private async Task<List<DeviceEpisodeDto>> DtosAsync(IReadOnlyCollection<DeviceEpisode> rows, CancellationToken ct)
    {
        var admissionIds = rows.Select(r => r.AdmissionId).Distinct().ToList();
        var admissions = await db.Set<Admission>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(a => admissionIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.AdmissionNumber, ct);
        var wardIds = rows.Where(r => r.WardId != null).Select(r => r.WardId!.Value).Distinct().ToList();
        var wards = await db.Set<Ward>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(w => wardIds.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        var uhids = await UhidsAsync(rows.Select(r => r.PatientId), ct);
        var today = time.LocalToday().DayNumber;
        return rows.Select(e =>
        {
            var span = DeviceDays.Span(e);
            return new DeviceEpisodeDto(e.Id, e.PatientId, uhids.GetValueOrDefault(e.PatientId), e.AdmissionId, admissions.GetValueOrDefault(e.AdmissionId) ?? string.Empty,
                e.WardId is { } w ? wards.GetValueOrDefault(w) : null, e.Device, e.Detail, e.InsertedAt, e.InsertedByName, e.RemovedAt, e.RemovalReason, e.RemovedByName,
                (span.LastDay ?? today) - span.FirstDay + 1, e.Notes, e.Version);
        }).ToList();
    }

    private async Task<Dictionary<Guid, DeviceEpisode>> EpisodesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Set<DeviceEpisode>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(e => list.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
    }

    private async Task<Dictionary<Guid, string>> UhidsAsync(IEnumerable<Guid> patientIds, CancellationToken ct)
    {
        var ids = patientIds.Distinct().ToList();
        return await db.Set<Patient>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.IsVip ? "Restricted record" : p.Uhid, ct);
    }

    private Task<Guid?> CurrentAdmissionAsync(Guid patientId, CancellationToken ct) =>
        db.Set<Admission>().AsNoTracking().Where(a => a.PatientId == patientId && a.Status != AdmissionStatus.Cancelled)
            .OrderByDescending(a => a.AdmittedAt).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Name(HaiType type) => type switch
    {
        HaiType.Clabsi => "CLABSI",
        HaiType.Cauti => "CAUTI",
        HaiType.Vap => "VAP",
        _ => "surgical site infection",
    };

    private static string DeviceName(HaiType type) => type switch
    {
        HaiType.Clabsi => "central line",
        HaiType.Cauti => "urinary catheter",
        _ => "ventilator",
    };
}
