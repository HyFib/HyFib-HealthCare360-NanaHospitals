using System.Globalization;
using HyFib.HealthCare360.Modules.SupportServices.Features.Waste;

namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Waste;

/// <summary>
/// The statutory forms of the Bio-Medical Waste Management Rules 2016 (as amended), answered from the records: Form I
/// (the report of an accident, Rule 4(o), 5(i) and 15(2)) and Form IV (the annual report, Rule 13, by 30 June). Each
/// line keeps the form's own numbering and wording; what HC360 does not record is answered "To be filled by the
/// occupier", never left to look answered.
/// </summary>
public static class WasteForms
{
    public const string ToFill = "To be filled by the occupier";

    public static IReadOnlyList<FormLine> FormILines(WasteAccident a, TimeSpan offset) =>
    [
        new("1", "Date and time of accident", When(a.OccurredAt, offset)),
        new("2", "Type of accident", Kind(a.Kind)),
        new("3", "Sequence of events leading to accident", a.Sequence),
        new("4", "Has the Authority been informed immediately", a.AuthorityInformedAt is { } told ? $"Yes, on {When(told, offset)}" : "No"),
        new("5", "The type of waste involved in accident",
            a.Category is { } category ? $"{a.WasteInvolved} ({category} category)" : a.WasteInvolved),
        new("6", "Assessment of the effects of the accidents on human health and the environment",
            $"{a.Effects} Persons affected: {a.PersonsAffected.ToString(CultureInfo.InvariantCulture)}."),
        new("7", "Emergency measures taken", a.EmergencyMeasures),
        new("8", "Steps taken to alleviate the effects of accidents", a.StepsToAlleviate ?? ToFill),
        new("9", "Steps taken to prevent the recurrence of such an accident", a.StepsToPrevent ?? ToFill),
        new("10", "Does the facility have an Emergency Control policy? If yes, give details", ToFill),
        new("", "Date of reporting", When(a.ReportedAt, offset)),
        new("", "Signature and designation", a.ReportedByDesignation is { } designation ? $"{a.ReportedByName}, {designation}" : a.ReportedByName),
    ];

    public static IReadOnlyList<FormLine> FormIvLines(
        int year, string name, string? address, string? authorisation, int beds, IReadOnlyList<WasteCategoryTotal> generated, decimal totalKg, int days,
        string? cbwtf, int handovers, IReadOnlyList<WasteCategoryTotal> handedOver, int lateBags, int mismatchedBags, IReadOnlyList<WasteAccident> accidents)
    {
        var lines = new List<FormLine>
        {
            new("1", "Particulars of the occupier: name of the health care facility", name),
            new("1(ii)", "Address of the health care facility", address ?? ToFill),
            new("1(iii)", "Authorisation number and its validity", authorisation ?? ToFill),
            new("2", "Type of health care facility", "Bedded hospital"),
            new("2(i)", "Number of beds", beds.ToString(CultureInfo.InvariantCulture)),
            new("3", "Quantity of waste generated (kg per annum)", $"{Kg(totalKg)} kg in {year} ({Kg(Math.Round(totalKg / days, 3))} kg per day on average)"),
        };
        lines.AddRange(generated.Select(c => new FormLine($"3 ({c.Category})", $"{c.Category} category", $"{Kg(c.Kg)} kg in {c.Bags} bags")));
        lines.Add(new("4", "Details of the common bio-medical waste treatment facility the waste is given to", cbwtf ?? ToFill));
        lines.Add(new("4(i)", "Quantity handed over to the CBWTF (kg per annum, weighed at handover)",
            $"{Kg(handedOver.Sum(c => c.Kg))} kg in {handovers.ToString(CultureInfo.InvariantCulture)} handovers"));
        lines.AddRange(handedOver.Select(c => new FormLine($"4(i) ({c.Category})", $"{c.Category} category handed over", $"{Kg(c.Kg)} kg in {c.Bags} bags")));
        lines.Add(new("4(ii)", "Bags kept beyond 48 hours before treatment", lateBags.ToString(CultureInfo.InvariantCulture)));
        lines.Add(new("4(iii)", "Bags whose weight at handover differed from the weight where they were filled", mismatchedBags.ToString(CultureInfo.InvariantCulture)));
        lines.Add(new("5", "Details of on-site storage, treatment and disposal facilities", ToFill));
        lines.Add(new("6", "Training: number of trainings conducted and staff trained", ToFill));
        lines.Add(new("7", "Details of the accidents occurred during the year (as reported in Form I)",
            accidents.Count == 0
                ? "None"
                : $"{accidents.Count.ToString(CultureInfo.InvariantCulture)} accidents, {accidents.Sum(a => a.PersonsAffected).ToString(CultureInfo.InvariantCulture)} persons affected"));
        lines.AddRange(accidents.OrderBy(a => a.OccurredAt).Select((a, i) => new FormLine($"7({i + 1})",
            $"{Kind(a.Kind)} on {a.OccurredAt.ToOffset(TimeSpan.FromHours(5.5)):dd-MM-yyyy}",
            $"{a.PersonsAffected.ToString(CultureInfo.InvariantCulture)} affected; remedial action: {a.StepsToPrevent ?? a.EmergencyMeasures}")));
        lines.Add(new("8", "Are you meeting the standards of air pollution from the incinerator? (if any)", ToFill));
        lines.Add(new("9", "Liquid waste generated and treatment methods in place", ToFill));
        return lines;
    }

    public static string Kind(WasteAccidentKind kind) => kind switch
    {
        WasteAccidentKind.NeedleStick => "Needle-stick injury",
        WasteAccidentKind.SharpsInjury => "Injury from other sharps",
        WasteAccidentKind.Spill => "Spill of bio-medical waste",
        WasteAccidentKind.Exposure => "Exposure to infectious or hazardous waste",
        WasteAccidentKind.Fire => "Fire",
        WasteAccidentKind.SegregationFailure => "Waste mixed (segregation failure)",
        WasteAccidentKind.TransportAccident => "Accident while carrying waste",
        _ => "Other",
    };

    private static string When(DateTimeOffset at, TimeSpan offset) => at.ToOffset(offset).ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string Kg(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
