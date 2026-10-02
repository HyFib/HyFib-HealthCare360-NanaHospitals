using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>Builds the physician desktop worklist (EMR-01) for one practitioner and day, within the current facility.</summary>
public sealed class WorklistQueries(HospitalDbContext db, TimeProvider time)
{
    private const int ListLimit = 50;

    public async Task<EmrWorklist> BuildAsync(DateOnly date, Guid? practitionerId, Guid? departmentId, CancellationToken ct)
    {
        departmentId ??= practitionerId is { } pid
            ? await db.Set<Practitioner>().Where(p => p.Id == pid).Select(p => p.DepartmentId).FirstOrDefaultAsync(ct)
            : null;

        return new EmrWorklist(
            date,
            practitionerId,
            departmentId,
            await QueueAsync(date, practitionerId, departmentId, ct),
            await AppointmentsAsync(date, practitionerId, departmentId, ct),
            practitionerId is { } p1 ? await OpenEncountersAsync(p1, ct) : [],
            practitionerId is { } p2 ? await UnsignedNotesAsync(p2, ct) : [],
            practitionerId is { } p3 ? await DraftPrescriptionsAsync(p3, ct) : [],
            practitionerId is { } p4 ? await ResultsAsync(p4, ct) : [],
            practitionerId is { } p5 ? await DeficienciesAsync(p5, ct) : []);
    }

    private async Task<List<WorklistQueueEntry>> QueueAsync(DateOnly date, Guid? practitionerId, Guid? departmentId, CancellationToken ct)
    {
        if (departmentId is null && practitionerId is null)
        {
            return [];
        }

        var now = time.GetUtcNow();
        var tokens = db.Set<QueueToken>().AsNoTracking().Where(t => t.TokenDate == date && t.Status != TokenStatus.Cancelled);
        if (departmentId is not null)
        {
            tokens = tokens.Where(t => t.DepartmentId == departmentId);
        }

        if (practitionerId is not null)
        {
            tokens = tokens.Where(t => t.PractitionerId == null || t.PractitionerId == practitionerId);
        }

        var rows = await (
                from t in tokens
                join p in db.Set<Patient>() on t.PatientId equals p.Id
                join v in db.Set<Visit>() on t.VisitId equals v.Id
                // Enums are stored as text: order by explicit comparisons (in service, called, waiting by priority, done last).
                orderby t.Status == TokenStatus.InService descending, t.Status == TokenStatus.Called descending, t.Status == TokenStatus.Completed,
                    t.Priority == TokenPriority.Emergency descending, t.Priority == TokenPriority.Priority descending, t.Sequence
                select new
                {
                    Token = t,
                    p.Uhid,
                    p.FullName,
                    p.Gender,
                    p.DateOfBirth,
                    v.ChiefComplaint,
                    v.IsFollowUp,
                    Encounter = db.Set<Encounter>().Where(e => e.VisitId == t.VisitId).OrderByDescending(e => e.StartedAt)
                        .Select(e => new { e.Id, e.Status }).FirstOrDefault(),
                    HasAllergies = db.Set<Allergy>().Any(a => a.PatientId == t.PatientId && a.Status == AllergyStatus.Active),
                    HasCriticalAlerts = db.Set<PatientAlert>().Any(a => a.PatientId == t.PatientId && a.IsActive && a.Severity == AlertSeverity.Critical),
                })
            .Take(200)
            .ToListAsync(ct);

        return rows.Select(r => new WorklistQueueEntry(
                r.Token.Id, r.Token.Label, r.Token.Status, r.Token.Priority, r.Token.VisitId, r.Token.PatientId, r.Uhid, r.FullName, r.Gender,
                r.DateOfBirth is { } dob ? Age.Format(dob, date) : null, r.ChiefComplaint, r.IsFollowUp, r.Token.IssuedAt,
                (int)Math.Max(0, ((r.Token.CalledAt ?? now) - r.Token.IssuedAt).TotalMinutes), r.Encounter?.Id, r.Encounter?.Status,
                r.HasAllergies, r.HasCriticalAlerts))
            .ToList();
    }

    private async Task<List<WorklistAppointment>> AppointmentsAsync(DateOnly date, Guid? practitionerId, Guid? departmentId, CancellationToken ct)
    {
        if (departmentId is null && practitionerId is null)
        {
            return [];
        }

        var rows = await Appointments(date, practitionerId, departmentId).Take(200).ToListAsync(ct);
        return rows.Select(r => new WorklistAppointment(
                r.Appointment.Id, r.Appointment.Number, r.Appointment.StartsAt, r.Appointment.EndsAt, r.Appointment.Status, r.Appointment.VisitType,
                r.Appointment.IsTelehealth, r.Appointment.Reason, r.Appointment.PatientId, r.Uhid, r.FullName, r.Gender,
                r.DateOfBirth is { } dob ? Age.Format(dob, date) : null, r.Appointment.PractitionerId, r.PractitionerName, r.Appointment.VisitId,
                r.Appointment.CheckedInAt, r.TokenLabel, r.EncounterId, r.EncounterStatus, r.HasAllergies, r.HasCriticalAlerts))
            .ToList();
    }

    /// <summary>
    /// The doctor's bookings for the day, or the department's when no doctor is chosen, in slot order. Cancelled and
    /// rescheduled bookings are left out (the replacement booking is listed).
    /// </summary>
    internal IQueryable<AppointmentRow> Appointments(DateOnly date, Guid? practitionerId, Guid? departmentId)
    {
        var (from, to) = ClinicalClock.RangeUtc(date, date);
        var appointments = db.Set<Appointment>().AsNoTracking()
            .Where(a => a.StartsAt >= from && a.StartsAt < to && a.Status != AppointmentStatus.Cancelled && a.Status != AppointmentStatus.Rescheduled);
        appointments = practitionerId is { } doctor
            ? appointments.Where(a => a.PractitionerId == doctor)
            : appointments.Where(a => a.DepartmentId == departmentId);

        return from a in appointments
               join p in db.Set<Patient>() on a.PatientId equals p.Id
               join pr in db.Set<Practitioner>() on a.PractitionerId equals pr.Id into doctors
               from pr in doctors.DefaultIfEmpty()
               let encounter = db.Set<Encounter>().Where(e => a.VisitId != null && e.VisitId == a.VisitId).OrderByDescending(e => e.StartedAt).FirstOrDefault()
               orderby a.StartsAt, a.SeatNumber
               select new AppointmentRow
               {
                   Appointment = a,
                   Uhid = p.Uhid,
                   FullName = p.FullName,
                   Gender = p.Gender,
                   DateOfBirth = p.DateOfBirth,
                   PractitionerName = pr != null ? pr.DisplayName : null,
                   TokenLabel = db.Set<QueueToken>().Where(t => a.VisitId != null && t.VisitId == a.VisitId && t.Status != TokenStatus.Cancelled)
                       .OrderByDescending(t => t.IssuedAt).Select(t => t.Label).FirstOrDefault(),
                   EncounterId = encounter != null ? encounter.Id : null,
                   EncounterStatus = encounter != null ? encounter.Status : null,
                   HasAllergies = db.Set<Allergy>().Any(x => x.PatientId == a.PatientId && x.Status == AllergyStatus.Active),
                   HasCriticalAlerts = db.Set<PatientAlert>().Any(x => x.PatientId == a.PatientId && x.IsActive && x.Severity == AlertSeverity.Critical),
               };
    }

    private Task<List<WorklistEncounter>> OpenEncountersAsync(Guid practitionerId, CancellationToken ct) =>
        (from e in db.Set<Encounter>().AsNoTracking()
         join p in db.Set<Patient>() on e.PatientId equals p.Id
         where e.PractitionerId == practitionerId && e.Status == EncounterStatus.InProgress
         orderby e.StartedAt descending
         select new WorklistEncounter(e.Id, e.PatientId, p.Uhid, p.FullName, e.Type, e.StartedAt, e.ChiefComplaint))
        .Take(ListLimit)
        .ToListAsync(ct);

    private Task<List<WorklistNote>> UnsignedNotesAsync(Guid practitionerId, CancellationToken ct) =>
        (from n in db.Set<ClinicalNote>().AsNoTracking()
         join p in db.Set<Patient>() on n.PatientId equals p.Id
         where n.AuthorPractitionerId == practitionerId && !n.IsSigned
         orderby n.CreatedAt
         select new WorklistNote(n.Id, n.PatientId, p.Uhid, p.FullName, n.Type, n.Title, n.CreatedAt))
        .Take(ListLimit)
        .ToListAsync(ct);

    private Task<List<WorklistPrescription>> DraftPrescriptionsAsync(Guid practitionerId, CancellationToken ct) =>
        (from rx in db.Set<Prescription>().AsNoTracking()
         join p in db.Set<Patient>() on rx.PatientId equals p.Id
         where rx.PractitionerId == practitionerId && rx.Status == PrescriptionStatus.Draft
         orderby rx.PrescribedAt descending
         select new WorklistPrescription(rx.Id, rx.PrescriptionNumber, rx.PatientId, p.Uhid, p.FullName, rx.PrescribedAt, rx.Items.Count))
        .Take(ListLimit)
        .ToListAsync(ct);

    private Task<List<WorklistResult>> ResultsAsync(Guid practitionerId, CancellationToken ct) =>
        (from r in db.Set<ResultReview>().AsNoTracking()
         join p in db.Set<Patient>() on r.PatientId equals p.Id
         where r.PractitionerId == practitionerId && r.ReviewedAt == null
         orderby r.HasCriticalFindings descending, r.ReleasedAt
         select new WorklistResult(r.Id, r.ReportId, r.Kind, r.Title, r.HasCriticalFindings, r.ReleasedAt, r.PatientId, p.Uhid, p.FullName))
        .Take(100)
        .ToListAsync(ct);

    private Task<List<WorklistDeficiency>> DeficienciesAsync(Guid practitionerId, CancellationToken ct) =>
        (from d in db.Set<ChartDeficiency>().AsNoTracking()
         join m in db.Set<MedicalRecord>() on d.MedicalRecordId equals m.Id
         join p in db.Set<Patient>() on m.PatientId equals p.Id
         where d.AssignedPractitionerId == practitionerId && d.ResolvedAt == null
         orderby d.DueDate
         select new WorklistDeficiency(d.Id, m.Id, d.Type, d.Description, d.DueDate, m.PatientId, p.Uhid, p.FullName))
        .Take(ListLimit)
        .ToListAsync(ct);
}

internal sealed record AppointmentRow
{
    public required Appointment Appointment { get; init; }
    public required string Uhid { get; init; }
    public required string FullName { get; init; }
    public Gender Gender { get; init; }
    public DateOnly? DateOfBirth { get; init; }
    public string? PractitionerName { get; init; }
    public string? TokenLabel { get; init; }
    public Guid? EncounterId { get; init; }
    public EncounterStatus? EncounterStatus { get; init; }
    public bool HasAllergies { get; init; }
    public bool HasCriticalAlerts { get; init; }
}
