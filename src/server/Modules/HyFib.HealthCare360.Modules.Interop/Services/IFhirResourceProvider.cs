using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>The outcome of looking up one resource by id.</summary>
/// <param name="Resource">The mapped resource (null when missing or deleted).</param>
/// <param name="PatientId">The patient the resource belongs to (known for deleted rows too).</param>
/// <param name="IsDeleted">The source row is soft-deleted (410 Gone once the patient checks pass).</param>
internal sealed record ResourceLookup(R4.Resource? Resource, Guid? PatientId, bool IsDeleted)
{
    public static readonly ResourceLookup Missing = new(null, null, false);

    public static ResourceLookup Found(R4.Resource resource, Guid patientId) => new(resource, patientId, false);

    public static ResourceLookup Deleted(Guid patientId) => new(null, patientId, true);

    /// <summary>Missing or deleted for a row that cannot be returned; null when the row is live and should be loaded.</summary>
    public static ResourceLookup? Unavailable(RowState? state) =>
        state is null ? Missing
        : state.IsDeleted ? Deleted(state.PatientId)
        : null;
}

/// <summary>The patient and the soft-delete flag of a source row, read with the soft-delete filter off (to answer 410).</summary>
internal sealed record RowState(Guid PatientId, bool IsDeleted);

/// <summary>
/// Loads one FHIR resource type from the modules that own the data. Queries run through <see cref="HospitalDbContext"/>
/// with the platform's facility and soft-delete filters, and only records that are complete for sharing are returned
/// (released results and reports, signed prescriptions and discharge summaries). Patients have their own service.
/// </summary>
internal interface IFhirResourceProvider
{
    string ResourceType { get; }

    Task<ResourceLookup> FindAsync(string id, CancellationToken ct);

    /// <summary>Every shareable resource of this type in one patient's record (searched in memory afterwards).</summary>
    Task<IReadOnlyList<R4.Resource>> ListForPatientAsync(Guid patientId, CancellationToken ct);
}
