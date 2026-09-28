using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Search;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping;

/// <summary>
/// The server's CapabilityStatement (<c>GET /fhir/r4/metadata</c>), generated from <see cref="SearchDefinitions"/> so the
/// published search parameters are exactly the ones the parser accepts.
/// </summary>
internal static class CapabilityStatementBuilder
{
    public const string SoftwareName = "HyFib HealthCare360";

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
                          + "record-sharing consent are returned, and every access is audited.",
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
                            + $"{SearchDefinitions.DefaultCount}, at most {SearchDefinitions.MaxCount}) and _offset; every type except Patient "
                            + "is searched for one patient at a time. Unknown parameters and modifiers are rejected with an OperationOutcome.",
            Security = new R4.CapabilityStatement.SecurityComponent
            {
                Cors = false,
                Description = "Bearer access tokens issued by HC360 (POST /api/v1/auth/login). The caller needs the fhir.read permission. "
                              + "Records are returned only for patients with an active record-sharing (DataSharing) consent; restricted (VIP) "
                              + "records also need patients.restricted.read or an emergency (break-the-glass) grant. Every read and search is "
                              + "recorded in the tamper-evident audit trail.",
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
