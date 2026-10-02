using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The rules a number format must follow before an administrator may save it, and the messages they read. The staff
/// app's numberFormat.ts checks the same rules with the same messages.
/// </summary>
public sealed class NumberFormatRulesTests
{
    private static readonly NumberSeriesDefinition OpBill =
        new("billing.bill.op", "OP bill", "Billing", "{FAC}OB{YY}{SEQ:7}", NumberResetPolicy.Yearly, MaxLength: 24);

    private static readonly NumberSeriesDefinition Employee =
        new("hr.employee", "Employee code", "Human resources", "{FAC}EMP{SEQ:5}", NumberResetPolicy.Never, MaxLength: 20);

    private static readonly NumberSeriesDefinition Breach =
        new("privacy.breach", "Data breach", "Privacy", "DPB{YY}{SEQ:4}", NumberResetPolicy.Yearly, MaxLength: 24) { PerFacility = false };

    private static readonly NumberSeriesDefinition Uhid =
        new("uhid", "UHID (patient number)", "Patients", "{FAC}{YY}{SEQ:6}", NumberResetPolicy.Never, MaxLength: 20)
        {
            PerFacility = false,
            LockedReason = "Other systems read UHIDs.",
        };

    [Theory]
    [InlineData("{FAC}OB{YY}{SEQ:7}")]
    [InlineData("{FAC}/OP/{YYYY}/{SEQ:6}")]
    [InlineData("VP{YY}{MM}{DD}-{SEQ:4}")]
    [InlineData("{SEQ}")]
    [InlineData("{SEQ:12}")]
    public void Well_formed_formats_have_no_problems(string format) =>
        NumberFormatRules.FormatProblems(format).ShouldBeEmpty();

    [Theory]
    [InlineData("", "Enter a format, for example {FAC}{YY}{SEQ:6}.")]
    [InlineData("   ", "Enter a format, for example {FAC}{YY}{SEQ:6}.")]
    [InlineData("{FAC}OB{YY}", "Add {SEQ} or {SEQ:n} for the running number.")]
    [InlineData("{SEQ:4}-{SEQ:4}", "Use the running number {SEQ} only once.")]
    [InlineData("{FAC}{SEQ:0}", "In {SEQ:n}, n is the number of digits and must be from 1 to 12.")]
    [InlineData("{FAC}{SEQ:13}", "In {SEQ:n}, n is the number of digits and must be from 1 to 12.")]
    [InlineData("{FAC}{YEAR}{SEQ}", "{YEAR} is not a part a number can have. Use {FAC}, {YYYY}, {YY}, {MM}, {DD} and {SEQ:n}.")]
    [InlineData("{FY}{YR}{SEQ}", "{FY} and {YR} are not parts a number can have. Use {FAC}, {YYYY}, {YY}, {MM}, {DD} and {SEQ:n}.")]
    [InlineData("{FAC}OB{YY{SEQ}", "{YY{SEQ} is not a part a number can have. Use {FAC}, {YYYY}, {YY}, {MM}, {DD} and {SEQ:n}.")]
    [InlineData("OB{SEQ", "A { has no closing }. Parts in braces look like {YY} or {SEQ:6}.")]
    [InlineData("op {SEQ}", "Outside the braces use only capital letters A to Z, digits, - and /. Not allowed: small letters and spaces.")]
    [InlineData("OP.{SEQ}_", "Outside the braces use only capital letters A to Z, digits, - and /. Not allowed: \".\" and \"_\".")]
    public void A_badly_written_format_says_what_to_fix(string format, string message) =>
        NumberFormatRules.FormatProblems(format)[0].ShouldBe(message);

    [Fact]
    public void A_format_longer_than_the_database_keeps_is_refused() =>
        NumberFormatRules.FormatProblems("{SEQ}" + new string('A', 60)).ShouldContain("Use at most 64 characters.");

    [Theory]
    [InlineData("{FAC}{SEQ:6}", NumberResetPolicy.Never, true)]
    [InlineData("{FAC}{YY}{SEQ:6}", NumberResetPolicy.Yearly, true)]
    [InlineData("{FAC}{YYYY}{SEQ:6}", NumberResetPolicy.Yearly, true)]
    [InlineData("{FAC}{SEQ:6}", NumberResetPolicy.Yearly, false)]
    [InlineData("{FAC}{YY}{MM}{SEQ:4}", NumberResetPolicy.Monthly, true)]
    [InlineData("{FAC}{MM}{SEQ:4}", NumberResetPolicy.Monthly, false)]
    [InlineData("{FAC}{YY}{SEQ:4}", NumberResetPolicy.Monthly, false)]
    [InlineData("VP{YY}{MM}{DD}-{SEQ:4}", NumberResetPolicy.Daily, true)]
    [InlineData("VP{YY}{MM}-{SEQ:4}", NumberResetPolicy.Daily, false)]
    public void Numbers_that_start_again_carry_the_date_of_their_period(string format, NumberResetPolicy policy, bool allowed) =>
        (NumberFormatRules.ResetProblem(format, policy) is null).ShouldBe(allowed);

    [Fact]
    public void A_yearly_restart_without_the_year_explains_why_it_is_needed() =>
        NumberFormatRules.ResetProblem("{FAC}OB{SEQ:7}", NumberResetPolicy.Yearly).ShouldBe(
            "Numbers that start again every year must contain the year ({YY} or {YYYY}), or numbers from earlier years would be issued again.");

    [Fact]
    public void The_longest_number_counts_the_longest_facility_code_and_eight_digits_of_running_number()
    {
        var length = NumberFormatRules.LongestNumber("{FAC}/OP/{YYYY}/{SEQ:6}", facilityCodeLength: 6);

        length.ShouldBe(new NumberFormatLength(FacilityCode: 6, Date: 4, RunningNumber: 8, FixedText: 5));
        length.Total.ShouldBe(23);
        NumberFormatRules.LongestNumber("{SEQ:9}", 6).Total.ShouldBe(9, "a running number padded beyond 8 digits counts its padding");
    }

    [Fact]
    public void A_good_change_has_no_errors() =>
        NumberFormatRules.Check(Change(OpBill, "{FAC}/OP/{YY}/{SEQ:6}")).ShouldBeEmpty();

    [Fact]
    public void A_format_too_long_for_its_column_says_what_to_shorten()
    {
        var errors = NumberFormatRules.Check(Change(Employee, "{FAC}EMPLOYEE{YYYY}{SEQ:5}"));

        errors[NumberFormatRules.FormatField].ShouldBe([
            "Numbers in this format can be up to 26 characters long (facility code 6, date 4, running number 8, fixed text 8), and only 20 fit. " +
            "Shorten the fixed text or use {YY} instead of {YYYY}.",
        ]);
    }

    [Fact]
    public void The_length_counts_the_longest_facility_code_in_use() =>
        NumberFormatRules.Check(Change(Employee, "{FAC}EMPLOYEE{SEQ:5}") with { LongestFacilityCode = 4 }).ShouldBeEmpty("4 + 8 + 8 = 20");

    [Fact]
    public void Per_facility_numbers_need_the_facility_code_once_there_are_two_facilities()
    {
        var change = Change(OpBill, "OB{YY}{SEQ:7}");

        NumberFormatRules.Check(change).ShouldBeEmpty("one facility cannot clash with another");
        NumberFormatRules.Check(change with { FacilityCount = 2 })[NumberFormatRules.FormatField].ShouldBe([
            "Add {FAC}. Each facility counts its own numbers, so without the facility code two facilities would issue the same numbers.",
        ]);
        NumberFormatRules.Check(Change(Breach, "BR{YY}{SEQ:4}") with { FacilityCount = 2 }).ShouldBeEmpty("a shared series counts once for all");
    }

    [Fact]
    public void A_changed_format_must_differ_from_the_other_series()
    {
        var change = Change(OpBill, "{FAC}IB{YY}{SEQ:7}") with { OtherFormats = [new OtherNumberFormat("IP bill", "{FAC}IB{YY}{SEQ:7}", IsDefault: true)] };

        NumberFormatRules.Check(change)[NumberFormatRules.FormatField].ShouldBe([
            "IP bill already uses this format. Change the fixed text so the two kinds of number can be told apart.",
        ]);
    }

    [Theory]
    [InlineData("{FAC}OB{YY}{SEQ:7}", "{FAC}OB#")]
    [InlineData("{FAC}OB{SEQ:9}", "{FAC}OB#")]
    [InlineData("{FAC}OB26{SEQ}", "{FAC}OB#")]
    [InlineData("{FAC}/OP/{YYYY}/{SEQ:6}", "{FAC}/OP/#/#")]
    [InlineData("B{YY}X{SEQ}", "B#X#")]
    public void A_format_reads_with_each_run_of_digits_as_one_place(string format, string outline) =>
        NumberFormatRules.Outline(format).ShouldBe(outline);

    [Theory]
    [InlineData("{FAC}OB{YY}{SEQ:1}", "{FAC}OB{YY}{SEQ}")]
    [InlineData("{FAC}OB{YY}{SEQ:06}", "{FAC}OB{YY}{SEQ:6}")]
    [InlineData("{FAC}OB{YY}{SEQ:8}", "{FAC}OB{YY}{SEQ:6}")]
    [InlineData("{FAC}OB{YY}{SEQ:4}", "{FAC}OB{YY}0{SEQ:3}")]
    [InlineData("{FAC}OB{YY}{SEQ:6}", "{FAC}OB26{SEQ:6}")]
    public void A_format_that_can_make_another_series_numbers_is_refused(string format, string other)
    {
        // {SEQ:1} and {SEQ}, or {SEQ:06} and {SEQ:6}, make the same numbers; {SEQ:6} reaching 10000000 makes {SEQ:8}'s.
        var change = Change(OpBill, format) with { OtherFormats = [new OtherNumberFormat("IP bill", other, IsDefault: false)] };

        NumberFormatRules.Check(change)[NumberFormatRules.FormatField].ShouldBe([
            $"IP bill uses {other}, which can make the same numbers: the two differ only in digits, dates or the length of " +
            "the running number. Change the letters, - or / so the two kinds of number can be told apart.",
        ]);
    }

    [Theory]
    [InlineData("{FAC}/OP/{YY}/{SEQ:5}", "{FAC}/IP/{YY}/{SEQ:5}")]
    [InlineData("{FAC}OB{YYYY}{SEQ:6}", "{FAC}OB-{YY}{SEQ:7}")]
    [InlineData("{FAC}B{YY}X{SEQ:5}", "{FAC}BX{YY}{SEQ:5}")]
    public void Formats_whose_letters_differ_can_be_used_together(string format, string other) =>
        NumberFormatRules.Check(Change(OpBill, format) with { OtherFormats = [new OtherNumberFormat("IP bill", other, IsDefault: false)] })
            .ShouldBeEmpty();

    [Theory]
    [InlineData("{FAC}AB{YY}{SEQ:7}", "AB", "ABAB#")]
    [InlineData("AB{FAC}{YY}{SEQ:7}", "AB", "ABAB#")]
    [InlineData("{FAC}{SEQ:6}", "NH1", "NH#")]
    [InlineData("{FAC}OB{SEQ}", null, "{FAC}OB#")]
    public void The_facility_code_is_read_in_place_of_FAC(string format, string? facilityCode, string outline) =>
        NumberFormatRules.Outline(format, facilityCode).ShouldBe(outline);

    [Fact]
    public void Formats_that_make_the_same_numbers_once_the_facility_code_is_filled_in_are_refused()
    {
        // At facility AB both make ABAB260000001: {FAC} is the facility's code, not a part apart.
        var change = Change(OpBill, "{FAC}AB{YY}{SEQ:7}") with
        {
            FacilityCode = "AB",
            OtherFormats = [new OtherNumberFormat("IP bill", "AB{FAC}{YY}{SEQ:7}", IsDefault: false, FacilityCode: "AB")],
        };

        NumberFormatRules.Check(change)[NumberFormatRules.FormatField].ShouldBe([
            "IP bill uses AB{FAC}{YY}{SEQ:7}. With the facility code AB in place of {FAC}, the two can make the same numbers. " +
            "Change the letters, - or / so the two kinds of number can be told apart.",
        ]);
    }

    [Fact]
    public void A_facility_code_written_out_in_another_format_counts_as_that_code()
    {
        var change = Change(OpBill, "{FAC}-{YY}{SEQ:7}") with
        {
            FacilityCode = "NH",
            OtherFormats = [new OtherNumberFormat("Visitor pass", "NH-{YY}{SEQ:7}", IsDefault: false, FacilityCode: "NH")],
        };

        NumberFormatRules.Check(change)[NumberFormatRules.FormatField].ShouldBe([
            "Visitor pass uses NH-{YY}{SEQ:7}. With the facility code NH in place of {FAC}, the two can make the same numbers. " +
            "Change the letters, - or / so the two kinds of number can be told apart.",
        ]);
    }

    [Fact]
    public void Formats_at_two_facilities_are_compared_with_each_ones_code()
    {
        // Number columns are unique across facilities: AB's {FAC}C... and ABC's {FAC}... both make ABC260000001.
        var change = Change(OpBill, "{FAC}C{YY}{SEQ:7}") with
        {
            FacilityCode = "AB",
            OtherFormats = [new OtherNumberFormat("IP bill", "{FAC}{YY}{SEQ:7}", IsDefault: false, FacilityCode: "ABC")],
        };

        NumberFormatRules.Check(change)[NumberFormatRules.FormatField].ShouldBe([
            "IP bill uses {FAC}{YY}{SEQ:7}. With the facility codes AB and ABC in place of {FAC}, the two can make the same " +
            "numbers. Change the letters, - or / so the two kinds of number can be told apart.",
        ]);
    }

    [Fact]
    public void The_same_format_at_facilities_with_different_codes_never_makes_the_same_numbers() =>
        NumberFormatRules.Check(Change(OpBill, "{FAC}IB{YY}{SEQ:7}") with
        {
            FacilityCode = "NH",
            OtherFormats = [new OtherNumberFormat("IP bill", "{FAC}IB{YY}{SEQ:7}", IsDefault: false, FacilityCode: "KH")],
        }).ShouldBeEmpty("NHIB... and KHIB... can never be the same number.");

    [Fact]
    public void Restoring_a_default_that_looks_like_another_default_is_allowed()
    {
        // Refunds and pharmacy refills were both {FAC}RF{YY}{SEQ:6} from the start; they are kept in different tables.
        var refund = new NumberSeriesDefinition("billing.refund", "Refund", "Billing", "{FAC}RF{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);
        var restore = new NumberFormatChange
        {
            Series = refund,
            Format = refund.DefaultFormat,
            ResetPolicy = NumberResetPolicy.Yearly,
            CurrentFormat = "{FAC}/RF/{YY}/{SEQ:6}",
            CurrentResetPolicy = NumberResetPolicy.Yearly,
            OtherFormats = [new OtherNumberFormat("Refill request", "{FAC}RF{YY}{SEQ:6}", IsDefault: true)],
        };

        NumberFormatRules.Check(restore).ShouldBeEmpty();
        NumberFormatRules.Check(restore with { OtherFormats = [new OtherNumberFormat("Credit note", "{FAC}RF{YY}{SEQ:6}", IsDefault: false)] })
            .Keys.ShouldContain(NumberFormatRules.FormatField);
    }

    [Fact]
    public void An_unchanged_format_is_not_checked_again_when_only_the_next_number_changes()
    {
        // A second facility was added after visitor passes were set up: raising the next number still works.
        var visitorPass = new NumberSeriesDefinition("visitor-pass", "Visitor pass", "Front office", "VP{YY}{MM}{DD}-{SEQ:4}", NumberResetPolicy.Daily, MaxLength: 24);
        var change = new NumberFormatChange
        {
            Series = visitorPass,
            Format = visitorPass.DefaultFormat,
            ResetPolicy = NumberResetPolicy.Daily,
            StartAt = 50,
            CurrentFormat = visitorPass.DefaultFormat,
            CurrentResetPolicy = NumberResetPolicy.Daily,
            FacilityCount = 2,
        };

        NumberFormatRules.Check(change).ShouldBeEmpty();
        NumberFormatRules.Check(change with { ResetPolicy = NumberResetPolicy.Monthly })
            .Keys.ShouldContain(NumberFormatRules.FormatField, "changing the reset checks the format in full");
    }

    [Fact]
    public void A_new_reset_needs_the_date_parts_of_its_period() =>
        NumberFormatRules.Check(Change(Employee, Employee.DefaultFormat) with { ResetPolicy = NumberResetPolicy.Yearly })[NumberFormatRules.ResetPolicyField]
            .ShouldBe(["Numbers that start again every year must contain the year ({YY} or {YYYY}), or numbers from earlier years would be issued again."]);

    [Theory]
    [InlineData(NumberResetPolicy.Yearly, "Number 120 has already been issued this year. Enter a number above 120.")]
    [InlineData(NumberResetPolicy.Monthly, "Number 120 has already been issued this month. Enter a number above 120.")]
    [InlineData(NumberResetPolicy.Daily, "Number 120 has already been issued today. Enter a number above 120.")]
    [InlineData(NumberResetPolicy.Never, "Number 120 has already been issued. Enter a number above 120.")]
    public void The_next_number_must_be_above_the_last_one_issued(NumberResetPolicy policy, string message)
    {
        var change = new NumberFormatChange
        {
            Series = OpBill,
            Format = "{FAC}OB{YY}{MM}{DD}{SEQ:7}",
            ResetPolicy = policy,
            StartAt = 120,
            CurrentFormat = "{FAC}OB{YY}{MM}{DD}{SEQ:7}",
            CurrentResetPolicy = policy,
            LastNumber = 120,
        };

        NumberFormatRules.Check(change)[NumberFormatRules.StartAtField].ShouldBe([message]);
        NumberFormatRules.Check(change with { StartAt = 121 }).ShouldBeEmpty();
    }

    [Fact]
    public void The_next_number_fits_the_digits_the_format_is_measured_with()
    {
        var change = Change(OpBill, OpBill.DefaultFormat) with { StartAt = 99_999_999 };

        NumberFormatRules.Check(change).ShouldBeEmpty();
        NumberFormatRules.Check(change with { StartAt = 100_000_000 })[NumberFormatRules.StartAtField]
            .ShouldBe(["The next number can have at most 8 digits."]);
        NumberFormatRules.Check(change with { StartAt = 0 })[NumberFormatRules.StartAtField].ShouldBe(["Enter a number of 1 or more."]);
    }

    [Fact]
    public void A_locked_series_keeps_its_shape_when_its_next_number_is_raised()
    {
        var change = new NumberFormatChange
        {
            Series = Uhid,
            Format = Uhid.DefaultFormat,
            ResetPolicy = NumberResetPolicy.Never,
            StartAt = 1_000_000,
            CurrentFormat = Uhid.DefaultFormat,
            CurrentResetPolicy = NumberResetPolicy.Never,
            LastNumber = 4_000,
        };

        NumberFormatRules.Check(change)[NumberFormatRules.StartAtField]
            .ShouldBe(["The next number can be at most 999999, so that these numbers keep the shape other systems read."]);
        NumberFormatRules.Check(change with { StartAt = 250_001 }).ShouldBeEmpty("a hospital moving from an older system continues its UHIDs");
    }

    [Fact]
    public void The_largest_next_number_follows_the_running_number()
    {
        NumberFormatRules.LargestStartAt(OpBill, "{FAC}OB{YY}{SEQ:4}").ShouldBe(99_999_999);
        NumberFormatRules.LargestStartAt(OpBill, "{FAC}OB{YY}{SEQ:10}").ShouldBe(9_999_999_999);
        NumberFormatRules.LargestStartAt(Uhid, "{FAC}{YY}{SEQ:6}").ShouldBe(999_999);
        NumberFormatRules.LargestStartAt(OpBill, "{FAC}OB{YY}").ShouldBeNull();
    }

    private static NumberFormatChange Change(NumberSeriesDefinition series, string format) => new()
    {
        Series = series,
        Format = format,
        ResetPolicy = series.DefaultReset,
        CurrentFormat = series.DefaultFormat,
        CurrentResetPolicy = series.DefaultReset,
        FacilityCount = 1,
        LongestFacilityCode = 6,
    };
}
