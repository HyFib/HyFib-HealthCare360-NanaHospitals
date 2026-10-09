using System.Net;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The backup recovery key over the real API (ASVS V11.1.1, V13.3.2): no screen shows the current key. A new one is shown
/// once, to an administrator who gives their password again, and backups use it only once that administrator types it back.
/// </summary>
public sealed class BackupRecoveryKeyTests(HospitalServer server)
{
    private const string KeyPath = "/api/v1/admin/backups/recovery-key";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_recovery_key_is_shown_once_and_used_once_it_is_typed_back()
    {
        // From an address of its own: asking for the password shares sign-in's limit per address.
        var (admin, _) = await server.SignInNewSessionAsync(HospitalServer.Users.Admin);

        // A new server backs up with the original key, which nobody has seen: System health says the backups cannot be
        // restored on new hardware.
        var before = await admin.GetAsync<RecoveryKeyStatus>(KeyPath);
        (before.Original, before.OriginalShown).ShouldBe((true, false));
        (await WarningsAsync(admin)).ShouldContain("backup.recovery_key_missing");

        using (var wrongPassword = await admin.SendRawAsync(HttpMethod.Post, KeyPath, new ReauthenticateRequest("not-the-password")))
        {
            wrongPassword.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var created = await admin.PostAsync<NewRecoveryKey>(KeyPath, new ReauthenticateRequest(server.Password));

        created.Key.Split('-').Length.ShouldBe(14);
        (await admin.GetAsync<RecoveryKeyStatus>(KeyPath)).KeyId.ShouldBe(before.KeyId);
        using (var mistyped = await admin.SendRawAsync(HttpMethod.Post, $"{KeyPath}/{created.KeyId}/confirm",
                   new ConfirmRecoveryKeyRequest(BackupRecoveryKey.Format(BackupRecoveryKey.Create()))))
        {
            mistyped.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var after = await admin.PostAsync<RecoveryKeyStatus>($"{KeyPath}/{created.KeyId}/confirm", new ConfirmRecoveryKeyRequest(created.Key));

        (after.KeyId, after.Original, after.By, after.OriginalKeyId).ShouldBe((created.KeyId, false, HospitalServer.Users.Admin, before.KeyId));
        (await WarningsAsync(admin)).ShouldNotContain(code => code.StartsWith("backup.recovery_key", StringComparison.Ordinal));
        using (var again = await admin.SendRawAsync(HttpMethod.Post, $"{KeyPath}/{created.KeyId}/confirm", new ConfirmRecoveryKeyRequest(created.Key)))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        // Showing it and putting it to use are both in the audit log, and neither entry holds the key.
        await using var scope = server.Services.CreateAsyncScope();
        var events = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.Action == "backup.recovery_key_shown" || e.Action == "backup.recovery_key_replaced")
            .OrderBy(e => e.Id)
            .ToListAsync(Ct);
        events.Select(e => (e.Action, e.Severity, e.UserName)).ShouldBe([
            ("backup.recovery_key_shown", AuditSeverity.Critical, HospitalServer.Users.Admin),
            ("backup.recovery_key_replaced", AuditSeverity.Critical, HospitalServer.Users.Admin),
        ]);
        events.ShouldAllBe(e => !(e.Summary ?? string.Empty).Contains(created.Key) && !(e.Details ?? string.Empty).Contains(created.Key));
    }

    [Fact]
    public async Task The_hospital_administrator_neither_sees_nor_makes_recovery_keys()
    {
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);

        using var status = await hospitalAdmin.SendRawAsync(HttpMethod.Get, KeyPath);
        using var create = await hospitalAdmin.SendRawAsync(HttpMethod.Post, KeyPath, new ReauthenticateRequest(server.Password));

        status.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        create.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static async Task<IReadOnlyList<string>> WarningsAsync(ApiClient admin) =>
        (await admin.GetAsync<HealthWarnings>("/api/v1/admin/system/health")).Warnings.Select(w => w.Code).ToList();

    private sealed record HealthWarnings(IReadOnlyList<HealthWarning> Warnings);
}
