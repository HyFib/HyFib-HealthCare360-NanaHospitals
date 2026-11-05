using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Search;
using Microsoft.Extensions.Primitives;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>A FHIR response: the HTTP status and the resource (or OperationOutcome) to return.</summary>
internal sealed record FhirResponse(int StatusCode, R4.Resource Body, bool Pretty = false)
{
    public static FhirResponse Ok(R4.Resource body, bool pretty = false) => new(StatusCodes.Status200OK, body, pretty);

    public static FhirResponse From(FhirError error) => new(error.StatusCode, error.Outcome);
}

/// <summary>
/// The read and search interactions. Every request passes the same gate: the record's patient must exist, not be
/// deleted or merged, hold a record-sharing consent that admits this reader today for the purpose they stated, and be
/// openable under the restricted-record rules. Then only the records those consents cover (their kinds and dates) are
/// returned. A read that fails is refused with an OperationOutcome (403, 404 or 410); a search simply leaves the patient,
/// or the records not covered, out. Every successful read and search is audited as PHI access with the stated purpose;
/// refusals on consent or restriction are security events.
/// </summary>
internal sealed class FhirApi(
    IEnumerable<IFhirResourceProvider> providers,
    IEnumerable<IFhirDirectoryProvider> directory,
    PatientRecords patients,
    PatientSharingService sharing,
    FhirAuditTrail audit,
    TimeProvider time)
{
    private readonly Dictionary<string, IFhirResourceProvider> _providers = providers.ToDictionary(p => p.ResourceType, StringComparer.Ordinal);

    private readonly Dictionary<string, IFhirDirectoryProvider> _directory = directory.ToDictionary(p => p.ResourceType, StringComparer.Ordinal);

    public async Task<FhirResponse> ReadAsync(string type, string id, SharingReader reader, CancellationToken ct)
    {
        if (!FhirResourceTypes.IsServed(type))
        {
            return FhirResponse.From(FhirOutcomes.UnknownType(type));
        }

        if (FhirResourceTypes.IsDirectory(type))
        {
            return await ReadDirectoryAsync(type, id, reader, ct);
        }

        return type == FhirResourceTypes.Patient ? await ReadPatientAsync(id, reader, ct) : await ReadClinicalAsync(type, id, reader, ct);
    }

    public async Task<FhirResponse> SearchAsync(
        string type, IEnumerable<KeyValuePair<string, StringValues>> parameters, string baseUrl, SharingReader reader, CancellationToken ct)
    {
        if (SearchDefinitions.Find(type) is not { } definition)
        {
            return FhirResponse.From(FhirOutcomes.UnknownType(type));
        }

        var parsed = SearchRequestParser.Parse(definition, parameters);
        if (parsed.Request is not { } request)
        {
            return FhirResponse.From(parsed.Error!);
        }

        if (FhirResourceTypes.IsDirectory(definition.Type))
        {
            return await SearchDirectoryAsync(request, baseUrl, reader, ct);
        }

        return definition.IsPatientScoped
            ? await SearchPatientRecordAsync(request, baseUrl, reader, ct)
            : await SearchPatientsAsync(request, baseUrl, reader, ct);
    }

    /// <summary>
    /// An entry of the hospital's directory. It is in no patient's record, so no consent is asked; the read is audited as an
    /// integration event, not PHI access, whatever it finds, so a lookup of a deleted or unknown id is on the trail too. A
    /// deleted one is Gone, as records still refer to it.
    /// </summary>
    private async Task<FhirResponse> ReadDirectoryAsync(string type, string id, SharingReader reader, CancellationToken ct)
    {
        var lookup = await _directory[type].FindAsync(id, ct);
        var found = lookup.Resource is null ? 0 : 1;
        await audit.DirectoryAsync("read", type, id, found, found, [], lookup.IsDeleted ? "gone" : found == 0 ? "not-found" : null, reader, ct);
        if (lookup.IsDeleted)
        {
            return FhirResponse.From(FhirOutcomes.Error(StatusCodes.Status410Gone, R4.OperationOutcome.IssueType.Deleted, $"{type}/{id} was deleted.", FhirOutcomes.Deleted));
        }

        if (lookup.Resource is null)
        {
            return FhirResponse.From(FhirOutcomes.ResourceNotFound(type, FhirOutcomes.Printable(id)));
        }

        return FhirResponse.Ok(lookup.Resource);
    }

    /// <summary>A search of the hospital's directory, run in memory over its live entries (a few hundred at most).</summary>
    private async Task<FhirResponse> SearchDirectoryAsync(SearchRequest request, string baseUrl, SharingReader reader, CancellationToken ct)
    {
        var type = request.Definition.Type;
        var page = ResourceSearch.Execute(await _directory[type].ListAsync(ct), request);
        await audit.DirectoryAsync("search", type, null, page.Resources.Count, page.Total, request.ParameterNames, null, reader, ct);
        return FhirResponse.Ok(SearchBundles.Build(baseUrl, request, page, time.GetUtcNow()), request.Pretty);
    }

    private async Task<FhirResponse> ReadPatientAsync(string id, SharingReader reader, CancellationToken ct)
    {
        if (!FhirIds.TryParse(id, out var patientId))
        {
            return FhirResponse.From(FhirOutcomes.ResourceNotFound(FhirResourceTypes.Patient, FhirOutcomes.Printable(id)));
        }

        var access = await sharing.EvaluateAsync(patientId, reader, ct);
        if (!access.IsAllowed)
        {
            return await RefuseAsync(access, FhirResourceTypes.Patient, id, patientId, reader, ct);
        }

        if (await patients.FindAsync(patientId, ct) is not { } patient)
        {
            return FhirResponse.From(FhirOutcomes.ResourceNotFound(FhirResourceTypes.Patient, id));
        }

        // The patient's own details say whose records they are, so they go with any consent that admits the reader.
        await audit.ReadAsync(FhirResourceTypes.Patient, id, patientId, reader, ct);
        return FhirResponse.Ok(patient);
    }

    private async Task<FhirResponse> ReadClinicalAsync(string type, string id, SharingReader reader, CancellationToken ct)
    {
        var lookup = await _providers[type].FindAsync(id, ct);
        if (lookup.PatientId is not { } patientId)
        {
            return FhirResponse.From(FhirOutcomes.ResourceNotFound(type, FhirOutcomes.Printable(id)));
        }

        // The patient gate comes first, so nothing (not even a deletion) is revealed about a record that may not be shared.
        var access = await sharing.EvaluateAsync(patientId, reader, ct);
        if (!access.IsAllowed)
        {
            return await RefuseAsync(access, type, id, patientId, reader, ct);
        }

        if (lookup.IsDeleted || lookup.Resource is null)
        {
            return lookup.IsDeleted
                ? FhirResponse.From(FhirOutcomes.Error(StatusCodes.Status410Gone, R4.OperationOutcome.IssueType.Deleted, $"{type}/{id} was deleted.", FhirOutcomes.Deleted))
                : FhirResponse.From(FhirOutcomes.ResourceNotFound(type, id));
        }

        // The consents that admit the reader may cover only some kinds of records, or records of some dates.
        if (!access.Covers(SharedRecords.KindOf(lookup.Resource), SharedRecords.DateOf(lookup.Resource)))
        {
            await audit.DeniedAsync("read", type, id, patientId, OutsideConsent, reader, ct);
            return FhirResponse.From(FhirOutcomes.OutsideConsent(type, id));
        }

        await audit.ReadAsync(type, id, patientId, reader, ct);
        return FhirResponse.Ok(lookup.Resource);
    }

    private async Task<FhirResponse> RefuseAsync(PatientAccess access, string type, string id, Guid patientId, SharingReader reader, CancellationToken ct)
    {
        if (access.Decision is SharingDecision.NoConsent or SharingDecision.Restricted)
        {
            await audit.DeniedAsync("read", type, id, patientId, ReasonOf(access.Decision), reader, ct);
        }

        var survivor = access.Decision == SharingDecision.Merged ? await sharing.ShareableSurvivorAsync(access.MergedIntoPatientId, reader, ct) : null;
        return FhirResponse.From(FhirOutcomes.Denied(access.Decision, type, FhirOutcomes.Printable(id), survivor));
    }

    /// <summary>
    /// A search inside one patient's record: only the records the consents admitting the reader cover. A patient whose
    /// record may not be shared yields an empty bundle; records left out are said to be withheld, never counted. A search
    /// that would reach more records than one answer holds is refused, asking for a narrower one.
    /// </summary>
    private async Task<FhirResponse> SearchPatientRecordAsync(SearchRequest request, string baseUrl, SharingReader reader, CancellationToken ct)
    {
        var type = request.Definition.Type;
        var access = FhirIds.TryParse(request.PatientId, out var patientId)
            ? await sharing.EvaluateAsync(patientId, reader, ct)
            : new PatientAccess(SharingDecision.NotFound);
        if (!access.IsAllowed)
        {
            if (access.Decision is SharingDecision.NoConsent or SharingDecision.Restricted)
            {
                await audit.DeniedAsync("search", type, null, patientId, ReasonOf(access.Decision), reader, ct);
            }

            var empty = SearchBundles.Build(baseUrl, request, new SearchPage([], 0), time.GetUtcNow(), FhirOutcomes.Withheld());
            return FhirResponse.Ok(empty, request.Pretty);
        }

        var candidates = await _providers[type].ListForPatientAsync(patientId, request, access, ct);
        if (candidates.Refusal is { } refusal)
        {
            return FhirResponse.From(refusal);
        }

        var shared = candidates.Resources.Where(resource => access.Covers(SharedRecords.KindOf(resource), SharedRecords.DateOf(resource))).ToList();
        var withheld = candidates.Resources.Count - shared.Count + candidates.Withheld;
        var page = ResourceSearch.Execute(shared, request);
        await audit.SearchAsync(type, patientId, page.Resources.Count, page.Total, withheld, request.ParameterNames, reader, ct);
        return FhirResponse.Ok(SearchBundles.Build(baseUrl, request, page, time.GetUtcNow(), withheld > 0 ? FhirOutcomes.Withheld() : null), request.Pretty);
    }

    /// <summary>A Patient search: consent, restriction and lifecycle filters run in SQL, so excluded patients never load.</summary>
    private async Task<FhirResponse> SearchPatientsAsync(SearchRequest request, string baseUrl, SharingReader reader, CancellationToken ct)
    {
        var (page, total) = await patients.SearchAsync(request, reader, ct);
        var ids = page.Select(p => FhirIds.TryParse(p.Id, out var id) ? id : (Guid?)null).OfType<Guid>().ToList();
        await audit.PatientSearchAsync(ids, total, request.ParameterNames, reader, ct);
        return FhirResponse.Ok(SearchBundles.Build(baseUrl, request, new SearchPage(page, total), time.GetUtcNow()), request.Pretty);
    }

    /// <summary>The audit reason of a read refused because the consents admitting the reader do not cover the record.</summary>
    public const string OutsideConsent = "outside-consent";

    private static string ReasonOf(SharingDecision decision) =>
        decision == SharingDecision.NoConsent ? FhirOutcomes.ConsentRequired : FhirOutcomes.RestrictedRecord;
}
