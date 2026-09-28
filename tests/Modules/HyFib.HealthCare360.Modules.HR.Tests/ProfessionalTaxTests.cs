using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Tests;

public sealed class ProfessionalTaxTests
{
    /// <summary>Karnataka: nil below ₹25,000 a month, ₹200 otherwise and ₹300 in February.</summary>
    private static readonly ProfessionalTaxSchedule Karnataka = new("KA", ProfessionalTaxFrequency.Monthly, [],
        [new ProfessionalTaxBand(0, 0), new ProfessionalTaxBand(25_000, 200, 300)]);

    /// <summary>Tamil Nadu (Greater Chennai): half-yearly income slabs, deducted in September and March.</summary>
    private static readonly ProfessionalTaxSchedule TamilNadu = new("TN", ProfessionalTaxFrequency.HalfYearly, [9, 3],
    [
        new ProfessionalTaxBand(0, 0), new ProfessionalTaxBand(21_001, 180), new ProfessionalTaxBand(30_001, 430),
        new ProfessionalTaxBand(45_001, 930), new ProfessionalTaxBand(60_001, 1_025), new ProfessionalTaxBand(75_001, 1_250),
    ]);

    [Theory]
    [InlineData(24_999, 9, 0)]
    [InlineData(25_000, 9, 200)]
    [InlineData(80_000, 9, 200)]
    [InlineData(25_000, 2, 300)]
    [InlineData(10_000, 2, 0)]
    public void Karnataka_is_monthly_with_a_february_top_up(double gross, int month, double expected) =>
        ProfessionalTaxCalculator.Compute(Karnataka, month, (decimal)gross).ShouldBe((decimal)expected);

    [Fact]
    public void Tamil_nadu_charges_nothing_outside_its_collection_months()
    {
        ProfessionalTaxCalculator.Compute(TamilNadu, 8, 50_000, halfYearIncome: 300_000).ShouldBe(0m);
    }

    [Theory]
    [InlineData(120_000, 1_250)]
    [InlineData(75_000, 1_025)]
    [InlineData(50_000, 930)]
    [InlineData(25_000, 180)]
    [InlineData(20_000, 0)]
    public void Tamil_nadu_uses_half_yearly_income_in_collection_months(double halfYearIncome, double expected)
    {
        ProfessionalTaxCalculator.Compute(TamilNadu, 9, 10_000, (decimal)halfYearIncome).ShouldBe((decimal)expected);
        ProfessionalTaxCalculator.Compute(TamilNadu, 3, 10_000, (decimal)halfYearIncome).ShouldBe((decimal)expected);
    }

    [Fact]
    public void Tamil_nadu_projects_six_months_when_history_is_unknown()
    {
        ProfessionalTaxCalculator.Compute(TamilNadu, 9, 8_000).ShouldBe(930m);
    }

    [Fact]
    public void No_schedule_or_no_salary_means_no_tax()
    {
        ProfessionalTaxCalculator.Compute(null, 9, 50_000).ShouldBe(0m);
        ProfessionalTaxCalculator.Compute(Karnataka, 9, 0).ShouldBe(0m);
    }

    [Fact]
    public void Half_years_run_april_to_september_and_october_to_march()
    {
        ProfessionalTaxCalculator.HalfYearMonths(2026, 9).ShouldBe([(2026, 4), (2026, 5), (2026, 6), (2026, 7), (2026, 8), (2026, 9)]);
        ProfessionalTaxCalculator.HalfYearMonths(2026, 3).ShouldBe([(2025, 10), (2025, 11), (2025, 12), (2026, 1), (2026, 2), (2026, 3)]);
        ProfessionalTaxCalculator.HalfYearMonths(2026, 11).ShouldBe([(2026, 10), (2026, 11)]);
        ProfessionalTaxCalculator.HalfYearMonths(2026, 4).ShouldBe([(2026, 4)]);
    }

    [Fact]
    public void A_valid_slab_table_passes_validation()
    {
        ProfessionalTaxCalculator.Validate(TamilNadu.Bands, ProfessionalTaxFrequency.HalfYearly, [9, 3]).ShouldBeEmpty();
        ProfessionalTaxCalculator.Validate(Karnataka.Bands, ProfessionalTaxFrequency.Monthly, []).ShouldBeEmpty();
    }

    [Fact]
    public void Invalid_slab_tables_are_explained()
    {
        ProfessionalTaxCalculator.Validate([], ProfessionalTaxFrequency.Monthly, []).ShouldNotBeEmpty();
        ProfessionalTaxCalculator.Validate([new(100, 0)], ProfessionalTaxFrequency.Monthly, []).ShouldContain("The first band must start at ₹0.");
        ProfessionalTaxCalculator.Validate([new(0, 0), new(0, 200)], ProfessionalTaxFrequency.Monthly, []).ShouldContain("Two bands start at the same income.");
        ProfessionalTaxCalculator.Validate([new(0, -1)], ProfessionalTaxFrequency.Monthly, []).ShouldContain("Amounts and incomes cannot be negative.");
        ProfessionalTaxCalculator.Validate([new(0, 0)], ProfessionalTaxFrequency.HalfYearly, []).ShouldNotBeEmpty();
        ProfessionalTaxCalculator.Validate([new(0, 0)], ProfessionalTaxFrequency.HalfYearly, [13]).ShouldNotBeEmpty();
    }

    [Fact]
    public void Schedules_are_built_from_stored_slabs_in_income_order()
    {
        var schedule = ProfessionalTaxSchedule.From(
        [
            new ProfessionalTaxSlab { StateCode = "KA", Frequency = ProfessionalTaxFrequency.Monthly, IncomeFrom = 25_000, Amount = 200, FebruaryAmount = 300 },
            new ProfessionalTaxSlab { StateCode = "KA", Frequency = ProfessionalTaxFrequency.Monthly, IncomeFrom = 0, Amount = 0 },
        ])!;

        schedule.StateCode.ShouldBe("KA");
        schedule.Bands.Select(b => b.IncomeFrom).ShouldBe([0m, 25_000m]);
        ProfessionalTaxSchedule.From([]).ShouldBeNull();
    }
}
