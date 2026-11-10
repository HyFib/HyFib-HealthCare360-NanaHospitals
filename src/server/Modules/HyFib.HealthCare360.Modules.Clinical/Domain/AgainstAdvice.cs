namespace HyFib.HealthCare360.Modules.Clinical.Domain;

/// <summary>Who signed the form for a patient leaving against medical advice (LAMA, DAMA), or that nobody would.</summary>
public enum AgainstAdviceSignatory
{
    /// <summary>The patient signed it.</summary>
    Patient = 0,

    /// <summary>A relative signed it, named with their relationship to the patient.</summary>
    Relative,

    /// <summary>The patient (and family) refused to sign: the doctor and the witness record it.</summary>
    RefusedToSign,
}

/// <summary>
/// A patient leaving against medical advice, as recorded (maker-checker, slice 8): who signed the form (or that nobody
/// would), the doctor who explained the risks, from their own sign-in or their own confirmation at this screen, and a
/// witness, another member of staff who confirmed with their own user name and password (ADR-014), never the doctor. A
/// scanned copy of the signed form, when one is attached, is a file of the document store.
/// </summary>
public sealed record AgainstAdviceRecord(
    AgainstAdviceSignatory SignedBy,
    string? SignatoryName,
    string? Relationship,
    Guid DoctorUserId,
    string DoctorName,
    Guid WitnessUserId,
    string WitnessName,
    Guid? SignedFormFileId)
{
    /// <summary>"Signed by Ravi Kumar (son); risks explained by Dr Rao; witnessed by Sister Mary".</summary>
    public string Words => $"{AgainstAdviceRules.Signed(SignedBy, SignatoryName, Relationship)}; risks explained by {DoctorName}; witnessed by {WitnessName}";
}

/// <summary>What a declaration of leaving against medical advice must say.</summary>
public static class AgainstAdviceRules
{
    /// <summary>
    /// The signatory is named when a relative signs, with the relationship; the patient signing, or refusing to, needs no
    /// name. Null when it says enough.
    /// </summary>
    public static Error? Check(AgainstAdviceSignatory signedBy, string? signatoryName, string? relationship) =>
        signedBy == AgainstAdviceSignatory.Relative && (string.IsNullOrWhiteSpace(signatoryName) || string.IsNullOrWhiteSpace(relationship))
            ? Error.Field("againstAdvice.signatoryName", "Name the relative who signed, and their relationship to the patient.")
            : null;

    /// <summary>"Signed by the patient", "Signed by Ravi Kumar (son)", "Refused to sign".</summary>
    public static string Signed(AgainstAdviceSignatory signedBy, string? signatoryName, string? relationship) => signedBy switch
    {
        AgainstAdviceSignatory.Patient => "Signed by the patient",
        AgainstAdviceSignatory.Relative => $"Signed by {signatoryName?.Trim()} ({relationship?.Trim()})",
        _ => "Refused to sign",
    };
}
