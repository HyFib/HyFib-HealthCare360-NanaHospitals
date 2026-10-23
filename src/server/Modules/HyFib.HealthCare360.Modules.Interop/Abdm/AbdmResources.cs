using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Abdm;

/// <summary>A doctor named in a document, from the practitioner master.</summary>
internal sealed record PractitionerRow
{
    public required Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string? Title { get; init; }
    public string FirstName { get; init; } = string.Empty;
    public string? LastName { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public Gender Gender { get; init; }
    public string? Qualification { get; init; }
    public string? RegistrationCouncil { get; init; }
    public string? RegistrationNumber { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>The facility (hospital or branch) that holds the record, from the facility master.</summary>
internal sealed record FacilityRow
{
    public required Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? HfrId { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public Address Address { get; init; } = new();
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
/// The Practitioner and Organization resources of a document. The IG requires a typed identifier on both: the doctor's
/// medical-council registration (v2-0203 MD) and staff code, and the facility's Health Facility Registry id (HFR,
/// v2-0203 PRN) and facility code. A doctor's own phone number and e-mail are not put in patients' documents.
/// </summary>
internal static class AbdmParticipants
{
    public static R4.Practitioner Practitioner(PractitionerRow p)
    {
        var practitioner = new R4.Practitioner
        {
            Id = FhirIds.Of(p.Id),
            Meta = FhirElements.Meta(p.CreatedAt, p.UpdatedAt, AbdmProfiles.Practitioner),
            Gender = PatientMapper.Gender(p.Gender),
        };

        if (FhirElements.Clean(p.RegistrationNumber) is { } registration)
        {
            practitioner.Identifier.Add(new R4.Identifier(AbdmSystems.MedicalRegistration, registration)
            {
                Use = R4.Identifier.IdentifierUse.Official,
                Type = FhirElements.Concept(FhirSystems.IdentifierType, "MD", "Medical License number"),
                Assigner = FhirElements.Clean(p.RegistrationCouncil) is { } council ? new R4.ResourceReference { Display = council } : null,
            });
        }

        practitioner.Identifier.Add(new R4.Identifier(AbdmSystems.Practitioner, FhirElements.Clean(p.Code) ?? FhirIds.Of(p.Id))
        {
            Use = R4.Identifier.IdentifierUse.Usual,
            Type = FhirElements.Concept(FhirSystems.IdentifierType, "PRN", "Provider number"),
        });

        var name = new R4.HumanName
        {
            Text = FhirElements.Clean(p.DisplayName) ?? string.Join(' ', new[] { p.Title, p.FirstName, p.LastName }.Select(FhirElements.Clean).OfType<string>()),
            Family = FhirElements.Clean(p.LastName),
            Given = new[] { p.FirstName }.Select(FhirElements.Clean).OfType<string>().ToList(),
        };
        if (FhirElements.Clean(p.Title) is { } title)
        {
            name.Prefix = [title];
        }

        practitioner.Name.Add(name);
        if (FhirElements.Clean(p.Qualification) is { } qualification)
        {
            practitioner.Qualification.Add(new R4.Practitioner.QualificationComponent { Code = FhirElements.Text(qualification) });
        }

        return practitioner;
    }

    public static R4.Organization Organization(FacilityRow f)
    {
        var organization = new R4.Organization
        {
            Id = FhirIds.Of(f.Id),
            Meta = FhirElements.Meta(f.CreatedAt, f.UpdatedAt, AbdmProfiles.Organization),
            Name = f.Name,
            Type = [FhirElements.Concept(AbdmSystems.OrganizationType, "prov", "Healthcare Provider")],
        };

        if (FhirElements.Clean(f.HfrId) is { } hfr)
        {
            organization.Identifier.Add(new R4.Identifier(FhirSystems.Hfr, hfr)
            {
                Use = R4.Identifier.IdentifierUse.Official,
                Type = FhirElements.Concept(FhirSystems.IdentifierType, "PRN", "Provider number"),
            });
        }

        organization.Identifier.Add(new R4.Identifier(AbdmSystems.Facility, FhirElements.Clean(f.Code) ?? FhirIds.Of(f.Id))
        {
            Use = R4.Identifier.IdentifierUse.Usual,
            Type = FhirElements.Concept(FhirSystems.IdentifierType, "XX", "Organization identifier"),
        });

        if (FhirElements.Clean(f.Phone) is { } phone)
        {
            organization.Telecom.Add(new R4.ContactPoint(R4.ContactPoint.ContactPointSystem.Phone, R4.ContactPoint.ContactPointUse.Work, phone));
        }

        if (FhirElements.Clean(f.Email) is { } email)
        {
            organization.Telecom.Add(new R4.ContactPoint(R4.ContactPoint.ContactPointSystem.Email, R4.ContactPoint.ContactPointUse.Work, email));
        }

        if (PatientMapper.Address(f.Address) is { } address)
        {
            address.Use = R4.Address.AddressUse.Work;
            organization.Address.Add(address);
        }

        return organization;
    }

    /// <summary>A referral laboratory: an outside provider, named by its code in the laboratory's master.</summary>
    public static R4.Organization ReferralLab(ReferralLabRow l)
    {
        var organization = new R4.Organization
        {
            Id = FhirIds.Of(l.Id),
            Meta = FhirElements.Meta(l.CreatedAt, l.UpdatedAt, AbdmProfiles.Organization),
            Name = l.Name,
            Type = [FhirElements.Concept(AbdmSystems.OrganizationType, "prov", "Healthcare Provider")],
        };

        organization.Identifier.Add(new R4.Identifier(AbdmSystems.ReferralLab, FhirElements.Clean(l.Code) ?? FhirIds.Of(l.Id))
        {
            Use = R4.Identifier.IdentifierUse.Usual,
            Type = FhirElements.Concept(FhirSystems.IdentifierType, "XX", "Organization identifier"),
        });

        if (FhirElements.Clean(l.Phone) is { } phone)
        {
            organization.Telecom.Add(new R4.ContactPoint(R4.ContactPoint.ContactPointSystem.Phone, R4.ContactPoint.ContactPointUse.Work, phone));
        }

        if (FhirElements.Clean(l.Email) is { } email)
        {
            organization.Telecom.Add(new R4.ContactPoint(R4.ContactPoint.ContactPointSystem.Email, R4.ContactPoint.ContactPointUse.Work, email));
        }

        if (FhirElements.Clean(l.Address) is { } text)
        {
            organization.Address.Add(new R4.Address { Use = R4.Address.AddressUse.Work, Text = text });
        }

        return organization;
    }
}

/// <summary>
/// Brings the FHIR read API's resources in line with the IG's profiles: the profile in <c>meta.profile</c>, the ABHA
/// number typed with the IG's ABHA identifier type, observation and report codings limited to the code systems the IG
/// allows (LOINC and SNOMED CT, each with a display), a SNOMED CT service section and an interpreter on laboratory and
/// imaging reports. Nothing clinical is added or changed.
/// </summary>
internal static class AbdmProfiler
{
    /// <summary>
    /// The conclusion of a laboratory report that flags nothing. The IG requires a conclusion, the laboratory does not
    /// write one, and an unflagged result is not a normal one (a qualitative result, or one without a reference range,
    /// is never flagged), so it says nothing about the results themselves.
    /// </summary>
    public const string SeeTheResults = "See the individual results.";

    /// <summary>What a radiology report's one <c>media</c> entry says, as it carries no key image (<see cref="ImagingReport"/>).</summary>
    public const string NoKeyImages = "No key images are attached: the images of the study are kept in the hospital's PACS.";

    /// <summary>Vital signs the IG's ObservationVitalSigns profile names.</summary>
    private static readonly HashSet<string> VitalSignCodes = ["85354-9", "8867-4", "59408-5", "2708-6", "8310-5", "9279-1"];

    /// <summary>Body measurements of the IG's ObservationBodyMeasurement profile.</summary>
    private static readonly HashSet<string> BodyMeasurementCodes = ["29463-7", "8302-2", "39156-5"];

    public static R4.Patient Patient(R4.Patient patient)
    {
        patient.Meta = Profile(patient.Meta, AbdmProfiles.Patient);
        foreach (var identifier in patient.Identifier.Where(i => i.System == FhirSystems.Abha))
        {
            identifier.Type = new R4.CodeableConcept
            {
                Coding = [FhirElements.Coding(AbdmSystems.IdentifierType, "ABHA", "Ayushman Bharat Health Account (ABHA) ID")],
                Text = "ABHA number",
            };
        }

        return patient;
    }

    public static R4.Encounter Encounter(R4.Encounter encounter)
    {
        encounter.Meta = Profile(encounter.Meta, AbdmProfiles.Encounter);
        return encounter;
    }

    public static R4.Condition Condition(R4.Condition condition)
    {
        condition.Meta = Profile(condition.Meta, AbdmProfiles.Condition);
        Codings(condition.Code, [FhirSystems.Icd10, FhirSystems.Snomed]);
        return condition;
    }

    public static R4.AllergyIntolerance Allergy(R4.AllergyIntolerance allergy)
    {
        allergy.Meta = Profile(allergy.Meta, AbdmProfiles.AllergyIntolerance);
        Codings(allergy.Code, [FhirSystems.Snomed]);
        return allergy;
    }

    public static R4.MedicationRequest MedicationRequest(R4.MedicationRequest request)
    {
        request.Meta = Profile(request.Meta, AbdmProfiles.MedicationRequest);
        return request;
    }

    /// <summary>
    /// Vital signs and body measurements get the IG's profiles for them, every other observation (laboratory results,
    /// pain score, GCS) the IG's general Observation profile. Only LOINC and SNOMED CT codings with a display stay
    /// (<see cref="Codings"/>): a lab test whose LOINC name the laboratory has not entered goes by its own name, as text.
    /// </summary>
    public static R4.Observation Observation(R4.Observation observation)
    {
        Codings(observation.Code, [FhirSystems.Loinc, FhirSystems.Snomed]);
        var codes = observation.Code?.Coding.Where(c => c.System == FhirSystems.Loinc).Select(c => c.Code).ToList() ?? [];
        var profile = codes.Any(c => c is not null && VitalSignCodes.Contains(c)) ? AbdmProfiles.ObservationVitalSigns
            : codes.Any(c => c is not null && BodyMeasurementCodes.Contains(c)) ? AbdmProfiles.ObservationBodyMeasurement
            : AbdmProfiles.Observation;
        observation.Meta = Profile(observation.Meta, profile);
        return observation;
    }

    /// <summary>
    /// A laboratory report: the IG's DiagnosticReportLab profile wants a SNOMED CT service section, someone who
    /// interpreted the results (the laboratory, <paramref name="laboratory"/>), and a conclusion. Reports carry no
    /// written conclusion, so it names the results the laboratory flagged (the observations' interpretation), or only
    /// points to the results (<see cref="SeeTheResults"/>).
    /// </summary>
    public static R4.DiagnosticReport LabReport(R4.DiagnosticReport report, R4.ResourceReference laboratory, IReadOnlyList<R4.Observation> results)
    {
        report.Meta = Profile(report.Meta, AbdmProfiles.DiagnosticReportLab);
        report.Category = [AbdmCodes.PathologyService.Concept("Laboratory")];
        Codings(report.Code, [FhirSystems.Loinc]);
        report.Performer = [Copy(laboratory)];
        report.ResultsInterpreter = [Copy(laboratory)];
        report.Conclusion ??= LabConclusion(results);
        return report;
    }

    /// <summary>
    /// A radiology report: the IG's DiagnosticReportImaging profile wants a SNOMED CT service section, LOINC codings only,
    /// who interpreted the study (<paramref name="interpreters"/>: the radiologists, else the hospital, which performed the
    /// study and issues the report), the study (<paramref name="study"/>), a conclusion and at least one key image
    /// (<c>media</c>). HC360 keeps no key images: the images stay in the hospital's PACS, which the imaging study names, and
    /// no one marks key images among them. So the one media entry says there is none, with the reason FHIR gives a
    /// required element the source system cannot fill (data-absent-reason <c>unsupported</c>), rather than carrying
    /// something that is not a key image of the study.
    /// </summary>
    public static R4.DiagnosticReport ImagingReport(
        R4.DiagnosticReport report, R4.ResourceReference hospital, IReadOnlyList<R4.ResourceReference> interpreters, R4.ResourceReference study)
    {
        report.Meta = Profile(report.Meta, AbdmProfiles.DiagnosticReportImaging);
        report.Category = [AbdmCodes.RadiologyService.Concept("Radiology")];
        Codings(report.Code, [FhirSystems.Loinc]);
        report.Performer = [Copy(hospital)];
        report.ResultsInterpreter = interpreters.Count > 0 ? [.. interpreters.Select(Copy)] : [Copy(hospital)];
        report.ImagingStudy = [Copy(study)];
        // A reference to nothing still says what it stands for (the HL7 validator warns on one without a display).
        var noImage = new R4.ResourceReference { Display = "No key image" };
        noImage.Extension.Add(new R4.Extension(AbdmSystems.DataAbsentReason, new R4.Code("unsupported")));
        report.Media = [new R4.DiagnosticReport.MediaComponent { Comment = NoKeyImages, Link = noImage }];
        return report;
    }

    public static R4.DocumentReference DocumentReference(R4.DocumentReference document)
    {
        document.Meta = Profile(document.Meta, AbdmProfiles.DocumentReference);
        return document;
    }

    /// <summary>"Flagged by the laboratory: Haemoglobin (Low), CRP (High).", or <see cref="SeeTheResults"/>.</summary>
    public static string LabConclusion(IReadOnlyList<R4.Observation> results)
    {
        var flagged = results
            .Select(o => (Test: o.Code?.Text, Flag: o.Interpretation.SelectMany(i => i.Coding).FirstOrDefault(c => c.Code != "N")?.Display))
            .Where(r => r.Flag is not null && !string.IsNullOrWhiteSpace(r.Test))
            .Select(r => $"{r.Test} ({r.Flag})")
            .ToList();
        return flagged.Count == 0
            ? SeeTheResults
            : $"Flagged by the laboratory: {string.Join(", ", flagged)}.";
    }

    /// <summary>
    /// Keeps the codings of the allowed systems that have a display. The IG requires one on each, and a display is the
    /// code system's own name for the code, never the hospital's: a coding without it is left out, and the concept's text
    /// still says what it is.
    /// </summary>
    private static void Codings(R4.CodeableConcept? concept, string[] systems)
    {
        if (concept is not null)
        {
            concept.Coding = concept.Coding
                .Where(c => systems.Contains(c.System, StringComparer.Ordinal) && !string.IsNullOrWhiteSpace(c.Display))
                .ToList();
        }
    }

    /// <summary>The IG profile in place of any other (the FHIR vital-signs profiles of the read API).</summary>
    private static R4.Meta Profile(R4.Meta? meta, string profile)
    {
        meta ??= new R4.Meta();
        meta.Profile = [profile];
        return meta;
    }

    private static R4.ResourceReference Copy(R4.ResourceReference reference) => R4.BaseExtensions.DeepCopy(reference);
}
