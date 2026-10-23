using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Queue;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SRS SEC-06 over the real API. Text longer than the column that keeps it was a database error, answered as a 500 with
/// nothing saved; it is now a 400 that names the field, before the handler runs. The architecture test checks that every
/// write validates its body; these show what that changes for a value the database itself refuses.
/// </summary>
public sealed class WriteBodyValidationTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_setting_description_longer_than_its_column_is_a_field_error()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var key = "test.sec06." + Letters(8);

        using (var tooLong = await admin.SendRawAsync(HttpMethod.Put, $"/api/v1/admin/settings/{key}",
                   new SaveSettingRequest(JsonValue.Create(true), null, false, new string('d', 301))))
        {
            await ShouldNameFieldAsync(tooLong, "description");
        }

        (await admin.SettingVersionAsync(key)).ShouldBeNull("Nothing was saved.");
        await admin.SaveSettingAsync(key, new SaveSettingRequest(JsonValue.Create(true), null, false, new string('d', 300)));
        (await admin.SettingVersionAsync(key)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_room_longer_than_the_board_shows_is_a_field_error()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var token = await WaitingTokenAsync(frontOffice, doctor);

        using (var tooLong = await doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/queue/{token}/call", new CallTokenRequest(new string('r', 33))))
        {
            await ShouldNameFieldAsync(tooLong, "room");
        }

        (await doctor.PostAsync<QueueTokenDto>($"/api/v1/queue/{token}/call", new CallTokenRequest("Consultation room 4"))).Room
            .ShouldBe("Consultation room 4");
    }

    [Fact]
    public async Task A_device_name_longer_than_its_column_is_a_field_error()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var device = (await admin.GetAsync<List<DeviceDto>>("/api/v1/admin/devices/")).Single(d => d.Id == server.Desktop.Id);
        var path = $"/api/v1/admin/devices/{device.Id}";

        using (var tooLong = await admin.SendRawAsync(HttpMethod.Put, path, new UpdateDeviceRequest(new string('n', 129), device.DepartmentId, device.Location, device.Version)))
        {
            await ShouldNameFieldAsync(tooLong, "name");
        }

        using (var noVersion = await admin.SendRawAsync(HttpMethod.Put, path, new UpdateDeviceRequest(device.Name, device.DepartmentId, device.Location, 0)))
        {
            await ShouldNameFieldAsync(noVersion, "version");
        }

        (await admin.GetAsync<List<DeviceDto>>("/api/v1/admin/devices/")).Single(d => d.Id == device.Id).Name.ShouldBe(device.Name);
    }

    [Fact]
    public async Task A_mistyped_portal_code_is_answered_before_the_proof_is_asked_for()
    {
        // No proof of work is sent: a code that cannot be right is answered by what is wrong with it, without the check
        // (which would spend a proof) and without counting as a failed try for the number.
        var portal = server.AnonymousWithoutProof();

        using var response = await portal.SendRawAsync(HttpMethod.Post, "/api/v1/auth/otp/verify", new OtpVerifyRequest(Guid.NewGuid(), "12345"));

        await ShouldNameFieldAsync(response, "code");
    }

    private static async Task ShouldNameFieldAsync(HttpResponseMessage response, string field)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        JsonDocument.Parse(body).RootElement.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue(body);
    }

    internal static async Task<Guid> WaitingTokenAsync(ApiClient frontOffice, ApiClient doctor)
    {
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: "Meena", MiddleName: null, LastName: "Sv" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1975, 1, 1).AddDays(Random.Shared.Next(7300)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true, WhatsAppOptIn: false));
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var departmentId = (await frontOffice.GetAsync<JsonElement>($"/api/v1/masters/practitioners/{practitionerId}")).GetProperty("departmentId").GetGuid();
        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            created.Id, VisitType.OutPatient, departmentId, practitionerId, null, PayerType.Self, null, ReferralSource.Self, null, "Fever since yesterday",
            false, null, null, TokenPriority.Normal, IssueToken: true, NotifyPatient: false));
        return visit.Token.ShouldNotBeNull("The visit issues a queue token.").Id;
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));
}
