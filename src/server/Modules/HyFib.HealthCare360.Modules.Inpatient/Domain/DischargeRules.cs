using HyFib.HealthCare360.Modules.Clinical.Services;

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

    /// <summary>A discharge the doctor planned: home, or referred to another hospital (not a death, against advice or absconded).</summary>
    public static bool IsPlanned(DischargeType type) => type is DischargeType.Normal or DischargeType.Referred;

    public static bool NotifiesPatient(DischargeType type) => RequiresBillingClearance(type);

    /// <summary>LAMA, DAMA and abscondence are medico-legal events: the record must say who was informed and what was signed.</summary>
    public static bool RequiresNotes(DischargeType type) => type is DischargeType.Lama or DischargeType.Dama or DischargeType.Absconded;

    /// <summary>
    /// LAMA and DAMA go with who signed the form (or that nobody would), the doctor who explained the risks and a witness
    /// (maker-checker, slice 8).
    /// </summary>
    public static bool IsAgainstAdvice(DischargeType type) => type is DischargeType.Lama or DischargeType.Dama;

    /// <summary>
    /// The stays one person does not close (maker-checker, slice 8): an absconded patient, and a death closed without its
    /// certificate (a medico-legal case whose certificate waits for the post-mortem). A second person approves.
    /// </summary>
    public static bool WaitsForSecondPerson(DischargeType type, bool certificatePendingPostMortem) =>
        type == DischargeType.Absconded || (type == DischargeType.Expired && certificatePendingPostMortem);
}

/// <summary>What closing a stay as expired needs, from the patient's death certificate (maker-checker, slice 8).</summary>
public enum ExpiredDischargeOutcome
{
    /// <summary>The certificate is confirmed (or reported, or the facility does not ask for confirmation): closed at once.</summary>
    Certified = 0,

    /// <summary>A medico-legal death whose certificate waits for the post-mortem: a second doctor approves closing it without one.</summary>
    AskPendingPostMortem,
}

public static class ExpiredDischarge
{
    /// <summary>
    /// Whether the stay may be closed as expired with <paramref name="certificate"/>, the patient's certificate (null when
    /// none is certified): the certificate is this stay's (or names none) and a second doctor confirmed it. Without one,
    /// only a medico-legal stay whose certificate waits for the post-mortem goes on, and then only with a second doctor's
    /// approval. Otherwise the reason it waits.
    /// </summary>
    public static Result<ExpiredDischargeOutcome> Check(DeathCertificateStanding? certificate, Guid admissionId, bool medicoLegal, bool pendingPostMortem)
    {
        if (certificate is null)
        {
            if (!pendingPostMortem)
            {
                return new Error("discharge.death_certificate_required",
                    "Certify the cause of death (MCCD, Form 4) first: the stay is closed as expired with the death certificate, once a second doctor confirms it.",
                    ErrorType.BusinessRule);
            }

            return medicoLegal
                ? ExpiredDischargeOutcome.AskPendingPostMortem
                : Error.Rule("discharge.postmortem_not_mlc",
                    "Only a medico-legal death is closed before its certificate, while the post-mortem is pending. Certify the cause of death first.");
        }

        if (certificate.AdmissionId is { } stay && stay != admissionId)
        {
            return Error.Rule("discharge.death_certificate_other_stay",
                $"Death certificate {certificate.SerialNumber} names another stay. Correct the certificate, or close the stay it names.");
        }

        if (!certificate.Settled)
        {
            return new Error("discharge.death_certificate_unconfirmed",
                $"Death certificate {certificate.SerialNumber} is not yet confirmed by a second doctor. The stay is closed as expired once it is"
                + (certificate.ConfirmationWaitingSince is not null ? " (the confirmation is asked for and waiting)." : "; the certifying doctor asks for the confirmation."),
                ErrorType.BusinessRule)
            {
                Data = new { certificate = certificate.SerialNumber, waitingSince = certificate.ConfirmationWaitingSince },
            };
        }

        return ExpiredDischargeOutcome.Certified;
    }
}
