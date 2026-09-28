using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Features.Telehealth;
using HyFib.HealthCare360.Modules.VirtualCare.Hubs;

namespace HyFib.HealthCare360.Modules.VirtualCare.Services;

/// <summary>
/// Telehealth session lifecycle for staff (TEL-01..05). Starting a consultation creates the telehealth visit through the
/// Patients module (so the doctor documents the EMR and e-prescribes against it, TEL-04) and moves the appointment to
/// "in consultation"; ending it records the end time and duration; every change is pushed to the call screens and to
/// the doctor's worklist.
/// </summary>
public sealed class TelehealthSessionService(
    HospitalDbContext db,
    VisitService visits,
    TelehealthLinkSender links,
    TelehealthBroadcaster broadcaster,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public const int DefaultDurationMinutes = 15;
    public const int MaxDaysAhead = 60;

    /// <summary>Creates a session for a telehealth appointment or an ad-hoc consultation and (optionally) sends the join link.</summary>
    public async Task<Result<TelehealthLinkIssued>> CreateAsync(CreateTelehealthSessionRequest request, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        TelehealthSession session;
        if (request.AppointmentId is { } appointmentId)
        {
            var appointment = await db.Set<Appointment>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == appointmentId, ct);
            if (appointment is null)
            {
                return Error.NotFound(nameof(Appointment), appointmentId);
            }

            if (!appointment.IsTelehealth)
            {
                return Error.Rule("telehealth.not_video_appointment", "This is an in-person appointment. Reschedule it as a video consultation first.");
            }

            if (!appointment.HoldsSeat)
            {
                return Error.Rule("telehealth.appointment_closed", "This appointment was cancelled or rescheduled.");
            }

            var existing = await db.Set<TelehealthSession>().Where(s => s.AppointmentId == appointmentId && s.Status != TelehealthStatus.Cancelled)
                .Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                return Error.Conflict("telehealth.exists", "A video consultation already exists for this appointment. Resend its link instead.", new { sessionId = existing });
            }

            session = TelehealthSession.Schedule(
                appointment.FacilityId, appointment.PatientId, appointment.PractitionerId, appointment.DepartmentId, appointment.StartsAt, appointment.EndsAt,
                appointment.Id, appointment.Number);
        }
        else
        {
            if (currentUser.FacilityId is not { } facilityId)
            {
                return Error.Rule("facility.required", "Select a facility first.");
            }

            var startsAt = request.ScheduledStartAt!.Value;
            if (startsAt < now.AddMinutes(-15) || startsAt > now.AddDays(MaxDaysAhead))
            {
                return Error.Field("scheduledStartAt", $"Choose a time between now and {MaxDaysAhead} days ahead.");
            }

            var patient = await db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PatientId, ct);
            if (patient is null || !patient.IsActive || patient.IsDeceased || patient.MergedIntoPatientId is not null)
            {
                return Error.Field("patientId", "Choose an active patient.");
            }

            var practitioner = await db.Set<Practitioner>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PractitionerId && p.IsActive, ct);
            if (practitioner is null)
            {
                return Error.Field("practitionerId", "Choose an active doctor.");
            }

            session = TelehealthSession.Schedule(
                facilityId, patient.Id, practitioner.Id, practitioner.DepartmentId, startsAt,
                startsAt.AddMinutes(request.DurationMinutes ?? DefaultDurationMinutes));
        }

        var token = session.IssueJoinToken().Value;
        db.Add(session);
        var url = await links.JoinUrlAsync(session, token, ct);
        var sent = request.SendLink && await links.SendAsync(session, url, ct);
        await db.SaveChangesAsync(ct);
        await broadcaster.SessionChangedAsync(session, ct);
        return new TelehealthLinkIssued(session.Id, url, session.JoinTokenExpiresAt, sent);
    }

    /// <summary>
    /// Starts the consultation: creates the telehealth visit (once), marks the appointment "in consultation" and starts
    /// the clock. Safe to repeat (for example after the doctor's app reconnects).
    /// </summary>
    public async Task<Result<TelehealthSession>> StartAsync(Guid id, CancellationToken ct)
    {
        var session = await db.Set<TelehealthSession>().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null)
        {
            return Error.NotFound(nameof(TelehealthSession), id);
        }

        if (session.IsClosed)
        {
            return Error.Rule("telehealth.closed", "This consultation is closed.");
        }

        if (session.VisitId is null)
        {
            var visit = await visits.CreateAsync(new CreateVisitRequest(
                session.PatientId, VisitType.Telehealth, session.DepartmentId, session.PractitionerId, session.AppointmentId,
                PayerType.Self, null, ReferralSource.Self, null, "Video consultation", false, null, null, TokenPriority.Normal,
                IssueToken: false, NotifyPatient: false), ct);
            if (visit.IsFailure)
            {
                return visit.Error!;
            }

            session.AttachVisit(visit.Value.Id);
        }

        var now = time.GetUtcNow();
        var started = session.Start(now);
        if (started.IsFailure)
        {
            return started.Error!;
        }

        if (session.AppointmentId is { } appointmentId)
        {
            var appointment = await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(a => a.Id == appointmentId, ct);
            appointment?.StartConsultation(now);
        }

        await db.SaveChangesAsync(ct);
        await broadcaster.SessionChangedAsync(session, ct);
        return session;
    }

    public async Task<Result<TelehealthSession>> EndAsync(Guid id, string? qualityNotes, CancellationToken ct)
    {
        var session = await db.Set<TelehealthSession>().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null)
        {
            return Error.NotFound(nameof(TelehealthSession), id);
        }

        var ended = session.End(time.GetUtcNow(), qualityNotes);
        if (ended.IsFailure)
        {
            return ended.Error!;
        }

        var open = await db.Set<TelehealthParticipant>().Where(p => p.SessionId == id && p.LeftAt == null).ToListAsync(ct);
        foreach (var participant in open)
        {
            participant.Leave(session.EndedAt!.Value);
        }

        await db.SaveChangesAsync(ct);
        await broadcaster.SessionClosedAsync(session, open.Where(p => p.AdmittedAt is null).Select(p => p.ConnectionId).ToList(), ct);
        return session;
    }

    public async Task<Result<TelehealthSession>> CancelAsync(Guid id, string reason, CancellationToken ct)
    {
        var session = await db.Set<TelehealthSession>().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null)
        {
            return Error.NotFound(nameof(TelehealthSession), id);
        }

        var cancelled = session.Cancel(reason.Trim(), time.GetUtcNow());
        if (cancelled.IsFailure)
        {
            return cancelled.Error!;
        }

        await db.SaveChangesAsync(ct);
        var waiting = await WaitingConnectionsAsync(id, ct);
        await broadcaster.SessionClosedAsync(session, waiting, ct);
        return session;
    }

    /// <summary>Issues a fresh link (the old one stops working) and sends it again.</summary>
    public async Task<Result<TelehealthLinkIssued>> ResendLinkAsync(Guid id, CancellationToken ct)
    {
        var session = await db.Set<TelehealthSession>().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null)
        {
            return Error.NotFound(nameof(TelehealthSession), id);
        }

        var token = session.IssueJoinToken();
        if (token.IsFailure)
        {
            return token.Error!;
        }

        var url = await links.JoinUrlAsync(session, token.Value, ct);
        var sent = await links.SendAsync(session, url, ct);
        await db.SaveChangesAsync(ct);
        return new TelehealthLinkIssued(session.Id, url, session.JoinTokenExpiresAt, sent);
    }

    public async Task<Result> RecordConsentAsync(Guid id, bool granted, CancellationToken ct)
    {
        var session = await db.Set<TelehealthSession>().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null)
        {
            return Error.NotFound(nameof(TelehealthSession), id);
        }

        var result = session.RecordConsent(granted, currentUser.UserId, time.GetUtcNow());
        if (result.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return result;
    }

    /// <summary>Chat from the doctor's REST client (the hub is the usual path); delivered to the call room.</summary>
    public async Task<Result<TelehealthMessageDto>> PostMessageAsync(Guid id, string text, CancellationToken ct)
    {
        var session = await db.Set<TelehealthSession>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null)
        {
            return Error.NotFound(nameof(TelehealthSession), id);
        }

        if (session.Status is TelehealthStatus.Cancelled or TelehealthStatus.Missed)
        {
            return Error.Rule("telehealth.closed", "This consultation did not take place.");
        }

        var created = TelehealthMessage.Create(id, TelehealthRole.Clinician, currentUser.UserId, currentUser.DisplayName ?? "Doctor", text, time.GetUtcNow());
        if (created.IsFailure)
        {
            return created.Error!;
        }

        db.Add(created.Value);
        await db.SaveChangesAsync(ct);
        var dto = TelehealthMessageDto.From(created.Value);
        await broadcaster.ChatAsync(id, dto, ct);
        return dto;
    }

    private Task<List<string>> WaitingConnectionsAsync(Guid id, CancellationToken ct) =>
        db.Set<TelehealthParticipant>().AsNoTracking()
            .Where(p => p.SessionId == id && p.LeftAt == null && p.AdmittedAt == null)
            .Select(p => p.ConnectionId)
            .ToListAsync(ct);
}
