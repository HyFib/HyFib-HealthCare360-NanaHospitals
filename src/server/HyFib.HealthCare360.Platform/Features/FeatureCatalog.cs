namespace HyFib.HealthCare360.Platform.Features;

/// <summary>Setting keys of the feature switches (see <see cref="FeatureCatalog"/> and docs/modules/features.md).</summary>
public static class FeatureKeys
{
    public const string Prefix = "features.";

    /// <summary>The patient portal: sign-in, registration, every portal API and online booking.</summary>
    public const string PatientPortal = Prefix + "patientPortal";

    /// <summary>Video consultations (telehealth), not remote patient monitoring.</summary>
    public const string Telehealth = Prefix + "telehealth";
}

/// <summary>
/// A part of HC360 an administrator can switch off for the whole hospital (or, through the settings store, for one
/// branch). The words are shown on the Features screen and in the answer a switched-off endpoint gives.
/// </summary>
/// <param name="Key">The setting key, <c>features.&lt;code&gt;</c>.</param>
/// <param name="Name">What the feature is called on screen.</param>
/// <param name="Description">What it does, for the administrator deciding.</param>
/// <param name="WhenOff">What stops, and what keeps working, while it is off.</param>
/// <param name="DefaultEnabled">The value while nobody has set one (on, so an upgrade changes nothing).</param>
/// <param name="OffTitle">The title of the 503 answer while it is off.</param>
/// <param name="OffDetailForStaff">What a member of staff can do about it.</param>
/// <param name="OffDetailForPatients">What a patient can do about it.</param>
public sealed record FeatureDefinition(
    string Key,
    string Name,
    string Description,
    string WhenOff,
    bool DefaultEnabled,
    string OffTitle,
    string OffDetailForStaff,
    string OffDetailForPatients)
{
    /// <summary>The key without its prefix, as the apps name it (<c>telehealth</c>, <c>patientPortal</c>).</summary>
    public string Code => Key[FeatureKeys.Prefix.Length..];
}

/// <summary>Every feature switch. Adding one: a definition here, <c>.RequireFeature(...)</c> on its endpoints, and the
/// <c>feature</c> of its links in the staff app (docs/modules/features.md).</summary>
public static class FeatureCatalog
{
    public static FeatureDefinition PatientPortal { get; } = new(
        FeatureKeys.PatientPortal,
        "Patient portal",
        "Patients and their families sign in on their own phone or computer to book appointments, pay bills, see reports and "
        + "prescriptions, and join video consultations.",
        "Patients cannot sign in, register or book appointments online, and anyone already signed in sees that the portal "
        + "is not available. Report and prescription checks from QR codes, feedback and unsubscribe links, and video "
        + "consultation links sent by SMS keep working.",
        DefaultEnabled: true,
        "The patient portal is turned off",
        "Patients cannot use the portal right now. An administrator can turn it back on under Hospital setup, Features.",
        "The patient portal is not available right now. Please contact the hospital.");

    public static FeatureDefinition Telehealth { get; } = new(
        FeatureKeys.Telehealth,
        "Video consultations",
        "Doctors see patients over video. Patients join from the portal or from a link sent by SMS or WhatsApp.",
        "Video consultations disappear from the menus and nobody can join one. New appointments are in person only. Video "
        + "appointments already booked stay in the diary for the front office to move to an in-person visit or cancel. "
        + "Remote patient monitoring is not affected.",
        DefaultEnabled: true,
        "Video consultations are turned off",
        "Book an in-person visit instead. An administrator can turn video consultations back on under Hospital setup, Features.",
        "Video consultations are not available right now. Please contact the hospital to see the doctor in person.");

    public static IReadOnlyList<FeatureDefinition> All { get; } = [PatientPortal, Telehealth];

    /// <summary>The feature with this setting key, or null.</summary>
    public static FeatureDefinition? Find(string key) => All.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));

    /// <summary>The feature with this setting key; a key outside the catalogue is a programming error.</summary>
    public static FeatureDefinition Get(string key) =>
        Find(key) ?? throw new ArgumentOutOfRangeException(nameof(key), key, "Not a feature in the catalogue (FeatureCatalog.All).");
}
