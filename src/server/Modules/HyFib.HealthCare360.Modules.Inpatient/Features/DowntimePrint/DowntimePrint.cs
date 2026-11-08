using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.CarePlan;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.DowntimePrint;

public sealed record DowntimeAllergy(string Allergen, AllergySeverity Severity, string? Reaction);

/// <param name="DueTimes">Scheduled doses not yet documented in the window (and earlier ones still due), oldest first.</param>
/// <param name="DosesHeld">No dose is given until a doctor countersigns the order or decides a nurse's stop.</param>
public sealed record DowntimeMedication(
    Guid OrderId,
    string DrugName,
    string Dose,
    string? Route,
    string Frequency,
    bool IsAsNeeded,
    bool IsHighAlert,
    bool IsControlledDrug,
    bool DosesHeld,
    IReadOnlyList<DateTimeOffset> DueTimes);

public sealed record DowntimeNews2(int Score, News2Response Response, bool IsComplete, DateTimeOffset RecordedAt);

public sealed record DowntimeCarePlanItem(string Title, string Goal, IReadOnlyList<CarePlanIntervention> Interventions);

/// <summary>
/// One patient on the downtime sheet. For a restricted (VIP) patient the name reads "Restricted patient" and the diagnosis
/// is left out unless the one printing may open restricted records, as on the bed board; allergies, medicines and tasks
/// are printed, since the ward cares for the patient on paper.
/// </summary>
public sealed record DowntimePatient(
    Guid AdmissionId,
    string AdmissionNumber,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    string? BedLabel,
    string? ConsultantName,
    DateTimeOffset AdmittedAt,
    int DayOfStay,
    string? ProvisionalDiagnosis,
    bool IsMlc,
    AdmissionStatus Status,
    AllergyRecordingStatus AllergyStatus,
    IReadOnlyList<DowntimeAllergy> Allergies,
    IReadOnlyList<DowntimeMedication> Medications,
    DowntimeNews2? News2,
    IReadOnlyList<string> PendingTasks,
    IReadOnlyList<DowntimeCarePlanItem> CarePlan);

public sealed record DowntimeWard(Guid WardId, string Code, string Name, WardType Type, BedCounts Counts, IReadOnlyList<DowntimePatient> Patients);

/// <summary>What the wards print before a planned downtime (docs/modules/downtime.md, Downtime procedure step 2).</summary>
public sealed record DowntimeWardPack(
    DateTimeOffset GeneratedAt,
    DateTimeOffset WindowEndsAt,
    string? FacilityName,
    string? PrintedByName,
    IReadOnlyList<DowntimeWard> Wards);

/// <summary>
/// Builds the pre-downtime ward print: per ward the census, and per patient the allergies, the active medication orders
/// with the doses due in the next <see cref="WindowHours"/> hours, the pending tasks and the latest NEWS2. One ward, or
/// every ward of the facility as one pack.
/// </summary>
public sealed class DowntimePrintService(
    HospitalDbContext db,
    IPermissionService permissions,
    EarlyWarningService earlyWarning,
    MarService mar,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public const int WindowHours = 8;

    public async Task<DowntimeWardPack> BuildAsync(Guid? wardId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var windowEnd = now.AddHours(WindowHours);
        var today = IndiaClock.LocalDate(now);

        var wards = await db.Set<Ward>().AsNoTracking()
            .Where(w => w.IsActive && (wardId == null || w.Id == wardId))
            .OrderBy(w => w.DisplayOrder).ThenBy(w => w.Name)
            .ToListAsync(ct);
        var wardIds = wards.Select(w => w.Id).ToList();
        var beds = await db.Set<Bed>().AsNoTracking().Where(b => b.IsActive && wardIds.Contains(b.WardId)).ToListAsync(ct);

        var stays = await (
                from a in db.Set<Admission>().AsNoTracking()
                join p in db.Set<Patient>() on a.PatientId equals p.Id
                join b in db.Set<Bed>() on a.BedId equals b.Id
                join c in db.Set<Practitioner>() on a.ConsultantPractitionerId equals c.Id into consultants
                from c in consultants.DefaultIfEmpty()
                where (a.Status == AdmissionStatus.Admitted || a.Status == AdmissionStatus.DischargeInitiated) && a.WardId != null && wardIds.Contains(a.WardId!.Value)
                orderby b.DisplayOrder, b.Label
                select new
                {
                    Admission = a,
                    p.Uhid,
                    p.FullName,
                    p.Gender,
                    p.DateOfBirth,
                    p.IsVip,
                    BedLabel = b.Label,
                    ConsultantName = c != null ? c.DisplayName : null,
                })
            .ToListAsync(ct);
        var admissionIds = stays.Select(s => s.Admission.Id).ToList();
        var patientIds = stays.Select(s => s.Admission.PatientId).Distinct().ToList();

        var allergies = (await db.Set<Allergy>().AsNoTracking()
                .Where(x => patientIds.Contains(x.PatientId) && x.Status == AllergyStatus.Active)
                .OrderByDescending(x => x.Severity).ThenBy(x => x.Allergen)
                .Select(x => new { x.PatientId, x.Allergen, x.Severity, x.Reaction })
                .ToListAsync(ct))
            .ToLookup(x => x.PatientId);
        var noKnownAllergies = (await db.Set<NoKnownAllergiesStatement>().AsNoTracking()
                .Where(x => patientIds.Contains(x.PatientId)).Select(x => x.PatientId).ToListAsync(ct))
            .ToHashSet();

        var orders = (await db.Set<MedicationOrder>().AsNoTracking()
                .Where(o => admissionIds.Contains(o.AdmissionId) && o.Status == MedicationOrderStatus.Active)
                .OrderBy(o => o.StartAt)
                .ToListAsync(ct))
            .ToLookup(o => o.AdmissionId);
        var doses = (await db.Set<MedicationAdministration>().AsNoTracking()
                .Where(m => admissionIds.Contains(m.AdmissionId) && m.Status == MarStatus.Due && m.ScheduledAt < windowEnd)
                .OrderBy(m => m.ScheduledAt)
                .Select(m => new { m.AdmissionId, m.MedicationOrderId, m.ScheduledAt })
                .ToListAsync(ct))
            .ToLookup(m => m.AdmissionId);
        var grace = await mar.GraceAsync(currentUser.FacilityId, ct);

        var news2 = await earlyWarning.LatestAsync(stays.Select(s => (s.Admission.Id, s.Admission.PatientId, s.Admission.AdmittedAt, s.DateOfBirth)).ToList(), ct);
        var escalations = (await db.Set<EarlyWarningEscalation>().AsNoTracking()
                .Where(e => admissionIds.Contains(e.AdmissionId) && e.Status == EscalationStatus.Open)
                .Select(e => new { e.AdmissionId, e.Summary, e.VitalsRecordedAt })
                .ToListAsync(ct))
            .ToLookup(e => e.AdmissionId);
        var transfers = (await (
                    from t in db.Set<TransferRequest>().AsNoTracking()
                    join w in db.Set<Ward>() on t.ToWardId equals w.Id
                    where admissionIds.Contains(t.AdmissionId)
                          && (t.Status == TransferRequestStatus.Pending || t.Status == TransferRequestStatus.MovedAwaitingAcceptance)
                    select new { t.AdmissionId, t.Status, WardName = w.Name })
                .ToListAsync(ct))
            .ToLookup(t => t.AdmissionId);
        var dutyDate = NursingShifts.DutyDate(now);
        var since = dutyDate.AddDays(-1);
        var handoverTasks = (await db.Set<ShiftHandover>().AsNoTracking()
                .Where(h => admissionIds.Contains(h.AdmissionId) && h.PendingTasks != null && h.DutyDate >= since)
                .Select(h => new { h.AdmissionId, h.HandedOverAt, h.PendingTasks })
                .ToListAsync(ct))
            .GroupBy(h => h.AdmissionId)
            .ToDictionary(g => g.Key, g => g.MaxBy(h => h.HandedOverAt)!.PendingTasks!);
        var carePlans = (await CarePlanQueries.ForAdmissionsAsync(db, admissionIds, true, now, ct)).ToLookup(p => p.AdmissionId);

        var canSeeRestricted = currentUser.UserId is { } userId && await permissions.HasPermissionAsync(userId, PatientsPermissions.ReadRestricted, ct);
        var patients = stays.Select(s =>
        {
            var a = s.Admission;
            var patientAllergies = allergies[a.PatientId].Select(x => new DowntimeAllergy(x.Allergen, x.Severity, x.Reaction)).ToList();
            var stayDoses = doses[a.Id].ToList();
            var medications = orders[a.Id].Select(o => new DowntimeMedication(o.Id, o.DrugName, o.Dose, o.Route, o.Frequency, o.IsAsNeeded, o.IsHighAlert,
                o.IsControlledDrug, o.DosesHeld, stayDoses.Where(d => d.MedicationOrderId == o.Id).Select(d => d.ScheduledAt).ToList())).ToList();
            var tasks = new List<string>();
            tasks.AddRange(escalations[a.Id].Select(e => $"{e.Summary}: escalated at {IndiaClock.LocalTime(e.VitalsRecordedAt):HH\\:mm}, waiting for a response"));
            tasks.AddRange(transfers[a.Id].Select(t => t.Status == TransferRequestStatus.Pending
                ? $"Transfer to {t.WardName} waiting for the receiving ward"
                : $"Urgent transfer to {t.WardName}: acceptance to be recorded"));
            var overdue = stayDoses.Count(d => d.ScheduledAt < now - grace);
            if (overdue > 0)
            {
                tasks.Add($"{overdue} {(overdue == 1 ? "dose" : "doses")} overdue on the MAR");
            }

            var waiting = orders[a.Id].Count(o => o.AwaitingCountersignature || o.StopPending);
            if (waiting > 0)
            {
                tasks.Add($"{waiting} medication {(waiting == 1 ? "order waits" : "orders wait")} for a doctor's countersignature");
            }

            tasks.AddRange(carePlans[a.Id].Where(p => !p.EvaluatedThisShift).Select(p => $"Care plan to evaluate this shift: {p.Title}"));
            if (handoverTasks.TryGetValue(a.Id, out var handover))
            {
                tasks.Add($"From the last handover: {handover}");
            }

            var withheld = s.IsVip && !canSeeRestricted;
            return (a.WardId, Patient: new DowntimePatient(a.Id, a.AdmissionNumber, s.Uhid, withheld ? WardRoundQueries.RestrictedName : s.FullName, s.Gender,
                s.DateOfBirth is { } dob ? Age.Format(dob, today) : null, s.BedLabel, s.ConsultantName, a.AdmittedAt,
                today.DayNumber - IndiaClock.LocalDate(a.AdmittedAt).DayNumber + 1, withheld ? null : a.ProvisionalDiagnosis, a.IsMlc, a.Status,
                NoKnownAllergiesStatement.StatusOf(patientAllergies.Count > 0, noKnownAllergies.Contains(a.PatientId)), patientAllergies, medications,
                news2.TryGetValue(a.Id, out var score) ? new DowntimeNews2(score.Score, score.Response, score.IsComplete, score.RecordedAt) : null,
                tasks, carePlans[a.Id].Select(p => new DowntimeCarePlanItem(p.Title, p.Goal, p.Interventions)).ToList()));
        }).ToLookup(x => x.WardId, x => x.Patient);

        var facilityName = currentUser.FacilityId is { } facilityId
            ? await db.Set<Facility>().AsNoTracking().Where(f => f.Id == facilityId).Select(f => f.Name).FirstOrDefaultAsync(ct)
            : null;
        await audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, "inpatient.downtime_print",
            wardId is null
                ? $"Printed the downtime pack of every ward ({admissionIds.Count} patients)"
                : $"Printed the downtime sheet of {wards.FirstOrDefault()?.Name ?? "a ward"} ({admissionIds.Count} patients)")
        {
            EntityType = nameof(Ward),
            EntityId = wardId?.ToString(),
        }, ct);

        return new DowntimeWardPack(now, windowEnd, facilityName, currentUser.DisplayName, wards.Select(w => new DowntimeWard(w.Id, w.Code, w.Name, w.Type,
            BedCounts.From(beds.Where(b => b.WardId == w.Id).Select(b => b.Status)), patients[w.Id].ToList())).ToList());
    }
}

/// <summary>GET /api/v1/inpatient/downtime-print?wardId — one ward's sheet, or without a ward every ward's as one pack (<c>nursing.chart</c>).</summary>
internal static class DowntimePrintEndpoints
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/inpatient/downtime-print", async (Guid? wardId, DowntimePrintService service, CancellationToken ct) =>
                TypedResults.Ok(await service.BuildAsync(wardId, ct)))
            .WithTags("Inpatient")
            .RequirePermission(InpatientPermissions.NursingChart);
}
