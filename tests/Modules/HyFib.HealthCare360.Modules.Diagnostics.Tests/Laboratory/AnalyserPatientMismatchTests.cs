using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Platform.Contracts;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

/// <summary>
/// An analyser that names another patient for a sample than the tube's has its results kept for the lab, which is told
/// whom it named; one whose patient can be the tube's, or that names no one, is taken as before. A result kept so and
/// put on a test by hand tells the pathologist verifying it.
/// </summary>
public sealed class AnalyserPatientMismatchTests
{
    private const string Barcode = "000000123";
    private const string Uhid = "NAN26000123";
    private const string Name = "Ravi Kumar Sharma";

    private static string? Mismatch(InstrumentPatient sent, AnalyserPatientId sentId) =>
        InstrumentResultSink.PatientMismatch("EM360", Barcode, sent, sentId, Uhid, Name);

    [Fact]
    public void Another_registered_patients_uhid_is_a_mismatch_whatever_the_name()
    {
        Mismatch(new("NAN26000999", "SHARMA RAVI KUMAR"), AnalyserPatientId.AnotherPatient)
            .ShouldBe($"EM360 sent sample {Barcode} for patient NAN26000999 (SHARMA RAVI KUMAR), not the sample's patient {Name} ({Uhid}).");
        Mismatch(new("NAN26000999", null), AnalyserPatientId.AnotherPatient)
            .ShouldBe($"EM360 sent sample {Barcode} for patient NAN26000999, not the sample's patient {Name} ({Uhid}).");
    }

    [Fact]
    public void Without_a_uhid_to_go_by_a_name_that_cannot_be_the_tube_patients_is_a_mismatch()
    {
        Mismatch(new(null, "VERMA MEERA"), AnalyserPatientId.NoPatient)
            .ShouldBe($"EM360 sent sample {Barcode} for VERMA MEERA, not the sample's patient {Name} ({Uhid}).");

        // The analyser's own patient number is no registered patient's: it is not shown as the patient's id.
        Mismatch(new("77", "VERMA MEERA"), AnalyserPatientId.NoPatient)
            .ShouldBe($"EM360 sent sample {Barcode} for VERMA MEERA, not the sample's patient {Name} ({Uhid}).");
    }

    [Theory]
    // The tube patient's UHID, whatever the name (corrected since the order was sent).
    [InlineData("NAN26000123", "VERMA MEERA", AnalyserPatientId.TubePatient)]
    // No registered patient's id, and a name that can be the tube patient's.
    [InlineData("77", "SHARMA RAVI", AnalyserPatientId.NoPatient)]
    [InlineData(null, "R K SHARMA", AnalyserPatientId.NoPatient)]
    // Nothing to compare by.
    [InlineData("77", null, AnalyserPatientId.NoPatient)]
    [InlineData(null, "12345", AnalyserPatientId.NoPatient)]
    public void A_patient_who_can_be_the_tube_patient_or_no_one_to_compare_is_no_mismatch(string? id, string? name, AnalyserPatientId sentId) =>
        Mismatch(new(id, name), sentId).ShouldBeNull();

    [Fact]
    public void What_the_analyser_sent_is_shown_on_one_line_and_cut_to_64_characters()
    {
        var detail = Mismatch(new("NAN\t26000999", "VERMA\r\nMEERA " + new string('X', 80)), AnalyserPatientId.AnotherPatient).ShouldNotBeNull();

        detail.ShouldStartWith($"EM360 sent sample {Barcode} for patient NAN 26000999 (VERMA MEERA {new string('X', 52)}), not");
        detail.ShouldNotContain('\n');
    }

    [Fact]
    public void A_patient_mismatch_put_on_a_test_by_hand_tells_the_pathologist_whom_the_analyser_named()
    {
        var detail = $"EM360 sent sample {Barcode} for patient NAN26000999, not the sample's patient {Name} ({Uhid}).";

        UnmatchedResultService.HeldReasons(Kept(UnmatchedResultReason.PatientMismatch, detail)).ShouldBe(
        [
            UnmatchedResultService.AssignedReason,
            $"The analyser named another patient for the sample: {detail}",
        ]);
        UnmatchedResultService.HeldReasons(Kept(UnmatchedResultReason.UnknownSample, "No sample of EM360's facility has the barcode 000000999."))
            .ShouldBe([UnmatchedResultService.AssignedReason]);
    }

    private static UnmatchedLabResult Kept(UnmatchedResultReason reason, string detail) => new()
    {
        FacilityId = Guid.CreateVersion7(),
        InstrumentCode = "EM360",
        SampleBarcode = Barcode,
        ChannelCode = "GLU",
        Value = "98",
        ReceivedAt = Now,
        Reason = reason,
        Detail = detail,
    };
}
