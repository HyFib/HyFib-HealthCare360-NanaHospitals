using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>
/// Read side of ward and bed management: the live bed board (WB-02), current occupancy, beds available for an
/// admission or transfer, and the housekeeping queue. Restricted (VIP) patients appear as "Restricted patient" unless the
/// user may open restricted records, so the board never leaks a confidential admission to the whole ward.
/// </summary>
public sealed class BedBoardService(HospitalDbContext db, IPermissionService permissions, MarService mar, ICurrentUser currentUser, TimeProvider time)
{
    /// <summary>Beds waiting longer than this for cleaning are flagged (turnaround target).</summary>
    public static readonly TimeSpan CleaningTarget = TimeSpan.FromMinutes(60);

    public async Task<IReadOnlyList<BedBoardWard>> BoardAsync(Guid? wardId, CancellationToken ct)
    {
        var wards = await db.Set<Ward>().AsNoTracking()
            .Where(w => w.IsActive && (wardId == null || w.Id == wardId))
            .OrderBy(w => w.DisplayOrder).ThenBy(w => w.Name)
            .ToListAsync(ct);
        var wardIds = wards.Select(w => w.Id).ToList();
        var beds = await db.Set<Bed>().AsNoTracking()
            .Where(b => b.IsActive && wardIds.Contains(b.WardId))
            .OrderBy(b => b.DisplayOrder).ThenBy(b => b.Label)
            .ToListAsync(ct);
        var rooms = await db.Set<Room>().AsNoTracking().Where(r => wardIds.Contains(r.WardId)).ToDictionaryAsync(r => r.Id, r => r.Number, ct);

        var now = time.GetUtcNow();
        var today = time.Today();
        var admissionIds = beds.Where(b => b.CurrentAdmissionId != null).Select(b => b.CurrentAdmissionId!.Value).ToList();
        var occupants = await OccupantsAsync(admissionIds, now, today, ct);
        var clinicalAlerts = await ClinicalAlertsAsync(admissionIds, now, ct);

        return wards.Select(w =>
        {
            var wardBeds = beds.Where(b => b.WardId == w.Id).ToList();
            var items = wardBeds.Select(b =>
            {
                var alerts = new List<BedAlert>();
                BedOccupant? occupant = null;
                if (b.CurrentAdmissionId is { } admissionId && occupants.TryGetValue(admissionId, out var found))
                {
                    occupant = found.Occupant;
                    alerts.AddRange(OccupantAlerts(found.Occupant, found.IsMlc, found.IsRestricted, today));
                    alerts.AddRange(clinicalAlerts.GetValueOrDefault(admissionId, []));
                }

                if (b.IsReservationExpired(now))
                {
                    alerts.Add(BedAlert.ReservationExpired);
                }

                if (b.Status == BedStatus.Cleaning && b.StatusChangedAt is { } since && now - since > CleaningTarget)
                {
                    alerts.Add(BedAlert.CleaningDelayed);
                }

                return new BedBoardItem(b.Id, b.Label, b.RoomId is { } roomId ? rooms.GetValueOrDefault(roomId) : null, b.Status, b.StatusReason,
                    b.StatusChangedAt, b.HasOxygen, b.HasVentilator, b.HasMonitor, b.ReservedUntil, occupant, alerts);
            }).ToList();
            return new BedBoardWard(w.Id, w.Code, w.Name, w.Type, w.Floor, w.NurseStation, BedCounts.From(wardBeds.Select(b => b.Status)), items);
        }).ToList();
    }

    public async Task<OccupancyReport> OccupancyAsync(CancellationToken ct)
    {
        var wards = await db.Set<Ward>().AsNoTracking()
            .Where(w => w.IsActive)
            .OrderBy(w => w.DisplayOrder).ThenBy(w => w.Name)
            .Select(w => new { w.Id, w.Code, w.Name, w.Type })
            .ToListAsync(ct);
        var counts = await db.Set<Bed>().AsNoTracking()
            .Where(b => b.IsActive)
            .GroupBy(b => new { b.WardId, b.Status })
            .Select(g => new { g.Key.WardId, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        var byWard = wards
            .Select(w => new WardOccupancy(w.Id, w.Code, w.Name, w.Type,
                BedCounts.From(counts.Where(c => c.WardId == w.Id).SelectMany(c => Enumerable.Repeat(c.Status, c.Count)))))
            .ToList();
        var byType = byWard.GroupBy(w => w.Type)
            .Select(g => new WardTypeOccupancy(g.Key, BedCounts.Sum(g.Select(w => w.Counts))))
            .OrderBy(t => t.Type)
            .ToList();
        return new OccupancyReport(time.GetUtcNow(), BedCounts.Sum(byWard.Select(w => w.Counts)), byWard, byType);
    }

    /// <summary>Beds an admission or transfer may use, with ward eligibility for the given patient.</summary>
    public async Task<IReadOnlyList<AvailableBedDto>> AvailableAsync(Guid? patientId, WardType? wardType, Guid? wardId, CancellationToken ct)
    {
        var rows = await (
                from b in db.Set<Bed>().AsNoTracking()
                join w in db.Set<Ward>() on b.WardId equals w.Id
                join r in db.Set<Room>() on b.RoomId equals r.Id into rooms
                from r in rooms.DefaultIfEmpty()
                where b.IsActive && w.IsActive && (b.Status == BedStatus.Vacant || b.Status == BedStatus.Reserved)
                      && (wardType == null || w.Type == wardType) && (wardId == null || w.Id == wardId)
                orderby w.DisplayOrder, w.Name, b.DisplayOrder, b.Label
                select new { Bed = b, WardName = w.Name, w.Type, w.GenderPolicy, RoomNumber = r != null ? r.Number : null })
            .ToListAsync(ct);

        var patient = patientId is { } id
            ? await db.Set<Patient>().AsNoTracking().Where(p => p.Id == id).Select(p => new { p.Id, p.Gender, p.DateOfBirth }).FirstOrDefaultAsync(ct)
            : null;
        var now = time.GetUtcNow();
        var today = time.Today();

        return rows
            .Where(x => patient is null
                ? x.Bed.Status == BedStatus.Vacant || x.Bed.IsReservationExpired(now)
                : x.Bed.IsAvailableFor(patient.Id, now))
            .Select(x =>
            {
                var eligibility = patient is null
                    ? Result.Success()
                    : WardEligibility.Check(x.Type, x.GenderPolicy, patient.Gender, patient.DateOfBirth, today);
                return new AvailableBedDto(x.Bed.Id, x.Bed.Label, x.Bed.WardId, x.WardName, x.Type, x.RoomNumber, x.Bed.Status, x.Bed.HasOxygen,
                    x.Bed.HasVentilator, x.Bed.HasMonitor, eligibility.IsSuccess, eligibility.Error?.Message);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<HousekeepingTaskDto>> HousekeepingAsync(HousekeepingStatus? status, Guid? wardId, CancellationToken ct)
    {
        var query =
            from t in db.Set<HousekeepingTask>().AsNoTracking()
            join b in db.Set<Bed>() on t.BedId equals b.Id
            join w in db.Set<Ward>() on t.WardId equals w.Id
            select new { Task = t, b.Label, WardName = w.Name };
        query = status is { } s
            ? query.Where(x => x.Task.Status == s)
            : query.Where(x => x.Task.Status == HousekeepingStatus.Pending || x.Task.Status == HousekeepingStatus.InProgress);
        if (wardId is { } ward)
        {
            query = query.Where(x => x.Task.WardId == ward);
        }

        var rows = await query.OrderBy(x => x.Task.RequestedAt).Take(500).ToListAsync(ct);
        var now = time.GetUtcNow();
        return rows.Select(x => ToTaskDto(x.Task, x.Label, x.WardName, now)).ToList();
    }

    internal static async Task<HousekeepingTaskDto> TaskDtoAsync(HospitalDbContext db, HousekeepingTask task, DateTimeOffset now, CancellationToken ct)
    {
        var bedLabel = await db.Set<Bed>().Where(b => b.Id == task.BedId).Select(b => b.Label).FirstOrDefaultAsync(ct);
        var wardName = await db.Set<Ward>().Where(w => w.Id == task.WardId).Select(w => w.Name).FirstOrDefaultAsync(ct);
        return ToTaskDto(task, bedLabel ?? string.Empty, wardName ?? string.Empty, now);
    }

    private static HousekeepingTaskDto ToTaskDto(HousekeepingTask t, string bedLabel, string wardName, DateTimeOffset now) => new(
        t.Id, t.BedId, bedLabel, t.WardId, wardName, t.AdmissionId, t.Trigger, t.Status, t.RequestedAt, t.StartedAt, t.CompletedAt,
        t.TurnaroundMinutes, (int)Math.Max(0, ((t.CompletedAt ?? now) - t.RequestedAt).TotalMinutes), t.Notes);

    private static IEnumerable<BedAlert> OccupantAlerts(BedOccupant occupant, bool isMlc, bool isRestricted, DateOnly today)
    {
        if (isMlc)
        {
            yield return BedAlert.Mlc;
        }

        if (isRestricted)
        {
            yield return BedAlert.Restricted;
        }

        if (occupant.Status == AdmissionStatus.DischargeInitiated)
        {
            yield return BedAlert.DischargeInitiated;
        }
        else if (occupant.ExpectedDischargeDate is { } expected && expected <= today)
        {
            yield return BedAlert.DischargeDueToday;
        }
    }

    private async Task<Dictionary<Guid, (BedOccupant Occupant, bool IsMlc, bool IsRestricted)>> OccupantsAsync(
        List<Guid> admissionIds, DateTimeOffset now, DateOnly today, CancellationToken ct)
    {
        if (admissionIds.Count == 0)
        {
            return new();
        }

        var rows = await (
                from a in db.Set<Admission>().AsNoTracking()
                join p in db.Set<Patient>() on a.PatientId equals p.Id
                join pr in db.Set<Practitioner>() on a.ConsultantPractitionerId equals pr.Id into consultants
                from pr in consultants.DefaultIfEmpty()
                where admissionIds.Contains(a.Id)
                select new
                {
                    a.Id,
                    a.AdmissionNumber,
                    a.PatientId,
                    p.Uhid,
                    p.FullName,
                    p.Gender,
                    p.DateOfBirth,
                    p.IsVip,
                    ConsultantName = pr != null ? pr.DisplayName : null,
                    a.AdmittedAt,
                    a.ExpectedDischargeDate,
                    a.Status,
                    a.PayerType,
                    a.IsMlc,
                })
            .ToListAsync(ct);

        var canSeeRestricted = currentUser.UserId is { } userId && await permissions.HasPermissionAsync(userId, PatientsPermissions.ReadRestricted, ct);
        return rows.ToDictionary(r => r.Id, r => (
            new BedOccupant(r.Id, r.AdmissionNumber, r.PatientId, r.Uhid, r.IsVip && !canSeeRestricted ? "Restricted patient" : r.FullName, r.Gender,
                r.DateOfBirth is { } dob ? Age.Format(dob, today) : null, r.ConsultantName, r.AdmittedAt, LengthOfStay.Days(r.AdmittedAt, now),
                r.ExpectedDischargeDate, r.Status, r.PayerType),
            r.IsMlc,
            r.IsVip));
    }

    private async Task<Dictionary<Guid, List<BedAlert>>> ClinicalAlertsAsync(List<Guid> admissionIds, DateTimeOffset now, CancellationToken ct)
    {
        var alerts = admissionIds.Distinct().ToDictionary(id => id, _ => new List<BedAlert>());
        if (alerts.Count == 0)
        {
            return alerts;
        }

        // Latest assessment of each type per admission (NOT EXISTS a newer one).
        var latest = await db.Set<NursingAssessment>().AsNoTracking()
            .Where(x => admissionIds.Contains(x.AdmissionId)
                        && !db.Set<NursingAssessment>().Any(y => y.AdmissionId == x.AdmissionId && y.Type == x.Type && y.AssessedAt > x.AssessedAt))
            .Select(x => new { x.AdmissionId, x.Type, x.Risk })
            .ToListAsync(ct);
        foreach (var a in latest)
        {
            var alert = a.Type switch
            {
                AssessmentType.MorseFall when a.Risk >= RiskLevel.High => BedAlert.HighFallRisk,
                AssessmentType.Braden when a.Risk >= RiskLevel.High => BedAlert.PressureInjuryRisk,
                AssessmentType.Pain when a.Risk >= RiskLevel.High => BedAlert.SeverePain,
                _ => (BedAlert?)null,
            };
            if (alert is { } value)
            {
                alerts[a.AdmissionId].Add(value);
            }
        }

        var cutoff = now - await mar.GraceAsync(currentUser.FacilityId, ct);
        var overdue = await db.Set<MedicationAdministration>().AsNoTracking()
            .Where(m => admissionIds.Contains(m.AdmissionId) && m.Status == MarStatus.Due && m.ScheduledAt < cutoff)
            .Select(m => m.AdmissionId)
            .Distinct()
            .ToListAsync(ct);
        foreach (var id in overdue)
        {
            alerts[id].Add(BedAlert.MedicationOverdue);
        }

        return alerts;
    }
}
