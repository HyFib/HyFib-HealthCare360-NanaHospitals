using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Features.Incidents;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Quality.Services;

/// <summary>
/// The incident register as other modules raise into it (<see cref="IIncidentRegister"/>): a never event the department
/// found itself, such as a retained swab, named to the member of staff who raised it, sentinel whatever its harm, so its
/// root-cause analysis is due and the quality team is alerted at once. Added in the caller's unit of work and transaction.
/// </summary>
internal sealed class IncidentRegister(HospitalDbContext db, INumberSeries numbers, ICurrentUser currentUser, IncidentService incidents, TimeProvider time)
    : IIncidentRegister
{
    public async Task<Result<RaisedIncident>> RaiseAsync(PatientSafetyIncident incident, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incident);
        if (currentUser.UserId is not { } reporter)
        {
            return Error.Forbidden("incident.staff_only", "Only staff raise incidents.");
        }

        var row = new Incident
        {
            FacilityId = incident.FacilityId,
            OccurredAt = incident.OccurredAt,
            ReportedAt = time.GetUtcNow(),
            Location = Fit(incident.Location, 150),
            PatientId = incident.PatientId,
            InvolvedStaffUserIds = [],
            Description = Fit(incident.Description, 4000),
            InvolvesDevice = incident.InvolvesDevice,
            ImmediateAction = incident.ImmediateAction is { Length: > 0 } action ? Fit(action, 2000) : null,
            ReporterUserId = reporter,
            ReporterName = currentUser.DisplayName ?? currentUser.UserName,
            Status = IncidentStatus.Reported,
        };
        row.Classify(IncidentType.Clinical, Harm(incident.Harm), Kind(incident.Kind), flaggedSentinel: true);
        row.IncidentNumber = await numbers.NextAsync(QualityNumberSeries.Incident, incident.FacilityId, cancellationToken);
        row.MarkReported();
        db.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        await incidents.EscalateAsync(new IncidentReceipt(row.Id, row.IncidentNumber, row.IsSentinel, row.Severity, row.Type), row.FacilityId, row.Location,
            cancellationToken);
        return new RaisedIncident(row.Id, row.IncidentNumber);
    }

    private static IncidentSeverity Harm(IncidentHarm harm) => harm switch
    {
        IncidentHarm.NearMiss => IncidentSeverity.NearMiss,
        IncidentHarm.NoHarm => IncidentSeverity.NoHarm,
        IncidentHarm.Mild => IncidentSeverity.Mild,
        IncidentHarm.Moderate => IncidentSeverity.Moderate,
        IncidentHarm.Severe => IncidentSeverity.Severe,
        _ => IncidentSeverity.Death,
    };

    private static SentinelEventKind Kind(NeverEvent kind) =>
        kind == NeverEvent.RetainedForeignObject ? SentinelEventKind.RetainedForeignObject : SentinelEventKind.Other;

    private static string Fit(string text, int length) => text.Trim().Length <= length ? text.Trim() : text.Trim()[..(length - 1)] + "…";
}
