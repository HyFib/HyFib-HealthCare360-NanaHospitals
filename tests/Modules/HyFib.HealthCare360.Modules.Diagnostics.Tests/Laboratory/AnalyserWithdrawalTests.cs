using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Platform.Contracts;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

/// <summary>
/// What an analyser says of a test instead of a value: a result it withdraws (posted in error, deleted) stops counting
/// before verification, and is put to a person after; one it could not obtain is noted for the bench. Sent again,
/// neither changes anything.
/// </summary>
public sealed class AnalyserWithdrawalTests
{
    private const string Barcode = "000000123";

    private static readonly AnalyserReading Final = new("mmol/L", "H", "F", "Haemolysed");

    private static InstrumentResult Withdrawal(string status = "W") =>
        new("K", "5.2", "mmol/L", "H", Now) { Status = status, Kind = InstrumentResultKind.Withdrawn };

    private static InstrumentResult NotObtained() =>
        new("K", string.Empty, null, null, Now) { Status = "X", Kind = InstrumentResultKind.NotObtained };

    /// <summary>Potassium 5.2 mmol/L, flagged high, from <paramref name="instrument"/> (null: typed in), not yet verified.</summary>
    private static LabResult Entered(string? instrument = "EM360")
    {
        var result = Result();
        var capture = Capture("5.2", 5.2m, ResultFlag.High) with { Delta = new DeltaOutcome(4.1m, Now.AddDays(-1), 26.8m, true) };
        result.Record(instrument is null ? capture : capture with { InstrumentCode = instrument, Reading = Final }, null, Now).IsSuccess.ShouldBeTrue();
        result.HoldForReview(["The result is flagged High."]);
        return result;
    }

    private static LabResult Verified(string? instrument = "EM360")
    {
        var result = Entered(instrument);
        result.Verify(UserId, Now, automatic: false).IsSuccess.ShouldBeTrue();
        return result;
    }

    [Fact]
    public void A_value_the_analyser_withdraws_before_verification_stops_counting_and_the_bench_is_told_why()
    {
        var result = Entered();

        var told = InstrumentResultSink.Told(result, Withdrawal(), "EM360", Barcode);

        told.ShouldBe(new InstrumentResultSink.TestTold(true));
        (result.Status, result.ValueText, result.NumericValue, result.Flag, result.EnteredAt).ShouldBe((LabResultStatus.Pending, null, null, null, null));
        (result.InstrumentCode, result.InstrumentFlags, result.InstrumentStatus, result.InstrumentComment, result.DeltaExceeded)
            .ShouldBe((null, null, null, null, false));
        result.HeldReasons.ShouldBe(["EM360 withdrew its result 5.2 mmol/L (status W): run the test again or enter the result by hand."]);
        result.Verify(UserId, Now, automatic: false).Error!.Code.ShouldBe("lab.result_missing");
    }

    [Fact]
    public void A_value_entered_after_the_withdrawal_takes_its_place()
    {
        var result = Entered();
        InstrumentResultSink.Told(result, Withdrawal(), "EM360", Barcode);

        result.Record(Capture("4.4", 4.4m), UserId, Now.AddMinutes(10)).IsSuccess.ShouldBeTrue();

        (result.Status, result.ValueText, result.EnteredBy).ShouldBe((LabResultStatus.Entered, "4.4", UserId));
        result.HeldReasons.ShouldBeEmpty();
    }

    [Fact]
    public void A_withdrawal_sent_again_or_with_no_value_of_the_analysers_on_the_test_changes_nothing()
    {
        var withdrawn = Entered();
        InstrumentResultSink.Told(withdrawn, Withdrawal(), "EM360", Barcode);
        List<string> told = [.. withdrawn.HeldReasons];

        InstrumentResultSink.Told(withdrawn, Withdrawal(), "EM360", Barcode).ShouldBe(new InstrumentResultSink.TestTold(false));
        withdrawn.HeldReasons.ShouldBe(told);

        // Nothing stored yet.
        var pending = Result();
        InstrumentResultSink.Told(pending, Withdrawal(status: "D"), "EM360", Barcode).ShouldBe(new InstrumentResultSink.TestTold(false));
        pending.HeldReasons.ShouldBeEmpty();

        // A value typed in, or from another analyser, is a person's or that analyser's.
        var typedIn = Entered(instrument: null);
        InstrumentResultSink.Told(typedIn, Withdrawal(), "EM360", Barcode).ShouldBe(new InstrumentResultSink.TestTold(false));
        var fromXn550 = Entered("XN550");
        InstrumentResultSink.Told(fromXn550, Withdrawal(), "EM360", Barcode).ShouldBe(new InstrumentResultSink.TestTold(false));
        (typedIn.ValueText, fromXn550.ValueText).ShouldBe(("5.2", "5.2"));
    }

    [Fact]
    public void A_verified_value_the_analyser_withdraws_stands_and_the_withdrawal_is_kept_for_a_person()
    {
        var verified = Verified();
        var withdrawal = Withdrawal();

        var told = InstrumentResultSink.Told(verified, withdrawal, "EM360", Barcode);

        told.Changed.ShouldBeFalse();
        var kept = told.Kept.ShouldNotBeNull();
        (kept.Reason, kept.LabResultId, kept.Reading).ShouldBe((UnmatchedResultReason.Withdrawn, verified.Id, withdrawal));
        kept.Detail.ShouldBe(
            $"Potassium on sample {Barcode} is verified as 5.2 mmol/L; EM360 withdrew it (status W). Check it, and reopen or amend it if it is wrong.");
        (verified.Status, verified.ValueText).ShouldBe((LabResultStatus.Verified, "5.2"));

        // A verified value typed in, or from another analyser, was never this analyser's to withdraw.
        InstrumentResultSink.Told(Verified(instrument: null), withdrawal, "EM360", Barcode).ShouldBe(new InstrumentResultSink.TestTold(false));
        InstrumentResultSink.Told(Verified("XN550"), withdrawal, "EM360", Barcode).ShouldBe(new InstrumentResultSink.TestTold(false));
    }

    [Fact]
    public void A_result_the_analyser_could_not_obtain_is_noted_on_the_pending_test_once()
    {
        var pending = Result();

        InstrumentResultSink.Told(pending, NotObtained(), "EM360", Barcode).ShouldBe(new InstrumentResultSink.TestTold(true));

        pending.Status.ShouldBe(LabResultStatus.Pending);
        pending.HeldReasons.ShouldBe(["EM360 could not obtain a result for this test (status X): run it again or enter the result by hand."]);
        InstrumentResultSink.Told(pending, NotObtained(), "EM360", Barcode).ShouldBe(new InstrumentResultSink.TestTold(false));
        pending.HeldReasons.Count.ShouldBe(1);
    }

    [Fact]
    public void A_result_not_obtained_is_noted_beside_a_value_not_yet_verified_and_leaves_a_verified_one_alone()
    {
        var entered = Entered();
        InstrumentResultSink.Told(entered, NotObtained(), "EM360", Barcode).Changed.ShouldBeTrue();
        (entered.Status, entered.ValueText).ShouldBe((LabResultStatus.Entered, "5.2"));
        entered.HeldReasons.ShouldBe(["The result is flagged High.", "EM360 reported that it could not obtain a result for this test (status X)."]);

        var verified = Verified();
        InstrumentResultSink.Told(verified, NotObtained(), "EM360", Barcode).ShouldBe(new InstrumentResultSink.TestTold(false));
        verified.HeldReasons.ShouldBe(["The result is flagged High."]);
    }

    [Fact]
    public void Only_the_analysers_own_value_not_yet_verified_can_be_withdrawn()
    {
        var verified = Verified();

        verified.Withdraw("EM360", "Withdrawn.").ShouldBeFalse();
        verified.Note("Not obtained.").ShouldBeFalse();
        (verified.Status, verified.ValueText).ShouldBe((LabResultStatus.Verified, "5.2"));
        Entered().Withdraw("em360", "Withdrawn.").ShouldBeTrue("the analyser's code, however it is written");
    }

    private static UnmatchedLabResult Kept(string channel = "K", UnmatchedResultReason reason = UnmatchedResultReason.UnknownSample) => new()
    {
        FacilityId = Guid.CreateVersion7(),
        InstrumentCode = "EM360",
        SampleBarcode = Barcode,
        ChannelCode = channel,
        Value = "5.2",
        ReceivedAt = Now,
        Reason = reason,
        Detail = $"No sample of EM360's facility has the barcode {Barcode}.",
    };

    [Fact]
    public void The_analysers_results_kept_aside_for_the_sample_on_a_withdrawn_channel_are_discarded_once()
    {
        var potassium = Kept();
        var rerun = Kept(reason: UnmatchedResultReason.DiffersFromVerified);
        var sodium = Kept("NA");
        var heldForAPerson = Kept(reason: UnmatchedResultReason.Withdrawn);
        var assigned = Kept();
        assigned.Assign(Guid.CreateVersion7(), UserId, Now).IsSuccess.ShouldBeTrue();
        UnmatchedLabResult[] kept = [potassium, rerun, sodium, heldForAPerson, assigned];

        InstrumentResultSink.DiscardWithdrawn(kept, [Withdrawal(status: "D")], "EM360", Now).ShouldBe(2);

        (potassium.Status, potassium.DiscardReason, potassium.ResolvedBy, potassium.ResolvedAt)
            .ShouldBe((UnmatchedResultStatus.Discarded, "EM360 withdrew this result (status D).", null, Now));
        rerun.Status.ShouldBe(UnmatchedResultStatus.Discarded);
        (sodium.Status, heldForAPerson.Status, assigned.Status).ShouldBe((UnmatchedResultStatus.Open, UnmatchedResultStatus.Open, UnmatchedResultStatus.Assigned));

        // The withdrawal sent again finds nothing more to discard.
        InstrumentResultSink.DiscardWithdrawn(kept, [Withdrawal(status: "D")], "EM360", Now).ShouldBe(0);
        InstrumentResultSink.DiscardWithdrawn([Kept()], [], "EM360", Now).ShouldBe(0);
    }
}
