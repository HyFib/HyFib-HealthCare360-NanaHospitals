using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Organization;

namespace HyFib.HealthCare360.Integrations.Laboratory.Devices;

/// <summary>A device of an analyser connection as Lab setup lists it. The secret is never listed.</summary>
public sealed record LabDeviceDto(
    Guid Id, string ConnectionCode, string? Name, LabDeviceAuthentication Method, string Identifier, string? Subject, LabDeviceStatus Status,
    DateTimeOffset? FirstSeenAt, DateTimeOffset? LastSeenAt, string? LastAddress, int RefusedAttempts, string? AddedByName, DateTimeOffset? AddedAt,
    string? ApprovedByName, DateTimeOffset? ApprovedAt, Guid? PendingApprovalId, uint Version);

/// <summary>
/// Add a device to a connection: a device the gateway saw and refused (<see cref="SeenDeviceId"/>), or a new one. A
/// mutual-TLS device is named by its client certificate's SHA-256 fingerprint; a shared-secret device is given a key id and
/// a secret here, shown once, to enter on the device.
/// </summary>
public sealed record AddLabDeviceRequest(string ConnectionCode, string Name, LabDeviceAuthentication Method, string? Fingerprint, Guid? SeenDeviceId, string Reason);

/// <summary>The device added and the approval it waits for; for a shared-secret device, its key id and secret, shown this once.</summary>
public sealed record LabDeviceAdded(LabDeviceDto Device, ApprovalRequestDto Approval, string? KeyId, string? Secret);

public sealed record RevokeLabDeviceRequest(uint Version, string Reason);

public sealed class AddLabDeviceRequestValidator : AbstractValidator<AddLabDeviceRequest>
{
    public AddLabDeviceRequestValidator()
    {
        RuleFor(x => x.ConnectionCode).NotEmpty().MaximumLength(32);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Method).Must(m => m is LabDeviceAuthentication.SharedSecret or LabDeviceAuthentication.MutualTls or LabDeviceAuthentication.LabAgent)
            .WithMessage("Choose a shared secret, a client certificate or a lab agent.");
        RuleFor(x => x.SeenDeviceId).NotNull().When(x => x.Method == LabDeviceAuthentication.LabAgent)
            .WithMessage("Add a lab agent from the list of devices refused on the connection.");
        RuleFor(x => x.Fingerprint).NotEmpty().Matches("^[0-9A-Fa-f:]{64,95}$")
            .When(x => x.Method == LabDeviceAuthentication.MutualTls && x.SeenDeviceId is null)
            .WithMessage("Enter the client certificate's SHA-256 fingerprint (64 hexadecimal digits).");
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}

public sealed class RevokeLabDeviceRequestValidator : AbstractValidator<RevokeLabDeviceRequest>
{
    public RevokeLabDeviceRequestValidator()
    {
        RuleFor(x => x.Version).SeenVersion();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}

internal sealed record LabDevicePayload(Guid DeviceId, string ConnectionCode, string Identifier, Guid AddedBy);

/// <summary>
/// The devices of the analyser connections: listed with those refused at the door, added by one person and approved by
/// another (kind <see cref="Kind"/>), and revoked at once by anyone who sets up connections.
/// </summary>
internal sealed class LabDeviceService(HospitalDbContext db, IApprovals approvals, ICurrentUser current, IAuditLogger audit, TimeProvider time)
{
    public const string Kind = "lab.device";
    public const string SubjectType = "LabDevice";

    public async Task<IReadOnlyList<LabDeviceDto>> ListAsync(string? connectionCode, CancellationToken ct)
    {
        var devices = await db.Set<LabDevice>().AsNoTracking()
            .Where(d => connectionCode == null || d.ConnectionCode == connectionCode)
            .OrderBy(d => d.ConnectionCode).ThenBy(d => d.Status).ThenByDescending(d => d.LastSeenAt)
            .ToListAsync(ct);
        var people = devices.SelectMany(d => new[] { d.AddedBy, d.ApprovedBy }).OfType<Guid>().Distinct().ToList();
        var names = await db.Set<Platform.Identity.User>().IgnoreQueryFilters().Where(u => people.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var ids = devices.Select(d => d.Id).ToList();
        var pending = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == Kind && r.Status == ApprovalStatus.Pending && ids.Contains(r.SubjectId))
            .Select(r => new { r.SubjectId, r.Id })
            .ToListAsync(ct);
        return [.. devices.Select(d => ToDto(d, names, pending.FirstOrDefault(p => p.SubjectId == d.Id)?.Id))];
    }

    public async Task<Result<LabDeviceAdded>> AddAsync(AddLabDeviceRequest request, CancellationToken ct)
    {
        var maker = current.UserId ?? Guid.Empty;
        var now = time.GetUtcNow();
        LabDevice device;
        string? secret = null;
        if (request.SeenDeviceId is { } seenId)
        {
            device = await db.Set<LabDevice>().FirstOrDefaultAsync(d => d.Id == seenId, ct) ?? null!;
            if (device is null || device.Status != LabDeviceStatus.Seen || device.ConnectionCode != request.ConnectionCode || device.Method != request.Method)
            {
                return Error.Rule("lab.device_not_seen", "That device is no longer waiting to be added to this connection.");
            }

            if (device.Method == LabDeviceAuthentication.SharedSecret)
            {
                // The key id it named is kept; the secret is the one it must be given now.
                secret = SecretTokens.NewOpaqueToken();
                device.SecretCipher = secret;
            }
        }
        else
        {
            var identifier = request.Method == LabDeviceAuthentication.MutualTls
                ? request.Fingerprint!.Replace(":", string.Empty, StringComparison.Ordinal).ToUpperInvariant()
                : "dev-" + SecretTokens.NewReadableCode(10).ToLowerInvariant();
            if (await db.Set<LabDevice>().AnyAsync(d => d.ConnectionCode == request.ConnectionCode && d.Method == request.Method && d.Identifier == identifier, ct))
            {
                return Error.Conflict("lab.device_exists", "This device is already listed for the connection.");
            }

            if (request.Method == LabDeviceAuthentication.SharedSecret)
            {
                secret = SecretTokens.NewOpaqueToken();
            }

            device = new LabDevice { ConnectionCode = request.ConnectionCode, Method = request.Method, Identifier = identifier, SecretCipher = secret };
            db.Add(device);
        }

        device.Name = request.Name.Trim();
        device.Status = LabDeviceStatus.PendingApproval;
        device.AddedBy = maker;
        device.AddedAt = now;
        var facility = current.FacilityId ?? await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstOrDefaultAsync(ct);
        var asked = await approvals.RequestAsync(
            Kind,
            new ApprovalSubject(SubjectType, device.Id, null, facility, null),
            new LabDevicePayload(device.Id, device.ConnectionCode, device.Identifier, maker),
            $"Device {device.Name} for analyser connection {device.ConnectionCode}",
            [
                new("Connection", device.ConnectionCode),
                new("How it proves itself", device.Method == LabDeviceAuthentication.MutualTls ? "Client certificate (mutual TLS)" : "Shared secret"),
                new(device.Method == LabDeviceAuthentication.MutualTls ? "Certificate fingerprint (SHA-256)" : "Key id", device.Identifier),
                new("Last seen from", device.LastAddress ?? "Not seen yet"),
                new("What approving does", "The gateway accepts results from this device on this connection."),
            ],
            request.Reason.Trim(),
            ct);
        if (asked.IsFailure)
        {
            return asked.Error!;
        }

        audit.Add(new AuditEntry(AuditCategories.Security, "lab.device_added", $"Device {device.Name} added to analyser connection {device.ConnectionCode}; waits for approval")
        {
            EntityType = nameof(LabDevice),
            EntityId = device.Id.ToString(),
            Details = new { connection = device.ConnectionCode, method = device.Method.ToString(), approval = asked.Value.Id },
        });
        await db.SaveChangesAsync(ct);
        var names = new Dictionary<Guid, string> { [maker] = current.DisplayName ?? current.UserName ?? string.Empty };
        return new LabDeviceAdded(ToDto(device, names, asked.Value.Id), asked.Value,
            device.Method == LabDeviceAuthentication.SharedSecret ? device.Identifier : null, secret);
    }

    /// <summary>Approves the device, in the approval's transaction, if it is still the one asked about and still waiting.</summary>
    public async Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<LabDevicePayload>(request.Payload);
        var device = await db.Set<LabDevice>().FirstOrDefaultAsync(d => d.Id == asked.DeviceId, ct);
        if (device is null || device.Status != LabDeviceStatus.PendingApproval || device.Identifier != asked.Identifier || device.AddedBy != asked.AddedBy)
        {
            return Error.Rule("approval.stale", "The device was changed or revoked after this was asked for. Ask again.");
        }

        device.Status = LabDeviceStatus.Approved;
        device.ApprovedBy = current.UserId;
        device.ApprovedAt = time.GetUtcNow();
        audit.Add(new AuditEntry(AuditCategories.Security, "lab.device_approved",
            $"Device {device.Name} approved for analyser connection {device.ConnectionCode} (added by {request.RequestedByName})")
        {
            EntityType = nameof(LabDevice),
            EntityId = device.Id.ToString(),
            Details = new { approval = request.Id, connection = device.ConnectionCode },
        });
        return $"the gateway accepts results from {device.Name} on {device.ConnectionCode}";
    }

    public async Task<Result> RevokeAsync(Guid id, RevokeLabDeviceRequest request, CancellationToken ct)
    {
        var device = await db.Set<LabDevice>().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
        {
            return Error.NotFound(nameof(LabDevice), id);
        }

        if (device.Version != request.Version)
        {
            return Error.Conflict("lab.device_changed", "The device was changed since you opened the list. Open it again.");
        }

        device.Status = LabDeviceStatus.Revoked;
        device.SecretCipher = null;
        device.RevokedBy = current.UserId;
        device.RevokedAt = time.GetUtcNow();
        audit.Add(new AuditEntry(AuditCategories.Security, "lab.device_revoked", $"Device {device.Name ?? device.Identifier} revoked on analyser connection {device.ConnectionCode}: {request.Reason}")
        {
            EntityType = nameof(LabDevice),
            EntityId = device.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    private static LabDeviceDto ToDto(LabDevice d, IReadOnlyDictionary<Guid, string> names, Guid? pendingApprovalId) =>
        new(d.Id, d.ConnectionCode, d.Name, d.Method, d.Identifier, d.Subject, d.Status, d.FirstSeenAt, d.LastSeenAt, d.LastAddress, d.RefusedAttempts,
            d.AddedBy is { } added ? names.GetValueOrDefault(added) : null, d.AddedAt,
            d.ApprovedBy is { } approved ? names.GetValueOrDefault(approved) : null, d.ApprovedAt, pendingApprovalId, d.Version);
}

internal sealed class LabDeviceApproval(LabDeviceService devices) : IApprovalAction
{
    public string Kind => LabDeviceService.Kind;

    public string Title => "Analyser device";

    public string ApprovePermission => IntegrationsPermissions.LabConnectionsManage;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => devices.ApplyAsync(request, ct);
}

internal static class LabDeviceEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/integrations/lab/devices").WithTags("Laboratory analysers")
            .RequirePermission(IntegrationsPermissions.LabConnectionsManage);
        group.MapGet("/", async (string? connection, LabDeviceService devices, CancellationToken ct) => TypedResults.Ok(await devices.ListAsync(connection, ct)));
        group.MapPost("/", async (AddLabDeviceRequest request, LabDeviceService devices, CancellationToken ct) =>
                (await devices.AddAsync(request, ct)).ToHttpResult(added => TypedResults.Accepted($"/api/v1/approvals/{added.Approval.Id}", added)))
            .WithValidation<AddLabDeviceRequest>();
        group.MapPost("/{id:guid}/revoke", async (Guid id, RevokeLabDeviceRequest request, LabDeviceService devices, CancellationToken ct) =>
                (await devices.RevokeAsync(id, request, ct)).ToNoContent())
            .WithValidation<RevokeLabDeviceRequest>();
    }
}
