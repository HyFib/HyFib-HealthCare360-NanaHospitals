using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

public sealed class TallyTrackingTests
{
    private static readonly DateOnly Day = new(2026, 9, 25);
    private static readonly Guid Run = Guid.CreateVersion7();

    private static AccountingVoucher Receipt(string number, decimal amount = 500m) => new(
        "Receipt", Day, number, $"Receipt {number}",
        [new AccountingEntry("Cash", amount, true), new AccountingEntry("Patient Receivables", amount, false)], number);

    private static TallyExport Export() => new() { FacilityId = TestData.FacilityId, Date = Day, RunId = Run };

    private static AccountingExportResult Answers(params (string Key, AccountingVoucherOutcome Outcome)[] answers) =>
        new(answers.All(a => a.Outcome is AccountingVoucherOutcome.Created or AccountingVoucherOutcome.Altered), 0, 0, 0, null)
        {
            Vouchers = answers.Select(a => new AccountingVoucherAnswer(a.Key, a.Outcome, a.Outcome == AccountingVoucherOutcome.Rejected ? "Ledger 'Cash' does not exist!" : null)).ToList(),
        };

    [Fact]
    public void The_fingerprint_ignores_the_order_of_lines_and_changes_with_any_amount()
    {
        var voucher = Receipt("RC1");

        TallyTracking.Fingerprint(voucher with { Entries = voucher.Entries.Reverse().ToList() }).ShouldBe(TallyTracking.Fingerprint(voucher));
        TallyTracking.Fingerprint(Receipt("RC1", 500.01m)).ShouldNotBe(TallyTracking.Fingerprint(voucher));
        TallyTracking.Fingerprint(voucher).Length.ShouldBe(64);
    }

    [Fact]
    public void The_day_books_fingerprint_changes_when_a_voucher_is_added()
    {
        IReadOnlyList<AccountingVoucher> day = [Receipt("RC1"), Receipt("RC2")];

        TallyTracking.Fingerprint([day[1], day[0]]).ShouldBe(TallyTracking.Fingerprint(day));
        TallyTracking.Fingerprint([.. day, Receipt("RC3")]).ShouldNotBe(TallyTracking.Fingerprint(day));
    }

    [Fact]
    public void Tracking_adds_a_record_per_voucher_with_its_remote_id_amount_and_hash()
    {
        var export = Export();

        var tracked = TallyTracking.Track(export, [], [Receipt("RC1"), Receipt("RC2", 250m)]);

        tracked.Added.Count.ShouldBe(2);
        var record = tracked.Current["RC2"];
        record.ShouldSatisfyAllConditions(
            r => r.ExportId.ShouldBe(export.Id),
            r => r.RemoteId.ShouldBe("HC360-RC2"),
            r => r.VoucherType.ShouldBe("Receipt"),
            r => r.Amount.ShouldBe(250m),
            r => r.Hash.ShouldBe(TallyTracking.Fingerprint(Receipt("RC2", 250m))),
            r => r.InDayBook.ShouldBeTrue(),
            r => r.Status.ShouldBe(TallyVoucherStatus.NotSent));
    }

    [Fact]
    public void A_voucher_that_left_the_day_book_stays_tracked_only_if_tally_may_hold_it()
    {
        var export = Export();
        var tracked = TallyTracking.Track(export, [], [Receipt("RC1"), Receipt("RC2"), Receipt("RC3")]);
        TallyTracking.Apply(tracked.Current, [Receipt("RC1"), Receipt("RC2")], Answers(("RC1", AccountingVoucherOutcome.Created), ("RC2", AccountingVoucherOutcome.Rejected)), Run, TestData.Now);

        var again = TallyTracking.Track(export, [.. tracked.Current.Values], []);

        again.Removed.Select(r => r.Key).ShouldBe(["RC2", "RC3"], ignoreOrder: true);
        var sent = tracked.Current["RC1"];
        sent.InDayBook.ShouldBeFalse();
        sent.Status.ShouldBe(TallyVoucherStatus.NoLongerInHc360);
    }

    [Fact]
    public void Answers_confirm_vouchers_for_the_run_and_a_retry_sends_only_the_rest()
    {
        var export = Export();
        IReadOnlyList<AccountingVoucher> day = [Receipt("RC1"), Receipt("RC2")];
        var tracked = TallyTracking.Track(export, [], day);

        TallyTracking.Apply(tracked.Current, day, Answers(("RC1", AccountingVoucherOutcome.Created), ("RC2", AccountingVoucherOutcome.Rejected)), Run, TestData.Now);

        tracked.Current["RC1"].ShouldSatisfyAllConditions(
            r => r.Status.ShouldBe(TallyVoucherStatus.InTally),
            r => r.SentHash.ShouldBe(r.Hash),
            r => r.ConfirmedRunId.ShouldBe(Run),
            r => r.AnsweredAt.ShouldBe(TestData.Now));
        tracked.Current["RC2"].ShouldSatisfyAllConditions(
            r => r.Status.ShouldBe(TallyVoucherStatus.Refused),
            r => r.AnswerMessage.ShouldBe("Ledger 'Cash' does not exist!"));
        day.Where(v => TallyTracking.NeedsSending(tracked.Current[v.Key], Run)).Select(v => v.Key).ShouldBe(["RC2"]);
        day.Where(v => TallyTracking.NeedsSending(tracked.Current[v.Key], Guid.CreateVersion7())).Count().ShouldBe(2);
    }

    [Fact]
    public void A_voucher_not_sent_keeps_the_copy_tally_already_confirmed()
    {
        IReadOnlyList<AccountingVoucher> day = [Receipt("RC1")];
        var tracked = TallyTracking.Track(Export(), [], day);
        TallyTracking.Apply(tracked.Current, day, Answers(("RC1", AccountingVoucherOutcome.Created)), Run, TestData.Now);

        TallyTracking.Apply(tracked.Current, day, Answers(("RC1", AccountingVoucherOutcome.NotSent)), Guid.CreateVersion7(), TestData.Now.AddDays(1));

        tracked.Current["RC1"].ShouldSatisfyAllConditions(r => r.Answer.ShouldBe(TallyVoucherAnswer.Created), r => r.AnsweredAt.ShouldBe(TestData.Now));
    }

    [Fact]
    public void Counts_that_cover_every_voucher_confirm_them_when_the_provider_gives_no_answers_per_voucher()
    {
        IReadOnlyList<AccountingVoucher> day = [Receipt("RC1"), Receipt("RC2")];
        var tracked = TallyTracking.Track(Export(), [], day);

        TallyTracking.Apply(tracked.Current, day, new AccountingExportResult(true, 1, 1, 0, null), Run, TestData.Now);

        tracked.Current.Values.ShouldAllBe(r => r.Answer == TallyVoucherAnswer.Accepted);
    }

    [Fact]
    public void A_voucher_changed_after_tally_confirmed_it_is_changed_since_sent()
    {
        var export = Export();
        IReadOnlyList<AccountingVoucher> day = [Receipt("RC1")];
        var tracked = TallyTracking.Track(export, [], day);
        TallyTracking.Apply(tracked.Current, day, Answers(("RC1", AccountingVoucherOutcome.Created)), Run, TestData.Now);

        var again = TallyTracking.Track(export, [.. tracked.Current.Values], [Receipt("RC1", 450m)]);

        again.Current["RC1"].Status.ShouldBe(TallyVoucherStatus.ChangedSinceSent);
        TallyTracking.NeedsSending(again.Current["RC1"], Run).ShouldBeTrue();
    }

    [Fact]
    public void The_days_counts_come_from_the_vouchers_of_the_current_day_book()
    {
        var export = Export();
        IReadOnlyList<AccountingVoucher> day = [Receipt("RC1"), Receipt("RC2"), Receipt("RC3")];
        var tracked = TallyTracking.Track(export, [], day);
        TallyTracking.Apply(tracked.Current, day,
            Answers(("RC1", AccountingVoucherOutcome.Created), ("RC2", AccountingVoucherOutcome.Altered), ("RC3", AccountingVoucherOutcome.Rejected)), Run, TestData.Now);

        TallyTracking.Summarize(export, tracked.Current.Values);

        (export.VoucherCount, export.ConfirmedCount, export.RejectedCount, export.CreatedCount, export.AlteredCount, export.ErrorCount).ShouldBe((3, 2, 1, 1, 1, 1));
        TallyTracking.Count(tracked.Current.Values).ShouldBe(new TallySending(3, 2, 0, 1, 0, 0, 0, 0));
    }

    [Fact]
    public void Marking_imported_records_the_day_book_as_tally_holds_it()
    {
        IReadOnlyList<AccountingVoucher> day = [Receipt("RC1"), Receipt("RC2")];
        var tracked = TallyTracking.Track(Export(), [], day);

        TallyTracking.MarkImported(tracked.Current.Values, TestData.Now);

        tracked.Current.Values.ShouldAllBe(r => r.Answer == TallyVoucherAnswer.Imported && r.Status == TallyVoucherStatus.InTally);
        TallyTracking.Count(tracked.Current.Values).Complete.ShouldBeTrue();
        day.ShouldAllBe(v => TallyTracking.NeedsSending(tracked.Current[v.Key], Run));
    }

    [Fact]
    public void References_shared_by_vouchers_are_reported() =>
        TallyTracking.DuplicateKeys([Receipt("RC1"), Receipt("RC2"), Receipt("RC1")]).ShouldBe(["RC1"]);

    [Fact]
    public void The_check_against_tally_records_each_verdict_and_what_only_tally_holds()
    {
        var export = Export();
        IReadOnlyList<AccountingVoucher> day = [Receipt("RC1"), Receipt("RC2")];
        var tracked = TallyTracking.Track(export, [], [.. day, Receipt("RC9")]);
        TallyTracking.Apply(tracked.Current, [.. day, Receipt("RC9")],
            Answers(("RC1", AccountingVoucherOutcome.Created), ("RC2", AccountingVoucherOutcome.Created), ("RC9", AccountingVoucherOutcome.Created)), Run, TestData.Now);
        var records = TallyTracking.Track(export, [.. tracked.Current.Values], day).Current.Values.Concat([tracked.Current["RC9"]]).ToList();
        var readBack = new AccountingReadBack(true, "Tally holds 1 of the 2 voucher(s)", [
            new AccountingVoucherCheck("RC1", AccountingVoucherMatch.Matched, 500m, "1", null),
            new AccountingVoucherCheck("RC2", AccountingVoucherMatch.AmountDiffers, 450m, "2", "Tally holds ₹450.00; HC360 sent ₹500.00."),
        ], [
            new AccountingVoucherFound("RC9", "Receipt", "9", Day, 500m, true),
            new AccountingVoucherFound("DF-20260925-001", "Journal", "DF-20260925-001", Day, 1_200m, true),
            new AccountingVoucherFound("NHPUOB260000004", "Sales", "4", Day, 900m, true),
        ]);

        var (added, removed) = TallyTracking.ApplyCheck(export, records, readBack, new HashSet<string>(["NHPUOB260000004"]), TestData.Now);

        records.Single(r => r.Key == "RC1").Status.ShouldBe(TallyVoucherStatus.InTally);
        records.Single(r => r.Key == "RC2").ShouldSatisfyAllConditions(r => r.Status.ShouldBe(TallyVoucherStatus.DiffersInTally), r => r.TallyAmount.ShouldBe(450m));
        records.Single(r => r.Key == "RC9").ShouldSatisfyAllConditions(r => r.Status.ShouldBe(TallyVoucherStatus.NoLongerInHc360), r => r.TallyDetail.ShouldBe("Still in Tally."));
        added.ShouldHaveSingleItem().ShouldSatisfyAllConditions(r => r.Key.ShouldBe("DF-20260925-001"), r => r.Status.ShouldBe(TallyVoucherStatus.OnlyInTally));
        removed.ShouldBeEmpty();
        (export.TallyCheck, export.TallyDifferences).ShouldBe((TallyCheckStatus.Differences, 3));
    }

    [Fact]
    public void A_voucher_that_left_the_day_book_and_is_gone_from_tally_is_dropped()
    {
        var export = Export();
        var tracked = TallyTracking.Track(export, [], [Receipt("RC9")]);
        TallyTracking.Apply(tracked.Current, [Receipt("RC9")], Answers(("RC9", AccountingVoucherOutcome.Created)), Run, TestData.Now);
        var records = TallyTracking.Track(export, [.. tracked.Current.Values], []).Current.Values.Concat([tracked.Current["RC9"]]).ToList();

        var (_, removed) = TallyTracking.ApplyCheck(export, records, new AccountingReadBack(true, null, [], []), new HashSet<string>(), TestData.Now);

        removed.ShouldHaveSingleItem().Key.ShouldBe("RC9");
        export.TallyCheck.ShouldBe(TallyCheckStatus.Matched);
    }

    [Fact]
    public void A_record_found_only_in_tally_becomes_ordinary_when_the_day_book_has_the_voucher_again()
    {
        var export = Export();
        var (added, _) = TallyTracking.ApplyCheck(export, [], new AccountingReadBack(true, null, [], [new AccountingVoucherFound("PH-NH-20260925", "Sales", "1", Day, 10m, true)]),
            new HashSet<string>(), TestData.Now);

        var tracked = TallyTracking.Track(export, added, [Receipt("PH-NH-20260925", 10m)]);

        tracked.Added.ShouldBeEmpty();
        tracked.Current["PH-NH-20260925"].ShouldSatisfyAllConditions(r => r.Match.ShouldBe(TallyMatch.NotChecked), r => r.Status.ShouldBe(TallyVoucherStatus.NotSent));
    }
}
