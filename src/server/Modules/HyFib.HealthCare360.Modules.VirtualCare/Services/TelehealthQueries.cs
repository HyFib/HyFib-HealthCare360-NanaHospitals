using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Features.Telehealth;

namespace HyFib.HealthCare360.Modules.VirtualCare.Services;

/// <summary>Read models for telehealth screens (joined with patient and doctor names).</summary>
public static class TelehealthQueries
{
    public static async Task<List<TelehealthSessionListItem>> ListAsync(HospitalDbContext db, IQueryable<TelehealthSession> sessions, DateOnly today, CancellationToken ct)
    {
        var rows = await (
                from s in sessions
                join p in db.Set<Patient>() on s.PatientId equals p.Id
                join pr in db.Set<Practitioner>() on s.PractitionerId equals pr.Id into practitioners
                from pr in practitioners.DefaultIfEmpty()
                orderby s.ScheduledStartAt
                select new
                {
                    Session = s,
                    p.Uhid,
                    p.FullName,
                    p.Gender,
                    p.DateOfBirth,
                    PractitionerName = pr != null ? pr.DisplayName : null,
                })
            .Take(500)
            .ToListAsync(ct);

        return rows.Select(r => new TelehealthSessionListItem(
            r.Session.Id, r.Session.AppointmentId, r.Session.AppointmentNumber, r.Session.PatientId, r.Uhid, r.FullName, r.Gender,
            r.DateOfBirth is { } dob ? Age.Format(dob, today) : null, r.Session.PractitionerId, r.PractitionerName ?? "Doctor", r.Session.Status,
            r.Session.ScheduledStartAt, r.Session.ScheduledEndAt, r.Session.PatientWaitingSince, r.Session.StartedAt, r.Session.EndedAt,
            r.Session.DurationSeconds, r.Session.VisitId, r.Session.LinkSentAt is not null)).ToList();
    }

    public static async Task<TelehealthSessionDetail?> DetailAsync(HospitalDbContext db, Guid id, DateOnly today, CancellationToken ct)
    {
        var row = await (
                from s in db.Set<TelehealthSession>().AsNoTracking()
                join p in db.Set<Patient>() on s.PatientId equals p.Id
                join pr in db.Set<Practitioner>() on s.PractitionerId equals pr.Id into practitioners
                from pr in practitioners.DefaultIfEmpty()
                where s.Id == id
                select new { Session = s, p.Uhid, p.FullName, p.Gender, p.DateOfBirth, p.Phone, PractitionerName = pr != null ? pr.DisplayName : null })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }

        var participants = await db.Set<TelehealthParticipant>().AsNoTracking()
            .Where(p => p.SessionId == id)
            .OrderBy(p => p.JoinedAt)
            .Select(p => new TelehealthParticipantDto(
                p.Id, p.Role, p.DisplayName, p.JoinedAt, p.AdmittedAt, p.LeftAt, p.AudioOnly, p.WorstRoundTripMs, p.MaxPacketLossPercent, p.LowestBitrateKbps))
            .ToListAsync(ct);
        var messageCount = await db.Set<TelehealthMessage>().CountAsync(m => m.SessionId == id, ct);

        var x = row.Session;
        return new TelehealthSessionDetail(
            x.Id, x.FacilityId, x.AppointmentId, x.AppointmentNumber, x.PatientId, row.Uhid, row.FullName, row.Gender,
            row.DateOfBirth is { } dob ? Age.Format(dob, today) : null, Phone.Mask(row.Phone), x.PractitionerId, row.PractitionerName ?? "Doctor",
            x.DepartmentId, x.VisitId, x.Status, x.ScheduledStartAt, x.ScheduledEndAt, x.PatientWaitingSince, x.StartedAt, x.EndedAt, x.DurationSeconds,
            x.LinkSentAt, x.LinkSendCount, x.JoinTokenExpiresAt, x.RecordingConsent, x.RecordingConsentAt, x.AudioOnlyUsed, x.QualityNotes, x.CancelReason,
            messageCount, participants, x.Version);
    }

    public static Task<List<TelehealthMessageDto>> MessagesAsync(HospitalDbContext db, Guid sessionId, CancellationToken ct) =>
        db.Set<TelehealthMessage>().AsNoTracking()
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.SentAt)
            .Take(1000)
            .Select(m => new TelehealthMessageDto(m.Id, m.SenderRole, m.SenderName, m.Text, m.SentAt))
            .ToListAsync(ct);
}
