using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.VirtualCare.Services;

/// <summary>
/// Re-points remote monitoring and telehealth records when duplicate patient records are merged (Patients raises
/// <see cref="PatientsMerged"/> inside the merge transaction), across all facilities. The surviving record then has the
/// whole monitoring history: the duplicate's enrolments with their readings and alerts, the devices assigned to it and
/// its video consultations. Otherwise the survivor's charts, alerts and shared record (FHIR) would miss everything
/// measured under the duplicate, and the duplicate's devices would go on filing readings under a merged record.
/// <para>
/// The rows move in set-based statements (readings run to thousands), which the data audit does not see, so one audit
/// entry in the same transaction names what moved (<see cref="RecordsMovedAction"/>): the ids of the enrolments, alerts,
/// devices and sessions, and the number of readings. A reading belongs to its enrolment, so the enrolments' ids name the
/// readings too, which is what an unmerge would have to put back. An enrolment moves as it is: if both records were
/// enrolled in the same programme the survivor has two open enrolments in it, for the care team to close one.
/// </para>
/// </summary>
internal sealed class PatientsMergedHandler(HospitalDbContext db, IAuditLogger audit) : IDomainEventHandler<PatientsMerged>
{
    public const string RecordsMovedAction = "virtualcare.records_moved";

    public async Task HandleAsync(PatientsMerged domainEvent, CancellationToken cancellationToken)
    {
        var source = domainEvent.SourcePatientId;
        var target = domainEvent.TargetPatientId;
        var ct = cancellationToken;

        var enrollments = await MoveAsync(db.Set<RpmEnrollment>().IgnoreQueryFilters().Where(e => e.PatientId == source), e => e.PatientId, target, ct);
        var readings = await db.Set<RpmReading>().IgnoreQueryFilters().Where(r => r.PatientId == source)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.PatientId, target), ct);
        var alerts = await MoveAsync(db.Set<RpmAlert>().IgnoreQueryFilters().Where(a => a.PatientId == source), a => a.PatientId, target, ct);
        var devices = await MoveAsync(db.Set<RpmDevice>().IgnoreQueryFilters().Where(d => d.PatientId == source), d => d.PatientId, (Guid?)target, ct);
        var sessions = await MoveAsync(db.Set<TelehealthSession>().IgnoreQueryFilters().Where(s => s.PatientId == source), s => s.PatientId, target, ct);
        if (enrollments.Count + readings + alerts.Count + devices.Count + sessions.Count == 0)
        {
            return;
        }

        audit.Add(new AuditEntry(AuditCategories.Data, RecordsMovedAction,
            $"Merge moved {enrollments.Count} monitoring enrolment(s) with {readings} reading(s), {alerts.Count} alert(s), " +
            $"{devices.Count} device(s) and {sessions.Count} telehealth session(s) to the surviving record")
        {
            PatientId = target,
            EntityType = nameof(Patient),
            EntityId = source.ToString(),
            Details = new
            {
                sourcePatientId = source,
                targetPatientId = target,
                enrollments,
                readings,
                alerts,
                devices,
                telehealthSessions = sessions,
            },
        });
    }

    /// <summary>
    /// Points <paramref name="rows"/> at <paramref name="target"/>, and returns the ids of those it moved: read first in the
    /// merge's transaction, and only those are moved, so the audit entry names exactly the rows that moved.
    /// </summary>
    private static async Task<List<Guid>> MoveAsync<T, TLink>(IQueryable<T> rows, Expression<Func<T, TLink>> link, TLink target, CancellationToken ct)
        where T : Entity
    {
        var ids = await rows.OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
        if (ids.Count > 0)
        {
            await rows.Where(x => ids.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(link, target), ct);
        }

        return ids;
    }
}
