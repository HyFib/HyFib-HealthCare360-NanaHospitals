using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// A child's personal data (DPDP Act 2023 s.9; DPDP Rules 2025, rules 10 to 12 and the Fourth Schedule). A child is
/// anyone under 18.
/// <list type="bullet">
/// <item>Processing for the child's health care needs no further consent: clinical establishments are exempt from s.9(1)
/// and (3) to the extent needed to provide health services and protect the child's health (Fourth Schedule, Part A, item 1).</item>
/// <item>Anything else that needs consent (sharing the record outside the care given, research, photography, a consent
/// manager's artefact) needs the verifiable consent of a parent or lawful guardian first (s.9(1), rule 10):
/// <see cref="IGuardianConsents"/>.</item>
/// <item>No tracking or behavioural monitoring of children, and no targeted advertising at them (s.9(3)): marketing
/// consent is refused for a child, whoever gives it, and campaigns never reach one.</item>
/// </list>
/// </summary>
public static class ChildData
{
    public const int AdultAge = 18;

    public const string MarketingRefusedCode = "consent.child_marketing";
    public const string GuardianNeededCode = "consent.child_guardian_unverified";

    /// <summary>Under 18 on <paramref name="today"/>. An unknown date of birth is not taken to be a child's here.</summary>
    public static bool IsChild(DateOnly? dateOfBirth, DateOnly today) => dateOfBirth is { } born && born > today.AddYears(-AdultAge);

    /// <summary>The last date of birth of someone who is an adult on <paramref name="today"/>.</summary>
    public static DateOnly AdultBornOnOrBefore(DateOnly today) => today.AddYears(-AdultAge);

    /// <summary>Consents that, for a child, need a parent's or guardian's verified consent on record first: not part of health care.</summary>
    public static bool NeedsGuardianConsent(ConsentType type) => type is ConsentType.DataSharing or ConsentType.Research or ConsentType.Photography;

    /// <summary>Consents never recorded as granted for a child (s.9(3)): targeted advertising.</summary>
    public static bool BarredForChildren(ConsentType type) => type is ConsentType.MarketingCommunication;

    /// <summary>
    /// Why granting <paramref name="type"/> for this patient is refused, or null when it may be recorded.
    /// <paramref name="guardianVerified"/> says whether a parent's or guardian's verifiable consent is on record.
    /// </summary>
    public static Error? WhyRefused(ConsentType type, DateOnly? dateOfBirth, DateOnly today, bool guardianVerified)
    {
        if (!IsChild(dateOfBirth, today))
        {
            return null;
        }

        if (BarredForChildren(type))
        {
            return Error.Rule(MarketingRefusedCode,
                "The patient is under 18: the DPDP Act (s.9(3)) does not allow marketing or targeted messages to children, even with a parent's consent.");
        }

        return NeedsGuardianConsent(type) && !guardianVerified
            ? Error.Rule(GuardianNeededCode,
                "The patient is under 18: a parent's or lawful guardian's consent must be verified first, by a code sent to their mobile number or by their identity document.")
            : null;
    }
}

/// <summary>Whether a parent or lawful guardian has given verifiable consent for a child's data (implemented by the Privacy module).</summary>
public interface IGuardianConsents
{
    Task<bool> HasVerifiedAsync(Guid patientId, CancellationToken ct);
}
