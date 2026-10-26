using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Features;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// User-initiated linking over the real callbacks and a fake ABDM gateway (<see cref="FakeAbdmGateway"/>, ABDM M2): a
/// patient searches for their records from their PHR app, is found by their ABHA address (or by the mobile number ABDM
/// verified, with their gender, year of birth and name), is offered their signed consultations, chooses one, and links
/// it with the code sent to their mobile number. A linked record is not offered again; a record not offered cannot be
/// chosen; a wrong code is refused and three of them end the request; and a call the gateway did not sign is refused.
/// The ABDM switch is hospital-wide, so these tests are in the feature-switch collection.
/// </summary>
[Collection(FeatureSwitchCollection.Name)]
public sealed class AbdmUserLinkingTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeAbdmGateway Gateway => server.AbdmGateway;

    [Fact]
    public async Task A_patient_finds_their_consultations_by_abha_address_and_links_one_with_the_code_sent_to_them()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var address = "ravi." + AbdmExportTests.Letters(8) + "@sbx";
        var registration = AbdmExportTests.Registration(AbdmExportTests.NewAbha(), address);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", registration);
        var first = await AbdmExportTests.ConsultAsync(doctor, patient.Id, sign: true);
        var second = await AbdmExportTests.ConsultAsync(doctor, patient.Id, sign: true);
        await AbdmExportTests.ConsultAsync(doctor, patient.Id, sign: false);
        var gatewayCaller = server.Anonymous(ownAddress: true);

        try
        {
            await SwitchAsync(enabled: true);
            await AbdmLinkingTests.SetUpGatewayAsync(server, enabled: true);

            // A search the gateway did not sign is refused, and answered nothing.
            var transaction = Guid.NewGuid().ToString();
            var discover = Discover(transaction, address, registration, mobile: null);
            using (var forged = await AbdmLinkingTests.CallBackAsync(gatewayCaller, AbdmGatewayCallbackEndpoints.DiscoverPath, discover, FakeAbdmGateway.ForgedToken(), Guid.NewGuid().ToString()))
            {
                forged.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            }

            Answers(HttpAbdmGatewayClient.OnDiscoverPath, transaction).ShouldBeEmpty();

            // Found by the ABHA address: the signed consultations are offered, the draft is not.
            var discovered = await CallAsync(AbdmGatewayCallbackEndpoints.DiscoverPath, discover);
            var offer = Answers(HttpAbdmGatewayClient.OnDiscoverPath, transaction).ShouldHaveSingleItem().Json();
            offer.GetProperty("response").GetProperty("requestId").GetString().ShouldBe(discovered);
            offer.GetProperty("matchedBy").EnumerateArray().Select(m => m.GetString()).ShouldBe(["ABHA_ADDRESS"]);
            var group = offer.GetProperty("patient").EnumerateArray().ShouldHaveSingleItem();
            group.GetProperty("referenceNumber").GetString().ShouldBe(patient.Id.ToString("D"));
            group.GetProperty("hiType").GetString().ShouldBe("OPConsultation");
            group.GetProperty("display").GetString().ShouldNotBeNull().ShouldContain(registration.LastName!);
            References(group).ShouldBe([$"op-consultation/{second}", $"op-consultation/{first}"], ignoreOrder: true);
            (await AuditAsync(patient.Id, AbdmUserLinkingService.Discovered)).ShouldNotBeNull();

            // The patient chooses the first; a code goes to their mobile number, and the gateway gets the link reference.
            var initiated = await CallAsync(AbdmGatewayCallbackEndpoints.LinkInitPath, Init(transaction, address, patient.Id, $"op-consultation/{first}"));
            var init = Answers(HttpAbdmGatewayClient.OnLinkInitPath, transaction).ShouldHaveSingleItem().Json();
            init.GetProperty("response").GetProperty("requestId").GetString().ShouldBe(initiated);
            var link = init.GetProperty("link");
            link.GetProperty("authenticationType").GetString().ShouldBe("MEDIATE");
            link.GetProperty("meta").GetProperty("communicationMedium").GetString().ShouldBe("MOBILE");
            var hint = link.GetProperty("meta").GetProperty("communicationHint").GetString().ShouldNotBeNull();
            hint.ShouldEndWith(registration.Phone![^4..]);
            hint.ShouldNotContain(registration.Phone[..6]);
            var code = await CodeAsync(registration.Phone);

            // One code per search: asking again under the same search is refused, so wrong guesses cannot be reset.
            await CallAsync(AbdmGatewayCallbackEndpoints.LinkInitPath, Init(transaction, address, patient.Id, $"op-consultation/{first}"));
            Answers(HttpAbdmGatewayClient.OnLinkInitPath, transaction).Last().Json().GetProperty("error").GetProperty("code").GetString()
                .ShouldBe(AbdmUserLinkingService.ErrorCode);

            // A wrong code is refused with ABDM's code for it.
            var wrong = await CallAsync(AbdmGatewayCallbackEndpoints.LinkConfirmPath, Confirm(link, code == "000000" ? "111111" : "000000"));
            Error(Answer(HttpAbdmGatewayClient.OnLinkConfirmPath, wrong)).ShouldBe(AbdmUserLinkingService.WrongCodeCode);

            // The right one links it.
            var confirmed = await CallAsync(AbdmGatewayCallbackEndpoints.LinkConfirmPath, Confirm(link, code));
            var linked = Answer(HttpAbdmGatewayClient.OnLinkConfirmPath, confirmed);
            linked.TryGetProperty("error", out _).ShouldBeFalse();
            References(linked.GetProperty("patient").EnumerateArray().ShouldHaveSingleItem()).ShouldBe([$"op-consultation/{first}"]);
            var careContext = (await LinkedAsync(patient.Id)).ShouldHaveSingleItem();
            (careContext.ReferenceNumber, careContext.Route, careContext.AbhaAddress).ShouldBe(($"op-consultation/{first}", AbdmLinkRoute.UserInitiated, address));
            (await AuditAsync(patient.Id, AbdmUserLinkingService.UserLinked)).ShouldNotBeNull();

            // The code is spent.
            var again = await CallAsync(AbdmGatewayCallbackEndpoints.LinkConfirmPath, Confirm(link, code));
            Error(Answer(HttpAbdmGatewayClient.OnLinkConfirmPath, again)).ShouldBe(AbdmUserLinkingService.ErrorCode);

            // A later search offers only the record not linked yet, and the linked one cannot be chosen.
            var later = Guid.NewGuid().ToString();
            await CallAsync(AbdmGatewayCallbackEndpoints.DiscoverPath, Discover(later, address, registration, mobile: null));
            References(Answers(HttpAbdmGatewayClient.OnDiscoverPath, later).ShouldHaveSingleItem().Json().GetProperty("patient").EnumerateArray().Single())
                .ShouldBe([$"op-consultation/{second}"]);
            await CallAsync(AbdmGatewayCallbackEndpoints.LinkInitPath, Init(later, address, patient.Id, $"op-consultation/{first}"));
            var refused = Answers(HttpAbdmGatewayClient.OnLinkInitPath, later).ShouldHaveSingleItem().Json();
            refused.GetProperty("error").GetProperty("message").GetString().ShouldBe("Care context reference numbers are invalid");
        }
        finally
        {
            await AbdmLinkingTests.RemoveGatewayAsync(server);
            await SwitchAsync(enabled: false);
        }
    }

    [Fact]
    public async Task A_patient_without_an_abha_address_on_record_is_found_by_their_verified_mobile_and_three_wrong_codes_end_the_request()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var registration = AbdmExportTests.Registration(AbdmExportTests.NewAbha(), address: null);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", registration);
        var consultation = await AbdmExportTests.ConsultAsync(doctor, patient.Id, sign: true);
        var address = "meera." + AbdmExportTests.Letters(8) + "@sbx";

        try
        {
            await SwitchAsync(enabled: true);
            await AbdmLinkingTests.SetUpGatewayAsync(server, enabled: true);

            // Someone else's gender is not this patient.
            var other = Guid.NewGuid().ToString();
            await CallAsync(AbdmGatewayCallbackEndpoints.DiscoverPath, Discover(other, address, registration, registration.Phone, gender: "M"));
            Error(Answers(HttpAbdmGatewayClient.OnDiscoverPath, other).ShouldHaveSingleItem().Json()).ShouldBe(AbdmUserLinkingService.NotFoundCode);

            var transaction = Guid.NewGuid().ToString();
            await CallAsync(AbdmGatewayCallbackEndpoints.DiscoverPath, Discover(transaction, address, registration, registration.Phone));
            var offer = Answers(HttpAbdmGatewayClient.OnDiscoverPath, transaction).ShouldHaveSingleItem().Json();
            offer.GetProperty("matchedBy").EnumerateArray().Select(m => m.GetString()).ShouldBe(["MOBILE"]);
            References(offer.GetProperty("patient").EnumerateArray().Single()).ShouldBe([$"op-consultation/{consultation}"]);

            await CallAsync(AbdmGatewayCallbackEndpoints.LinkInitPath, Init(transaction, address, patient.Id, $"op-consultation/{consultation}"));
            var link = Answers(HttpAbdmGatewayClient.OnLinkInitPath, transaction).ShouldHaveSingleItem().Json().GetProperty("link");
            var code = await CodeAsync(registration.Phone!);
            var wrong = code == "000000" ? "111111" : "000000";
            for (var attempt = 0; attempt < AbdmDiscovery.MaxAttempts; attempt++)
            {
                var refused = await CallAsync(AbdmGatewayCallbackEndpoints.LinkConfirmPath, Confirm(link, wrong));
                Error(Answer(HttpAbdmGatewayClient.OnLinkConfirmPath, refused)).ShouldBe(AbdmUserLinkingService.WrongCodeCode);
            }

            // The right code comes too late: the request has ended, and nothing was linked.
            var late = await CallAsync(AbdmGatewayCallbackEndpoints.LinkConfirmPath, Confirm(link, code));
            Error(Answer(HttpAbdmGatewayClient.OnLinkConfirmPath, late)).ShouldBe(AbdmUserLinkingService.ErrorCode);
            (await LinkedAsync(patient.Id)).ShouldBeEmpty();
        }
        finally
        {
            await AbdmLinkingTests.RemoveGatewayAsync(server);
            await SwitchAsync(enabled: false);
        }
    }

    private static object Discover(string transactionId, string address, RegisterPatientRequest patient, string? mobile, string gender = "F") => new
    {
        transactionId,
        patient = new
        {
            id = address,
            name = $"{patient.FirstName} {patient.LastName}",
            gender,
            yearOfBirth = patient.DateOfBirth!.Value.Year,
            verifiedIdentifiers = mobile is null ? Array.Empty<object>() : [new { type = "MOBILE", value = mobile }],
            unverifiedIdentifiers = Array.Empty<object>(),
        },
    };

    private static object Init(string transactionId, string address, Guid patientId, string reference) => new
    {
        transactionId,
        abhaAddress = address,
        patient = new[]
        {
            new { referenceNumber = patientId.ToString("D"), careContexts = new[] { new { referenceNumber = reference } }, hiType = "OPConsultation", count = 1 },
        },
    };

    private static object Confirm(JsonElement link, string token) => new
    {
        confirmation = new { linkRefNumber = link.GetProperty("referenceNumber").GetString(), token },
    };

    /// <summary>Sends a signed call from the gateway under a new REQUEST-ID, which is returned.</summary>
    private async Task<string> CallAsync(string path, object body)
    {
        var requestId = Guid.NewGuid().ToString();
        using var response = await AbdmLinkingTests.CallBackAsync(server.Anonymous(ownAddress: true), path, body, Gateway.Token(), requestId);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, path);
        return requestId;
    }

    /// <summary>The answers sent to <paramref name="path"/> for the transaction.</summary>
    private List<GatewayCall> Answers(string path, string transactionId) =>
        [.. Gateway.CallsTo(path).Where(c => c.Json().GetProperty("transactionId").GetString() == transactionId)];

    /// <summary>The answer sent to <paramref name="path"/> for the call made under <paramref name="requestId"/>.</summary>
    private JsonElement Answer(string path, string requestId) =>
        Gateway.CallsTo(path).Single(c => c.Json().GetProperty("response").GetProperty("requestId").GetString() == requestId).Json();

    private static string? Error(JsonElement answer) => answer.GetProperty("error").GetProperty("code").GetString();

    private static List<string?> References(JsonElement group) =>
        [.. group.GetProperty("careContexts").EnumerateArray().Select(c => c.GetProperty("referenceNumber").GetString())];

    // The test server sends no SMS: the code waits in the outbox.
    private async Task<string> CodeAsync(string phone)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var deliveries = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Type == NotificationService.SmsType)
            .OrderByDescending(m => m.CreatedAt)
            .Take(50)
            .ToListAsync(Ct);
        return deliveries.Select(m => m.Read<NotificationDelivery>())
            .First(d => d.TemplateKey == AbdmUserLinkingService.CodeTemplate && d.To == Phone.Normalize(phone))
            .Values["code"];
    }

    private async Task<List<AbdmLinkedCareContext>> LinkedAsync(Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AbdmLinkedCareContext>().AsNoTracking()
            .Where(c => c.PatientId == patientId).ToListAsync(Ct);
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
