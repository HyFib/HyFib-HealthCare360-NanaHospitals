using System.Text.Json;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Identity;
using Microsoft.IdentityModel.Tokens;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>A registered SMART client as administrators see it (its keys are public; it holds no secret).</summary>
public sealed record SmartClientDto(
    Guid Id, string ClientId, string Name, SmartClientKind Kind, SmartClientStatus Status, IReadOnlyList<string> RedirectUris, string Scopes,
    bool HasKeys, Guid? ServiceUserId, string? ServiceUserName, Guid? FacilityId, bool ReplacesPasswordSignIn,
    string RegisteredByName, DateTimeOffset RegisteredAt, string? ApprovedByName, DateTimeOffset? ApprovedAt, DateTimeOffset? SuspendedAt,
    string? SuspendedReason, Guid? PendingApprovalId, uint Version);

/// <summary>
/// Registers a partner application. An app: its exact redirect URIs (https, or http on the loopback address for a native
/// app) and <c>patient/</c> or <c>user/</c> scopes; it may register keys to authenticate at the token endpoint. A backend
/// service: its public keys (a JSON Web Key Set, RSA of 2048 bits or more, or EC P-256/P-384, no private parts),
/// <c>system/</c> scopes, the integration account it reads as and that account's facility.
/// </summary>
public sealed record RegisterSmartClientRequest(
    string Name, SmartClientKind Kind, IReadOnlyList<string>? RedirectUris, string Scopes, JsonElement? Jwks, Guid? ServiceUserId, Guid? FacilityId,
    bool ReplacesPasswordSignIn, string Reason);

public sealed record SuspendSmartClientRequest(uint Version, string Reason);

public sealed record SmartClientRegistered(SmartClientDto Client, ApprovalRequestDto Approval);

public sealed class RegisterSmartClientRequestValidator : AbstractValidator<RegisterSmartClientRequest>
{
    public RegisterSmartClientRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Scopes).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
        RuleFor(x => x.RedirectUris).NotEmpty().When(x => x.Kind == SmartClientKind.App).WithMessage("An app needs at least one redirect URI.");
        RuleFor(x => x.RedirectUris!.Count).LessThanOrEqualTo(10).When(x => x.RedirectUris is not null);
        RuleForEach(x => x.RedirectUris).Must(SmartClientRegistry.IsAllowedRedirect)
            .WithMessage("'{PropertyValue}' is not an allowed redirect URI: use https (or http on 127.0.0.1 or [::1] for a native app), without a fragment.");
        RuleFor(x => x.Jwks).NotNull().When(x => x.Kind == SmartClientKind.BackendService).WithMessage("A backend service needs its public keys (a JSON Web Key Set).");
        RuleFor(x => x.ServiceUserId).NotNull().When(x => x.Kind == SmartClientKind.BackendService).WithMessage("Choose the integration account the service reads as.");
        RuleFor(x => x.FacilityId).NotNull().When(x => x.Kind == SmartClientKind.BackendService).WithMessage("Choose the facility whose records the service reads.");
    }
}

public sealed class SuspendSmartClientRequestValidator : AbstractValidator<SuspendSmartClientRequest>
{
    public SuspendSmartClientRequestValidator()
    {
        RuleFor(x => x.Version).SeenVersion();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}

internal sealed record SmartClientPayload(Guid ClientId, string Scopes, string RedirectUrisJson, string? JwksJson, Guid? ServiceUserId, Guid RegisteredBy);

/// <summary>
/// SMART client registration: one administrator registers a client, which is issued nothing until a second approves it
/// (kind <see cref="Kind"/>, the approvals framework refusing the one who registered it); any administrator may suspend
/// one at once, revoking its tokens.
/// </summary>
internal sealed class SmartClientRegistry(
    HospitalDbContext db, IApprovals approvals, SmartAuthorization smart, ICurrentUser current, IAuditLogger audit, TimeProvider time)
{
    public const string Kind = "interop.smart_client";
    public const string SubjectType = "SmartClient";

    public static bool IsAllowedRedirect(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && string.IsNullOrEmpty(parsed.Fragment) && string.IsNullOrEmpty(parsed.UserInfo)
        && (parsed.Scheme == Uri.UriSchemeHttps
            || (parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback && parsed.Host is "127.0.0.1" or "[::1]"));

    public async Task<IReadOnlyList<SmartClientDto>> ListAsync(CancellationToken ct)
    {
        var clients = await db.Set<SmartClient>().AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
        var people = clients.SelectMany(c => new[] { c.RegisteredBy, c.ApprovedBy ?? Guid.Empty, c.ServiceUserId ?? Guid.Empty }).Distinct().ToList();
        var names = await db.Set<User>().IgnoreQueryFilters().Where(u => people.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.UserName, ct);
        var ids = clients.Select(c => c.Id).ToList();
        var pending = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == Kind && r.Status == ApprovalStatus.Pending && ids.Contains(r.SubjectId))
            .Select(r => new { r.SubjectId, r.Id }).ToListAsync(ct);
        return [.. clients.Select(c => ToDto(c, names, pending.FirstOrDefault(p => p.SubjectId == c.Id)?.Id))];
    }

    public async Task<Result<SmartClientRegistered>> RegisterAsync(RegisterSmartClientRequest request, CancellationToken ct)
    {
        var contexts = request.Kind == SmartClientKind.App ? new[] { SmartContext.Patient, SmartContext.User } : [SmartContext.System];
        var asked = request.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var scopes = new List<SmartScope>();
        foreach (var item in asked)
        {
            if (item == SmartScopes.LaunchPatient && request.Kind == SmartClientKind.App)
            {
                continue;
            }

            // A registration names only what it may be granted: read and search (v2 r, s, rs; v1 read), nothing more.
            if (!SmartScopes.TryParse(item, out var scope) || !contexts.Contains(scope.Context)
                || (scope.ToString() != item && !item.EndsWith(".read", StringComparison.Ordinal)))
            {
                return Error.Field("scopes", request.Kind == SmartClientKind.App
                    ? $"'{item}' cannot be granted to an app: use patient/ or user/ read scopes, such as patient/*.rs (this API is read-only)."
                    : $"'{item}' cannot be granted to a backend service: use system/ read scopes, such as system/*.rs (this API is read-only).");
            }

            scopes.Add(scope);
        }

        string? jwks = null;
        if (request.Jwks is { } keys)
        {
            if (KeysProblem(keys) is { } problem)
            {
                return Error.Field("jwks", problem);
            }

            jwks = keys.GetRawText();
        }

        if (request.Kind == SmartClientKind.BackendService
            && !await smart.MayReadAsync(request.ServiceUserId!.Value, request.FacilityId!.Value, ct))
        {
            return Error.Field("serviceUserId", "The integration account must be an active staff account assigned to the facility, with a role that reads FHIR (fhir.read).");
        }

        var maker = current.UserId ?? Guid.Empty;
        var now = time.GetUtcNow();
        var client = new SmartClient
        {
            ClientId = "hc360-" + SecretTokens.NewReadableCode(12).ToLowerInvariant(),
            Name = request.Name.Trim(),
            Kind = request.Kind,
            Status = SmartClientStatus.PendingApproval,
            RedirectUrisJson = JsonSerializer.Serialize(request.Kind == SmartClientKind.App ? request.RedirectUris ?? [] : []),
            JwksJson = jwks,
            Scopes = SmartScopes.Format(scopes, false),
            ServiceUserId = request.Kind == SmartClientKind.BackendService ? request.ServiceUserId : null,
            FacilityId = request.Kind == SmartClientKind.BackendService ? request.FacilityId : null,
            ReplacesPasswordSignIn = request.Kind == SmartClientKind.BackendService && request.ReplacesPasswordSignIn,
            RegisteredBy = maker,
            RegisteredAt = now,
        };
        db.Add(client);
        var facility = client.FacilityId ?? current.FacilityId ?? await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstOrDefaultAsync(ct);
        var serviceUser = client.ServiceUserId is { } su ? await db.Set<User>().Where(u => u.Id == su).Select(u => u.UserName).FirstOrDefaultAsync(ct) : null;
        var approval = await approvals.RequestAsync(
            Kind,
            new ApprovalSubject(SubjectType, client.Id, null, facility, null),
            new SmartClientPayload(client.Id, client.Scopes, client.RedirectUrisJson, client.JwksJson, client.ServiceUserId, maker),
            $"SMART client {client.Name} ({(client.Kind == SmartClientKind.App ? "app" : "backend service")})",
            [
                new("Client id", client.ClientId),
                new("Scopes", client.Scopes),
                new("Redirect URIs", client.Kind == SmartClientKind.App ? string.Join(", ", request.RedirectUris ?? []) : "None (a backend service)"),
                new("Reads as", client.Kind == SmartClientKind.App ? "The staff member who authorizes it" : serviceUser ?? "Unknown"),
                new("Replaces the account's password sign-in", client.ReplacesPasswordSignIn ? "Yes" : "No"),
                new("What approving does", "The client may be issued access tokens to the FHIR API. Each patient's record-sharing consent still applies to every read."),
            ],
            request.Reason.Trim(),
            ct);
        if (approval.IsFailure)
        {
            return approval.Error!;
        }

        audit.Add(new AuditEntry(AuditCategories.Security, "smart.client_registered", $"SMART client {client.Name} registered; waits for a second administrator")
        {
            EntityType = nameof(SmartClient),
            EntityId = client.Id.ToString(),
            Details = new { client.ClientId, kind = client.Kind.ToString(), client.Scopes, approval = approval.Value.Id },
        });
        await db.SaveChangesAsync(ct);
        var names = new Dictionary<Guid, string> { [maker] = current.DisplayName ?? current.UserName ?? string.Empty };
        return new SmartClientRegistered(ToDto(client, names, approval.Value.Id), approval.Value);
    }

    /// <summary>Activates the client, in the approval's transaction, if it is still as registered and still waiting.</summary>
    public async Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<SmartClientPayload>(request.Payload);
        var client = await db.Set<SmartClient>().FirstOrDefaultAsync(c => c.Id == asked.ClientId, ct);
        if (client is null || client.Status != SmartClientStatus.PendingApproval || client.Scopes != asked.Scopes
            || !SameJson(client.RedirectUrisJson, asked.RedirectUrisJson) || !SameJson(client.JwksJson, asked.JwksJson) || client.ServiceUserId != asked.ServiceUserId)
        {
            return Error.Rule("approval.stale", "The client was changed or suspended after this was asked for. Ask again.");
        }

        client.Status = SmartClientStatus.Active;
        client.ApprovedBy = current.UserId;
        client.ApprovedAt = time.GetUtcNow();
        audit.Add(new AuditEntry(AuditCategories.Security, "smart.client_approved", $"SMART client {client.Name} approved (registered by {request.RequestedByName})")
        {
            EntityType = nameof(SmartClient),
            EntityId = client.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { client.ClientId, approval = request.Id },
        });
        return $"{client.Name} may be issued access tokens as {client.ClientId}";
    }

    /// <summary>The same JSON whatever its spacing or key order (PostgreSQL stores jsonb normalised).</summary>
    private static bool SameJson(string? stored, string? asked) =>
        stored is null || asked is null
            ? stored == asked
            : System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(stored), System.Text.Json.Nodes.JsonNode.Parse(asked));

    public async Task<Result> SuspendAsync(Guid id, SuspendSmartClientRequest request, CancellationToken ct)
    {
        var client = await db.Set<SmartClient>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is null)
        {
            return Error.NotFound(nameof(SmartClient), id);
        }

        if (client.Version != request.Version)
        {
            return Error.Conflict("smart.client_changed", "The client was changed since you opened it. Open it again.");
        }

        client.Status = SmartClientStatus.Suspended;
        client.SuspendedBy = current.UserId;
        client.SuspendedAt = time.GetUtcNow();
        client.SuspendedReason = request.Reason.Trim();
        audit.Add(new AuditEntry(AuditCategories.Security, "smart.client_suspended", $"SMART client {client.Name} suspended: {client.SuspendedReason}")
        {
            EntityType = nameof(SmartClient),
            EntityId = client.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        await smart.RevokeAllAsync(client.Id, ct);
        return Result.Success();
    }

    /// <summary>Why a key set cannot be registered, or null: at least one public RSA (2048+) or EC (P-256, P-384) key, no private parts.</summary>
    public static string? KeysProblem(JsonElement keys)
    {
        try
        {
            var set = new JsonWebKeySet(keys.GetRawText());
            if (set.Keys.Count is 0 or > 10)
            {
                return "Register between one and ten keys.";
            }

            foreach (var key in set.Keys)
            {
                if (!string.IsNullOrEmpty(key.D) || !string.IsNullOrEmpty(key.P) || !string.IsNullOrEmpty(key.Q))
                {
                    return "The key set holds a private key. Register the public keys only: the private key stays with the partner.";
                }

                var fits = key.Kty switch
                {
                    "RSA" => Base64UrlEncoder.DecodeBytes(key.N ?? string.Empty).Length * 8 >= 2048,
                    "EC" => key.Crv is "P-256" or "P-384",
                    _ => false,
                };
                if (!fits)
                {
                    return "Use RSA keys of 2048 bits or more, or EC keys on P-256 or P-384.";
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException)
        {
            return "The keys are not a JSON Web Key Set.";
        }
    }

    private static SmartClientDto ToDto(SmartClient c, IReadOnlyDictionary<Guid, string> names, Guid? pending) =>
        new(c.Id, c.ClientId, c.Name, c.Kind, c.Status, JsonSerializer.Deserialize<List<string>>(c.RedirectUrisJson) ?? [], c.Scopes, c.JwksJson is not null,
            c.ServiceUserId, c.ServiceUserId is { } su ? names.GetValueOrDefault(su) : null, c.FacilityId, c.ReplacesPasswordSignIn,
            names.GetValueOrDefault(c.RegisteredBy) ?? "Unknown", c.RegisteredAt, c.ApprovedBy is { } a ? names.GetValueOrDefault(a) : null, c.ApprovedAt,
            c.SuspendedAt, c.SuspendedReason, pending, c.Version);
}

internal sealed class SmartClientApproval(SmartClientRegistry registry) : IApprovalAction
{
    public string Kind => SmartClientRegistry.Kind;

    public string Title => "SMART on FHIR client";

    public string ApprovePermission => InteropPermissions.SmartClientsManage;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => registry.ApplyAsync(request, ct);
}
