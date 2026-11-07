using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Features.Mvpi;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Quality.Services;

/// <summary>
/// Device adverse event reports to the Materiovigilance Programme of India (MvPI): raised by the quality team from an
/// incident of the register, or for a patient, filled in from the incident and from the devices billed to the patient, and
/// recorded as sent with MvPI's reference. The form names the patient by initials only.
/// </summary>
internal sealed class MvpiService(
    HospitalDbContext db,
    INumberSeries numbers,
    ICurrentUser currentUser,
    IBillingStatusProvider billing,
    TimeProvider time)
{
    public Task<MvpiReport?> FindAsync(Guid id, CancellationToken ct) => db.Set<MvpiReport>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);

    /// <summary>
    /// What a new report starts from. From an incident: its day, place, account, harm (a death is a death on the form) and
    /// patient; for a patient alone, today. Either way, the devices billed to the patient, and the newest implant as the device.
    /// </summary>
    public async Task<Result<MvpiPrefill>> PrefillAsync(Guid? incidentId, Guid? patientId, CancellationToken ct)
    {
        Incident? incident = null;
        if (incidentId is { } id)
        {
            incident = await db.Set<Incident>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
            if (incident is null)
            {
                return Error.NotFound(nameof(Incident), id);
            }

            patientId ??= incident.PatientId;
        }

        Patient? patient = null;
        if (patientId is { } pid)
        {
            patient = await db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid, ct);
            if (patient is null)
            {
                return Error.NotFound(nameof(Patient), pid);
            }
        }

        var today = time.LocalToday();
        var eventOn = incident is null ? today : QualityClock.LocalDate(incident.OccurredAt);
        IReadOnlyList<ChargedDevice> devices = patient is null ? [] : await billing.GetDevicesAsync(patient.Id, ct);
        var died = incident?.Severity == IncidentSeverity.Death;
        var description = incident is null
            ? null
            : string.IsNullOrWhiteSpace(incident.ImmediateAction) ? incident.Description : $"{incident.Description}\n\nAction taken: {incident.ImmediateAction}";
        return new MvpiPrefill(
            incident?.Id,
            incident?.IncidentNumber,
            patient?.Id,
            patient is null ? null : PatientNames.Initials(patient.FullName),
            patient?.Uhid,
            patient?.DateOfBirth is { } dob ? MvpiReport.AgeOn(dob, eventOn) : null,
            patient?.Gender.ToString(),
            eventOn,
            incident is null ? today : QualityClock.LocalDate(incident.ReportedAt),
            incident?.Location,
            description?.Length > 4000 ? description[..4000] : description,
            died ? MvpiSeriousness.Death : MvpiSeriousness.None,
            died ? MvpiPatientOutcome.Fatal : MvpiPatientOutcome.Unknown,
            devices.FirstOrDefault(d => d.IsImplant) is { } implant ? DeviceFrom(implant) : MvpiDevice.Blank,
            devices.Select(d => new ChargedDeviceItem(
                d.Description, d.Device.Brand, d.Device.Manufacturer, d.Device.Specification, d.Device.Batch, d.Device.Serial, d.IsImplant, d.UsedOn))
                .ToList());
    }

    public async Task<Result<MvpiReportDto>> SaveAsync(Guid? id, SaveMvpiReportRequest request, CancellationToken ct)
    {
        Patient? patient = null;
        if (request.PatientId is { } patientId)
        {
            patient = await db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == patientId, ct);
            if (patient is null)
            {
                return Error.Field("patientId", "Choose a registered patient.");
            }
        }

        if (request.IncidentId is { } incidentId && !await db.Set<Incident>().AnyAsync(i => i.Id == incidentId, ct))
        {
            return Error.Field("incidentId", "Choose an incident of this facility's register.");
        }

        MvpiReport? report;
        if (id is { } existing)
        {
            report = await db.Set<MvpiReport>().FirstOrDefaultAsync(r => r.Id == existing, ct);
            if (report is null)
            {
                return Error.NotFound("MvPI report", existing);
            }

            if (report.IncidentId != request.IncidentId)
            {
                return Error.Field("incidentId", "A report stays with the incident it was raised from.");
            }

            db.SetExpectedVersion(report, request.Version);
        }
        else
        {
            if (currentUser.FacilityId is not { } facility)
            {
                return Error.Rule("facility.required", "Choose the facility first.");
            }

            report = new MvpiReport
            {
                FacilityId = facility,
                IncidentId = request.IncidentId,
                ReportedOn = time.LocalToday(),
                Number = await numbers.NextAsync(QualityNumberSeries.Mvpi, facility, ct),
                ReportedBy = currentUser.UserId,
                ReporterName = currentUser.DisplayName ?? currentUser.UserName ?? "Staff",
            };
            db.Add(report);
        }

        var recorded = report.Record(
            new MvpiDetails(
                patient is null ? null : new MvpiPatient(patient.Id, patient.FullName, patient.DateOfBirth, patient.Gender.ToString()),
                request.NoPatient, request.WeightKg, request.EventOn, request.AwareOn, request.Location, request.Description, request.Seriousness,
                request.DeathOn, request.Outcome, request.Device, request.ReporterDesignation, request.ReporterDepartment),
            time.LocalToday());
        if (recorded.IsFailure)
        {
            return recorded.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await DtoAsync(report, ct);
    }

    public async Task<Result<MvpiReportDto>> SubmitAsync(Guid id, SubmitMvpiReportRequest request, CancellationToken ct)
    {
        var report = await db.Set<MvpiReport>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (report is null)
        {
            return Error.NotFound("MvPI report", id);
        }

        db.SetExpectedVersion(report, request.Version);
        var by = string.IsNullOrWhiteSpace(request.SubmittedBy) ? currentUser.DisplayName ?? currentUser.UserName ?? "Quality" : request.SubmittedBy;
        var submitted = report.Submit(request.SubmittedOn, currentUser.UserId, by, request.Reference, time.LocalToday());
        if (submitted.IsFailure)
        {
            return submitted.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await DtoAsync(report, ct);
    }

    public async Task<Result<MvpiReportDto>> CorrectReferenceAsync(Guid id, CorrectMvpiReferenceRequest request, CancellationToken ct)
    {
        var report = await db.Set<MvpiReport>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (report is null)
        {
            return Error.NotFound("MvPI report", id);
        }

        db.SetExpectedVersion(report, request.Version);
        var corrected = report.CorrectReference(request.Reference, request.Reason, currentUser.DisplayName ?? currentUser.UserName ?? "Quality", time.GetUtcNow());
        if (corrected.IsFailure)
        {
            return corrected.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await DtoAsync(report, ct);
    }

    /// <summary>A follow-up report to one already sent: a new draft, numbered and made by whoever starts it.</summary>
    public async Task<Result<MvpiReportDto>> FollowUpAsync(Guid id, CancellationToken ct)
    {
        var original = await db.Set<MvpiReport>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (original is null)
        {
            return Error.NotFound("MvPI report", id);
        }

        var followUp = original.FollowUp(time.LocalToday());
        if (followUp.IsFailure)
        {
            return followUp.Error!;
        }

        var report = followUp.Value;
        report.Number = await numbers.NextAsync(QualityNumberSeries.Mvpi, original.FacilityId, ct);
        report.ReportedBy = currentUser.UserId;
        report.ReporterName = currentUser.DisplayName ?? currentUser.UserName ?? "Staff";
        db.Add(report);
        await db.SaveChangesAsync(ct);
        return await DtoAsync(report, ct);
    }

    /// <summary>The register, newest event first: by state, by the day of the event, or the reports of one incident.</summary>
    public async Task<IReadOnlyList<MvpiReportListItem>> ListAsync(MvpiReportStatus? status, DateOnly? from, DateOnly? to, Guid? incidentId, CancellationToken ct)
    {
        var query = db.Set<MvpiReport>().AsNoTracking();
        if (status is not null)
        {
            query = query.Where(r => r.Status == status);
        }

        if (from is { } start)
        {
            query = query.Where(r => r.EventOn >= start);
        }

        if (to is { } end)
        {
            query = query.Where(r => r.EventOn <= end);
        }

        if (incidentId is { } incident)
        {
            query = query.Where(r => r.IncidentId == incident);
        }

        var reports = await query.OrderByDescending(r => r.EventOn).ThenByDescending(r => r.CreatedAt).Take(500).ToListAsync(ct);
        var patientIds = reports.Where(r => r.PatientId is not null).Select(r => r.PatientId!.Value).Distinct().ToList();
        var uhids = await db.Set<Patient>().AsNoTracking().Where(p => patientIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Uhid, ct);
        var incidentIds = reports.Where(r => r.IncidentId is not null).Select(r => r.IncidentId!.Value).Distinct().ToList();
        var incidents = await db.Set<Incident>().AsNoTracking().Where(i => incidentIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.IncidentNumber, ct);
        var originalIds = reports.Where(r => r.FollowUpOfId is not null).Select(r => r.FollowUpOfId!.Value).Distinct().ToList();
        var originals = await db.Set<MvpiReport>().AsNoTracking().Where(r => originalIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Number, ct);
        return reports.Select(r => new MvpiReportListItem(
                r.Id, r.Number, r.Status, r.ReportedOn, r.EventOn, r.PatientInitials, r.PatientId is { } p ? uhids.GetValueOrDefault(p) : null,
                r.NoPatientInvolved, r.Device.Name, r.Device.Manufacturer, r.IsSerious, r.Outcome,
                r.IncidentId is { } i ? incidents.GetValueOrDefault(i) : null, r.FollowUpOfId is { } o ? originals.GetValueOrDefault(o) : null,
                r.SubmittedOn, r.Reference, r.ReporterName))
            .ToList();
    }

    public async Task<MvpiReportDto> DtoAsync(MvpiReport r, CancellationToken ct)
    {
        var uhid = r.PatientId is { } patientId
            ? await db.Set<Patient>().AsNoTracking().Where(p => p.Id == patientId).Select(p => p.Uhid).FirstOrDefaultAsync(ct)
            : null;
        var incidentNumber = r.IncidentId is { } incidentId
            ? await db.Set<Incident>().AsNoTracking().Where(i => i.Id == incidentId).Select(i => i.IncidentNumber).FirstOrDefaultAsync(ct)
            : null;
        var original = r.FollowUpOfId is { } originalId
            ? await db.Set<MvpiReport>().AsNoTracking().Where(o => o.Id == originalId).Select(o => new { o.Number, o.Reference }).FirstOrDefaultAsync(ct)
            : null;
        var followUps = await db.Set<MvpiReport>().AsNoTracking().Where(f => f.FollowUpOfId == r.Id).OrderBy(f => f.CreatedAt)
            .Select(f => new MvpiReportRef(f.Id, f.Number, f.Status))
            .ToListAsync(ct);
        return new MvpiReportDto(
            r.Id, r.Number, r.Status, r.ReportedOn, r.IncidentId, incidentNumber, r.FollowUpOfId, original?.Number, original?.Reference, followUps,
            r.PatientId, r.NoPatientInvolved, r.PatientInitials, uhid, r.PatientAgeYears, r.PatientSex, r.WeightKg, r.EventOn, r.AwareOn, r.Location,
            r.Description, r.Seriousness, r.DeathOn, r.Outcome, r.Device, r.ReporterName, r.ReporterDesignation, r.ReporterDepartment, r.SubmittedOn,
            r.SubmittedByName, r.Reference, r.ReferenceCorrectionReason, r.ReferenceCorrectedByName, r.ReferenceCorrectedAt,
            r.Status == MvpiReportStatus.Draft ? r.Missing() : new Dictionary<string, string>(), r.Version);
    }

    /// <summary>
    /// The device as billing names it: the brand as its name, the item and its specification as what it is, the maker,
    /// batch and serial; an implant, single use, implanted the day it was used in theatre.
    /// </summary>
    private static MvpiDevice DeviceFrom(ChargedDevice charged)
    {
        var item = charged.Description.StartsWith("Implant: ", StringComparison.Ordinal) ? charged.Description["Implant: ".Length..] : charged.Description;
        var device = charged.Device;
        var generic = device.Specification is { } spec ? (device.Brand is null ? spec : $"{item}: {spec}") : device.Brand is null ? null : item;
        return MvpiDevice.Blank with
        {
            Name = Cap(device.Brand ?? item, 200),
            GenericName = generic is null ? null : Cap(generic, 200),
            Manufacturer = device.Manufacturer,
            LotNumber = device.Batch,
            SerialNumber = device.Serial,
            Implantable = charged.IsImplant,
            ImplantedOn = charged.IsImplant ? charged.UsedOn : null,
            Use = charged.IsImplant ? DeviceUse.SingleUse : DeviceUse.Unknown,
        };
    }

    private static string Cap(string value, int length) => value.Length <= length ? value : value[..length];
}
