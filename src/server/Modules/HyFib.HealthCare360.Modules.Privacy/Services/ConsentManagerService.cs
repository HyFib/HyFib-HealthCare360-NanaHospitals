using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Privacy.Services;

/// <summary>
/// The consent manager protocol, as HC360 speaks it (DPDP Rules 2025, rule 4 and the First Schedule). The Board has not
/// yet published a wire format, so this is HC360's own, kept small, and an adapter can translate once it does:
/// <list type="bullet">
/// <item>Each consent manager POSTs events to <c>/api/v1/consent-managers/{code}/events</c>: JSON
/// (<see cref="ConsentManagerEventBody"/>), with <c>X-CM-Timestamp</c> (Unix seconds) and <c>X-CM-Signature</c>, the hex
/// HMAC-SHA256 of <c>{timestamp}.{body}</c> under the secret it shares with the hospital. A timestamp more than five
/// minutes away from the server's clock is refused, and an event id is acted on once.</item>
/// <item>Events: <c>consent.granted</c> (a new artefact: the patient, the purpose, the data items and the validity) and
/// <c>consent.withdrawn</c> (the artefact's id).</item>
/// <item>A withdrawal made at the hospital is sent to the consent manager (<see cref="IConsentManagerGateway"/>) as
/// <c>POST {baseUrl}/artefacts/{id}/withdrawal</c>, signed the same way, and sent again until it is acknowledged.</item>
/// </list>
/// </summary>
public static class ConsentManagerProtocol
{
    public const string TimestampHeader = "X-CM-Timestamp";
    public const string SignatureHeader = "X-CM-Signature";
    public const string Granted = "consent.granted";
    public const string Withdrawn = "consent.withdrawn";
    public static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);
    public const int MaxBodyBytes = 32 * 1024;

    public static string Sign(string secret, string timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        var message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(prefix.Length));
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message));
    }

    public static bool Verify(string secret, string timestamp, ReadOnlySpan<byte> body, string signature) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(secret, timestamp, body)), Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant()));
}

/// <summary>Who the consent is about: the hospital's UHID and the mobile number registered with it, which must match.</summary>
public sealed record ConsentPrincipal(string? Uhid, string? Mobile);

public sealed record ConsentArtefactBody(
    string Id, ConsentPrincipal? Principal, string? Purpose, IReadOnlyList<string>? DataItems, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil, bool GivenByGuardian = false);

public sealed record ConsentManagerEventBody(string EventId, string Kind, ConsentArtefactBody Artefact);

/// <summary>The answer to a consent manager: what was done with its event.</summary>
public sealed record ConsentManagerEventAnswer(string Outcome, Guid? ArtefactId = null);

public sealed record ConsentManagerDto(Guid Id, string Code, string Name, string RegistrationNumber, string? BaseUrl, bool IsSimulator, bool IsActive, int ActiveArtefacts, uint Version);

/// <summary>A consent manager just registered, and its signing secret, shown once.</summary>
public sealed record ConsentManagerCreated(Guid Id, string Code, string Secret);

public sealed record SaveConsentManagerRequest(string Code, string Name, string RegistrationNumber, string? BaseUrl, bool IsSimulator);

public sealed record ConsentArtefactDto(
    Guid Id, Guid ConsentManagerId, string ConsentManagerName, string ArtefactId, Guid PatientId, ConsentType Purpose, IReadOnlyList<string> DataItems,
    DateTimeOffset ValidFrom, DateTimeOffset ValidUntil, ConsentArtefactStatus Status, bool InForce, bool GivenByGuardian, DateTimeOffset ReceivedAt,
    DateTimeOffset? WithdrawnAt, ConsentWithdrawalSource? WithdrawnVia, bool WithdrawalPending);

/// <summary>A simulated event for the built-in simulator: what a consent manager would send.</summary>
public sealed record SimulateConsentEventRequest(string Kind, string ArtefactId, string? Uhid, string? Mobile, ConsentType? Purpose, IReadOnlyList<string>? DataItems,
    DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil, bool GivenByGuardian = false);

public sealed class SaveConsentManagerRequestValidator : AbstractValidator<SaveConsentManagerRequest>
{
    public SaveConsentManagerRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Matches("^[a-z0-9-]{2,12}$").WithMessage("Use 2 to 12 lower-case letters, digits or dashes: it goes in the address the consent manager calls.");
        RuleFor(x => x.Name).NotEmpty().SafeText(200);
        RuleFor(x => x.RegistrationNumber).NotEmpty().WithMessage("Enter its registration number with the Data Protection Board.").SafeText(64);
        RuleFor(x => x.BaseUrl).NotEmpty().WithMessage("Enter the address withdrawals are sent to.").When(x => !x.IsSimulator)
            .Must(u => u is null || (Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
            .WithMessage("Use an https:// address.").MaximumLength(500);
    }
}

public sealed class SimulateConsentEventRequestValidator : AbstractValidator<SimulateConsentEventRequest>
{
    public SimulateConsentEventRequestValidator()
    {
        RuleFor(x => x.Kind).Must(k => k is ConsentManagerProtocol.Granted or ConsentManagerProtocol.Withdrawn).WithMessage("consent.granted or consent.withdrawn.");
        RuleFor(x => x.ArtefactId).NotEmpty().SafeText(100);
        RuleFor(x => x.Uhid).SafeText(32);
        RuleFor(x => x.Mobile).PhoneNumber();
        RuleForEach(x => x.DataItems).NotEmpty().SafeText(64);
    }
}

/// <summary>Sends a withdrawal made at the hospital to the consent manager the artefact came from.</summary>
public interface IConsentManagerGateway
{
    Task<Result> SendWithdrawalAsync(ConsentManager manager, string secret, string artefactId, DateTimeOffset withdrawnAt, CancellationToken ct);
}

/// <summary>The real consent managers: an HTTPS POST signed as the protocol says, acknowledged by a 2xx answer.</summary>
internal sealed class HttpConsentManagerGateway(HttpClient http, TimeProvider time) : IConsentManagerGateway
{
    public async Task<Result> SendWithdrawalAsync(ConsentManager manager, string secret, string artefactId, DateTimeOffset withdrawnAt, CancellationToken ct)
    {
        if (manager.BaseUrl is not { Length: > 0 } baseUrl)
        {
            return Error.Rule("privacy.cm_no_address", "The consent manager has no address to send withdrawals to.");
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(new { artefactId, withdrawnAt }, JsonSerializerOptions.Web);
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/artefacts/{Uri.EscapeDataString(artefactId)}/withdrawal")
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new("application/json") } },
        };
        request.Headers.Add(ConsentManagerProtocol.TimestampHeader, timestamp);
        request.Headers.Add(ConsentManagerProtocol.SignatureHeader, ConsentManagerProtocol.Sign(secret, timestamp, body));
        try
        {
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode
                ? Result.Success()
                : Error.Rule("privacy.cm_refused", string.Create(CultureInfo.InvariantCulture, $"The consent manager answered {(int)response.StatusCode}."));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Error.Rule("privacy.cm_unreachable", "The consent manager could not be reached: " + ex.Message);
        }
    }
}

/// <summary>The built-in simulator: withdrawals are recorded as sent (and kept for the screen), never sent anywhere.</summary>
internal sealed class SimulatorConsentManagerGateway : IConsentManagerGateway
{
    public Task<Result> SendWithdrawalAsync(ConsentManager manager, string secret, string artefactId, DateTimeOffset withdrawnAt, CancellationToken ct) =>
        Task.FromResult(Result.Success());
}

/// <summary>
/// Consent managers and their consent artefacts (DPDP Act 2023 s.6(7) to (9); DPDP Rules 2025, rule 4): registers the
/// consent managers the hospital accepts, takes their signed events, records each artefact as the patient's consent of
/// its kind so every existing check honours it, and carries withdrawals both ways.
/// </summary>
public sealed class ConsentManagerService(
    HospitalDbContext db,
    IFieldProtector protector,
    IConsentManagerGateway gateway,
    [FromKeyedServices("simulator")] IConsentManagerGateway simulator,
    IGuardianConsents guardians,
    IAuditLogger audit,
    ICurrentUser currentUser,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<ConsentManagerService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async Task<IReadOnlyList<ConsentManagerDto>> ListAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return await db.Set<ConsentManager>().AsNoTracking().OrderBy(m => m.Name)
            .Select(m => new ConsentManagerDto(m.Id, m.Code, m.Name, m.RegistrationNumber, m.BaseUrl, m.IsSimulator, m.IsActive,
                db.Set<ConsentArtefact>().Count(a => a.ConsentManagerId == m.Id && a.Status == ConsentArtefactStatus.Active && a.ValidUntil > now), m.Version))
            .ToListAsync(ct);
    }

    /// <summary>Registers a consent manager and makes its signing secret, shown once to give to the consent manager.</summary>
    public async Task<Result<ConsentManagerCreated>> CreateAsync(SaveConsentManagerRequest request, CancellationToken ct)
    {
        if (request.IsSimulator && !options.Value.AllowsSimulators)
        {
            return Error.Rule("privacy.cm_simulator", "The consent manager simulator is for training and demonstration servers only.");
        }

        if (await db.Set<ConsentManager>().AnyAsync(m => m.Code == request.Code, ct))
        {
            return Error.Conflict("privacy.cm_code_taken", "Another consent manager has this code.");
        }

        var secret = SecretTokens.NewOpaqueToken(32);
        var manager = new ConsentManager
        {
            Code = request.Code,
            Name = request.Name.Trim(),
            RegistrationNumber = request.RegistrationNumber.Trim(),
            BaseUrl = request.IsSimulator ? null : request.BaseUrl?.Trim(),
            IsSimulator = request.IsSimulator,
            SecretCipher = protector.Protect(secret),
        };
        db.Add(manager);
        audit.Add(new AuditEntry(AuditCategories.Admin, "privacy.cm_registered", $"Consent manager {manager.Name} ({manager.RegistrationNumber}) registered as {manager.Code}")
        {
            EntityType = nameof(ConsentManager),
            EntityId = manager.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        return new ConsentManagerCreated(manager.Id, manager.Code, secret);
    }

    /// <summary>Stops accepting events from a consent manager. Its artefacts stay as they are, and can still be withdrawn.</summary>
    public async Task<Result> DeactivateAsync(Guid id, CancellationToken ct)
    {
        var manager = await db.Set<ConsentManager>().FirstOrDefaultAsync(m => m.Id == id, ct);
        if (manager is null)
        {
            return Error.NotFound(nameof(ConsentManager), id);
        }

        manager.IsActive = false;
        audit.Add(new AuditEntry(AuditCategories.Admin, "privacy.cm_deactivated", $"Consent manager {manager.Name} no longer accepted")
        {
            EntityType = nameof(ConsentManager),
            EntityId = manager.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// An event from a consent manager, as received: checked against its signature and timestamp before anything in it is
    /// read, acted on once by its id.
    /// </summary>
    public async Task<Result<ConsentManagerEventAnswer>> HandleAsync(string code, byte[] body, string? timestamp, string? signature, CancellationToken ct)
    {
        var manager = await db.Set<ConsentManager>().FirstOrDefaultAsync(m => m.Code == code && m.IsActive, ct);
        if (manager is null)
        {
            return Error.NotFound(nameof(ConsentManager), code);
        }

        var secret = protector.Unprotect(manager.SecretCipher);
        if (secret is null || string.IsNullOrWhiteSpace(timestamp) || string.IsNullOrWhiteSpace(signature)
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || !ConsentManagerProtocol.Verify(secret, timestamp, body, signature))
        {
            logger.LogWarning("A consent manager event for {Code} was refused: its signature did not verify", code);
            return Error.Forbidden("privacy.cm_signature", "The signature does not verify.");
        }

        var now = time.GetUtcNow();
        if ((now - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > ConsentManagerProtocol.Skew)
        {
            return Error.Forbidden("privacy.cm_stale", "The timestamp is more than five minutes from the hospital's clock.");
        }

        ConsentManagerEventBody? message;
        try
        {
            message = JsonSerializer.Deserialize<ConsentManagerEventBody>(body, Json);
        }
        catch (JsonException)
        {
            message = null;
        }

        if (message is not { EventId.Length: > 0 and <= 100, Artefact.Id.Length: > 0 and <= 100 })
        {
            return Error.Validation("privacy.cm_body", "The event must carry an eventId and an artefact id (at most 100 characters each).");
        }

        if (await db.Set<ConsentManagerEvent>().AsNoTracking().FirstOrDefaultAsync(e => e.ConsentManagerId == manager.Id && e.EventId == message.EventId, ct) is { } seen)
        {
            return new ConsentManagerEventAnswer("duplicate: " + seen.Outcome);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var result = message.Kind switch
        {
            ConsentManagerProtocol.Granted => await GrantAsync(manager, message.Artefact, now, ct),
            ConsentManagerProtocol.Withdrawn => await WithdrawFromManagerAsync(manager, message.Artefact.Id, now, ct),
            _ => Error.Validation("privacy.cm_kind", $"Unknown event kind; use {ConsentManagerProtocol.Granted} or {ConsentManagerProtocol.Withdrawn}."),
        };
        if (result.IsFailure)
        {
            return result.Error!;
        }

        db.Add(new ConsentManagerEvent
        {
            ConsentManagerId = manager.Id,
            EventId = message.EventId,
            Kind = message.Kind,
            ArtefactId = message.Artefact.Id,
            ReceivedAt = now,
            Outcome = result.Value!.Outcome,
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return result.Value!;
    }

    private async Task<Result<ConsentManagerEventAnswer>> GrantAsync(ConsentManager manager, ConsentArtefactBody body, DateTimeOffset now, CancellationToken ct)
    {
        if (!Enum.TryParse<ConsentType>(body.Purpose, ignoreCase: false, out var purpose) || !ConsentArtefact.IsAcceptedPurpose(purpose))
        {
            return Error.Validation("privacy.cm_purpose",
                "The purpose must be one of DataSharing, Research, SmsWhatsAppUpdates, MarketingCommunication or Photography. Health care itself needs no consent artefact.");
        }

        if (body.ValidFrom is not { } from || body.ValidUntil is not { } until || until <= from || until <= now)
        {
            return Error.Validation("privacy.cm_validity", "The artefact needs a validFrom and a later validUntil that has not passed.");
        }

        var items = (body.DataItems ?? []).Select(i => i.Trim()).Where(i => i.Length is > 0 and <= 64).Distinct(StringComparer.Ordinal).ToList();
        if (items.Count == 0)
        {
            return Error.Validation("privacy.cm_items", "Name the data items the consent covers.");
        }

        var uhid = body.Principal?.Uhid?.Trim();
        var mobile = Phone.Normalize(body.Principal?.Mobile);
        var patient = uhid is null || mobile is null
            ? null
            : await db.Set<Patient>().Where(p => p.Uhid == uhid && p.Phone == mobile && p.IsActive && p.MergedIntoPatientId == null)
                .Select(p => new { p.Id, p.DateOfBirth }).FirstOrDefaultAsync(ct);
        if (patient is null)
        {
            return Error.Rule("privacy.cm_principal_unknown", "No patient has this UHID with this mobile number.");
        }

        if (await db.Set<ConsentArtefact>().AnyAsync(a => a.ConsentManagerId == manager.Id && a.ArtefactId == body.Id, ct))
        {
            return Error.Conflict("privacy.cm_artefact_exists", "This artefact was already received.");
        }

        var today = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromMinutes(330)).DateTime);
        if (ChildData.IsChild(patient.DateOfBirth, today))
        {
            // s.9: a child's consent comes from a parent or guardian whose consent the hospital verified; never marketing.
            var verified = body.GivenByGuardian && await guardians.HasVerifiedAsync(patient.Id, ct);
            if (ChildData.WhyRefused(purpose, patient.DateOfBirth, today, verified) is { } refused)
            {
                return refused;
            }

            if (!verified)
            {
                return Error.Rule("privacy.cm_child_guardian", "The patient is under 18: the consent must be given by the parent or guardian whose consent the hospital verified.");
            }
        }

        var consent = new PatientConsent
        {
            PatientId = patient.Id,
            Type = purpose,
            ConsentVersion = $"cm:{manager.Code}",
            Granted = true,
            CapturedAt = now,
            SignedByRelation = body.GivenByGuardian ? "Parent or guardian (consent manager)" : null,
            Notes = Fit($"Consent artefact {body.Id} from consent manager {manager.Name} ({manager.RegistrationNumber}), valid until {until:yyyy-MM-dd}: {string.Join(", ", items)}", 1000),
            ShareUntil = purpose == ConsentType.DataSharing ? DateOnly.FromDateTime(until.ToOffset(TimeSpan.FromMinutes(330)).DateTime) : null,
        };
        db.Add(consent);
        var artefact = new ConsentArtefact
        {
            ConsentManagerId = manager.Id,
            ArtefactId = body.Id,
            PatientId = patient.Id,
            Purpose = purpose,
            DataItems = items,
            ValidFrom = from,
            ValidUntil = until,
            Status = ConsentArtefactStatus.Active,
            ReceivedAt = now,
            ConsentRecordId = consent.Id,
            GivenByGuardian = body.GivenByGuardian,
        };
        db.Add(artefact);
        audit.Add(new AuditEntry(AuditCategories.Data, "privacy.cm_consent_granted",
            $"Consent for {purpose} received from consent manager {manager.Name} (artefact {body.Id}), valid until {until:yyyy-MM-dd}")
        {
            PatientId = patient.Id,
            EntityType = nameof(ConsentArtefact),
            EntityId = artefact.Id.ToString(),
            Details = new { manager = manager.Code, artefact = body.Id, purpose, items, from, until },
        });
        return new ConsentManagerEventAnswer("recorded", artefact.Id);
    }

    private async Task<Result<ConsentManagerEventAnswer>> WithdrawFromManagerAsync(ConsentManager manager, string artefactId, DateTimeOffset now, CancellationToken ct)
    {
        var artefact = await db.Set<ConsentArtefact>().FirstOrDefaultAsync(a => a.ConsentManagerId == manager.Id && a.ArtefactId == artefactId, ct);
        if (artefact is null)
        {
            return Error.NotFound(nameof(ConsentArtefact), artefactId);
        }

        if (artefact.Status != ConsentArtefactStatus.Active)
        {
            return new ConsentManagerEventAnswer("already " + artefact.Status.ToString().ToLowerInvariant(), artefact.Id);
        }

        await EndAsync(artefact, ConsentArtefactStatus.Withdrawn, ConsentWithdrawalSource.ConsentManager, $"Withdrawn at consent manager {manager.Name}", null, now, ct);
        artefact.WithdrawalSentAt = now;
        return new ConsentManagerEventAnswer("withdrawn", artefact.Id);
    }

    /// <summary>The artefacts of a patient, newest first.</summary>
    public async Task<IReadOnlyList<ConsentArtefactDto>> ForPatientAsync(Guid patientId, CancellationToken ct) =>
        await ForPatientsAsync([patientId], ct);

    public async Task<IReadOnlyList<ConsentArtefactDto>> ForPatientsAsync(IReadOnlyCollection<Guid> patientIds, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var rows = await db.Set<ConsentArtefact>().AsNoTracking().Where(a => patientIds.Contains(a.PatientId))
            .Join(db.Set<ConsentManager>().AsNoTracking(), a => a.ConsentManagerId, m => m.Id, (a, m) => new { a, m.Name })
            .OrderByDescending(x => x.a.ReceivedAt).Take(200).ToListAsync(ct);
        return [.. rows.Select(x => Dto(x.a, x.Name, now))];
    }

    /// <summary>
    /// The patient (portal) or staff withdraw an artefact at the hospital (s.6(4): as easy as giving it): the consent is
    /// revoked at once and the withdrawal is sent to the consent manager, now and again until it is acknowledged.
    /// </summary>
    public async Task<Result<ConsentArtefactDto>> WithdrawAsync(Guid artefactId, ConsentWithdrawalSource via, Func<Guid, bool>? mayActFor, CancellationToken ct)
    {
        var artefact = await db.Set<ConsentArtefact>().FirstOrDefaultAsync(a => a.Id == artefactId, ct);
        if (artefact is null || (mayActFor is not null && !mayActFor(artefact.PatientId)))
        {
            return Error.NotFound(nameof(ConsentArtefact), artefactId);
        }

        if (artefact.Status != ConsentArtefactStatus.Active)
        {
            return Error.Rule("privacy.cm_artefact_ended", "This consent has already ended.");
        }

        var now = time.GetUtcNow();
        await EndAsync(artefact, ConsentArtefactStatus.Withdrawn, via, via == ConsentWithdrawalSource.Portal ? "Withdrawn by the patient in the portal" : "Withdrawn at the hospital",
            currentUser.UserId, now, ct);
        await db.SaveChangesAsync(ct);
        await SendWithdrawalAsync(artefact, ct);
        var manager = await db.Set<ConsentManager>().AsNoTracking().FirstAsync(m => m.Id == artefact.ConsentManagerId, ct);
        return Dto(artefact, manager.Name, now);
    }

    /// <summary>
    /// The hourly round: artefacts past their validity are ended, a consent revoked at the desk ends its artefact too (and
    /// the consent manager is told), and withdrawals not yet acknowledged are sent again.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var changed = 0;
        foreach (var artefact in await db.Set<ConsentArtefact>().Where(a => a.Status == ConsentArtefactStatus.Active && a.ValidUntil <= now).Take(500).ToListAsync(ct))
        {
            await EndAsync(artefact, ConsentArtefactStatus.Expired, null, "The consent artefact's validity ended", null, now, ct);
            changed++;
        }

        var revokedHere = await db.Set<ConsentArtefact>()
            .Where(a => a.Status == ConsentArtefactStatus.Active
                        && db.Set<PatientConsent>().Any(c => c.Id == a.ConsentRecordId && c.RevokedAt != null))
            .Take(500).ToListAsync(ct);
        foreach (var artefact in revokedHere)
        {
            await EndAsync(artefact, ConsentArtefactStatus.Withdrawn, ConsentWithdrawalSource.Staff, "The consent was withdrawn at the hospital", null, now, ct);
            changed++;
        }

        await db.SaveChangesAsync(ct);
        var pending = await db.Set<ConsentArtefact>()
            .Where(a => a.Status == ConsentArtefactStatus.Withdrawn && a.WithdrawalSentAt == null && a.WithdrawalAttempts < 48)
            .Take(200).ToListAsync(ct);
        foreach (var artefact in pending)
        {
            await SendWithdrawalAsync(artefact, ct);
        }

        return changed + pending.Count;
    }

    /// <summary>
    /// The built-in simulator: makes the event a consent manager would send, signs it with the consent manager's own
    /// secret, and hands it to <see cref="HandleAsync"/>, so the whole path (signature included) is the real one.
    /// </summary>
    public async Task<Result<ConsentManagerEventAnswer>> SimulateAsync(Guid managerId, SimulateConsentEventRequest request, CancellationToken ct)
    {
        var manager = await db.Set<ConsentManager>().AsNoTracking().FirstOrDefaultAsync(m => m.Id == managerId, ct);
        if (manager is null)
        {
            return Error.NotFound(nameof(ConsentManager), managerId);
        }

        if (!manager.IsSimulator || !options.Value.AllowsSimulators)
        {
            return Error.Rule("privacy.cm_simulator", "Only the built-in simulator, on a training or demonstration server, makes events here.");
        }

        var now = time.GetUtcNow();
        var body = JsonSerializer.SerializeToUtf8Bytes(new ConsentManagerEventBody(Guid.NewGuid().ToString("N"), request.Kind,
            new ConsentArtefactBody(request.ArtefactId, new ConsentPrincipal(request.Uhid, request.Mobile), request.Purpose?.ToString(), request.DataItems,
                request.ValidFrom ?? now, request.ValidUntil ?? now.AddDays(180), request.GivenByGuardian)), Json);
        var timestamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var secret = protector.Unprotect(manager.SecretCipher)!;
        return await HandleAsync(manager.Code, body, timestamp, ConsentManagerProtocol.Sign(secret, timestamp, body), ct);
    }

    private async Task EndAsync(ConsentArtefact artefact, ConsentArtefactStatus status, ConsentWithdrawalSource? via, string reason, Guid? userId, DateTimeOffset now, CancellationToken ct)
    {
        artefact.Status = status;
        if (status == ConsentArtefactStatus.Withdrawn)
        {
            artefact.WithdrawnAt = now;
            artefact.WithdrawnVia = via;
        }

        if (artefact.ConsentRecordId is { } consentId && await db.Set<PatientConsent>().FirstOrDefaultAsync(c => c.Id == consentId, ct) is { RevokedAt: null } consent)
        {
            consent.Revoke(reason, userId, now);
        }

        audit.Add(new AuditEntry(AuditCategories.Data, status == ConsentArtefactStatus.Expired ? "privacy.cm_consent_expired" : "privacy.cm_consent_withdrawn",
            $"Consent for {artefact.Purpose} (artefact {artefact.ArtefactId}) ended: {reason}")
        {
            PatientId = artefact.PatientId,
            EntityType = nameof(ConsentArtefact),
            EntityId = artefact.Id.ToString(),
            Details = new { artefact = artefact.ArtefactId, status, via },
        });
    }

    private async Task SendWithdrawalAsync(ConsentArtefact artefact, CancellationToken ct)
    {
        if (artefact.WithdrawnVia is not (ConsentWithdrawalSource.Portal or ConsentWithdrawalSource.Staff) || artefact.WithdrawalSentAt is not null)
        {
            return;
        }

        var manager = await db.Set<ConsentManager>().AsNoTracking().FirstAsync(m => m.Id == artefact.ConsentManagerId, ct);
        var secret = protector.Unprotect(manager.SecretCipher) ?? string.Empty;
        var sent = await (manager.IsSimulator ? simulator : gateway).SendWithdrawalAsync(manager, secret, artefact.ArtefactId, artefact.WithdrawnAt ?? time.GetUtcNow(), ct);
        artefact.WithdrawalAttempts++;
        if (sent.IsSuccess)
        {
            artefact.WithdrawalSentAt = time.GetUtcNow();
            artefact.WithdrawalError = null;
        }
        else
        {
            artefact.WithdrawalError = Fit(sent.Error!.Message, 500);
            logger.LogWarning("A withdrawal could not be sent to consent manager {Code}: {Error}", manager.Code, sent.Error.Message);
        }

        await db.SaveChangesAsync(ct);
    }

    private static ConsentArtefactDto Dto(ConsentArtefact a, string managerName, DateTimeOffset now) => new(
        a.Id, a.ConsentManagerId, managerName, a.ArtefactId, a.PatientId, a.Purpose, a.DataItems, a.ValidFrom, a.ValidUntil, a.Status, a.IsInForce(now),
        a.GivenByGuardian, a.ReceivedAt, a.WithdrawnAt, a.WithdrawnVia,
        a.Status == ConsentArtefactStatus.Withdrawn && a.WithdrawnVia != ConsentWithdrawalSource.ConsentManager && a.WithdrawalSentAt is null);

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
