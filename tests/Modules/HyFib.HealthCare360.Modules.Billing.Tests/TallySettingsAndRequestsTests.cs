using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Tally;
using HyFib.HealthCare360.Modules.Billing.Services;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

public sealed class TallySettingsAndRequestsTests
{
    private static readonly TallyLedgerMapValidator Ledgers = new();

    [Fact]
    public void The_built_in_ledger_names_are_valid() => Ledgers.Validate(new TallyLedgerMap()).IsValid.ShouldBeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Cash\nCounter")]
    public void A_ledger_name_must_be_one_line_of_text(string name)
    {
        var result = Ledgers.Validate(new TallyLedgerMap { Discount = name });

        result.Errors.ShouldHaveSingleItem().PropertyName.ShouldBe(nameof(TallyLedgerMap.Discount));
    }

    [Fact]
    public void A_ledger_name_is_at_most_a_hundred_characters() =>
        Ledgers.Validate(new TallyLedgerMap { RoundOff = new string('R', 101) }).IsValid.ShouldBeFalse();

    [Fact]
    public void Mode_maps_take_only_known_payment_modes()
    {
        Ledgers.Validate(new TallyLedgerMap { ModeLedgers = new Dictionary<string, string> { ["card"] = "HDFC Card Settlement" } }).IsValid.ShouldBeTrue();
        Ledgers.Validate(new TallyLedgerMap { ModeLedgers = new Dictionary<string, string> { ["1"] = "Bank" } }).IsValid.ShouldBeFalse();
        Ledgers.Validate(new TallyLedgerMap { Pharmacy = new TallyCounterLedgers { ModeLedgers = new Dictionary<string, string> { ["Cheque"] = "Bank" } } })
            .IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Department_codes_must_be_short_codes()
    {
        Ledgers.Validate(new TallyLedgerMap { IncomeByDepartment = new Dictionary<string, string> { ["LAB"] = "Laboratory Income" } }).IsValid.ShouldBeTrue();
        Ledgers.Validate(new TallyLedgerMap { IncomeByDepartment = new Dictionary<string, string> { ["LAB; DROP"] = "Laboratory Income" } }).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Saving_tidies_names_and_drops_blank_entries()
    {
        var map = TallyLedgerMaps.Normalize(new TallyLedgerMap
        {
            Discount = "  Discount Allowed ",
            IncomeByDepartment = new Dictionary<string, string> { ["lab"] = " Laboratory Income ", ["RAD"] = " " },
            Pharmacy = new TallyCounterLedgers { Sales = " ", OnAccount = " Pharmacy Debtors " },
        });

        map.Discount.ShouldBe("Discount Allowed");
        map.IncomeByDepartment.ShouldBe(new Dictionary<string, string> { ["LAB"] = "Laboratory Income" });
        map.Pharmacy.Sales.ShouldBeNull();
        map.Pharmacy.OnAccount.ShouldBe("Pharmacy Debtors");
    }

    [Fact]
    public void Up_to_a_month_of_days_can_be_posted_at_once_but_never_future_ones()
    {
        var validator = new PostDayBookRequestValidator(new FixedTimeProvider(TestData.Now));
        var today = new DateOnly(2026, 9, 25);

        validator.Validate(new PostDayBookRequest(today.AddDays(-30), today)).IsValid.ShouldBeTrue();
        validator.Validate(new PostDayBookRequest(today.AddDays(-31), today)).IsValid.ShouldBeFalse();
        validator.Validate(new PostDayBookRequest(today, today.AddDays(-1))).IsValid.ShouldBeFalse();
        validator.Validate(new PostDayBookRequest(today.AddDays(3))).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_day_posted_before_it_ended_is_posted_again_the_next_night()
    {
        var dayEnded = BillingTime.StartOfDay(new DateOnly(2026, 9, 26));
        TallyExport Export(TallyExportStatus status, DateTimeOffset queuedAt) => new() { Status = status, QueuedAt = queuedAt, CreatedAt = queuedAt };

        TallyDayBookScheduler.PostedBeforeTheDayEnded(Export(TallyExportStatus.Posted, dayEnded.AddHours(-5)), dayEnded).ShouldBeTrue();
        TallyDayBookScheduler.PostedBeforeTheDayEnded(Export(TallyExportStatus.Posted, dayEnded.AddHours(1)), dayEnded).ShouldBeFalse();
        TallyDayBookScheduler.PostedBeforeTheDayEnded(Export(TallyExportStatus.Queued, dayEnded.AddHours(-5)), dayEnded).ShouldBeFalse();
        TallyDayBookScheduler.PostedBeforeTheDayEnded(Export(TallyExportStatus.Imported, dayEnded.AddHours(-5)), dayEnded).ShouldBeFalse();
    }

    [Fact]
    public void Starting_a_run_requeues_the_day()
    {
        var export = new TallyExport { Status = TallyExportStatus.Failed, Message = "Could not reach Tally.", Attempts = 4 };
        var run = Guid.CreateVersion7();

        export.StartRun(run, TestData.Now);

        export.ShouldSatisfyAllConditions(
            e => e.Status.ShouldBe(TallyExportStatus.Queued),
            e => e.RunId.ShouldBe(run),
            e => e.QueuedAt.ShouldBe(TestData.Now),
            e => e.Attempts.ShouldBe(0),
            e => e.Message.ShouldBeNull());
    }
}
