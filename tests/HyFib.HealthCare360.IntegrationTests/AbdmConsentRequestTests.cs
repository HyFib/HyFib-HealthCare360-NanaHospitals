using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Features;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The hospital asking a patient for their records at other providers, as an HIU (ABDM M3), over the real callbacks
/// and a fake ABDM gateway (<see cref="FakeAbdmGateway"/>): a doctor asks, the consent manager takes the request
/// (on-init), the patient grants it with two artefacts, which the hospital acknowledges and fetches, and an artefact
/// arrives with its provider and records. Its records are asked for with a fresh key pair, pushed by the provider in
/// two pages encrypted for that key (one record readable, one for a visit not consented, one altered on the way), and
/// the consent manager is told which arrived; the doctor reads the one that did. Later the patient revokes the consent
/// and what was received is erased. Staff without the permission cannot ask; a
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

            // The fetched artefact's records are asked for at once, as the HIU, with a key pair of this request's own.
            var hiCall = Gateway.CallsTo(HttpAbdmGatewayClient.HealthInformationRequestPath)
                .Single(c => c.Json().GetProperty("hiRequest").GetProperty("consent").GetProperty("id").GetString() == first);
            hiCall.Header("X-HIU-ID").ShouldBe(AbdmLinkingTests.HipId);
            var hiRequest = hiCall.Json().GetProperty("hiRequest");
            hiRequest.GetProperty("dataPushUrl").GetString().ShouldBe(AbdmLinkingTests.DataPushUrl);
            hiRequest.GetProperty("dateRange").GetProperty("from").GetString().ShouldBe("2024-01-01T00:00:00.000Z");
            var hiuKey = hiRequest.GetProperty("keyMaterial");
            hiuKey.GetProperty("cryptoAlg").GetString().ShouldBe("ECDH");
            hiuKey.GetProperty("curve").GetString().ShouldBe("Curve25519");
            hiuKey.GetProperty("dhPublicKey").GetProperty("expiry").GetString().ShouldBe("2026-11-25T23:59:59.999Z");
            var (hiuPublicKey, hiuNonce) = (hiuKey.GetProperty("dhPublicKey").GetProperty("keyValue").GetString()!, hiuKey.GetProperty("nonce").GetString()!);

            // Before the consent manager names the transaction, a page for it is refused.
            var transactionId = Guid.NewGuid().ToString();
            var hip = Fidelius.Generate();
            (await PushAsync(Page(1, 2, transactionId, hip, [Entry("visit/1", Bundle("Consultation, City Care"), hip, hiuPublicKey, hiuNonce)]))).ShouldBe(HttpStatusCode.NotFound);
            await CallAsync(AbdmGatewayCallbackEndpoints.HealthInformationOnRequestPath,
                new { hiRequest = new { transactionId, sessionStatus = "REQUESTED" }, response = new { requestId = hiCall.Header("REQUEST-ID") } });

            // Page 1 of 2: a record for a covered visit, and one for a visit the consent does not cover.
            (await PushAsync(Page(1, 2, transactionId, hip,
            [
                Entry("visit/1", Bundle("Consultation, City Care"), hip, hiuPublicKey, hiuNonce),
                Entry("visit/9", Bundle("Not consented"), hip, hiuPublicKey, hiuNonce),
            ]))).ShouldBe(HttpStatusCode.Accepted);
            Gateway.CallsTo(HttpAbdmGatewayClient.TransferNotifyPath).ShouldNotContain(c => c.Json().GetProperty("notification").GetProperty("transactionId").GetString() == transactionId);

            // Page 2: a record altered on the way, which does not decrypt. Then every page is in: the consent manager is told.
            var altered = Entry("visit/2", Bundle("Discharge summary"), hip, hiuPublicKey, hiuNonce);
            altered = altered with { content = Convert.ToBase64String([.. Convert.FromBase64String(altered.content).Select((b, i) => i == 0 ? (byte)(b ^ 1) : b)]) };
            (await PushAsync(Page(2, 2, transactionId, hip, [altered]))).ShouldBe(HttpStatusCode.Accepted);
            var received = Gateway.CallsTo(HttpAbdmGatewayClient.TransferNotifyPath)
                .Single(c => c.Json().GetProperty("notification").GetProperty("transactionId").GetString() == transactionId);
            received.Header("X-HIU-ID").ShouldBe(AbdmLinkingTests.HipId);
            var notification = received.Json().GetProperty("notification");
            notification.GetProperty("consentId").GetString().ShouldBe(first);
            notification.GetProperty("notifier").GetProperty("type").GetString().ShouldBe("HIU");
            var statuses = notification.GetProperty("statusNotification");
            statuses.GetProperty("sessionStatus").GetString().ShouldBe("TRANSFERRED");
            statuses.GetProperty("hipId").GetString().ShouldBe("IN2910000001");
            statuses.GetProperty("statusResponses").EnumerateArray()
                .Select(r => (r.GetProperty("careContextReference").GetString(), r.GetProperty("hiStatus").GetString()))
                .OrderBy(r => r.Item1).ShouldBe([("visit/1", "OK"), ("visit/2", "ERRORED"), ("visit/9", "ERRORED")]);

            // The transfer is over: its key is dropped and another page is refused.
            (await PushAsync(Page(1, 2, transactionId, hip, [Entry("visit/1", Bundle("Again"), hip, hiuPublicKey, hiuNonce)]))).ShouldBe(HttpStatusCode.NotFound);

            // The doctor sees the record that arrived, by its title, and opens it; front office cannot.
            using (var notAllowed = await frontOffice.SendRawAsync(HttpMethod.Get, $"/api/v1/abdm/patients/{patient.Id}/received-records", null))
            {
                notAllowed.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }

            var records = await doctor.GetAsync<List<AbdmReceivedRecordSummary>>($"/api/v1/abdm/patients/{patient.Id}/received-records");
            var readable = records.Single(r => r.Error is null);
            (readable.Title, readable.CareContextReference, readable.HipName, readable.ChecksumMatches).ShouldBe(("Consultation, City Care", "visit/1", "City Care Hospital", true));
            records.Count(r => r.Error is not null).ShouldBe(2);
            var opened = await doctor.GetAsync<AbdmReceivedRecordView>($"/api/v1/abdm/patients/{patient.Id}/received-records/{readable.Id}");
            opened.Bundle!.Value.GetProperty("resourceType").GetString().ShouldBe("Bundle");
            var shown = (await ListAsync(doctor, patient.Id)).Single(r => r.Id == sent.Id).Artefacts.Single(a => a.ArtefactId == first);
            (shown.Transfer, shown.Received).ShouldBe((AbdmHealthInformationStatus.Received, 1));

            // Later the patient revokes it.
            await CallAsync(AbdmGatewayCallbackEndpoints.HiuNotifyPath, Notify(consentRequestId, "REVOKED", first, second));
            var revoked = (await ListAsync(doctor, patient.Id)).Single(r => r.Id == sent.Id);
            revoked.Status.ShouldBe(AbdmConsentRequestStatus.Revoked);
            revoked.Artefacts.ShouldAllBe(a => a.Status == "REVOKED");

            // What was received under the revoked consent is erased at once.
            (await doctor.GetAsync<List<AbdmReceivedRecordSummary>>($"/api/v1/abdm/patients/{patient.Id}/received-records")).ShouldBeEmpty();
            await using (var scope = server.Services.CreateAsyncScope())
            {
                (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AbdmReceivedRecord>().IgnoreQueryFilters()
                    .CountAsync(r => r.PatientId == patient.Id, TestContext.Current.CancellationToken)).ShouldBe(0);
            }
        }
        finally
        {
            await AbdmLinkingTests.RemoveGatewayAsync(server);
            await SwitchAsync(enabled: false);
        }
    }

    [Fact]
    public async Task Records_past_their_consents_erase_date_are_deleted_and_their_keys_dropped_and_others_kept()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", AbdmExportTests.Registration(AbdmExportTests.NewAbha(), "erase." + AbdmExportTests.Letters(8) + "@sbx"));
        var ct = TestContext.Current.CancellationToken;
        Guid expiredRecord, keptRecord, expiredRequest;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var request = new AbdmConsentRequest
            {
                PatientId = patient.Id,
                AbhaAddress = "erase@sbx",
                GatewayRequestId = Guid.NewGuid(),
                Status = AbdmConsentRequestStatus.Granted,
                Purpose = "CAREMGT",
                HiTypes = "OPConsultation",
                RequesterName = "Dr. Test",
                DateFrom = DateTimeOffset.UtcNow.AddYears(-1),
                DateTo = DateTimeOffset.UtcNow,
                DataEraseAt = DateTimeOffset.UtcNow.AddDays(30),
            };
            db.Add(request);
            (Guid Record, Guid Request) Add(DateTimeOffset eraseAt)
            {
                var artefact = new AbdmConsentArtefact
                {
                    ConsentRequestEntityId = request.Id,
                    PatientId = patient.Id,
                    ArtefactId = Guid.NewGuid().ToString(),
                    Status = "GRANTED",
                    DataEraseAt = eraseAt,
                };
                var transfer = new AbdmHealthInformationRequest
                {
                    ArtefactEntityId = artefact.Id,
                    PatientId = patient.Id,
                    GatewayRequestId = Guid.NewGuid(),
                    Status = AbdmHealthInformationStatus.Requested,
                    PrivateKeyCipher = "v1.key",
                    Nonce = "nonce",
                    KeyExpiresAt = eraseAt,
                };
                var record = new AbdmReceivedRecord { RequestEntityId = transfer.Id, PatientId = patient.Id, CareContextReference = "visit/1", PageNumber = 1, Content = "{}" };
                db.AddRange(artefact, transfer, record);
                return (record.Id, transfer.Id);
            }

            (expiredRecord, expiredRequest) = Add(DateTimeOffset.UtcNow.AddMinutes(-5));
            (keptRecord, _) = Add(DateTimeOffset.UtcNow.AddDays(5));
            await db.SaveChangesAsync(ct);
        }

        await using (var scope = server.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<AbdmHealthInformationService>().EraseExpiredAsync(ct)).ShouldBeGreaterThanOrEqualTo(1);
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.Set<AbdmReceivedRecord>().IgnoreQueryFilters().AnyAsync(r => r.Id == expiredRecord, ct)).ShouldBeFalse();
            (await db.Set<AbdmReceivedRecord>().AnyAsync(r => r.Id == keptRecord, ct)).ShouldBeTrue();
            (await db.Set<AbdmHealthInformationRequest>().AsNoTracking().SingleAsync(q => q.Id == expiredRequest, ct)).PrivateKeyCipher.ShouldBeNull();
            (await db.Set<AuditEvent>().AsNoTracking().AnyAsync(a => a.PatientId == patient.Id && a.Action == AbdmHealthInformationService.Erased, ct)).ShouldBeTrue();
        }
    }

    private sealed record PushEntry(string content, string media, string checksum, string careContextReference);

    private static string Bundle(string title) => JsonSerializer.Serialize(new
    {
        resourceType = "Bundle",
        type = "document",
        timestamp = "2026-09-01T10:00:00Z",
        entry = new object[] { new { resource = new { resourceType = "Composition", title, date = "2026-09-01T10:00:00Z", status = "final" } } },
    });

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Primitives",
        Justification = "ABDM's data push names each entry by the MD5 of its document, as a provider sends it.")]
    private static PushEntry Entry(string careContext, string bundle, FideliusKeyPair hip, string hiuPublicKey, string hiuNonce) => new(
        Fidelius.Encrypt(bundle, hip.PrivateKey, hip.Nonce, hiuPublicKey, hiuNonce).Value,
        "application/fhir+json",
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(bundle))),
        careContext);

    private static object Page(int number, int count, string transactionId, FideliusKeyPair hip, PushEntry[] entries) => new
    {
        pageNumber = number,
        pageCount = count,
        transactionId,
        entries,
        keyMaterial = new
        {
            cryptoAlg = "ECDH",
            curve = "Curve25519",
            dhPublicKey = new { expiry = "2026-11-25T23:59:59.999Z", parameters = "Curve25519/32byte random key", keyValue = hip.PublicKey },
            nonce = hip.Nonce,
        },
    };

    /// <summary>Posts a page as another provider does: anonymous, no gateway token.</summary>
    private async Task<HttpStatusCode> PushAsync(object page)
    {
        using var content = new StringContent(JsonSerializer.Serialize(page), Encoding.UTF8, "application/json");
        using var response = await server.Anonymous(ownAddress: true).Http.PostAsync(AbdmGatewayCallbackEndpoints.DataPushPath, content, TestContext.Current.CancellationToken);
        return response.StatusCode;
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
