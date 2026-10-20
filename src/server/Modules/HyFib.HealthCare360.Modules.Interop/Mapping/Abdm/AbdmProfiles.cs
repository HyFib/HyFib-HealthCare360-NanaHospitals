namespace HyFib.HealthCare360.Modules.Interop.Mapping.Abdm;

/// <summary>
/// The profiles of the NRCeS "FHIR Implementation Guide for ABDM" (package <c>ndhm.in</c> 6.5.0, canonical
/// <c>https://nrces.in/ndhm/fhir/r4</c>) that HC360's ABDM health records claim in <c>meta.profile</c>. The tests
/// validate the documents against the profiles of that package.
/// </summary>
internal static class AbdmProfiles
{
    /// <summary>The version of the implementation guide the documents are built to.</summary>
    public const string ImplementationGuideVersion = "6.5.0";

    private const string Base = "https://nrces.in/ndhm/fhir/r4/StructureDefinition/";

    public const string DocumentBundle = Base + "DocumentBundle";
    public const string DischargeSummaryRecord = Base + "DischargeSummaryRecord";
    public const string Patient = Base + "Patient";
    public const string Practitioner = Base + "Practitioner";
    public const string Organization = Base + "Organization";
    public const string Encounter = Base + "Encounter";
    public const string Condition = Base + "Condition";
    public const string CarePlan = Base + "CarePlan";
    public const string DocumentReference = Base + "DocumentReference";
}
