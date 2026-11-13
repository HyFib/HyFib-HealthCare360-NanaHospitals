using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// A document's IRN asked for on HC360's own once it is final: what each ending leads to. Only what can change on its own
/// is asked again; a refusal is not, and the register's holders hear of it.
/// </summary>
public sealed class EInvoiceAutomationTests
{
    [Theory]
    [InlineData(null, EInvoiceAutoOutcome.Done)]
    [InlineData("einvoice.registered", EInvoiceAutoOutcome.Done)]
    [InlineData("einvoice.document_cancelled", EInvoiceAutoOutcome.Done)]
    [InlineData("einvoice.not_applicable", EInvoiceAutoOutcome.Done)]
    [InlineData("einvoice.irp_unreachable", EInvoiceAutoOutcome.Retry)]
    [InlineData("einvoice.irp_not_connected", EInvoiceAutoOutcome.Retry)]
    [InlineData("einvoice.invalid", EInvoiceAutoOutcome.GiveUp)]
    [InlineData("einvoice.irp_refused", EInvoiceAutoOutcome.GiveUp)]
    [InlineData("einvoice.irp_mismatch", EInvoiceAutoOutcome.GiveUp)]
    [InlineData("einvoice.irp_cancelled", EInvoiceAutoOutcome.GiveUp)]
    [InlineData("einvoice.irn_exists", EInvoiceAutoOutcome.GiveUp)]
    public void An_automatic_request_is_tried_again_only_when_what_stopped_it_can_change_on_its_own(string? code, EInvoiceAutoOutcome expected) =>
        EInvoiceAutomation.After(code).ShouldBe(expected);
}
