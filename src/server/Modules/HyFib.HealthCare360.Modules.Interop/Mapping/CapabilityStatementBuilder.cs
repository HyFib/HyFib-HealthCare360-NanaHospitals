using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Search;
using HyFib.HealthCare360.Modules.Patients.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping;

/// <summary>
/// The server's CapabilityStatement (<c>GET /fhir/r4/metadata</c>), generated from <see cref="SearchDefinitions"/> so the
/// published search parameters are exactly the ones the parser accepts.
/// </summary>
internal static class CapabilityStatementBuilder
{
    public const string SoftwareName = "HyFib HealthCare360";

    /// <summary>The server's origin (scheme, host and path base) of the FHIR base URL.</summary>
    private static string Origin(string baseUrl) =>
        baseUrl.EndsWith(Features.FhirEndpoints.BasePath, StringComparison.Ordinal) ? baseUrl[..^Features.FhirEndpoints.BasePath.Length] : baseUrl;

    public static R4.CapabilityStatement Build(string baseUrl, string softwareVersion, DateTimeOffset now)
    {
        var statement = new R4.CapabilityStatement
        {
            Id = "hc360-fhir-r4",
            Name = "HC360FhirR4ReadApi",
            Title = "HyFib HealthCare360 FHIR R4 read API",
            Status = R4.PublicationStatus.Active,
            Experimental = false,
            Date = FhirTime.Date(DateOnly.FromDateTime(now.ToOffset(FhirTime.Ist).DateTime)),
            Publisher = "HyFib",
            Description = "Read and search access to patient records for other providers (SRS EHR-05). Only patients with an active "
                          + "record-sharing consent are returned, and every access is audited. The hospital's practitioners, facilities and "
                          + "places of care (Practitioner, Organization, Location) are read without one: nothing in them is a patient's.",
            Kind = R4.CapabilityStatementKind.Instance,
            Software = new R4.CapabilityStatement.SoftwareComponent { Name = SoftwareName, Version = softwareVersion },
            Implementation = new R4.CapabilityStatement.ImplementationComponent
            {
                Description = "HyFib HealthCare360 FHIR R4 facade",
                Url = baseUrl,
            },
            FhirVersion = R4.FHIRVersion.N4_0_1,
            Format = ["application/fhir+json", "json"],
        };

        var rest = new R4.CapabilityStatement.RestComponent
        {
            Mode = R4.CapabilityStatement.RestfulCapabilityMode.Server,
            Documentation = "Read-only. Searches (GET or POST _search) page with _count (default "
                            + $"{SearchDefinitions.DefaultCount}, at most {SearchDefinitions.MaxCount}) and _offset; the types of a patient's record "
                            + "are searched for one patient at a time, and Patient and Practitioner searches need a selector. Results are newest first; "
                            + "_sort orders them by a type's date, string and code parameters instead (comma-separated in priority order, '-' for "
                            + "decreasing; not by a reference or an identifier). Unknown parameters and modifiers are rejected with an OperationOutcome.",
            Security = new R4.CapabilityStatement.SecurityComponent
            {
                Cors = false,
                Service =
                [
                    new R4.CodeableConcept("http://terminology.hl7.org/CodeSystem/restful-security-service", "SMART-on-FHIR", "SMART-on-FHIR"),
                ],
                Extension =
                [
                    new R4.Extension
                    {
                        Url = "http://fhir-registry.smarthealthit.org/StructureDefinition/oauth-uris",
                        Extension =
                        [
                            new R4.Extension("authorize", new R4.FhirUri(Origin(baseUrl) + "/oauth2/authorize")),
                            new R4.Extension("token", new R4.FhirUri(Origin(baseUrl) + "/oauth2/token")),
                            new R4.Extension("introspect", new R4.FhirUri(Origin(baseUrl) + "/oauth2/introspect")),
                            new R4.Extension("revoke", new R4.FhirUri(Origin(baseUrl) + "/oauth2/revoke")),
                        ],
                    },
                ],
                Description = "SMART on FHIR (App Launch 2.0 and Backend Services; discovery at .well-known/smart-configuration): apps get "
                              + "tokens by authorization code with PKCE (S256), authorized by a staff member who reads FHIR, with patient/ or user/ "
                              + "read scopes; backend services by client credentials with a JWT client assertion (RS384, ES384, RS256 or ES256), with "
                              + "system/ read scopes. Clients are registered by an administrator and approved by a second. Tokens are introspected and "
                              + "revoked at the endpoints named here. Integration accounts not yet moved to SMART still sign in with "
                              + "POST /api/v1/auth/login. Either way the caller's account needs the fhir.read permission. "
                              + "Records are returned only for patients with a record-sharing (DataSharing) consent that admits the caller today: "
                              + "a consent may name the accounts that may read, the purposes of use, the kinds of records, the records' dates "
                              + "and its last day. State the purpose of use in the X-Purpose-Of-Use header (HL7 v3 ActReason: "
                              + string.Join(", ", SharingPurposeCodes.All) + "); a consent that names purposes admits no request without one. "
                              + "Records outside every consent that admits the caller are left out of searches and refused on read "
                              + "(403 consent-required). Counselling notes go only under a consent that names them, labelled restricted "
                              + "(v3 confidentiality R). Restricted (VIP) records also need patients.restricted.read or an emergency "
                              + "(break-the-glass) grant. Practitioner, Organization and Location need no consent. Every read and search is recorded in the "
                              + "tamper-evident audit trail with the stated purpose.",
            },
        };

        foreach (var definition in SearchDefinitions.All)
        {
            var resource = new R4.CapabilityStatement.ResourceComponent
            {
                Type = definition.Type,
                Documentation = definition.Documentation,
                Versioning = R4.CapabilityStatement.ResourceVersionPolicy.NoVersion,
                ReadHistory = false,
                UpdateCreate = false,
                ConditionalCreate = false,
                ConditionalUpdate = false,
                ConditionalRead = R4.CapabilityStatement.ConditionalReadStatus.NotSupported,
                ConditionalDelete = R4.CapabilityStatement.ConditionalDeleteStatus.NotSupported,
            };
            resource.Interaction.Add(new R4.CapabilityStatement.ResourceInteractionComponent { Code = R4.CapabilityStatement.TypeRestfulInteraction.Read });
            resource.Interaction.Add(new R4.CapabilityStatement.ResourceInteractionComponent { Code = R4.CapabilityStatement.TypeRestfulInteraction.SearchType });
            foreach (var parameter in definition.Parameters)
            {
                resource.SearchParam.Add(new R4.CapabilityStatement.SearchParamComponent
                {
                    Name = parameter.Name,
                    Type = parameter.Type,
                    Documentation = parameter.Documentation,
                });
            }

            rest.Resource.Add(resource);
        }

        statement.Rest.Add(rest);
        return statement;
    }
}
