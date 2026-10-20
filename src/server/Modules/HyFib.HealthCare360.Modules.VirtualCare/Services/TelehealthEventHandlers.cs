using System.Globalization;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Hubs;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.VirtualCare.Services;

/// <summary>
/// TEL-01: a telehealth booking gets its video session and join link in the same transaction as the booking, so the
/// patient never receives a link for a booking that failed, and a booking never lacks a session.
/// </summary>
internal sealed class CreateTelehealthSessionWhenBooked(HospitalDbContext db, TelehealthLinkSender links, ILogger<CreateTelehealthSessionWhenBooked> logger)
    : IDomainEventHandler<AppointmentBooked>
{
    public async Task HandleAsync(AppointmentBooked domainEvent, CancellationToken cancellationToken)
    {
        if (!domainEvent.IsTelehealth)
        {
            return;
        }

        // The appointment is normally being inserted in this very unit of work, so look in the change tracker first.
        var appointment = db.ChangeTracker.Entries<Appointment>().Select(e => e.Entity).FirstOrDefault(a => a.Id == domainEvent.AppointmentId)
                          ?? await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                              .FirstOrDefaultAsync(a => a.Id == domainEvent.AppointmentId, cancellationToken);
        if (appointment is null)
        {
            logger.LogWarning("Telehealth appointment {AppointmentId} was not found; no video session created", domainEvent.AppointmentId);
            return;
        }

        var alreadyTracked = db.ChangeTracker.Entries<TelehealthSession>()
            .Any(e => e.Entity.AppointmentId == appointment.Id && e.Entity.Status != TelehealthStatus.Cancelled);
        if (alreadyTracked || await db.Set<TelehealthSession>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .AnyAsync(s => s.AppointmentId == appointment.Id && s.Status != TelehealthStatus.Cancelled, cancellationToken))
        {
            return;
        }

        var session = TelehealthSession.Schedule(
            appointment.FacilityId, appointment.PatientId, appointment.PractitionerId, appointment.DepartmentId, appointment.StartsAt, appointment.EndsAt,
            appointment.Id, appointment.Number);
        var token = session.IssueJoinToken().Value;
        db.Add(session);
        await links.SendAsync(session, await links.JoinUrlAsync(session, token, cancellationToken), cancellationToken);
    }
}

/// <summary>A cancelled (or rescheduled) appointment cancels its video session and invalidates the link.</summary>
internal sealed class CancelTelehealthSessionWhenAppointmentCancelled(HospitalDbContext db, TelehealthBroadcaster broadcaster, TimeProvider time)
    : IDomainEventHandler<AppointmentCancelled>
{
    public async Task HandleAsync(AppointmentCancelled domainEvent, CancellationToken cancellationToken)
    {
        var sessions = await db.Set<TelehealthSession>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(s => s.AppointmentId == domainEvent.AppointmentId
                        && (s.Status == TelehealthStatus.Scheduled || s.Status == TelehealthStatus.WaitingRoom))
            .ToListAsync(cancellationToken);

        foreach (var session in sessions)
        {
            if (session.Cancel(domainEvent.Reason ?? "Appointment cancelled", time.GetUtcNow()).IsSuccess)
            {
                var waiting = await db.Set<TelehealthParticipant>().AsNoTracking()
                    .Where(p => p.SessionId == session.Id && p.LeftAt == null && p.AdmittedAt == null)
                    .Select(p => p.ConnectionId)
                    .ToListAsync(cancellationToken);
                await broadcaster.SessionClosedAsync(session, waiting, cancellationToken);
            }
        }
    }
}

/// <summary>Links the telehealth visit to its session, whoever created it (the doctor's Start, or the front desk).</summary>
internal sealed class LinkVisitToTelehealthSession(HospitalDbContext db) : IDomainEventHandler<VisitCreated>
{
    public async Task HandleAsync(VisitCreated domainEvent, CancellationToken cancellationToken)
    {
        if (domainEvent.AppointmentId is not { } appointmentId)
        {
            return;
        }

        var session = await db.Set<TelehealthSession>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .FirstOrDefaultAsync(s => s.AppointmentId == appointmentId && s.Status != TelehealthStatus.Cancelled && s.Status != TelehealthStatus.Missed, cancellationToken);
        session?.AttachVisit(domainEvent.VisitId);
    }
}

/// <summary>
/// TEL-04: when the e-prescription of a video consultation is shared with the patient, they are told it is on the portal
/// (the pharmacy flow itself belongs to Pharmacy). Clinical sends <see cref="Template"/> on WhatsApp for every
/// out-patient prescription, this one included, so this handler adds only the SMS — at the same moment: when the doctor
/// signs it, or, where the hospital requires the doctor's DSC (ERX-04), once signed with it (or once the hospital no
/// longer requires it), and once only (<see cref="PrescriptionSharedWithPatient"/>).
/// </summary>
internal sealed class SharePrescriptionAfterTeleconsultation(HospitalDbContext db, INotificationService notifications)
    : IDomainEventHandler<PrescriptionSharedWithPatient>
{
    public const string Template = "prescription.shared";

    /// <summary>No WhatsApp: Clinical's message goes there, and queuing it here as well sent the patient two.</summary>
    internal static readonly IReadOnlyList<NotificationChannel> Channels = [NotificationChannel.Sms];

    public async Task HandleAsync(PrescriptionSharedWithPatient domainEvent, CancellationToken cancellationToken)
    {
        if (domainEvent.VisitId is not { } visitId || domainEvent.IsInpatient)
        {
            return;
        }

        var isTeleconsultation = await db.Set<TelehealthSession>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .AnyAsync(s => s.VisitId == visitId && (s.Status == TelehealthStatus.InProgress || s.Status == TelehealthStatus.Completed), cancellationToken);
        if (!isTeleconsultation)
        {
            return;
        }

        var patient = await db.Set<Patient>().AsNoTracking()
            .Where(p => p.Id == domainEvent.PatientId)
            .Select(p => new { p.Id, p.FullName, p.Phone, p.Email })
            .FirstOrDefaultAsync(cancellationToken);
        if (patient?.Phone is null)
        {
            return;
        }

        var doctor = await db.Set<Practitioner>().AsNoTracking().Where(p => p.Id == domainEvent.PractitionerId).Select(p => p.DisplayName).FirstOrDefaultAsync(cancellationToken);
        notifications.Queue(
            Template,
            new NotificationRecipient(patient.Phone, patient.Email, patient.Id, patient.FullName),
            new Dictionary<string, string>
            {
                ["name"] = patient.FullName,
                ["doctor"] = doctor ?? "your doctor",
                ["date"] = HospitalCalendar.India.ToLocal(domainEvent.SignedAt).ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
            },
            Channels,
            TimeSpan.FromDays(2),
            domainEvent.FacilityId);
    }
}
