using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Features;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Scan and share (ABDM M1) over the real callback and a fake ABDM gateway (<see cref="FakeAbdmGateway"/>): a patient
/// scans the hospital's QR code at a counter and their ABHA profile arrives; they get a token number, the same one when
/// they scan again; a patient already on record is matched by their ABHA address; the desk sees the queue with contact
/// details masked, opens one share in full to register the patient, and marks it registered or dismisses it, once.
/// A share without an ABHA address is answered with an error, and one the gateway did not sign is refused.
/// </summary>
[Collection(FeatureSwitchCollection.Name)]
public sealed class AbdmProfileShareTests(HospitalServer server)
{
    private const string Counter = "REG-1";

    private FakeAbdmGateway Gateway => server.AbdmGateway;

    [Fact]
    public async Task A_shared_profile_gets_a_token_is_matched_and_the_desk_registers_the_patient_from_it()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var knownAddress = "meera." + AbdmExportTests.Letters(8) + "@sbx";
        var registration = AbdmExportTests.Registration(AbdmExportTests.NewAbha(), knownAddress);
        var known = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", registration);
        var newAddress = "arjun." + AbdmExportTests.Letters(8) + "@sbx";
        var newAbha = AbdmExportTests.NewAbha();
        var gatewayCaller = server.Anonymous(ownAddress: true);

        try
        {
            await SwitchAsync(enabled: true);
            await AbdmLinkingTests.SetUpGatewayAsync(server, enabled: true);

            // A share the gateway did not sign is refused.
            using (var forged = await AbdmLinkingTests.CallBackAsync(
                gatewayCaller, AbdmGatewayCallbackEndpoints.ProfileSharePath, Share(newAddress, "Arjun Rao", "M", "1990", null, null), FakeAbdmGateway.ForgedToken(), Guid.NewGuid().ToString()))
            {
                forged.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            }

            // The patient on record shares at the counter: a token, and the match by ABHA address.
            var first = Answer(await CallAsync(Share(knownAddress, $"Meera {registration.LastName}", "F", registration.DateOfBirth!.Value.Year, registration.Phone, null)));
            var ack = first.GetProperty("acknowledgement");
            ack.GetProperty("status").GetString().ShouldBe("SUCCESS");
            ack.GetProperty("abhaAddress").GetString().ShouldBe(knownAddress);
            var knownToken = ack.GetProperty("profile").GetProperty("tokenNumber").GetString().ShouldNotBeNull();
            knownToken.Length.ShouldBe(4);
            ack.GetProperty("profile").GetProperty("context").GetString().ShouldBe(Counter);

            // Scanning again within the hour gives the same token.
            var again = Answer(await CallAsync(Share(knownAddress, $"Meera {registration.LastName}", "F", registration.DateOfBirth!.Value.Year, registration.Phone, null)));
            again.GetProperty("acknowledgement").GetProperty("profile").GetProperty("tokenNumber").GetString().ShouldBe(knownToken);

            // Someone new, with the date parts as strings (one of them empty), gets the next token and no match.
            var phone = "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + Random.Shared.Next(10))));
            var second = Answer(await CallAsync(Share(newAddress, "Arjun Rao", "M", "1990", phone, newAbha)));
            var newToken = int.Parse(second.GetProperty("acknowledgement").GetProperty("profile").GetProperty("tokenNumber").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            newToken.ShouldBeGreaterThan(int.Parse(knownToken, System.Globalization.CultureInfo.InvariantCulture));

            // A share without an ABHA address is answered with an error.
            var invalid = Answer(await CallAsync(Share(string.Empty, "No Address", "M", "1990", null, null)));
            invalid.GetProperty("error").GetProperty("code").GetString().ShouldBe(AbdmProfileShareService.ErrorCode);

            // The desk's queue: contact details masked, the known patient matched.
            (await doctor.SendRawAsync(HttpMethod.Get, "/api/v1/abdm/shares")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            var queue = await frontOffice.GetAsync<List<AbdmShareRow>>("/api/v1/abdm/shares");
            var knownRow = queue.Single(r => r.AbhaAddress == knownAddress);
            knownRow.Status.ShouldBe(AbdmShareStatus.Waiting);
            knownRow.MatchedBy.ShouldBe("ABHA_ADDRESS");
            knownRow.Patient.ShouldNotBeNull().Id.ShouldBe(known.Id);
            var newRow = queue.Single(r => r.AbhaAddress == newAddress);
            newRow.TokenNumber.ShouldBe(newToken);
            newRow.Patient.ShouldBeNull();
            newRow.YearOfBirth.ShouldBe(1990);
            newRow.PhoneMasked.ShouldNotBeNull().ShouldNotContain(phone[2..]);
            newRow.AbhaNumberMasked.ShouldNotBeNull().ShouldNotContain(newAbha[..10]);

            // The share in full, to fill in the registration form.
            var detail = await frontOffice.GetAsync<AbdmShareDetail>($"/api/v1/abdm/shares/{newRow.Id}");
            detail.Phone.ShouldBe("+91" + phone);
            detail.AbhaNumber.ShouldBe(Abha.Format(newAbha));
            detail.DayOfBirth.ShouldBeNull();
            detail.Name.ShouldBe("Arjun Rao");

            // Registered from the share, once.
            var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", AbdmExportTests.Registration(newAbha, newAddress) with { FirstName = "Arjun", LastName = "Rao", Gender = Gender.Male });
            var registered = await frontOffice.PostAsync<AbdmShareRow>($"/api/v1/abdm/shares/{newRow.Id}/registered", new AbdmShareRegisteredInput(created.Id));
            registered.Status.ShouldBe(AbdmShareStatus.Registered);
            registered.Patient.ShouldNotBeNull().Id.ShouldBe(created.Id);
            using (var twice = await frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/abdm/shares/{newRow.Id}/registered", new AbdmShareRegisteredInput(created.Id)))
            {
                twice.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            }

            // The other is dismissed, and then cannot be registered.
            (await frontOffice.PostAsync<AbdmShareRow>($"/api/v1/abdm/shares/{knownRow.Id}/dismiss")).Status.ShouldBe(AbdmShareStatus.Dismissed);
            using (var late = await frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/abdm/shares/{knownRow.Id}/registered", new AbdmShareRegisteredInput(known.Id)))
            {
                late.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            }

            // Once dealt with, a new scan gets a new token.
            var after = Answer(await CallAsync(Share(knownAddress, $"Meera {registration.LastName}", "F", registration.DateOfBirth!.Value.Year, registration.Phone, null)));
            after.GetProperty("acknowledgement").GetProperty("profile").GetProperty("tokenNumber").GetString().ShouldNotBe(knownToken);
        }
        finally
        {
            await AbdmLinkingTests.RemoveGatewayAsync(server);
            await SwitchAsync(enabled: false);
        }
    }

    private static object Share(string address, string name, string gender, object yearOfBirth, string? phone, string? abhaNumber) => new
    {
        intent = "PROFILE_SHARE",
        metaData = new { hipId = AbdmLinkingTests.HipId, context = Counter, hprId = (string?)null, latitude = "12.97", longitude = "77.59" },
        profile = new
        {
            patient = new
            {
                abhaAddress = address,
                abhaNumber,
                name,
                gender,
                address = new { line = "12 MG Road", district = "Bengaluru", state = "Karnataka", pincode = "560001" },
                yearOfBirth,
                dayOfBirth = string.Empty,
                monthOfBirth = "6",
                phoneNumber = phone,
                identifiers = Array.Empty<object>(),
            },
        },
    };

    private async Task<string> CallAsync(object body)
    {
        var requestId = Guid.NewGuid().ToString();
        using var response = await AbdmLinkingTests.CallBackAsync(server.Anonymous(ownAddress: true), AbdmGatewayCallbackEndpoints.ProfileSharePath, body, Gateway.Token(), requestId);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return requestId;
    }

    private JsonElement Answer(string requestId) =>
        Gateway.CallsTo(HttpAbdmGatewayClient.OnSharePath).Single(c => c.Json().GetProperty("response").GetProperty("requestId").GetString() == requestId).Json();

    private async Task SwitchAsync(bool enabled)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        (await admin.SetFeatureAsync(FeatureKeys.Abdm, new SetFeatureRequest(enabled))).Enabled.ShouldBe(enabled);
    }
}
