using System.Globalization;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Receipts;

namespace HyFib.HealthCare360.Modules.SupplyChain.Tests;

/// <summary>
/// The invoice's total as printed is kept to the paisa, as the column holds it: the checker compares the total the store
/// typed, not one the database rounded, and a total too large to keep is refused as entered rather than failing on save.
/// </summary>
public sealed class GoodsReceiptRequestTests
{
    private static readonly DateOnly Today = new(2026, 11, 6);

    [Theory]
    [InlineData(null, true)]
    [InlineData("0", true)]
    [InlineData("525.40", true)]
    [InlineData("9999999999999999.99", true)]
    [InlineData("525.405", false)]
    [InlineData("-1", false)]
    [InlineData("10000000000000000", false)]
    public void The_invoice_total_as_printed_is_refused_beyond_two_decimal_places_or_the_columns_size(string? printed, bool valid)
    {
        var amount = printed is null ? (decimal?)null : decimal.Parse(printed, CultureInfo.InvariantCulture);
        var request = new CreateGoodsReceiptRequest(
            Guid.CreateVersion7(), Guid.CreateVersion7(), null, "SGP/26-27/1873", Today, Today, null,
            [new GoodsReceiptLineInput(Guid.CreateVersion7(), null, "AMX2601", null, Today.AddYears(2), 10m, 0m, 50m, 80m, 0m, 5m, AcceptShortExpiry: false)],
            SendForApproval: true, VendorInvoiceAmount: amount);

        var result = new CreateGoodsReceiptRequestValidator().Validate(request);

        result.Errors.Any(e => e.PropertyName == nameof(CreateGoodsReceiptRequest.VendorInvoiceAmount)).ShouldBe(!valid);
    }
}
