using System.Globalization;
using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Immunizations;
using HyFib.HealthCare360.Modules.Clinical.Features.Notes;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Features;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
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

    [Fact]
    public async Task A_grant_arriving_after_a_revocation_does_not_revive_the_consent_and_no_records_flow()
    {
        var (patientId, address, reference) = await LinkedConsultationAsync();
        var consentId = Guid.NewGuid().ToString();
        var hiu = Fidelius.Generate();

        try
        {
            await SwitchAsync(enabled: true);
            await AbdmLinkingTests.SetUpGatewayAsync(server, enabled: true);

            await CallAsync(AbdmGatewayCallbackEndpoints.ConsentNotifyPath, Notify(consentId, "GRANTED", address, patientId, reference));
            await CallAsync(AbdmGatewayCallbackEndpoints.ConsentNotifyPath, Notify(consentId, "REVOKED", address, patientId, reference));

            // The late grant is acknowledged, so the gateway stops retrying, and audited as ignored.
            var late = await CallAsync(AbdmGatewayCallbackEndpoints.ConsentNotifyPath, Notify(consentId, "GRANTED", address, patientId, reference));
            Answer(HttpAbdmGatewayClient.OnConsentNotifyPath, late).GetProperty("acknowledgement").GetProperty("status").GetString().ShouldBe("OK");
            (await AuditAsync(patientId, AbdmDataFlowService.ConsentGrantIgnored)).ShouldNotBeNull();

            await using (var scope = server.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
                var consent = await db.Set<AbdmConsent>().AsNoTracking().SingleAsync(c => c.ConsentId == consentId, Ct);
                consent.Status.ShouldBe("REVOKED");
                consent.EndedAt.ShouldNotBeNull();
            }

            // A request for the records under it is refused, and nothing is pushed.
            var transaction = Guid.NewGuid().ToString();
            var requested = await CallAsync(AbdmGatewayCallbackEndpoints.HealthInformationRequestPath, Request(transaction, consentId, hiu, PushUrl));
            Answer(HttpAbdmGatewayClient.OnHealthInformationRequestPath, requested).GetProperty("error").GetProperty("message").GetString().ShouldBe("The consent is revoked.");
            Gateway.CallsTo(PushUrl).ShouldNotContain(c => c.Json().GetProperty("transactionId").GetString() == transaction);
        }
        finally
        {
            await AbdmLinkingTests.RemoveGatewayAsync(server);
            await SwitchAsync(enabled: false);
        }
    }

    [Fact]
    public async Task A_vaccine_record_is_sent_only_under_a_consent_for_immunisation_records()
    {
        var (patientId, address, reference) = await LinkedDoseAsync();
        var hiu = Fidelius.Generate();

        try
        {
            await SwitchAsync(enabled: true);
            await AbdmLinkingTests.SetUpGatewayAsync(server, enabled: true);

            // The patient chose the dose for a consent to share prescriptions: the consent does not cover it, and it is not sent.
            var prescriptions = Guid.NewGuid().ToString();
            await CallAsync(AbdmGatewayCallbackEndpoints.ConsentNotifyPath, Notify(prescriptions, "GRANTED", address, patientId, reference, "Prescription"));
            var refused = Guid.NewGuid().ToString();
            await CallAsync(AbdmGatewayCallbackEndpoints.HealthInformationRequestPath, Request(refused, prescriptions, hiu, PushUrl));
            var notSent = await TransferAsync(refused);
            notSent.Status.ShouldBe(AbdmTransferStatus.Failed);
            notSent.Error.ShouldBe(AbdmDataFlowService.NotCovered);
            Gateway.CallsTo(PushUrl).ShouldNotContain(c => c.Json().GetProperty("transactionId").GetString() == refused);

            // Under a consent for immunisation records it reaches the HIU, as the guide's immunisation record.
            var immunisations = Guid.NewGuid().ToString();
            await CallAsync(AbdmGatewayCallbackEndpoints.ConsentNotifyPath, Notify(immunisations, "GRANTED", address, patientId, reference, "ImmunizationRecord"));
            var transaction = Guid.NewGuid().ToString();
            await CallAsync(AbdmGatewayCallbackEndpoints.HealthInformationRequestPath, Request(transaction, immunisations, hiu, PushUrl));
            var sent = await TransferAsync(transaction);
            sent.Status.ShouldBe(AbdmTransferStatus.Transferred, sent.Error);
            var page = Gateway.Calls.Single(c => c.Path == PushUrl && c.Json().GetProperty("transactionId").GetString() == transaction).Json();
            var entry = page.GetProperty("entries").EnumerateArray().ShouldHaveSingleItem();
            entry.GetProperty("careContextReference").GetString().ShouldBe(reference);
            var keyMaterial = page.GetProperty("keyMaterial");
            var document = Fidelius.Decrypt(
                entry.GetProperty("content").GetString()!, hiu.PrivateKey, hiu.Nonce,
                keyMaterial.GetProperty("dhPublicKey").GetProperty("keyValue").GetString()!, keyMaterial.GetProperty("nonce").GetString()!);
            var composition = JsonDocument.Parse(document.Value).RootElement.GetProperty("entry")[0].GetProperty("resource");
            composition.GetProperty("type").GetProperty("coding")[0].GetProperty("code").GetString().ShouldBe("41000179103");
        }
        finally
        {
            await AbdmLinkingTests.RemoveGatewayAsync(server);
            await SwitchAsync(enabled: false);
        }
    }

    [Fact]
    public async Task A_bill_is_sent_only_under_a_consent_for_invoices()
    {
        var (patientId, address, reference, billNumber) = await LinkedBillAsync();
        var hiu = Fidelius.Generate();

        try
        {
            await SwitchAsync(enabled: true);
            await AbdmLinkingTests.SetUpGatewayAsync(server, enabled: true);

            // The patient chose the bill for a consent to share consultations: the consent does not cover it, and it is not sent.
            var consultations = Guid.NewGuid().ToString();
            await CallAsync(AbdmGatewayCallbackEndpoints.ConsentNotifyPath, Notify(consultations, "GRANTED", address, patientId, reference, "OPConsultation"));
            var refused = Guid.NewGuid().ToString();
            await CallAsync(AbdmGatewayCallbackEndpoints.HealthInformationRequestPath, Request(refused, consultations, hiu, PushUrl));
            var notSent = await TransferAsync(refused);
            notSent.Status.ShouldBe(AbdmTransferStatus.Failed);
            notSent.Error.ShouldBe(AbdmDataFlowService.NotCovered);
            Gateway.CallsTo(PushUrl).ShouldNotContain(c => c.Json().GetProperty("transactionId").GetString() == refused);

            // Under a consent for invoices it reaches the HIU as the guide's invoice record, the bill's Invoice in it.
            var invoices = Guid.NewGuid().ToString();
            await CallAsync(AbdmGatewayCallbackEndpoints.ConsentNotifyPath, Notify(invoices, "GRANTED", address, patientId, reference, "Invoice"));
            var transaction = Guid.NewGuid().ToString();
            await CallAsync(AbdmGatewayCallbackEndpoints.HealthInformationRequestPath, Request(transaction, invoices, hiu, PushUrl));
            var sent = await TransferAsync(transaction);
            sent.Status.ShouldBe(AbdmTransferStatus.Transferred, sent.Error);
            var page = Gateway.Calls.Single(c => c.Path == PushUrl && c.Json().GetProperty("transactionId").GetString() == transaction).Json();
            var entry = page.GetProperty("entries").EnumerateArray().ShouldHaveSingleItem();
            entry.GetProperty("careContextReference").GetString().ShouldBe(reference);
            var keyMaterial = page.GetProperty("keyMaterial");
            var document = Fidelius.Decrypt(
                entry.GetProperty("content").GetString()!, hiu.PrivateKey, hiu.Nonce,
                keyMaterial.GetProperty("dhPublicKey").GetProperty("keyValue").GetString()!, keyMaterial.GetProperty("nonce").GetString()!);
            var resources = JsonDocument.Parse(document.Value).RootElement.GetProperty("entry").EnumerateArray().Select(e => e.GetProperty("resource")).ToList();
            resources[0].GetProperty("type").GetProperty("coding")[0].GetProperty("code").GetString().ShouldBe("INVOICE");
            resources.Single(r => r.GetProperty("resourceType").GetString() == "Invoice").GetProperty("identifier")[0].GetProperty("value").GetString()
                .ShouldBe(billNumber);
        }
        finally
        {
            await AbdmLinkingTests.RemoveGatewayAsync(server);
            await SwitchAsync(enabled: false);
        }
    }

    [Fact]
    public async Task A_clinical_note_is_sent_with_its_addendum_only_under_a_consent_for_health_documents()
    {
        var (patientId, address, reference) = await LinkedNoteAsync();
        var hiu = Fidelius.Generate();

        try
        {
            await SwitchAsync(enabled: true);
            await AbdmLinkingTests.SetUpGatewayAsync(server, enabled: true);

            // The patient chose the note for a consent to share consultations: the consent does not cover it, and it is not sent.
            var consultations = Guid.NewGuid().ToString();
            await CallAsync(AbdmGatewayCallbackEndpoints.ConsentNotifyPath, Notify(consultations, "GRANTED", address, patientId, reference, "OPConsultation"));
            var refused = Guid.NewGuid().ToString();
            await CallAsync(AbdmGatewayCallbackEndpoints.HealthInformationRequestPath, Request(refused, consultations, hiu, PushUrl));
            var notSent = await TransferAsync(refused);
            notSent.Status.ShouldBe(AbdmTransferStatus.Failed);
            notSent.Error.ShouldBe(AbdmDataFlowService.NotCovered);
            Gateway.CallsTo(PushUrl).ShouldNotContain(c => c.Json().GetProperty("transactionId").GetString() == refused);

            // Under a consent for health documents it reaches the HIU as the guide's health document, the addendum signed
            // since in it.
            var documents = Guid.NewGuid().ToString();
            await CallAsync(AbdmGatewayCallbackEndpoints.ConsentNotifyPath, Notify(documents, "GRANTED", address, patientId, reference, "HealthDocumentRecord"));
            var transaction = Guid.NewGuid().ToString();
            await CallAsync(AbdmGatewayCallbackEndpoints.HealthInformationRequestPath, Request(transaction, documents, hiu, PushUrl));
            var sent = await TransferAsync(transaction);
            sent.Status.ShouldBe(AbdmTransferStatus.Transferred, sent.Error);
            var page = Gateway.Calls.Single(c => c.Path == PushUrl && c.Json().GetProperty("transactionId").GetString() == transaction).Json();
            var entry = page.GetProperty("entries").EnumerateArray().ShouldHaveSingleItem();
            entry.GetProperty("careContextReference").GetString().ShouldBe(reference);
            var keyMaterial = page.GetProperty("keyMaterial");
            var document = Fidelius.Decrypt(
                entry.GetProperty("content").GetString()!, hiu.PrivateKey, hiu.Nonce,
                keyMaterial.GetProperty("dhPublicKey").GetProperty("keyValue").GetString()!, keyMaterial.GetProperty("nonce").GetString()!);
            var bundle = JsonDocument.Parse(document.Value).RootElement;
            var composition = bundle.GetProperty("entry")[0].GetProperty("resource");
            composition.GetProperty("type").GetProperty("coding")[0].GetProperty("code").GetString().ShouldBe("419891008");
            composition.GetProperty("status").GetString().ShouldBe("amended");
            bundle.GetProperty("entry").EnumerateArray().Count(e => e.GetProperty("resource").GetProperty("resourceType").GetString() == "DocumentReference")
                .ShouldBe(2, "the note and its addendum");
        }
        finally
        {
            await AbdmLinkingTests.RemoveGatewayAsync(server);
            await SwitchAsync(enabled: false);
        }
    }

    [Fact]
    public async Task Home_readings_are_sent_a_document_a_month_and_only_those_measured_in_the_dates_asked_for()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var address = "uma." + AbdmExportTests.Letters(8) + "@sbx";
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", AbdmExportTests.Registration(AbdmExportTests.NewAbha(), address));
        var enrollment = await AbdmExportTests.EnrolAsync(doctor, patient.Id);
        await LinkAsync(patient.Id, address, AbdmRecordType.HomeReadings, enrollment, "Home readings: Hypertension monitoring");
        var reference = AbdmLinkingService.ReferenceNumber(AbdmRecordType.HomeReadings, enrollment);

        // Last month in India: a weight on the 3rd, a blood pressure on the 10th, a pulse at its last second; this month a
        // glucose at its first second and a temperature at noon on the 1st.
        var ist = TimeSpan.FromMinutes(330);
        var thisMonth = ReadingsSpan.Of(DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(ist).DateTime));
        var lastMonth = ReadingsSpan.Of(thisMonth.Month.AddMonths(-1));
        DateTimeOffset At(DateOnly day, int hour) => new(day.ToDateTime(new TimeOnly(hour, 0)), ist);
        await AbdmExportTests.ReadAsync(server, enrollment,
            (RpmReadingType.Weight, 70.2m, At(lastMonth.Month.AddDays(2), 8)),
            (RpmReadingType.SystolicBp, 132m, At(lastMonth.Month.AddDays(9), 8)), (RpmReadingType.DiastolicBp, 84m, At(lastMonth.Month.AddDays(9), 8)),
            (RpmReadingType.HeartRate, 71m, lastMonth.End.AddSeconds(-1)),
            (RpmReadingType.Glucose, 112m, thisMonth.Start), (RpmReadingType.Temperature, 36.8m, At(thisMonth.Month, 12)));
        var hiu = Fidelius.Generate();

        try
        {
            await SwitchAsync(enabled: true);
            await AbdmLinkingTests.SetUpGatewayAsync(server, enabled: true);

            // Chosen for a consent to share consultations, the programme is not sent: the consent does not cover its kind.
            var consultations = Guid.NewGuid().ToString();
            await CallAsync(AbdmGatewayCallbackEndpoints.ConsentNotifyPath, Notify(consultations, "GRANTED", address, patient.Id, reference, "OPConsultation"));
            var refused = Guid.NewGuid().ToString();
            await CallAsync(AbdmGatewayCallbackEndpoints.HealthInformationRequestPath, Request(refused, consultations, hiu, PushUrl));
            (await TransferAsync(refused)).Error.ShouldBe(AbdmDataFlowService.NotCovered);

            // Under a consent for wellness records, the HIU asks from the 5th of last month to 11 am on the 1st of this:
            // the weight of the 3rd and the temperature at noon are outside it; the glucose of this month's first second is in.
            var wellness = Guid.NewGuid().ToString();
            await CallAsync(AbdmGatewayCallbackEndpoints.ConsentNotifyPath, Notify(wellness, "GRANTED", address, patient.Id, reference, "WellnessRecord"));
            var from = At(lastMonth.Month.AddDays(4), 0);
            var to = At(thisMonth.Month, 11);
            var transaction = Guid.NewGuid().ToString();
            await CallAsync(AbdmGatewayCallbackEndpoints.HealthInformationRequestPath, Request(transaction, wellness, hiu, PushUrl, from, to));
            var sent = await TransferAsync(transaction);
            sent.Status.ShouldBe(AbdmTransferStatus.Transferred, sent.Error);

            // A document for each month, both under the programme's care context, each cut where the dates cut it.
            var page = Gateway.Calls.Single(c => c.Path == PushUrl && c.Json().GetProperty("transactionId").GetString() == transaction).Json();
            var keyMaterial = page.GetProperty("keyMaterial");
            var documents = page.GetProperty("entries").EnumerateArray().Select(entry =>
            {
                entry.GetProperty("careContextReference").GetString().ShouldBe(reference);
                var json = Fidelius.Decrypt(entry.GetProperty("content").GetString()!, hiu.PrivateKey, hiu.Nonce,
                    keyMaterial.GetProperty("dhPublicKey").GetProperty("keyValue").GetString()!, keyMaterial.GetProperty("nonce").GetString()!);
                return JsonDocument.Parse(json.Value).RootElement;
            }).ToList();
            documents.Count.ShouldBe(2);
            var compositions = documents.Select(d => d.GetProperty("entry")[0].GetProperty("resource")).ToList();
            compositions.Select(c => c.GetProperty("type").GetProperty("text").GetString()).ShouldAllBe(t => t == "Wellness Record");
            compositions.Select(c => c.GetProperty("title").GetString()!.Split(", ")[^1])
                .ShouldBe([lastMonth.Month.ToString("MMMM yyyy", CultureInfo.InvariantCulture), thisMonth.Month.ToString("MMMM yyyy", CultureInfo.InvariantCulture)]);
            var periods = compositions.Select(c => c.GetProperty("event")[0].GetProperty("period")).ToList();
            (periods[0].GetProperty("start").GetString(), periods[0].GetProperty("end").GetString())
                .ShouldBe((FhirTime.DateTime(from), lastMonth.Month.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            (periods[1].GetProperty("start").GetString(), periods[1].GetProperty("end").GetString())
                .ShouldBe((thisMonth.Month.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), FhirTime.DateTime(to)));
            List<string?> Codes(JsonElement document) =>
            [
                .. document.GetProperty("entry").EnumerateArray().Select(e => e.GetProperty("resource"))
                    .Where(r => r.GetProperty("resourceType").GetString() == "Observation")
                    .Select(r => r.GetProperty("code").GetProperty("coding")[0].GetProperty("code").GetString()),
            ];
            Codes(documents[0]).ShouldBe(["85354-9", "8867-4"], ignoreOrder: true, "the blood pressure and the pulse; not the weight of the 3rd");
            Codes(documents[1]).ShouldBe(["41653-7"], "the glucose; not the temperature at noon");

            // Dates in which nothing was measured: nothing to send, and the gateway is told so.
            var empty = Guid.NewGuid().ToString();
            await CallAsync(AbdmGatewayCallbackEndpoints.HealthInformationRequestPath,
                Request(empty, wellness, hiu, PushUrl, At(lastMonth.Month, 0), At(lastMonth.Month.AddDays(1), 0)));
            var nothing = await TransferAsync(empty);
            nothing.Status.ShouldBe(AbdmTransferStatus.Failed);
            nothing.Error.ShouldBe(AbdmBundleService.NoReadingsInDates);
        }
        finally
        {
            await AbdmLinkingTests.RemoveGatewayAsync(server);
            await SwitchAsync(enabled: false);
        }
    }

    [Fact]
    public async Task A_programme_whose_months_fill_more_than_one_page_is_delivered_only_when_every_page_is_accepted()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var address = "vani." + AbdmExportTests.Letters(8) + "@sbx";
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", AbdmExportTests.Registration(AbdmExportTests.NewAbha(), address));
        var enrollment = await AbdmExportTests.EnrolAsync(doctor, patient.Id);
        await LinkAsync(patient.Id, address, AbdmRecordType.HomeReadings, enrollment, "Home readings: Hypertension monitoring");
        var reference = AbdmLinkingService.ReferenceNumber(AbdmRecordType.HomeReadings, enrollment);

        // A weight on the 10th of each of the months before this one in India: a document a month, one more than a page holds.
        var ist = TimeSpan.FromMinutes(330);
        var thisMonth = ReadingsSpan.Of(DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(ist).DateTime)).Month;
        await AbdmExportTests.ReadAsync(server, enrollment,
        [
            .. Enumerable.Range(1, AbdmDataFlowService.PageSize + 1).Select(back =>
                (RpmReadingType.Weight, 70m + back, new DateTimeOffset(thisMonth.AddMonths(-back).AddDays(9).ToDateTime(new TimeOnly(8, 0)), ist))),
        ]);
        var hiu = Fidelius.Generate();
        var transaction = Guid.NewGuid().ToString();

        // The HIU refuses the first page and accepts the second.
        static bool IsPage(GatewayCall call, string transaction, int number) =>
            call.Path == PushUrl && call.Json() is var page
            && page.GetProperty("transactionId").GetString() == transaction && page.GetProperty("pageNumber").GetInt32() == number;
        Gateway.Answer(call => IsPage(call, transaction, 0) ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null);

        try
        {
            await SwitchAsync(enabled: true);
            await AbdmLinkingTests.SetUpGatewayAsync(server, enabled: true);
            var wellness = Guid.NewGuid().ToString();
            await CallAsync(AbdmGatewayCallbackEndpoints.ConsentNotifyPath, Notify(wellness, "GRANTED", address, patient.Id, reference, "WellnessRecord"));
            await CallAsync(AbdmGatewayCallbackEndpoints.HealthInformationRequestPath, Request(transaction, wellness, hiu, PushUrl));
            var transfer = await TransferAsync(transaction);

            // Both pages were sent, each holding documents of the programme only.
            var pages = Gateway.CallsTo(PushUrl).Where(c => IsPage(c, transaction, 0) || IsPage(c, transaction, 1)).Select(c => c.Json()).ToList();
            pages.Select(p => (p.GetProperty("pageNumber").GetInt32(), p.GetProperty("pageCount").GetInt32(), p.GetProperty("entries").GetArrayLength()))
                .ShouldBe([(0, 2, AbdmDataFlowService.PageSize), (1, 2, 1)]);
            pages.SelectMany(p => p.GetProperty("entries").EnumerateArray()).ShouldAllBe(e => e.GetProperty("careContextReference").GetString() == reference);

            // The second page went through, but the programme did not all arrive: the gateway hears it errored.
            transfer.Status.ShouldBe(AbdmTransferStatus.Failed);
            transfer.Error.ShouldBe("The HIU did not accept the page.");
            var status = Gateway.CallsTo(HttpAbdmGatewayClient.TransferNotifyPath)
                .Single(c => c.Json().GetProperty("notification").GetProperty("transactionId").GetString() == transaction).Json()
                .GetProperty("notification").GetProperty("statusNotification");
            status.GetProperty("sessionStatus").GetString().ShouldBe("FAILED");
            var programme = status.GetProperty("statusResponses").EnumerateArray().ShouldHaveSingleItem();
            (programme.GetProperty("careContextReference").GetString(), programme.GetProperty("hiStatus").GetString()).ShouldBe((reference, "ERRORED"));
        }
        finally
        {
            Gateway.Answer(null);
            await AbdmLinkingTests.RemoveGatewayAsync(server);
            await SwitchAsync(enabled: false);
        }
    }

    /// <summary>A patient with an ABHA address and a signed progress note, with a signed addendum, linked to it as a care context.</summary>
    private async Task<(Guid PatientId, string Address, string Reference)> LinkedNoteAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var address = "ravi." + AbdmExportTests.Letters(8) + "@sbx";
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", AbdmExportTests.Registration(AbdmExportTests.NewAbha(), address));
        Task<ClinicalNoteDto> Signed(ClinicalNoteDto note) =>
            doctor.PostAsync<ClinicalNoteDto>($"/api/v1/emr/notes/{note.Id}/sign", new SignNoteRequest(note.Version));
        var note = await Signed(await doctor.PostAsync<ClinicalNoteDto>($"/api/v1/emr/patients/{patient.Id}/notes",
            new WriteNoteRequest(null, null, ClinicalNoteType.ProgressNote, null, "Review after the antibiotics: better.")));
        await Signed(await doctor.PostAsync<ClinicalNoteDto>($"/api/v1/emr/notes/{note.Id}/addendum", new AddendumRequest("Cough settled too.")));
        await LinkAsync(patient.Id, address, AbdmRecordType.ClinicalNote, note.Id, "Clinical note: Progress note");
        return (patient.Id, address, AbdmLinkingService.ReferenceNumber(AbdmRecordType.ClinicalNote, note.Id));
    }

    /// <summary>A patient with an ABHA address and a signed consultation linked to it as a care context.</summary>
    private async Task<(Guid PatientId, string Address, string Reference)> LinkedConsultationAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var address = "asha." + AbdmExportTests.Letters(8) + "@sbx";
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", AbdmExportTests.Registration(AbdmExportTests.NewAbha(), address));
        var consultation = await AbdmExportTests.ConsultAsync(doctor, patient.Id, sign: true);
        await LinkAsync(patient.Id, address, AbdmRecordType.OpConsultation, consultation, "Consultation note");
        return (patient.Id, address, AbdmLinkingService.ReferenceNumber(AbdmRecordType.OpConsultation, consultation));
    }

    /// <summary>A patient with an ABHA address and a vaccine dose given here linked to it as a care context.</summary>
    private async Task<(Guid PatientId, string Address, string Reference)> LinkedDoseAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var address = "kiran." + AbdmExportTests.Letters(8) + "@sbx";
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", AbdmExportTests.Registration(AbdmExportTests.NewAbha(), address));
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromMinutes(330)).DateTime);
        var dose = await nurse.PostAsync<ImmunizationDto>($"/api/v1/emr/patients/{patient.Id}/immunizations", new RecordImmunizationRequest(
            null, "Td", "TD", 1, today, "IM", "Left deltoid", "TD2608-114", "Biological E. Limited", today.AddMonths(10), null, false, today.AddDays(28), null));
        await LinkAsync(patient.Id, address, AbdmRecordType.Immunization, dose.Id, "Immunisation record: Td, dose 1");
        return (patient.Id, address, AbdmLinkingService.ReferenceNumber(AbdmRecordType.Immunization, dose.Id));
    }

    /// <summary>A patient with an ABHA address and a finalised bill linked to it as a care context.</summary>
    private async Task<(Guid PatientId, string Address, string Reference, string BillNumber)> LinkedBillAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var address = "neha." + AbdmExportTests.Letters(8) + "@sbx";
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", AbdmExportTests.Registration(AbdmExportTests.NewAbha(), address));
        var bill = await AbdmInvoiceExportTests.FinalizedBillAsync(frontOffice, billing, patient.Id, dressings: 1m);
        await LinkAsync(patient.Id, address, AbdmRecordType.Invoice, bill.Id, $"Bill: {bill.BillNumber}");
        return (patient.Id, address, AbdmLinkingService.ReferenceNumber(AbdmRecordType.Invoice, bill.Id), bill.BillNumber!);
    }

    private async Task LinkAsync(Guid patientId, string address, AbdmRecordType type, Guid recordId, string display)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        db.Add(new AbdmLinkedCareContext
        {
            HipId = AbdmLinkingTests.HipId,
            PatientId = patientId,
            AbhaAddress = address,
            RecordType = type,
            RecordId = recordId,
            ReferenceNumber = AbdmLinkingService.ReferenceNumber(type, recordId),
            Display = display,
            Route = AbdmLinkRoute.HipInitiated,
            LinkedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
    }

    private static object Notify(string consentId, string status, string address, Guid patientId, string reference, string hiType = "OPConsultation") => new
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
                hiTypes = new[] { hiType },
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

    private static object Request(string transactionId, string consentId, FideliusKeyPair hiu, string pushUrl, DateTimeOffset? from = null, DateTimeOffset? to = null) => new
    {
        transactionId,
        hiRequest = new
        {
            consent = new { id = consentId },
            dateRange = new
            {
                from = (from ?? new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                to = (to ?? DateTimeOffset.UtcNow.AddHours(1)).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            },
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
