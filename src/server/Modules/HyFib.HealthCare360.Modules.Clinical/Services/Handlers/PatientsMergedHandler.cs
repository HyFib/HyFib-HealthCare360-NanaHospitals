using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Clinical.Services.Handlers;

/// <summary>
/// When front office merges a duplicate UHID, every clinical row of the duplicate moves to the surviving record in the
/// same transaction — otherwise allergies or current medicines recorded on the duplicate would be missing from the
/// prescribing safety checks. Soft-deleted and other-facility rows are moved too; each change is audited.
/// </summary>
internal sealed class PatientsMergedHandler(HospitalDbContext db) : IDomainEventHandler<PatientsMerged>
{
    public async Task HandleAsync(PatientsMerged domainEvent, CancellationToken cancellationToken)
    {
        var (source, target) = (domainEvent.SourcePatientId, domainEvent.TargetPatientId);
        await RepointAsync<Encounter>(source, target, cancellationToken);
        await RepointAsync<VitalSigns>(source, target, cancellationToken);
        await RepointAsync<Allergy>(source, target, cancellationToken);
        await MergeNoKnownAllergiesAsync(source, target, cancellationToken);
        await RepointAsync<PatientAlert>(source, target, cancellationToken);
        await RepointAsync<Diagnosis>(source, target, cancellationToken);
        await RepointAsync<ClinicalNote>(source, target, cancellationToken);
        await RepointAsync<Immunization>(source, target, cancellationToken);
        await RepointAsync<ProcedureRecord>(source, target, cancellationToken);
        await RepointAsync<ResultReview>(source, target, cancellationToken);
        await RepointAsync<ClinicalOrder>(source, target, cancellationToken);
        await RepointAsync<Prescription>(source, target, cancellationToken);
        await RepointAsync<ControlledDrugRegisterEntry>(source, target, cancellationToken);
        await RepointAsync<DischargeSummary>(source, target, cancellationToken);
        await RepointAsync<EncounterCoding>(source, target, cancellationToken);
        await RepointAsync<MedicalRecord>(source, target, cancellationToken);
        await RepointAsync<RecordRequest>(source, target, cancellationToken);
        await RepointAsync<PackageBooking>(source, target, cancellationToken);
    }

    /// <summary>
    /// "No known allergies" moves with the chart, but the merged record keeps at most one live statement (the
    /// survivor's first) and none at all when either record has an active allergy: a statement must never sit next to
    /// an allergy. Statements that no longer apply are soft-deleted; history rows are re-pointed like everything else.
    /// </summary>
    private async Task MergeNoKnownAllergiesAsync(Guid source, Guid target, CancellationToken ct)
    {
        var statements = await db.Set<NoKnownAllergiesStatement>().IgnoreQueryFilters()
            .Where(s => s.PatientId == source || s.PatientId == target)
            .ToListAsync(ct);
        if (statements.Count == 0)
        {
            return;
        }

        var live = statements.Where(s => !s.IsDeleted).OrderByDescending(s => s.PatientId == target).ThenByDescending(s => s.RecordedAt).ToList();
        var hasActiveAllergy = live.Count > 0
                               && await db.Set<Allergy>().AnyAsync(a => (a.PatientId == source || a.PatientId == target) && a.Status == AllergyStatus.Active, ct);
        foreach (var statement in statements)
        {
            statement.PatientId = target;
        }

        db.RemoveRange(hasActiveAllergy ? live : live.Skip(1));
    }

    private async Task RepointAsync<T>(Guid source, Guid target, CancellationToken ct)
        where T : class
    {
        var rows = await db.Set<T>().IgnoreQueryFilters().Where(x => EF.Property<Guid>(x, "PatientId") == source).ToListAsync(ct);
        foreach (var row in rows)
        {
            db.Entry(row).Property("PatientId").CurrentValue = target;
        }
    }
}
