using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// Marks a patient deceased when the death is certified or the stay ends with the patient expired, and takes the mark
/// back when the certificate was made on the wrong patient. Saved with the caller's unit of work and audited.
/// </summary>
public sealed class PatientDeaths(HospitalDbContext db, IAuditLogger audit)
{
    public async Task MarkDeceasedAsync(Guid patientId, DateTimeOffset at, string source, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().FirstOrDefaultAsync(p => p.Id == patientId, ct);
        if (patient is null || patient.IsDeceased)
        {
            return;
        }

        (patient.IsDeceased, patient.DeceasedAt) = (true, at);
        Audit(patient, "patient.deceased", $"Marked deceased by {source}");
    }

    /// <summary>Takes back the mark <see cref="MarkDeceasedAsync"/> made at <paramref name="at"/>; one made otherwise stays.</summary>
    public async Task ClearDeceasedAsync(Guid patientId, DateTimeOffset at, string source, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().FirstOrDefaultAsync(p => p.Id == patientId, ct);
        if (patient is null || !patient.IsDeceased || patient.DeceasedAt != at)
        {
            return;
        }

        (patient.IsDeceased, patient.DeceasedAt) = (false, null);
        Audit(patient, "patient.deceased_cleared", $"Deceased mark taken back: {source}");
    }

    private void Audit(Patient patient, string action, string summary) =>
        audit.Add(new AuditEntry(AuditCategories.Data, action, summary)
        {
            PatientId = patient.Id,
            EntityType = nameof(Patient),
            EntityId = patient.Id.ToString(),
        });
}

/// <summary>A stay that ended with the patient expired marks the patient deceased, so the desk stops booking for them.</summary>
internal sealed class ExpiredDischargeHandler(PatientDeaths deaths) : IDomainEventHandler<PatientDischarged>
{
    public Task HandleAsync(PatientDischarged domainEvent, CancellationToken cancellationToken) =>
        domainEvent.DischargeType == "Expired"
            ? deaths.MarkDeceasedAsync(domainEvent.PatientId, domainEvent.DischargedAt, "an expired discharge", cancellationToken)
            : Task.CompletedTask;
}
