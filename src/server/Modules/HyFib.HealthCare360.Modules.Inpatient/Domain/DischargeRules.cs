namespace HyFib.HealthCare360.Modules.Inpatient.Domain;

/// <summary>
/// Policy for how each discharge type is processed. Death and abscondence are recorded immediately: a body must not be
/// held for unpaid bills and an absconded patient cannot wait for billing, so neither needs billing clearance nor the
/// initiate step. Families of deceased or absconded patients are not sent the routine "discharged, get well soon" SMS.
/// </summary>
public static class DischargeRules
{
    public static AdmissionStatus StatusFor(DischargeType type) => type switch
    {
        DischargeType.Lama => AdmissionStatus.Lama,
        DischargeType.Dama => AdmissionStatus.Dama,
        DischargeType.Absconded => AdmissionStatus.Absconded,
        DischargeType.Expired => AdmissionStatus.Expired,
        _ => AdmissionStatus.Discharged,
    };

    public static bool RequiresBillingClearance(DischargeType type) => type is not (DischargeType.Expired or DischargeType.Absconded);

    public static bool RequiresInitiation(DischargeType type) => RequiresBillingClearance(type);

    public static bool NotifiesPatient(DischargeType type) => RequiresBillingClearance(type);

    /// <summary>LAMA, DAMA and abscondence are medico-legal events: the record must say who was informed and what was signed.</summary>
    public static bool RequiresNotes(DischargeType type) => type is DischargeType.Lama or DischargeType.Dama or DischargeType.Absconded;
}
