using System.Globalization;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Privacy.Domain;

/// <summary>
/// The details a principal can ask to have corrected (DPDP Act s12). Registration details are changed by staff through
/// the patient record, with its duplicate checks and audit; something in the health record is answered by the clinical
/// team, who add a correction rather than rewrite the record.
/// </summary>
public static class CorrectableFields
{
    public const string Name = "name";
    public const string DateOfBirth = "dateOfBirth";
    public const string Gender = "gender";
    public const string Mobile = "mobile";
    public const string AlternatePhone = "alternatePhone";
    public const string Email = "email";
    public const string Address = "address";
    public const string EmergencyContact = "emergencyContact";
    public const string Guardian = "guardian";
    public const string Language = "language";

    /// <summary>Something in the health record: a diagnosis, a note, a report.</summary>
    public const string HealthRecord = "healthRecord";

    public const string Other = "other";

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        [Name] = "Name",
        [DateOfBirth] = "Date of birth",
        [Gender] = "Gender",
        [Mobile] = "Mobile number",
        [AlternatePhone] = "Other phone number",
        [Email] = "E-mail address",
        [Address] = "Address",
        [EmergencyContact] = "Emergency contact",
        [Guardian] = "Guardian",
        [Language] = "Preferred language",
        [HealthRecord] = "Something in the health record",
        [Other] = "Something else",
    };

    public static IReadOnlyCollection<string> All => Labels.Keys;

    public static bool IsKnown(string? field) => field is not null && Labels.ContainsKey(field);

    public static string Label(string field) => Labels.GetValueOrDefault(field, field);

    /// <summary>The value on record for <paramref name="field"/>, shown to staff next to the requested value.</summary>
    public static string? CurrentValue(Patient patient, string field) => field switch
    {
        Name => patient.FullName,
        DateOfBirth => patient.DateOfBirth?.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
        Gender => patient.Gender.ToString(),
        Mobile => patient.Phone is null ? null : Phone.Mask(patient.Phone),
        AlternatePhone => patient.AlternatePhone is null ? null : Phone.Mask(patient.AlternatePhone),
        Email => patient.Email,
        Address => patient.Address.ToString() is { Length: > 0 } address ? address : null,
        EmergencyContact => Join(patient.EmergencyContactName, patient.EmergencyContactRelation,
            patient.EmergencyContactPhone is null ? null : Phone.Mask(patient.EmergencyContactPhone)),
        Guardian => Join(patient.GuardianName, patient.GuardianRelation?.ToString()),
        Language => patient.PreferredLanguage,
        _ => null,
    };

    private static string? Join(params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return present.Count == 0 ? null : string.Join(", ", present);
    }
}
