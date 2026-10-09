using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SRS SEC-07 over the real API: an administrator rotates the data encryption key and the sign-in token key from System
/// health. Identifiers, secret settings and documents encrypted before move to the new key and stay readable, ABHA
/// search keeps working, and nobody is signed out.
/// </summary>
public sealed class KeyRotationTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Data_encrypted_before_a_rotation_moves_to_the_new_key_and_stays_readable()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var abha = "91" + Digits(12);
        var patient = await RegisterPatientAsync(abha);
        var settingKey = "it.rotation." + Digits(6) + ".secret";
        await admin.PutAsync($"/api/v1/admin/settings/{settingKey}", new SaveSettingRequest(JsonNode.Parse("""{"apiKey":"k-123"}"""), null, true, null));
        var png = TinyPng;
        var fileId = await UploadAsync(admin, png);
        var before = await admin.GetAsync<EncryptionKeysStatus>("/api/v1/admin/system/keys");

        var rotated = await admin.PostAsync<EncryptionKeysStatus>("/api/v1/admin/system/keys/data/rotate");

        rotated.DataKey.KeyId.ShouldNotBe(before.DataKey.KeyId);
        rotated.DataKey.Keys.ShouldBe(before.DataKey.Keys + 1);
        rotated.DataKey.LastRotation.ShouldNotBeNull().By.ShouldBe(HospitalServer.Users.Admin);
        rotated.DataKey.ValuesToMove.ShouldBeGreaterThanOrEqualTo(2, "the patient's ABHA and the secret setting");
        rotated.DataKey.DocumentsToMove.ShouldBeGreaterThanOrEqualTo(1);

        // Everything is readable before it moves...
        (await admin.GetAsync<PagedResult<PatientListItem>>($"/api/v1/patients?search={abha}")).Items.ShouldContain(p => p.Id == patient.Id);
        (await DownloadAsync(admin, fileId)).ShouldBe(png);

        // ...and after. The background mover may be at work too: a pass can find the documents locked by it, so keep going
        // until nothing is left.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var mover = scope.ServiceProvider.GetRequiredService<FieldKeyMover>();
            for (var pass = 0; pass < 40 && !(await mover.CountAsync(Ct)).IsEmpty; pass++)
            {
                await mover.MoveAsync(Ct);
                await Task.Delay(250, Ct);
            }
        }

        var moved = await admin.GetAsync<EncryptionKeysStatus>("/api/v1/admin/system/keys");
        (moved.DataKey.ValuesToMove, moved.DataKey.DocumentsToMove).ShouldBe((0, 0));
        var prefix = $"v2.{rotated.DataKey.KeyId}.";
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.Set<Patient>().Where(p => p.Id == patient.Id).Select(p => p.AbhaNumberCipher).SingleAsync(Ct)).ShouldNotBeNull().ShouldStartWith(prefix);
            JsonSerializer.Deserialize<string>(await db.Set<SystemSetting>().Where(s => s.Key == settingKey).Select(s => s.Value).SingleAsync(Ct)).ShouldNotBeNull().ShouldStartWith(prefix);
            (await db.Set<StoredFile>().Where(f => f.Id == fileId).Select(f => f.EncryptionKeyId).SingleAsync(Ct)).ShouldBe(rotated.DataKey.KeyId);
            (await scope.ServiceProvider.GetRequiredService<ISettingsService>().GetAsync<JsonNode>(settingKey, null, Ct))!["apiKey"]!.GetValue<string>().ShouldBe("k-123");
        }

        (await admin.GetAsync<PagedResult<PatientListItem>>($"/api/v1/patients?search={abha}")).Items.ShouldContain(p => p.Id == patient.Id);
        (await DownloadAsync(admin, fileId)).ShouldBe(png);
    }

    [Fact]
    public async Task After_the_sign_in_key_is_rotated_tokens_already_issued_keep_working()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var nurse = server.Anonymous(ownAddress: true);
        var first = await nurse.PostAsync<AuthResult>("/api/v1/auth/login", new LoginRequest(HospitalServer.Users.Nurse, server.Password));

        var status = await admin.PostAsync<EncryptionKeysStatus>("/api/v1/admin/system/keys/signing/rotate");

        status.SigningKey.KeyId.ShouldNotBe(KeyIdOf(first.AccessToken!));
        status.SigningKey.LastRotation.ShouldNotBeNull();
        nurse.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first.AccessToken);
        (await nurse.GetAsync<AuthUser>("/api/v1/auth/me")).UserName.ShouldBe(HospitalServer.Users.Nurse);

        // New tokens are signed with the new key.
        using var again = server.Anonymous(ownAddress: true).Http;
        using var response = await again.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(HospitalServer.Users.Nurse, server.Password), ApiClient.Json, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = (await response.Content.ReadFromJsonAsync<AuthResult>(ApiClient.Json, Ct))!;
        KeyIdOf(second.AccessToken!).ShouldBe(status.SigningKey.KeyId);
    }

    [Fact]
    public async Task Only_an_administrator_who_manages_the_system_may_rotate_keys()
    {
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);

        using var data = await hospitalAdmin.SendRawAsync(HttpMethod.Post, "/api/v1/admin/system/keys/data/rotate");
        using var signing = await hospitalAdmin.SendRawAsync(HttpMethod.Post, "/api/v1/admin/system/keys/signing/rotate");

        data.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        signing.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public void Every_encrypted_column_moves_with_a_rotation()
    {
        using var scope = server.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Model;

        var encrypted = model.GetEntityTypes()
            .SelectMany(e => e.GetProperties().Where(p => p.ClrType == typeof(string) && (p.Name.EndsWith("Cipher", StringComparison.Ordinal) || p.Name == "MfaSecret")))
            .ToList();

        encrypted.Count.ShouldBeGreaterThanOrEqualTo(7);
        encrypted.Where(p => p.FindAnnotation(ProtectedValueBuilderExtensions.Annotation)?.Value is not true)
            .Select(p => $"{p.DeclaringType.DisplayName()}.{p.Name}").ShouldBeEmpty("mark the column IsProtectedValue() in its configuration");
    }

    private async Task<PatientCreated> RegisterPatientAsync(string abha)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        return await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: "Rotan", MiddleName: null, LastName: "Keys" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: abha, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
    }

    private static async Task<Guid> UploadAsync(ApiClient client, byte[] png)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "rotation.png");
        form.Add(new StringContent("document"), "category");
        using var response = await client.Http.PostAsync("/api/v1/files", form, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<Created>(ApiClient.Json, Ct))!.Id;
    }

    private static async Task<byte[]> DownloadAsync(ApiClient client, Guid id)
    {
        using var response = await client.SendRawAsync(HttpMethod.Get, $"/api/v1/files/{id}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadAsByteArrayAsync(Ct);
    }

    private static string KeyIdOf(string jwt) =>
        JsonDocument.Parse(Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(jwt.Split('.')[0]))).RootElement.GetProperty("kid").GetString()!;

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    // A 1×1 transparent PNG.
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private sealed record Created(Guid Id);
}
