using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Physiotherapy;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// Re-points this module's records when duplicate patient records are merged (Patients raises
/// <see cref="PatientsMerged"/> inside the merge transaction), across all facilities, so the surviving UHID shows
/// the complete surgical, dialysis, physiotherapy and transfusion history.
/// </summary>
internal sealed class PatientMergeHandler(HospitalDbContext db) : IDomainEventHandler<PatientsMerged>
{
    private static readonly DialysisSessionStatus[] ActiveSessionStatuses =
        [DialysisSessionStatus.Scheduled, DialysisSessionStatus.InProgress, DialysisSessionStatus.Completed];

    public async Task HandleAsync(PatientsMerged domainEvent, CancellationToken cancellationToken)
    {
        var source = domainEvent.SourcePatientId;
        var target = domainEvent.TargetPatientId;
        var ct = cancellationToken;

        // One dialysis profile per patient per unit: if both records were enrolled in the same unit keep the
        // surviving record's profile and deactivate (not move) the duplicate's.
        await db.Set<DialysisPatientProfile>().IgnoreQueryFilters()
            .Where(p => p.PatientId == source
                        && db.Set<DialysisPatientProfile>().IgnoreQueryFilters().Any(t => t.PatientId == target && t.FacilityId == p.FacilityId))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false), ct);
        await db.Set<DialysisPatientProfile>().IgnoreQueryFilters()
            .Where(p => p.PatientId == source
                        && !db.Set<DialysisPatientProfile>().IgnoreQueryFilters().Any(t => t.PatientId == target && t.FacilityId == p.FacilityId))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.PatientId, target), ct);

        // Two bookings of the same shift would violate the one-session-per-shift rule; cancel the duplicate's.
        var activeStatuses = ActiveSessionStatuses;
        await db.Set<DialysisSession>().IgnoreQueryFilters()
            .Where(s => s.PatientId == source && s.Status == DialysisSessionStatus.Scheduled
                        && db.Set<DialysisSession>().IgnoreQueryFilters().Any(t => t.PatientId == target && t.ScheduledDate == s.ScheduledDate
                                                                                   && t.Shift == s.Shift && activeStatuses.Contains(t.Status)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, DialysisSessionStatus.Cancelled)
                .SetProperty(x => x.CancelReason, "Duplicate booking removed when patient records were merged"), ct);

        await RepointAsync<AdmissionRecord>(x => x.PatientId, source, target, ct);
        await RepointAsync<SurgeryBooking>(x => x.PatientId, source, target, ct);
        await RepointAsync<AnaesthesiaRecord>(x => x.PatientId, source, target, ct);
        await RepointAsync<OperationNote>(x => x.PatientId, source, target, ct);
        await RepointAsync<SurgeryMaterial>(x => x.PatientId, source, target, ct);
        await RepointAsync<Dialyzer>(x => x.PatientId, source, target, ct);
        await RepointAsync<DialysisSession>(x => x.PatientId, source, target, ct);
        await RepointAsync<PhysioReferral>(x => x.PatientId, source, target, ct);
        await RepointAsync<PhysioAssessment>(x => x.PatientId, source, target, ct);
        await RepointAsync<TreatmentPlan>(x => x.PatientId, source, target, ct);
        await RepointAsync<PhysioSession>(x => x.PatientId, source, target, ct);
        await RepointAsync<BloodRequest>(x => x.PatientId, source, target, ct);
        await RepointAsync<CrossMatch>(x => x.PatientId, source, target, ct);
        await RepointAsync<BloodIssue>(x => x.PatientId, source, target, ct);
        await RepointAsync<TransfusionReaction>(x => x.PatientId, source, target, ct);
        await db.Set<Donation>().IgnoreQueryFilters()
            .Where(d => d.ReplacementForPatientId == source)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ReplacementForPatientId, target), ct);
    }

    private async Task RepointAsync<T>(Expression<Func<T, Guid>> patientId, Guid source, Guid target, CancellationToken ct)
        where T : class
    {
        // Compare against a captured variable so the id is sent as a parameter, not inlined into the SQL text.
        Expression<Func<Guid>> sourceValue = () => source;
        var predicate = Expression.Lambda<Func<T, bool>>(Expression.Equal(patientId.Body, sourceValue.Body), patientId.Parameters);
        await db.Set<T>().IgnoreQueryFilters().Where(predicate).ExecuteUpdateAsync(s => s.SetProperty(patientId, target), ct);
    }
}
