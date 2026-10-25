using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.SupplyChain;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace HyFib.HealthCare360.Modules.Pharmacy.Tests;

/// <summary>Builds the EF Core model with the modules Pharmacy depends on, so mapping errors surface before migrations.</summary>
public sealed class PharmacyModelTests
{
    private static readonly Lazy<IModel> Model = new(() =>
    {
        var options = new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new HospitalDbContext(options, new ModuleCatalog([new PatientsModule(), new SupplyChainModule(), new PharmacyModule()]));
        return db.GetService<IDesignTimeModel>().Model;
    });

    [Fact]
    public void Every_pharmacy_table_lives_in_the_pharmacy_schema()
    {
        var entities = Model.Value.GetEntityTypes().Where(e => e.ClrType.Namespace == typeof(PharmacySale).Namespace).ToList();

        entities.Count.ShouldBe(18);
        entities.ShouldAllBe(e => e.GetSchema() == "pharmacy");
    }

    [Fact]
    public void A_prescription_is_queued_once_while_refills_are_extra_entries()
    {
        var index = Model.Value.FindEntityType(typeof(PharmacyQueueEntry))!.GetIndexes()
            .Single(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(PharmacyQueueEntry.PrescriptionId));

        index.IsUnique.ShouldBeTrue();
        index.GetFilter().ShouldBe("refill_of_entry_id IS NULL");
    }

    [Fact]
    public void Register_has_exactly_one_line_per_stock_movement_and_invoices_are_unique()
    {
        Model.Value.FindEntityType(typeof(ControlledDrugRegisterEntry))!.GetIndexes()
            .ShouldContain(i => i.IsUnique && i.Properties.Count == 1 && i.Properties[0].Name == nameof(ControlledDrugRegisterEntry.LedgerEntryId));
        Model.Value.FindEntityType(typeof(PharmacySale))!.GetIndexes()
            .ShouldContain(i => i.IsUnique && i.Properties.Count == 1 && i.Properties[0].Name == nameof(PharmacySale.InvoiceNumber));
    }

    [Fact]
    public void Sales_and_queue_entries_use_optimistic_concurrency()
    {
        Model.Value.FindEntityType(typeof(PharmacySale))!.FindProperty(nameof(PharmacySale.Version))!.IsConcurrencyToken.ShouldBeTrue();
        Model.Value.FindEntityType(typeof(PharmacyQueueEntry))!.FindProperty(nameof(PharmacyQueueEntry.Version))!.IsConcurrencyToken.ShouldBeTrue();
    }

    [Fact]
    public void Nurses_can_only_raise_ward_indents_count_narcotics_and_report_reactions_by_default()
    {
        var permissions = new PharmacyModule().Permissions.ToList();

        permissions.Count.ShouldBe(11);
        permissions.Select(p => p.Name).Distinct().Count().ShouldBe(11);
        permissions.Where(p => p.DefaultRoles!.Contains("Nurse")).Select(p => p.Name)
            .ShouldBe([PharmacyPermissions.IpIndent, PharmacyPermissions.NdpsCount, PharmacyPermissions.AdrReport]);
        permissions.ShouldAllBe(p => p.DefaultRoles!.Contains("Pharmacist"));

        // The shift count is made by those who hold the narcotics, and reactions are reported by those who see them, not by the administration.
        permissions.Where(p => !p.DefaultRoles!.Contains("HospitalAdmin")).Select(p => p.Name)
            .ShouldBe([PharmacyPermissions.NdpsCount, PharmacyPermissions.AdrReport]);
    }
}
