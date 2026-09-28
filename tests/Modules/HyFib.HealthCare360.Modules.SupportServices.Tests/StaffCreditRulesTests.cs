using System.Globalization;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Canteen;

namespace HyFib.HealthCare360.Modules.SupportServices.Tests;

public sealed class StaffCreditRulesTests
{
    [Theory]
    [InlineData(1000, 500, 3000, true)]
    [InlineData(2600, 400, 3000, true)]
    [InlineData(2600, 401, 3000, false)]
    [InlineData(50000, 100, 0, true)]
    public void Credit_sales_must_stay_within_the_limit(int outstanding, int sale, int limit, bool allowed)
    {
        var result = StaffCreditRules.CheckLimit(outstanding, sale, limit);

        result.IsSuccess.ShouldBe(allowed);
        if (!allowed)
        {
            result.Error!.Code.ShouldBe("canteen.credit_limit");
        }
    }

    [Fact]
    public void A_statement_carries_the_opening_balance_and_a_running_balance()
    {
        var statement = StaffCreditRules.BuildStatement("EMP042", "Ravi K", "2026-09", 100m,
        [
            new CreditEntryData(new DateOnly(2026, 9, 3), StaffCreditEntryKind.Sale, 50m, "NHCN260000123"),
            new CreditEntryData(new DateOnly(2026, 9, 1), StaffCreditEntryKind.Sale, 30m, "NHCN260000101"),
            new CreditEntryData(new DateOnly(2026, 9, 3), StaffCreditEntryKind.Cancellation, -20m, "NHCN260000123"),
            new CreditEntryData(new DateOnly(2026, 9, 30), StaffCreditEntryKind.PayrollDeduction, -160m, "PAY-2026-09"),
        ]);

        statement.OpeningBalance.ShouldBe(100m);
        statement.Purchases.ShouldBe(80m);
        statement.Credits.ShouldBe(180m);
        statement.ClosingBalance.ShouldBe(0m);
        statement.Lines.Select(l => l.Balance).ShouldBe([130m, 180m, 160m, 0m]);
        statement.Lines.First().Date.ShouldBe(new DateOnly(2026, 9, 1));
    }

    [Theory]
    [InlineData("2026-09", true)]
    [InlineData("2026-9", false)]
    [InlineData("2026-13", false)]
    [InlineData("Sep-2026", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Periods_are_calendar_months_written_yyyy_MM(string? period, bool valid)
    {
        StaffCreditRules.TryParsePeriod(period, out var firstDay).ShouldBe(valid);
        if (valid)
        {
            firstDay.ShouldBe(new DateOnly(2026, 9, 1));
        }
    }

    [Fact]
    public void Periods_are_written_the_same_in_every_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            StaffCreditRules.PeriodOf(new DateOnly(2026, 9, 15)).ShouldBe("2026-09");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("EMP001", true)]
    [InlineData("hr/2024-17", true)]
    [InlineData("E", false)]
    [InlineData("EMP 001", false)]
    [InlineData("=HYPERLINK(1)", false)]
    [InlineData("ABCDEFGHIJKLMNOPQRSTU", false)]
    [InlineData(null, false)]
    public void Employee_codes_are_short_alphanumeric_identifiers(string? code, bool valid) =>
        StaffCreditRules.IsValidEmployeeCode(code).ShouldBe(valid);

    [Fact]
    public void Employee_codes_are_normalised_to_upper_case() =>
        StaffCreditRules.NormalizeEmployeeCode("  emp042 ").ShouldBe("EMP042");
}
