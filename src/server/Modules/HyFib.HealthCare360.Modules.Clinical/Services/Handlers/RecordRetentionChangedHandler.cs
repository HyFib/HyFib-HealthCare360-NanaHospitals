using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Clinical.Services.Handlers;

/// <summary>
/// When the retention period for out-patient or in-patient charts changes (Privacy's retention schedule, SRS PRV-02),
/// every chart of that kind already completed moves its retention date by the difference, in the same transaction, so
/// the MRD review list follows the schedule in force. Medico-legal charts have no date and are never touched. The
/// set-based update is not audited row by row; the schedule change itself is.
/// </summary>
internal sealed class RecordRetentionChangedHandler(HospitalDbContext db) : IDomainEventHandler<RecordRetentionChanged>
{
    public async Task HandleAsync(RecordRetentionChanged domainEvent, CancellationToken cancellationToken)
    {
        var shift = domainEvent.Years - domainEvent.PreviousYears;
        if (shift == 0)
        {
            return;
        }

        var inpatient = domainEvent.Inpatient;
        await db.Set<MedicalRecord>().IgnoreQueryFilters()
            .Where(m => m.RetainUntil != null && !m.IsMlc && (m.AdmissionId != null) == inpatient)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.RetainUntil, m => m.RetainUntil!.Value.AddYears(shift)), cancellationToken);
    }
}
