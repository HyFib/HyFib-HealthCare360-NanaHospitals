using System.Text.Json;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Localization;

/// <summary>A language of India a hospital may display in: its ISO 639 code ("ta", "kok") and its name in English.</summary>
public sealed record IndianLanguage(string Code, string Name);

/// <summary>
/// A facility's local language (<see cref="LocalLanguages.OfFacility"/>), and what could not be read on the way to it.
/// </summary>
/// <param name="Language">The local language, or null when it is not known.</param>
/// <param name="UnknownSetting">The hospital's setting (<see cref="LocalLanguages.Setting"/>) when it names no language.</param>
/// <param name="UnknownState">The address's state when it names no state, and the GSTIN gives none either.</param>
public sealed record FacilityLanguage(IndianLanguage? Language, string? UnknownSetting = null, string? UnknownState = null);

/// <summary>
/// The local language of a facility, for what the law has a hospital display "in the local as well as in English
/// language" (Clinical Establishments (Central Government) Rules 2012, r.9(i): the rates of each service). By default it
/// is the official language of the facility's state or union territory; none where English is its only official language
/// or several stand equal (Jammu and Kashmir, Chandigarh, Sikkim, Arunachal Pradesh, Nagaland, Meghalaya, Dadra and Nagar
/// Haveli and Daman and Diu, Ladakh). In Puducherry it is the district's, told by the PIN code: Tamil, but Malayalam in
/// Mahe and Telugu in Yanam. The hospital may set it, for one facility or for all (<see cref="Setting"/>).
/// </summary>
public static class LocalLanguages
{
    /// <summary>A facility's local language, as a code or a name ("kn", "Kannada"), in place of its state's.</summary>
    public const string Setting = "billing.rateList.language";

    /// <summary>
    /// The 22 languages of the Constitution's Eighth Schedule, and the official languages of Mizoram, Tripura and
    /// Meghalaya beside them.
    /// </summary>
    public static IReadOnlyList<IndianLanguage> All { get; } =
    [
        new("as", "Assamese"), new("bn", "Bengali"), new("brx", "Bodo"), new("doi", "Dogri"), new("grt", "Garo"),
        new("gu", "Gujarati"), new("hi", "Hindi"), new("kn", "Kannada"), new("ks", "Kashmiri"), new("kha", "Khasi"),
        new("trp", "Kokborok"), new("kok", "Konkani"), new("mai", "Maithili"), new("ml", "Malayalam"), new("mni", "Manipuri"),
        new("mr", "Marathi"), new("lus", "Mizo"), new("ne", "Nepali"), new("or", "Odia"), new("pa", "Punjabi"),
        new("sa", "Sanskrit"), new("sat", "Santali"), new("sd", "Sindhi"), new("ta", "Tamil"), new("te", "Telugu"),
        new("ur", "Urdu"),
    ];

    // By GST state code (Gstin.States).
    private static readonly Dictionary<string, string> ByState = new(StringComparer.Ordinal)
    {
        ["02"] = "hi", // Himachal Pradesh
        ["03"] = "pa", // Punjab
        ["05"] = "hi", // Uttarakhand
        ["06"] = "hi", // Haryana
        ["07"] = "hi", // Delhi
        ["08"] = "hi", // Rajasthan
        ["09"] = "hi", // Uttar Pradesh
        ["10"] = "hi", // Bihar
        ["14"] = "mni", // Manipur
        ["15"] = "lus", // Mizoram
        ["16"] = "bn", // Tripura
        ["18"] = "as", // Assam
        ["19"] = "bn", // West Bengal
        ["20"] = "hi", // Jharkhand
        ["21"] = "or", // Odisha
        ["22"] = "hi", // Chhattisgarh
        ["23"] = "hi", // Madhya Pradesh
        ["24"] = "gu", // Gujarat
        ["27"] = "mr", // Maharashtra
        ["29"] = "kn", // Karnataka
        ["30"] = "kok", // Goa
        ["31"] = "ml", // Lakshadweep
        ["32"] = "ml", // Kerala
        ["33"] = "ta", // Tamil Nadu
        ["34"] = "ta", // Puducherry, by district (OfState)
        ["35"] = "hi", // Andaman and Nicobar Islands
        ["36"] = "te", // Telangana
        ["37"] = "te", // Andhra Pradesh
    };

    /// <summary>Whether <paramref name="code"/> is the code of one of <see cref="All"/>, as written there ("ta", not "TA").</summary>
    public static bool IsCode(string? code) => All.Any(l => l.Code == code);

    /// <summary>The language a code or an English name names ("ta", "TA", "ta-IN", "Tamil"), or null.</summary>
    public static IndianLanguage? Find(string? codeOrName)
    {
        var text = codeOrName?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var code = text.Split('-', '_')[0];
        return All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase) || string.Equals(l.Name, text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The local language of the state with GST code <paramref name="stateCode"/>, or null where it has none of its own.
    /// In Puducherry the district's: Malayalam in Mahe (PIN codes 67…) and Telugu in Yanam (53…).
    /// </summary>
    public static IndianLanguage? OfState(string? stateCode, string? pinCode = null)
    {
        if (stateCode == "34" && pinCode?.Trim() is { Length: >= 2 } pin)
        {
            return Find(pin[..2] switch
            {
                "67" => "ml",
                "53" => "te",
                _ => "ta",
            });
        }

        return stateCode is not null && ByState.TryGetValue(stateCode, out var code) ? Find(code) : null;
    }

    /// <summary>
    /// A facility's local language: the one the hospital set (<paramref name="setting"/>), else that of the state its
    /// address names (<see cref="IndianStates"/>), else that of its GSTIN's state.
    /// </summary>
    public static FacilityLanguage OfFacility(string? setting, Address? address, string? gstin)
    {
        var chosen = string.IsNullOrWhiteSpace(setting) ? null : Find(setting);
        var unknownSetting = chosen is null && !string.IsNullOrWhiteSpace(setting) ? setting.Trim() : null;
        if (chosen is not null)
        {
            return new(chosen);
        }

        var state = address?.State?.Trim();
        if (IndianStates.Code(state) is { } named)
        {
            return new(OfState(named, address?.PinCode), unknownSetting);
        }

        return Gstin.StateCode(gstin) is { } registered
            ? new(OfState(registered, address?.PinCode), unknownSetting)
            : new(null, unknownSetting, string.IsNullOrEmpty(state) ? null : state);
    }

    /// <summary><see cref="OfFacility"/> with the hospital's setting for the facility.</summary>
    public static async Task<FacilityLanguage> OfFacilityAsync(
        ISettingsService settings, Guid facilityId, Address? address, string? gstin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var value = await settings.GetAsync<JsonElement?>(Setting, facilityId, cancellationToken);
        var setting = value switch
        {
            { ValueKind: JsonValueKind.String } text => text.GetString(),
            null or { ValueKind: JsonValueKind.Null } => null,
            { } other => other.GetRawText(),
        };
        return OfFacility(setting, address, gstin);
    }
}
