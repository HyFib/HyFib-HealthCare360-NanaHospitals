using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Platform.Messaging;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Scheduling.Services;

/// <summary>
/// Keeps appointments in step with visits created by the Patients module (PMS-03). Front office may check a patient in
/// from the appointment screen (which calls <c>VisitService</c>) or directly from the visit screen by picking the
/// appointment; either way the visit raises <see cref="VisitCreated"/> and the appointment is checked in within the
/// same transaction. A visit that cannot be linked never blocks the visit itself — it is only logged.
/// </summary>
internal sealed class CheckInAppointmentWhenVisitCreated(HospitalDbContext db, TimeProvider time, ILogger<CheckInAppointmentWhenVisitCreated> logger)
    : IDomainEventHandler<VisitCreated>
{
    public async Task HandleAsync(VisitCreated domainEvent, CancellationToken cancellationToken)
    {
        if (domainEvent.AppointmentId is not { } appointmentId)
        {
            return;
        }

        var appointment = await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .FirstOrDefaultAsync(a => a.Id == appointmentId, cancellationToken);
        if (appointment is null)
        {
            logger.LogWarning("Visit {VisitId} references unknown appointment {AppointmentId}", domainEvent.VisitId, appointmentId);
            return;
        }

        if (appointment.PatientId != domainEvent.PatientId)
        {
            logger.LogWarning("Visit {VisitId} is for a different patient than appointment {AppointmentId}; not linked", domainEvent.VisitId, appointmentId);
            return;
        }

        var result = appointment.CheckIn(domainEvent.VisitId, time.GetUtcNow());
        if (result.IsFailure)
        {
            logger.LogWarning("Appointment {AppointmentId} could not be checked in for visit {VisitId}: {Code}", appointmentId, domainEvent.VisitId, result.Error!.Code);
        }
    }
}

/// <summary>
/// The doctor started seeing the patient (queue token started or encounter opened): the appointment moves to "in
/// consultation", so the desk and the patient portal show the live state for in-person visits as well as video ones.
/// </summary>
internal sealed class StartConsultationWhenVisitStarted(HospitalDbContext db, TimeProvider time) : IDomainEventHandler<VisitStarted>
{
    public async Task HandleAsync(VisitStarted domainEvent, CancellationToken cancellationToken)
    {
        if (domainEvent.AppointmentId is not { } appointmentId)
        {
            return;
        }

        var appointment = await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .FirstOrDefaultAsync(a => a.Id == appointmentId, cancellationToken);
        if (appointment?.VisitId == domainEvent.VisitId)
        {
            appointment.StartConsultation(time.GetUtcNow());
        }
    }
}

/// <summary>Completes the appointment when its visit is completed (consultation finished).</summary>
internal sealed class CompleteAppointmentWhenVisitCompleted(HospitalDbContext db, TimeProvider time) : IDomainEventHandler<VisitCompleted>
{
    public async Task HandleAsync(VisitCompleted domainEvent, CancellationToken cancellationToken)
    {
        var appointment = await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .FirstOrDefaultAsync(a => a.VisitId == domainEvent.VisitId, cancellationToken);
        appointment?.Complete(time.GetUtcNow());
    }
}

/// <summary>
/// Reverts the check-in when the desk cancels the visit (typically a check-in made by mistake), so the appointment
/// can be checked in again, cancelled or marked as a no-show.
/// </summary>
internal sealed class RevertCheckInWhenVisitCancelled(HospitalDbContext db) : IDomainEventHandler<VisitCancelled>
{
    public async Task HandleAsync(VisitCancelled domainEvent, CancellationToken cancellationToken)
    {
        var appointment = await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .FirstOrDefaultAsync(a => a.VisitId == domainEvent.VisitId, cancellationToken);
        appointment?.UndoCheckIn(domainEvent.VisitId);
    }
}
