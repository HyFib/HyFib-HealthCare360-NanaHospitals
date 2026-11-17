using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Laboratory.Astm;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabOrders;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;
using HyFib.HealthCare360.Platform.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The results HC360 calculates (N7: "Some parameters are not run, which means calculated: indirect bilirubin, globulin,
/// A/G ratio, VLDL") over the real API: calculated from what is entered on the same order and sample, marked calculated
/// with the formula and its inputs for the verifier, never verified automatically, calculated again when an input
/// changes (and taken back to unverified when it was verified), corrected with an amendment of an input, and replaced by
/// a value measured for it.
/// </summary>
public sealed class LabCalculatedResultTests(HospitalServer server)
{
    private static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromHours(5.5), "IST", "IST");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Indirect_bilirubin_globulin_and_the_ag_ratio_are_calculated_from_what_is_entered_and_wait_for_a_person()
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var tube = await LabVerificationTests.ReceivedSampleAsync(server, "LAB-LFT");

        var answered = await EnterAsync(lab, tube, new() { ["TBIL"] = "1.20", ["DBIL"] = "0.30", ["TP"] = "7.2", ["ALB"] = "4.1" });

        answered.Single(l => l.TestCode == "IBIL").Value.ShouldBe("0.90", "what was calculated comes back with what was entered");
        var sheet = await SheetAsync(lab, tube);
        var indirect = Line(sheet, "IBIL");
        (indirect.Value, indirect.Status, indirect.IsCalculated, indirect.AutoValidated, indirect.InstrumentCode).ShouldBe(("0.90", LabResultStatus.Entered, true, false, null));
        indirect.Formula.ShouldBe("Bilirubin, total − Bilirubin, direct");
        indirect.HeldReasons.ShouldBe(["Calculated by HC360: Bilirubin, total − Bilirubin, direct = 1.20 − 0.30 = 0.90 mg/dL."]);
        indirect.Flag.ShouldBe(ResultFlag.Normal, "its reference interval applies as to any result");
        (Line(sheet, "GLOB").Value, Line(sheet, "GLOB").IsCalculated).ShouldBe(("3.1", true));
        Line(sheet, "AGR").Value.ShouldBe("1.32");
        Line(sheet, "TP").IsCalculated.ShouldBeFalse();

        // The total protein corrected before verification: the globulin and the ratio follow.
        await EnterAsync(lab, tube, new() { ["TP"] = "6.2" });
        sheet = await SheetAsync(lab, tube);
        (Line(sheet, "GLOB").Value, Line(sheet, "AGR").Value).ShouldBe(("2.1", "1.95"));
        Line(sheet, "GLOB").HeldReasons.ShouldBe(["Calculated by HC360: Total protein − Albumin = 6.2 − 4.1 = 2.1 g/dL."]);
    }

    [Fact]
    public async Task An_input_corrected_after_verification_takes_what_was_calculated_from_it_back_to_unverified()
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var tube = await LabVerificationTests.ReceivedSampleAsync(server, "LAB-LFT");
        await EnterAsync(lab, tube, new() { ["TP"] = "7.2", ["ALB"] = "4.1" });
        var sheet = await SheetAsync(lab, tube);
        string[] verified = ["TP", "ALB", "GLOB", "AGR"];
        await pathologist.PostAsync<VerificationOutcome>("/api/v1/lab/results/verify",
            new VerifyResultsRequest([.. verified.Select(code => Line(sheet, code).ResultId)], null, null, Release: false));
        (await SheetAsync(lab, tube)).Lines.Where(l => verified.Contains(l.TestCode)).ShouldAllBe(l => l.Status == LabResultStatus.Verified);

        // The total protein is reopened and corrected: the globulin and the ratio are calculated again, and wait for a person.
        await pathologist.PostAsync($"/api/v1/lab/results/{Line(sheet, "TP").ResultId}/reopen", new ReopenResultRequest("Rerun after a haemolysed aliquot"));
        await EnterAsync(lab, tube, new() { ["TP"] = "6.6" });

        sheet = await SheetAsync(lab, tube);
        (Line(sheet, "GLOB").Value, Line(sheet, "GLOB").Status).ShouldBe(("2.5", LabResultStatus.Entered));
        (Line(sheet, "AGR").Value, Line(sheet, "AGR").Status).ShouldBe(("1.64", LabResultStatus.Entered));
        Line(sheet, "ALB").Status.ShouldBe(LabResultStatus.Verified, "an input that did not change stays verified");
    }

    [Fact]
    public async Task Vldl_is_calculated_below_400_mg_per_dl_of_triglycerides_and_a_measured_one_wins()
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var tube = await LabVerificationTests.ReceivedSampleAsync(server, "LAB-LIPID");

        await EnterAsync(lab, tube, new() { ["CHOL"] = "200", ["HDL"] = "45", ["TG"] = "150" });
        var sheet = await SheetAsync(lab, tube);
        (Line(sheet, "VLDL").Value, Line(sheet, "VLDL").IsCalculated).ShouldBe(("30", true));
        Line(sheet, "LDL").Value.ShouldBe("125");

        // From 400 mg/dL up, Friedewald does not hold: the values calculated before go, and the tests wait, with why.
        await EnterAsync(lab, tube, new() { ["TG"] = "452" });
        sheet = await SheetAsync(lab, tube);
        (Line(sheet, "VLDL").Value, Line(sheet, "VLDL").Status).ShouldBe((null, LabResultStatus.Pending));
        Line(sheet, "VLDL").HeldReasons.ShouldBe(
            ["Not calculated: The triglycerides are 452 mg/dL: from 400 up, VLDL is not calculated (Friedewald). Measure it, or enter it by hand."]);

        // The analyser measures it: its value stands, as an analyser's.
        (await AnalyserSendsAsync("EM360", $"O|1|{tube.Barcode}||^^^VLDL|R", "R|1|^^^VLDL|61|mg/dL||H||F")).ShouldNotBeNull().Accepted.ShouldBeTrue();
        var measured = Line(await SheetAsync(lab, tube), "VLDL");
        (measured.Value, measured.InstrumentCode, measured.IsCalculated).ShouldBe(("61", "EM360", false));
    }

    [Fact]
    public async Task An_amendment_of_an_input_corrects_the_released_results_calculated_from_it()
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var tube = await LabVerificationTests.ReceivedSampleAsync(server, "LAB-LFT");
        await EnterAsync(lab, tube, new()
        {
            ["TBIL"] = "0.80",
            ["DBIL"] = "0.20",
            ["AST"] = "28",
            ["ALT"] = "31",
            ["ALP"] = "92",
            ["GGT"] = "24",
            ["TP"] = "7.2",
            ["ALB"] = "4.1",
        });
        var sheet = await SheetAsync(lab, tube);
        await pathologist.PostAsync<VerificationOutcome>("/api/v1/lab/results/verify",
            new VerifyResultsRequest([.. sheet.Lines.Select(l => l.ResultId)], null, null, Release: true));
        var order = await lab.GetAsync<LabOrderDetail>($"/api/v1/lab/orders/{sheet.LabOrderId}");
        var reportId = order.ReportId.ShouldNotBeNull();

        await pathologist.PostAsync<AmendmentOutcome>($"/api/v1/lab/reports/{reportId}/amend", new AmendReportRequest(
            "Total protein re-read from the analyser printout", [new AmendmentChange(Line(sheet, "TP").ResultId, "6.8", null, null, null)], null));

        var report = await pathologist.GetAsync<LabReportView>($"/api/v1/lab/reports/{reportId}");
        var changes = report.Amendments.ShouldHaveSingleItem().Changes;
        changes.ShouldContain("Globulin (calculated): 3.1 g/dL -> 2.7 g/dL, calculated from the corrected results");
        changes.ShouldContain("Albumin/globulin ratio: 1.32 -> 1.52, calculated from the corrected results");
        sheet = await SheetAsync(lab, tube);
        (Line(sheet, "GLOB").Value, Line(sheet, "GLOB").Status, Line(sheet, "GLOB").IsCalculated).ShouldBe(("2.7", LabResultStatus.Verified, true));
    }

    private static ResultSheetLine Line(ResultSheet sheet, string code) => sheet.Lines.Single(l => l.TestCode == code);

    private static Task<ResultSheet> SheetAsync(ApiClient api, CreatedSample tube) => api.GetAsync<ResultSheet>($"/api/v1/lab/samples/{tube.Id}/results");

    /// <summary>Enters the given values by hand on the tube's tests of those codes.</summary>
    private static async Task<List<ResultSheetLine>> EnterAsync(ApiClient lab, CreatedSample tube, Dictionary<string, string> values)
    {
        var sheet = await SheetAsync(lab, tube);
        return await lab.PostAsync<List<ResultSheetLine>>("/api/v1/lab/results", new EnterResultsRequest(
            [.. values.Select(v => new ResultEntry(Line(sheet, v.Key).ResultId, v.Value, null, null, null, null))]));
    }

    /// <summary>An ASTM message from the analyser, read by the gateway's own mapper and handed to the LIS.</summary>
    private async Task<LabSubmitOutcome?> AnalyserSendsAsync(string code, params string[] records)
    {
        var raw = string.Join("\r", [$@"H|\^&|||{code}|||||||P|1", .. records, "L|1|N"]);
        var results = AstmMessageMapper.MapResults(AstmRecords.Parse(raw), code, raw, DateTimeOffset.UtcNow, India);
        if (results.Count == 0)
        {
            return null;
        }

        var instrument = new LabInstrumentSettings { Code = code, Name = code, Protocol = LabProtocol.Astm };
        return await server.Services.GetRequiredService<ILabMessageProcessor>().SubmitResultsAsync(instrument, results, raw, Ct);
    }
}
