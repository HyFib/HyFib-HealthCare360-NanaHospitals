using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// An IRN recorded by hand, checked with the connected portal: the portal is asked only while it still gives an IRN's
/// details, and the IRN is recorded only when the portal holds it, active, under the acknowledgement given, with a signed QR
/// code naming the document.
/// </summary>
public sealed class IrnVerificationTests
{
    private const string Hospital = "33AABCN1234F1Z3";
    private const string AckNo = "112610012345678";
    private static readonly string Irn = new('a', 64);

    [Fact]
    public void The_portal_is_asked_only_while_it_still_gives_an_irns_details()
    {
        var generated = new DateTimeOffset(2026, 10, 24, 18, 5, 0, TimeSpan.FromHours(5.5));

        IrnVerification.AsksPortal(generated, generated.AddHours(1)).ShouldBeTrue();
        IrnVerification.AsksPortal(generated, generated + IrpIrn.DetailsWindow).ShouldBeTrue();
        IrnVerification.AsksPortal(generated, generated + IrpIrn.DetailsWindow + TimeSpan.FromMinutes(1)).ShouldBeFalse();
    }

    [Fact]
    public void The_portal_confirms_an_irn_it_holds_active_under_the_acknowledgement_for_the_document() =>
        IrnVerification.Contradictions(Held(), Irn, $" {AckNo} ", Invoice(), Hospital).ShouldBeEmpty();

    [Fact]
    public void An_irn_the_portal_holds_cancelled_or_for_another_document_or_acknowledgement_is_not_confirmed()
    {
        IrnVerification.Contradictions(Held() with { Status = IrpIrn.CancelledStatus }, Irn, AckNo, Invoice(), Hospital)
            .ShouldBe(["The portal holds it as cancelled."]);
        IrnVerification.Contradictions(Held() with { Status = "REJ" }, Irn, AckNo, Invoice(), Hospital).ShouldBe(["The portal holds it as REJ, not active."]);
        IrnVerification.Contradictions(Held() with { Irn = new string('b', 64) }, Irn, AckNo, Invoice(), Hospital)
            .ShouldBe([$"The portal answered for IRN {new string('b', 64)}."]);
        IrnVerification.Contradictions(Held(), Irn, "112610099999999", Invoice(), Hospital)
            .ShouldBe([$"The portal's acknowledgement number for it is {AckNo}, not 112610099999999."]);
        IrnVerification.Contradictions(Held(Invoice("NHOB260000124")), Irn, AckNo, Invoice(), Hospital).ShouldHaveSingleItem()
            .ShouldBe("The portal's signed QR code for it is not this invoice's: The code's invoice number is NHOB260000124, the invoice's NHOB260000123.");
    }

    [Fact]
    public void An_answer_that_does_not_say_the_irn_is_active_does_not_confirm_it() =>
        IrnVerification.Contradictions(Held() with { Status = null }, Irn, AckNo, Invoice(), Hospital)
            .ShouldBe(["The portal's answer does not say whether it is active."]);

    /// <summary>The IRN's details as the portal gives them, its signed QR code naming <paramref name="document"/>.</summary>
    private static IrpIrn Held(EInvoiceDocument? document = null) =>
        new(Irn, AckNo, DateTimeOffset.UtcNow, "signed.invoice.jwt", Qr(document ?? Invoice()), Status: IrpIrn.ActiveStatus);

    private static EInvoiceDocument Invoice(string number = "NHOB260000123") =>
        new("bill", Guid.CreateVersion7(), Guid.CreateVersion7(), number, new DateOnly(2026, 10, 24),
            new EInvoiceBuyer("32AAACH7409R1ZA", "Malabar Traders Ltd", "Malabar Traders, 4/12 MG Road, Kochi, Kerala 682016", null, null), "32",
            [new EInvoiceItem("Executive health check-up", true, "999311", 1m, 1000m, 1000m, 100m, 900m, 18m, 162m, 0m, 0m, 1062m)], 0m, 1062m, false);

    private static string Qr(EInvoiceDocument document)
    {
        var data = new JsonObject
        {
            ["SellerGstin"] = Hospital,
            ["BuyerGstin"] = document.Buyer.Gstin,
            ["DocNo"] = document.Number,
            ["DocTyp"] = document.Type,
            ["DocDt"] = InvoiceRegistrationPayload.Date(document.Date),
            ["TotInvVal"] = document.Total,
            ["ItemCnt"] = document.Items.Count,
            ["Irn"] = Irn,
            ["IrnDt"] = "2026-10-24 18:05:00",
        };
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Part("{\"alg\":\"RS256\"}")}.{Part(JsonSerializer.Serialize(new { data = data.ToJsonString(), iss = "NIC" }))}.c2lnbmF0dXJl";
    }
}
