using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Platform.Contracts;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

/// <summary>
/// An analyser's message sent again (its acknowledgement lost) changes nothing, and only that: a settled test's value read
/// otherwise, or a result kept aside that differs in anything the analyser said of it, is kept for a person.
/// </summary>
public sealed class AnalyserResendTests
{
    private const string Barcode = "000000123";

    private static readonly AnalyserReading Final = new("mmol/L", "N", "F", null);

    private static ResultCapture Sent(string value, decimal numeric, AnalyserReading reading) =>
        Capture(value, numeric) with { InstrumentCode = "EM360", Reading = reading };

    private static InstrumentResult Raw(string value) => new("K", value, "mmol/L", "N", Now);

    /// <summary>Potassium 4.2 mmol/L from <paramref name="instrument"/> (null: typed in), verified.</summary>
    private static LabResult Verified(string? instrument = "EM360")
    {
        var result = Result();
        result.Record(instrument is null ? Capture("4.2", 4.2m) : Capture("4.2", 4.2m) with { InstrumentCode = instrument, Reading = Final }, null, Now);
        result.Verify(UserId, Now, automatic: false).IsSuccess.ShouldBeTrue();
        return result;
    }

    [Fact]
    public void The_reading_on_a_verified_test_sent_again_changes_nothing()
    {
        var verified = Verified();

        InstrumentResultSink.Settled(verified, Sent("4.2", 4.2m, Final), Raw("4.2"), "EM360", Barcode).ShouldBeNull();
        InstrumentResultSink.Settled(verified, Sent("4.2", 4.2m, Final) with { InstrumentCode = "em360" }, Raw("4.2"), "em360", Barcode).ShouldBeNull();
    }

    [Theory]
    [InlineData("mmol/L", "N", "C", null, "status C")]
    [InlineData("mmol/L", "H", "F", null, "flags H")]
    [InlineData("mmol/L", "N", "F", "Haemolysed", "the comment \"Haemolysed\"")]
    [InlineData(null, "N", "F", null, "no unit")]
    [InlineData("mmol/L", "H", "C", null, "flags H, status C")]
    public void The_same_value_read_otherwise_on_a_verified_test_is_kept_with_what_differs(
        string? unit, string? flags, string? status, string? comment, string differs)
    {
        var verified = Verified();

        var kept = InstrumentResultSink.Settled(verified, Sent("4.2", 4.2m, new AnalyserReading(unit, flags, status, comment)), Raw("4.2"), "EM360", Barcode)
            .ShouldNotBeNull();

        (kept.Reason, kept.LabResultId).ShouldBe((UnmatchedResultReason.DiffersFromVerified, verified.Id));
        kept.Detail.ShouldBe($"Potassium on sample {Barcode} is verified as 4.2 mmol/L; EM360 sent the same value with {differs}.");
    }

    [Fact]
    public void The_same_value_for_a_verified_result_typed_in_or_from_another_analyser_is_kept()
    {
        InstrumentResultSink.Settled(Verified(instrument: null), Sent("4.2", 4.2m, Final), Raw("4.2"), "EM360", Barcode)
            .ShouldNotBeNull().Detail.ShouldBe($"Potassium on sample {Barcode} is verified as 4.2 mmol/L, typed in; EM360 sent the same value.");
        InstrumentResultSink.Settled(Verified("XN550"), Sent("4.2", 4.2m, Final), Raw("4.2"), "EM360", Barcode)
            .ShouldNotBeNull().Detail.ShouldBe($"Potassium on sample {Barcode} is verified as 4.2 mmol/L from XN550; EM360 sent the same value.");
    }

    [Fact]
    public void Another_value_for_a_verified_test_is_kept()
    {
        var kept = InstrumentResultSink.Settled(Verified(), Sent("5.0", 5.0m, Final), Raw("5.0"), "EM360", Barcode).ShouldNotBeNull();

        kept.Reason.ShouldBe(UnmatchedResultReason.DiffersFromVerified);
        kept.Detail.ShouldBe($"Potassium on sample {Barcode} is verified as 4.2 mmol/L; EM360 sent 5.0 mmol/L.");
    }

    [Fact]
    public void A_test_the_message_gives_twice_keeps_the_first_and_sets_aside_any_other_reading()
    {
        var entered = Result();
        entered.Record(Sent("4.2", 4.2m, Final), null, Now);

        InstrumentResultSink.Settled(entered, Sent("4.2", 4.2m, Final), Raw("4.2"), "EM360", Barcode).ShouldBeNull();
        var flagged = InstrumentResultSink.Settled(entered, Sent("4.2", 4.2m, Final with { Flags = "H" }), Raw("4.2"), "EM360", Barcode).ShouldNotBeNull();
        (flagged.Reason, flagged.Detail).ShouldBe((UnmatchedResultReason.RepeatedInMessage,
            $"EM360 sent Potassium for sample {Barcode} twice in one message; the test has 4.2 mmol/L, and the same value came again with flags H."));
        InstrumentResultSink.Settled(entered, Sent("5.0", 5.0m, Final), Raw("5.0"), "EM360", Barcode).ShouldNotBeNull().Detail
            .ShouldBe($"EM360 sent Potassium for sample {Barcode} twice in one message; the test has 4.2 mmol/L.");
    }

    private static UnmatchedLabResult Kept(
        string? barcode = "000000999", string? unit = "mg/dL", string? flags = "N", string? status = "F", string? comment = null, int minutesAgo = 5) => new()
        {
            FacilityId = Guid.CreateVersion7(),
            InstrumentCode = "EM360",
            SampleBarcode = barcode,
            ChannelCode = "GLU",
            Value = "98",
            InstrumentUnit = unit,
            InstrumentFlags = flags,
            InstrumentStatus = status,
            InstrumentComment = comment,
            ResultedAt = Now.AddMinutes(-minutesAgo),
            ReceivedAt = Now,
            Reason = UnmatchedResultReason.UnknownSample,
            Detail = "No sample of EM360's facility has the barcode 000000999.",
        };

    [Fact]
    public void Results_kept_aside_that_differ_only_in_what_the_analyser_said_of_them_are_each_kept()
    {
        var first = Kept();
        var flagged = Kept(flags: "H");
        var corrected = Kept(status: "C");
        var inMmol = Kept(unit: "mmol/L");
        var commented = Kept(comment: "Lipaemic sample");

        // In one message, and against the results kept from an earlier one.
        InstrumentResultSink.NotKeptYet([first, flagged, corrected, inMmol, commented], []).ShouldBe([first, flagged, corrected, inMmol, commented]);
        var later = Kept(status: "C", comment: "Rerun");
        InstrumentResultSink.NotKeptYet([later], [first, flagged, corrected, inMmol, commented]).ShouldBe([later]);
    }

    [Fact]
    public void The_same_result_kept_aside_is_kept_once()
    {
        var first = Kept();
        var twice = Kept();
        var measuredLater = Kept(minutesAgo: 1);

        InstrumentResultSink.NotKeptYet([first, twice], []).ShouldBe([first]);
        InstrumentResultSink.NotKeptYet([Kept(), measuredLater], [first]).ShouldBe([measuredLater]);
    }

    [Fact]
    public void Without_a_sample_id_each_result_is_kept()
    {
        var one = Kept(barcode: null);
        var another = Kept(barcode: null);

        InstrumentResultSink.NotKeptYet([one, another], [Kept(barcode: null)]).ShouldBe([one, another]);
    }
}
