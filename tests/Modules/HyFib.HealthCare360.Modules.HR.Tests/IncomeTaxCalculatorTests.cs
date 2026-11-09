using HyFib.HealthCare360.Modules.HR.Domain.Statutory;
using HyFib.HealthCare360.Modules.HR.Services;

namespace HyFib.HealthCare360.Modules.HR.Tests;

/// <summary>
/// Worked examples checked against the Finance Act 2025 explanatory memorandum and Budget speech figures (₹12 lakh of
/// income tax-free, ₹12.10 lakh paying ₹10,000 under marginal relief, ₹25 lakh paying ₹3,30,000), the Finance (No. 2) Act
/// 2024 rebate (₹7 lakh), and the Income Tax Department's tax calculator for the old regime.
/// </summary>
public sealed class IncomeTaxCalculatorTests
{
    private static readonly IncomeTaxYear Fy2025 = IncomeTaxRates.Find(2025)!;
    private static readonly IncomeTaxYear Fy2024 = IncomeTaxRates.Find(2024)!;

    private static IncomeTaxComputation New(IncomeTaxYear year, decimal gross) =>
        IncomeTaxCalculator.Compute(year, TaxRegime.New, new AnnualSalary(gross, 0, 0, 0, 0), TaxDeclarationAmounts.None, new DateOnly(1990, 1, 1));

    [Theory]
    [InlineData(1_275_000, 1_200_000, 60_000, 60_000, 0)] // salaried ₹12.75 lakh: ₹12 lakh after the standard deduction, all rebated
    [InlineData(1_285_000, 1_210_000, 61_500, 51_500, 10_400)] // marginal relief: tax limited to the ₹10,000 above ₹12 lakh, plus cess
    [InlineData(2_575_000, 2_500_000, 330_000, 0, 343_200)] // ₹25 lakh: ₹3,30,000 as in the Budget speech, plus 4% cess
    [InlineData(475_000, 400_000, 0, 0, 0)]
    public void New_regime_2025_26(double gross, double taxable, double slabTax, double rebate, double total)
    {
        var c = New(Fy2025, (decimal)gross);
        c.StandardDeduction.ShouldBe(75_000m);
        c.TaxableIncome.ShouldBe((decimal)taxable);
        c.TaxOnIncome.ShouldBe((decimal)slabTax);
        c.Rebate87A.ShouldBe((decimal)rebate);
        c.TotalTax.ShouldBe((decimal)total);
    }

    [Theory]
    [InlineData(775_000, 0)] // ₹7 lakh taxable: ₹20,000 tax, all rebated (limit ₹25,000)
    [InlineData(785_000, 10_400)] // ₹7.10 lakh: ₹21,000 tax cut to the ₹10,000 above ₹7 lakh, plus cess
    public void New_regime_2024_25_rebate_and_marginal_relief(double gross, double total) =>
        New(Fy2024, (decimal)gross).TotalTax.ShouldBe((decimal)total);

    [Fact]
    public void Surcharge_has_marginal_relief_just_above_fifty_lakh()
    {
        // ₹51 lakh taxable: tax ₹11,10,000, surcharge at 10% would be ₹1,11,000. Tax and surcharge cannot exceed the tax on
        // ₹50 lakh (₹10,80,000) plus the ₹1,00,000 above it: surcharge is ₹70,000.
        var c = New(Fy2025, 5_175_000);
        c.TaxableIncome.ShouldBe(5_100_000m);
        c.TaxOnIncome.ShouldBe(1_110_000m);
        c.Surcharge.ShouldBe(70_000m);
        c.Cess.ShouldBe(47_200m);
        c.TotalTax.ShouldBe(1_227_200m);
    }

    [Fact]
    public void Old_regime_with_80C_professional_tax_and_rounding()
    {
        // ₹10 lakh salary, PF ₹21,600 and ₹1,30,000 of other 80C investments (capped at ₹1,50,000), professional tax ₹2,400.
        var c = IncomeTaxCalculator.Compute(Fy2025, TaxRegime.Old, new AnnualSalary(1_000_000, 0, 0, 21_600, 2_400),
            new TaxDeclarationAmounts(Section80C: 130_000), new DateOnly(1990, 1, 1));

        c.StandardDeduction.ShouldBe(50_000m);
        c.ProfessionalTax.ShouldBe(2_400m);
        c.Deduction80C.ShouldBe(150_000m);
        c.TaxableIncome.ShouldBe(797_600m);
        c.TaxOnIncome.ShouldBe(72_020m); // 5% of ₹2.5 lakh + 20% of ₹2,97,600
        c.Cess.ShouldBe(2_880.80m);
        c.TotalTax.ShouldBe(74_900m); // ₹74,900.80 to the nearest ten rupees (s.288B)
    }

    [Fact]
    public void Old_regime_rebate_up_to_five_lakh()
    {
        var c = IncomeTaxCalculator.Compute(Fy2025, TaxRegime.Old, new AnnualSalary(550_000, 0, 0, 0, 0), TaxDeclarationAmounts.None, new DateOnly(1990, 1, 1));
        c.TaxableIncome.ShouldBe(500_000m);
        c.TaxOnIncome.ShouldBe(12_500m);
        c.Rebate87A.ShouldBe(12_500m);
        c.TotalTax.ShouldBe(0m);
    }

    [Fact]
    public void Old_regime_senior_citizen_has_a_three_lakh_exemption()
    {
        var salary = new AnnualSalary(600_000, 0, 0, 0, 0);
        IncomeTaxCalculator.Compute(Fy2025, TaxRegime.Old, salary, TaxDeclarationAmounts.None, new DateOnly(1960, 6, 1)).TotalTax.ShouldBe(20_800m);
        IncomeTaxCalculator.Compute(Fy2025, TaxRegime.Old, salary, TaxDeclarationAmounts.None, new DateOnly(1990, 6, 1)).TotalTax.ShouldBe(23_400m);
    }

    [Theory]
    [InlineData(1966, 4, 1, true)] // turns 60 on 1 April 2026, so attains it on 31 March 2026, inside FY 2025-26
    [InlineData(1966, 4, 2, false)]
    public void A_senior_is_sixty_at_any_time_in_the_year(int y, int m, int d, bool senior) =>
        IncomeTaxCalculator.IsSenior(new DateOnly(y, m, d), 2025).ShouldBe(senior);

    [Theory]
    [InlineData(240_000, 600_000, 240_000, true, 180_000)] // rent less a tenth of basic is the least
    [InlineData(240_000, 600_000, 240_000, false, 180_000)]
    [InlineData(240_000, 600_000, 360_000, false, 240_000)] // the allowance itself (and 40% of basic) is the least
    [InlineData(300_000, 600_000, 500_000, false, 240_000)] // 40% of basic outside the four metros
    [InlineData(240_000, 600_000, 0, true, 0)]
    public void House_rent_exemption_is_the_least_of_three(double hra, double basic, double rent, bool metro, double expected) =>
        IncomeTaxCalculator.HouseRentExemption((decimal)hra, (decimal)basic, (decimal)rent, metro).ShouldBe((decimal)expected);

    [Fact]
    public void The_new_regime_ignores_old_regime_deductions()
    {
        var declared = new TaxDeclarationAmounts(RentPaid: 300_000, Section80C: 150_000, Section80DSelf: 25_000, HousingLoanInterest: 200_000);
        var c = IncomeTaxCalculator.Compute(Fy2025, TaxRegime.New, new AnnualSalary(1_500_000, 600_000, 240_000, 21_600, 2_500), declared, null);
        (c.HouseRentExemption, c.ProfessionalTax, c.TotalDeductions, c.HousePropertyLoss).ShouldBe((0m, 0m, 0m, 0m));
        c.TaxableIncome.ShouldBe(1_425_000m);
    }

    [Fact]
    public void Every_rate_year_names_its_source_and_has_slabs_in_order()
    {
        foreach (var year in IncomeTaxRates.Years)
        {
            year.Source.ShouldNotBeNullOrWhiteSpace();
            foreach (var regime in new[] { year.New, year.Old })
            {
                regime.Slabs.Select(s => s.From).ShouldBeInOrder(SortDirection.Ascending);
                regime.Slabs[0].From.ShouldBe(0m);
            }
        }

        IncomeTaxRates.Years.Select(y => y.StartYear).ShouldBeUnique();
    }

    [Theory]
    [InlineData(2026, 3, 2025)]
    [InlineData(2026, 4, 2026)]
    public void Financial_year_runs_april_to_march(int year, int month, int fy) => IncomeTaxRates.FinancialYearOf(year, month).ShouldBe(fy);
}

public sealed class TdsProjectorTests
{
    private static readonly IncomeTaxYear Fy2025 = IncomeTaxRates.Find(2025)!;
    private static readonly MonthSalary Month = new(150_000, 60_000, 24_000, 1_800, 0);

    [Fact]
    public void Tax_for_the_year_is_spread_over_the_months_left()
    {
        // ₹1.5 lakh a month: ₹18 lakh, ₹17.25 lakh taxable, tax ₹1,45,000 + cess = ₹1,50,800. In April, a twelfth.
        var april = TdsProjector.Project(Fy2025, TaxRegime.New, MonthSalary.Zero, 0, Month, Month, 11, TaxDeclarationAmounts.None, null, true);
        april.AnnualTax.ShouldBe(150_800m);
        april.ThisMonth.ShouldBe(12_567m);

        // October, six months paid at ₹12,567: the ₹75,398 left over six months.
        var october = TdsProjector.Project(Fy2025, TaxRegime.New, Month.Times(6), 75_402, Month, Month, 5, TaxDeclarationAmounts.None, null, true);
        october.ThisMonth.ShouldBe(12_566m);
    }

    [Fact]
    public void A_raise_late_in_the_year_is_taxed_in_the_months_left()
    {
        var raised = Month with { Gross = 200_000 };
        var january = TdsProjector.Project(Fy2025, TaxRegime.New, Month.Times(9), 113_103, raised, raised, 2, TaxDeclarationAmounts.None, null, true);

        // ₹13.5 lakh paid + ₹6 lakh = ₹19.5 lakh, ₹18.75 lakh taxable: ₹1,75,000 + cess = ₹1,82,000; ₹68,897 left over 3 months.
        january.AnnualTax.ShouldBe(182_000m);
        january.ThisMonth.ShouldBe(22_966m);
    }

    [Fact]
    public void Previous_employer_tax_is_credited()
    {
        var declared = new TaxDeclarationAmounts(PreviousEmployerSalary: 300_000, PreviousEmployerTds: 20_000);
        var p = TdsProjector.Project(Fy2025, TaxRegime.New, MonthSalary.Zero, 0, Month, Month, 8, declared, null, true);
        p.Computation.GrossSalary.ShouldBe(1_650_000m);
        p.DeductedSoFar.ShouldBe(20_000m);
    }

    [Fact]
    public void Without_a_pan_tds_is_at_least_twenty_percent_but_nothing_when_no_tax_is_due()
    {
        var p = TdsProjector.Project(Fy2025, TaxRegime.New, MonthSalary.Zero, 0, Month, Month, 11, TaxDeclarationAmounts.None, null, false);
        p.WithoutPan.ShouldBeTrue();
        p.AnnualTax.ShouldBe(345_000m); // 20% of ₹17,25,000
        p.ThisMonth.ShouldBe(28_750m);

        var low = new MonthSalary(30_000, 15_000, 6_000, 1_800, 0);
        var none = TdsProjector.Project(Fy2025, TaxRegime.New, MonthSalary.Zero, 0, low, low, 11, TaxDeclarationAmounts.None, null, false);
        none.ThisMonth.ShouldBe(0m);
        none.WithoutPan.ShouldBeFalse();
    }

    [Fact]
    public void Over_deducted_tax_is_not_refunded_through_payroll()
    {
        var p = TdsProjector.Project(Fy2025, TaxRegime.New, Month.Times(6), 200_000, Month, Month, 5, TaxDeclarationAmounts.None, null, true);
        p.ThisMonth.ShouldBe(0m);
    }

    [Theory]
    [InlineData(2026, 4, null, 11)]
    [InlineData(2027, 3, null, 0)]
    [InlineData(2026, 4, "2026-06-15", 2)]
    [InlineData(2026, 12, "2026-12-10", 0)]
    public void Months_left_stop_at_march_or_the_last_working_day(int year, int month, string? leaving, int expected) =>
        PayrollTaxContext.MonthsLeftAfter(year, month, leaving is null ? null : DateOnly.Parse(leaving, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(expected);
}
