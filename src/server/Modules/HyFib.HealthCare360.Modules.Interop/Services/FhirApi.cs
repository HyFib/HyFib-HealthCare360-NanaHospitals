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
/// deleted or merged, hold an active record-sharing consent and be openable under the restricted-record rules. A read
/// that fails the gate is refused with an OperationOutcome (403, 404 or 410); a search simply leaves the patient out.
/// Every successful read and search is audited as PHI access; refusals on consent or restriction are security events.
/// </summary>
internal sealed class FhirApi(
    IEnumerable<IFhirResourceProvider> providers,
    PatientRecords patients,
    PatientSharingService sharing,
    FhirAuditTrail audit,
    TimeProvider time)
{
    private readonly Dictionary<string, IFhirResourceProvider> _providers = providers.ToDictionary(p => p.ResourceType, StringComparer.Ordinal);

    public async Task<FhirResponse> ReadAsync(string type, string id, CancellationToken ct)
    {
        if (!FhirResourceTypes.IsServed(type))
        {
            return FhirResponse.From(FhirOutcomes.UnknownType(type));
        }

        return type == FhirResourceTypes.Patient ? await ReadPatientAsync(id, ct) : await ReadClinicalAsync(type, id, ct);
    }

    public async Task<FhirResponse> SearchAsync(string type, IEnumerable<KeyValuePair<string, StringValues>> parameters, string baseUrl, CancellationToken ct)
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

        return definition.IsPatientScoped ? await SearchPatientRecordAsync(request, baseUrl, ct) : await SearchPatientsAsync(request, baseUrl, ct);
    }

    private async Task<FhirResponse> ReadPatientAsync(string id, CancellationToken ct)
    {
        if (!FhirIds.TryParse(id, out var patientId))
        {
            return FhirResponse.From(FhirOutcomes.ResourceNotFound(FhirResourceTypes.Patient, FhirOutcomes.Printable(id)));
        }

        var access = await sharing.EvaluateAsync(patientId, ct);
        if (!access.IsAllowed)
        {
            return await RefuseAsync(access, FhirResourceTypes.Patient, id, patientId, ct);
        }

        if (await patients.FindAsync(patientId, ct) is not { } patient)
        {
            return FhirResponse.From(FhirOutcomes.ResourceNotFound(FhirResourceTypes.Patient, id));
        }

        await audit.ReadAsync(FhirResourceTypes.Patient, id, patientId, ct);
        return FhirResponse.Ok(patient);
    }

    private async Task<FhirResponse> ReadClinicalAsync(string type, string id, CancellationToken ct)
    {
        var lookup = await _providers[type].FindAsync(id, ct);
        if (lookup.PatientId is not { } patientId)
        {
            return FhirResponse.From(FhirOutcomes.ResourceNotFound(type, FhirOutcomes.Printable(id)));
        }

        // The patient gate comes first, so nothing (not even a deletion) is revealed about a record that may not be shared.
        var access = await sharing.EvaluateAsync(patientId, ct);
        if (!access.IsAllowed)
        {
            return await RefuseAsync(access, type, id, patientId, ct);
        }

        if (lookup.IsDeleted || lookup.Resource is null)
        {
            return lookup.IsDeleted
                ? FhirResponse.From(FhirOutcomes.Error(StatusCodes.Status410Gone, R4.OperationOutcome.IssueType.Deleted, $"{type}/{id} was deleted.", FhirOutcomes.Deleted))
                : FhirResponse.From(FhirOutcomes.ResourceNotFound(type, id));
        }

        await audit.ReadAsync(type, id, patientId, ct);
        return FhirResponse.Ok(lookup.Resource);
    }

    private async Task<FhirResponse> RefuseAsync(PatientAccess access, string type, string id, Guid patientId, CancellationToken ct)
    {
        if (access.Decision is SharingDecision.NoConsent or SharingDecision.Restricted)
        {
            await audit.DeniedAsync("read", type, id, patientId, ReasonOf(access.Decision), ct);
        }

        var survivor = access.Decision == SharingDecision.Merged ? await sharing.ShareableSurvivorAsync(access.MergedIntoPatientId, ct) : null;
        return FhirResponse.From(FhirOutcomes.Denied(access.Decision, type, FhirOutcomes.Printable(id), survivor));
    }

    /// <summary>A search inside one patient's record; a patient whose record may not be shared yields an empty bundle.</summary>
    private async Task<FhirResponse> SearchPatientRecordAsync(SearchRequest request, string baseUrl, CancellationToken ct)
    {
        var type = request.Definition.Type;
        var access = FhirIds.TryParse(request.PatientId, out var patientId)
            ? await sharing.EvaluateAsync(patientId, ct)
            : new PatientAccess(SharingDecision.NotFound);
        if (!access.IsAllowed)
        {
            if (access.Decision is SharingDecision.NoConsent or SharingDecision.Restricted)
            {
                await audit.DeniedAsync("search", type, null, patientId, ReasonOf(access.Decision), ct);
            }

            var empty = SearchBundles.Build(baseUrl, request, new SearchPage([], 0), time.GetUtcNow(), FhirOutcomes.Withheld());
            return FhirResponse.Ok(empty, request.Pretty);
        }

        var resources = await _providers[type].ListForPatientAsync(patientId, ct);
        var page = ResourceSearch.Execute(resources, request);
        await audit.SearchAsync(type, patientId, page.Resources.Count, page.Total, request.ParameterNames, ct);
        return FhirResponse.Ok(SearchBundles.Build(baseUrl, request, page, time.GetUtcNow()), request.Pretty);
    }

    /// <summary>A Patient search: consent, restriction and lifecycle filters run in SQL, so excluded patients never load.</summary>
    private async Task<FhirResponse> SearchPatientsAsync(SearchRequest request, string baseUrl, CancellationToken ct)
    {
        var (page, total) = await patients.SearchAsync(request, ct);
        var ids = page.Select(p => FhirIds.TryParse(p.Id, out var id) ? id : (Guid?)null).OfType<Guid>().ToList();
        await audit.PatientSearchAsync(ids, total, request.ParameterNames, ct);
        return FhirResponse.Ok(SearchBundles.Build(baseUrl, request, new SearchPage(page, total), time.GetUtcNow()), request.Pretty);
    }

    private static string ReasonOf(SharingDecision decision) =>
        decision == SharingDecision.NoConsent ? FhirOutcomes.ConsentRequired : FhirOutcomes.RestrictedRecord;
}
