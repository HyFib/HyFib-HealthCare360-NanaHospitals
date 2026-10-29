using HyFib.HealthCare360.Platform.Localization;

namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>A service of the catalogue as the rate list prices it (free of EF types so the rules stay unit-testable).</summary>
public sealed record RateListItem(
    Guid Id, string Code, string Name, string? LocalName, ServiceCategory Category, decimal BasePrice, decimal TaxRate,
    bool IsDiscountAllowed, bool IsPriceEditable);

/// <summary>The plan that prices a self-paying patient: its concession and its bed-class multipliers.</summary>
public sealed record RateListPlan(Guid Id, string Name, decimal ConcessionPercent, IReadOnlyDictionary<Guid, decimal> BedMultipliers)
{
    public decimal MultiplierFor(Guid bedServiceItemId) =>
        BedMultipliers.TryGetValue(bedServiceItemId, out var multiplier) && multiplier > 0 ? multiplier : 1m;
}

/// <summary>
/// A doctor who consults at the facility: their own consultation fee (0 when they have none), the days after a paid
/// consultation in which a follow-up with them is free, and whether they consult by video.
/// </summary>
public sealed record RateListDoctor(string Name, string? Department, decimal Fee, int FreeFollowUpDays, bool ByVideo = false);

/// <summary>A fixed-price package a self-paying patient can be billed (<see cref="Package"/>).</summary>
public sealed record RateListPackage(string Code, string Name, Guid ServiceItemId, decimal Price, int? LengthOfStayDays, PackageUse UsedFor);

/// <summary>What a rate list is built from, as on one day.</summary>
public sealed record RateListInput
{
    public required DateOnly AsOn { get; init; }

    /// <summary>The services in use.</summary>
    public required IReadOnlyList<RateListItem> Items { get; init; }

    /// <summary>The plan that prices a self-paying patient, or null when there is none and base prices apply.</summary>
    public RateListPlan? Plan { get; init; }

    /// <summary>The plan's prices of each service, with their validity.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<PlanPrice>> PlanPrices { get; init; } = new Dictionary<Guid, IReadOnlyList<PlanPrice>>();

    public IReadOnlyList<RateListDoctor> Doctors { get; init; } = [];

    public IReadOnlyList<RoomClass> Rooms { get; init; } = [];

    public IReadOnlyList<RateListPackage> Packages { get; init; } = [];

    /// <summary>Whether a patient's first visit is charged the registration fee (REG-NEW).</summary>
    public bool RegistrationFee { get; init; } = true;
}

/// <summary>
/// One line of the rate list: what a self-paying patient is charged for one of the service before GST, the GST rate, and
/// what they pay with it. <see cref="MayVary"/> marks a service whose price the counter may change.
/// </summary>
public sealed record RateLine(string Code, string Name, string? LocalName, decimal Rate, decimal GstRate, decimal Payable, bool MayVary = false);

/// <summary>The services of one category.</summary>
public sealed record RateSection(ServiceCategory Category, IReadOnlyList<RateLine> Lines);

/// <summary>
/// A class of bed: a day of it with its GST, and what each day's nursing and each consultant's visit cost in it. In-patient
/// procedures, surgery, nursing and doctor visits cost <see cref="Multiplier"/> times their listed rate in this class.
/// </summary>
public sealed record RoomRateLine(
    RateLine Day, bool IntensiveCare, IReadOnlyList<string> Wards, RateLine? Nursing, RateLine? Visit, decimal Multiplier);

/// <summary>
/// What a consultation with a doctor costs in person and, when they consult by video, by video. A follow-up with them
/// within <see cref="FreeFollowUpDays"/> of a paid consultation is free (none when 0).
/// </summary>
public sealed record DoctorFeeLine(string Name, string? Department, RateLine Fee, RateLine? Video, int FreeFollowUpDays);

/// <summary>A fixed-price package, its length of stay, and the bills it may price.</summary>
public sealed record PackageLine(RateLine Price, int? LengthOfStayDays, PackageUse UsedFor);

/// <summary>
/// The hospital's rates for a self-paying patient as on one day (Clinical Establishments Rules 2012, r.9(i): the rates of
/// each service and facility, displayed in English and the local language). Insurance, corporate and scheme patients are
/// charged at their plan's rates instead.
/// </summary>
public sealed record RateList(
    DateOnly AsOn,
    string? PlanName,
    decimal ConcessionPercent,
    RateLine? Registration,
    IReadOnlyList<DoctorFeeLine> Doctors,
    IReadOnlyList<RoomRateLine> Rooms,
    IReadOnlyList<RateSection> Sections,
    IReadOnlyList<PackageLine> Packages);

/// <summary>
/// Builds the rate list with the bill's own rules, so what the list says is what a bill charges: the plan price in effect
/// that day or the base price (<see cref="PriceResolver"/>), the plan's concession on a discountable service, the room's
/// GST by <see cref="RoomRentGst"/>, and the line arithmetic of <see cref="ChargeAmounts"/>. A doctor's own fee and a
/// package's price take the place of the service's price, as they do on a bill.
/// </summary>
public static class RateListBuilder
{
    /// <summary>The service a consultation in person is charged as (VisitChargesHandler).</summary>
    public const string ConsultationCode = "CONS-NEW";

    /// <summary>The service a consultation by video is charged as.</summary>
    public const string VideoConsultationCode = "CONS-TELE";

    /// <summary>
    /// The service a follow-up is charged as. A follow-up within the doctor's days is free, and after them it is a new
    /// consultation, so the list does not show its price.
    /// </summary>
    public const string FollowUpCode = "CONS-FU";

    /// <summary>The service a patient's first visit is charged as, unless the facility charges no registration fee.</summary>
    public const string RegistrationCode = "REG-NEW";

    /// <summary>Bed charges are priced with their rooms, and a package's own service by its package.</summary>
    private static readonly HashSet<ServiceCategory> PricedElsewhere = [ServiceCategory.BedCharge, ServiceCategory.Package];

    private static readonly HashSet<string> PricedElsewhereCodes =
        new([ConsultationCode, VideoConsultationCode, FollowUpCode, RegistrationCode], StringComparer.OrdinalIgnoreCase);

    public static RateList Build(RateListInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var items = input.Items.ToDictionary(i => i.Id);
        var byCode = input.Items.GroupBy(i => i.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        RateLine Line(RateListItem item, decimal multiplier = 1m, decimal? unitPriceOverride = null, RoomCharge? room = null) =>
            Price(item, input.PlanPrices.GetValueOrDefault(item.Id) ?? [], input.AsOn, input.Plan?.ConcessionPercent ?? 0m, multiplier, unitPriceOverride, room);

        // A doctor's own fee takes the place of the consultation's price, as on a bill (PriceResolver.ConsultationOverride).
        var consultation = byCode.GetValueOrDefault(ConsultationCode);
        var video = byCode.GetValueOrDefault(VideoConsultationCode);
        RateLine Consultation(RateListItem service, RateListDoctor doctor) =>
            Line(service, unitPriceOverride: PriceResolver.ConsultationOverride(false, doctor.Fee)) with { Name = doctor.Name, LocalName = null };
        List<DoctorFeeLine> doctors = consultation is null
            ? []
            :
            [
                .. input.Doctors
                    .OrderBy(d => d.Department ?? string.Empty, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(d => new DoctorFeeLine(
                        d.Name, d.Department, Consultation(consultation, d), d.ByVideo && video is not null ? Consultation(video, d) : null,
                        Math.Max(d.FreeFollowUpDays, 0))),
            ];

        // The nursing and visit charges of a room are listed with it; in-patient services cost its multiple of their rate.
        List<RoomRateLine> rooms =
        [
            .. input.Rooms
                .Where(r => items.ContainsKey(r.BedServiceItemId))
                .Select(r =>
                {
                    var multiplier = input.Plan?.MultiplierFor(r.BedServiceItemId) ?? 1m;
                    RateLine? Daily(Guid? id) => id is { } key && items.TryGetValue(key, out var item) ? Line(item, multiplier) : null;
                    return new RoomRateLine(
                        Line(items[r.BedServiceItemId], room: r.IsIntensiveCare ? RoomCharge.IntensiveCare : RoomCharge.Room),
                        r.IsIntensiveCare, r.Wards, Daily(r.NursingServiceItemId), Daily(r.VisitServiceItemId), multiplier);
                }),
        ];
        var withRooms = input.Rooms.SelectMany(r => new[] { r.NursingServiceItemId, r.VisitServiceItemId }).OfType<Guid>().ToHashSet();

        List<RateSection> sections =
        [
            .. input.Items
                .Where(i => !PricedElsewhere.Contains(i.Category) && !PricedElsewhereCodes.Contains(i.Code) && !(withRooms.Contains(i.Id) && rooms.Count > 0))
                .GroupBy(i => i.Category)
                .OrderBy(g => g.Key)
                .Select(g => new RateSection(g.Key, [.. g.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Select(i => Line(i))])),
        ];

        List<PackageLine> packages =
        [
            .. input.Packages
                .Where(p => items.ContainsKey(p.ServiceItemId))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => new PackageLine(
                    Line(items[p.ServiceItemId], unitPriceOverride: p.Price) with { Code = p.Code, Name = p.Name, LocalName = null },
                    p.LengthOfStayDays, p.UsedFor)),
        ];

        var registration = input.RegistrationFee && byCode.GetValueOrDefault(RegistrationCode) is { } fee ? Line(fee) : null;
        return new RateList(input.AsOn, input.Plan?.Name, input.Plan?.ConcessionPercent ?? 0m, registration, doctors, rooms, sections, packages);
    }

    /// <summary>
    /// One of the service, priced as <c>Charge.ApplyPrice</c> prices a charge: the resolved price, the concession when the
    /// service allows a discount, and GST on what is left (a room's by <see cref="RoomRentGst"/>, on its price after the
    /// concession).
    /// </summary>
    public static RateLine Price(
        RateListItem item, IEnumerable<PlanPrice> planPrices, DateOnly asOn, decimal concessionPercent, decimal multiplier = 1m,
        decimal? unitPriceOverride = null, RoomCharge? room = null)
    {
        var quote = PriceResolver.Resolve(new PricingItem(item.Id, item.Category, item.BasePrice), planPrices, asOn, multiplier, unitPriceOverride: unitPriceOverride);
        var discount = item.IsDiscountAllowed ? Math.Clamp(concessionPercent, 0m, 100m) : 0m;
        var taxRate = room is { } kind
            ? RoomRentGst.RateFor(kind, quote.UnitPrice - Money.PercentOf(quote.UnitPrice, discount))
            : Math.Max(item.TaxRate, 0m);
        var amounts = ChargeAmounts.Calculate(1m, quote.UnitPrice, discount, taxRate);
        return new RateLine(item.Code, item.Name, item.LocalName, amounts.Taxable, taxRate, amounts.Net, item.IsPriceEditable && unitPriceOverride is null);
    }
}

/// <summary>
/// The language a facility's rate list is written in beside English (Clinical Establishments Rules 2012, r.9(i): the
/// local language as well as English): the language of the facility's state, when HC360 writes it. Tamil in Tamil Nadu
/// and Puducherry; Hindi in the states and union territory whose official language it is. Null elsewhere: the list's
/// own words are then in English only, and each service's local name is the hospital's to enter.
/// </summary>
public static class RateListLanguage
{
    // GST state codes (Gstin.States).
    private static readonly Dictionary<string, string> ByState = new(StringComparer.Ordinal)
    {
        ["33"] = PatientLanguages.Tamil, // Tamil Nadu
        ["34"] = PatientLanguages.Tamil, // Puducherry
        ["02"] = PatientLanguages.Hindi, // Himachal Pradesh
        ["05"] = PatientLanguages.Hindi, // Uttarakhand
        ["06"] = PatientLanguages.Hindi, // Haryana
        ["07"] = PatientLanguages.Hindi, // Delhi
        ["08"] = PatientLanguages.Hindi, // Rajasthan
        ["09"] = PatientLanguages.Hindi, // Uttar Pradesh
        ["10"] = PatientLanguages.Hindi, // Bihar
        ["20"] = PatientLanguages.Hindi, // Jharkhand
        ["22"] = PatientLanguages.Hindi, // Chhattisgarh
        ["23"] = PatientLanguages.Hindi, // Madhya Pradesh
    };

    /// <summary>The language of the facility's state: the state of its address, or failing that of its GSTIN.</summary>
    public static string? For(string? state, string? gstin)
    {
        var named = state?.Trim();
        var code = Gstin.States.FirstOrDefault(s => string.Equals(s.Value, named, StringComparison.OrdinalIgnoreCase)).Key
                   ?? (Gstin.Normalize(gstin) is { Length: >= 2 } number && Gstin.States.ContainsKey(number[..2]) ? number[..2] : null);
        return code is not null && ByState.TryGetValue(code, out var language) ? language : null;
    }
}
