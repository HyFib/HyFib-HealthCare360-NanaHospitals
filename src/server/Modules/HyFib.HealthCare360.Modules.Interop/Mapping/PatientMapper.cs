using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping;

/// <summary>
/// Patients module patient → FHIR <c>Patient</c>. The UHID and (when on file) the ABHA number are identifiers; the
/// record shows the full phone number because it is the shared record (it is never logged).
/// </summary>
internal static class PatientMapper
{
    private static readonly Dictionary<string, string> LanguageCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["English"] = "en",
        ["Hindi"] = "hi",
        ["Tamil"] = "ta",
        ["Telugu"] = "te",
        ["Kannada"] = "kn",
        ["Malayalam"] = "ml",
        ["Marathi"] = "mr",
        ["Bengali"] = "bn",
        ["Gujarati"] = "gu",
        ["Punjabi"] = "pa",
        ["Odia"] = "or",
        ["Urdu"] = "ur",
        ["Assamese"] = "as",
    };

    public static R4.Patient Map(PatientRow p, ReferenceDirectory refs)
    {
        var patient = new R4.Patient
        {
            Id = FhirIds.Of(p.Id),
            Meta = FhirElements.Meta(p.CreatedAt, p.UpdatedAt),
            Active = p.IsActive && p.MergedIntoPatientId is null,
            Gender = Gender(p.Gender),
            BirthDate = BirthDate(p.DateOfBirth, p.IsDateOfBirthEstimated),
            Deceased = Deceased(p.IsDeceased, p.DeceasedAt),
            MaritalStatus = MaritalStatus(p.MaritalStatus),
            ManagingOrganization = refs.Organization(p.RegisteredFacilityId),
        };

        patient.Identifier.Add(Uhid(p.Uhid));
        if (Abha(p.AbhaNumber) is { } abha)
        {
            patient.Identifier.Add(abha);
        }

        patient.Name.Add(Name(p));
        patient.Telecom.AddRange(Telecom(p));
        if (Address(p.Address) is { } address)
        {
            patient.Address.Add(address);
        }

        patient.Contact.AddRange(Contacts(p));
        if (Communication(p.PreferredLanguage) is { } language)
        {
            patient.Communication.Add(language);
        }

        return patient;
    }

    public static R4.AdministrativeGender Gender(Gender gender) => gender switch
    {
        SharedKernel.Gender.Male => R4.AdministrativeGender.Male,
        SharedKernel.Gender.Female => R4.AdministrativeGender.Female,
        SharedKernel.Gender.Other => R4.AdministrativeGender.Other,
        _ => R4.AdministrativeGender.Unknown,
    };

    /// <summary>
    /// An estimated date of birth (registered from a stated age) is published with year precision only, so receivers do
    /// not treat an invented day and month as fact.
    /// </summary>
    public static string? BirthDate(DateOnly? dateOfBirth, bool estimated) =>
        dateOfBirth is not { } dob ? null
        : estimated ? dob.Year.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)
        : FhirTime.Date(dob);

    /// <summary>The hospital-group UHID (the medical record number).</summary>
    public static R4.Identifier Uhid(string uhid)
    {
        var identifier = FhirElements.Identifier(FhirSystems.Uhid, uhid, "MR", "Medical record number");
        identifier.Use = R4.Identifier.IdentifierUse.Usual;
        return identifier;
    }

    /// <summary>ABHA number as an ABDM identifier, formatted NN-NNNN-NNNN-NNNN (<see cref="SharedKernel.Abha.Format"/>).</summary>
    public static R4.Identifier? Abha(string? abhaNumber)
    {
        if (string.IsNullOrWhiteSpace(abhaNumber))
        {
            return null;
        }

        return new R4.Identifier(FhirSystems.Abha, SharedKernel.Abha.Format(abhaNumber))
        {
            Use = R4.Identifier.IdentifierUse.Official,
            Type = new R4.CodeableConcept
            {
                Coding = [FhirElements.Coding(FhirSystems.IdentifierType, "NI", "National unique individual identifier")],
                Text = "ABHA number",
            },
        };
    }

    private static R4.DataType? Deceased(bool isDeceased, DateTimeOffset? deceasedAt) =>
        !isDeceased ? null
        : deceasedAt is { } at ? new R4.FhirDateTime(FhirTime.DateTime(at))
        : new R4.FhirBoolean(true);

    public static R4.CodeableConcept MaritalStatus(MaritalStatus status) => status switch
    {
        SharedKernel.MaritalStatus.Single => FhirElements.Concept(FhirSystems.MaritalStatus, "S", "Never Married"),
        SharedKernel.MaritalStatus.Married => FhirElements.Concept(FhirSystems.MaritalStatus, "M", "Married"),
        SharedKernel.MaritalStatus.Divorced => FhirElements.Concept(FhirSystems.MaritalStatus, "D", "Divorced"),
        SharedKernel.MaritalStatus.Widowed => FhirElements.Concept(FhirSystems.MaritalStatus, "W", "Widowed"),
        SharedKernel.MaritalStatus.Separated => FhirElements.Concept(FhirSystems.MaritalStatus, "L", "Legally Separated"),
        _ => FhirElements.Concept(FhirSystems.NullFlavor, "UNK", "unknown"),
    };

    private static R4.HumanName Name(PatientRow p)
    {
        var name = new R4.HumanName
        {
            Use = R4.HumanName.NameUse.Official,
            Text = FhirElements.Clean(p.FullName) ?? Patient.ComposeFullName(p.FirstName, p.MiddleName, p.LastName),
            Family = FhirElements.Clean(p.LastName),
            Given = new[] { p.FirstName, p.MiddleName }.Select(FhirElements.Clean).OfType<string>().ToList(),
        };
        if (FhirElements.Clean(p.Title) is { } title)
        {
            name.Prefix = [title];
        }

        return name;
    }

    private static IEnumerable<R4.ContactPoint> Telecom(PatientRow p)
    {
        var rank = 1;
        if (FhirElements.Clean(p.Phone) is { } phone)
        {
            yield return new R4.ContactPoint(R4.ContactPoint.ContactPointSystem.Phone, PhoneUse(phone), phone) { Rank = rank++ };
        }

        if (FhirElements.Clean(p.AlternatePhone) is { } alternate)
        {
            yield return new R4.ContactPoint(R4.ContactPoint.ContactPointSystem.Phone, PhoneUse(alternate), alternate) { Rank = rank++ };
        }

        if (FhirElements.Clean(p.Email) is { } email)
        {
            yield return new R4.ContactPoint(R4.ContactPoint.ContactPointSystem.Email, R4.ContactPoint.ContactPointUse.Home, email) { Rank = rank };
        }
    }

    private static R4.ContactPoint.ContactPointUse PhoneUse(string e164) =>
        Phone.IsIndianMobile(e164) ? R4.ContactPoint.ContactPointUse.Mobile : R4.ContactPoint.ContactPointUse.Home;

    public static R4.Address? Address(Address address)
    {
        string?[] lines = [FhirElements.Clean(address.Line1), FhirElements.Clean(address.Line2)];
        var city = FhirElements.Clean(address.City);
        var district = FhirElements.Clean(address.District);
        var state = FhirElements.Clean(address.State);
        var pin = FhirElements.Clean(address.PinCode);
        if (lines.All(l => l is null) && city is null && district is null && state is null && pin is null)
        {
            return null;
        }

        return new R4.Address
        {
            Use = R4.Address.AddressUse.Home,
            Type = R4.Address.AddressType.Physical,
            Text = FhirElements.Clean(address.ToString()),
            Line = lines.OfType<string>().ToList(),
            City = city,
            District = district,
            State = state,
            PostalCode = pin,
            Country = FhirElements.Clean(address.Country),
        };
    }

    private static IEnumerable<R4.Patient.ContactComponent> Contacts(PatientRow p)
    {
        var emergencyName = FhirElements.Clean(p.EmergencyContactName);
        var emergencyPhone = FhirElements.Clean(p.EmergencyContactPhone);
        if (emergencyName is not null || emergencyPhone is not null)
        {
            var contact = new R4.Patient.ContactComponent
            {
                Relationship = [FhirElements.Concept(FhirSystems.ContactRole, "C", "Emergency Contact", FhirElements.Clean(p.EmergencyContactRelation))],
                Name = emergencyName is null ? null : new R4.HumanName { Text = emergencyName },
            };
            if (emergencyPhone is not null)
            {
                contact.Telecom.Add(new R4.ContactPoint(R4.ContactPoint.ContactPointSystem.Phone, PhoneUse(emergencyPhone), emergencyPhone));
            }

            yield return contact;
        }

        if (FhirElements.Clean(p.GuardianName) is { } guardian)
        {
            yield return new R4.Patient.ContactComponent
            {
                Relationship = p.GuardianRelation is { } relation ? [GuardianRole(relation)] : [],
                Name = new R4.HumanName { Text = guardian },
            };
        }
    }

    /// <summary>
    /// "S/O, D/O, W/O…" on Indian registration cards names the patient's relation to the guardian; FHIR records the
    /// guardian's role towards the patient (v3 RoleCode).
    /// </summary>
    public static R4.CodeableConcept GuardianRole(GuardianRelation relation) => relation switch
    {
        Patients.Domain.GuardianRelation.SonOf => FhirElements.Concept(FhirSystems.RoleCode, "PRN", "parent", "Parent (S/O)"),
        Patients.Domain.GuardianRelation.DaughterOf => FhirElements.Concept(FhirSystems.RoleCode, "PRN", "parent", "Parent (D/O)"),
        Patients.Domain.GuardianRelation.WifeOf => FhirElements.Concept(FhirSystems.RoleCode, "HUSB", "husband", "Husband (W/O)"),
        Patients.Domain.GuardianRelation.HusbandOf => FhirElements.Concept(FhirSystems.RoleCode, "WIFE", "wife", "Wife (H/O)"),
        Patients.Domain.GuardianRelation.FatherOf => FhirElements.Concept(FhirSystems.RoleCode, "CHILD", "child", "Child (F/O)"),
        Patients.Domain.GuardianRelation.MotherOf => FhirElements.Concept(FhirSystems.RoleCode, "CHILD", "child", "Child (M/O)"),
        _ => FhirElements.Concept(FhirSystems.RoleCode, "GUARD", "guardian", "Guardian"),
    };

    public static R4.Patient.CommunicationComponent? Communication(string? preferredLanguage)
    {
        if (FhirElements.Clean(preferredLanguage) is not { } language)
        {
            return null;
        }

        var concept = LanguageCodes.TryGetValue(language, out var code)
            ? FhirElements.Concept(FhirSystems.Language, code, language, language)
            : FhirElements.Text(language);
        return new R4.Patient.CommunicationComponent { Language = concept, Preferred = true };
    }
}
