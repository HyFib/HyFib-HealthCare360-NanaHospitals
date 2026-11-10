using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Search;
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
/// What a provider puts forward for a search inside one patient's record: the resources the search is then run on, how
/// many records it left out itself because the consents admitting the reader do not cover them (they count as withheld,
/// as those the caller filters out do), or a refusal when the search would reach more records than one answer holds.
/// </summary>
internal sealed record SearchCandidates(IReadOnlyList<R4.Resource> Resources, int Withheld = 0, FhirError? Refusal = null)
{
    public static SearchCandidates Refused(FhirError refusal) => new([], 0, refusal);
}

/// <summary>
/// Loads one FHIR resource type from the modules that own the data. Queries run through <see cref="HospitalDbContext"/>
/// with the platform's facility and soft-delete filters, and only records that are complete for sharing are returned
/// (released results and reports, signed prescriptions and discharge summaries). Patients have their own service.
/// </summary>
internal interface IFhirResourceProvider
{
    string ResourceType { get; }

    Task<ResourceLookup> FindAsync(string id, CancellationToken ct);

    /// <summary>
    /// The shareable resources of this type in one patient's record that the search could match; the search is run on
    /// them in memory afterwards, and the consents are applied to each. A provider may leave out records the search
    /// cannot match, never one it could; it may leave out records the consents do not cover, counting them as withheld;
    /// and it may refuse a search that would reach more records than one answer holds.
    /// </summary>
    Task<SearchCandidates> ListForPatientAsync(Guid patientId, SearchRequest request, PatientAccess access, CancellationToken ct);
}
