using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
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
/// health. Identifiers, secret settings, documents and scheduled report files encrypted before move to the new key and
/// stay readable, ABHA search keeps working, and nobody is signed out. An earlier data key is retired once nothing is
/// encrypted with it any more (ASVS V11.1.1).
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
        await admin.SaveSettingAsync(settingKey, new SaveSettingRequest(JsonNode.Parse("""{"apiKey":"k-123"}"""), null, true, null));
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

        // ...and after.
        await MoveEverythingAsync();
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
    public async Task Search_indexes_move_to_the_new_key_and_the_Aadhaar_index_stays_on_the_original()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var abha = "91" + Digits(12);
        var aadhaar = Aadhaar();
        var patient = await RegisterPatientAsync(abha, aadhaar: aadhaar);
        var before = await SearchIndexesAsync(patient.Id);

        var rotated = await admin.PostAsync<EncryptionKeysStatus>("/api/v1/admin/system/keys/data/rotate");

        // For a minute the indexes stay as they are: a search that took the keys just before the rotation still looks for
        // the earlier index. The patient is found by either number meanwhile...
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<FieldKeyMover>().MoveAsync(Ct);
        }

        (await SearchIndexesAsync(patient.Id)).Abha.ShouldBe(before.Abha);
        (await admin.GetAsync<PagedResult<PatientListItem>>($"/api/v1/patients?search={abha}")).Items.ShouldContain(p => p.Id == patient.Id);
        (await admin.GetAsync<PagedResult<PatientListItem>>($"/api/v1/patients?search={aadhaar}")).Items.ShouldContain(p => p.Id == patient.Id);
        await MoveEverythingAsync();

        // ...and after. The ABHA index is now made with the new key, which it names. The Aadhaar index stays on the original
        // key: only the last four digits are kept, so it cannot be computed again.
        var after = await SearchIndexesAsync(patient.Id);
        after.Abha.ShouldStartWith(rotated.DataKey.KeyId + ".");
        after.Abha.ShouldNotBe(before.Abha);
        after.Aadhaar.ShouldBe(before.Aadhaar);
        after.Aadhaar.ShouldMatch("^[0-9A-F]{64}$");
        (await admin.GetAsync<PagedResult<PatientListItem>>($"/api/v1/patients?search={abha}")).Items.ShouldContain(p => p.Id == patient.Id);
        (await admin.GetAsync<PagedResult<PatientListItem>>($"/api/v1/patients?search={aadhaar}")).Items.ShouldContain(p => p.Id == patient.Id);

        // The same number is still recognised when someone registers it again.
        var ex = await Should.ThrowAsync<HttpRequestException>(() => RegisterPatientAsync(abha));
        (ex.StatusCode, ex.Message.Contains("patient.abha_in_use", StringComparison.Ordinal)).ShouldBe((HttpStatusCode.Conflict, true));
    }

    [Fact]
    public async Task A_scheduled_report_file_moves_to_the_new_key_and_still_opens()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var content = Encoding.UTF8.GetBytes("Ward,Census\nGeneral,42\n");
        Guid deliveryId;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var delivery = new ReportDelivery
            {
                FacilityId = Guid.NewGuid(),
                ScheduleId = Guid.NewGuid(),
                ReportId = "ward-census",
                ReportTitle = "Ward census",
                CreatedAt = DateTimeOffset.UtcNow,
                FileName = "ward-census.csv",
                ContentType = "text/csv",
                Content = scope.ServiceProvider.GetRequiredService<IFieldProtector>().ProtectBytes(content, "analytics.delivery"),
            };
            db.Add(delivery);
            await db.SaveChangesAsync(Ct);
            deliveryId = delivery.Id;
        }

        var rotated = await admin.PostAsync<EncryptionKeysStatus>("/api/v1/admin/system/keys/data/rotate");
        await MoveEverythingAsync();

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<ReportDelivery>().IgnoreQueryFilters()
                .Where(d => d.Id == deliveryId).Select(d => d.Content).SingleAsync(Ct);
            var ring = scope.ServiceProvider.GetRequiredService<IFieldKeyRing>();
            ring.KeyIdOf(stored.ShouldNotBeNull()).ShouldBe(rotated.DataKey.KeyId);
            scope.ServiceProvider.GetRequiredService<IFieldProtector>().UnprotectBytes(stored, "analytics.delivery").ShouldBe(content);
        }
    }

    [Fact]
    public async Task An_earlier_data_key_is_retired_once_nothing_is_encrypted_with_it()
    {
        // From the test's own address: retiring a key asks for the password, so it is rate-limited like signing in.
        var (admin, _) = await server.SignInNewSessionAsync(HospitalServer.Users.Admin);
        var earlier = (await admin.PostAsync<EncryptionKeysStatus>("/api/v1/admin/system/keys/data/rotate")).DataKey.KeyId;
        var abha = "91" + Digits(12);

        // Registered while that key is current, and the answer is kept a day for a retry of the same request. The answer
        // is not moved by a rotation: it expires.
        var patient = await RegisterPatientAsync(abha, idempotencyKey: Guid.NewGuid().ToString());
        var rotated = await admin.PostAsync<EncryptionKeysStatus>("/api/v1/admin/system/keys/data/rotate");
        var current = rotated.DataKey.KeyId;
        var entry = rotated.DataKey.Earlier.Single(k => k.KeyId == earlier);
        entry.Expiring.ShouldBeGreaterThanOrEqualTo(1);
        entry.CanRetire.ShouldBeFalse();
        await RefusedAsync(admin, earlier, "keys.in_use", "stored answer");

        // Everything else moves, and the kept answer expires.
        await MoveEverythingAsync();
        await ExpireStoredAnswersAsync(earlier);

        // The key changed a moment ago: whatever was still being encrypted with the earlier one gets a day to be written.
        var waiting = (await admin.GetAsync<EncryptionKeysStatus>("/api/v1/admin/system/keys")).DataKey.Earlier.Single(k => k.KeyId == earlier);
        (waiting.Values, waiting.Documents, waiting.Expiring, waiting.CanRetire).ShouldBe((0, 0, 0, false));
        waiting.RetireFrom.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddHours(23));
        await RefusedAsync(admin, earlier, "keys.rotated_recently", "less than a day ago");
        var directories = server.Services.GetRequiredService<DataDirectories>();
        File.SetLastWriteTimeUtc(Path.Combine(FieldKeyRing.DirectoryIn(directories), FieldKeyRing.CurrentFileName), DateTime.UtcNow.AddDays(-2));

        // The password is asked again.
        using (var wrong = await admin.SendRawAsync(HttpMethod.Post, $"/api/v1/admin/system/keys/data/{earlier}/retire", new ReauthenticateRequest("not-the-password")))
        {
            wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var retired = await admin.PostAsync<EncryptionKeysStatus>($"/api/v1/admin/system/keys/data/{earlier}/retire", new ReauthenticateRequest(server.Password));

        retired.DataKey.KeyId.ShouldBe(current);
        retired.DataKey.Earlier.ShouldNotContain(k => k.KeyId == earlier);
        retired.DataKey.Keys.ShouldBe(rotated.DataKey.Keys - 1);
        File.Exists(Path.Combine(FieldKeyRing.DirectoryIn(directories), earlier + ".key")).ShouldBeFalse();

        // What was encrypted with it moved, so it is still readable and searchable.
        (await admin.GetAsync<PagedResult<PatientListItem>>($"/api/v1/patients?search={abha}")).Items.ShouldContain(p => p.Id == patient.Id);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var cipher = await db.Set<Patient>().Where(p => p.Id == patient.Id).Select(p => p.AbhaNumberCipher).SingleAsync(Ct);
            scope.ServiceProvider.GetRequiredService<IFieldProtector>().Unprotect(cipher).ShouldBe(abha);
            var audit = await db.Set<AuditEvent>().AsNoTracking().Where(e => e.Action == "security.data_key_retired").OrderByDescending(e => e.Id).FirstAsync(Ct);
            (audit.Severity, audit.UserName).ShouldBe((AuditSeverity.Warning, HospitalServer.Users.Admin));
            audit.Summary.ShouldNotBeNull().ShouldContain(earlier);
        }

        // The original key and the current one are never retired.
        await RefusedAsync(admin, server.Services.GetRequiredService<IFieldKeyRing>().OriginalKeyId, "keys.original_kept", "never retired");
        await RefusedAsync(admin, current, "keys.current_key", "encrypts new data");
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

        // Retiring asks for the password, so it is rate-limited like signing in: from the test's own address.
        var (ownAddress, _) = await server.SignInNewSessionAsync(HospitalServer.Users.HospitalAdmin);
        using var retire = await ownAddress.SendRawAsync(HttpMethod.Post, "/api/v1/admin/system/keys/data/0123456789ABCDEF/retire",
            new ReauthenticateRequest(server.Password));

        data.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        signing.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        retire.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
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

        // Blind indexes move too, computed again from the encrypted value beside them. An Aadhaar index cannot be (only the
        // last four digits are kept), so it stays on the original key.
        model.GetEntityTypes()
            .SelectMany(e => e.GetProperties().Where(p => p.FindAnnotation(ProtectedValueBuilderExtensions.BlindIndexAnnotation) is not null))
            .Select(p => $"{p.DeclaringType.DisplayName()}.{p.Name}={p.FindAnnotation(ProtectedValueBuilderExtensions.BlindIndexAnnotation)!.Value}")
            .ShouldBe([
                "Patient.AbhaNumberIndex=AbhaNumberCipher",
                "PatientPolicy.PolicyNumberIndex=PolicyNumberCipher",
                "Employee.PanIndex=PanCipher",
                "Employee.BankAccountIndex=BankAccountCipher",
            ], ignoreOrder: true);

        // Encrypted files kept in the database: moved (report files), or expiring (stored answers), so a key that still
        // encrypts one is not retired.
        model.GetEntityTypes()
            .SelectMany(e => e.GetProperties().Where(p => p.FindAnnotation(ProtectedValueBuilderExtensions.BytesAnnotation) is not null))
            .Select(p => $"{p.DeclaringType.DisplayName()}.{p.Name}={p.FindAnnotation(ProtectedValueBuilderExtensions.BytesAnnotation)!.Value}")
            .ShouldBe(["IdempotencyRecord.ResponseBody=", "ReportDelivery.Content=analytics.delivery"], ignoreOrder: true);
    }

    // The background mover may be at work too: a pass can find the documents locked by it, so keep going until nothing is
    // left. Blind indexes move once the rotation is a minute old, so the rotation is made older first.
    private async Task MoveEverythingAsync()
    {
        var current = Path.Combine(FieldKeyRing.DirectoryIn(server.Services.GetRequiredService<DataDirectories>()), FieldKeyRing.CurrentFileName);
        if (File.Exists(current) && File.GetLastWriteTimeUtc(current) > DateTime.UtcNow - FieldKeyMover.IndexesSettleAfter)
        {
            File.SetLastWriteTimeUtc(current, DateTime.UtcNow - FieldKeyMover.IndexesSettleAfter - TimeSpan.FromMinutes(1));
        }

        await using var scope = server.Services.CreateAsyncScope();
        var mover = scope.ServiceProvider.GetRequiredService<FieldKeyMover>();
        for (var pass = 0; pass < 40 && !(await mover.CountAsync(Ct)).IsEmpty; pass++)
        {
            await mover.MoveAsync(Ct);
            await Task.Delay(250, Ct);
        }

        (await mover.CountAsync(Ct)).IsEmpty.ShouldBeTrue();
    }

    // Stored answers to repeated requests are kept a day; these, encrypted with the key, are removed as if they had expired.
    private async Task ExpireStoredAnswersAsync(string keyId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        byte[] header = [2, .. Convert.FromHexString(keyId)];
        var records = await db.Set<IdempotencyRecord>().Where(r => r.ResponseBody != null).ToListAsync(Ct);
        db.RemoveRange(records.Where(r => r.ResponseBody!.AsSpan().StartsWith(header)));
        await db.SaveChangesAsync(Ct);
    }

    private async Task RefusedAsync(ApiClient admin, string keyId, string code, string message)
    {
        using var response = await admin.SendRawAsync(HttpMethod.Post, $"/api/v1/admin/system/keys/data/{keyId}/retire", new ReauthenticateRequest(server.Password));
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await response.Content.ReadAsStringAsync(Ct);
        problem.ShouldContain($"\"code\":\"{code}\"");
        problem.ShouldContain(message);
    }

    private async Task<(string Abha, string Aadhaar)> SearchIndexesAsync(Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var indexes = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Patient>()
            .Where(p => p.Id == patientId).Select(p => new { p.AbhaNumberIndex, p.AadhaarIndex }).SingleAsync(Ct);
        return (indexes.AbhaNumberIndex.ShouldNotBeNull(), indexes.AadhaarIndex.ShouldNotBeNull());
    }

    private async Task<PatientCreated> RegisterPatientAsync(string abha, string? idempotencyKey = null, string? aadhaar = null)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var request = new RegisterPatientRequest(
            Title: "Mr.", FirstName: "Rotan", MiddleName: null, LastName: "Keys" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: abha, AbhaAddress: null, AadhaarNumber: aadhaar, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true);
        if (idempotencyKey is null)
        {
            return await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", request);
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/patients") { Content = JsonContent.Create(request, options: ApiClient.Json) };
        message.Headers.Add(IdempotencyMiddleware.HeaderName, idempotencyKey);
        using var response = await frontOffice.Http.SendAsync(message, Ct);
        return await ApiClient.ReadAsync<PatientCreated>(response, HttpMethod.Post, "/api/v1/patients");
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

    // A valid Aadhaar number: 12 digits, the first 2 to 9, and a Verhoeff check digit last.
    private static string Aadhaar()
    {
        int[,] d =
        {
            { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, { 1, 2, 3, 4, 0, 6, 7, 8, 9, 5 }, { 2, 3, 4, 0, 1, 7, 8, 9, 5, 6 },
            { 3, 4, 0, 1, 2, 8, 9, 5, 6, 7 }, { 4, 0, 1, 2, 3, 9, 5, 6, 7, 8 }, { 5, 9, 8, 7, 6, 0, 4, 3, 2, 1 },
            { 6, 5, 9, 8, 7, 1, 0, 4, 3, 2 }, { 7, 6, 5, 9, 8, 2, 1, 0, 4, 3 }, { 8, 7, 6, 5, 9, 3, 2, 1, 0, 4 },
            { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 },
        };
        int[,] p =
        {
            { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, { 1, 5, 7, 6, 2, 8, 3, 0, 9, 4 }, { 5, 8, 0, 3, 7, 9, 6, 1, 4, 2 },
            { 8, 9, 1, 6, 0, 4, 3, 5, 2, 7 }, { 9, 4, 5, 3, 1, 2, 6, 8, 7, 0 }, { 4, 2, 8, 6, 5, 7, 3, 9, 0, 1 },
            { 2, 7, 9, 3, 8, 0, 6, 4, 1, 5 }, { 7, 0, 4, 6, 9, 1, 3, 2, 5, 8 },
        };
        int[] inverse = [0, 4, 3, 2, 1, 5, 6, 7, 8, 9];
        var digits = (char)('2' + Random.Shared.Next(8)) + Digits(10);
        var check = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            check = d[check, p[(i + 1) % 8, digits[^(i + 1)] - '0']];
        }

        return digits + (char)('0' + inverse[check]);
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    // A 1×1 transparent PNG.
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private sealed record Created(Guid Id);
}
