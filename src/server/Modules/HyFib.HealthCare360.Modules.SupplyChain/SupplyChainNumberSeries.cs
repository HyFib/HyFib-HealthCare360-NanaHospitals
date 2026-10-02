using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SupplyChain;

/// <summary>The numbers the Supply chain module issues (administrators set their formats under Hospital setup).</summary>
public static class SupplyChainNumberSeries
{
    public static NumberSeriesDefinition PurchaseRequisition { get; } =
        new("supplychain.pr", "Purchase requisition", "Purchase", "{FAC}PR{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition PurchaseOrder { get; } =
        new("supplychain.po", "Purchase order", "Purchase", "{FAC}PO{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition GoodsReceipt { get; } =
        new("supplychain.grn", "Goods receipt (GRN)", "Purchase", "{FAC}GRN{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition Indent { get; } =
        new("supplychain.indent", "Store indent", "Stores and inventory", "{FAC}IND{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition Issue { get; } =
        new("supplychain.issue", "Store issue", "Stores and inventory", "{FAC}ISS{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition Transfer { get; } =
        new("supplychain.transfer", "Stock transfer", "Stores and inventory", "{FAC}STN{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition Adjustment { get; } =
        new("supplychain.adjustment", "Stock adjustment", "Stores and inventory", "{FAC}ADJ{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition StockCount { get; } =
        new("supplychain.count", "Stock count", "Stores and inventory", "{FAC}PSC{YY}{SEQ:5}", NumberResetPolicy.Yearly, MaxLength: 24);

    /// <summary>The label on each piece of equipment, looked up by scanning it (GET …/assets/by-tag/{tag}).</summary>
    public static NumberSeriesDefinition AssetTag { get; } =
        new("supplychain.asset", "Asset tag", "Equipment and assets", "{FAC}AS{SEQ:6}", NumberResetPolicy.Never, MaxLength: 24);

    public static NumberSeriesDefinition MaintenanceTicket { get; } =
        new("supplychain.ticket", "Maintenance ticket", "Equipment and assets", "{FAC}MT{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    internal static IEnumerable<NumberSeriesDefinition> All { get; } =
    [
        PurchaseRequisition, PurchaseOrder, GoodsReceipt, Indent, Issue, Transfer, Adjustment, StockCount, AssetTag, MaintenanceTicket,
    ];
}
