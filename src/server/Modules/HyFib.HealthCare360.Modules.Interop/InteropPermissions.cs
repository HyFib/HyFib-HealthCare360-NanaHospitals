namespace HyFib.HealthCare360.Modules.Interop;

/// <summary>
/// Interoperability permissions. <see cref="FhirRead"/> opens the HL7 FHIR R4 read API (EHR-05), which returns PHI of
/// every patient who consented to record sharing, so only hospital administrators get it by default; integration service
/// accounts (referral partners, health information exchanges) are granted it explicitly through a role.
/// <see cref="AbdmExport"/> exports one record at a time as an ABDM health record (EHR-08), for the medical records
/// department and administrators.
/// </summary>
public static class InteropPermissions
{
    public const string FhirRead = "fhir.read";

    public const string AbdmExport = "abdm.export";

    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(FhirRead, "Interoperability",
            "Read and search patient records through the HL7 FHIR R4 API (patients with record-sharing consent only; every access is audited)",
            [SystemRoles.HospitalAdmin]),
        new(AbdmExport, "Interoperability",
            "Export discharge summaries, lab reports, consultation notes and prescriptions as ABDM health records (FHIR documents; every export is audited)",
            [SystemRoles.HospitalAdmin, SystemRoles.MrdOfficer]),
    ];
}
