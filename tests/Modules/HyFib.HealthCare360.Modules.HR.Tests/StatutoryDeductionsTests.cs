using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Tests;

public sealed class StatutoryDeductionsTests
{
    [Theory]
    [InlineData(20_000, false, 15_000)]
    [InlineData(15_000, false, 15_000)]
    [InlineData(12_345, false, 12_345)]
    [InlineData(20_000, true, 20_000)]
    [InlineData(-10, false, 0)]
    public void Pf_wage_is_capped_at_the_ceiling_unless_opted_for_actual(double wages, bool onActual, double expected) =>
        ProvidentFund.Wage((decimal)wages, onActual).ShouldBe((decimal)expected);

    // Code on Social Security 2020, s.2(88): of the pay left out of wages (house rent, conveyance, overtime…), what comes to
    // more than half of all the pay is added back to wages.
    [Theory]
    [InlineData(8_000, 12_000, 10_000)]
    [InlineData(13_000, 12_000, 13_000)]
    [InlineData(10_000, 10_000, 10_000)]
    [InlineData(0, 9_000, 4_500)]
    [InlineData(6_000, 0, 6_000)]
    public void Pf_wages_take_in_what_the_pay_left_out_comes_to_above_half_of_all_the_pay(double wages, double leftOut, double expected) =>
        ProvidentFund.Wages((decimal)wages, (decimal)leftOut).ShouldBe((decimal)expected);

    [Theory]
    [InlineData(15_000, 1_800)]
    [InlineData(12_345, 1_481)]
    [InlineData(12_512.50, 1_502)]
    [InlineData(0, 0)]
    public void Pf_is_twelve_percent_rounded_to_the_nearest_rupee(double wage, double expected)
    {
        ProvidentFund.EmployeeContribution((decimal)wage).ShouldBe((decimal)expected);
        ProvidentFund.EmployerContribution((decimal)wage).ShouldBe((decimal)expected);
    }

    [Theory]
    [InlineData(21_000, true)]
    [InlineData(21_000.01, false)]
    [InlineData(9_500, true)]
    [InlineData(0, false)]
    public void Esi_covers_contracted_gross_up_to_21000(double gross, bool expected) =>
        EmployeeStateInsurance.IsCovered((decimal)gross).ShouldBe(expected);

    [Theory]
    [InlineData(15_000, 113, 488)]
    [InlineData(20_000, 150, 650)]
    [InlineData(15_600, 117, 507)]
    [InlineData(0, 0, 0)]
    public void Esi_contributions_round_up_to_the_next_rupee(double wage, double employee, double employer)
    {
        EmployeeStateInsurance.EmployeeContribution((decimal)wage).ShouldBe((decimal)employee);
        EmployeeStateInsurance.EmployerContribution((decimal)wage).ShouldBe((decimal)employer);
    }
}
