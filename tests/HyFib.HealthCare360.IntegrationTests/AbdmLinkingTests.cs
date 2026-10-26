using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Features;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// HIP-initiated linking over the real API and a fake ABDM gateway (<see cref="FakeAbdmGateway"/>, ABDM M2): the medical
/// records officer links a signed consultation to the patient's ABHA address; the server starts a gateway session, asks
/// for a link token, and sends the care contexts when the gateway's signed <c>on-generate-token</c> brings it; the
/// gateway's <c>on_carecontext</c> marks them linked. A second request reuses the kept token, a refused link drops it,
/// a callback the gateway did not sign is refused, and a token for another ABHA address is ignored. The ABDM switch is hospital-wide, so these tests are in the
/// feature-switch collection and turn it off again, and remove the gateway set-up, in a <c>finally</c> block.
/// </summary>
[Collection(FeatureSwitchCollection.Name)]
public sealed class AbdmLinkingTests(HospitalServer server)
{
    private const string HipId = "IN2710000999";
    private const string TokenPath = "/v3/token/generate-token";
    private const string CareContextPath = "/hip/v3/link/carecontext";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeAbdmGateway Gateway => server.AbdmGateway;

    [Fact]
    public async Task A_signed_consultation_is_linked_to_the_patients_abha_through_the_gateway_and_its_signed_callbacks()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var medicalRecords = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var address = "meera." + AbdmExportTests.Letters(8) + "@sbx";
        var registration = AbdmExportTests.Registration(AbdmExportTests.NewAbha(), address);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", registration);
        var consultation = await AbdmExportTests.ConsultAsync(doctor, patient.Id, sign: true);
        var later = await AbdmExportTests.ConsultAsync(doctor, patient.Id, sign: true);
        var draft = await AbdmExportTests.ConsultAsync(doctor, patient.Id, sign: false);

        try
        {
            await SwitchAsync(enabled: true);
            await SetUpGatewayAsync(enabled: true);

            // Only those who export records link them, and only what the export would send.
            using (var notAllowed = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/abdm/links", Link(consultation)))
            {
                notAllowed.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }

            using (var notSigned = await medicalRecords.SendRawAsync(HttpMethod.Post, "/api/v1/abdm/links", Link(draft)))
            {
                notSigned.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
                (await Code(notSigned)).ShouldBe("abdm.not_exportable");
            }

            // No token is kept for the address yet: the server asks the gateway for one and waits for it.
            AbdmLinkView first;
            using (var accepted = await medicalRecords.SendRawAsync(HttpMethod.Post, "/api/v1/abdm/links", Link(consultation)))
            {
                accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
                first = (await accepted.Content.ReadFromJsonAsync<AbdmLinkView>(ApiClient.Json, Ct)).ShouldNotBeNull();
                accepted.Headers.Location!.ToString().ShouldBe($"/api/v1/abdm/links/{first.Id}");
            }

            first.Status.ShouldBe(AbdmLinkStatus.AwaitingToken);
            first.AbhaAddress.ShouldBe(address);
            var record = first.Records.ShouldHaveSingleItem();
            record.ReferenceNumber.ShouldBe($"op-consultation/{consultation}");
            record.Display.ShouldStartWith("Consultation note, ");

            var tokenCall = TokenCallsFor(address).ShouldHaveSingleItem();
            tokenCall.Header("Authorization").ShouldBe("Bearer it-session");
            tokenCall.Header("X-HIP-ID").ShouldBe(HipId);
            tokenCall.Header("X-CM-ID").ShouldBe("sbx");
            var asked = tokenCall.Json();
            asked.GetProperty("gender").GetString().ShouldBe("F");
            asked.GetProperty("yearOfBirth").GetInt32().ShouldBe(registration.DateOfBirth!.Value.Year);
            asked.GetProperty("name").GetString().ShouldNotBeNull().ShouldContain(registration.LastName!);

            // A callback the gateway did not sign is refused, and changes nothing.
            var gatewayCaller = server.Anonymous(ownAddress: true);
            var linkToken = "it-link-token-" + AbdmExportTests.Letters(12);
            var onToken = new { abhaAddress = address, linkToken, response = new { requestId = tokenCall.Header("REQUEST-ID") } };
            using (var forged = await CallBackAsync(gatewayCaller, AbdmGatewayCallbackEndpoints.OnGenerateTokenPath, onToken, FakeAbdmGateway.ForgedToken()))
            {
                forged.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            }

            using (var unsigned = await CallBackAsync(gatewayCaller, AbdmGatewayCallbackEndpoints.OnGenerateTokenPath, onToken, token: null))
            {
                unsigned.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            }

            // A signed answer naming another ABHA address is not this request's, and is ignored.
            var otherAddress = new { abhaAddress = "someone.else@sbx", linkToken, response = new { requestId = tokenCall.Header("REQUEST-ID") } };
            using (var mixedUp = await CallBackAsync(gatewayCaller, AbdmGatewayCallbackEndpoints.OnGenerateTokenPath, otherAddress, Gateway.Token()))
            {
                mixedUp.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            }

            CareContextCallsWith(linkToken).ShouldBeEmpty();
            (await medicalRecords.GetAsync<AbdmLinkView>($"/api/v1/abdm/links/{first.Id}")).Status.ShouldBe(AbdmLinkStatus.AwaitingToken);

            // The gateway's signed answer brings the token, and the care contexts go with it.
            using (var answered = await CallBackAsync(gatewayCaller, AbdmGatewayCallbackEndpoints.OnGenerateTokenPath, onToken, Gateway.Token()))
            {
                answered.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            }

            var linkCall = CareContextCallsWith(linkToken).ShouldHaveSingleItem();
            var sent = linkCall.Json();
            sent.GetProperty("abhaAddress").GetString().ShouldBe(address);
            sent.GetProperty("abhaNumber").GetString().ShouldBe(registration.AbhaNumber);
            var group = sent.GetProperty("patient").EnumerateArray().ShouldHaveSingleItem();
            group.GetProperty("hiType").GetString().ShouldBe("OPConsultation");
            group.GetProperty("referenceNumber").GetString().ShouldBe(patient.Id.ToString("D"));
            group.GetProperty("careContexts")[0].GetProperty("referenceNumber").GetString().ShouldBe($"op-consultation/{consultation}");
            (await medicalRecords.GetAsync<AbdmLinkView>($"/api/v1/abdm/links/{first.Id}")).Status.ShouldBe(AbdmLinkStatus.Sent);

            // The token is kept encrypted, never as sent.
            (await KeptTokenAsync(address)).ShouldNotBeNull().ShouldNotContain(linkToken);

            using (var linked = await CallBackAsync(gatewayCaller, AbdmGatewayCallbackEndpoints.OnAddCareContextsPath,
                new { abhaAddress = address, status = "SUCCESS", response = new { requestId = linkCall.Header("REQUEST-ID") } }, Gateway.Token()))
            {
                linked.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            }

            var done = await medicalRecords.GetAsync<AbdmLinkView>($"/api/v1/abdm/links/{first.Id}");
            done.Status.ShouldBe(AbdmLinkStatus.Linked);
            done.AnsweredAt.ShouldNotBeNull();
            (await AuditAsync(patient.Id, AbdmLinkingService.LinkRequested)).ShouldNotBeNull().EntityId.ShouldBe(first.Id.ToString("D"));

            // A second request reuses the kept token: the care contexts go at once, without asking for another.
            var second = await medicalRecords.PostAsync<AbdmLinkView>("/api/v1/abdm/links", Link(later));
            second.Status.ShouldBe(AbdmLinkStatus.Sent);
            TokenCallsFor(address).Count.ShouldBe(1);
            var secondCall = CareContextCallsWith(linkToken).Last();

            // The gateway refuses it (the token expired): the request fails with the gateway's reason and the token is dropped.
            using (var refused = await CallBackAsync(gatewayCaller, AbdmGatewayCallbackEndpoints.OnAddCareContextsPath,
                new { abhaAddress = address, response = new { requestId = secondCall.Header("REQUEST-ID") }, error = new { code = "ABDM-1006", message = "Link token expired" } },
                Gateway.Token()))
            {
                refused.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            }

            var failed = await medicalRecords.GetAsync<AbdmLinkView>($"/api/v1/abdm/links/{second.Id}");
            (failed.Status, failed.ErrorCode, failed.Error).ShouldBe((AbdmLinkStatus.Failed, "ABDM-1006", "Link token expired"));
            (await KeptTokenAsync(address)).ShouldBeNull();

            // So the next request asks for a new token.
            (await medicalRecords.PostAsync<AbdmLinkView>("/api/v1/abdm/links", Link(later))).Status.ShouldBe(AbdmLinkStatus.AwaitingToken);
            TokenCallsFor(address).Count.ShouldBe(2);
        }
        finally
        {
            await RemoveGatewayAsync();
            await SwitchAsync(enabled: false);
        }
    }

    [Fact]
    public async Task Nothing_is_sent_or_accepted_while_the_gateway_is_off()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var medicalRecords = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var address = "ravi." + AbdmExportTests.Letters(8) + "@sbx";
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", AbdmExportTests.Registration(AbdmExportTests.NewAbha(), address));
        var consultation = await AbdmExportTests.ConsultAsync(doctor, patient.Id, sign: true);

        try
        {
            await SwitchAsync(enabled: true);
            await SetUpGatewayAsync(enabled: false);

            using (var off = await medicalRecords.SendRawAsync(HttpMethod.Post, "/api/v1/abdm/links", Link(consultation)))
            {
                off.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
                (await Code(off)).ShouldBe(DisabledAbdmGatewayClient.Code);
            }

            using (var callback = await CallBackAsync(server.Anonymous(ownAddress: true), AbdmGatewayCallbackEndpoints.OnGenerateTokenPath,
                new { abhaAddress = address, linkToken = "x", response = new { requestId = Guid.NewGuid() } }, Gateway.Token()))
            {
                callback.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            }

            TokenCallsFor(address).ShouldBeEmpty();
        }
        finally
        {
            await RemoveGatewayAsync();
            await SwitchAsync(enabled: false);
        }
    }

    private static AbdmLinkInput Link(Guid consultation) => new([new AbdmLinkRecordInput(AbdmRecordType.OpConsultation, consultation)]);

    private List<GatewayCall> TokenCallsFor(string address) =>
        [.. Gateway.CallsTo(TokenPath).Where(c => c.Json().GetProperty("abhaAddress").GetString() == address)];

    private List<GatewayCall> CareContextCallsWith(string linkToken) =>
        [.. Gateway.CallsTo(CareContextPath).Where(c => c.Header("X-LINK-TOKEN") == linkToken)];

    private static async Task<HttpResponseMessage> CallBackAsync(ApiClient caller, string path, object body, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await caller.Http.SendAsync(request, Ct);
    }

    private static async Task<string?> Code(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString();

    private async Task SetUpGatewayAsync(bool enabled)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        await settings.SetAsync(AbdmGatewaySettingKeys.Gateway, new { enabled, baseUrl = FakeAbdmGateway.BaseUrl, hipId = HipId, cmId = "sbx", timeoutSeconds = 10 }, cancellationToken: Ct);
        await settings.SetAsync(AbdmGatewaySettingKeys.Secret, new { clientId = "SBX_IT", clientSecret = "it-" + Guid.NewGuid().ToString("N") }, secret: true, cancellationToken: Ct);
    }

    private async Task RemoveGatewayAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        await settings.RemoveAsync(AbdmGatewaySettingKeys.Gateway, null, Ct);
        await settings.RemoveAsync(AbdmGatewaySettingKeys.Secret, null, Ct);
    }

    private async Task<string?> KeptTokenAsync(string address)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var kept = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AbdmLinkToken>().AsNoTracking()
            .FirstOrDefaultAsync(t => t.HipId == HipId && t.AbhaAddress == address, Ct);
        return kept?.TokenCipher;
    }

    private async Task SwitchAsync(bool enabled)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        (await admin.SetFeatureAsync(FeatureKeys.Abdm, new SetFeatureRequest(enabled))).Enabled.ShouldBe(enabled);
    }

    private async Task<AuditEvent?> AuditAsync(Guid patientId, string action)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(a => a.PatientId == patientId && a.Action == action)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(Ct);
    }
}
