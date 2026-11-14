using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// What a document's print says of its e-invoice (<see cref="EInvoiceService.PrintOf"/>, behind
/// <see cref="IEInvoiceRegister.PrintAsync"/>): a live IRN always prints; otherwise a B2B document that e-invoicing applies
/// to (GST rule 48(4)), not cancelled here, says why it has none, which its print turns into "not a valid tax invoice for
/// the buyer" (rule 48(5)). Every other document says nothing.
/// </summary>
public sealed class EInvoicePrintTests
{
    private static readonly DateOnly Dated = new(2026, 10, 24);
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 6, 30, 0, TimeSpan.Zero);

    public enum Document
    {
        /// <summary>Its source does not call it a B2B e-invoice: a bill to a patient, a bill of supply, a draft.</summary>
        NotAnEInvoice,
        B2b,
        B2bCancelledHere,
    }

    public enum Registration
    {
        None,
        Live,
        Cancelled,
    }

    [Theory]
    // E-invoicing applies from (days from the document's date; null: not switched on), the document, its IRN: the print.
    [InlineData(null, Document.B2b, Registration.None, "nothing")]
    [InlineData(1, Document.B2b, Registration.None, "nothing")]
    [InlineData(0, Document.B2b, Registration.None, "Pending")]
    [InlineData(-30, Document.B2b, Registration.None, "Pending")]
    [InlineData(-30, Document.NotAnEInvoice, Registration.None, "nothing")]
    [InlineData(-30, Document.B2bCancelledHere, Registration.None, "nothing")]
    [InlineData(-30, Document.B2b, Registration.Cancelled, "IrnCancelled")]
    [InlineData(-30, Document.B2bCancelledHere, Registration.Cancelled, "nothing")]
    [InlineData(1, Document.B2b, Registration.Cancelled, "nothing")]
    [InlineData(-30, Document.B2b, Registration.Live, "IRN")]
    [InlineData(1, Document.B2b, Registration.Live, "IRN")]
    [InlineData(null, Document.NotAnEInvoice, Registration.Live, "IRN")]
    public void A_document_due_an_irn_says_why_it_has_none_and_a_live_irn_always_prints(
        int? applicableFromDays, Document document, Registration registration, string print)
    {
        var applicableFrom = applicableFromDays is { } days ? Dated.AddDays(days) : (DateOnly?)null;

        var result = EInvoiceService.PrintOf(DocumentOf(document), applicableFrom, RegistrationOf(registration));

        var said = result switch
        {
            { Stamp: { } stamp, Missing: null } => stamp.Irn == Irn ? "IRN" : "another IRN",
            { Stamp: null, Missing: { } missing } => missing.ToString(),
            { Stamp: null, Missing: null } => "nothing",
            _ => "both",
        };
        said.ShouldBe(print);
    }

    [Fact]
    public void A_live_irn_prints_with_its_acknowledgement_and_signed_qr_code()
    {
        var stamp = EInvoiceService.PrintOf(null, null, RegistrationOf(Registration.Live)).Stamp.ShouldNotBeNull();

        (stamp.Irn, stamp.AckNo, stamp.AckDate).ShouldBe((Irn, "112610012345678", Now));
        stamp.QrCodeSvg.ShouldStartWith("data:image/svg+xml;base64,");
    }

    private static readonly string Irn = new('a', 64);

    private static EInvoiceDocument? DocumentOf(Document document) => document switch
    {
        Document.NotAnEInvoice => null,
        _ => new EInvoiceDocument(
            "bill", Guid.CreateVersion7(), Guid.CreateVersion7(), "NHOB260000123", Dated,
            new EInvoiceBuyer("32AAACH7409R1ZA", "Malabar Traders Ltd", "4/12 MG Road, Kochi, Kerala 682016", null, null), "32",
            [new EInvoiceItem("Executive health check-up", true, "999311", 1m, 1000m, 1000m, 0m, 1000m, 18m, 180m, 0m, 0m, 1180m)], 0m, 1180m,
            IsCancelled: document == Document.B2bCancelledHere),
    };

    private static EInvoice? RegistrationOf(Registration registration)
    {
        if (registration == Registration.None)
        {
            return null;
        }

        var einvoice = new EInvoice
        {
            Source = "bill",
            SourceId = Guid.CreateVersion7(),
            DocumentNumber = "NHOB260000123",
            DocumentDate = Dated,
            Irn = Irn,
            AckNo = "112610012345678",
            AckDate = Now,
            SignedQrCode = "eyJhbGciOiJSUzI1NiJ9.e30.c2lnbmF0dXJl",
        };
        if (registration == Registration.Cancelled)
        {
            einvoice.Cancel(EInvoiceCancelReason.DataEntryMistake, "Billed to the wrong company", Now.AddHours(1)).IsSuccess.ShouldBeTrue();
        }

        return einvoice;
    }
}
