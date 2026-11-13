using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Features;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Requests the gateway does not answer (<see cref="AbdmUnansweredRequests"/>): a link request whose callbacks are lost
/// is marked failed once <c>answerTimeoutMinutes</c> has passed, with a reason staff read, and the answers that come
/// late are still taken, a token sending the records and the linking answer linking them, as the gateway has acted on
/// them. Staff see the failed request among the patient's and send it again, as a new request. A consent request the
/// consent manager never took is marked failed and asked again; one the patient is deciding is not touched. The ABDM
/// switch is hospital-wide, so these tests are in the feature-switch collection.
/// </summary>
[Collection(FeatureSwitchCollection.Name)]
public sealed class AbdmUnansweredRequestTests(HospitalServer server)
{
    private const string TokenPath = "/v3/token/generate-token";
    private const string CareContextPath = "/hip/v3/link/carecontext";
    private const int TimeoutMinutes = AbdmGatewaySettings.MinAnswerTimeoutMinutes;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeAbdmGateway Gateway => server.AbdmGateway;

    [Fact]
    public async Task A_link_request_abdm_does_not_answer_is_marked_failed_and_its_late_answers_still_link_the_record()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var medicalRecords = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var address = "nisha." + AbdmExportTests.Letters(8) + "@sbx";
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", AbdmExportTests.Registration(AbdmExportTests.NewAbha(), address));
        var consultation = await AbdmExportTests.ConsultAsync(doctor, patient.Id, sign: true);

        try
        {
            await SwitchAsync(enabled: true);
            await AbdmLinkingTests.SetUpGatewayAsync(server, enabled: true, answerTimeoutMinutes: TimeoutMinutes);
            var link = await medicalRecords.PostAsync<AbdmLinkView>("/api/v1/abdm/links",
                new AbdmLinkInput([new AbdmLinkRecordInput(AbdmRecordType.OpConsultation, consultation)]));
            link.Status.ShouldBe(AbdmLinkStatus.AwaitingToken);
            var tokenCall = Gateway.CallsTo(TokenPath).Single(c => c.Json().GetProperty("abhaAddress").GetString() == address);

            // Within the timeout it waits; past it, the sweep marks it failed, saying what did not come.
            await SweepAsync();
            (await ViewAsync(medicalRecords, link.Id)).Status.ShouldBe(AbdmLinkStatus.AwaitingToken);
            await AgeLinkAsync(link.Id);
            await SweepAsync();
            var unanswered = await ViewAsync(medicalRecords, link.Id);
            (unanswered.Status, unanswered.ErrorCode).ShouldBe((AbdmLinkStatus.Failed, AbdmLinkingService.NoAnswerCode));
            unanswered.Error.ShouldNotBeNull().ShouldContain($"did not send a link token within {TimeoutMinutes} minutes");

            // The token comes late: it is kept, and the record goes with it.
            var linkToken = "it-late-token-" + AbdmExportTests.Letters(12);
            await CallBackAsync(AbdmGatewayCallbackEndpoints.OnGenerateTokenPath, new { abhaAddress = address, linkToken, response = new { requestId = tokenCall.Header("REQUEST-ID") } });
            var resumed = await ViewAsync(medicalRecords, link.Id);
            (resumed.Status, resumed.ErrorCode, resumed.Error).ShouldBe((AbdmLinkStatus.Sent, (string?)null, (string?)null));
            var linkCall = Gateway.CallsTo(CareContextPath).Single(c => c.Header("X-LINK-TOKEN") == linkToken);

            // The answer to the care contexts is late too: marked failed again, then linked when it comes, and kept as linked,
            // so a consent naming the record is honoured.
            await AgeLinkAsync(link.Id);
            await SweepAsync();
            var waited = await ViewAsync(medicalRecords, link.Id);
            (waited.Status, waited.ErrorCode).ShouldBe((AbdmLinkStatus.Failed, AbdmLinkingService.NoAnswerCode));
            waited.Error.ShouldNotBeNull().ShouldContain("whether it linked the records");
            await CallBackAsync(AbdmGatewayCallbackEndpoints.OnAddCareContextsPath,
                new { abhaAddress = address, status = "SUCCESS", response = new { requestId = linkCall.Header("REQUEST-ID") } });
            var linked = await ViewAsync(medicalRecords, link.Id);
            (linked.Status, linked.ErrorCode, linked.Error).ShouldBe((AbdmLinkStatus.Linked, (string?)null, (string?)null));
            (await LinkedReferencesAsync(address)).ShouldContain($"op-consultation/{consultation}");
        }
        finally
        {
            await AbdmLinkingTests.RemoveGatewayAsync(server);
            await SwitchAsync(enabled: false);
        }
    }

    [Fact]
    public async Task Staff_send_a_request_abdm_never_answered_again_as_a_new_request_once_it_is_marked_failed()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var medicalRecords = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var address = "omkar." + AbdmExportTests.Letters(8) + "@sbx";
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", AbdmExportTests.Registration(AbdmExportTests.NewAbha(), address));
        var consultation = await AbdmExportTests.ConsultAsync(doctor, patient.Id, sign: true);
        var links = $"/api/v1/abdm/patients/{patient.Id}/links";

        try
        {
            await SwitchAsync(enabled: true);
            await AbdmLinkingTests.SetUpGatewayAsync(server, enabled: true, answerTimeoutMinutes: TimeoutMinutes);
            var first = await medicalRecords.PostAsync<AbdmLinkView>("/api/v1/abdm/links",
                new AbdmLinkInput([new AbdmLinkRecordInput(AbdmRecordType.OpConsultation, consultation)]));

            // Not while ABDM may still answer it.
            using (var waiting = await medicalRecords.SendRawAsync(HttpMethod.Post, $"/api/v1/abdm/links/{first.Id}/retry"))
            {
                waiting.StatusCode.ShouldBe(System.Net.HttpStatusCode.UnprocessableEntity);
                (await CodeAsync(waiting)).ShouldBe(AbdmLinkingService.NotFailedCode);
            }

            // Marked failed, it is offered again among the patient's requests, made by staff.
            await AgeLinkAsync(first.Id);
            await SweepAsync();
            var listed = (await medicalRecords.GetAsync<List<AbdmLinkView>>(links)).ShouldHaveSingleItem();
            (listed.Id, listed.Status, listed.Retryable, listed.Automatic).ShouldBe((first.Id, AbdmLinkStatus.Failed, true, false));

            // Sent again: a new request for the same record, which asks the gateway for a token as the first did.
            using (var again = await medicalRecords.SendRawAsync(HttpMethod.Post, $"/api/v1/abdm/links/{first.Id}/retry"))
            {
                again.StatusCode.ShouldBe(System.Net.HttpStatusCode.Accepted);
            }

            Gateway.CallsTo(TokenPath).Count(c => c.Json().GetProperty("abhaAddress").GetString() == address).ShouldBe(2);
            var both = await medicalRecords.GetAsync<List<AbdmLinkView>>(links);
            both.Count.ShouldBe(2);
            var second = both[0];
            (second.Status, second.Records.ShouldHaveSingleItem().Id).ShouldBe((AbdmLinkStatus.AwaitingToken, consultation));
            (both[1].Id, both[1].Retryable).ShouldBe((first.Id, false));

            // Once more is not a third request: the record is awaiting ABDM's answer in the second.
            using var twice = await medicalRecords.SendRawAsync(HttpMethod.Post, $"/api/v1/abdm/links/{first.Id}/retry");
            twice.StatusCode.ShouldBe(System.Net.HttpStatusCode.UnprocessableEntity);
            (await CodeAsync(twice)).ShouldBe(AbdmLinkingService.LinkPendingCode);
        }
        finally
        {
            await AbdmLinkingTests.RemoveGatewayAsync(server);
            await SwitchAsync(enabled: false);
        }
    }

    [Fact]
    public async Task A_consent_request_the_consent_manager_never_took_is_marked_failed_but_one_the_patient_is_deciding_waits()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var address = "deepa." + AbdmExportTests.Letters(8) + "@sbx";
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", AbdmExportTests.Registration(AbdmExportTests.NewAbha(), address));
        var input = new AbdmConsentRequestInput("CAREMGT", ["OPConsultation"], new DateOnly(2025, 1, 1), new DateOnly(2026, 10, 1), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)));
        var requests = $"/api/v1/abdm/patients/{patient.Id}/consent-requests";

        try
        {
            await SwitchAsync(enabled: true);
            await AbdmLinkingTests.SetUpGatewayAsync(server, enabled: true, answerTimeoutMinutes: TimeoutMinutes);
            var neverTaken = await doctor.PostAsync<AbdmConsentRequestView>(requests, input);
            var deciding = await doctor.PostAsync<AbdmConsentRequestView>(requests, input);
            await CallBackAsync(AbdmGatewayCallbackEndpoints.ConsentOnInitPath,
                new { consentRequest = new { id = Guid.NewGuid().ToString() }, response = new { requestId = await GatewayRequestIdAsync(deciding.Id) } });

            await AgeConsentAsync(neverTaken.Id, deciding.Id);
            await SweepAsync();
            var listed = await doctor.GetAsync<List<AbdmConsentRequestView>>(requests);
            var failed = listed.Single(r => r.Id == neverTaken.Id);
            failed.Status.ShouldBe(AbdmConsentRequestStatus.Failed);
            failed.Error.ShouldNotBeNull().ShouldContain($"did not say within {TimeoutMinutes} minutes");
            listed.Single(r => r.Id == deciding.Id).Status.ShouldBe(AbdmConsentRequestStatus.Requested);

            // The doctor asks again on the same terms: a new request goes to the consent manager. One the patient is
            // deciding is not asked again.
            var initsBefore = Gateway.CallsTo(HttpAbdmGatewayClient.ConsentInitPath).Count;
            var askedAgain = await doctor.PostAsync<AbdmConsentRequestView>($"{requests}/{neverTaken.Id}/retry");
            (askedAgain.Status, askedAgain.Purpose, askedAgain.DateFrom, askedAgain.DateTo, askedAgain.DataEraseAt).ShouldBe(
                (AbdmConsentRequestStatus.Sent, failed.Purpose, failed.DateFrom, failed.DateTo, failed.DataEraseAt));
            askedAgain.HiTypes.ShouldBe(failed.HiTypes);
            askedAgain.Id.ShouldNotBe(neverTaken.Id);
            Gateway.CallsTo(HttpAbdmGatewayClient.ConsentInitPath).Count.ShouldBe(initsBefore + 1);
            using (var notFailed = await doctor.SendRawAsync(HttpMethod.Post, $"{requests}/{deciding.Id}/retry"))
            {
                notFailed.StatusCode.ShouldBe(System.Net.HttpStatusCode.UnprocessableEntity);
                (await CodeAsync(notFailed)).ShouldBe(AbdmConsentRequestService.NotFailedCode);
            }

            // The consent manager takes the first after all: it goes ahead too.
            await CallBackAsync(AbdmGatewayCallbackEndpoints.ConsentOnInitPath,
                new { consentRequest = new { id = Guid.NewGuid().ToString() }, response = new { requestId = await GatewayRequestIdAsync(neverTaken.Id) } });
            (await doctor.GetAsync<List<AbdmConsentRequestView>>(requests)).Single(r => r.Id == neverTaken.Id).Status.ShouldBe(AbdmConsentRequestStatus.Requested);
        }
        finally
        {
            await AbdmLinkingTests.RemoveGatewayAsync(server);
            await SwitchAsync(enabled: false);
        }
    }

    private static Task<AbdmLinkView> ViewAsync(ApiClient caller, Guid id) => caller.GetAsync<AbdmLinkView>($"/api/v1/abdm/links/{id}");

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<System.Text.Json.JsonElement>(response.Content, ApiClient.Json, Ct))
            .GetProperty("code").GetString();

    private async Task CallBackAsync(string path, object body)
    {
        using var answer = await AbdmLinkingTests.CallBackAsync(server.Anonymous(ownAddress: true), path, body, Gateway.Token());
        answer.StatusCode.ShouldBe(System.Net.HttpStatusCode.Accepted);
    }

    /// <summary>Runs the sweep now, as the server's job does every minute.</summary>
    private async Task SweepAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AbdmUnansweredRequests>().SweepAsync(Ct);
    }

    /// <summary>Makes the request look sent a minute longer ago than the timeout.</summary>
    private async Task AgeLinkAsync(Guid id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var sentAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(TimeoutMinutes + 1);
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AbdmLinkRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(r => r.Id == id)
            .ExecuteUpdateAsync(r => r.SetProperty(x => x.SentAt, sentAt), Ct);
    }

    private async Task AgeConsentAsync(params Guid[] ids)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var createdAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(TimeoutMinutes + 1);
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AbdmConsentRequest>()
            .Where(r => ids.Contains(r.Id))
            .ExecuteUpdateAsync(r => r.SetProperty(x => x.CreatedAt, createdAt), Ct);
    }

    private async Task<string> GatewayRequestIdAsync(Guid id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var request = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AbdmConsentRequest>().AsNoTracking().FirstAsync(r => r.Id == id, Ct);
        return request.GatewayRequestId.ToString("D");
    }

    private async Task<List<string>> LinkedReferencesAsync(string address)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AbdmLinkedCareContext>().AsNoTracking()
            .Where(c => c.HipId == AbdmLinkingTests.HipId && c.AbhaAddress == address)
            .Select(c => c.ReferenceNumber)
            .ToListAsync(Ct);
    }

    private async Task SwitchAsync(bool enabled)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        (await admin.SetFeatureAsync(FeatureKeys.Abdm, new SetFeatureRequest(enabled))).Enabled.ShouldBe(enabled);
    }
}
