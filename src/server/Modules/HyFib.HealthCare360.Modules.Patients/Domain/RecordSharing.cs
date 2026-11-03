namespace HyFib.HealthCare360.Modules.Patients.Domain;

/// <summary>
/// The kinds of records a record-sharing consent (<see cref="ConsentType.DataSharing"/>) can name: what another care
/// provider may read through the FHIR API. Every resource the API serves is of one kind. The patient's own details
/// (name, sex, date of birth) go with any consent that lets the provider read, as they say whose records they are.
/// </summary>
public enum SharedRecordKind
{
    /// <summary>Visits and stays, and the diagnoses made in them (Encounter, Condition).</summary>
    Visits = 0,

    /// <summary>Allergies and intolerances (AllergyIntolerance).</summary>
    Allergies,

    /// <summary>Prescribed medicines (MedicationRequest).</summary>
    Medicines,

    /// <summary>Lab results and lab reports (laboratory Observations, lab DiagnosticReports).</summary>
    LabResults,

    /// <summary>Radiology and heart test reports (imaging and cardiology DiagnosticReports).</summary>
    ScansAndHeartTests,

    /// <summary>Vital signs (vital-signs Observations).</summary>
    VitalSigns,

    /// <summary>Documents of the record: discharge summaries and documents filed with it (DocumentReference).</summary>
    Documents,
}

/// <summary>
/// Why a provider reads the record: the HL7 v3 purposes of use (code system ActReason) that FHIR consents and ABDM
/// consent artefacts both use. The reader states it with each request (<c>X-Purpose-Of-Use</c>).
/// </summary>
public enum SharingPurpose
{
    /// <summary>TREAT: treating the patient.</summary>
    Treatment = 0,

    /// <summary>ETREAT: emergency treatment.</summary>
    EmergencyTreatment,

    /// <summary>CAREMGT: coordinating the patient's care.</summary>
    CareManagement,

    /// <summary>HPAYMT: payment for the patient's care (an insurer or a scheme settling a claim).</summary>
    Payment,

    /// <summary>PUBHLTH: public health reporting.</summary>
    PublicHealth,

    /// <summary>HRESCH: research.</summary>
    Research,

    /// <summary>PATRQT: at the patient's own request.</summary>
    PatientRequest,
}

/// <summary>The v3 ActReason codes of <see cref="SharingPurpose"/>.</summary>
public static class SharingPurposeCodes
{
    /// <summary>The HL7 v3 ActReason code system.</summary>
    public const string System = "http://terminology.hl7.org/CodeSystem/v3-ActReason";

    private static readonly IReadOnlyDictionary<SharingPurpose, string> Codes = new Dictionary<SharingPurpose, string>
    {
        [SharingPurpose.Treatment] = "TREAT",
        [SharingPurpose.EmergencyTreatment] = "ETREAT",
        [SharingPurpose.CareManagement] = "CAREMGT",
        [SharingPurpose.Payment] = "HPAYMT",
        [SharingPurpose.PublicHealth] = "PUBHLTH",
        [SharingPurpose.Research] = "HRESCH",
        [SharingPurpose.PatientRequest] = "PATRQT",
    };

    /// <summary>The purpose's code, for example <c>TREAT</c>.</summary>
    public static string CodeOf(SharingPurpose purpose) => Codes[purpose];

    /// <summary>Reads a code as sent, ignoring case and surrounding spaces; false for anything else.</summary>
    public static bool TryParse(string? code, out SharingPurpose purpose)
    {
        var trimmed = code?.Trim();
        foreach (var (candidate, value) in Codes)
        {
            if (string.Equals(value, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                purpose = candidate;
                return true;
            }
        }

        purpose = default;
        return false;
    }

    /// <summary>Every code, in the enum's order (for the CapabilityStatement and refusals).</summary>
    public static IReadOnlyList<string> All { get; } = [.. Enum.GetValues<SharingPurpose>().Select(CodeOf)];
}
