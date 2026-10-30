using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>Prescribers search the drug master without inventory permissions (ERX-01).</summary>
public sealed class DrugSearchTests
{
    [Theory]
    [InlineData(null, 20)]
    [InlineData(15, 15)]
    [InlineData(50, 50)]
    [InlineData(51, 50)]
    [InlineData(1000, 50)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public void Take_defaults_to_twenty_and_is_capped_at_fifty(int? take, int expected) => DrugSearch.Take(take).ShouldBe(expected);

    [Fact]
    public void Only_active_drugs_are_listed_in_name_order_up_to_take()
    {
        Item[] items =
        [
            Drug("DRG-003", "Pantocid 40"),
            Drug("DRG-001", "Azithral 500"),
            Drug("DRG-002", "Dolo 650"),
            Drug("DRG-004", "Crocin 500", active: false),
            new() { Code = "CON-001", Name = "Bandage roll", Type = ItemType.Consumable, IsActive = true },
        ];

        var all = DrugSearch.Query(items.AsQueryable(), null, null).ToList();
        var firstTwo = DrugSearch.Query(items.AsQueryable(), null, 2).ToList();

        all.Select(d => d.Name).ShouldBe(["Azithral 500", "Dolo 650", "Pantocid 40"]);
        firstTwo.Select(d => d.Name).ShouldBe(["Azithral 500", "Dolo 650"]);
    }

    [Fact]
    public void The_query_translates_to_sql_with_a_case_insensitive_search_and_a_text_sort()
    {
        using var db = ClinicalModelTests.CreateContext();

        var sql = DrugSearch.Query(db.Set<Item>().AsNoTracking(), "dolo", 500).ToQueryString();

        sql.ShouldContain("ILIKE");
        sql.ShouldContain("'Drug'");
        var orderBy = sql[sql.LastIndexOf("ORDER BY", StringComparison.Ordinal)..];
        orderBy.ShouldContain(".name");
        orderBy.ShouldNotContain("type"); // enums are stored as text: never sort on them
        orderBy.ShouldNotContain("schedule");
    }

    [Theory]
    [InlineData(DrugSchedule.None, false, false, null, false)]
    [InlineData(DrugSchedule.Otc, false, false, null, false)]
    [InlineData(DrugSchedule.G, false, false, null, false)]
    [InlineData(DrugSchedule.H, false, false, null, true)]
    [InlineData(DrugSchedule.H1, false, true, "H1", true)]
    [InlineData(DrugSchedule.X, false, true, "X", true)]
    [InlineData(DrugSchedule.H, true, true, "H1", true)]
    [InlineData(DrugSchedule.X, true, true, "X", true)]
    public void Controlled_lines_are_schedule_H1_X_or_NDPS(DrugSchedule schedule, bool ndps, bool controlled, string? controlledSchedule, bool prescriptionOnly)
    {
        var drug = DrugSearch.ToDrug(Summary(schedule, ndps), null);

        drug.IsControlled.ShouldBe(controlled);
        drug.ControlledSchedule.ShouldBe(controlledSchedule);
        drug.RequiresPrescription.ShouldBe(prescriptionOnly);
    }

    [Fact]
    public void The_line_snapshot_and_stock_are_mapped()
    {
        var item = Summary(DrugSchedule.H, false);

        var drug = DrugSearch.ToDrug(item, new PharmacyStock(42, new DateOnly(2027, 3, 31)));

        drug.Id.ShouldBe(item.Id);
        drug.Code.ShouldBe("DRG-010");
        drug.Name.ShouldBe("Augmentin 625");
        drug.GenericName.ShouldBe("Amoxicillin + Clavulanic acid");
        drug.BrandName.ShouldBe("Augmentin");
        drug.Strength.ShouldBe("500 mg + 125 mg");
        drug.DosageForm.ShouldBe("Tablet");
        drug.Schedule.ShouldBe(DrugSchedule.H);
        drug.BaseUnit.ShouldBe("TAB");
        drug.Available.ShouldBe(42);
        drug.NearestExpiry.ShouldBe(new DateOnly(2027, 3, 31));
    }

    [Fact]
    public void Without_a_pharmacy_store_availability_is_unknown_rather_than_zero()
    {
        var drug = DrugSearch.ToDrug(Summary(DrugSchedule.H, false), null);

        drug.Available.ShouldBeNull();
        drug.NearestExpiry.ShouldBeNull();
    }

    [Fact]
    public void Stock_is_summed_over_the_pharmacy_stores_with_the_nearest_expiry()
    {
        var item = Guid.CreateVersion7();

        var stock = DrugSearch.Combine(
        [
            new StockAvailability(item, 30, 20, new DateOnly(2027, 6, 30), 12.5m),
            new StockAvailability(item, 10, 10, new DateOnly(2026, 12, 31), 12m),
            new StockAvailability(item, 0, 0, null, null),
        ]);

        stock.Available.ShouldBe(30);
        stock.NearestExpiry.ShouldBe(new DateOnly(2026, 12, 31));
    }

    [Fact]
    public void Out_of_stock_everywhere_is_zero() =>
        DrugSearch.Combine([new StockAvailability(Guid.CreateVersion7(), 5, 0, null, null)]).ShouldBe(new PharmacyStock(0, null));

    private static Item Drug(string code, string name, bool active = true) => new() { Code = code, Name = name, Type = ItemType.Drug, IsActive = active };

    private static ItemSummary Summary(DrugSchedule schedule, bool ndps) => new(
        Guid.CreateVersion7(), "DRG-010", "Augmentin 625", "Amoxicillin + Clavulanic acid", "Augmentin", "500 mg + 125 mg", "Tablet", ItemType.Drug, schedule,
        ndps, false, "TAB", "30042019", 12, true, Manufacturer: null, Specification: null, CeilingPrice: null);
}
