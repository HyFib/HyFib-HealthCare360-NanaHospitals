using HyFib.HealthCare360.Modules.SupportServices.Domain.Canteen;

namespace HyFib.HealthCare360.Modules.SupportServices.Features.Canteen;

public sealed record MenuItemDto(
    Guid Id,
    string Code,
    string Name,
    MenuCategory Category,
    decimal Price,
    decimal GstRate,
    string? HsnSac,
    bool PriceIncludesGst,
    bool IsVeg,
    bool IsAvailable,
    bool TrackStock,
    int StockOnHand,
    int DisplayOrder,
    string? Description,
    uint Version);

/// <param name="HsnSac">HSN code of packaged goods or a SAC; empty for prepared food, which is billed under SAC 996333.</param>
public sealed record SaveMenuItemRequest(
    string Code,
    string Name,
    MenuCategory Category,
    decimal Price,
    decimal GstRate,
    string? HsnSac,
    bool PriceIncludesGst,
    bool IsVeg,
    bool IsAvailable,
    bool TrackStock,
    int DisplayOrder,
    string? Description,
    uint? Version);

public sealed record StockMovementRequest(StockMovementKind Kind, int Quantity, string? Reason);

public sealed record SaleLineInput(Guid MenuItemId, int Quantity);

public sealed record CreateSaleRequest(
    CanteenCustomerType CustomerType,
    string? CustomerName,
    string? EmployeeCode,
    string? EmployeeName,
    Guid? PatientId,
    CanteenPaymentMode PaymentMode,
    string? PaymentReference,
    IReadOnlyList<SaleLineInput> Lines);

public sealed record CanteenSaleLineDto(
    Guid MenuItemId,
    string ItemName,
    string? HsnSac,
    int Quantity,
    decimal UnitPrice,
    decimal GstRate,
    decimal TaxableAmount,
    decimal CgstAmount,
    decimal SgstAmount,
    decimal LineTotal);

public sealed record CanteenSaleDto(
    Guid Id,
    string BillNumber,
    DateTimeOffset SoldAt,
    DateOnly BusinessDate,
    CanteenCustomerType CustomerType,
    string? CustomerName,
    string? EmployeeCode,
    string? EmployeeName,
    decimal TaxableAmount,
    decimal CgstAmount,
    decimal SgstAmount,
    decimal GrossAmount,
    decimal RoundOff,
    decimal NetAmount,
    CanteenPaymentMode PaymentMode,
    string? PaymentReference,
    CanteenSaleStatus Status,
    string? CashierName,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    IReadOnlyList<CanteenSaleLineDto> Lines);

public sealed record CanteenSaleListItem(
    Guid Id,
    string BillNumber,
    DateTimeOffset SoldAt,
    CanteenCustomerType CustomerType,
    string? CustomerName,
    string? EmployeeCode,
    decimal NetAmount,
    CanteenPaymentMode PaymentMode,
    CanteenSaleStatus Status,
    string? CashierName);

public sealed record CancelSaleRequest(string Reason);

public sealed record AmountByKey(string Key, int Bills, decimal Amount);

public sealed record ItemConsumption(Guid MenuItemId, string ItemName, MenuCategory? Category, int QuantitySold, decimal Amount, int? ClosingStock);

/// <summary>Completed bill lines of one HSN/SAC code and GST rate, as the GSTR-1 HSN summary needs them.</summary>
/// <param name="HsnSac">Null for lines billed before codes were recorded.</param>
/// <param name="Total">Taxable value plus CGST and SGST (before the bills' round-off).</param>
public sealed record HsnSacSummary(string? HsnSac, decimal GstRate, int Quantity, decimal TaxableAmount, decimal Cgst, decimal Sgst, decimal Total);

/// <summary>Day-end canteen report (CAN-04): sales with GST split, collections by mode, item-wise consumption and the HSN/SAC summary.</summary>
public sealed record DailySalesSummary(
    DateOnly Date,
    int Bills,
    int CancelledBills,
    decimal TaxableAmount,
    decimal Cgst,
    decimal Sgst,
    decimal RoundOff,
    decimal NetSales,
    IReadOnlyList<AmountByKey> ByPaymentMode,
    IReadOnlyList<AmountByKey> ByCustomerType,
    IReadOnlyList<AmountByKey> ByCategory,
    IReadOnlyList<ItemConsumption> Items,
    IReadOnlyList<HsnSacSummary> ByHsnSac);

public sealed record StaffBalanceDto(string EmployeeCode, string? EmployeeName, decimal Balance, DateOnly LastEntryDate);

public sealed record SettleStaffCreditRequest(string Period, string Reference, IReadOnlyList<string>? EmployeeCodes);

/// <param name="Skipped">Balances not deducted, with the reason (unknown or separated employee, payroll already approved).</param>
public sealed record SettlementResult(string Period, int Employees, decimal Amount, IReadOnlyList<string>? Skipped = null);

public sealed record StaffPaymentRequest(string EmployeeCode, decimal Amount, string? Reference);

public sealed class SaveMenuItemRequestValidator : AbstractValidator<SaveMenuItemRequest>
{
    public SaveMenuItemRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(24).Matches("^[A-Za-z0-9-]+$").WithMessage("Use letters, digits or dashes.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Category).IsInEnum();
        RuleFor(x => x.Price).GreaterThan(0).LessThanOrEqualTo(10_000).PrecisionScale(8, 2, true);
        RuleFor(x => x.GstRate).Must(r => r is 0m or 5m or 12m or 18m or 28m).WithMessage("Use a GST slab: 0, 5, 12, 18 or 28 %.");
        RuleFor(x => x.HsnSac).Must(c => MenuItem.IsValidHsnSac(c!.Trim())).When(x => !string.IsNullOrWhiteSpace(x.HsnSac))
            .WithMessage("Enter the HSN or SAC code as 4, 6 or 8 digits, or leave it empty for food made in the canteen.");
        RuleFor(x => x.DisplayOrder).InclusiveBetween(0, 10_000);
        RuleFor(x => x.Description).SafeText(300);
    }
}

public sealed class StockMovementRequestValidator : AbstractValidator<StockMovementRequest>
{
    public StockMovementRequestValidator()
    {
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.Quantity).NotEqual(0).InclusiveBetween(-10_000, 10_000);
        RuleFor(x => x.Quantity).GreaterThan(0).When(x => x.Kind is StockMovementKind.Receipt or StockMovementKind.Wastage)
            .WithMessage("Enter the number of units received or wasted.");
        RuleFor(x => x.Reason).NotEmpty().When(x => x.Kind != StockMovementKind.Receipt).WithMessage("Give a reason for adjustments and wastage.");
        RuleFor(x => x.Reason).SafeText(300);
    }
}

public sealed class CreateSaleRequestValidator : AbstractValidator<CreateSaleRequest>
{
    public CreateSaleRequestValidator()
    {
        RuleFor(x => x.CustomerType).IsInEnum();
        RuleFor(x => x.PaymentMode).IsInEnum();
        RuleFor(x => x.CustomerName).MaximumLength(200);
        RuleFor(x => x.EmployeeName).MaximumLength(200);
        RuleFor(x => x.PaymentReference).MaximumLength(64);
        RuleFor(x => x.EmployeeCode).Must(StaffCreditRules.IsValidEmployeeCode)
            .When(x => x.PaymentMode == CanteenPaymentMode.StaffCredit || !string.IsNullOrWhiteSpace(x.EmployeeCode))
            .WithMessage("Enter the employee code (letters, digits, '/' or '-').");
        RuleFor(x => x.CustomerType).Equal(CanteenCustomerType.Staff).When(x => x.PaymentMode == CanteenPaymentMode.StaffCredit)
            .WithMessage("Only staff can buy on credit.");
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Add at least one item.");
        RuleFor(x => x.Lines).Must(lines => lines is null || lines.Count <= 50).WithMessage("A bill can have at most 50 lines.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.MenuItemId).NotEmpty();
            line.RuleFor(l => l.Quantity).InclusiveBetween(1, 100);
        });
    }
}

public sealed class CancelSaleRequestValidator : AbstractValidator<CancelSaleRequest>
{
    public CancelSaleRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).SafeText(300);
}

public sealed class SettleStaffCreditRequestValidator : AbstractValidator<SettleStaffCreditRequest>
{
    public SettleStaffCreditRequestValidator()
    {
        RuleFor(x => x.Period).Must(p => StaffCreditRules.TryParsePeriod(p, out _)).WithMessage("Use the month as yyyy-MM.");
        RuleFor(x => x.Reference).NotEmpty().MaximumLength(64).WithMessage("Enter the payroll run reference.");
        RuleForEach(x => x.EmployeeCodes).Must(StaffCreditRules.IsValidEmployeeCode).WithMessage("Enter valid employee codes.");
    }
}

public sealed class StaffPaymentRequestValidator : AbstractValidator<StaffPaymentRequest>
{
    public StaffPaymentRequestValidator()
    {
        RuleFor(x => x.EmployeeCode).Must(StaffCreditRules.IsValidEmployeeCode).WithMessage("Enter a valid employee code.");
        RuleFor(x => x.Amount).GreaterThan(0).LessThanOrEqualTo(100_000).PrecisionScale(10, 2, true);
        RuleFor(x => x.Reference).MaximumLength(64);
    }
}
