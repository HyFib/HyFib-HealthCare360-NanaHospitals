using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The check digit a format can end in ({CHK}): the Damm digit of every digit written before it. A number copied by hand
/// with one digit wrong, or two neighbouring digits swapped, no longer ends in its check digit, so it is never taken for
/// another number the series issued.
/// </summary>
public sealed class NumberCheckDigitTests
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    private static readonly DateTimeOffset October2 = new(2026, 10, 2, 11, 15, 0, Ist);

    [Theory]
    [InlineData("572", '4')]
    [InlineData("11294", '6')]
    [InlineData("100000000", '5')]
    [InlineData("100001234", '2')]
    [InlineData("100001235", '0')]
    [InlineData("999999999", '0')]
    public void The_check_digit_is_the_damm_digit_of_the_digits(string digits, char check) =>
        DammCheckDigit.Of(digits).ShouldBe(check);

    [Theory]
    [InlineData("5724", true)]
    [InlineData("5727", false)]
    [InlineData("112946", true)]
    [InlineData("112949", false)]
    [InlineData("1000000005", true)]
    [InlineData("1000012342", true)]
    [InlineData("1000012346", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    public void A_number_verifies_when_it_ends_in_the_check_digit_of_the_digits_before_it(string number, bool verifies) =>
        DammCheckDigit.Verifies(number).ShouldBe(verifies);

    [Fact]
    public void Letters_and_signs_between_the_digits_are_passed_over()
    {
        DammCheckDigit.Of("NH/26-000124").ShouldBe(DammCheckDigit.Of("26000124"));
        DammCheckDigit.Verifies("NH/26-000124" + DammCheckDigit.Of("26000124")).ShouldBeTrue();
    }

    [Fact]
    public void Every_wrong_digit_and_every_swap_of_two_neighbouring_digits_is_caught()
    {
        var random = new Random(20261119);
        for (var sample = 0; sample < 2_000; sample++)
        {
            var running = random.NextInt64(100_000_000, 1_000_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var number = running + DammCheckDigit.Of(running);
            DammCheckDigit.Verifies(number).ShouldBeTrue(number);

            for (var place = 0; place < number.Length; place++)
            {
                foreach (var digit in "0123456789".Where(d => d != number[place]))
                {
                    var typed = number[..place] + digit + number[(place + 1)..];
                    DammCheckDigit.Verifies(typed).ShouldBeFalse($"{typed} is {number} with one digit wrong");
                }
            }

            for (var place = 0; place + 1 < number.Length; place++)
            {
                if (number[place] != number[place + 1])
                {
                    var typed = number[..place] + number[place + 1] + number[place] + number[(place + 2)..];
                    DammCheckDigit.Verifies(typed).ShouldBeFalse($"{typed} is {number} with two neighbouring digits swapped");
                }
            }
        }
    }

    [Theory]
    [InlineData("{SEQ:9}{CHK}", 100_001_234L, null, "1000012342")]
    [InlineData("{SEQ:9}{CHK}", 100_000_000L, null, "1000000005")]
    [InlineData("{FAC}{YY}{SEQ:6}{CHK}", 124L, "NH", "NH260001240")]
    [InlineData("{FAC}/{SEQ:4}-{CHK}", 7L, "NH", "NH/0007-6")]
    public void The_check_digit_part_ends_the_number_with_the_check_digit_of_the_digits_before_it(string format, long value, string? facility, string expected)
    {
        var number = NumberFormat.Render(format, value, facility, October2);

        number.ShouldBe(expected);
        DammCheckDigit.Verifies(number).ShouldBeTrue();
    }

    [Fact]
    public void The_check_digit_is_a_part_of_its_own()
    {
        var parts = NumberFormat.Parse("{SEQ:9}{CHK}");

        parts.Select(p => p.Kind).ShouldBe([NumberFormatPartKind.Sequence, NumberFormatPartKind.CheckDigit]);
        parts[1].Text.ShouldBe("{CHK}");
        NumberFormat.Parse("{chk}").ShouldHaveSingleItem().Kind.ShouldBe(NumberFormatPartKind.Unknown);
    }

    [Theory]
    [InlineData("{SEQ:9}{CHK}")]
    [InlineData("{FAC}{YY}{SEQ:6}{CHK}")]
    [InlineData("{SEQ}-{CHK}")]
    public void A_check_digit_at_the_end_after_the_running_number_is_well_formed(string format) =>
        NumberFormatRules.FormatProblems(format).ShouldBeEmpty();

    [Theory]
    [InlineData("{SEQ:9}{CHK}{CHK}", "Use the check digit {CHK} only once.")]
    [InlineData("{CHK}{SEQ:9}", "Put the check digit {CHK} at the end, after the running number: it checks every digit before it.")]
    [InlineData("{SEQ:6}{CHK}-A", "Put the check digit {CHK} at the end, after the running number: it checks every digit before it.")]
    [InlineData("{FAC}{CHK}", "Add {SEQ} or {SEQ:n} for the running number.")]
    [InlineData("{FAC}{CHECK}{SEQ}", "{CHECK} is not a part a number can have. Use {FAC}, {YYYY}, {YY}, {MM}, {DD}, {SEQ:n} and {CHK}.")]
    public void A_check_digit_anywhere_else_says_what_to_fix(string format, string message) =>
        NumberFormatRules.FormatProblems(format).ShouldContain(message);

    [Fact]
    public void The_longest_number_counts_the_check_digit()
    {
        var length = NumberFormatRules.LongestNumber("{SEQ:9}{CHK}", facilityCodeLength: 6);

        length.ShouldBe(new NumberFormatLength(FacilityCode: 0, Date: 0, RunningNumber: 9, FixedText: 0, CheckDigit: 1));
        length.Total.ShouldBe(10);
        NumberFormatRules.GstLongestNumber("{FAC}B{YY}{SEQ:6}{CHK}", NumberResetPolicy.Yearly, 4).Total.ShouldBe(4 + 1 + 2 + 6 + 1);
    }

    [Fact]
    public void A_format_too_long_with_its_check_digit_says_so()
    {
        var employee = new NumberSeriesDefinition("hr.employee", "Employee code", "Human resources", "{FAC}EMP{SEQ:5}", NumberResetPolicy.Never, MaxLength: 20);
        var change = new NumberFormatChange
        {
            Series = employee,
            Format = "{FAC}EMPLOYEE{SEQ:5}{CHK}",
            ResetPolicy = NumberResetPolicy.Never,
            CurrentFormat = employee.DefaultFormat,
            CurrentResetPolicy = NumberResetPolicy.Never,
        };

        NumberFormatRules.Check(change)[NumberFormatRules.FormatField].ShouldBe([
            "Numbers in this format can be up to 23 characters long (facility code 6, running number 8, check digit 1, fixed text 8), and only 20 fit. " +
            "Shorten the fixed text.",
        ]);
    }

    [Fact]
    public void A_check_digit_is_one_more_digit_of_the_number_when_formats_are_compared()
    {
        NumberFormatRules.Outline("{SEQ:9}{CHK}").ShouldBe("#");
        NumberFormatRules.CanMakeSameNumbers("{SEQ:9}{CHK}", null, "{SEQ:10}", null).ShouldBeTrue("both make 10-digit numbers");
        NumberFormatRules.CanMakeSameNumbers("{FAC}A{SEQ:6}{CHK}", "NH", "{FAC}B{SEQ:6}", "NH").ShouldBeFalse();
        NumberFormatRules.CanMakeSameNumbers("{FAC}{SEQ:4}-{CHK}", "NH", "{FAC}{SEQ:4}-{YY}", "NH")
            .ShouldBeFalse("the check digit is one digit and the year two");
    }

    [Theory]
    [InlineData(1_000_012_346L, "{SEQ:10}", "{SEQ:9}{CHK}", 100_001_234L)]
    [InlineData(523L, "{SEQ:9}", "{SEQ:9}{CHK}", 52L)]
    [InlineData(100_001_234L, "{SEQ:9}{CHK}", "{SEQ:10}", 1_000_012_349L)]
    [InlineData(100_001_234L, "{SEQ:9}{CHK}", "{SEQ:9}{CHK}", 100_001_234L)]
    [InlineData(41L, "{FAC}OB{YY}{SEQ:6}", "{FAC}/OP/{YY}/{SEQ:6}", 41L)]
    [InlineData(0L, "{SEQ:9}{CHK}", "{SEQ:10}", 0L)]
    public void A_counter_is_counted_in_tens_between_a_format_with_a_check_digit_and_one_without(long value, string from, string to, long expected) =>
        NumberFormat.CounterIn(value, from, to).ShouldBe(expected);

    [Fact]
    public void The_next_number_after_a_counter_counted_in_tens_is_above_every_number_issued_before()
    {
        var random = new Random(17);
        for (var sample = 0; sample < 5_000; sample++)
        {
            var last = random.NextInt64(1_000_000_000, 9_999_999_990);
            var counter = NumberFormat.CounterIn(last, "{SEQ:10}", "{SEQ:9}{CHK}");
            var next = NumberFormat.Render("{SEQ:9}{CHK}", NumberFormat.NextNumber(counter, 100_000_000), null, October2);

            long.Parse(next, System.Globalization.CultureInfo.InvariantCulture).ShouldBeGreaterThan(last, $"after {last}");
            next.Length.ShouldBe(10);
        }
    }
}
