namespace HyFib.HealthCare360.SharedKernel;

/// <summary>
/// The GST identification number: two digits of the state, the holder's PAN, the entity number, "Z", and a check
/// character (mod 36, as GSTN computes it), so a mistyped number is caught before it reaches an invoice.
/// </summary>
public static class Gstin
{
    private const string Characters = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>The GST state codes (the first two digits of a GSTIN), as the place of supply is written on an invoice.</summary>
    public static IReadOnlyDictionary<string, string> States { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["01"] = "Jammu and Kashmir",
        ["02"] = "Himachal Pradesh",
        ["03"] = "Punjab",
        ["04"] = "Chandigarh",
        ["05"] = "Uttarakhand",
        ["06"] = "Haryana",
        ["07"] = "Delhi",
        ["08"] = "Rajasthan",
        ["09"] = "Uttar Pradesh",
        ["10"] = "Bihar",
        ["11"] = "Sikkim",
        ["12"] = "Arunachal Pradesh",
        ["13"] = "Nagaland",
        ["14"] = "Manipur",
        ["15"] = "Mizoram",
        ["16"] = "Tripura",
        ["17"] = "Meghalaya",
        ["18"] = "Assam",
        ["19"] = "West Bengal",
        ["20"] = "Jharkhand",
        ["21"] = "Odisha",
        ["22"] = "Chhattisgarh",
        ["23"] = "Madhya Pradesh",
        ["24"] = "Gujarat",
        ["26"] = "Dadra and Nagar Haveli and Daman and Diu",
        ["27"] = "Maharashtra",
        ["29"] = "Karnataka",
        ["30"] = "Goa",
        ["31"] = "Lakshadweep",
        ["32"] = "Kerala",
        ["33"] = "Tamil Nadu",
        ["34"] = "Puducherry",
        ["35"] = "Andaman and Nicobar Islands",
        ["36"] = "Telangana",
        ["37"] = "Andhra Pradesh",
        ["38"] = "Ladakh",
        ["97"] = "Other territory",
    };

    /// <summary>Upper-cased and without spaces, or null for nothing.</summary>
    public static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : string.Concat(value.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

    /// <summary>A GSTIN as GSTN issues it, its check character included: what a new buyer's number must be.</summary>
    public static bool IsValid(string? value) =>
        IsWellFormed(value) && Normalize(value) is { } gstin && CheckCharacter(gstin[..14]) == gstin[14];

    /// <summary>
    /// The shape of a GSTIN (state, PAN, entity, "Z", one more character), without the check character: numbers saved
    /// before it was checked keep working for their state.
    /// </summary>
    public static bool IsWellFormed(string? value)
    {
        var gstin = Normalize(value);
        if (gstin is null || gstin.Length != 15 || !States.ContainsKey(gstin[..2]) || gstin[13] != 'Z')
        {
            return false;
        }

        // PAN: five letters, four digits, a letter.
        var pan = gstin[2..12];
        return pan[..5].All(char.IsAsciiLetterUpper) && pan[5..9].All(char.IsAsciiDigit) && char.IsAsciiLetterUpper(pan[9])
               && gstin.All(c => Characters.Contains(c, StringComparison.Ordinal));
    }

    /// <summary>The state a GSTIN is registered in, as its two-digit code; null when it is not a GSTIN at all.</summary>
    public static string? StateCode(string? value) => IsWellFormed(value) ? Normalize(value)![..2] : null;

    /// <summary>The check character GSTN appends to the first 14 characters.</summary>
    public static char CheckCharacter(string first14)
    {
        var sum = 0;
        for (var i = 0; i < 14; i++)
        {
            var product = Characters.IndexOf(first14[i], StringComparison.Ordinal) * (i % 2 == 0 ? 1 : 2);
            sum += (product / 36) + (product % 36);
        }

        return Characters[(36 - (sum % 36)) % 36];
    }
}
