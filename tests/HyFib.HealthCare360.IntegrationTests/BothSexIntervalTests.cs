using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A patient recorded as transgender (Transgender Persons (Protection of Rights) Act 2019, s.4), or whose sex is not
/// known, is flagged and gets a critical-value call like anyone else. Adult haemoglobin has only a male and a female
/// interval; read against both, a value below 7 g/dL is critical, whichever applies.
/// </summary>
public sealed class BothSexIntervalTests(HospitalServer server)
{
    [Theory]
    [InlineData(Gender.Other)]
    [InlineData(Gender.Unknown)]
    public async Task A_critically_low_haemoglobin_is_flagged_and_needs_a_call_for_a_patient_neither_male_nor_female(Gender gender)
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var patient = await LabVerificationTests.RegisterPatientAsync(server, gender);
        var tube = await LabVerificationTests.ReceivedSampleAsync(server, "LAB-CBC", patient.Id);
        var sheet = await lab.GetAsync<ResultSheet>($"/api/v1/lab/samples/{tube.Id}/results");
        var haemoglobin = sheet.Lines.Single(l => l.TestCode == "HB");

        var line = (await lab.PostAsync<List<ResultSheetLine>>("/api/v1/lab/results", new EnterResultsRequest(
            [new ResultEntry(haemoglobin.ResultId, "5.0", null, null, null, null)]))).ShouldHaveSingleItem();

        line.Flag.ShouldBe(ResultFlag.CriticalLow, "below 7 g/dL, the critical limit of the male and the female interval");
        line.IsCritical.ShouldBeTrue("the doctor must be called before the result is verified");
        line.ReferenceText.ShouldBe("Male 13.0 - 17.0; Female 12.0 - 15.0");
    }
}
