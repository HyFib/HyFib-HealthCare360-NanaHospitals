using FluentValidation;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Identity;

public sealed record CreateEnrollmentCodeRequest(DeviceKind Kind, Guid? FacilityId, string? Note, int? ValidMinutes);

public sealed class CreateEnrollmentCodeRequestValidator : AbstractValidator<CreateEnrollmentCodeRequest>
{
    public CreateEnrollmentCodeRequestValidator()
    {
        RuleFor(r => r.Kind).IsInEnum().WithMessage("Choose what kind of device this is.");
        RuleFor(r => r.FacilityId).OptionalId();
        RuleFor(r => r.Note).SafeText(256);
        RuleFor(r => r.ValidMinutes).InclusiveBetween(DeviceEnrollment.MinValidMinutes, DeviceEnrollment.MaxValidMinutes)
            .WithMessage($"A code is valid for {DeviceEnrollment.MinValidMinutes} minutes to {DeviceEnrollment.MaxValidMinutes / 60} hours.");
    }
}

public sealed record EnrollmentCodeResponse(string Code, DateTimeOffset ExpiresAt);

public sealed record EnrollDeviceRequest(string Code, string Name, string? MachineName, string? OsVersion, string? AppVersion);

/// <summary>What HC360 Desktop sends when it enrols, held to the columns it is kept in.</summary>
public sealed class EnrollDeviceRequestValidator : AbstractValidator<EnrollDeviceRequest>
{
    public EnrollDeviceRequestValidator()
    {
        RuleFor(r => r.Code).NotEmpty().WithMessage("Enter the enrolment code.").MaximumLength(32);
        RuleFor(r => r.Name).NotEmpty().WithMessage(DeviceEnrollment.NameMessage).SafeText(128).WithMessage(DeviceEnrollment.NameMessage);
        RuleFor(r => r.MachineName).SafeText(128);
        RuleFor(r => r.OsVersion).SafeText(128);
        RuleFor(r => r.AppVersion).SafeText(32);
    }
}

public sealed record EnrollDeviceResponse(Guid DeviceId, string DeviceSecret, string ServerName, string? CaFingerprint);

public sealed record DeviceHeartbeatRequest(Guid DeviceId, string DeviceSecret, string? AppVersion, string? WifiSsid, int? WifiSignal);

/// <summary>
/// The device's id and secret, and what it reports: Windows gives the WiFi signal as 0–100. A network's name (SSID) may
/// be any 32 bytes, so it is held to its length only.
/// </summary>
public sealed class DeviceHeartbeatRequestValidator : AbstractValidator<DeviceHeartbeatRequest>
{
    public DeviceHeartbeatRequestValidator()
    {
        RuleFor(r => r.DeviceId).NotEmpty();
        RuleFor(r => r.DeviceSecret).NotEmpty().MaximumLength(IdentityRules.MaxDeviceSecretLength);
        RuleFor(r => r.AppVersion).SafeText(32);
        RuleFor(r => r.WifiSsid).MaximumLength(64);
        RuleFor(r => r.WifiSignal).InclusiveBetween(0, 100);
    }
}

public sealed record DeviceDto(
    Guid Id,
    string Name,
    string? MachineName,
    DeviceKind Kind,
    string? OsVersion,
    string? AppVersion,
    DateTimeOffset EnrolledAt,
    DateTimeOffset? LastSeenAt,
    string? LastIpAddress,
    string? LastWifiSsid,
    int? LastWifiSignal,
    bool IsRevoked,
    Guid? FacilityId,
    Guid? DepartmentId,
    string? Location,
    uint Version);

public sealed record UpdateDeviceRequest(string Name, Guid? DepartmentId, string? Location, uint Version);

public sealed class UpdateDeviceRequestValidator : AbstractValidator<UpdateDeviceRequest>
{
    public UpdateDeviceRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().WithMessage("Name is required.").SafeText(128);
        RuleFor(r => r.DepartmentId).OptionalId();
        RuleFor(r => r.Location).SafeText(128);
        RuleFor(r => r.Version).SeenVersion();
    }
}

/// <summary>The limits on enrolment codes and device names.</summary>
public static class DeviceEnrollment
{
    public const int MinValidMinutes = 5;
    public const int MaxValidMinutes = 24 * 60;
    public const string NameMessage = "Enter a device name (up to 128 characters).";
}

/// <summary>
/// Workstation enrolment (DED-05, SEC-08): an administrator issues a short-lived one-time code; HC360 Desktop
/// exchanges it for a device secret (stored with DPAPI on the workstation) and pins the hospital CA.
/// Heartbeats report each workstation's WiFi SSID/signal so IT can see weak spots on the floor.
/// </summary>
internal static class DeviceEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // Administration works only on an enrolled desktop (SEC-08); a browser enrols the first one from this page.
        var admin = app.MapGroup("/api/v1/admin/devices").WithTags("Administration");
        admin.MapGet("/", ListDevices).RequirePermission(PlatformPermissions.DevicesManage).AllowedForFirstDesktopEnrolment();
        admin.MapPost("/enrollment-codes", CreateEnrollmentCode).RequirePermission(PlatformPermissions.DevicesManage).AllowedForFirstDesktopEnrolment()
            .WithValidation<CreateEnrollmentCodeRequest>();
        admin.MapPut("/{id:guid}", UpdateDevice).RequirePermission(PlatformPermissions.DevicesManage).WithValidation<UpdateDeviceRequest>();
        admin.MapPost("/{id:guid}/revoke", RevokeDevice).RequirePermission(PlatformPermissions.DevicesManage);

        var devices = app.MapGroup("/api/v1/devices").WithTags("Devices");
        devices.MapPost("/enroll", Enroll).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Public).WithValidation<EnrollDeviceRequest>();
        devices.MapPost("/heartbeat", Heartbeat).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Public).WithValidation<DeviceHeartbeatRequest>();
    }

    private static async Task<IResult> ListDevices(HospitalDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Set<Device>()
            .OrderBy(d => d.IsRevoked).ThenBy(d => d.Name)
            .Select(d => new DeviceDto(d.Id, d.Name, d.MachineName, d.Kind, d.OsVersion, d.AppVersion, d.EnrolledAt, d.LastSeenAt, d.LastIpAddress,
                d.LastWifiSsid, d.LastWifiSignal, d.IsRevoked, d.FacilityId, d.DepartmentId, d.Location, d.Version))
            .ToListAsync(ct));

    private static async Task<IResult> CreateEnrollmentCode(CreateEnrollmentCodeRequest request, HospitalDbContext db, ICurrentUser current, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var minutes = request.ValidMinutes ?? 30;
        var code = SecretTokens.NewReadableCode(8);
        db.Add(new DeviceEnrollmentCode
        {
            CodeHash = SecretTokens.Hash(code),
            Kind = request.Kind,
            FacilityId = request.FacilityId ?? current.FacilityId,
            Note = request.Note?.Trim(),
            CreatedBy = current.UserId ?? Guid.Empty,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(minutes),
        });
        audit.Add(new AuditEntry(AuditCategories.Security, "device.enrollment_code_created", $"Enrolment code for a {request.Kind} (valid {minutes} min)"));
        await db.SaveChangesAsync(ct);

        // Shown once, formatted for reading aloud: ABCD-EFGH.
        return TypedResults.Ok(new EnrollmentCodeResponse($"{code[..4]}-{code[4..]}", now.AddMinutes(minutes)));
    }

    private static async Task<IResult> Enroll(
        EnrollDeviceRequest request,
        HospitalDbContext db,
        IAuditLogger audit,
        LanCertificateManager certificates,
        IOptions<PlatformOptions> options,
        HttpContext http,
        TimeProvider time,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var normalizedCode = new string((request.Code ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        var hash = SecretTokens.Hash(normalizedCode);

        // Enrolments sent at once with the same code take turns under its row lock, so a code enrols one device only.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM identity.device_enrollment_codes WHERE code_hash = {hash} FOR NO KEY UPDATE", ct);
        var enrollment = await db.Set<DeviceEnrollmentCode>().FirstOrDefaultAsync(c => c.CodeHash == hash, ct);
        if (enrollment is null || enrollment.UsedAt is not null || enrollment.ExpiresAt <= now)
        {
            await audit.WriteAsync(new AuditEntry(AuditCategories.Security, "device.enrollment_failed", "Invalid or expired enrolment code") { Severity = AuditSeverity.Warning }, ct);
            return Error.Rule("device.invalid_code", "The enrolment code is invalid or has expired. Ask your administrator for a new code.").ToProblem();
        }

        var secret = SecretTokens.NewOpaqueToken(32);
        var device = new Device
        {
            Name = request.Name.Trim(),
            MachineName = request.MachineName?.Trim(),
            Kind = enrollment.Kind,
            OsVersion = request.OsVersion?.Trim(),
            AppVersion = request.AppVersion?.Trim(),
            SecretHash = SecretTokens.Hash(secret),
            EnrolledAt = now,
            LastSeenAt = now,
            LastIpAddress = http.Connection.RemoteIpAddress?.ToString(),
            FacilityId = enrollment.FacilityId,
        };
        enrollment.UsedAt = now;
        enrollment.UsedByDeviceId = device.Id;
        db.Add(device);
        audit.Add(new AuditEntry(AuditCategories.Security, "device.enrolled", $"Enrolled {device.Kind} '{device.Name}'")
        {
            EntityType = nameof(Device),
            EntityId = device.Id.ToString(),
            UserId = enrollment.CreatedBy,
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return TypedResults.Ok(new EnrollDeviceResponse(device.Id, secret, Environment.MachineName, certificates.CaFingerprint));
    }

    private static async Task<IResult> Heartbeat(DeviceHeartbeatRequest request, HospitalDbContext db, HttpContext http, TimeProvider time, CancellationToken ct)
    {
        var device = await db.Set<Device>().FirstOrDefaultAsync(d => d.Id == request.DeviceId, ct);
        // The desktop heartbeat is for HC360 Desktop only; lab agents call their own (LabAgentProtocol.HeartbeatPath).
        if (device is null || device.IsRevoked || !device.Kind.IsDesktop()
            || !SecretTokens.FixedTimeEquals(
                device.SecretHash, SecretTokens.Hash(request.DeviceSecret ?? string.Empty)))
        {
            return TypedResults.Problem(title: "Device not recognised", statusCode: StatusCodes.Status401Unauthorized);
        }

        device.LastSeenAt = time.GetUtcNow();
        device.LastIpAddress = http.Connection.RemoteIpAddress?.ToString();
        device.AppVersion = request.AppVersion?.Trim() ?? device.AppVersion;
        device.LastWifiSsid = request.WifiSsid?.Trim();
        device.LastWifiSignal = request.WifiSignal;
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> UpdateDevice(Guid id, UpdateDeviceRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var device = await db.Set<Device>().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
        {
            return Error.NotFound(nameof(Device), id).ToProblem();
        }

        db.SetExpectedVersion(device, request.Version);
        device.Name = request.Name.Trim();
        device.DepartmentId = request.DepartmentId;
        device.Location = request.Location?.Trim();
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> RevokeDevice(Guid id, HospitalDbContext db, IAuditLogger audit, ISessionValidator sessions, TimeProvider time, CancellationToken ct)
    {
        var device = await db.Set<Device>().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
        {
            return Error.NotFound(nameof(Device), id).ToProblem();
        }

        var now = time.GetUtcNow();
        device.IsRevoked = true;
        device.RevokedAt = now;
        var active = await db.Set<UserSession>().Where(s => s.DeviceId == id && s.RevokedAt == null).ToListAsync(ct);
        foreach (var session in active)
        {
            session.RevokedAt = now;
            session.RevokedReason = "device_revoked";
        }

        audit.Add(new AuditEntry(AuditCategories.Security, "device.revoked", $"Revoked device '{device.Name}'")
        {
            EntityType = nameof(Device),
            EntityId = device.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);

        // Only once the sessions' end is saved: a server told sooner could read one as active and keep that (SCAL-01).
        foreach (var session in active)
        {
            sessions.Invalidate(session.Id);
        }

        return TypedResults.NoContent();
    }
}
