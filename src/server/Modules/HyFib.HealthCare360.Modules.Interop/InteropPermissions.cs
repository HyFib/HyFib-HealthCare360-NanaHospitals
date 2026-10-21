namespace HyFib.HealthCare360.Modules.Interop;

/// <summary>
/// Interoperability permissions. <see cref="FhirRead"/> opens the HL7 FHIR R4 read API (EHR-05), which returns PHI of
/// every patient who consented to record sharing, so only hospital administrators get it by default; integration service
/// accounts (referral partners, health information exchanges) are granted it explicitly through a role.
/// <see cref="AbdmExport"/> downloads one signed record (a discharge summary or a prescription) at a time as an ABDM
/// document bundle (EHR-08), which is how medical records release a record, so medical records officers get it as well.
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
            "Download signed discharge summaries and prescriptions as ABDM health records (FHIR document bundles; patients with record-sharing consent only; every export is audited)",
            [SystemRoles.HospitalAdmin, SystemRoles.MrdOfficer]),
    ];
}
