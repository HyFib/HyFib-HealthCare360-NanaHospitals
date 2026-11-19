using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A urine entered by hand with the strip's own words (N9: "-" is nil, "+-" trace, "++" 2+), over the real API: each
/// is stored as the test's own answer and flagged as that answer, as a strip reader's word is.
/// </summary>
public sealed class LabStripWordTests(HospitalServer server)
{
    [Fact]
    public async Task Strip_words_entered_by_hand_are_stored_as_the_urine_tests_answers()
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var urine = await LabVerificationTests.ReceivedSampleAsync(server, "LAB-URINE-RE");
        var sheet = await lab.GetAsync<ResultSheet>($"/api/v1/lab/samples/{urine.Id}/results");
        var words = new Dictionary<string, string>
        {
            ["UR-PROT"] = "-",
            ["UR-GLU"] = "+-",
            ["UR-BLD"] = "++",
            ["UR-NIT"] = "-",
            ["UR-LE"] = "+ -",
        };

        var entered = await lab.PostAsync<List<ResultSheetLine>>("/api/v1/lab/results", new EnterResultsRequest(
            [.. words.Select(w => new ResultEntry(sheet.Lines.Single(l => l.TestCode == w.Key).ResultId, w.Value, null, null, null, null))]));

        string? Answer(string code) => entered.Single(l => l.TestCode == code).Value;
        (Answer("UR-PROT"), Answer("UR-GLU"), Answer("UR-BLD"), Answer("UR-NIT"), Answer("UR-LE")).ShouldBe(("Nil", "Trace", "2+", "Negative", "Trace"));
        entered.Single(l => l.TestCode == "UR-PROT").Flag.ShouldBe(ResultFlag.Normal);
        entered.Single(l => l.TestCode == "UR-BLD").Flag.ShouldBe(ResultFlag.Abnormal);
        entered.ShouldAllBe(l => l.InstrumentValue == null, "a word typed by hand is not an analyser's");
    }
}
