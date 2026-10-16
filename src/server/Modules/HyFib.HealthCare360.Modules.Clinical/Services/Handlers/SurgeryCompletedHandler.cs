using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Clinical.Services.Handlers;

/// <summary>
/// An operation finished in the theatre (OT-03): record it as a procedure on the patient's record (EHR-01), so the
/// timeline and the procedure list show it without anyone copying it from the theatre. Runs in the theatre's transaction,
/// so a completion that fails to save leaves no procedure behind.
/// </summary>
internal sealed class SurgeryCompletedHandler(ProcedureService procedures) : IDomainEventHandler<SurgeryCompleted>
{
    public Task HandleAsync(SurgeryCompleted domainEvent, CancellationToken cancellationToken) =>
        procedures.RecordSurgeryAsync(domainEvent, cancellationToken);
}
