using System.Globalization;

namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;

/// <summary>
/// Best-effort modality and body part for a radiology service that has no procedure master yet (pure; unit-tested).
/// This way a newly added service ("RAD-CT-CHEST-HRCT", "USG Doppler legs") still lands on the right worklist
/// instead of being rejected. The procedure master always wins when it exists.
/// </summary>
internal static class ModalityResolver
{
    private static readonly string[] MrTokens = ["MRI", "MR", "MRA", "MRCP", "MRV"];
    private static readonly string[] CtTokens = ["CT", "CECT", "NCCT", "HRCT", "CTA", "CTPA"];
    private static readonly string[] UsTokens = ["USG", "US", "ULTRASOUND", "SONOGRAPHY", "SONO", "DOPPLER", "ECHOGRAPHY"];
    private static readonly string[] MgTokens = ["MAMMO", "MAMMOGRAPHY", "MAMMOGRAM", "MG"];
    private static readonly string[] RfTokens = ["FLUORO", "FLUOROSCOPY", "RF", "BARIUM", "HSG", "IVP", "IVU", "MCU", "RGU"];
    private static readonly string[] DxTokens = ["DX", "DR"];
    private static readonly string[] CodeNoise = ["RAD", "XR", "XRAY", "X", "RAY", "DX", "DR", "CR", "CT", "MR", "MRI", "US", "USG", "MG", "MAMMO", "RF", "FLUORO"];

    public static Modality Resolve(string code, string name)
    {
        var tokens = Tokens(code).Concat(Tokens(name)).ToHashSet(StringComparer.Ordinal);
        if (tokens.Overlaps(MrTokens))
        {
            return Modality.MR;
        }

        if (tokens.Overlaps(CtTokens))
        {
            return Modality.CT;
        }

        if (tokens.Overlaps(UsTokens))
        {
            return Modality.US;
        }

        if (tokens.Overlaps(MgTokens))
        {
            return Modality.MG;
        }

        if (tokens.Overlaps(RfTokens))
        {
            return Modality.RF;
        }

        return tokens.Overlaps(DxTokens) ? Modality.DX : Modality.XR;
    }

    /// <summary>Body part from a catalogue code: RAD-CT-HEAD → "Head", RAD-XR-SPINE-LS → "Spine LS".</summary>
    public static string? BodyPartFromCode(string code)
    {
        var words = Tokens(code).SkipWhile(t => CodeNoise.Contains(t, StringComparer.Ordinal)).ToList();
        if (words.Count == 0)
        {
            return null;
        }

        return string.Join(' ', words.Select(w => w.Length <= 2 ? w : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(w.ToLowerInvariant())));
    }

    private static IEnumerable<string> Tokens(string text) =>
        text.ToUpperInvariant()
            .Split([' ', '-', '_', '/', '(', ')', ',', '.', '&', '+'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
