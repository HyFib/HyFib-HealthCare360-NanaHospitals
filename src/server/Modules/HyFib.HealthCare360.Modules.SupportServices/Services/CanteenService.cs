using HyFib.HealthCare360.Modules.HR.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Canteen;
using HyFib.HealthCare360.Modules.SupportServices.Features.Canteen;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.SupportServices.Services;

/// <summary>
/// Canteen POS (CAN-02): bills numbered "{FAC}CN{YY}{SEQ:7}" with GST split and rupee rounding, staff credit sales
/// within the configured limit (CAN-03), stock for packaged items, same-day cancellation and the day-end report (CAN-04).
/// A bill is paid when it is made, so a cancellation pays money back: it waits for a second person who approves canteen
/// cancellations (maker-checker), never the cashier who sold it nor the one who asked, and the money goes back the way it
/// was paid (<see cref="CanteenSale.RefundWords"/>).
/// </summary>
public sealed class CanteenService(
    HospitalDbContext db,
    INumberSeries numbers,
    ISettingsService settings,
    IEmployeeDirectory employees,
    IApprovals approvals,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    /// <summary>The approval kind of a canteen bill's cancellation.</summary>
    public const string CancelKind = "canteen.sale_cancel";

    /// <summary>The cashier who sold the bill approves its cancellation.</summary>
    public const string CancelOwnSale = "canteen.cancel_own_sale";

    /// <summary>Facility setting (decimal): maximum outstanding staff credit per employee; 0 disables the limit. Default Rs. 3,000.</summary>
    public const string StaffCreditLimitSetting = "supportservices.canteen.staffCreditLimit";

    public const decimal DefaultStaffCreditLimit = 3000m;

    public async Task<Result<CanteenSaleDto>> CreateSaleAsync(CreateSaleRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var itemIds = request.Lines.Select(l => l.MenuItemId).Distinct().ToList();
        var items = await db.Set<MenuItem>().Where(m => itemIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        var unavailable = request.Lines.FirstOrDefault(l => !items.TryGetValue(l.MenuItemId, out var item) || !item.IsAvailable);
        if (unavailable is not null)
        {
            return Error.Rule("canteen.item_unavailable", "One of the items is not on today's menu. Refresh the menu and try again.");
        }

        var quantities = request.Lines.GroupBy(l => l.MenuItemId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        foreach (var (itemId, quantity) in quantities)
        {
            var consumed = items[itemId].Consume(quantity);
            if (consumed.IsFailure)
            {
                return consumed.Error!;
            }
        }

        if (request.PatientId is { } patientId && !await db.Set<Patient>().AnyAsync(p => p.Id == patientId, ct))
        {
            return Error.Field("patientId", "Patient not found.");
        }

        var totals = CanteenBillCalculator.Calculate(quantities.Select(q => items[q.Key].ToBillLine(q.Value)).ToList());

        var employeeCode = string.IsNullOrWhiteSpace(request.EmployeeCode) ? null : StaffCreditRules.NormalizeEmployeeCode(request.EmployeeCode);
        var employeeName = Clean(request.EmployeeName);
        if (request.PaymentMode == CanteenPaymentMode.StaffCredit)
        {
            // Credit only for current employees; the name printed on the bill comes from HR, not from the counter.
            var employee = employeeCode is null ? null : await employees.FindByCodeAsync(employeeCode, ct);
            if (employee is null)
            {
                return Error.Field("employeeCode", "No employee has this code. Check the staff ID card.");
            }

            if (!employee.IsActive)
            {
                return Error.Rule("canteen.employee_inactive", $"{employee.FullName} is not an active employee, so staff credit is not allowed.");
            }

            employeeName = employee.FullName;

            var outstanding = await db.Set<StaffCreditEntry>().Where(e => e.EmployeeCode == employeeCode).SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;
            var limit = await settings.GetAsync<decimal?>(StaffCreditLimitSetting, facilityId, ct) ?? DefaultStaffCreditLimit;
            var withinLimit = StaffCreditRules.CheckLimit(outstanding, totals.NetAmount, limit);
            if (withinLimit.IsFailure)
            {
                return withinLimit.Error!;
            }
        }

        var now = time.GetUtcNow();
        var today = time.Today();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var sale = new CanteenSale
        {
            FacilityId = facilityId,
            BillNumber = await numbers.NextAsync(SupportServicesNumberSeries.CanteenBill, facilityId, ct),
            SoldAt = now,
            BusinessDate = today,
            CustomerType = request.CustomerType,
            CustomerName = Clean(request.CustomerName),
            EmployeeCode = employeeCode,
            EmployeeName = employeeName,
            PatientId = request.PatientId,
            PaymentMode = request.PaymentMode,
            PaymentReference = Clean(request.PaymentReference),
            Status = CanteenSaleStatus.Completed,
            CashierName = currentUser.DisplayName,
        };
        sale.ApplyTotals(totals);
        db.Add(sale);

        if (request.PaymentMode == CanteenPaymentMode.StaffCredit)
        {
            db.Add(new StaffCreditEntry
            {
                FacilityId = facilityId,
                EmployeeCode = employeeCode!,
                EmployeeName = sale.EmployeeName,
                EntryDate = today,
                Kind = StaffCreditEntryKind.Sale,
                Amount = sale.NetAmount,
                SaleId = sale.Id,
                Reference = sale.BillNumber,
            });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("canteen.stock_changed", "Stock changed while billing (another counter sold the same item). Please bill again.");
        }

        await transaction.CommitAsync(ct);
        return ToDto(sale);
    }

    /// <summary>
    /// Asks for a bill to be cancelled. Nothing changes until someone else who approves canteen cancellations approves it,
    /// never the cashier who sold it (<see cref="CancelApprovedAsync"/>): then the money goes back the way it was paid and
    /// the stock goes back.
    /// </summary>
    public async Task<Result<ApprovalRequestDto>> AskCancelAsync(Guid saleId, string reason, CancellationToken ct)
    {
        var sale = await db.Set<CanteenSale>().Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == saleId, ct);
        if (sale is null)
        {
            return Error.NotFound("Canteen bill", saleId);
        }

        var today = time.Today();
        var cancellable = sale.CanCancel(today);
        if (cancellable.IsFailure)
        {
            return cancellable.Error!;
        }

        var soldBy = sale.CreatedBy is { } seller ? await db.Set<User>().Where(u => u.Id == seller).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) : null;
        List<ApprovalDetail> details =
        [
            new("Bill", $"{sale.BillNumber}, sold {DocumentFormat.DateTime(sale.SoldAt)} by {soldBy ?? sale.CashierName ?? "the counter"}"),
            new("Customer", Customer(sale)),
            new("Items", string.Join("; ", sale.Lines.Select(l => $"{l.ItemName} × {l.Quantity}"))),
            new("Amount", DocumentFormat.Money(sale.NetAmount)),
            new("Paid back", sale.RefundWords()),
            new("Stock", "Packaged items go back into stock"),
            new("Approve by", $"The end of {DocumentFormat.Date(today)}: a bill is cancelled only on the day it was sold."),
            new("Who decides", $"Someone who approves canteen cancellations, other than {soldBy ?? "the cashier"} who sold it and the one who asked."),
        ];
        var summary = Fit($"Cancel canteen bill {sale.BillNumber} ({Customer(sale)}): {sale.RefundWords()}", 300);
        return await approvals.RequestAsync(CancelKind, new ApprovalSubject(nameof(CanteenSale), sale.Id, sale.Version, sale.FacilityId, sale.PatientId),
            new CanteenCancelPayload(sale.Id, reason.Trim(), today, sale.PaymentMode, sale.NetAmount), summary, details, reason, ct);
    }

    /// <summary>
    /// Cancels an approved bill in the approval's transaction (the approval saves): only the bill as it was asked for, still
    /// cancellable today, and never by the cashier who sold it. Stock goes back; a staff credit sale is reversed in the ledger.
    /// </summary>
    internal async Task<Result<string>> CancelApprovedAsync(ApprovalRequest approval, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<CanteenCancelPayload>(approval.Payload);
        var sale = await db.Set<CanteenSale>().Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == asked.SaleId, ct);
        if (sale is null)
        {
            return Error.NotFound("Canteen bill", asked.SaleId);
        }

        if (sale.SoldBy(currentUser.UserId))
        {
            return Error.Forbidden(CancelOwnSale,
                $"You sold canteen bill {sale.BillNumber}, so you cannot approve its cancellation. Someone else who approves canteen cancellations decides it.");
        }

        if (sale.Version != approval.SubjectVersion || sale.PaymentMode != asked.PaymentMode || sale.NetAmount != asked.Amount)
        {
            return Error.Rule("approval.stale", $"Canteen bill {sale.BillNumber} changed after this was asked for, so nothing was changed. Ask again.");
        }

        var now = time.GetUtcNow();
        var approver = currentUser.DisplayName ?? currentUser.UserName ?? "Unknown user";
        var cancelled = sale.Cancel(asked.Reason, approval.RequestedBy, approval.RequestedByName, currentUser.UserId, approver, time.Today(), now);
        if (cancelled.IsFailure)
        {
            return Error.Rule("approval.stale", $"Canteen bill {sale.BillNumber} can no longer be cancelled, so nothing was changed: {cancelled.Error!.Message}");
        }

        var itemIds = sale.Lines.Select(l => l.MenuItemId).Distinct().ToList();
        var items = await db.Set<MenuItem>().Where(m => itemIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        foreach (var line in sale.Lines.Where(l => items.ContainsKey(l.MenuItemId)))
        {
            items[line.MenuItemId].Restock(line.Quantity);
        }

        if (sale.PaymentMode == CanteenPaymentMode.StaffCredit && sale.EmployeeCode is not null)
        {
            db.Add(new StaffCreditEntry
            {
                FacilityId = sale.FacilityId,
                EmployeeCode = sale.EmployeeCode,
                EmployeeName = sale.EmployeeName,
                EntryDate = sale.BusinessDate,
                Kind = StaffCreditEntryKind.Cancellation,
                Amount = -sale.NetAmount,
                SaleId = sale.Id,
                Reference = sale.BillNumber,
                Notes = sale.CancelReason,
            });
        }

        var outcome = $"Canteen bill {sale.BillNumber} cancelled: {sale.RefundWords()}";
        audit.Add(new AuditEntry(AuditCategories.Data, "canteen.sale_cancelled",
            Fit($"{outcome}. Asked for by {approval.RequestedByName}, approved by {approver}: {asked.Reason}", 1000))
        {
            PatientId = sale.PatientId,
            EntityType = nameof(CanteenSale),
            EntityId = sale.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { sale.BillNumber, sale.NetAmount, refundMode = sale.RefundMode, approval = approval.Id, requestedBy = approval.RequestedBy, approvedBy = currentUser.UserId },
        });
        return outcome;
    }

    /// <summary>The cancellation waiting for a bill, if any, and whether the reader sold it.</summary>
    public async Task<CanteenSaleDto> WithPendingAsync(CanteenSale sale, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var reader = currentUser.UserId;
        var pending = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.SubjectType == nameof(CanteenSale) && r.SubjectId == sale.Id && r.Kind == CancelKind && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderBy(r => r.RequestedAt)
            .Select(r => new CanteenPendingCancellation(r.Id, r.Summary, r.RequestedByName, r.RequestedAt, r.RequestedBy == reader))
            .FirstOrDefaultAsync(ct);
        return ToDto(sale) with { PendingCancellation = pending, OwnSale = sale.SoldBy(reader) };
    }

    private static string Customer(CanteenSale sale) => sale.CustomerType switch
    {
        CanteenCustomerType.Staff => sale.EmployeeName is null ? "Staff" : $"{sale.EmployeeName} ({sale.EmployeeCode})",
        _ => sale.CustomerName ?? sale.CustomerType.ToString(),
    };

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    public async Task<Result<MenuItemDto>> MoveStockAsync(Guid menuItemId, StockMovementRequest request, CancellationToken ct)
    {
        var item = await db.Set<MenuItem>().FirstOrDefaultAsync(m => m.Id == menuItemId, ct);
        if (item is null)
        {
            return Error.NotFound("Menu item", menuItemId);
        }

        if (!item.TrackStock)
        {
            return Error.Rule("canteen.stock_not_tracked", "Stock is not tracked for this item.");
        }

        var change = request.Kind == StockMovementKind.Wastage ? -request.Quantity : request.Quantity;
        if (item.StockOnHand + change < 0)
        {
            return Error.Rule("canteen.negative_stock", $"Only {item.StockOnHand} units are in stock.");
        }

        item.StockOnHand += change;
        db.Add(new CanteenStockMovement
        {
            FacilityId = item.FacilityId,
            MenuItemId = item.Id,
            Kind = request.Kind,
            Quantity = change,
            StockAfter = item.StockOnHand,
            Reason = Clean(request.Reason),
            MovedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        return ToDto(item);
    }

    public async Task<DailySalesSummary> DailySummaryAsync(DateOnly date, CancellationToken ct)
    {
        var sales = await db.Set<CanteenSale>().AsNoTracking().Include(s => s.Lines).Where(s => s.BusinessDate == date).ToListAsync(ct);
        var menu = await db.Set<MenuItem>().AsNoTracking().ToDictionaryAsync(m => m.Id, ct);
        return BuildDailySummary(date, sales, menu);
    }

    /// <summary>The day-end report from the day's bills; cancelled bills are only counted. The HSN/SAC summary uses the codes on the bills.</summary>
    internal static DailySalesSummary BuildDailySummary(DateOnly date, IReadOnlyList<CanteenSale> sales, IReadOnlyDictionary<Guid, MenuItem> menu)
    {
        var completed = sales.Where(s => s.Status == CanteenSaleStatus.Completed).ToList();
        var items = completed.SelectMany(s => s.Lines)
            .GroupBy(l => l.MenuItemId)
            .Select(g =>
            {
                var item = menu.GetValueOrDefault(g.Key);
                return new ItemConsumption(g.Key, g.First().ItemName, item?.Category, g.Sum(l => l.Quantity), g.Sum(l => l.LineTotal),
                    item is { TrackStock: true } ? item.StockOnHand : null);
            })
            .OrderByDescending(i => i.QuantitySold)
            .ToList();

        var byHsnSac = completed.SelectMany(s => s.Lines)
            .GroupBy(l => (l.HsnSac, l.GstRate))
            .Select(g => new HsnSacSummary(g.Key.HsnSac, g.Key.GstRate, g.Sum(l => l.Quantity), g.Sum(l => l.TaxableAmount), g.Sum(l => l.CgstAmount),
                g.Sum(l => l.SgstAmount), g.Sum(l => l.LineTotal)))
            .OrderBy(h => h.HsnSac is null)
            .ThenBy(h => h.HsnSac, StringComparer.Ordinal)
            .ThenBy(h => h.GstRate)
            .ToList();

        return new DailySalesSummary(
            date,
            completed.Count,
            sales.Count - completed.Count,
            completed.Sum(s => s.TaxableAmount),
            completed.Sum(s => s.CgstAmount),
            completed.Sum(s => s.SgstAmount),
            completed.Sum(s => s.RoundOff),
            completed.Sum(s => s.NetAmount),
            completed.GroupBy(s => s.PaymentMode.ToString()).Select(g => new AmountByKey(g.Key, g.Count(), g.Sum(s => s.NetAmount))).OrderBy(a => a.Key, StringComparer.Ordinal).ToList(),
            completed.GroupBy(s => s.CustomerType.ToString()).Select(g => new AmountByKey(g.Key, g.Count(), g.Sum(s => s.NetAmount))).OrderBy(a => a.Key, StringComparer.Ordinal).ToList(),
            items.GroupBy(i => i.Category?.ToString() ?? "Other").Select(g => new AmountByKey(g.Key, g.Count(), g.Sum(i => i.Amount))).OrderBy(a => a.Key, StringComparer.Ordinal).ToList(),
            items,
            byHsnSac);
    }

    internal static CanteenSaleDto ToDto(CanteenSale s) => new(
        s.Id, s.BillNumber, s.SoldAt, s.BusinessDate, s.CustomerType, s.CustomerName, s.EmployeeCode, s.EmployeeName, s.TaxableAmount, s.CgstAmount,
        s.SgstAmount, s.GrossAmount, s.RoundOff, s.NetAmount, s.PaymentMode, s.PaymentReference, s.Status, s.CashierName, s.CancelledAt, s.CancelReason,
        s.CancelRequestedByName, s.CancelledByName, s.RefundMode, s.RefundAmount, s.Version,
        s.Lines.Select(l => new CanteenSaleLineDto(l.MenuItemId, l.ItemName, l.HsnSac, l.Quantity, l.UnitPrice, l.GstRate, l.TaxableAmount, l.CgstAmount, l.SgstAmount,
            l.LineTotal)).ToList());

    internal static MenuItemDto ToDto(MenuItem m) => new(
        m.Id, m.Code, m.Name, m.Category, m.Price, m.GstRate, m.HsnSac, m.PriceIncludesGst, m.IsVeg, m.IsAvailable, m.TrackStock, m.StockOnHand,
        m.DisplayOrder, m.Description, m.Version);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>The cancellation asked for: the bill, why, the day it was asked on, and the money that goes back as it was paid.</summary>
public sealed record CanteenCancelPayload(Guid SaleId, string Reason, DateOnly AskedOn, CanteenPaymentMode PaymentMode, decimal Amount);

/// <summary>
/// A canteen bill cancelled after it was paid: one canteen user asks, and someone who approves canteen cancellations
/// (canteen managers, accountants and hospital administrators by default) approves, never the cashier who sold it.
/// </summary>
internal sealed class CanteenCancelApproval(CanteenService canteen) : IApprovalAction
{
    public string Kind => CanteenService.CancelKind;

    public string Title => "Canteen bill cancellation";

    public string ApprovePermission => SupportServicesPermissions.CanteenCancelApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => canteen.CancelApprovedAsync(request, ct);
}
