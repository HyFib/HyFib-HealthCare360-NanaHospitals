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
/// the complete surgical, dialysis, physiotherapy and transfusion history. The records move in set-based statements,
/// which the data audit does not see, so one audit entry in the same transaction lists the ids moved from each table, the
/// dialysis profiles deactivated and the sessions cancelled (<see cref="RecordsMovedAction"/>): what an unmerge would
/// have to put back.
/// </summary>
internal sealed class PatientMergeHandler(HospitalDbContext db, IAuditLogger audit) : IDomainEventHandler<PatientsMerged>
{
    public const string RecordsMovedAction = "specialtycare.records_moved";

    public const string DuplicateBookingReason = "Duplicate booking removed when patient records were merged";

    private static readonly DialysisSessionStatus[] ActiveSessionStatuses =
        [DialysisSessionStatus.Scheduled, DialysisSessionStatus.InProgress, DialysisSessionStatus.Completed];

    public async Task HandleAsync(PatientsMerged domainEvent, CancellationToken cancellationToken)
    {
        var source = domainEvent.SourcePatientId;
        var target = domainEvent.TargetPatientId;
        var ct = cancellationToken;
        var moved = new SortedDictionary<string, List<Guid>>(StringComparer.Ordinal);

        // One dialysis profile per patient per unit: if both records were enrolled in the same unit keep the
        // surviving record's profile and deactivate (not move) the duplicate's.
        var deactivated = await IdsAsync(db.Set<DialysisPatientProfile>().IgnoreQueryFilters()
            .Where(p => p.PatientId == source && p.IsActive
                        && db.Set<DialysisPatientProfile>().IgnoreQueryFilters().Any(t => t.PatientId == target && t.FacilityId == p.FacilityId)), ct);
        await db.Set<DialysisPatientProfile>().IgnoreQueryFilters()
            .Where(p => deactivated.Contains(p.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false), ct);
        moved[nameof(DialysisPatientProfile)] = await MoveAsync(db.Set<DialysisPatientProfile>().IgnoreQueryFilters()
            .Where(p => p.PatientId == source
                        && !db.Set<DialysisPatientProfile>().IgnoreQueryFilters().Any(t => t.PatientId == target && t.FacilityId == p.FacilityId)),
            p => p.PatientId, target, ct);

        // Two bookings of the same shift would violate the one-session-per-shift rule; cancel the duplicate's.
        var activeStatuses = ActiveSessionStatuses;
        var cancelled = await IdsAsync(db.Set<DialysisSession>().IgnoreQueryFilters()
            .Where(s => s.PatientId == source && s.Status == DialysisSessionStatus.Scheduled
                        && db.Set<DialysisSession>().IgnoreQueryFilters().Any(t => t.PatientId == target && t.ScheduledDate == s.ScheduledDate
                                                                                   && t.Shift == s.Shift && activeStatuses.Contains(t.Status))), ct);
        await db.Set<DialysisSession>().IgnoreQueryFilters()
            .Where(s => cancelled.Contains(s.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, DialysisSessionStatus.Cancelled)
                .SetProperty(x => x.CancelReason, DuplicateBookingReason), ct);

        await RepointAsync<AdmissionRecord>(x => x.PatientId, source, target, moved, ct);
        await RepointAsync<SurgeryBooking>(x => x.PatientId, source, target, moved, ct);
        await RepointAsync<AnaesthesiaRecord>(x => x.PatientId, source, target, moved, ct);
        await RepointAsync<OperationNote>(x => x.PatientId, source, target, moved, ct);
        await RepointAsync<SurgeryMaterial>(x => x.PatientId, source, target, moved, ct);
        await RepointAsync<Dialyzer>(x => x.PatientId, source, target, moved, ct);
        await RepointAsync<DialysisSession>(x => x.PatientId, source, target, moved, ct);
        await RepointAsync<PhysioReferral>(x => x.PatientId, source, target, moved, ct);
        await RepointAsync<PhysioAssessment>(x => x.PatientId, source, target, moved, ct);
        await RepointAsync<TreatmentPlan>(x => x.PatientId, source, target, moved, ct);
        await RepointAsync<PhysioSession>(x => x.PatientId, source, target, moved, ct);
        await RepointAsync<BloodRequest>(x => x.PatientId, source, target, moved, ct);
        await RepointAsync<CrossMatch>(x => x.PatientId, source, target, moved, ct);
        await RepointAsync<BloodIssue>(x => x.PatientId, source, target, moved, ct);
        await RepointAsync<TransfusionReaction>(x => x.PatientId, source, target, moved, ct);
        var replacementDonations = await MoveAsync(db.Set<Donation>().IgnoreQueryFilters().Where(d => d.ReplacementForPatientId == source),
            d => d.ReplacementForPatientId, (Guid?)target, ct);

        foreach (var nothing in moved.Where(m => m.Value.Count == 0).Select(m => m.Key).ToList())
        {
            moved.Remove(nothing);
        }

        if (moved.Count == 0 && deactivated.Count == 0 && cancelled.Count == 0 && replacementDonations.Count == 0)
        {
            return;
        }

        audit.Add(new AuditEntry(AuditCategories.Data, RecordsMovedAction,
            $"Merge moved {moved.Values.Sum(ids => ids.Count) + replacementDonations.Count} operation theatre, dialysis, physiotherapy and blood bank " +
            $"record(s) to the surviving record and cancelled {cancelled.Count} duplicate dialysis booking(s)")
        {
            PatientId = target,
            EntityType = nameof(Patient),
            EntityId = source.ToString(),
            Details = new
            {
                sourcePatientId = source,
                targetPatientId = target,
                moved,
                replacementDonations,
                dialysisProfilesDeactivated = deactivated,
                dialysisSessionsCancelled = cancelled,
            },
        });
    }

    private async Task RepointAsync<T>(
        Expression<Func<T, Guid>> patientId, Guid source, Guid target, SortedDictionary<string, List<Guid>> moved, CancellationToken ct)
        where T : Entity
    {
        // Compare against a captured variable so the id is sent as a parameter, not inlined into the SQL text.
        Expression<Func<Guid>> sourceValue = () => source;
        var predicate = Expression.Lambda<Func<T, bool>>(Expression.Equal(patientId.Body, sourceValue.Body), patientId.Parameters);
        moved[typeof(T).Name] = await MoveAsync(db.Set<T>().IgnoreQueryFilters().Where(predicate), patientId, target, ct);
    }

    /// <summary>
    /// Points <paramref name="rows"/> at <paramref name="target"/>, and returns the ids of those it moved: read first in the
    /// merge's transaction, and only those are moved, so the audit entry names exactly the rows that moved.
    /// </summary>
    private static async Task<List<Guid>> MoveAsync<T, TLink>(IQueryable<T> rows, Expression<Func<T, TLink>> link, TLink target, CancellationToken ct)
        where T : Entity
    {
        var ids = await IdsAsync(rows, ct);
        if (ids.Count > 0)
        {
            await rows.Where(x => ids.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(link, target), ct);
        }

        return ids;
    }

    private static Task<List<Guid>> IdsAsync<T>(IQueryable<T> rows, CancellationToken ct)
        where T : Entity =>
        rows.OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
}
