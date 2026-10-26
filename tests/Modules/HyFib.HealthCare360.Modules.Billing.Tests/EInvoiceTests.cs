using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// The e-invoice JSON (INV-01 v1.1) for a B2B tax invoice and the portal's checks on it; the buyer's address read for its
/// PIN code; the portal's signed QR code read and matched to the invoice; and the 24 hours in which an IRN is cancelled.
/// </summary>
public sealed class EInvoiceTests
{
    private const string Hospital = "33AABCN1234F1Z3";
    private const string KeralaBuyer = "32AAACH7409R1ZA";

    private static readonly EInvoiceSeller Seller = new(
        Hospital, "Nana Hospitals Private Limited",
        new Address { Line1 = "12 Anna Salai", Line2 = "Teynampet", City = "Chennai", State = "Tamil Nadu", PinCode = "600018" },
        "+914424340000", "accounts@nanahospitals.in");

    [Fact]
    public void A_bill_to_a_company_in_another_state_becomes_the_portals_json_with_igst()
    {
        var (payload, problems) = InvoiceRegistrationPayload.Build(Seller, Invoice());

        problems.ShouldBeEmpty();
        var json = payload.ShouldNotBeNull();
        json["Version"]!.GetValue<string>().ShouldBe("1.1");
        json["TranDtls"]!["SupTyp"]!.GetValue<string>().ShouldBe("B2B");
        (json["DocDtls"]!["Typ"]!.GetValue<string>(), json["DocDtls"]!["No"]!.GetValue<string>(), json["DocDtls"]!["Dt"]!.GetValue<string>())
            .ShouldBe(("INV", "NHOB260000123", "24/10/2026"));
        var seller = json["SellerDtls"]!;
        (seller["Gstin"]!.GetValue<string>(), seller["Loc"]!.GetValue<string>(), seller["Pin"]!.GetValue<int>(), seller["Stcd"]!.GetValue<string>())
            .ShouldBe((Hospital, "Chennai", 600018, "33"));
        seller["Ph"]!.GetValue<string>().ShouldBe("914424340000");
        var buyer = json["BuyerDtls"]!;
        (buyer["Pos"]!.GetValue<string>(), buyer["Addr1"]!.GetValue<string>(), buyer["Loc"]!.GetValue<string>(), buyer["Pin"]!.GetValue<int>())
            .ShouldBe(("32", "Malabar Traders, 4/12 MG Road", "Kochi", 682016));
        var item = json["ItemList"]![0]!;
        (item["SlNo"]!.GetValue<string>(), item["IsServc"]!.GetValue<string>(), item["HsnCd"]!.GetValue<string>()).ShouldBe(("1", "Y", "999311"));
        item.AsObject().ContainsKey("Unit").ShouldBeFalse("A service has no unit quantity code.");
        (item["AssAmt"]!.GetValue<decimal>(), item["IgstAmt"]!.GetValue<decimal>(), item["TotItemVal"]!.GetValue<decimal>())
            .ShouldBe((900m, 162m, 1062m));
        var values = json["ValDtls"]!;
        (values["AssVal"]!.GetValue<decimal>(), values["IgstVal"]!.GetValue<decimal>(), values["CgstVal"]!.GetValue<decimal>(), values["TotInvVal"]!.GetValue<decimal>())
            .ShouldBe((900m, 162m, 0m, 1062m));
    }

    [Fact]
    public void Medicines_go_with_their_hsn_code_and_a_unit()
    {
        var medicine = new EInvoiceItem("Paracetamol 500 mg tablets", false, "30049099", 10m, 1.786m, 17.86m, 0m, 17.86m, 12m, 0m, 1.07m, 1.07m, 20m);
        var (payload, problems) = InvoiceRegistrationPayload.Build(Seller, Invoice(placeOfSupply: "33", items: [medicine], total: 20m) with
        {
            Buyer = new EInvoiceBuyer("33AAACH7409R1Z8", "Sree Clinic", "5 Gandhi Road, Madurai 625001", null, null),
        });

        problems.ShouldBeEmpty();
        var item = payload!["ItemList"]![0]!;
        (item["IsServc"]!.GetValue<string>(), item["Unit"]!.GetValue<string>(), item["CgstAmt"]!.GetValue<decimal>()).ShouldBe(("N", "NOS", 1.07m));
    }

    [Theory]
    [InlineData("sac", "enter the service's 6-digit SAC code")]
    [InlineData("goods-sac", "is a SAC code for services")]
    [InlineData("negative", "a return goes on a credit note")]
    [InlineData("rate", "does not take a GST rate of 7%")]
    [InlineData("cgst-to-another-state", "carries IGST, not CGST and SGST")]
    [InlineData("tax", "the GST is not the rate on the taxable value")]
    public void The_portals_line_checks_are_made_here(string fault, string expected)
    {
        var item = fault switch
        {
            "sac" => Service() with { HsnCode = null },
            "goods-sac" => Service() with { IsService = false },
            "negative" => Service() with { Quantity = -1m },
            "rate" => Service() with { GstRate = 7m, Igst = 63m, TotalItemValue = 963m },
            "cgst-to-another-state" => Service() with { Igst = 0m, Cgst = 81m, Sgst = 81m },
            _ => Service() with { Igst = 100m, TotalItemValue = 1000m },
        };

        var (payload, problems) = InvoiceRegistrationPayload.Build(Seller, Invoice(items: [item], total: item.TotalItemValue));

        payload.ShouldBeNull();
        problems.ShouldContain(p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void The_invoice_number_the_hospital_and_the_buyer_must_be_ones_the_portal_takes()
    {
        var seller = Seller with { Gstin = null, Address = Seller.Address with { PinCode = null } };
        var invoice = Invoice(number: "NHMAINOB2600000123", total: 1000m) with
        {
            Buyer = new EInvoiceBuyer(KeralaBuyer, "Malabar Traders", "4/12 MG Road, Chennai 600018", null, null),
        };

        var (_, problems) = InvoiceRegistrationPayload.Build(seller, invoice);

        problems.ShouldContain(p => p.Contains("up to 16 letters", StringComparison.Ordinal));
        problems.ShouldContain("Enter the hospital's GSTIN under Hospital setup.");
        problems.ShouldContain("Enter the hospital's 6-digit PIN code.");
        problems.ShouldContain(p => p.Contains("PIN code 600018 is not in Kerala", StringComparison.Ordinal));
        problems.ShouldContain(p => p.Contains("not the invoice's 1000.00", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Malabar Traders, 4/12 MG Road, Kochi, Kerala 682016", "Malabar Traders, 4/12 MG Road", "Kochi", "682016")]
    [InlineData("Door 5\nGandhi Road\nMadurai - 625001\nIndia", "Door 5, Gandhi Road", "Madurai", "625001")]
    [InlineData("Plot 9, Hosur Road, Bengaluru 560034", "Plot 9, Hosur Road", "Bengaluru", "560034")]
    public void A_free_text_address_gives_the_line_the_place_and_the_pin(string address, string line, string location, string pin) =>
        BuyerAddress.Parse(address).ShouldBe(new BuyerAddress.Parts(line, location, pin, null));

    [Theory]
    [InlineData(null, "Enter the buyer's address")]
    [InlineData("4/12 MG Road, Kochi", "needs its 6-digit PIN code")]
    [InlineData("682016", "needs the street and the place")]
    public void An_address_without_a_pin_or_a_place_is_refused(string? address, string expected) =>
        BuyerAddress.Parse(address).Problem.ShouldNotBeNull().ShouldContain(expected);

    [Theory]
    [InlineData("33", "600018", true)]
    [InlineData("32", "682016", true)]
    [InlineData("34", "533464", true)]
    [InlineData("32", "600018", false)]
    [InlineData("27", "560034", false)]
    [InlineData("33", "682016", false)]
    [InlineData("97", "110001", true)]
    public void A_pin_code_is_in_its_states_postal_zone(string state, string pin, bool fits) => PinZones.Fits(state, pin).ShouldBe(fits);

    [Fact]
    public void The_signed_qr_code_names_the_invoice_it_was_given_for()
    {
        var invoice = Invoice();
        var read = SignedQrCode.Read(Qr(invoice, irn: new string('a', 64)));

        read.IsSuccess.ShouldBeTrue();
        var qr = read.Value;
        (qr.Irn, qr.DocNo, qr.IrnDt).ShouldBe((new string('a', 64), "NHOB260000123", new DateTimeOffset(2026, 10, 24, 18, 5, 0, TimeSpan.FromHours(5.5))));
        SignedQrCode.Mismatches(qr, invoice, Hospital).ShouldBeEmpty();
    }

    [Fact]
    public void A_signed_qr_code_for_another_invoice_is_not_accepted()
    {
        var other = Invoice(number: "NHOB260000124", total: 2000m);
        var qr = SignedQrCode.Read(Qr(other, irn: new string('b', 64))).Value;

        var mismatches = SignedQrCode.Mismatches(qr, Invoice(), Hospital);

        mismatches.ShouldContain("The code's invoice number is NHOB260000124, the invoice's NHOB260000123.");
        mismatches.ShouldContain("The code's invoice value is 2000.00, the invoice's 1062.00.");
    }

    [Theory]
    [InlineData("not a code")]
    [InlineData("a.b.c")]
    public void Something_that_is_not_a_signed_qr_code_is_refused(string text) =>
        SignedQrCode.Read(text).IsFailure.ShouldBeTrue();

    [Fact]
    public void An_irn_is_cancelled_within_24_hours_of_its_generation_and_only_once()
    {
        var generated = new DateTimeOffset(2026, 10, 24, 12, 35, 0, TimeSpan.Zero);
        var late = new EInvoice { AckDate = generated };
        late.Cancel(EInvoiceCancelReason.DataEntryMistake, "Wrong buyer", generated.AddHours(25)).Error!.Code.ShouldBe("einvoice.cancel_window");

        var einvoice = new EInvoice { AckDate = generated };
        einvoice.Cancel(EInvoiceCancelReason.DataEntryMistake, " Wrong buyer ", generated.AddHours(23)).IsSuccess.ShouldBeTrue();
        (einvoice.Status, einvoice.CancelReason, einvoice.CancelRemark).ShouldBe((EInvoiceStatus.Cancelled, EInvoiceCancelReason.DataEntryMistake, "Wrong buyer"));
        einvoice.Cancel(EInvoiceCancelReason.Other, "Again", generated.AddHours(23)).Error!.Code.ShouldBe("einvoice.cancelled");
    }

    private static EInvoiceItem Service() => new("Executive health check-up", true, "999311", 1m, 1000m, 1000m, 100m, 900m, 18m, 162m, 0m, 0m, 1062m);

    private static EInvoiceDocument Invoice(
        string number = "NHOB260000123", string placeOfSupply = "32", IReadOnlyList<EInvoiceItem>? items = null, decimal total = 1062m) =>
        new("bill", Guid.CreateVersion7(), Guid.CreateVersion7(), number, new DateOnly(2026, 10, 24),
            new EInvoiceBuyer(KeralaBuyer, "Malabar Traders Ltd", "Malabar Traders, 4/12 MG Road, Kochi, Kerala 682016", "9876543210", null),
            placeOfSupply, items ?? [Service()], 0m, total, false);

    /// <summary>A signed QR code as the portal makes it: the invoice's details in the payload's <c>data</c>, signed (here, not really).</summary>
    private static string Qr(EInvoiceDocument invoice, string irn)
    {
        var data = new JsonObject
        {
            ["SellerGstin"] = Hospital,
            ["BuyerGstin"] = invoice.Buyer.Gstin,
            ["DocNo"] = invoice.Number,
            ["DocTyp"] = "INV",
            ["DocDt"] = InvoiceRegistrationPayload.Date(invoice.Date),
            ["TotInvVal"] = invoice.Total,
            ["ItemCnt"] = invoice.Items.Count,
            ["MainHsnCode"] = InvoiceRegistrationPayload.MainHsnCode(invoice),
            ["Irn"] = irn,
            ["IrnDt"] = "2026-10-24 18:05:00",
        };
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = JsonSerializer.Serialize(new { data = data.ToJsonString(), iss = "NIC" });
        return $"{Part("{\"alg\":\"RS256\",\"typ\":\"JWT\"}")}.{Part(payload)}.c2lnbmF0dXJl";
    }
}
