using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Analytics.Reports.Definitions;

/// <summary>PH-01/02: pharmacy counter sales, cost and gross margin.</summary>
internal sealed class PharmacySalesMarginReport : ReportDefinition
{
    private const string ByDay = "day";
    private const string ByItem = "item";
    private const string ByStore = "store";

    public override ReportDescriptor Descriptor { get; } = new(
        "pharmacy-sales-margin",
        "Pharmacy sales and margin",
        ReportCategory.Pharmacy,
        "Pharmacy counter and prescription sales with cost and gross margin, by day, medicine or pharmacy store.",
        "Invoices in the period, excluding cancelled invoices; returns are deducted. Sales excl. GST = taxable value of the quantity kept. Cost = quantity kept × the batch's purchase rate (excl. GST). Gross margin = sales excl. GST − cost; margin % = margin ÷ sales excl. GST.",
        Audience.Finance,
        [
            P.Period(),
            P.Facility(),
            P.Choice(ReportKeys.GroupBy, "Show by", [new(ByDay, "Day"), new(ByItem, "Medicine"), new(ByStore, "Pharmacy store")], ByDay),
            P.Store(),
        ],
        [
            C.Date("key", "Date"),
            C.Int("invoices", "Invoices"),
            C.Dec("quantity", "Quantity sold"),
            C.Money("sales", "Net sales (incl. GST)"),
            C.Money("taxable", "Sales excl. GST"),
            C.Money("cost", "Cost"),
            C.Money("margin", "Gross margin"),
            C.Pct("marginPercent", "Margin %"),
        ],
        new ReportChartHint(ReportChartType.Bar, "key", ["taxable", "margin"], TimeSeries: true));

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (start, end) = (ctx.Args.Start, ctx.Args.End);
        var storeId = ctx.Args.StoreId;
        var groupBy = ctx.Args.Text(ReportKeys.GroupBy) ?? ByDay;
        var lines =
            from si in ctx.Db.Set<PharmacySaleItem>().AsNoTracking()
            join s in ctx.Db.Set<PharmacySale>() on si.PharmacySaleId equals s.Id
            join b in ctx.Db.Set<StockBatch>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]) on si.BatchId equals b.Id
            where s.SoldAt >= start && s.SoldAt < end && s.Status != SaleStatus.Cancelled && (storeId == null || s.StoreId == storeId)
            select new
            {
                SaleId = s.Id,
                Day = DateOnly.FromDateTime(s.SoldAt.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime),
                s.StoreId,
                si.ItemId,
                Quantity = si.Quantity - si.ReturnedQuantity,
                Net = si.NetAmount - si.RefundedAmount,
                Taxable = si.Quantity == 0 ? 0m : si.TaxableAmount * (si.Quantity - si.ReturnedQuantity) / si.Quantity,
                Cost = (si.Quantity - si.ReturnedQuantity) * b.PurchaseRate,
            };

        List<MarginRow> groups = groupBy switch
        {
            ByItem => await ctx.ListAsync(
                lines.GroupBy(x => x.ItemId).Select(g => new MarginRow(
                    g.Key, null, g.Select(x => x.SaleId).Distinct().Count(), g.Sum(x => x.Quantity), g.Sum(x => x.Net), g.Sum(x => x.Taxable), g.Sum(x => x.Cost))),
                ct),
            ByStore => await ctx.ListAsync(
                lines.GroupBy(x => x.StoreId).Select(g => new MarginRow(
                    g.Key, null, g.Select(x => x.SaleId).Distinct().Count(), g.Sum(x => x.Quantity), g.Sum(x => x.Net), g.Sum(x => x.Taxable), g.Sum(x => x.Cost))),
                ct),
            _ => await ctx.ListAsync(
                lines.GroupBy(x => x.Day).Select(g => new MarginRow(
                    null, g.Key, g.Select(x => x.SaleId).Distinct().Count(), g.Sum(x => x.Quantity), g.Sum(x => x.Net), g.Sum(x => x.Taxable), g.Sum(x => x.Cost))),
                ct),
        };

        List<object?[]> rows;
        IReadOnlyList<ReportColumn>? columns = null;
        ReportChartHint? chart = null;
        List<string?>? rowKeys = null;
        ReportDrillDown? drillDown = null;
        if (groupBy == ByDay)
        {
            var byDay = groups.Where(g => g.Day is not null).ToDictionary(g => g.Day!.Value);
            rows = Trend.Buckets(ctx.Args.From, ctx.Args.To, Trend.Day)
                .Select(day => byDay.TryGetValue(day, out var g) ? Row(day, g) : [day, 0, 0m, 0m, 0m, 0m, 0m, null])
                .ToList();
        }
        else
        {
            var names = groupBy == ByItem
                ? await Names.ItemsAsync(ctx, groups.Select(g => g.Key), ct)
                : await Names.StoresAsync(ctx, groups.Select(g => g.Key), ct);
            rows = groups.Select(g => Row(Labels.Name(names, g.Key), g)).ToList();
            columns = [C.Text("key", groupBy == ByItem ? "Medicine" : "Pharmacy store"), .. Descriptor.Columns.Skip(1)];
            if (groupBy == ByStore)
            {
                rowKeys = groups.Select(g => g.Key?.ToString()).ToList();
                drillDown = new ReportDrillDown("pharmacy-sales-margin", ReportKeys.Store, "See medicines", new Dictionary<string, string> { [ReportKeys.GroupBy] = ByItem });
            }
            chart = new ReportChartHint(ReportChartType.Bar, "key", ["taxable", "margin"]);
        }

        var taxable = groups.Sum(g => g.Taxable);
        var cost = groups.Sum(g => g.Cost);
        return new ReportData(rows)
        {
            Columns = columns,
            Chart = chart,
            RowKeys = rowKeys,
            DrillDown = drillDown,
            Totals = ["Total", null, groups.Sum(g => g.Quantity), groups.Sum(g => g.Net), Figures.Round2(taxable), Figures.Round2(cost), Figures.Round2(taxable - cost), KpiMath.MarginPercent(taxable, cost)],
            Summary =
            [
                new("sales", "Net sales", groups.Sum(g => g.Net), ReportValueType.Money),
                new("margin", "Gross margin", Figures.Round2(taxable - cost), ReportValueType.Money),
                new("marginPercent", "Margin %", KpiMath.MarginPercent(taxable, cost), ReportValueType.Percent),
            ],
            DefaultSort = groupBy == ByDay ? null : "sales",
            DefaultSortDescending = true,
        };
    }

    private static object?[] Row(object key, MarginRow g) =>
    [
        key, g.Invoices, g.Quantity, g.Net, Figures.Round2(g.Taxable), Figures.Round2(g.Cost), Figures.Round2(g.Taxable - g.Cost), KpiMath.MarginPercent(g.Taxable, g.Cost),
    ];

    private sealed record MarginRow(Guid? Key, DateOnly? Day, int Invoices, decimal Quantity, decimal Net, decimal Taxable, decimal Cost);
}

/// <summary>PH-04 / INV-08: near-expiry and expired stock with value.</summary>
internal sealed class NearExpiryStockReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "near-expiry-stock",
        "Near-expiry and expired stock",
        ReportCategory.Pharmacy,
        "Batches in stock that have expired or expire within the chosen number of days, with their value, soonest first.",
        "Batches with stock on hand whose expiry date is within the chosen days from today (expired batches included). Value at cost = quantity × purchase rate (excl. GST); value at MRP = quantity × MRP.",
        Audience.Stock,
        [P.Facility(), P.Store(), P.Number(ReportKeys.Days, "Expiring within (days)", 90, 1, 365)],
        [
            C.Text("store", "Store"),
            C.Text("item", "Item"),
            C.Text("batch", "Batch"),
            C.Date("expiry", "Expiry"),
            C.Int("daysLeft", "Days left"),
            C.Dec("quantity", "Quantity"),
            C.Money("valueAtCost", "Value at cost"),
            C.Money("valueAtMrp", "Value at MRP"),
            C.Text("status", "Status"),
        ],
        null,
        DefaultSort: "expiry");

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var today = ctx.Today;
        var limit = today.AddDays(ctx.Args.NumberOf(ReportKeys.Days) ?? 90);
        var storeId = ctx.Args.StoreId;
        var batches = await ctx.ListAsync(
            ctx.Db.Set<StockBatch>().AsNoTracking()
                .Where(b => b.QuantityOnHand > 0 && b.ExpiryDate != null && b.ExpiryDate <= limit && (storeId == null || b.StoreId == storeId))
                .OrderBy(b => b.ExpiryDate)
                .Select(b => new BatchRow(b.StoreId, b.ItemId, b.BatchNumber, b.ExpiryDate!.Value, b.QuantityOnHand, b.QuantityOnHand * b.PurchaseRate, b.QuantityOnHand * b.Mrp, b.IsBlocked)),
            ct);

        var stores = await Names.StoresAsync(ctx, batches.Select(b => (Guid?)b.StoreId), ct);
        var items = await Names.ItemsAsync(ctx, batches.Select(b => (Guid?)b.ItemId), ct);
        var rows = batches
            .Select(b =>
            {
                var daysLeft = b.Expiry.DayNumber - today.DayNumber;
                var status = daysLeft < 0 ? "Expired" : b.IsBlocked ? "Blocked" : daysLeft == 0 ? "Expires today" : $"Expires in {daysLeft} days";
                return new object?[]
                {
                    Labels.Name(stores, b.StoreId), Labels.Name(items, b.ItemId), b.BatchNumber, b.Expiry, daysLeft, b.Quantity, Figures.Round2(b.Cost),
                    Figures.Round2(b.Mrp), status,
                };
            })
            .ToList();

        var expired = batches.Where(b => b.Expiry < today).ToList();
        var within30 = batches.Where(b => b.Expiry >= today && b.Expiry <= today.AddDays(30)).ToList();
        return new ReportData(rows)
        {
            Totals = ["Total", null, null, null, null, batches.Sum(b => b.Quantity), Figures.Round2(batches.Sum(b => b.Cost)), Figures.Round2(batches.Sum(b => b.Mrp)), null],
            Summary =
            [
                new("batches", "Batches", batches.Count, ReportValueType.Integer),
                new("valueAtCost", "Value at cost", Figures.Round2(batches.Sum(b => b.Cost)), ReportValueType.Money),
                new("expired", "Already expired (cost)", Figures.Round2(expired.Sum(b => b.Cost)), ReportValueType.Money),
                new("within30", "Expiring in 30 days (cost)", Figures.Round2(within30.Sum(b => b.Cost)), ReportValueType.Money),
            ],
        };
    }

    private sealed record BatchRow(Guid StoreId, Guid ItemId, string BatchNumber, DateOnly Expiry, decimal Quantity, decimal Cost, decimal Mrp, bool IsBlocked);
}

/// <summary>INV-08 (FSN): dead or non-moving stock.</summary>
internal sealed class DeadStockReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "dead-stock",
        "Dead and non-moving stock",
        ReportCategory.Pharmacy,
        "Items in stock that have not been issued, sold, dispensed or consumed in the chosen number of days, by value.",
        "Items with stock on hand in a store and no issue, sale, dispense or consumption from that store in the last N days. Value at cost = quantity × purchase rate. Last issued = the latest such movement ever recorded.",
        Audience.Stock,
        [P.Facility(), P.Store(), P.Number(ReportKeys.Days, "No movement for (days)", 90, 15, 730)],
        [
            C.Text("store", "Store"),
            C.Text("item", "Item"),
            C.Dec("quantity", "Quantity"),
            C.Money("valueAtCost", "Value at cost"),
            C.Date("oldestReceipt", "Oldest batch received"),
            C.Date("lastIssued", "Last issued"),
            C.Int("daysIdle", "Days without movement"),
        ],
        new ReportChartHint(ReportChartType.Bar, "item", ["valueAtCost"]),
        DefaultSort: "valueAtCost",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var today = ctx.Today;
        var days = ctx.Args.NumberOf(ReportKeys.Days) ?? 90;
        var cutoff = AnalyticsClock.StartOfDay(today.AddDays(-days));
        var storeId = ctx.Args.StoreId;
        var consumption = ctx.Db.Set<StockLedgerEntry>().AsNoTracking()
            .Where(e => e.MovementType == StockMovementType.Issue || e.MovementType == StockMovementType.Sale
                        || e.MovementType == StockMovementType.Dispense || e.MovementType == StockMovementType.Consumption);
        var stock = ctx.Db.Set<StockBatch>().AsNoTracking()
            .Where(b => b.QuantityOnHand > 0 && (storeId == null || b.StoreId == storeId))
            .GroupBy(b => new { b.StoreId, b.ItemId })
            .Select(g => new
            {
                g.Key.StoreId,
                g.Key.ItemId,
                Quantity = g.Sum(b => b.QuantityOnHand),
                Cost = g.Sum(b => b.QuantityOnHand * b.PurchaseRate),
                Oldest = g.Min(b => b.ReceivedOn),
            });
        var dead = await ctx.ListAsync(
            stock.Where(s => !consumption.Any(e => e.StoreId == s.StoreId && e.ItemId == s.ItemId && e.OccurredAt >= cutoff))
                .OrderByDescending(s => s.Cost)
                .Select(s => new DeadRow(
                    s.StoreId, s.ItemId, s.Quantity, s.Cost, s.Oldest,
                    consumption.Where(e => e.StoreId == s.StoreId && e.ItemId == s.ItemId).Max(e => (DateTimeOffset?)e.OccurredAt))),
            ct);

        var stores = await Names.StoresAsync(ctx, dead.Select(d => (Guid?)d.StoreId), ct);
        var items = await Names.ItemsAsync(ctx, dead.Select(d => (Guid?)d.ItemId), ct);
        var rows = dead
            .Select(d =>
            {
                var last = d.LastIssued is { } issued ? AnalyticsClock.LocalDate(issued) : (DateOnly?)null;
                var idleSince = last ?? d.Oldest;
                return new object?[]
                {
                    Labels.Name(stores, d.StoreId), Labels.Name(items, d.ItemId), d.Quantity, Figures.Round2(d.Cost), d.Oldest, last, today.DayNumber - idleSince.DayNumber,
                };
            })
            .ToList();

        return new ReportData(rows)
        {
            Totals = ["Total", null, null, Figures.Round2(dead.Sum(d => d.Cost)), null, null, null],
            Summary =
            [
                new("items", "Items", dead.Count, ReportValueType.Integer),
                new("valueAtCost", "Value at cost", Figures.Round2(dead.Sum(d => d.Cost)), ReportValueType.Money),
                new("neverIssued", "Never issued", dead.Count(d => d.LastIssued is null), ReportValueType.Integer),
            ],
        };
    }

    private sealed record DeadRow(Guid StoreId, Guid ItemId, decimal Quantity, decimal Cost, DateOnly Oldest, DateTimeOffset? LastIssued);
}

/// <summary>INV: purchases received against consumption.</summary>
internal sealed class PurchaseVsConsumptionReport : ReportDefinition
{
    private const string ByCategory = "category";
    private const string ByItem = "item";

    public override ReportDescriptor Descriptor { get; } = new(
        "purchase-vs-consumption",
        "Purchase vs consumption",
        ReportCategory.Pharmacy,
        "Stock received against stock consumed in the period, by item category or item, at cost.",
        "Purchased = goods receipts (GRN) in the period. Consumed = issues, sales, dispenses and consumption, less returns to the store; transfers between stores are excluded. Values at the ledger's unit cost. Consumed ÷ purchased above 100% means stock is being run down.",
        Audience.Stock,
        [
            P.Period(),
            P.Facility(),
            P.Choice(ReportKeys.GroupBy, "Show by", [new(ByCategory, "Item category"), new(ByItem, "Item")], ByCategory),
            P.Store(),
        ],
        [
            C.Text("key", "Item category"),
            C.Dec("purchasedQty", "Purchased qty"),
            C.Money("purchased", "Purchased value"),
            C.Dec("consumedQty", "Consumed qty"),
            C.Money("consumed", "Consumed value"),
            C.Money("difference", "Purchased − consumed"),
            C.Pct("ratio", "Consumed ÷ purchased %"),
        ],
        new ReportChartHint(ReportChartType.Bar, "key", ["purchased", "consumed"]),
        DefaultSort: "consumed",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (start, end) = (ctx.Args.Start, ctx.Args.End);
        var storeId = ctx.Args.StoreId;
        var byItem = ctx.Args.Text(ReportKeys.GroupBy) == ByItem;
        var flows =
            from e in ctx.Db.Set<StockLedgerEntry>().AsNoTracking()
            join i in ctx.Db.Set<Item>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]) on e.ItemId equals i.Id
            where e.OccurredAt >= start && e.OccurredAt < end && (storeId == null || e.StoreId == storeId)
                  && (e.MovementType == StockMovementType.Grn || e.MovementType == StockMovementType.Issue || e.MovementType == StockMovementType.Sale
                      || e.MovementType == StockMovementType.Dispense || e.MovementType == StockMovementType.Consumption || e.MovementType == StockMovementType.Return)
            select new
            {
                Key = byItem ? (Guid?)e.ItemId : i.CategoryId,
                IsPurchase = e.MovementType == StockMovementType.Grn,
                e.Quantity,
                Value = e.Quantity * e.UnitCost,
            };
        var groups = await ctx.ListAsync(
            flows.GroupBy(f => f.Key)
                .Select(g => new FlowRow(
                    g.Key,
                    g.Sum(f => f.IsPurchase ? f.Quantity : 0m),
                    g.Sum(f => f.IsPurchase ? f.Value : 0m),
                    -g.Sum(f => f.IsPurchase ? 0m : f.Quantity),
                    -g.Sum(f => f.IsPurchase ? 0m : f.Value))),
            ct);

        var names = byItem ? await Names.ItemsAsync(ctx, groups.Select(g => g.Key), ct) : await Names.ItemCategoriesAsync(ctx, groups.Select(g => g.Key), ct);
        var rows = groups
            .Select(g => new object?[]
            {
                Labels.Name(names, g.Key, byItem ? "Unknown item" : "Uncategorised"), g.PurchasedQty, Figures.Round2(g.Purchased), g.ConsumedQty, Figures.Round2(g.Consumed),
                Figures.Round2(g.Purchased - g.Consumed), KpiMath.Percent(g.Consumed, g.Purchased),
            })
            .ToList();

        var purchased = groups.Sum(g => g.Purchased);
        var consumed = groups.Sum(g => g.Consumed);
        return new ReportData(rows)
        {
            Columns = byItem ? [C.Text("key", "Item"), .. Descriptor.Columns.Skip(1)] : null,
            Totals = ["Total", null, Figures.Round2(purchased), null, Figures.Round2(consumed), Figures.Round2(purchased - consumed), KpiMath.Percent(consumed, purchased)],
            Summary =
            [
                new("purchased", "Purchased", Figures.Round2(purchased), ReportValueType.Money),
                new("consumed", "Consumed", Figures.Round2(consumed), ReportValueType.Money),
                new("ratio", "Consumed ÷ purchased", KpiMath.Percent(consumed, purchased), ReportValueType.Percent),
            ],
        };
    }

    private sealed record FlowRow(Guid? Key, decimal PurchasedQty, decimal Purchased, decimal ConsumedQty, decimal Consumed);
}
