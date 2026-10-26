using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Modules.Billing.Domain;

public enum EInvoiceStatus
{
    Registered = 0,
    Cancelled,
}

/// <summary>Why an IRN was cancelled on the portal, with the portal's own codes.</summary>
public enum EInvoiceCancelReason
{
    Duplicate = 1,
    DataEntryMistake = 2,
    OrderCancelled = 3,
    Other = 4,
}

/// <summary>
/// The IRN the Invoice Registration Portal gave a B2B tax invoice (GST rule 48(4)), with its acknowledgement and the
/// signed QR code the invoice must print. One live IRN per invoice; an IRN is cancelled on the portal within 24 hours of
/// its generation, and after that only a credit note undoes the invoice.
/// </summary>
public sealed class EInvoice : AuditableEntity, IFacilityScoped
{
    public static readonly TimeSpan CancelWindow = TimeSpan.FromHours(24);

    public Guid FacilityId { get; set; }

    /// <summary>The <see cref="IEInvoiceSource.Source"/> the invoice comes from: <c>bill</c> or <c>pharmacy</c>.</summary>
    public string Source { get; set; } = string.Empty;

    public Guid SourceId { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public DateOnly DocumentDate { get; set; }
    public string BuyerGstin { get; set; } = string.Empty;
    public decimal InvoiceValue { get; set; }

    /// <summary>The invoice reference number: 64 hexadecimal characters.</summary>
    public string Irn { get; set; } = string.Empty;

    public string AckNo { get; set; } = string.Empty;

    /// <summary>When the portal generated the IRN (its <c>IrnDt</c>), from which the 24 hours to cancel it run.</summary>
    public DateTimeOffset AckDate { get; set; }

    /// <summary>The portal's signed QR code (a JWT), printed on the invoice as a QR code.</summary>
    public string SignedQrCode { get; set; } = string.Empty;

    public string RecordedByName { get; set; } = string.Empty;
    public EInvoiceStatus Status { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public EInvoiceCancelReason? CancelReason { get; private set; }
    public string? CancelRemark { get; private set; }

    public bool CanCancel(DateTimeOffset now) => Status == EInvoiceStatus.Registered && now - AckDate <= CancelWindow;

    /// <summary>Records that the IRN was cancelled on the portal, which allows it only within 24 hours.</summary>
    public Result Cancel(EInvoiceCancelReason reason, string remark, DateTimeOffset now)
    {
        if (Status == EInvoiceStatus.Cancelled)
        {
            return Error.Rule("einvoice.cancelled", "This IRN is already cancelled.");
        }

        if (!CanCancel(now))
        {
            return Error.Rule("einvoice.cancel_window",
                "The portal cancels an IRN only within 24 hours of its generation. Issue a credit note against the invoice instead.");
        }

        Status = EInvoiceStatus.Cancelled;
        CancelledAt = now;
        CancelReason = reason;
        CancelRemark = remark.Trim();
        return Result.Success();
    }
}

/// <summary>The supplier on an e-invoice: the facility, from its master.</summary>
public sealed record EInvoiceSeller(string? Gstin, string LegalName, Address Address, string? Phone, string? Email);

/// <summary>
/// The e-invoice JSON in the portal's schema (INV-01, version 1.1) for a B2B tax invoice, and the checks the portal
/// applies before it gives an IRN, so a refusal is seen here with its reason instead of on the portal.
/// </summary>
public static partial class InvoiceRegistrationPayload
{
    /// <summary>The GST rates the portal accepts.</summary>
    public static readonly IReadOnlySet<decimal> Rates = new HashSet<decimal> { 0m, 0.1m, 0.25m, 1m, 1.5m, 3m, 5m, 6m, 7.5m, 12m, 18m, 28m, 40m };

    /// <summary>The payload, or the problems that stop the portal from registering it (and then no payload).</summary>
    public static (JsonObject? Payload, IReadOnlyList<string> Problems) Build(EInvoiceSeller seller, EInvoiceDocument document)
    {
        var problems = new List<string>();
        if (document.Problem is { } problem)
        {
            problems.Add(problem);
        }

        if (document.IsCancelled)
        {
            problems.Add("The invoice is cancelled.");
        }

        if (!DocumentNumber().IsMatch(document.Number))
        {
            problems.Add($"The portal takes invoice numbers of up to 16 letters, digits, / and -, not starting with 0; {document.Number} is not one. Shorten the number format under Hospital setup.");
        }

        var sellerState = Gstin.StateCode(seller.Gstin);
        if (sellerState is null)
        {
            problems.Add("Enter the hospital's GSTIN under Hospital setup.");
        }

        var sellerLegalName = Required(seller.LegalName, 3, 100, "the hospital's legal name", problems);
        var sellerAddress = Required(seller.Address.Line1, 1, 100, "the first line of the hospital's address", problems);
        var sellerLocation = Required(seller.Address.City, 3, 50, "the hospital's city", problems);
        var sellerPin = Pin(seller.Address.PinCode, sellerState, "the hospital's", problems);

        var buyerState = Gstin.StateCode(document.Buyer.Gstin);
        if (buyerState is null)
        {
            problems.Add($"{document.Buyer.Gstin} is not a GSTIN.");
        }
        else if (Gstin.Normalize(document.Buyer.Gstin) == Gstin.Normalize(seller.Gstin))
        {
            problems.Add("The buyer's GSTIN is the hospital's own.");
        }

        var buyerLegalName = Required(document.Buyer.LegalName, 3, 100, "the buyer's legal name", problems);
        var buyerAddress = BuyerAddress.Parse(document.Buyer.Address);
        if (buyerAddress.Problem is { } addressProblem)
        {
            problems.Add(addressProblem);
        }

        var buyerPin = buyerAddress.Pin is null ? null : Pin(buyerAddress.Pin, buyerState, "the buyer's", problems);
        if (!Gstin.States.ContainsKey(document.PlaceOfSupply))
        {
            problems.Add($"{document.PlaceOfSupply} is not a GST state code for the place of supply.");
        }

        var interState = sellerState is not null && document.PlaceOfSupply != sellerState;
        if (document.Items.Count == 0)
        {
            problems.Add("The invoice has no lines.");
        }

        if (document.Items.Count > 1000)
        {
            problems.Add("The portal takes at most 1000 lines on one invoice.");
        }

        var items = new JsonArray();
        foreach (var (item, index) in document.Items.Select((item, index) => (item, index)))
        {
            problems.AddRange(Check(item, index + 1, interState));
            var line = new JsonObject
            {
                ["SlNo"] = (index + 1).ToString(CultureInfo.InvariantCulture),
                ["PrdDesc"] = Truncate(item.Description.Trim(), 300),
                ["IsServc"] = item.IsService ? "Y" : "N",
                ["HsnCd"] = item.HsnCode,
                ["Qty"] = Math.Round(item.Quantity, 3),
                ["UnitPrice"] = Math.Round(item.UnitPrice, 3),
                ["TotAmt"] = Money.Round(item.TotalAmount),
                ["Discount"] = Money.Round(item.Discount),
                ["AssAmt"] = Money.Round(item.AssessableValue),
                ["GstRt"] = item.GstRate,
                ["IgstAmt"] = Money.Round(item.Igst),
                ["CgstAmt"] = Money.Round(item.Cgst),
                ["SgstAmt"] = Money.Round(item.Sgst),
                ["TotItemVal"] = Money.Round(item.TotalItemValue),
            };
            if (!item.IsService)
            {
                // Goods need the unit quantity code; medicines are counted in numbers.
                line.Insert(6, "Unit", "NOS");
            }

            items.Add(line);
        }

        var total = document.Items.Sum(i => i.TotalItemValue) + document.RoundOff;
        if (Math.Abs(total - document.Total) > 0.01m)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"The lines and the round-off come to {total:0.00}, not the invoice's {document.Total:0.00}."));
        }

        if (Math.Abs(document.RoundOff) >= 100m)
        {
            problems.Add("The portal takes a round-off of less than ₹100.");
        }

        if (problems.Count > 0)
        {
            return (null, problems);
        }

        var payload = new JsonObject
        {
            ["Version"] = "1.1",
            ["TranDtls"] = new JsonObject { ["TaxSch"] = "GST", ["SupTyp"] = "B2B", ["RegRev"] = "N", ["IgstOnIntra"] = "N" },
            ["DocDtls"] = new JsonObject { ["Typ"] = "INV", ["No"] = document.Number, ["Dt"] = Date(document.Date) },
            ["SellerDtls"] = Party(Gstin.Normalize(seller.Gstin)!, sellerLegalName!, null, sellerAddress!, seller.Address.Line2, sellerLocation!,
                sellerPin!.Value, sellerState!, seller.Phone, seller.Email),
            ["BuyerDtls"] = Party(Gstin.Normalize(document.Buyer.Gstin)!, buyerLegalName!, document.PlaceOfSupply, buyerAddress.Line!, null,
                buyerAddress.Location!, buyerPin!.Value, buyerState!, document.Buyer.Phone, document.Buyer.Email),
            ["ItemList"] = items,
            ["ValDtls"] = new JsonObject
            {
                ["AssVal"] = Money.Round(document.Items.Sum(i => i.AssessableValue)),
                ["CgstVal"] = Money.Round(document.Items.Sum(i => i.Cgst)),
                ["SgstVal"] = Money.Round(document.Items.Sum(i => i.Sgst)),
                ["IgstVal"] = Money.Round(document.Items.Sum(i => i.Igst)),
                ["Discount"] = 0m,
                ["RndOffAmt"] = Money.Round(document.RoundOff),
                ["TotInvVal"] = Money.Round(document.Total),
            },
        };
        return (payload, []);
    }

    /// <summary>The portal's date: dd/MM/yyyy.</summary>
    public static string Date(DateOnly date) => date.ToString("dd'/'MM'/'yyyy", CultureInfo.InvariantCulture);

    /// <summary>The HSN or SAC code of the line with the largest taxable value, as the signed QR code names it.</summary>
    public static string? MainHsnCode(EInvoiceDocument document) =>
        document.Items.OrderByDescending(i => i.AssessableValue).FirstOrDefault()?.HsnCode;

    private static IEnumerable<string> Check(EInvoiceItem item, int line, bool interState)
    {
        var name = $"Line {line} ({Truncate(item.Description, 40)})";
        if (item.Description.Trim().Length < 3)
        {
            yield return $"{name}: the portal needs a description of at least 3 characters.";
        }

        if (item.HsnCode is not { } hsn || !HsnCode().IsMatch(hsn))
        {
            yield return item.IsService
                ? $"{name}: enter the service's 6-digit SAC code in the service master."
                : $"{name}: enter the item's HSN code (4, 6 or 8 digits) in the item master.";
        }
        else if (item.IsService != hsn.StartsWith("99", StringComparison.Ordinal))
        {
            yield return item.IsService
                ? $"{name}: {hsn} is an HSN code for goods; a service's SAC code starts with 99."
                : $"{name}: {hsn} is a SAC code for services; an item's HSN code does not start with 99.";
        }

        if (item.Quantity < 0 || item.TotalAmount < 0 || item.Discount < 0 || item.AssessableValue < 0)
        {
            yield return $"{name}: the portal takes no negative quantities or amounts; a return goes on a credit note.";
        }

        if (!Rates.Contains(item.GstRate))
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"{name}: the portal does not take a GST rate of {item.GstRate}%.");
        }

        if (Math.Abs(item.TotalAmount - item.Discount - item.AssessableValue) > 0.01m)
        {
            yield return $"{name}: the amount less the discount is not the taxable value.";
        }

        var tax = item.Igst + item.Cgst + item.Sgst;
        if (Math.Abs(item.AssessableValue + tax - item.TotalItemValue) > 0.01m)
        {
            yield return $"{name}: the taxable value and the GST do not add up to the line's value.";
        }

        if (Math.Abs(item.AssessableValue * item.GstRate / 100m - tax) > 1m)
        {
            yield return $"{name}: the GST is not the rate on the taxable value.";
        }

        // CGST and SGST are halves of the tax; when it has an odd paisa, SGST takes it (Money.SplitGst).
        if (interState ? item.Cgst != 0 || item.Sgst != 0 : item.Igst != 0 || Math.Abs(item.Cgst - item.Sgst) > 0.01m)
        {
            yield return interState
                ? $"{name}: a supply to another state carries IGST, not CGST and SGST."
                : $"{name}: a supply within the state carries CGST and SGST in equal halves, not IGST.";
        }
    }

    /// <summary>A supplier's or buyer's details; <paramref name="placeOfSupply"/> is the buyer's alone.</summary>
    private static JsonObject Party(
        string gstin, string legalName, string? placeOfSupply, string address, string? address2, string location, int pin, string state, string? phone,
        string? email)
    {
        var party = new JsonObject { ["Gstin"] = gstin, ["LglNm"] = legalName };
        if (placeOfSupply is not null)
        {
            party["Pos"] = placeOfSupply;
        }

        party["Addr1"] = address;
        if (!string.IsNullOrWhiteSpace(address2))
        {
            party["Addr2"] = Truncate(address2.Trim(), 100);
        }

        party["Loc"] = location;
        party["Pin"] = pin;
        party["Stcd"] = state;

        var digits = phone is null ? string.Empty : new string(phone.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length is >= 6 and <= 12)
        {
            party["Ph"] = digits;
        }

        if (email?.Trim() is { Length: >= 6 and <= 100 } mail)
        {
            party["Em"] = mail;
        }

        return party;
    }

    private static string? Required(string? value, int min, int max, string what, List<string> problems)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length < min)
        {
            problems.Add($"Enter {what}{(min > 1 ? $" (at least {min} characters)" : string.Empty)}.");
            return null;
        }

        return Truncate(text, max);
    }

    private static int? Pin(string? value, string? state, string whose, List<string> problems)
    {
        var text = value?.Trim();
        if (text is null || !PinCode().IsMatch(text))
        {
            problems.Add($"Enter {whose} 6-digit PIN code.");
            return null;
        }

        if (state is not null && !PinZones.Fits(state, text))
        {
            problems.Add($"PIN code {text} is not in {Gstin.States.GetValueOrDefault(state, state)}, the state of {whose} GSTIN.");
        }

        return int.Parse(text, CultureInfo.InvariantCulture);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    [GeneratedRegex("^[A-Z1-9][A-Z0-9/-]{0,15}$")]
    private static partial Regex DocumentNumber();

    [GeneratedRegex("^([0-9]{4}|[0-9]{6}|[0-9]{8})$")]
    private static partial Regex HsnCode();

    [GeneratedRegex("^[1-9][0-9]{5}$")]
    private static partial Regex PinCode();
}

/// <summary>
/// The postal circles (the first two digits of the PIN code) of each state, which the portal checks against the GSTIN's
/// state. Kept generous where circles share prefixes (Uttarakhand within Uttar Pradesh's, Goa within Maharashtra's,
/// Puducherry's districts inside Tamil Nadu, Kerala and Andhra Pradesh); a state not listed fits any PIN code.
/// </summary>
public static class PinZones
{
    private static readonly IReadOnlyDictionary<string, string[]> Circles = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["01"] = ["18", "19"],
        ["02"] = ["17"],
        ["03"] = ["14", "15", "16"],
        ["04"] = ["16"],
        ["05"] = ["24", "25", "26"],
        ["06"] = ["12", "13"],
        ["07"] = ["11"],
        ["08"] = ["30", "31", "32", "33", "34"],
        ["09"] = ["20", "21", "22", "23", "24", "25", "26", "27", "28"],
        ["10"] = ["80", "81", "82", "83", "84", "85"],
        ["11"] = ["73"],
        ["12"] = ["79"],
        ["13"] = ["79"],
        ["14"] = ["79"],
        ["15"] = ["79"],
        ["16"] = ["79"],
        ["17"] = ["79"],
        ["18"] = ["78"],
        ["19"] = ["70", "71", "72", "73", "74"],
        ["20"] = ["81", "82", "83"],
        ["21"] = ["75", "76", "77"],
        ["22"] = ["49"],
        ["23"] = ["45", "46", "47", "48"],
        ["24"] = ["36", "37", "38", "39"],
        ["26"] = ["36", "39"],
        ["27"] = ["40", "41", "42", "43", "44"],
        ["29"] = ["56", "57", "58", "59"],
        ["30"] = ["40"],
        ["31"] = ["68"],
        ["32"] = ["67", "68", "69"],
        ["33"] = ["60", "61", "62", "63", "64"],
        ["34"] = ["53", "60", "67"],
        ["35"] = ["74"],
        ["36"] = ["50"],
        ["37"] = ["50", "51", "52", "53"],
        ["38"] = ["19"],
    };

    /// <summary>Whether <paramref name="pin"/> can be in <paramref name="state"/>.</summary>
    public static bool Fits(string state, string pin) => !Circles.TryGetValue(state, out var circles) || circles.Contains(pin[..2]);
}

/// <summary>
/// A buyer's address as the payer master and the pharmacy keep it, free text, split into what the e-invoice names: the
/// address line, the place (the part just before the PIN code) and the PIN code.
/// </summary>
public static partial class BuyerAddress
{
    public sealed record Parts(string? Line, string? Location, string? Pin, string? Problem);

    public static Parts Parse(string? address)
    {
        var text = address?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return new Parts(null, null, null, "Enter the buyer's address, with its PIN code.");
        }

        var pins = PinCode().Matches(text);
        if (pins.Count == 0)
        {
            return new Parts(null, null, null, "The buyer's address needs its 6-digit PIN code.");
        }

        var pin = pins[^1].Value;
        var rest = text.Remove(pins[^1].Index, pin.Length);
        var segments = Separators().Split(rest)
            .Select(s => s.Trim(' ', '-', '.'))
            .Where(s => s.Length > 0 && !Gstin.States.Values.Contains(s, StringComparer.OrdinalIgnoreCase) && !s.Equals("India", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (segments.Count == 0)
        {
            return new Parts(null, null, pin, "The buyer's address needs the street and the place, before the PIN code.");
        }

        var location = segments[^1];
        var line = segments.Count > 1 ? string.Join(", ", segments[..^1]) : segments[0];
        if (location.Length < 3)
        {
            return new Parts(line, null, pin, "The buyer's address needs the place (at least 3 letters) before the PIN code.");
        }

        return new Parts(line.Length <= 100 ? line : line[..100], location.Length <= 50 ? location : location[..50], pin, null);
    }

    [GeneratedRegex(@"(?<!\d)[1-9]\d{5}(?!\d)")]
    private static partial Regex PinCode();

    [GeneratedRegex(@"[,\n\r;]+")]
    private static partial Regex Separators();
}

/// <summary>
/// The portal's signed QR code: a JWT whose payload's <c>data</c> names the invoice. Read here to make sure the IRN being
/// recorded is this invoice's; the signature is the portal's, and is checked by whoever scans the printed code with the
/// portal's app.
/// </summary>
public static partial class SignedQrCode
{
    public sealed record Contents(
        string SellerGstin,
        string BuyerGstin,
        string DocNo,
        string DocTyp,
        string DocDt,
        decimal TotInvVal,
        int ItemCnt,
        string? MainHsnCode,
        string Irn,
        DateTimeOffset IrnDt);

    private static readonly TimeSpan India = TimeSpan.FromHours(5.5);

    public static Result<Contents> Read(string? jwt)
    {
        var parts = jwt?.Trim().Split('.');
        if (parts is not { Length: 3 })
        {
            return Error.Field("signedQrCode", "Paste the signed QR code exactly as the portal gave it (three parts separated by dots).");
        }

        try
        {
            using var outer = JsonDocument.Parse(Base64Url(parts[1]));
            var data = outer.RootElement.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.String
                ? inner.GetString()!
                : outer.RootElement.GetRawText();
            using var fields = JsonDocument.Parse(data);
            var root = fields.RootElement;
            var irn = Text(root, "Irn");
            if (!IrnPattern().IsMatch(irn))
            {
                return Error.Field("signedQrCode", "The signed QR code does not hold a 64-character IRN.");
            }

            if (!DateTime.TryParseExact(Text(root, "IrnDt"), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var irnDate))
            {
                return Error.Field("signedQrCode", "The signed QR code does not hold the IRN's date.");
            }

            return new Contents(
                Text(root, "SellerGstin"), Text(root, "BuyerGstin"), Text(root, "DocNo"), Text(root, "DocTyp"), Text(root, "DocDt"),
                root.TryGetProperty("TotInvVal", out var total) && total.TryGetDecimal(out var value) ? value : -1m,
                root.TryGetProperty("ItemCnt", out var count) && count.TryGetInt32(out var items) ? items : -1,
                root.TryGetProperty("MainHsnCode", out var hsn) ? hsn.ToString() : null,
                irn.ToLowerInvariant(),
                new DateTimeOffset(irnDate, India));
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            return Error.Field("signedQrCode", "The signed QR code cannot be read. Paste it exactly as the portal gave it.");
        }
    }

    /// <summary>What in the code does not match the invoice; empty when it is this invoice's.</summary>
    public static IReadOnlyList<string> Mismatches(Contents qr, EInvoiceDocument document, string? sellerGstin)
    {
        var problems = new List<string>();
        void Check(bool same, string what, string found, string expected)
        {
            if (!same)
            {
                problems.Add($"The code's {what} is {found}, the invoice's {expected}.");
            }
        }

        Check(string.Equals(qr.SellerGstin, Gstin.Normalize(sellerGstin), StringComparison.OrdinalIgnoreCase), "supplier GSTIN", qr.SellerGstin, sellerGstin ?? "none");
        Check(string.Equals(qr.BuyerGstin, Gstin.Normalize(document.Buyer.Gstin), StringComparison.OrdinalIgnoreCase), "buyer GSTIN", qr.BuyerGstin, document.Buyer.Gstin);
        Check(string.Equals(qr.DocNo, document.Number, StringComparison.OrdinalIgnoreCase), "invoice number", qr.DocNo, document.Number);
        Check(qr.DocTyp == "INV", "document type", qr.DocTyp, "INV");
        Check(qr.DocDt == InvoiceRegistrationPayload.Date(document.Date), "invoice date", qr.DocDt, InvoiceRegistrationPayload.Date(document.Date));
        Check(Math.Abs(qr.TotInvVal - document.Total) <= 0.01m, "invoice value",
            qr.TotInvVal.ToString("0.00", CultureInfo.InvariantCulture), document.Total.ToString("0.00", CultureInfo.InvariantCulture));
        Check(qr.ItemCnt == document.Items.Count, "number of lines",
            qr.ItemCnt.ToString(CultureInfo.InvariantCulture), document.Items.Count.ToString(CultureInfo.InvariantCulture));
        return problems;
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;

    private static string Base64Url(string part)
    {
        var padded = part.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex IrnPattern();
}
