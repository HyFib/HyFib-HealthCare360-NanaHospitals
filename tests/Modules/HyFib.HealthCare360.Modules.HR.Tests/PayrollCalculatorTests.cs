using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Tests;

public sealed class PayrollCalculatorTests
{
    private static readonly ProfessionalTaxSchedule Karnataka = new("KA", ProfessionalTaxFrequency.Monthly, [],
        [new ProfessionalTaxBand(0, 0), new ProfessionalTaxBand(25_000, 200, 300)]);

    private static readonly ProfessionalTaxSchedule TamilNadu = new("TN", ProfessionalTaxFrequency.HalfYearly, [9, 3],
    [
        new ProfessionalTaxBand(0, 0), new ProfessionalTaxBand(21_001, 180), new ProfessionalTaxBand(30_001, 430),
        new ProfessionalTaxBand(45_001, 930), new ProfessionalTaxBand(60_001, 1_025), new ProfessionalTaxBand(75_001, 1_250),
    ]);

    private static List<SalaryLineInput> Structure(decimal basic, params SalaryLineInput[] extra) =>
    [
        new("BASIC", "Basic Salary", SalaryComponentKind.Earning, SalaryCalculationType.Fixed, basic, 10),
        new("HRA", "House Rent Allowance", SalaryComponentKind.Earning, SalaryCalculationType.PercentOfBasic, 40, 20, WageTreatment.Excluded),
        new("CONV", "Conveyance", SalaryComponentKind.Earning, SalaryCalculationType.Fixed, 1_600, 30, WageTreatment.Excluded),
        .. extra,
    ];

    private static SalaryLineInput Special(decimal amount) =>
        new("SPECIAL", "Special Allowance", SalaryComponentKind.Earning, SalaryCalculationType.Fixed, amount, 50);

    private static PayrollCalculationResult Calculate(
        List<SalaryLineInput> lines,
        decimal payableDays = 30,
        ProfessionalTaxSchedule? pt = null,
        decimal tds = 0,
        decimal? tdsOverride = null,
        bool pf = true,
        bool esi = true,
        decimal? earlierHalfYear = null,
        bool pfOnActual = false,
        params PayrollAdjustmentInput[] adjustments) =>
        PayrollCalculator.Calculate(new PayrollCalculationInput(
            2026, 9, 30, payableDays, lines, adjustments, new StatutoryProfile(pf, pfOnActual, esi, pt), tds, tdsOverride, earlierHalfYear));

    private static decimal Line(PayrollCalculationResult result, string code) => result.Lines.Single(l => l.Code == code).Amount;

    [Fact]
    public void Full_month_salary_with_pf_capped_pt_and_tds()
    {
        var result = Calculate(Structure(20_000), pt: Karnataka, tds: 500);

        Line(result, "BASIC").ShouldBe(20_000m);
        Line(result, "HRA").ShouldBe(8_000m);
        Line(result, "CONV").ShouldBe(1_600m);
        result.GrossEarnings.ShouldBe(29_600m);
        result.StandardGross.ShouldBe(29_600m);
        result.PfWage.ShouldBe(15_000m);
        result.EmployeePf.ShouldBe(1_800m);
        result.EmployerPf.ShouldBe(1_800m);
        result.EmployeeEsi.ShouldBe(0m);
        result.ProfessionalTax.ShouldBe(200m);
        result.Tds.ShouldBe(500m);
        result.TotalDeductions.ShouldBe(2_500m);
        result.NetPay.ShouldBe(27_100m);
        result.HasErrors.ShouldBeFalse();
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Loss_of_pay_prorates_every_earning_by_calendar_days()
    {
        var result = Calculate(Structure(20_000), payableDays: 27, pt: Karnataka, tds: 500);

        Line(result, "BASIC").ShouldBe(18_000m);
        Line(result, "HRA").ShouldBe(7_200m);
        Line(result, "CONV").ShouldBe(1_440m);
        result.GrossEarnings.ShouldBe(26_640m);
        result.StandardGross.ShouldBe(29_600m);
        result.EmployeePf.ShouldBe(1_800m);
        result.NetPay.ShouldBe(24_140m);
        result.Lines.Single(l => l.Code == "BASIC").FullAmount.ShouldBe(20_000m);
    }

    [Fact]
    public void Esi_applies_when_contracted_gross_is_within_the_ceiling()
    {
        var result = Calculate(Structure(10_000), pt: TamilNadu, earlierHalfYear: 78_000);

        result.GrossEarnings.ShouldBe(15_600m);
        result.EmployeePf.ShouldBe(1_200m);
        result.EsiWage.ShouldBe(15_600m);
        result.EmployeeEsi.ShouldBe(117m);
        result.EmployerEsi.ShouldBe(507m);
        result.ProfessionalTax.ShouldBe(1_250m);
        result.TotalDeductions.ShouldBe(2_567m);
        result.NetPay.ShouldBe(13_033m);
    }

    [Fact]
    public void Esi_coverage_is_judged_on_contracted_not_earned_gross()
    {
        var result = Calculate(Structure(15_000), payableDays: 20);

        result.StandardGross.ShouldBe(22_600m);
        result.GrossEarnings.ShouldBeLessThan(21_000m);
        result.EmployeeEsi.ShouldBe(0m);
        result.Lines.ShouldNotContain(l => l.Code == "ESI");
    }

    [Fact]
    public void One_off_inputs_are_added_without_proration()
    {
        var result = Calculate(Structure(20_000), payableDays: 15, pf: false,
            adjustments:
            [
                new PayrollAdjustmentInput("ARREARS", "Increment arrears", SalaryComponentKind.Earning, 1_000),
                new PayrollAdjustmentInput("CANTEEN", "Canteen credit", SalaryComponentKind.Deduction, 850),
            ]);

        Line(result, "ARREARS").ShouldBe(1_000m);
        Line(result, "CANTEEN").ShouldBe(850m);
        result.GrossEarnings.ShouldBe(14_800m + 1_000m);
        result.TotalDeductions.ShouldBe(850m);
        result.NetPay.ShouldBe(14_950m);
    }

    [Fact]
    public void Structure_deductions_are_not_prorated_but_percentages_follow_earned_basic()
    {
        var result = Calculate(
            Structure(20_000,
                new SalaryLineInput("LOAN", "Loan", SalaryComponentKind.Deduction, SalaryCalculationType.Fixed, 2_000, 1),
                new SalaryLineInput("WELFARE", "Welfare fund", SalaryComponentKind.Deduction, SalaryCalculationType.PercentOfBasic, 1, 2)),
            payableDays: 15, pf: false);

        Line(result, "LOAN").ShouldBe(2_000m);
        Line(result, "WELFARE").ShouldBe(100m);
    }

    [Fact]
    public void A_tds_override_replaces_the_projection()
    {
        Calculate(Structure(20_000), tds: 500, tdsOverride: 1_250).Tds.ShouldBe(1_250m);
    }

    [Fact]
    public void Pf_can_be_switched_off()
    {
        var result = Calculate(Structure(20_000), pf: false);

        result.EmployeePf.ShouldBe(0m);
        result.Lines.ShouldNotContain(l => l.Code == "PF");
    }

    [Fact]
    public void Statutory_lines_are_marked_and_ordered_after_earnings()
    {
        var result = Calculate(Structure(20_000), pt: Karnataka, tds: 500);

        result.Lines.Where(l => l.IsStatutory).Select(l => l.Code).ShouldBe(["PF", "PT", "TDS"]);
        result.Lines.Where(l => l.Kind == SalaryComponentKind.Earning).Select(l => l.Code).ShouldBe(["BASIC", "HRA", "CONV"]);
    }

    [Fact]
    public void Negative_net_pay_is_an_error_not_a_silent_cap()
    {
        var result = Calculate(Structure(5_000), tdsOverride: 20_000);

        result.NetPay.ShouldBeLessThan(0m);
        result.HasErrors.ShouldBeTrue();
        result.Warnings.ShouldContain(w => w.StartsWith("Deductions exceed earnings", StringComparison.Ordinal));
    }

    [Fact]
    public void A_structure_without_basic_is_an_error()
    {
        var result = Calculate([new SalaryLineInput("SPECIAL", "Special", SalaryComponentKind.Earning, SalaryCalculationType.Fixed, 10_000, 1)]);

        result.HasErrors.ShouldBeTrue();
        result.Warnings.ShouldContain("The salary structure has no fixed BASIC component.");
    }

    // PF wages (Code on Social Security 2020, s.2(88)): the pay that counts as wages, and of the pay left out of them (house
    // rent, conveyance, overtime…), what comes to more than half of all the pay.
    [Fact]
    public void Pf_is_on_wages_with_the_pay_left_out_above_half_added_back()
    {
        // ₹20,000 of pay: wages of ₹8,000 (basic and the special allowance), ₹12,000 left out (house rent, conveyance and
        // overtime), which is ₹2,000 more than half.
        var result = Calculate(Structure(6_000, Special(2_000)),
            adjustments: [new PayrollAdjustmentInput("OT", "Overtime", SalaryComponentKind.Earning, 8_000, WageTreatment.Excluded)]);

        result.GrossEarnings.ShouldBe(20_000m);
        result.PfWage.ShouldBe(10_000m);
        result.EmployeePf.ShouldBe(1_200m);
        result.EmployerPf.ShouldBe(1_200m);
    }

    [Fact]
    public void A_special_allowance_is_wages()
    {
        // Basic ₹8,000 and special allowance ₹4,000 are wages; house rent (₹3,200) and conveyance (₹1,600) are under half the pay.
        var result = Calculate(Structure(8_000, Special(4_000)));

        result.PfWage.ShouldBe(12_000m);
        result.EmployeePf.ShouldBe(1_440m);
    }

    [Fact]
    public void Pf_wages_are_the_month_s_pay_after_loss_of_pay()
    {
        var result = Calculate(Structure(6_000, Special(2_000)), payableDays: 27);

        result.PfWage.ShouldBe(7_200m);
        result.EmployeePf.ShouldBe(864m);
    }

    [Theory]
    [InlineData(false, 15_000, 1_800)]
    [InlineData(true, 18_000, 2_160)]
    public void Pf_wages_above_the_ceiling_count_only_on_actual_wages(bool onActual, double pfWage, double employeePf)
    {
        var result = Calculate(Structure(12_000, Special(6_000)), pfOnActual: onActual);

        result.PfWage.ShouldBe((decimal)pfWage);
        result.EmployeePf.ShouldBe((decimal)employeePf);
    }

    [Fact]
    public void Gratuity_paid_on_leaving_is_neither_wages_nor_counted_in_the_half()
    {
        var result = Calculate([new SalaryLineInput("BASIC", "Basic Salary", SalaryComponentKind.Earning, SalaryCalculationType.Fixed, 10_000, 10)],
            adjustments: [new PayrollAdjustmentInput("GRATUITY", "Gratuity", SalaryComponentKind.Earning, 50_000, WageTreatment.PaidOnLeaving)]);

        result.GrossEarnings.ShouldBe(60_000m);
        result.PfWage.ShouldBe(10_000m);
        result.EmployeePf.ShouldBe(1_200m);
    }

    [Fact]
    public void No_payable_days_pays_nothing_and_says_so()
    {
        var result = Calculate(Structure(20_000), payableDays: 0);

        result.GrossEarnings.ShouldBe(0m);
        result.EmployeePf.ShouldBe(0m);
        result.Warnings.ShouldContain("No payable days this month.");
    }
}
