using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace HyFib.HealthCare360.Modules.SupplyChain.Tests;

/// <summary>
/// Builds the real EF Core model (no database needed) so mapping mistakes surface before migrations are generated,
/// and pins the persistence rules the stock logic depends on (concurrency tokens, check constraint, unique indexes).
/// </summary>
public sealed class SupplyChainModelTests
{
    private static IModel DesignModel()
    {
        var options = new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new HospitalDbContext(options, new ModuleCatalog([new SupplyChainModule()]));
        return db.GetService<IDesignTimeModel>().Model;
    }

    private static readonly Lazy<IModel> Model = new(DesignModel);

    [Fact]
    public void Every_module_table_lives_in_the_supplychain_schema()
    {
        var entities = Model.Value.GetEntityTypes().Where(e => e.ClrType.Namespace == typeof(StockBatch).Namespace).ToList();

        entities.Count.ShouldBeGreaterThanOrEqualTo(28);
        entities.ShouldAllBe(e => e.GetSchema() == "supplychain");
    }

    [Fact]
    public void Stock_batches_use_optimistic_concurrency_and_a_non_negative_check()
    {
        var batch = Model.Value.FindEntityType(typeof(StockBatch))!;

        batch.FindProperty(nameof(StockBatch.Version))!.IsConcurrencyToken.ShouldBeTrue();
        batch.FindProperty(nameof(StockBatch.Version))!.GetColumnName().ShouldBe("xmin");
        batch.GetCheckConstraints().ShouldContain(c => c.Name == "ck_stock_batches_quantities");
        batch.FindProperty(nameof(StockBatch.QuantityOnHand))!.GetScale().ShouldBe(3);
        batch.FindProperty(nameof(StockBatch.Mrp))!.GetScale().ShouldBe(4);
        batch.GetIndexes().ShouldContain(i => i.IsUnique && i.Properties.Count == 5);
    }

    [Fact]
    public void Reservations_carry_their_own_concurrency_token()
    {
        Model.Value.FindEntityType(typeof(StockReservation))!.FindProperty(nameof(StockReservation.Version))!.IsConcurrencyToken.ShouldBeTrue();
    }

    [Fact]
    public void A_vendor_invoice_can_be_received_only_once()
    {
        var grn = Model.Value.FindEntityType(typeof(GoodsReceiptNote))!;

        grn.GetIndexes().ShouldContain(i => i.IsUnique && i.GetFilter() == "status <> 'Cancelled'");
    }

    [Fact]
    public void Enums_are_stored_as_readable_strings()
    {
        var movement = Model.Value.FindEntityType(typeof(StockLedgerEntry))!.FindProperty(nameof(StockLedgerEntry.MovementType))!;

        movement.GetProviderClrType().ShouldBe(typeof(string));
    }

    [Fact]
    public void Permissions_are_unique_and_granted_to_default_roles()
    {
        var permissions = new SupplyChainModule().Permissions.ToList();

        permissions.Count.ShouldBe(13);
        permissions.Select(p => p.Name).Distinct().Count().ShouldBe(permissions.Count);
        permissions.ShouldAllBe(p => p.DefaultRoles != null && p.DefaultRoles.Length > 0);
        permissions.Single(p => p.Name == SupplyChainPermissions.StockRead).DefaultRoles!.ShouldContain("Pharmacist");
        permissions.Single(p => p.Name == SupplyChainPermissions.IndentsRaise).DefaultRoles!.ShouldContain("Nurse");
        permissions.Single(p => p.Name == SupplyChainPermissions.AssetsMaintenance).DefaultRoles!.ShouldContain("BiomedicalEngineer");
    }
}
