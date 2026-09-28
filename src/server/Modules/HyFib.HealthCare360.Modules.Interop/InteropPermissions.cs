namespace HyFib.HealthCare360.Modules.Interop;

/// <summary>
/// Interoperability permissions (EHR-05). <see cref="FhirRead"/> opens the HL7 FHIR R4 read API, which returns PHI of
/// every patient who consented to record sharing, so only hospital administrators get it by default; integration service
/// accounts (referral partners, health information exchanges) are granted it explicitly through a role.
/// </summary>
public static class InteropPermissions
{
    public const string FhirRead = "fhir.read";

    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(FhirRead, "Interoperability",
            "Read and search patient records through the HL7 FHIR R4 API (patients with record-sharing consent only; every access is audited)",
            [SystemRoles.HospitalAdmin]),
    ];
}
