using System.Net;
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
/// Consent and data flow over the real callbacks and a fake ABDM gateway (<see cref="FakeAbdmGateway"/>, ABDM M2): the
/// consent manager notifies a consent for a linked consultation; the HIU asks for it with its Fidelius key; the hospital
/// acknowledges, makes the consultation's ABDM document, encrypts it for the HIU, posts it to the HIU's data-push URL
/// (which the fake gateway stands in for) and tells the gateway it was delivered. The HIU's private key opens it. A
/// consent naming records not linked here is refused, a request to an address on the hospital's network or under a
/// revoked consent is refused, and nothing is sent for it.
/// </summary>
[Collection(FeatureSwitchCollection.Name)]
public sealed class AbdmDataFlowTests(HospitalServer server)
{
    private const string PushUrl = "https://hiu.test/data/push";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeAbdmGateway Gateway => server.AbdmGateway;

    [Fact]
    public async Task A_consented_consultation_reaches_the_hiu_encrypted_for_it_and_the_gateway_hears_it_was_delivered()
    {
        var (patientId, address, reference) = await LinkedConsultationAsync();
        var consentId = Guid.NewGuid().ToString();
        var hiu = Fidelius.Generate();

        try
        {
            await SwitchAsync(enabled: true);
            await AbdmLinkingTests.SetUpGatewayAsync(server, enabled: true);

            // A consent naming a record that is not linked here is refused.
            var stranger = await CallAsync(AbdmGatewayCallbackEndpoints.ConsentNotifyPath, Notify(Guid.NewGuid().ToString(), "GRANTED", address, patientId, "op-consultation/" + Guid.NewGuid()));
            var refused = Answer(HttpAbdmGatewayClient.OnConsentNotifyPath, stranger);
            refused.GetProperty("acknowledgement").GetProperty("status").GetString().ShouldBe("FAILURE");
            refused.GetProperty("error").GetProperty("code").GetString().ShouldBe(AbdmDataFlowService.ErrorCode);

            // The consent for the linked consultation is kept and acknowledged.
            var notified = await CallAsync(AbdmGatewayCallbackEndpoints.ConsentNotifyPath, Notify(consentId, "GRANTED", address, patientId, reference));
            var acknowledged = Answer(HttpAbdmGatewayClient.OnConsentNotifyPath, notified);
            acknowledged.GetProperty("acknowledgement").GetProperty("status").GetString().ShouldBe("OK");
            acknowledged.GetProperty("acknowledgement").GetProperty("consentId").GetString().ShouldBe(consentId);
            (await AuditAsync(patientId, AbdmDataFlowService.ConsentGranted)).ShouldNotBeNull();

            // A request to push to an address on the hospital's network is refused, and nothing is sent.
            var inside = await CallAsync(AbdmGatewayCallbackEndpoints.HealthInformationRequestPath, Request(Guid.NewGuid().ToString(), consentId, hiu, "https://10.0.0.5/push"));
            Answer(HttpAbdmGatewayClient.OnHealthInformationRequestPath, inside).GetProperty("error").GetProperty("message").GetString()
                .ShouldNotBeNull().ShouldContain("https");

            // The HIU's request is acknowledged, and the transfer runs from the outbox.
            var transaction = Guid.NewGuid().ToString();
            var requested = await CallAsync(AbdmGatewayCallbackEndpoints.HealthInformationRequestPath, Request(transaction, consentId, hiu, PushUrl));
            var ack = Answer(HttpAbdmGatewayClient.OnHealthInformationRequestPath, requested);
            ack.GetProperty("hiRequest").GetProperty("sessionStatus").GetString().ShouldBe("ACKNOWLEDGED");
            ack.GetProperty("hiRequest").GetProperty("transactionId").GetString().ShouldBe(transaction);

            var transfer = await TransferAsync(transaction);
            transfer.Status.ShouldBe(AbdmTransferStatus.Transferred, transfer.Error);

            // One page, its one document encrypted for the HIU: its private key opens a FHIR document of the consultation.
            var page = Gateway.Calls.Single(c => c.Path == PushUrl && c.Json().GetProperty("transactionId").GetString() == transaction).Json();
            (page.GetProperty("pageNumber").GetInt32(), page.GetProperty("pageCount").GetInt32()).ShouldBe((0, 1));
            var entry = page.GetProperty("entries").EnumerateArray().ShouldHaveSingleItem();
            entry.GetProperty("careContextReference").GetString().ShouldBe(reference);
            entry.GetProperty("media").GetString().ShouldBe("application/fhir+json");
            var keyMaterial = page.GetProperty("keyMaterial");
            keyMaterial.GetProperty("dhPublicKey").GetProperty("parameters").GetString().ShouldBe(Fidelius.KeyParameters);
            var document = Fidelius.Decrypt(
                entry.GetProperty("content").GetString()!, hiu.PrivateKey, hiu.Nonce,
                keyMaterial.GetProperty("dhPublicKey").GetProperty("keyValue").GetString()!, keyMaterial.GetProperty("nonce").GetString()!);
            var bundle = JsonDocument.Parse(document.Value).RootElement;
            bundle.GetProperty("resourceType").GetString().ShouldBe("Bundle");
            bundle.GetProperty("type").GetString().ShouldBe("document");

            // The gateway hears it was delivered, and the disclosure is audited under the patient.
            var report = Gateway.CallsTo(HttpAbdmGatewayClient.TransferNotifyPath)
                .Single(c => c.Json().GetProperty("notification").GetProperty("transactionId").GetString() == transaction).Json().GetProperty("notification");
            report.GetProperty("consentId").GetString().ShouldBe(consentId);
            var status = report.GetProperty("statusNotification");
            status.GetProperty("sessionStatus").GetString().ShouldBe("TRANSFERRED");
            var delivered = status.GetProperty("statusResponses").EnumerateArray().ShouldHaveSingleItem();
            (delivered.GetProperty("careContextReference").GetString(), delivered.GetProperty("hiStatus").GetString()).ShouldBe((reference, "DELIVERED"));
            (await AuditAsync(patientId, AbdmDataFlowService.Shared)).ShouldNotBeNull().EntityId.ShouldBe(transaction);

            // Once the consent is revoked, a request under it is refused.
            await CallAsync(AbdmGatewayCallbackEndpoints.ConsentNotifyPath, Notify(consentId, "REVOKED", address, patientId, reference));
            var late = await CallAsync(AbdmGatewayCallbackEndpoints.HealthInformationRequestPath, Request(Guid.NewGuid().ToString(), consentId, hiu, PushUrl));
            Answer(HttpAbdmGatewayClient.OnHealthInformationRequestPath, late).GetProperty("error").GetProperty("message").GetString().ShouldBe("The consent is revoked.");

            // A transfer acknowledged before the consent was revoked sends nothing when it runs.
            var stale = Guid.NewGuid().ToString();
            Guid staleId;
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
                var row = new AbdmTransfer
                {
                    TransactionId = stale,
                    ConsentId = consentId,
                    HipId = AbdmLinkingTests.HipId,
                    PatientId = patientId,
                    DataPushUrl = PushUrl,
                    KeyMaterialJson = transfer.KeyMaterialJson,
                    Status = AbdmTransferStatus.Acknowledged,
                    RequestedAt = DateTimeOffset.UtcNow,
                };
                db.Add(row);
                await db.SaveChangesAsync(Ct);
                staleId = row.Id;
            }

            await using (var scope = server.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<AbdmDataFlowService>().TransferAsync(staleId, Ct);
            }

            (await TransferAsync(stale)).Status.ShouldBe(AbdmTransferStatus.Failed);
            Gateway.CallsTo(PushUrl).ShouldNotContain(c => c.Json().GetProperty("transactionId").GetString() == stale);
            var notice = Gateway.CallsTo(HttpAbdmGatewayClient.TransferNotifyPath)
                .Single(c => c.Json().GetProperty("notification").GetProperty("transactionId").GetString() == stale).Json()
                .GetProperty("notification").GetProperty("statusNotification");
            notice.GetProperty("sessionStatus").GetString().ShouldBe("FAILED");
            var errored = notice.GetProperty("statusResponses").EnumerateArray().ShouldHaveSingleItem();
            (errored.GetProperty("hiStatus").GetString(), errored.GetProperty("description").GetString()).ShouldBe(("ERRORED", "The consent is revoked."));
        }
        finally
        {
            await AbdmLinkingTests.RemoveGatewayAsync(server);
            await SwitchAsync(enabled: false);
        }
    }

    /// <summary>A patient with an ABHA address and a signed consultation linked to it as a care context.</summary>
    private async Task<(Guid PatientId, string Address, string Reference)> LinkedConsultationAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var address = "asha." + AbdmExportTests.Letters(8) + "@sbx";
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", AbdmExportTests.Registration(AbdmExportTests.NewAbha(), address));
        var consultation = await AbdmExportTests.ConsultAsync(doctor, patient.Id, sign: true);
        var reference = AbdmLinkingService.ReferenceNumber(AbdmRecordType.OpConsultation, consultation);

        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        db.Add(new AbdmLinkedCareContext
        {
            HipId = AbdmLinkingTests.HipId,
            PatientId = patient.Id,
            AbhaAddress = address,
            RecordType = AbdmRecordType.OpConsultation,
            RecordId = consultation,
            ReferenceNumber = reference,
            Display = "Consultation note",
            Route = AbdmLinkRoute.HipInitiated,
            LinkedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
        return (patient.Id, address, reference);
    }

    private static object Notify(string consentId, string status, string address, Guid patientId, string reference) => new
    {
        notification = new
        {
            status,
            consentId,
            consentDetail = new
            {
                schemaVersion = "v3",
                consentId,
                createdAt = DateTimeOffset.UtcNow.ToString("O"),
                patient = new { id = address },
                careContexts = new[] { new { patientReference = patientId.ToString("D"), careContextReference = reference } },
                purpose = new { text = "Care Management", code = "CAREMGT" },
                hip = new { id = AbdmLinkingTests.HipId },
                hiu = new { id = "IN-HIU-IT" },
                hiTypes = new[] { "OPConsultation" },
                permission = new
                {
                    accessMode = "VIEW",
                    dateRange = new { from = "2000-01-01T00:00:00.000Z", to = DateTimeOffset.UtcNow.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") },
                    dataEraseAt = DateTimeOffset.UtcNow.AddDays(30).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                    frequency = new { unit = "HOUR", value = 1, repeats = 0 },
                },
            },
        },
    };

    private static object Request(string transactionId, string consentId, FideliusKeyPair hiu, string pushUrl) => new
    {
        transactionId,
        hiRequest = new
        {
            consent = new { id = consentId },
            dateRange = new { from = "2000-01-01T00:00:00.000Z", to = DateTimeOffset.UtcNow.AddHours(1).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") },
            dataPushUrl = pushUrl,
            keyMaterial = new
            {
                cryptoAlg = Fidelius.CryptoAlgorithm,
                curve = Fidelius.Curve,
                dhPublicKey = new { expiry = DateTimeOffset.UtcNow.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), parameters = Fidelius.KeyParameters, keyValue = hiu.PublicKey },
                nonce = hiu.Nonce,
            },
        },
    };

    /// <summary>Sends a signed call from the gateway under a new REQUEST-ID, which is returned.</summary>
    private async Task<string> CallAsync(string path, object body)
    {
        var requestId = Guid.NewGuid().ToString();
        using var response = await AbdmLinkingTests.CallBackAsync(server.Anonymous(ownAddress: true), path, body, Gateway.Token(), requestId);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, path);
        return requestId;
    }

    private JsonElement Answer(string path, string requestId) =>
        Gateway.CallsTo(path).Single(c => c.Json().GetProperty("response").GetProperty("requestId").GetString() == requestId).Json();

    /// <summary>The transfer once the outbox has carried it out (it runs in the test server's background).</summary>
    private async Task<AbdmTransfer> TransferAsync(string transactionId)
    {
        for (var wait = 0; wait < 120; wait++)
        {
            await using var scope = server.Services.CreateAsyncScope();
            var transfer = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AbdmTransfer>().AsNoTracking()
                .SingleAsync(t => t.TransactionId == transactionId, Ct);
            if (transfer.Status != AbdmTransferStatus.Acknowledged)
            {
                return transfer;
            }

            await Task.Delay(500, Ct);
        }

        throw new TimeoutException($"Transfer {transactionId} did not run within a minute.");
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
            .FirstOrDefaultAsync(Ct);
    }
}
