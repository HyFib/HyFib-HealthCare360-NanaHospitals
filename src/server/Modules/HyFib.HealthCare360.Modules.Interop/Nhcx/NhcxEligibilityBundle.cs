using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Nhcx;

/// <summary>What the hospital asks the insurer about a policy before treatment (the CoverageEligibilityRequest's <c>purpose</c>).</summary>
internal enum NhcxEligibilityPurpose
{
    /// <summary>Is the policy in force for this patient?</summary>
    Validation = 0,

    /// <summary>What is covered, and how much of it is left, for these services?</summary>
    Benefits,

    /// <summary>Do these services need the insurer's approval in advance (a pre-authorisation)?</summary>
    AuthRequirements,

    /// <summary>Which policies does the insurer hold for this patient?</summary>
    Discovery,
}

/// <summary>A service the hospital asks about, with the diagnosis it is for.</summary>
internal sealed record NhcxEligibilityItem(string ServiceCode, string ServiceName, IReadOnlyList<NhcxDiagnosis> Diagnoses)
{
    /// <summary>The code system of <see cref="ServiceCode"/>: HC360's service master, or a scheme's package list (PM-JAY HBP).</summary>
    public string ServiceSystem { get; init; } = NhcxClaimBundle.ServiceSystem;
}

/// <summary>An eligibility check of the patient's policy, from Billing, with the masters it names.</summary>
internal sealed record NhcxEligibilityRequest
{
    public required NhcxEligibilityPurpose Purpose { get; init; }

    /// <summary>The check in Billing; with <see cref="Created"/> it makes the bundle's ids.</summary>
    public required Guid SourceId { get; init; }

    /// <summary>The hospital's number for the check, the request's identifier the insurer quotes back.</summary>
    public required string Number { get; init; }

    public required DateTimeOffset Created { get; init; }

    public required PatientRow Patient { get; init; }
    public required FacilityRow Facility { get; init; }
    public required NhcxPayerRow Payer { get; init; }
    public required NhcxPolicyRow Policy { get; init; }

    /// <summary>Who checks: the profile names them as a Practitioner, so the staff member is sent as one.</summary>
    public required PractitionerRow Enterer { get; init; }

    /// <summary>The services asked about (benefits and authorisation requirements); none for a validation or discovery.</summary>
    public IReadOnlyList<NhcxEligibilityItem> Items { get; init; } = [];
}

/// <summary>
/// The NHCX eligibility check: the NRCeS <c>CoverageEligibilityRequestBundle</c> (a <c>collection</c> Bundle whose
/// CoverageEligibilityRequest, profile <c>CoverageEligibilityRequest</c>, is followed by the Patient, the staff member who
/// checks as Practitioner, the hospital and insurer Organizations, the hospital as a Location and the Coverage it refers
/// to). The patient, insurer and policy are sent as on a claim (<see cref="NhcxClaimBundle"/>), so the insurer matches
/// them the same way; the services asked about carry HC360's service code (or a scheme's package code) and the
/// ICD-10 diagnosis they are for. The insurer answers with a CoverageEligibilityResponse (<see cref="NhcxResponses"/>).
/// </summary>
internal static class NhcxEligibilityBundle
{
    public const string BundleProfile = AbdmProfiles.Base + "CoverageEligibilityRequestBundle";
    public const string RequestProfile = AbdmProfiles.Base + "CoverageEligibilityRequest";

    public static Result<R4.Bundle> Build(NhcxEligibilityRequest request)
    {
        var problem = Check(request);
        if (problem is not null)
        {
            return problem;
        }

        string Id(string key) => AbdmDocumentBuilder.Uuid($"nhcx/{key}");
        var patientId = Id($"Patient/{FhirIds.Of(request.Patient.Id)}");
        var hospitalId = Id($"Organization/{FhirIds.Of(request.Facility.Id)}");
        var locationId = Id($"Location/{FhirIds.Of(request.Facility.Id)}");
        var insurerId = Id($"Organization/payer/{FhirIds.Of(request.Payer.Id)}");
        var coverageId = Id($"Coverage/{FhirIds.Of(request.Policy.Id)}");
        var entererId = Id($"Practitioner/{FhirIds.Of(request.Enterer.Id)}");
        var requestId = Id($"CoverageEligibilityRequest/{FhirIds.Of(request.SourceId)}");

        var patient = NhcxClaimBundle.Patient(request.Patient, request.Facility, hospitalId);
        patient.Id = patientId;
        var patientName = patient.Name.FirstOrDefault()?.Text;
        var hospital = AbdmParticipants.Organization(request.Facility);
        hospital.Id = hospitalId;
        var insurer = NhcxClaimBundle.Insurer(request.Payer);
        insurer.Id = insurerId;
        var coverage = NhcxClaimBundle.Coverage(request.Payer, request.Policy, patientId, patientName, insurerId);
        coverage.Id = coverageId;
        var enterer = AbdmParticipants.Practitioner(request.Enterer);
        enterer.Id = entererId;
        var location = new R4.Location
        {
            Id = locationId,
            Status = R4.Location.LocationStatus.Active,
            Name = request.Facility.Name,
            ManagingOrganization = new R4.ResourceReference(AbdmDocumentBuilder.Urn(hospitalId), request.Facility.Name),
        };

        var eligibility = new R4.CoverageEligibilityRequest
        {
            Id = requestId,
            Meta = new R4.Meta { LastUpdated = FhirTime.ToMilliseconds(request.Created), Profile = [RequestProfile] },
            Identifier = [new R4.Identifier($"{AbdmSystems.Facility}:{request.Facility.Code}:eligibility", request.Number.Trim())],
            Status = R4.FinancialResourceStatusCodes.Active,
            Priority = FhirElements.Concept("http://terminology.hl7.org/CodeSystem/processpriority", "normal", "Normal"),
            Purpose = [Purpose(request.Purpose)],
            Patient = new R4.ResourceReference(AbdmDocumentBuilder.Urn(patientId), patientName),
            Created = FhirTime.DateTime(request.Created),
            Enterer = new R4.ResourceReference(AbdmDocumentBuilder.Urn(entererId), request.Enterer.DisplayName),
            Provider = new R4.ResourceReference(AbdmDocumentBuilder.Urn(hospitalId), request.Facility.Name),
            Insurer = new R4.ResourceReference(AbdmDocumentBuilder.Urn(insurerId), request.Payer.Name),
            Facility = new R4.ResourceReference(AbdmDocumentBuilder.Urn(locationId), request.Facility.Name),
            Insurance = [new R4.CoverageEligibilityRequest.InsuranceComponent
            {
                Focal = true,
                Coverage = new R4.ResourceReference(AbdmDocumentBuilder.Urn(coverageId), request.Payer.Name),
            }],
        };
        foreach (var item in request.Items)
        {
            var asked = new R4.CoverageEligibilityRequest.DetailsComponent
            {
                ProductOrService = FhirElements.Concept(item.ServiceSystem, item.ServiceCode.Trim(), item.ServiceName.Trim()),
            };
            foreach (var diagnosis in item.Diagnoses.DistinctBy(d => d.Icd10Code.Trim().ToUpperInvariant()))
            {
                asked.Diagnosis.Add(new R4.CoverageEligibilityRequest.DiagnosisComponent
                {
                    Diagnosis = FhirElements.Concept(NhcxClaimBundle.Icd10, diagnosis.Icd10Code.Trim().ToUpperInvariant(), FhirElements.Clean(diagnosis.Title)),
                });
            }

            eligibility.Item.Add(asked);
        }

        eligibility.Text = AbdmNarrative.Generated([
            $"Eligibility check {request.Number.Trim()} ({PurposeName(request.Purpose)}) of policy {request.Policy.PolicyNumber.Trim()} with {request.Payer.Name}"
            + (request.Items.Count > 0 ? $": {request.Items.Count} service(s)." : "."),
        ]);

        var created = FhirTime.ToMilliseconds(request.Created);
        var bundleId = Id($"Bundle/eligibility/{FhirIds.Of(request.SourceId)}/{created.ToUnixTimeMilliseconds()}");
        var bundle = new R4.Bundle
        {
            Id = bundleId,
            Meta = new R4.Meta
            {
                LastUpdated = created,
                Profile = [BundleProfile],
                Security = [FhirElements.Coding(AbdmSystems.Confidentiality, "V", "very restricted")],
            },
            Identifier = new R4.Identifier(AbdmSystems.Uri, AbdmDocumentBuilder.Urn(bundleId)),
            Type = R4.Bundle.BundleType.Collection,
            Timestamp = created,
        };
        R4.Resource[] resources = [eligibility, patient, enterer, hospital, insurer, location, coverage];
        bundle.Entry.AddRange(resources.Select(r => new R4.Bundle.EntryComponent { FullUrl = AbdmDocumentBuilder.Urn(r.Id!), Resource = r }));
        return bundle;
    }

    /// <summary>The purpose's code in FHIR's <c>eligibilityrequest-purpose</c>.</summary>
    public static R4.CoverageEligibilityRequest.EligibilityRequestPurpose Purpose(NhcxEligibilityPurpose purpose) => purpose switch
    {
        NhcxEligibilityPurpose.Benefits => R4.CoverageEligibilityRequest.EligibilityRequestPurpose.Benefits,
        NhcxEligibilityPurpose.AuthRequirements => R4.CoverageEligibilityRequest.EligibilityRequestPurpose.AuthRequirements,
        NhcxEligibilityPurpose.Discovery => R4.CoverageEligibilityRequest.EligibilityRequestPurpose.Discovery,
        _ => R4.CoverageEligibilityRequest.EligibilityRequestPurpose.Validation,
    };

    private static string PurposeName(NhcxEligibilityPurpose purpose) => purpose switch
    {
        NhcxEligibilityPurpose.Benefits => "benefits",
        NhcxEligibilityPurpose.AuthRequirements => "whether approval is needed",
        NhcxEligibilityPurpose.Discovery => "policies held",
        _ => "policy in force",
    };

    /// <summary>What would make the insurer reject the check unread, or answer a different question.</summary>
    private static Error? Check(NhcxEligibilityRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Number))
        {
            return Error.Rule("nhcx.eligibility_number", "The check has no number for the insurer to quote.");
        }

        if (request.Purpose is NhcxEligibilityPurpose.Benefits or NhcxEligibilityPurpose.AuthRequirements && request.Items.Count == 0)
        {
            return Error.Rule("nhcx.eligibility_items", "Name the services to ask about: an insurer states benefits and approval rules per service.");
        }

        if (request.Items.FirstOrDefault(i => string.IsNullOrWhiteSpace(i.ServiceCode) || string.IsNullOrWhiteSpace(i.ServiceName) || string.IsNullOrWhiteSpace(i.ServiceSystem)) is { } item)
        {
            return Error.Rule("nhcx.eligibility_items", $"The service '{item.ServiceName}' needs a code and a name.");
        }

        return request.Items.SelectMany(i => i.Diagnoses).FirstOrDefault(d => !NhcxClaimBundle.Icd10Code().IsMatch(d.Icd10Code.Trim())) is { } wrong
            ? Error.Rule("nhcx.eligibility_diagnosis", $"'{wrong.Icd10Code}' is not an ICD-10 code (a letter, two digits, then an optional dot and up to four characters).")
            : null;
    }
}
