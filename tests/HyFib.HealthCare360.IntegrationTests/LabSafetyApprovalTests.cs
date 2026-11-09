using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Quality;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabMasters;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;
using HyFib.HealthCare360.Modules.Diagnostics.Features.QualityControl;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The lab's safety settings where the lab asks for a second pathologist (<c>lab.separateVerifier</c>; maker-checker slice
/// 15): an override of failed QC for the day waits for another pathologist and the override given at verification covers
/// only the results verified with it; loosening a critical limit, or switching automatic verification on, waits for
/// another pathologist, while narrowing a limit or switching it off is made at once.
/// </summary>
public sealed class LabSafetyApprovalTests(HospitalServer server)
{
    private const string OverrideReason = "Control vial reconstituted wrongly; patient results checked against yesterday's";
    private const string VerifyPath = "/api/v1/lab/results/verify";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Where_the_lab_asks_for_a_second_pathologist_an_override_of_failed_qc_for_the_day_waits_for_one()
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var second = await server.SignInAsync(HospitalServer.Users.SecondPathologist);
        var (fbs, lot) = await GlucoseControlAsync(lab);
        var result = (await LabVerificationTests.EnterAsync(lab, await LabVerificationTests.ReceivedSampleAsync(server, "LAB-FBS"),
            new Dictionary<string, string> { ["LAB-FBS"] = "91" })).Single().ResultId;

        await using (await SeparateVerifierAsync())
        {
            try
            {
                var rejected = await RecordQcRunAsync(lab, lot, sds: 5);

                // The pathologist asks for the override on the QC screen: 202, and the assay is still locked.
                var asked = await AskAsync(pathologist, "/api/v1/lab/qc/overrides", new QcOverrideRequest(fbs.Id, OverrideReason));
                (asked.Kind, asked.Status).ShouldBe((QcOverrideApproval.Key, ApprovalStatus.Pending));
                asked.Summary.ShouldBe($"Override today's failed QC for {fbs.Name}");
                asked.Details.ShouldContain(new ApprovalDetail("Failed rules", "1-3s"));
                (await QcBoardAsync(lab, lot)).Locked.ShouldBeTrue("nothing is overridden until another pathologist approves");
                (await RefusedAsync(pathologist, VerifyPath, new VerifyResultsRequest([result], null, null, Release: false), HttpStatusCode.UnprocessableEntity))
                    .ShouldBe("lab.qc_lockout");

                // The one who asked does not approve it; the second pathologist does, and the override names both.
                (await RefusedAsync(pathologist, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version), HttpStatusCode.Forbidden))
                    .ShouldBe("approval.self");
                var approved = await MakerChecker.ApproveAsync(second, asked);
                approved.Outcome.ShouldBe($"Verification of {fbs.Name} opened for the day (1 rejected run(s) overridden)");
                (await QcBoardAsync(lab, lot)).ShouldSatisfyAllConditions(b => b.Locked.ShouldBeFalse(), b => b.Overridden.ShouldBeTrue());
                var audited = (await AuditAsync("lab.qc.override", rejected.Id)).ShouldHaveSingleItem();
                (audited.UserId, audited.Severity).ShouldBe((await IdAsync(second), AuditSeverity.Warning));
                audited.Summary.ShouldStartWith("QC lockout overridden for the day (asked for by ");

                // Open for the day: a result is verified now without an override of its own.
                (await pathologist.PostAsync<VerificationOutcome>(VerifyPath, new VerifyResultsRequest([result], null, null, Release: false)))
                    .ShouldSatisfyAllConditions(v => v.Verified.ShouldBe(1), v => v.QcOverrides.ShouldBe(0));
            }
            finally
            {
                await ClearQcAsync(lab, lot);
            }
        }
    }

    [Fact]
    public async Task An_override_asked_for_before_a_newer_qc_run_is_not_made()
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var second = await server.SignInAsync(HospitalServer.Users.SecondPathologist);
        var (fbs, lot) = await GlucoseControlAsync(lab);

        await using (await SeparateVerifierAsync())
        {
            try
            {
                await RecordQcRunAsync(lab, lot, sds: 5);
                var asked = await AskAsync(pathologist, "/api/v1/lab/qc/overrides", new QcOverrideRequest(fbs.Id, OverrideReason));

                // Before anyone decides, the control is run again and fails again: the request was about the earlier run.
                await RecordQcRunAsync(lab, lot, sds: 5);
                (await RefusedAsync(second, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version), HttpStatusCode.UnprocessableEntity))
                    .ShouldBe("approval.stale");
                (await QcBoardAsync(lab, lot)).Locked.ShouldBeTrue();
            }
            finally
            {
                await ClearQcAsync(lab, lot);
            }
        }
    }

    [Fact]
    public async Task Where_the_lab_asks_for_a_second_pathologist_an_override_at_verification_covers_only_the_results_verified()
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var (_, lot) = await GlucoseControlAsync(lab);
        var first = (await LabVerificationTests.EnterAsync(lab, await LabVerificationTests.ReceivedSampleAsync(server, "LAB-FBS"),
            new Dictionary<string, string> { ["LAB-FBS"] = "92" })).Single().ResultId;
        var next = (await LabVerificationTests.EnterAsync(lab, await LabVerificationTests.ReceivedSampleAsync(server, "LAB-FBS"),
            new Dictionary<string, string> { ["LAB-FBS"] = "87" })).Single().ResultId;

        await using (await SeparateVerifierAsync())
        {
            try
            {
                var rejected = await RecordQcRunAsync(lab, lot, sds: 5);

                // The pathologist verifies the first with an override: it covers that result, and the run is not overridden.
                (await pathologist.PostAsync<VerificationOutcome>(VerifyPath, new VerifyResultsRequest([first], OverrideReason, null, Release: false)))
                    .ShouldSatisfyAllConditions(v => v.Verified.ShouldBe(1), v => v.QcOverrides.ShouldBe(1));
                (await QcBoardAsync(lab, lot)).Locked.ShouldBeTrue("the override was for the results verified with it, not the day");
                (await AuditAsync("lab.qc.override", rejected.Id)).ShouldHaveSingleItem().Summary
                    .ShouldBe($"QC lockout overridden for 1 result(s) being verified: {OverrideReason}");

                // The next result of the assay is locked again: it needs an override of its own.
                (await RefusedAsync(pathologist, VerifyPath, new VerifyResultsRequest([next], null, null, Release: false), HttpStatusCode.UnprocessableEntity))
                    .ShouldBe("lab.qc_lockout");
            }
            finally
            {
                await ClearQcAsync(lab, lot);
            }
        }
    }

    [Fact]
    public async Task Without_a_second_pathologist_an_override_at_verification_opens_the_assay_for_the_day_as_before()
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var (fbs, lot) = await GlucoseControlAsync(lab);
        var first = (await LabVerificationTests.EnterAsync(lab, await LabVerificationTests.ReceivedSampleAsync(server, "LAB-FBS"),
            new Dictionary<string, string> { ["LAB-FBS"] = "92" })).Single().ResultId;
        try
        {
            await RecordQcRunAsync(lab, lot, sds: 5);
            (await pathologist.PostAsync<VerificationOutcome>(VerifyPath, new VerifyResultsRequest([first], OverrideReason, null, Release: false))).QcOverrides.ShouldBe(1);
            (await QcBoardAsync(lab, lot)).Locked.ShouldBeFalse();

            // And the QC screen's override is made at once, with no approval.
            await RecordQcRunAsync(lab, lot, sds: 5);
            using var response = await pathologist.SendRawAsync(HttpMethod.Post, "/api/v1/lab/qc/overrides", new QcOverrideRequest(fbs.Id, OverrideReason));
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        }
        finally
        {
            await ClearQcAsync(lab, lot);
        }
    }

    [Fact]
    public async Task Where_the_lab_asks_for_a_second_pathologist_loosening_a_critical_limit_waits_and_narrowing_one_is_made_at_once()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var second = await server.SignInAsync(HospitalServer.Users.SecondPathologist);
        var test = await NewAnalyteAsync(admin);
        test = await RangesAsync(admin, test, Interval(criticalLow: 2.5m, criticalHigh: 6.5m));

        await using (await SeparateVerifierAsync())
        {
            // Narrowing (6.5 → 6.2) is stricter: made at once.
            test = await RangesAsync(admin, test, Interval(criticalLow: 2.5m, criticalHigh: 6.2m));
            test.Ranges.ShouldHaveSingleItem().CriticalHigh.ShouldBe(6.2m);

            // Raising it (6.2 → 7.0) waits for a second pathologist, with a reason, and nothing changes meanwhile.
            (await RefusedAsync(admin, $"/api/v1/lab/tests/{test.Id}/ranges", new SetRangesRequest([Interval(2.5m, 7.0m)], test.Version), HttpStatusCode.BadRequest, put: true))
                .ShouldBe("validation");
            var asked = await AskAsync(admin, $"/api/v1/lab/tests/{test.Id}/ranges",
                new SetRangesRequest([Interval(2.5m, 7.0m)], test.Version, "The analyser's critical high per the new reagent's insert"), put: true);
            (asked.Kind, asked.Summary).ShouldBe((LabSafetyApproval.Key, $"Loosen the critical limits of {test.Name}"));
            asked.Details.ShouldContain(new ApprovalDetail("Critical limit", "Critical high 6.2 → 7.0 (male, age not known)"));
            (await TestAsync(admin, test.Id)).Ranges.ShouldHaveSingleItem().CriticalHigh.ShouldBe(6.2m);

            // A pathologist, not the one who asked, approves it.
            (await MakerChecker.ApproveAsync(pathologist, asked)).Outcome.ShouldBe($"Reference intervals of {test.Name} saved");
            (await TestAsync(admin, test.Id)).Ranges.ShouldHaveSingleItem().CriticalHigh.ShouldBe(7.0m);
            (await AuditByEntityAsync("lab.master.critical_limits", test.Id)).ShouldHaveSingleItem().Summary.ShouldNotBeNull()
                .ShouldContain("Critical high 6.2 → 7.0");

            // Taking a limit away is loosening too; the second pathologist may not approve their own request.
            var current = await TestAsync(admin, test.Id);
            var removal = await AskAsync(second, $"/api/v1/lab/tests/{test.Id}/ranges",
                new SetRangesRequest([Interval(2.5m, null)], current.Version, "Critical high to be decided by the lab committee"), put: true);
            (await RefusedAsync(second, $"/api/v1/approvals/{removal.Id}/approve", new ApproveRequest(null, removal.Version), HttpStatusCode.Forbidden))
                .ShouldBe("approval.self");
        }
    }

    [Fact]
    public async Task Where_the_lab_asks_for_a_second_pathologist_switching_automatic_verification_on_waits_and_off_is_made_at_once()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var test = await NewAnalyteAsync(admin);

        await using (await SeparateVerifierAsync())
        {
            // Off is stricter: saved at once.
            using (var off = await admin.SendRawAsync(HttpMethod.Put, $"/api/v1/lab/tests/{test.Id}", Save(test, autoValidation: false)))
            {
                off.StatusCode.ShouldBe(HttpStatusCode.NoContent, await off.Content.ReadAsStringAsync(Ct));
            }

            test = await TestAsync(admin, test.Id);
            test.AllowAutoValidation.ShouldBeFalse();

            // On waits: the rest of the edit is saved, automatic verification stays off until a pathologist approves.
            var asked = await AskAsync(admin, $"/api/v1/lab/tests/{test.Id}",
                Save(test, autoValidation: true, tatHours: 6) with { Reason = "Validated on the analyser over 200 samples" }, put: true);
            (asked.Kind, asked.Summary).ShouldBe((LabSafetyApproval.Key, $"Switch on automatic verification of {test.Name}"));
            var saved = await TestAsync(admin, test.Id);
            (saved.TatHours, saved.AllowAutoValidation).ShouldBe((6, false));

            (await MakerChecker.ApproveAsync(pathologist, asked)).Outcome.ShouldBe($"Automatic verification of {test.Name} on");
            (await TestAsync(admin, test.Id)).AllowAutoValidation.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Without_a_second_pathologist_critical_limits_and_automatic_verification_change_at_once_as_before()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var test = await NewAnalyteAsync(admin);
        test = await RangesAsync(admin, test, Interval(criticalLow: 2.5m, criticalHigh: 6.5m));
        test = await RangesAsync(admin, test, Interval(criticalLow: 2.0m, criticalHigh: null));
        test.Ranges.ShouldHaveSingleItem().CriticalHigh.ShouldBeNull();
    }

    // ------------------------------------------------------------------ helpers

    private static ReferenceRangeDto Interval(decimal? criticalLow, decimal? criticalHigh) =>
        new(null, null, 0, null, 3.5m, 5.1m, criticalLow, criticalHigh, null, null, null);

    /// <summary>A numeric analyte of this test alone, so no other test's results or limits change.</summary>
    private static async Task<LabTestDetail> NewAnalyteAsync(ApiClient admin)
    {
        var code = "SAFE" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var created = await admin.PostAsync<JsonObject>("/api/v1/lab/tests", new SaveLabTestRequest(
            code, $"Safety analyte {code}", null, LabSection.Biochemistry, SpecimenType.Serum, ContainerType.Plain, null, 4, null, null, null,
            IsOrderable: false, IsPanel: false, LabResultType.Numeric, "mmol/L", 1, null, null, null, null, AllowAutoValidation: true, null, false,
            null, null, 0, IsActive: true, null));
        return await TestAsync(admin, created["id"]!.GetValue<Guid>());
    }

    private static SaveLabTestRequest Save(LabTestDetail t, bool autoValidation, int? tatHours = null) => new(
        t.Code, t.Name, t.ShortName, t.Section, t.SpecimenType, t.Container, t.Method, tatHours ?? t.TatHours, t.Loinc, t.LoincName, t.ServiceItemId,
        t.IsOrderable, t.IsPanel, t.ResultType, t.Unit, t.Decimals, t.Options, t.InstrumentChannelCodes, t.QcGroup, t.DeltaThresholdPercent,
        autoValidation, t.Preparation, t.DrawSeparately, t.Interpretation, t.DefaultReferralLabId, t.DisplayOrder, t.IsActive, t.Version);

    private static async Task<LabTestDetail> RangesAsync(ApiClient admin, LabTestDetail test, ReferenceRangeDto range)
    {
        using var response = await admin.SendRawAsync(HttpMethod.Put, $"/api/v1/lab/tests/{test.Id}/ranges", new SetRangesRequest([range], test.Version));
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
        return await TestAsync(admin, test.Id);
    }

    private static Task<LabTestDetail> TestAsync(ApiClient api, Guid id) => api.GetAsync<LabTestDetail>($"/api/v1/lab/tests/{id}");

    /// <summary>The glucose assay and its level 1 control on the chemistry analyser (seeded with accepted runs).</summary>
    private static async Task<(LabTestListItem Fbs, QcLotDto Lot)> GlucoseControlAsync(ApiClient lab)
    {
        var fbs = (await lab.GetAsync<PagedResult<LabTestListItem>>("/api/v1/lab/tests?search=LAB-FBS")).Items.Single(t => t.Code == "LAB-FBS");
        var lot = (await lab.GetAsync<List<QcLotDto>>($"/api/v1/lab/qc/lots?labTestId={fbs.Id}")).First(l => l.Level == 1 && !l.IsExpired);
        await ClearQcAsync(lab, lot);
        return (fbs, lot);
    }

    private static Task<QcRunDto> RecordQcRunAsync(ApiClient lab, QcLotDto lot, int sds) =>
        lab.PostAsync<QcRunDto>("/api/v1/lab/qc/runs", new RecordQcRunRequest(lot.Id, lot.TargetMean + (sds * lot.TargetSd), null));

    private static async Task<QcStatusRow> QcBoardAsync(ApiClient api, QcLotDto lot) =>
        (await api.GetAsync<List<QcStatusRow>>("/api/v1/lab/qc/status")).Single(r => r.LotId == lot.Id);

    /// <summary>The server is shared by every test of the run: the assay is never left locked.</summary>
    private static async Task ClearQcAsync(ApiClient lab, QcLotDto lot)
    {
        if ((await QcBoardAsync(lab, lot)).Locked)
        {
            await RecordQcRunAsync(lab, lot, sds: 0);
        }
    }

    private async Task<List<AuditEventDto>> AuditAsync(string action, Guid runId)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        return [.. (await admin.GetAsync<PagedResult<AuditEventDto>>($"/api/v1/admin/audit?action={action}&entityType=QcRun&entityId={runId}")).Items];
    }

    private async Task<List<AuditEventDto>> AuditByEntityAsync(string action, Guid id)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        return [.. (await admin.GetAsync<PagedResult<AuditEventDto>>($"/api/v1/admin/audit?action={action}&entityId={id}")).Items];
    }

    private static async Task<Guid> IdAsync(ApiClient client) => (await client.GetAsync<AuthUser>("/api/v1/auth/me")).Id;

    /// <summary>Turns on the facility's second pathologist; disposing turns it off again.</summary>
    private async Task<IAsyncDisposable> SeparateVerifierAsync()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId
                         ?? throw new InvalidOperationException("The hospital administrator has no facility.");
        await admin.SaveSettingAsync(LabResultService.SeparateVerifierSetting, new SaveSettingRequest(JsonValue.Create(true), facilityId, false, null));
        return new Restore(async () =>
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(LabResultService.SeparateVerifierSetting, facilityId, Ct);
        });
    }

    /// <summary>Asks for a change: 202 with the request.</summary>
    private static async Task<ApprovalRequestDto> AskAsync(ApiClient maker, string path, object body, bool put = false)
    {
        using var response = await maker.SendRawAsync(put ? HttpMethod.Put : HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull();
    }

    /// <summary>Is refused with <paramref name="status"/>, and returns the refusal's code.</summary>
    private static async Task<string?> RefusedAsync(ApiClient client, string path, object body, HttpStatusCode status, bool put = false)
    {
        using var response = await client.SendRawAsync(put ? HttpMethod.Put : HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString();
    }

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
