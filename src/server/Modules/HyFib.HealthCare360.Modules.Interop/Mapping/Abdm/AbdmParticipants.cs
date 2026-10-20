using HyFib.HealthCare360.Modules.Interop.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping.Abdm;

/// <summary>
/// The doctor and the hospital of an ABDM health record, as the NRCeS Practitioner and Organization profiles want them
/// (each with at least one typed identifier), and the references to them and to the patient within the document.
/// <list type="bullet">
/// <item>Practitioner: the medical council registration number (v2-0203 <c>MD</c>, the council as assigner) when on
/// file, and the HC360 practitioner code; name, gender when known and the qualification as written. No telephone or
/// e-mail: a shared record names the doctor, it does not publish their contact details.</item>
/// <item>Organization: the ABDM Health Facility Registry id (<c>https://facility.ndhm.gov.in</c>, typed <c>PRN</c> as in the
/// guide's examples) when on file, and the HC360 facility code; a healthcare provider (organization-type <c>prov</c>)
/// with its address, telephone and e-mail.</item>
/// </list>
/// </summary>
internal static class AbdmParticipants
{
    /// <summary>A reference to the document's patient entry.</summary>
    public static R4.ResourceReference Patient(PatientRow patient) => DocumentBundleBuilder.Reference(patient.Id, patient.FullName);

    /// <summary>
    /// The names a document's resources refer to, with its doctor and hospital referenced by their entries (and named as
    /// on file), so the mappers the FHIR API shares (patient, encounter, medication request) point inside the document.
    /// </summary>
    public static ReferenceDirectory WithEntries(ReferenceDirectory refs, FacilityRow facility, PractitionerRow? doctor)
    {
        var practitioners = new Dictionary<Guid, string>(refs.Practitioners);
        var facilities = new Dictionary<Guid, FacilityInfo>(refs.Facilities) { [facility.Id] = new(facility.Name, facility.HfrId) };
        var entries = new Dictionary<Guid, string> { [facility.Id] = DocumentBundleBuilder.FullUrl(facility.Id) };
        if (doctor is not null)
        {
            practitioners[doctor.Id] = doctor.DisplayName;
            entries[doctor.Id] = DocumentBundleBuilder.FullUrl(doctor.Id);
        }

        return new ReferenceDirectory
        {
            Practitioners = practitioners,
            Departments = refs.Departments,
            Facilities = facilities,
            AdmissionVisits = refs.AdmissionVisits,
            Entries = entries,
        };
    }

    public static R4.Practitioner Practitioner(PractitionerRow p)
    {
        var practitioner = new R4.Practitioner
        {
            Meta = FhirElements.Meta(p.CreatedAt, p.UpdatedAt),
            Gender = p.Gender == Gender.Unknown ? null : PatientMapper.Gender(p.Gender),
        };

        // A state medical council's registration number has no URI of its own (an ABDM HPR id would, but HC360 does not
        // record one), so it is typed and assigned by the council instead.
        if (FhirElements.Clean(p.RegistrationNumber) is { } registration)
        {
            practitioner.Identifier.Add(new R4.Identifier
            {
                Use = R4.Identifier.IdentifierUse.Official,
                Type = FhirElements.Concept(FhirSystems.IdentifierType, "MD", "Medical License number"),
                Value = registration,
                Assigner = FhirElements.Clean(p.RegistrationCouncil) is { } council ? new R4.ResourceReference { Display = council } : null,
            });
        }

        if (FhirElements.Clean(p.Code) is { } code)
        {
            practitioner.Identifier.Add(FhirElements.Identifier(FhirSystems.PractitionerCode, code, "PRN", "Provider number"));
        }

        var name = new R4.HumanName
        {
            Use = R4.HumanName.NameUse.Official,
            Text = FhirElements.Clean(p.DisplayName) ?? string.Join(' ', new[] { p.Title, p.FirstName, p.LastName }.Select(FhirElements.Clean).OfType<string>()),
            Family = FhirElements.Clean(p.LastName),
            Given = FhirElements.Clean(p.FirstName) is { } given ? [given] : [],
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
            Meta = FhirElements.Meta(f.CreatedAt, f.UpdatedAt),
            Name = FhirElements.Clean(f.Name) ?? f.Code,
        };

        if (FhirElements.Clean(f.HfrId) is { } hfr)
        {
            var identifier = FhirElements.Identifier(FhirSystems.Hfr, hfr, "PRN", "Provider number");
            identifier.Use = R4.Identifier.IdentifierUse.Official;
            organization.Identifier.Add(identifier);
        }

        organization.Identifier.Add(FhirElements.Identifier(FhirSystems.FacilityCode, f.Code, "XX", "Organization identifier"));
        organization.Type.Add(FhirElements.Concept(FhirSystems.OrganizationType, "prov", "Healthcare Provider"));
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
}
