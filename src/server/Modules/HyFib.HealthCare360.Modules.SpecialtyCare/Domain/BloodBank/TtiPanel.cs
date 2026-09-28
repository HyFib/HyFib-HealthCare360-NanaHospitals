namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

/// <summary>
/// Mandatory transfusion-transmitted infection screening in India (Drugs &amp; Cosmetics Rules): HIV 1/2, HBsAg,
/// HCV, syphilis and malaria. Tested once per donation and copied onto every component so each unit's record
/// (and label) is self-contained.
/// </summary>
public sealed record TtiPanel
{
    public TtiResult Hiv { get; init; }
    public TtiResult HbsAg { get; init; }
    public TtiResult Hcv { get; init; }
    public TtiResult Syphilis { get; init; }
    public TtiResult Malaria { get; init; }

    private IEnumerable<(string Marker, TtiResult Result)> Markers =>
    [
        ("HIV", Hiv), ("HBsAg", HbsAg), ("HCV", Hcv), ("Syphilis", Syphilis), ("Malaria", Malaria),
    ];

    public bool IsComplete => Markers.All(m => m.Result != TtiResult.Pending);

    /// <summary>The only state in which a unit may leave quarantine.</summary>
    public bool AllNonReactive => Markers.All(m => m.Result == TtiResult.NonReactive);

    public bool AnyReactive => Markers.Any(m => m.Result == TtiResult.Reactive);

    public IReadOnlyList<string> ReactiveMarkers => Markers.Where(m => m.Result == TtiResult.Reactive).Select(m => m.Marker).ToList();
}
