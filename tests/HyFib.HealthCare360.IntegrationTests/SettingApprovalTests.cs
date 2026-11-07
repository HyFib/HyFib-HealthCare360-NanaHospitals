using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Modules.AI.Services;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Pharmacy.Services;
using HyFib.HealthCare360.Modules.SupportServices.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// One administrator no longer switches off, alone, a control other people's second check relies on, raises a limit that
/// lets money out, or sends money, patients' data or alerts elsewhere (<see cref="SettingGuard"/>): the change waits for
/// another administrator who may change settings (<c>settings.change</c>), never the one who asked, and is made only over
/// the value it was asked against. The stricter direction is made at once. A credential asked for is kept with the
/// request as the settings store keeps it, encrypted, never in clear text. Over the real API and PostgreSQL; the hospital
/// administrator asks and the Super Administrator approves.
/// </summary>
public sealed class SettingApprovalTests(HospitalServer server)
{
    private const string Settings = "/api/v1/admin/settings";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Turning_the_second_pathologist_off_waits_for_another_administrator()
    {
        const string key = LabResultService.SeparateVerifierSetting;
        var maker = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var checker = await server.SignInAsync(HospitalServer.Users.Admin);
        var facilityId = await FacilityAsync(maker);
        await using var restore = await KeepAsync(maker, key, facilityId);

        // A second pathologist asked for is stricter: made at once.
        using (var on = await PutAsync(maker, key, JsonValue.Create(true), facilityId))
        {
            on.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await ReadAsync<bool?>(key, facilityId)).ShouldBe(true);

        // Off waits. The settings screens are told so before they save, and a save without a reason is refused, naming the change.
        var check = await maker.PostAsync<SettingChangeCheck>($"{Settings}/{key}/check",
            new SaveSettingRequest(JsonValue.Create(false), facilityId, false, null, await maker.SettingVersionAsync(key, facilityId)));
        (check.Waits, check.Setting, check.From, check.To).ShouldBe((true, "Second pathologist for lab corrections", "on", "off"));
        using (var unexplained = await PutAsync(maker, key, JsonValue.Create(false), facilityId, reason: null))
        {
            unexplained.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var problem = await unexplained.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
            problem.GetProperty("errors").GetProperty("reason")[0].GetString().ShouldNotBeNull()
                .ShouldStartWith("Second pathologist for lab corrections: on → off waits for a second administrator.");
        }

        const string reason = "Dr Rao is on leave this month; Dr Menon reports alone.";
        var asked = await AskAsync(maker, key, JsonValue.Create(false), facilityId, reason);
        (asked.Kind, asked.Status, asked.SubjectType, asked.SubjectId, asked.Reason)
            .ShouldBe((SettingGuard.ChangeKind, ApprovalStatus.Pending, SettingGuard.SubjectType, SettingGuard.SubjectId(key, facilityId), reason));
        asked.Summary.ShouldStartWith("Second pathologist for lab corrections: on → off");
        asked.Details.ShouldContain(new ApprovalDetail("In force now", "on"));
        asked.Details.ShouldContain(new ApprovalDetail("Asked for", "off"));
        asked.Details.ShouldContain(d => d.Label == "What it controls" && d.Value.Contains("second pathologist", StringComparison.Ordinal));

        // Nothing changes until another administrator approves it, and the settings list shows the change waiting.
        (await ReadAsync<bool?>(key, facilityId)).ShouldBe(true);
        (await maker.GetAsync<List<SettingDto>>($"{Settings}?prefix={key}")).Single(s => s.Key == key && s.FacilityId == facilityId)
            .Pending.ShouldNotBeNull().ApprovalId.ShouldBe(asked.Id);

        // Never by the administrator who asked, however many permissions they hold.
        (await RefusalAsync(maker, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        (await ReadAsync<bool?>(key, facilityId)).ShouldBe(true);

        var approved = await MakerChecker.ApproveAsync(checker, asked, "Agreed until Dr Rao is back.");
        (await ReadAsync<bool?>(key, facilityId)).ShouldBe(false);
        var audited = await ChangesAsync(key, asked.RequestedAt);
        var change = audited.ShouldHaveSingleItem();
        change.Severity.ShouldBe(AuditSeverity.Warning);
        change.Summary.ShouldNotBeNull().ShouldStartWith("Second pathologist for lab corrections: on → off");
        change.Summary.ShouldContain($"asked for by {asked.RequestedByName}");
        change.Summary.ShouldContain($"approved by {approved.DecidedByName}");
        asked.RequestedByName.ShouldNotBe(approved.DecidedByName);

        // Back on is stricter again: made at once.
        using (var back = await PutAsync(maker, key, JsonValue.Create(true), facilityId))
        {
            back.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await ReadAsync<bool?>(key, facilityId)).ShouldBe(true);
        (await maker.GetAsync<List<SettingDto>>($"{Settings}?prefix={key}")).Single(s => s.Key == key && s.FacilityId == facilityId).Pending.ShouldBeNull();
    }

    [Fact]
    public async Task Raising_the_own_authority_discount_limit_waits_and_is_not_made_over_a_change_made_meanwhile()
    {
        const string key = DiscountService.DefaultLimitSetting;
        var maker = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var checker = await server.SignInAsync(HospitalServer.Users.Admin);
        var facilityId = await FacilityAsync(maker);
        await using var restore = await KeepAsync(maker, key, facilityId);
        await SetAsync(key, 10m, facilityId);

        var asked = await AskAsync(maker, key, JsonValue.Create(25m), facilityId, "Festival discounts at the counter, as last year.");
        asked.Summary.ShouldStartWith("Own-authority discount limit: 10% → 25%");
        (await ReadAsync<decimal?>(key, facilityId)).ShouldBe(10m);

        // One change waits per setting: a second is refused while it does.
        (await RefusalAsync(maker, $"{Settings}/{key}",
                new SaveSettingRequest(JsonValue.Create(30m), facilityId, false, null, await maker.SettingVersionAsync(key, facilityId), "Thirty, then."), HttpMethod.Put))
            .ShouldBe((HttpStatusCode.Conflict, "approval.already_requested"));

        // Lowering it is stricter, made at once even while the raise waits...
        using (var lower = await PutAsync(maker, key, JsonValue.Create(5m), facilityId))
        {
            lower.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await ReadAsync<decimal?>(key, facilityId)).ShouldBe(5m);

        // ...and the raise, asked against 10%, is not made over it: approving it fails and changes nothing.
        (await RefusalAsync(checker, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "approval.stale"));
        (await ReadAsync<decimal?>(key, facilityId)).ShouldBe(5m);
        (await checker.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}")).Status.ShouldBe(ApprovalStatus.Failed);

        // Asked again, from the limit in force now, and approved, it is made.
        var again = await AskAsync(maker, key, JsonValue.Create(25m), facilityId, "Festival discounts at the counter, as last year.");
        again.Summary.ShouldStartWith("Own-authority discount limit: 5% → 25%");
        await MakerChecker.ApproveAsync(checker, again);
        (await ReadAsync<decimal?>(key, facilityId)).ShouldBe(25m);
    }

    [Fact]
    public async Task A_credential_waits_and_is_kept_with_the_request_only_encrypted()
    {
        var key = $"integrations.it{Guid.NewGuid():N}.secret";
        // Made up for each run, so no credential-shaped text sits in the source.
        var credential = $"rzp_live_{Guid.NewGuid():N}";
        var maker = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var checker = await server.SignInAsync(HospitalServer.Users.Admin);
        try
        {
            // Saved as an administrator may, without asking for a secret: the key makes it one.
            var asked = await AskAsync(maker, key, JsonNode.Parse($$"""{"keySecret":"{{credential}}"}"""), null, "The gateway issued live keys.");
            asked.Details.ShouldContain(new ApprovalDetail("In force now", "not set"));
            asked.Details.ShouldContain(new ApprovalDetail("Asked for", SettingGuard.NewSecretWords));
            (await ReadAsync<JsonElement?>(key, null)).ShouldBeNull("nothing is saved until it is approved");

            // Nothing of it in clear text with the request: its payload, details, summary and reason.
            await RequestHoldsNoneOfAsync(asked.Id, credential);

            await MakerChecker.ApproveAsync(checker, asked);

            // Approved, the settings store reads it back as asked, and keeps it encrypted.
            (await ReadAsync<JsonElement?>(key, null)).ShouldNotBeNull().GetProperty("keySecret").GetString().ShouldBe(credential);
            await using var scope = server.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var stored = await db.Set<SystemSetting>().AsNoTracking().SingleAsync(s => s.Key == key && s.FacilityId == null, Ct);
            stored.IsSecret.ShouldBeTrue();
            stored.Value.ShouldNotContain(credential);

            // Nor in the request decided, nor in the audit trail: the change, the setting's row, the request's asking and approval.
            await RequestHoldsNoneOfAsync(asked.Id, credential);
            string[] entities = [key, stored.Id.ToString(), asked.Id.ToString()];
            var audited = await db.Set<AuditEvent>().AsNoTracking()
                .Where(e => e.OccurredAt >= asked.RequestedAt && entities.Contains(e.EntityId))
                .ToListAsync(Ct);
            audited.ShouldContain(e => e.Action == "setting.changed" && e.Severity == AuditSeverity.Warning);
            audited.ShouldContain(e => e.Action == "approval.approved");
            audited.ShouldAllBe(e => !(e.Summary ?? string.Empty).Contains(credential) && !(e.Details ?? string.Empty).Contains(credential));
        }
        finally
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(key, null, CancellationToken.None);
        }
    }

    /// <summary>The guard names each setting by the key its module reads, and judges it from the module's own default.</summary>
    [Fact]
    public void The_guard_knows_each_setting_by_the_key_and_default_its_module_reads()
    {
        Switch(LabResultService.SeparateVerifierSetting).DefaultOn.ShouldBeFalse();
        Switch(VitalRegistrationService.ConfirmationSetting).DefaultOn.ShouldBeTrue();
        Switch(AdministrationOnDesktop.SettingKey).DefaultOn.ShouldBeTrue();
        Switch(SampleService.RequirePaymentSetting).DefaultOn.ShouldBeTrue();
        Switch(ShiftService.RequiredSetting).DefaultOn.ShouldBeTrue();
        (Switch(AuthService.BiometricUnlockSetting).DefaultOn, Switch(AuthService.BiometricUnlockSetting).OnIsLooser).ShouldBe((false, true));
        (Switch(AiSettingKeys.Enabled).DefaultOn, Switch(AiSettingKeys.Enabled).OnIsLooser).ShouldBe((false, true));

        Limit(DiscountService.DefaultLimitSetting).Default.ShouldBe(0m);
        Limit(DispensingService.MaxDiscountSetting).Default.ShouldBe(DispensingService.DefaultMaxDiscountPercent);
        Limit(CanteenService.StaffCreditLimitSetting).Default.ShouldBe(CanteenService.DefaultStaffCreditLimit);
        Limit(MarService.GraceSetting).Default.ShouldBe((decimal)MarRules.DefaultGrace.TotalMinutes);
        Limit(ResultEvaluator.DeltaPercentSetting).Default.ShouldBe(ResultEvaluator.DefaultDeltaPercent);
        Limit(ResultEvaluator.DeltaWindowSetting).Default.ShouldBe(ResultEvaluator.DefaultDeltaWindowDays);
        Limit(AuthService.MaxSessionsSetting).Default.ShouldBe(AuthService.DefaultMaxSessions);
        Limit(AuthService.IdleLockMinutesSetting).Default.ShouldBe(AuthService.DefaultIdleLockMinutes);

        string[] redirects =
        [
            DoctorFeeService.TdsSetting, PortalAddress.BaseUrlSetting, AbdmGatewaySettingKeys.Gateway, AbdmGatewaySettingKeys.Secret,
            AiSettingKeys.Provider, AiSettingKeys.OpenAi, AiSettingKeys.Speech, AiSettingKeys.AnthropicSecret, AiSettingKeys.OpenAiSecret, AiSettingKeys.SpeechSecret,
            AlertHeartbeat.UrlSetting, AlertHeartbeat.MinutesSetting, AlertEscalation.SecondContactSetting, AlertEscalation.SupportSetting,
            AlertEscalation.EscalateMinutesSetting, AlertEscalation.SupportMinutesSetting, "alerts.oncallPhone", "alerts.oncallWhatsApp",
            .. IntegrationNames.All.SelectMany(name => new[] { IntegrationNames.SettingsKey(name), IntegrationNames.SecretKey(name) }),
        ];
        redirects.ShouldAllBe(key => SettingGuard.RuleFor(key) is SettingRedirect);

        // A feature switched off removes a service, not a control: the Features screen saves it at once.
        FeatureCatalog.All.ShouldAllBe(feature => SettingGuard.RuleFor(feature.Key) == null);
    }

    private static SettingSwitch Switch(string key) => SettingGuard.RuleFor(key).ShouldBeOfType<SettingSwitch>();

    private static SettingLimit Limit(string key) => SettingGuard.RuleFor(key).ShouldBeOfType<SettingLimit>();

    private static async Task<Guid> FacilityAsync(ApiClient admin) => (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();

    private static async Task<HttpResponseMessage> PutAsync(ApiClient admin, string key, JsonNode? value, Guid? facilityId, string? reason = "As agreed.") =>
        await admin.SendRawAsync(HttpMethod.Put, $"{Settings}/{key}",
            new SaveSettingRequest(value, facilityId, false, null, await admin.SettingVersionAsync(key, facilityId), reason));

    /// <summary>Asks for a change that waits (202): the request, as the server answers it.</summary>
    private static async Task<ApprovalRequestDto> AskAsync(ApiClient maker, string key, JsonNode? value, Guid? facilityId, string reason)
    {
        using var response = await PutAsync(maker, key, value, facilityId, reason);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull();
    }

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, string path, object body, HttpMethod? method = null)
    {
        using var response = await api.SendRawAsync(method ?? HttpMethod.Post, path, body);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return (response.StatusCode, problem.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    private async Task<T?> ReadAsync<T>(string key, Guid? facilityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISettingsService>().GetAsync<T>(key, facilityId, Ct);
    }

    private async Task SetAsync<T>(string key, T value, Guid? facilityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsService>().SetAsync(key, value, facilityId, cancellationToken: Ct);
    }

    private async Task<List<AuditEvent>> ChangesAsync(string key, DateTimeOffset since)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.Action == "setting.changed" && e.EntityId == key && e.OccurredAt >= since)
            .ToListAsync(Ct);
    }

    /// <summary>The request row in <c>platform.approval_requests</c> holds <paramref name="text"/> nowhere a person or a backup reads.</summary>
    private async Task RequestHoldsNoneOfAsync(Guid approvalId, string text)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<ApprovalRequest>().IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(r => r.Id == approvalId, Ct);
        foreach (var held in new[] { row.Payload, row.Details, row.Summary, row.Reason, row.Outcome ?? string.Empty })
        {
            held.ShouldNotContain(text, Case.Sensitive);
        }
    }

    /// <summary>
    /// The facility's own value of <paramref name="key"/>, put back as it was when the test ends (the server is shared by
    /// every test of the run), with any change the test left waiting withdrawn.
    /// </summary>
    private async Task<IAsyncDisposable> KeepAsync(ApiClient maker, string key, Guid facilityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var before = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<SystemSetting>().AsNoTracking()
            .Where(s => s.Key == key && s.FacilityId == facilityId).Select(s => s.Value).FirstOrDefaultAsync(Ct);
        return new Restore(async () =>
        {
            await using var restore = server.Services.CreateAsyncScope();
            var db = restore.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var subject = SettingGuard.SubjectId(key, facilityId);
            foreach (var waiting in await db.Set<ApprovalRequest>().IgnoreQueryFilters().AsNoTracking()
                         .Where(r => r.SubjectId == subject && r.Status == ApprovalStatus.Pending).ToListAsync(CancellationToken.None))
            {
                using var _ = await maker.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{waiting.Id}/withdraw", new WithdrawApprovalRequest(waiting.Version));
            }

            var settings = restore.ServiceProvider.GetRequiredService<ISettingsService>();
            if (before is null)
            {
                await settings.RemoveAsync(key, facilityId, CancellationToken.None);
            }
            else
            {
                await settings.SetAsync(key, JsonNode.Parse(before), facilityId, cancellationToken: CancellationToken.None);
            }
        });
    }

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
