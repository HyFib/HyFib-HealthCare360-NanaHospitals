using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Features;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Features;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The hospital asking a patient for their records at other providers, as an HIU (ABDM M3), over the real callbacks
/// and a fake ABDM gateway (<see cref="FakeAbdmGateway"/>): a doctor asks, the consent manager takes the request
/// (on-init), the patient grants it with two artefacts, which the hospital acknowledges and fetches, and an artefact
/// arrives with its provider and records; later the patient revokes it. Staff without the permission cannot ask; a
/// patient without an ABHA address cannot be asked; a call for another HIU is ignored.
/// </summary>
[Collection(FeatureSwitchCollection.Name)]
public sealed class AbdmConsentRequestTests(HospitalServer server)
{
    private FakeAbdmGateway Gateway => server.AbdmGateway;

    [Fact]
    public async Task A_doctor_asks_the_patient_and_the_granted_artefacts_are_acknowledged_fetched_and_kept()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var address = "kiran." + AbdmExportTests.Letters(8) + "@sbx";
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", AbdmExportTests.Registration(AbdmExportTests.NewAbha(), address));
        var noAddress = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", AbdmExportTests.Registration(AbdmExportTests.NewAbha(), null));
        var input = new AbdmConsentRequestInput("CAREMGT", ["OPConsultation", "DiagnosticReport"], new DateOnly(2024, 1, 1), new DateOnly(2026, 10, 1), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)));

        try
        {
            await SwitchAsync(enabled: true);
            await AbdmLinkingTests.SetUpGatewayAsync(server, enabled: true);

            // Only doctors ask; a patient needs an ABHA address to be asked.
            using (var forbidden = await frontOffice.SendRawAsync(HttpMethod.Post, Requests(patient.Id), input))
            {
                forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }

            using (var none = await doctor.SendRawAsync(HttpMethod.Post, Requests(noAddress.Id), input))
            {
                none.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            }

            // The doctor asks: the consent manager gets the request as from the hospital's HIU.
            var sent = await doctor.PostAsync<AbdmConsentRequestView>(Requests(patient.Id), input);
            sent.Status.ShouldBe(AbdmConsentRequestStatus.Sent);
            var init = Gateway.CallsTo(HttpAbdmGatewayClient.ConsentInitPath).Last();
            init.Header("X-HIU-ID").ShouldBe(AbdmLinkingTests.HipId);
            init.Header("X-HIP-ID").ShouldBeEmpty();
            var consent = init.Json().GetProperty("consent");
            consent.GetProperty("patient").GetProperty("id").GetString().ShouldBe(address);
            consent.GetProperty("purpose").GetProperty("code").GetString().ShouldBe("CAREMGT");
            consent.GetProperty("hiTypes").EnumerateArray().Select(t => t.GetString()).ShouldBe(["OPConsultation", "DiagnosticReport"]);
            consent.GetProperty("requester").GetProperty("identifier").GetProperty("type").GetString().ShouldBe("REGNO");
            consent.GetProperty("requester").GetProperty("identifier").GetProperty("value").GetString().ShouldNotBeNullOrWhiteSpace();
            var permission = consent.GetProperty("permission");
            permission.GetProperty("accessMode").GetString().ShouldBe("VIEW");
            permission.GetProperty("dateRange").GetProperty("from").GetString().ShouldBe("2024-01-01T00:00:00.000Z");
            var requestId = init.Json().GetProperty("requestId").GetString().ShouldNotBeNull();
            init.Header("REQUEST-ID").ShouldBe(requestId);

            // on-init: the consent manager has the request.
            var consentRequestId = Guid.NewGuid().ToString();
            await CallAsync(AbdmGatewayCallbackEndpoints.ConsentOnInitPath, new { consentRequest = new { id = consentRequestId }, response = new { requestId } });
            (await ListAsync(doctor, patient.Id)).Single(r => r.Id == sent.Id).Status.ShouldBe(AbdmConsentRequestStatus.Requested);

            // A notification for another HIU is ignored.
            await CallAsync(AbdmGatewayCallbackEndpoints.HiuNotifyPath, Notify(consentRequestId, "DENIED"), hiuId: "IN9999999999");
            (await ListAsync(doctor, patient.Id)).Single(r => r.Id == sent.Id).Status.ShouldBe(AbdmConsentRequestStatus.Requested);

            // The patient grants it, with an artefact for each of two providers: acknowledged, and each fetched.
            var (first, second) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
            var notified = await CallAsync(AbdmGatewayCallbackEndpoints.HiuNotifyPath, Notify(consentRequestId, "GRANTED", first, second));
            var ack = Gateway.CallsTo(HttpAbdmGatewayClient.HiuOnNotifyPath).Single(c => c.Json().GetProperty("response").GetProperty("requestId").GetString() == notified);
            ack.Header("X-HIU-ID").ShouldBe(AbdmLinkingTests.HipId);
            ack.Json().GetProperty("acknowledgement").EnumerateArray().Select(a => (a.GetProperty("consentId").GetString(), a.GetProperty("status").GetString()))
                .ShouldBe([(first, "OK"), (second, "OK")]);
            var fetches = Gateway.CallsTo(HttpAbdmGatewayClient.ConsentFetchPath).Where(c => c.Json().GetProperty("consentId").GetString() is { } id && (id == first || id == second)).ToList();
            fetches.Count.ShouldBe(2);

            // The first artefact arrives: its provider and records are kept. One naming another artefact is ignored.
            var firstFetch = fetches.Single(c => c.Json().GetProperty("consentId").GetString() == first).Header("REQUEST-ID");
            await CallAsync(AbdmGatewayCallbackEndpoints.ConsentOnFetchPath, OnFetch(firstFetch, Guid.NewGuid().ToString(), address));
            await CallAsync(AbdmGatewayCallbackEndpoints.ConsentOnFetchPath, OnFetch(firstFetch, first, address));
            var granted = (await ListAsync(doctor, patient.Id)).Single(r => r.Id == sent.Id);
            granted.Status.ShouldBe(AbdmConsentRequestStatus.Granted);
            var artefact = granted.Artefacts.Single(a => a.ArtefactId == first);
            (artefact.HipId, artefact.HipName, artefact.Records, artefact.Status).ShouldBe(("IN2910000001", "City Care Hospital", 2, "GRANTED"));
            artefact.FetchedAt.ShouldNotBeNull();
            granted.Artefacts.Single(a => a.ArtefactId == second).FetchedAt.ShouldBeNull();

            // Later the patient revokes it.
            await CallAsync(AbdmGatewayCallbackEndpoints.HiuNotifyPath, Notify(consentRequestId, "REVOKED", first, second));
            var revoked = (await ListAsync(doctor, patient.Id)).Single(r => r.Id == sent.Id);
            revoked.Status.ShouldBe(AbdmConsentRequestStatus.Revoked);
            revoked.Artefacts.ShouldAllBe(a => a.Status == "REVOKED");
        }
        finally
        {
            await AbdmLinkingTests.RemoveGatewayAsync(server);
            await SwitchAsync(enabled: false);
        }
    }

    private static string Requests(Guid patientId) => $"/api/v1/abdm/patients/{patientId}/consent-requests";

    private static Task<List<AbdmConsentRequestView>> ListAsync(ApiClient client, Guid patientId) =>
        client.GetAsync<List<AbdmConsentRequestView>>(Requests(patientId));

    private static object Notify(string consentRequestId, string status, params string[] artefacts) => new
    {
        notification = new { consentRequestId, status, consentArtefacts = artefacts.Select(id => new { id }).ToArray() },
    };

    private static object OnFetch(string requestId, string consentId, string address) => new
    {
        consent = new
        {
            status = "GRANTED",
            consentDetail = new
            {
                schemaVersion = "v3",
                consentId,
                createdAt = "2026-10-26T10:00:00.000Z",
                patient = new { id = address },
                careContexts = new[]
                {
                    new { patientReference = "PAT-1", careContextReference = "visit/1" },
                    new { patientReference = "PAT-1", careContextReference = "visit/2" },
                },
                purpose = new { text = "Care Management", code = "CAREMGT", refUri = "http://terminology.hl7.org/ValueSet/v3-PurposeOfUse" },
                hip = new { id = "IN2910000001", name = "City Care Hospital" },
                hiu = new { id = AbdmLinkingTests.HipId },
                consentManager = new { id = "sbx" },
                hiTypes = new[] { "OPConsultation" },
                permission = new
                {
                    accessMode = "VIEW",
                    dateRange = new { from = "2024-01-01T00:00:00.000Z", to = "2026-10-01T23:59:59.999Z" },
                    dataEraseAt = "2026-11-25T23:59:59.999Z",
                    frequency = new { unit = "HOUR", value = 1, repeats = 0 },
                },
            },
            signature = "c2lnbmF0dXJl",
        },
        response = new { requestId },
    };

    /// <summary>Calls back as the gateway for the hospital's HIU (or <paramref name="hiuId"/>); returns the REQUEST-ID used.</summary>
    private async Task<string> CallAsync(string path, object body, string? hiuId = null)
    {
        var requestId = Guid.NewGuid().ToString();
        using var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new("Bearer", Gateway.Token());
        message.Headers.Add(AbdmGatewayCallbackEndpoints.RequestIdHeader, requestId);
        message.Headers.Add(AbdmGatewayCallbackEndpoints.HiuIdHeader, hiuId ?? AbdmLinkingTests.HipId);
        using var response = await server.Anonymous(ownAddress: true).Http.SendAsync(message, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, path);
        return requestId;
    }

    private async Task SwitchAsync(bool enabled)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        (await admin.SetFeatureAsync(FeatureKeys.Abdm, new SetFeatureRequest(enabled))).Enabled.ShouldBe(enabled);
    }
}
