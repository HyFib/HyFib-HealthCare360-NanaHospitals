using System.Globalization;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Nhcx;

/// <summary>What a claim bundle asks of the insurer: approval in advance of treatment, or payment of a final bill.</summary>
internal enum NhcxClaimUse
{
    Preauthorization = 0,
    Claim,
}

/// <summary>Whether the treatment is a stay or a visit, which decides the claim's type (the IG's <c>ndhm-claim-type</c>).</summary>
internal enum NhcxCareSetting
{
    Inpatient = 0,
    Outpatient,
    DayCare,
}

/// <summary>The payer in Billing's payer master (an insurer, a TPA's principal, a corporate or a government scheme).</summary>
internal sealed record NhcxPayerRow
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }

    /// <summary>A government scheme (PM-JAY, CGHS) or a corporate, rather than an insurance company.</summary>
    public bool IsInsurer { get; init; } = true;

    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>The patient's policy, with its number decrypted for the insurer (it is shown masked everywhere else).</summary>
internal sealed record NhcxPolicyRow
{
    public required Guid Id { get; init; }
    public required string PolicyNumber { get; init; }
    public string? MemberId { get; init; }
    public string? PolicyHolderName { get; init; }

    /// <summary>The patient's relation to the policy holder as recorded at the desk (self, spouse, son, mother...).</summary>
    public string? RelationToHolder { get; init; }

    public required DateOnly ValidFrom { get; init; }
    public required DateOnly ValidTo { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>A diagnosis as Billing keeps it: an ICD-10 code, with its title when the coder chose it from the index.</summary>
internal sealed record NhcxDiagnosis(string Icd10Code, string? Title = null);

/// <summary>One service on the claim: a bill line, or a line of a pre-authorisation's cost estimate.</summary>
internal sealed record NhcxClaimLine(string ServiceCode, string ServiceName, decimal Quantity, decimal UnitPrice)
{
    /// <summary>The code system of <see cref="ServiceCode"/>: HC360's service master, or PM-JAY's HBP procedures (<see cref="NhcxClaimBundle.HbpSystem"/>).</summary>
    public string ServiceSystem { get; init; } = NhcxClaimBundle.ServiceSystem;
}

/// <summary>A pre-authorisation request or a claim, from Billing, with the masters it names.</summary>
internal sealed record NhcxClaimRequest
{
    public required NhcxClaimUse Use { get; init; }

    /// <summary>The pre-authorisation or claim in Billing; with <see cref="Created"/> it makes the bundle's ids.</summary>
    public required Guid SourceId { get; init; }

    /// <summary>The hospital's number for it (pre-authorisation or claim number), the Claim's identifier.</summary>
    public required string Number { get; init; }

    public required DateTimeOffset Created { get; init; }
    public required NhcxCareSetting Setting { get; init; }

    /// <summary>The admission (or visit) to the expected or actual discharge.</summary>
    public required DateTimeOffset PeriodStart { get; init; }

    public DateTimeOffset? PeriodEnd { get; init; }

    public required PatientRow Patient { get; init; }
    public required FacilityRow Facility { get; init; }
    public required NhcxPayerRow Payer { get; init; }
    public required NhcxPolicyRow Policy { get; init; }

    /// <summary>The treating doctor, named on the claim's care team.</summary>
    public PractitionerRow? Doctor { get; init; }

    public required IReadOnlyList<NhcxDiagnosis> Diagnoses { get; init; }
    public required IReadOnlyList<NhcxClaimLine> Lines { get; init; }

    /// <summary>On a claim: the insurer's approval reference of the pre-authorisation it follows.</summary>
    public string? PreAuthorizationReference { get; init; }

    /// <summary>
    /// A claim to AB PM-JAY: each line names the scheme's HBP procedure and carries the programme (the IG's
    /// <c>ndhm-program-code</c> <c>AB-PMJAY</c>), so the scheme pays its package rate for it.
    /// </summary>
    public bool Pmjay { get; init; }
}

/// <summary>
/// The NHCX claim message: the NRCeS <c>ClaimBundle</c> (a <c>collection</c> Bundle whose Claim, profile <c>Claim</c>,
/// is followed by the Patient, the insurer and hospital Organizations, the Coverage and the treating Practitioner it refers
/// to, each with a <c>urn:uuid:</c> full URL). It is the payload the NHCX envelope (<c>NhcxEnvelope</c> in Integrations)
/// seals for the insurer.
/// <list type="bullet">
/// <item>A pre-authorisation's diagnoses are preliminary and a claim's final (the IG's <c>ndhm-diagnostic-type</c>); the
/// claim's type is inpatient, outpatient or day-care management (<c>ndhm-claim-type</c>, extensible, checked offline).</item>
/// <item>Each service carries HC360's service code, its name, quantity, unit price and net in rupees; the total is their
/// sum, so the insurer reads the same amount the bill does.</item>
/// <item>The patient is sent with what an insurer needs to match the policy (identifiers, name, gender, birth date,
/// phone, address); next of kin, marital status and language stay in the hospital.</item>
/// </list>
/// Ids are derived from HC360's ids as the ABDM documents' are (<see cref="AbdmDocumentBuilder.Uuid"/>), so a patient
/// keeps one id across claims, and the same request built twice is the same bundle.
/// </summary>
internal static partial class NhcxClaimBundle
{
    public const string ClaimBundleProfile = AbdmProfiles.Base + "ClaimBundle";
    public const string ClaimProfile = AbdmProfiles.Base + "Claim";
    public const string CoverageProfile = AbdmProfiles.Base + "Coverage";

    /// <summary>HC360's service master codes, the claim lines' <c>productOrService</c> (an example binding in the IG).</summary>
    public const string ServiceSystem = "urn:hc360:service-item";

    /// <summary>
    /// AB PM-JAY's Health Benefit Package procedure codes (<c>SG039A</c>). Neither NHA nor the IG publishes a URI for them,
    /// so HC360 names them under its own namespace until the NHCX sandbox says which one the scheme reads.
    /// </summary>
    public const string HbpSystem = "urn:hc360:pmjay-hbp";

    /// <summary>The IG's government programme codes, and AB PM-JAY's code and display there.</summary>
    public const string ProgramSystem = "https://nrces.in/ndhm/fhir/r4/CodeSystem/ndhm-program-code";
    public const string PmjayProgram = "AB-PMJAY";
    public const string PmjayProgramDisplay = "Ayushman Bharat Pradhan Mantri Jan Arogya Yojana (AB-PMJAY)";

    public const string PayerSystem = "urn:hc360:payer";
    public const string Icd10 = "http://hl7.org/fhir/sid/icd-10";

    private const string Snomed = "http://snomed.info/sct";

    public static Result<R4.Bundle> Build(NhcxClaimRequest request)
    {
        var problem = Check(request);
        if (problem is not null)
        {
            return problem;
        }

        string Id(string key) => AbdmDocumentBuilder.Uuid($"nhcx/{key}");
        var patientId = Id($"Patient/{FhirIds.Of(request.Patient.Id)}");
        var hospitalId = Id($"Organization/{FhirIds.Of(request.Facility.Id)}");
        var insurerId = Id($"Organization/payer/{FhirIds.Of(request.Payer.Id)}");
        var coverageId = Id($"Coverage/{FhirIds.Of(request.Policy.Id)}");
        var claimId = Id($"Claim/{request.Use}/{FhirIds.Of(request.SourceId)}");
        string? doctorId = request.Doctor is { } d ? Id($"Practitioner/{FhirIds.Of(d.Id)}") : null;

        var patient = Patient(request.Patient, request.Facility, hospitalId);
        patient.Id = patientId;
        var patientName = patient.Name.FirstOrDefault()?.Text;
        var hospital = AbdmParticipants.Organization(request.Facility);
        hospital.Id = hospitalId;
        var insurer = Insurer(request.Payer);
        insurer.Id = insurerId;
        var coverage = Coverage(request.Payer, request.Policy, patientId, patientName, insurerId);
        coverage.Id = coverageId;
        var claim = Claim(request, patientId, patientName, hospitalId, insurerId, coverageId, doctorId);
        claim.Id = claimId;

        var resources = new List<R4.Resource> { claim, patient, insurer, hospital, coverage };
        if (request.Doctor is { } doctor)
        {
            var practitioner = AbdmParticipants.Practitioner(doctor);
            practitioner.Id = doctorId;
            resources.Add(practitioner);
        }

        var created = FhirTime.ToMilliseconds(request.Created);
        var bundleId = Id($"Bundle/{request.Use}/{FhirIds.Of(request.SourceId)}/{created.ToUnixTimeMilliseconds()}");
        var bundle = new R4.Bundle
        {
            Id = bundleId,
            Meta = new R4.Meta
            {
                LastUpdated = created,
                Profile = [ClaimBundleProfile],
                Security = [FhirElements.Coding(AbdmSystems.Confidentiality, "V", "very restricted")],
            },
            Identifier = new R4.Identifier(AbdmSystems.Uri, AbdmDocumentBuilder.Urn(bundleId)),
            Type = R4.Bundle.BundleType.Collection,
            Timestamp = created,
        };
        bundle.Entry.AddRange(resources.Select(r => new R4.Bundle.EntryComponent { FullUrl = AbdmDocumentBuilder.Urn(r.Id!), Resource = r }));
        return bundle;
    }

    /// <summary>What would make the insurer reject the request unread, or misread it.</summary>
    private static Error? Check(NhcxClaimRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Number))
        {
            return Error.Rule("nhcx.claim_number", "The request has no number for the insurer to quote.");
        }

        if (request.Diagnoses.Count == 0)
        {
            return Error.Rule("nhcx.claim_diagnosis", "An insurer decides on the diagnosis: record at least one ICD-10 code first.");
        }

        if (request.Diagnoses.FirstOrDefault(x => !Icd10Code().IsMatch(x.Icd10Code.Trim())) is { } wrong)
        {
            return Error.Rule("nhcx.claim_diagnosis", $"'{wrong.Icd10Code}' is not an ICD-10 code (a letter, two digits, then an optional dot and up to four characters).");
        }

        if (request.Lines.Count == 0)
        {
            return Error.Rule("nhcx.claim_lines", "The request lists no services, so there is nothing for the insurer to approve or pay.");
        }

        if (request.Lines.FirstOrDefault(l => l.Quantity <= 0 || l.UnitPrice < 0 || string.IsNullOrWhiteSpace(l.ServiceCode) || string.IsNullOrWhiteSpace(l.ServiceName)) is { } line)
        {
            return Error.Rule("nhcx.claim_lines", $"The service '{line.ServiceName}' needs a code, a name, a quantity above zero and a price of zero or more.");
        }

        if (request.Pmjay && request.Lines.FirstOrDefault(l => l.ServiceSystem != HbpSystem || !HbpProcedureCode().IsMatch(l.ServiceCode.Trim())) is { } uncoded)
        {
            return Error.Rule("nhcx.claim_hbp", $"'{uncoded.ServiceName}' has no PM-JAY HBP procedure code: the scheme pays only the packages it lists. Map the package to its HBP procedure first.");
        }

        if (request.Lines.Sum(l => l.Quantity * l.UnitPrice) <= 0)
        {
            return Error.Rule("nhcx.claim_lines", "The request totals nothing: an insurer is not asked to pay ₹0.");
        }

        if (request.PeriodEnd is { } end && end < request.PeriodStart)
        {
            return Error.Rule("nhcx.claim_period", "The treatment ends before it starts.");
        }

        var start = FhirTime.IstDate(request.PeriodStart);
        return start < request.Policy.ValidFrom || start > request.Policy.ValidTo
            ? Error.Rule("nhcx.claim_policy", $"The policy covers {request.Policy.ValidFrom:dd-MM-yyyy} to {request.Policy.ValidTo:dd-MM-yyyy}; the treatment starts on {start:dd-MM-yyyy}.")
            : null;
    }

    /// <summary>The patient as an insurer needs them to match the policy, also on an eligibility check.</summary>
    internal static R4.Patient Patient(PatientRow row, FacilityRow facility, string hospitalId)
    {
        var refs = new ReferenceDirectory();
        var patient = AbdmProfiler.Patient(PatientMapper.Map(row, refs));
        patient.Contact.Clear();
        patient.MaritalStatus = null;
        patient.Communication.Clear();
        patient.ManagingOrganization = row.RegisteredFacilityId == facility.Id
            ? new R4.ResourceReference(AbdmDocumentBuilder.Urn(hospitalId), facility.Name)
            : null;
        return patient;
    }

    internal static R4.Organization Insurer(NhcxPayerRow payer) => new()
    {
        Meta = FhirElements.Meta(payer.CreatedAt, payer.UpdatedAt, AbdmProfiles.Organization),
        Name = payer.Name,
        Identifier =
        [
            new R4.Identifier(PayerSystem, payer.Code)
            {
                Use = R4.Identifier.IdentifierUse.Usual,
                Type = FhirElements.Concept(FhirSystems.IdentifierType, "XX", "Organization identifier"),
            },
        ],
        Type = [payer.IsInsurer
            ? FhirElements.Concept(AbdmSystems.OrganizationType, "ins", "Insurance Company")
            : FhirElements.Concept(AbdmSystems.OrganizationType, "pay", "Payer")],
    };

    internal static R4.Coverage Coverage(NhcxPayerRow payer, NhcxPolicyRow policy, string patientId, string? patientName, string insurerId)
    {
        var patient = new R4.ResourceReference(AbdmDocumentBuilder.Urn(patientId), patientName);
        var relation = Relationship(policy.RelationToHolder);
        var coverage = new R4.Coverage
        {
            Meta = FhirElements.Meta(policy.CreatedAt, policy.UpdatedAt, CoverageProfile),
            Identifier = [new R4.Identifier($"{PayerSystem}:{payer.Code}:policy", policy.PolicyNumber.Trim())],
            Status = R4.FinancialResourceStatusCodes.Active,
            Type = payer.IsInsurer
                ? FhirElements.Concept("http://terminology.hl7.org/CodeSystem/v3-ActCode", "HIP", "health insurance plan policy")
                : FhirElements.Concept("http://terminology.hl7.org/CodeSystem/v3-ActCode", "PUBLICPOL", "public healthcare"),
            SubscriberId = FhirElements.Clean(policy.MemberId),
            Beneficiary = patient,
            Relationship = relation is { } r ? FhirElements.Concept("http://terminology.hl7.org/CodeSystem/subscriber-relationship", r.Code, r.Display) : null,
            Period = new R4.Period { Start = FhirTime.Date(policy.ValidFrom), End = FhirTime.Date(policy.ValidTo) },
            Payor = [new R4.ResourceReference(AbdmDocumentBuilder.Urn(insurerId), payer.Name)],
        };

        // The patient holds the policy when the desk recorded "self" or no relation and no other holder.
        if (relation?.Code == "self" || (relation is null && FhirElements.Clean(policy.PolicyHolderName) is null))
        {
            coverage.Subscriber = new R4.ResourceReference(patient.Reference, patient.Display);
            coverage.PolicyHolder = new R4.ResourceReference(patient.Reference, patient.Display);
        }
        else if (FhirElements.Clean(policy.PolicyHolderName) is { } holder)
        {
            coverage.PolicyHolder = new R4.ResourceReference { Display = holder };
        }

        return coverage;
    }

    private static R4.Claim Claim(NhcxClaimRequest request, string patientId, string? patientName, string hospitalId, string insurerId, string coverageId, string? doctorId)
    {
        var final = request.Use == NhcxClaimUse.Claim;
        var claim = new R4.Claim
        {
            Meta = new R4.Meta { LastUpdated = FhirTime.ToMilliseconds(request.Created), Profile = [ClaimProfile] },
            Identifier = [new R4.Identifier($"{AbdmSystems.Facility}:{request.Facility.Code}:{(final ? "claim" : "pre-authorization")}", request.Number.Trim())],
            Status = R4.FinancialResourceStatusCodes.Active,
            Type = request.Setting switch
            {
                NhcxCareSetting.Outpatient => FhirElements.Concept(Snomed, "737492002", "Outpatient care management (procedure)"),
                NhcxCareSetting.DayCare => FhirElements.Concept(Snomed, "737850002", "Day care case management (procedure)"),
                _ => FhirElements.Concept(Snomed, "737481003", "Inpatient care management (procedure)"),
            },
            Use = final ? R4.ClaimUseCode.Claim : R4.ClaimUseCode.Preauthorization,
            Patient = new R4.ResourceReference(AbdmDocumentBuilder.Urn(patientId), patientName),
            BillablePeriod = new R4.Period
            {
                Start = FhirTime.DateTime(request.PeriodStart),
                End = request.PeriodEnd is { } end ? FhirTime.DateTime(end) : null,
            },
            Created = FhirTime.DateTime(request.Created),
            Insurer = new R4.ResourceReference(AbdmDocumentBuilder.Urn(insurerId), request.Payer.Name),
            Provider = new R4.ResourceReference(AbdmDocumentBuilder.Urn(hospitalId), request.Facility.Name),
            Priority = FhirElements.Concept("http://terminology.hl7.org/CodeSystem/processpriority", "normal", "Normal"),
        };

        if (doctorId is not null)
        {
            claim.CareTeam.Add(new R4.Claim.CareTeamComponent
            {
                Sequence = 1,
                Provider = new R4.ResourceReference(AbdmDocumentBuilder.Urn(doctorId), request.Doctor!.DisplayName),
                Responsible = true,
                Role = FhirElements.Concept(Snomed, "223366009", "Healthcare professional (occupation)"),
            });
        }

        R4.CodeableConcept Type() => final
            ? FhirElements.Concept(Snomed, "89100005", "Final diagnosis (discharge) (contextual qualifier) (qualifier value)")
            : FhirElements.Concept(Snomed, "148006", "Preliminary diagnosis (contextual qualifier) (qualifier value)");
        var sequence = 0;
        foreach (var diagnosis in request.Diagnoses.DistinctBy(x => x.Icd10Code.Trim().ToUpperInvariant()))
        {
            claim.Diagnosis.Add(new R4.Claim.DiagnosisComponent
            {
                Sequence = ++sequence,
                Diagnosis = FhirElements.Concept(Icd10, diagnosis.Icd10Code.Trim().ToUpperInvariant(), FhirElements.Clean(diagnosis.Title)),
                Type = [Type()],
            });
        }

        var insurance = new R4.Claim.InsuranceComponent
        {
            Sequence = 1,
            Focal = true,
            Coverage = new R4.ResourceReference(AbdmDocumentBuilder.Urn(coverageId), request.Payer.Name),
        };
        if (FhirElements.Clean(request.PreAuthorizationReference) is { } approval)
        {
            insurance.PreAuthRef = [approval];
        }

        claim.Insurance.Add(insurance);

        sequence = 0;
        var total = 0m;
        foreach (var line in request.Lines)
        {
            var net = decimal.Round(line.Quantity * line.UnitPrice, 2, MidpointRounding.AwayFromZero);
            total += net;
            var item = new R4.Claim.ItemComponent
            {
                Sequence = ++sequence,
                ProductOrService = FhirElements.Concept(line.ServiceSystem, line.ServiceCode.Trim(), line.ServiceName.Trim()),
                Quantity = new R4.Quantity { Value = FhirElements.Normalize(line.Quantity) },
                UnitPrice = Money(line.UnitPrice),
                Net = Money(net),
            };
            if (doctorId is not null)
            {
                item.CareTeamSequence = [1];
            }

            if (request.Pmjay)
            {
                item.ProgramCode = [FhirElements.Concept(ProgramSystem, PmjayProgram, PmjayProgramDisplay)];
            }

            claim.Item.Add(item);
        }

        claim.Total = Money(total);
        claim.Text = AbdmNarrative.Generated([
            $"{(final ? "Claim" : "Pre-authorisation request")} {request.Number.Trim()} to {request.Payer.Name}: "
            + $"{claim.Item.Count} service(s), total ₹{total.ToString("N2", CultureInfo.GetCultureInfo("en-IN"))}.",
        ]);
        return claim;
    }

    internal static R4.Money Money(decimal value) => new() { Value = FhirElements.Normalize(value), Currency = R4.Money.Currencies.INR };

    /// <summary>The desk's word for the patient's relation to the policy holder, as FHIR's subscriber relationship.</summary>
    private static (string Code, string Display)? Relationship(string? recorded) =>
        FhirElements.Clean(recorded)?.ToLowerInvariant() switch
        {
            null => null,
            "self" => ("self", "Self"),
            "spouse" or "wife" or "husband" => ("spouse", "Spouse"),
            "child" or "son" or "daughter" => ("child", "Child"),
            "parent" or "father" or "mother" => ("parent", "Parent"),
            _ => ("other", "Other"),
        };

    [GeneratedRegex(@"^[A-Z][0-9]{2}(\.[0-9A-Z]{1,4})?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex Icd10Code();

    /// <summary>Two letters for the specialty, three digits for the package, a letter for a stratified procedure (<c>BM001A</c>).</summary>
    [GeneratedRegex(@"^[A-Z]{2}[0-9]{3}[A-Z]?$", RegexOptions.CultureInvariant)]
    internal static partial Regex HbpProcedureCode();
}
