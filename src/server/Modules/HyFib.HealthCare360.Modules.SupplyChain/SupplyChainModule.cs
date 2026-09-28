using HyFib.HealthCare360.Modules.SupplyChain.Features.Adjustments;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Assets;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Indents;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Items;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Maintenance;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Purchase;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Receipts;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Reports;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stock;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Transfers;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Vendors;
using HyFib.HealthCare360.Modules.SupplyChain.Services;

namespace HyFib.HealthCare360.Modules.SupplyChain;

public static class SupplyChainPermissions
{
    public const string ItemsManage = "inventory.items.manage";
    public const string StockRead = "inventory.stock.read";
    public const string IndentsRaise = "inventory.indents.raise";
    public const string IndentsIssue = "inventory.indents.issue";
    public const string Transfers = "inventory.transfers";
    public const string AdjustmentsApprove = "inventory.adjustments.approve";
    public const string Counts = "inventory.counts";
    public const string PurchaseManage = "purchase.manage";
    public const string PurchaseApprove = "purchase.approve";
    public const string GrnReceive = "purchase.grn.receive";
    public const string AssetsRead = "assets.read";
    public const string AssetsManage = "assets.manage";
    public const string AssetsMaintenance = "assets.maintenance";

    /// <summary>Everyone who picks items or stores on a screen (indents, POs, GRNs, counts, transfers).</summary>
    internal static readonly string[] CatalogReaders = [ItemsManage, StockRead, IndentsRaise, IndentsIssue, Transfers, Counts, PurchaseManage, GrnReceive];

    internal static readonly string[] VendorReaders = [PurchaseManage, PurchaseApprove, GrnReceive, AssetsManage, AssetsMaintenance];

    internal static readonly string[] AssetReaders = [AssetsRead, AssetsManage, AssetsMaintenance];

    private static readonly string[] StoreStaff = [SystemRoles.StoreKeeper, SystemRoles.HospitalAdmin];
    private static readonly string[] Biomedical = [SystemRoles.BiomedicalEngineer, SystemRoles.HospitalAdmin];

    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(ItemsManage, "Inventory", "Maintain the item master, categories and stores", [.. StoreStaff, SystemRoles.PurchaseOfficer]),
        new(StockRead, "Inventory", "View stock, batches, the stock ledger and inventory reports",
            [.. StoreStaff, SystemRoles.PurchaseOfficer, SystemRoles.Pharmacist]),

        // Every department indents; the pharmacy replenishes from the central store through indents too.
        new(IndentsRaise, "Inventory", "Raise indents (stock requests) to a store", [.. StoreStaff, SystemRoles.Nurse, SystemRoles.Pharmacist]),
        new(IndentsIssue, "Inventory", "Approve, reserve and issue indents from a store", StoreStaff),

        // PH-04: pharmacies move stock between OP and IP pharmacy and back to the central store.
        new(Transfers, "Inventory", "Dispatch and receive stock transfers between stores", [.. StoreStaff, SystemRoles.Pharmacist]),
        new(AdjustmentsApprove, "Inventory", "Approve stock adjustments and write-offs (not your own)", [SystemRoles.HospitalAdmin]),
        new(Counts, "Inventory", "Run physical stock counts and raise stock adjustments", StoreStaff),
        new(PurchaseManage, "Purchase", "Vendors, purchase requisitions and purchase orders", [.. StoreStaff, SystemRoles.PurchaseOfficer]),
        new(PurchaseApprove, "Purchase", "Approve purchase requisitions and purchase orders", [SystemRoles.HospitalAdmin]),
        new(GrnReceive, "Purchase", "Receive goods against vendor invoices (GRN)", StoreStaff),

        // Ward staff must be able to see equipment and report breakdowns (AST-04).
        new(AssetsRead, "Assets", "View the asset register and report equipment breakdowns",
            [.. Biomedical, SystemRoles.StoreKeeper, SystemRoles.PurchaseOfficer, SystemRoles.Nurse]),
        new(AssetsManage, "Assets", "Register assets and contracts; decide condemnation and disposal", Biomedical),
        new(AssetsMaintenance, "Assets", "Preventive maintenance, breakdown tickets and calibration", Biomedical),
    ];
}

/// <summary>Inventory, stores, purchase, indents, vendors and asset management (SRS INV-01..08, AST-01..06).</summary>
public sealed class SupplyChainModule : IModule
{
    public string Name => "supplychain";

    public IEnumerable<PermissionDefinition> Permissions => SupplyChainPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<StockService>();
        services.AddScoped<PurchaseService>();
        services.AddScoped<GoodsReceiptService>();
        services.AddScoped<IndentService>();
        services.AddScoped<TransferService>();
        services.AddScoped<AdjustmentService>();
        services.AddScoped<InventoryReportService>();
        services.AddScoped<AssetService>();
        services.AddScoped<MaintenanceService>();
        services.AddHostedService<AssetReminderJob>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ItemEndpoints.Map(endpoints);
        StoreEndpoints.Map(endpoints);
        StockEndpoints.Map(endpoints);
        InventoryReportEndpoints.Map(endpoints);
        IndentEndpoints.Map(endpoints);
        TransferEndpoints.Map(endpoints);
        AdjustmentEndpoints.Map(endpoints);
        VendorEndpoints.Map(endpoints);
        PurchaseEndpoints.Map(endpoints);
        GoodsReceiptEndpoints.Map(endpoints);
        AssetEndpoints.Map(endpoints);
        MaintenanceEndpoints.Map(endpoints);
    }
}
