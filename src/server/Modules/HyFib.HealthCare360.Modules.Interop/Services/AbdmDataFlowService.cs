using System.Globalization;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Features;
using HyFib.HealthCare360.Platform.Messaging;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>A transfer for the outbox to carry out, after the request that asked for it was answered.</summary>
internal sealed record AbdmTransferJob(Guid TransferId);

/// <summary>
/// Consent and data flow at the hospital (HIP), ABDM M2: the consent manager notifies the consents patients gave other
/// providers (HIUs) to see records linked here, and those providers then ask for the records under a consent. HC360
/// answers each call as NHA's ABDM wrapper does:
/// <list type="number">
/// <item><b>Consent notify</b>: a granted consent is kept (<see cref="AbdmConsent"/>) when every care context it names
/// is linked here to the patient's ABHA address, and acknowledged <c>OK</c>; a revoked, expired or denied one ends the
/// consent kept for it.</item>
/// <item><b>Health-information request</b>: checked against the consent (granted, not past its erase date, the dates
/// asked for within its own, an https push URL outside the hospital's network, ECDH on Curve25519) and acknowledged; the
/// transfer then runs from the outbox (<see cref="TransferAsync"/>).</item>
/// <item><b>Transfer</b>: each consented record dated within the range is made as its ABDM document
/// (<see cref="AbdmBundleService.ShareAsync"/>), all are encrypted for the HIU with one fresh key pair
/// (<see cref="IFideliusCipher"/>), posted to the HIU seven to a page, and the gateway is told which care contexts were
/// delivered. It is audited under the patient as a disclosure. It runs once: a page the HIU refused is reported, not
/// sent again, and the HIU asks again under the consent.</item>
/// </list>
/// </summary>
internal sealed class AbdmDataFlowService(
    HospitalDbContext db,
    AbdmRecordLoader records,
    AbdmBundleService bundles,
    AbdmGatewayConfiguration configuration,
    IAbdmGatewayClient gateway,
    IFideliusCipher cipher,
    AbdmDataPush push,
    IOutbox outbox,
    IAuditLogger audit,
    TimeProvider time,
    ILogger<AbdmDataFlowService> log)
{
    public const string TransferType = "abdm.transfer";
    public const string ConsentGranted = "abdm.consent_granted";
    public const string ConsentEnded = "abdm.consent_ended";
    public const string Shared = "abdm.shared";

    public const string ErrorCode = "1000";
    public const string Granted = "GRANTED";

    /// <summary>Documents per page posted to the HIU, as NHA's wrapper pages them.</summary>
    public const int PageSize = 7;

    /// <summary>The consent manager's notification of a consent granted, or revoked, expired or denied.</summary>
    public async Task NotifyAsync(AbdmGatewayMessages.ConsentNotify call, string requestId, CancellationToken ct)
    {
        var notification = call.Notification;
        var detail = notification?.ConsentDetail;
        var consentId = (notification?.ConsentId ?? detail?.ConsentId)?.Trim() ?? string.Empty;
        var response = new AbdmGatewayMessages.RespRequest(requestId);
        if (consentId.Length is 0 or > 64 || notification?.Status is not { Length: > 0 } status)
        {
            await SendAsync(new AbdmGatewayMessages.OnConsentNotify(
                new("FAILURE", consentId), response, new(ErrorCode, "Invalid hip/notify request")), ct);
            return;
        }

        var now = time.GetUtcNow();
        var kept = await db.Set<AbdmConsent>().FirstOrDefaultAsync(c => c.ConsentId == consentId, ct);
        if (!string.Equals(status, Granted, StringComparison.OrdinalIgnoreCase))
        {
            if (kept is not null && kept.EndedAt is null)
            {
                kept.Status = status.ToUpperInvariant();
                kept.EndedAt = now;
                audit.Add(new AuditEntry(AuditCategories.PhiAccess, ConsentEnded, $"ABDM consent {kept.Status.ToLowerInvariant()}")
                {
                    PatientId = kept.PatientId,
                    EntityType = "abdm.consent",
                    EntityId = consentId,
                    Details = new { api = "abdm", status = kept.Status, hiu = kept.HiuId },
                });
                await db.SaveChangesAsync(ct);
            }

            await SendAsync(new AbdmGatewayMessages.OnConsentNotify(new("OK", consentId), response, null), ct);
            return;
        }

        var (settings, _, _) = await configuration.ReadAsync(ct);
        var address = AbdmLinkToken.Normalize(detail?.Patient?.Id ?? string.Empty);
        var named = (detail?.CareContexts ?? []).Where(c => !string.IsNullOrWhiteSpace(c.CareContextReference)).ToList();
        var references = named.Select(c => c.CareContextReference!).Distinct(StringComparer.Ordinal).ToList();
        var linked = await db.Set<AbdmLinkedCareContext>().AsNoTracking()
            .Where(c => c.HipId == settings.HipId && c.AbhaAddress == address && references.Contains(c.ReferenceNumber))
            .ToListAsync(ct);
        var patients = linked.Select(c => c.PatientId).Distinct().ToList();
        if (address.Length == 0 || references.Count == 0 || linked.Count != references.Count || patients.Count != 1
            || named.Any(c => c.PatientReference is { Length: > 0 } reference && reference != patients[0].ToString("D")))
        {
            await SendAsync(new AbdmGatewayMessages.OnConsentNotify(
                new("FAILURE", consentId), response, new(ErrorCode, "The consent names care contexts that are not linked to this ABHA address here.")), ct);
            return;
        }

        var consent = kept ?? db.Add(new AbdmConsent { ConsentId = consentId }).Entity;
        consent.HipId = settings.HipId!;
        consent.PatientId = patients[0];
        consent.AbhaAddress = address;
        consent.Status = Granted;
        consent.HiuId = detail?.Hiu?.Id;
        consent.Purpose = Truncate(string.Join(" ", new[] { detail?.Purpose?.Code, detail?.Purpose?.Text }.Where(s => !string.IsNullOrWhiteSpace(s))), 200);
        consent.HiTypes = Truncate(string.Join(",", detail?.HiTypes ?? []), 500);
        consent.DateFrom = Instant(detail?.Permission?.DateRange?.From);
        consent.DateTo = Instant(detail?.Permission?.DateRange?.To);
        consent.DataEraseAt = Instant(detail?.Permission?.DataEraseAt);
        consent.CareContextsJson = JsonSerializer.Serialize(references, AbdmGatewayMessages.Json);
        consent.NotifiedAt = now;
        consent.EndedAt = null;
        audit.Add(new AuditEntry(AuditCategories.PhiAccess, ConsentGranted, $"ABDM consent granted for {references.Count} records")
        {
            PatientId = consent.PatientId,
            EntityType = "abdm.consent",
            EntityId = consentId,
            Details = new { api = "abdm", hiu = consent.HiuId, purpose = consent.Purpose, records = references },
        });
        await db.SaveChangesAsync(ct);

        await SendAsync(new AbdmGatewayMessages.OnConsentNotify(new("OK", consentId), response, null), ct);
    }

    /// <summary>A provider's request for the records under a consent: checked, acknowledged, and queued.</summary>
    public async Task RequestAsync(AbdmGatewayMessages.HealthInformationRequest call, string requestId, CancellationToken ct)
    {
        var transactionId = call.TransactionId?.Trim() ?? string.Empty;
        var answer = new AbdmGatewayMessages.OnHealthInformationRequest(null, new AbdmGatewayMessages.RespRequest(requestId), null);
        if (transactionId.Length is 0 or > 64 || call.HiRequest is not { } hi)
        {
            await SendAsync(answer with { Error = new(ErrorCode, "Invalid health-information request") }, ct);
            return;
        }

        var acknowledged = answer with { HiRequest = new(transactionId, "ACKNOWLEDGED") };
        if (await db.Set<AbdmTransfer>().AnyAsync(t => t.TransactionId == transactionId, ct))
        {
            // The gateway sent the same request again: it was acknowledged, and its transfer runs once.
            await SendAsync(acknowledged, ct);
            return;
        }

        var now = time.GetUtcNow();
        var consentId = hi.Consent?.Id?.Trim() ?? string.Empty;
        var consent = consentId.Length == 0 ? null : await db.Set<AbdmConsent>().AsNoTracking().FirstOrDefaultAsync(c => c.ConsentId == consentId, ct);
        var from = Instant(hi.DateRange?.From) ?? consent?.DateFrom;
        var to = Instant(hi.DateRange?.To) ?? consent?.DateTo;
        var problem = consent is null ? $"ConsentId not found in database {consentId}"
            : consent.Status != Granted ? $"The consent is {consent.Status.ToLowerInvariant()}."
            : consent.DataEraseAt is { } erase && erase <= now ? "Consent EXPIRED: exceeded dataEraseAt"
            : (consent.DateFrom is { } consentFrom && from < consentFrom) || (consent.DateTo is { } consentTo && to > consentTo)
                ? "The dates asked for are outside the consent's."
            : !AbdmDataPush.IsAllowed(hi.DataPushUrl, out _) ? "The data-push URL must be https and outside the hospital's network."
            : KeyProblem(hi.KeyMaterial, now);
        if (problem is not null)
        {
            log.LogWarning("Refused ABDM health-information request {Transaction}: {Problem}", transactionId, problem);
            await SendAsync(answer with { Error = new(ErrorCode, problem) }, ct);
            return;
        }

        var transfer = new AbdmTransfer
        {
            TransactionId = transactionId,
            ConsentId = consent!.ConsentId,
            HipId = consent.HipId,
            PatientId = consent.PatientId,
            DataPushUrl = hi.DataPushUrl!,
            KeyMaterialJson = JsonSerializer.Serialize(hi.KeyMaterial, AbdmGatewayMessages.Json),
            DateFrom = from,
            DateTo = to,
            Status = AbdmTransferStatus.Acknowledged,
            RequestedAt = now,
        };
        db.Add(transfer);
        outbox.Enqueue(TransferType, new AbdmTransferJob(transfer.Id), expiresIn: TimeSpan.FromDays(1));
        await db.SaveChangesAsync(ct);

        await SendAsync(acknowledged, ct);
    }

    /// <summary>Makes, encrypts and sends the consented records, then reports to the gateway. Runs once per transfer.</summary>
    public async Task TransferAsync(Guid transferId, CancellationToken ct)
    {
        var transfer = await db.Set<AbdmTransfer>().FirstOrDefaultAsync(t => t.Id == transferId, ct);
        if (transfer is null || transfer.Status != AbdmTransferStatus.Acknowledged)
        {
            return;
        }

        var consent = await db.Set<AbdmConsent>().AsNoTracking().FirstAsync(c => c.ConsentId == transfer.ConsentId, ct);
        var references = JsonSerializer.Deserialize<List<string>>(consent.CareContextsJson, AbdmGatewayMessages.Json) ?? [];
        // The consent may have been revoked, or reached its erase date, since the request was acknowledged.
        var ended = consent.Status != Granted ? $"The consent is {consent.Status.ToLowerInvariant()}."
            : consent.DataEraseAt is { } erase && erase <= time.GetUtcNow() ? "Consent EXPIRED: exceeded dataEraseAt"
            : null;
        if (ended is not null)
        {
            references = [];
        }

        var linked = await db.Set<AbdmLinkedCareContext>().AsNoTracking()
            .Where(c => c.HipId == consent.HipId && c.AbhaAddress == consent.AbhaAddress && references.Contains(c.ReferenceNumber))
            .ToListAsync(ct);

        var statuses = new Dictionary<string, (string Status, string Description)>(StringComparer.Ordinal);
        var documents = new List<(string Reference, string Json)>();
        foreach (var reference in references)
        {
            var careContext = linked.FirstOrDefault(c => c.ReferenceNumber == reference);
            var located = careContext is null ? null : await records.LocateAsync(careContext.RecordType, careContext.RecordId, ct);
            if (careContext is null || located is null || located.IsFailure)
            {
                statuses[reference] = ("ERRORED", "The record is no longer at the hospital.");
                continue;
            }

            if ((transfer.DateFrom is { } from && located.Value.RecordedAt < from) || (transfer.DateTo is { } to && located.Value.RecordedAt > to))
            {
                statuses[reference] = ("ERRORED", "The record is outside the dates asked for.");
                continue;
            }

            var shared = await bundles.ShareAsync(careContext.RecordType, careContext.RecordId, ct);
            if (shared.IsFailure)
            {
                statuses[reference] = ("ERRORED", shared.Error!.Message);
                continue;
            }

            documents.Add((reference, FhirJson.Serialize(shared.Value.Bundle)));
        }

        var pagesSent = 0;
        var pages = 0;
        if (documents.Count > 0)
        {
            var hiu = JsonSerializer.Deserialize<AbdmGatewayMessages.KeyMaterial>(transfer.KeyMaterialJson, AbdmGatewayMessages.Json)!;
            var encrypted = cipher.Encrypt(documents, new AbdmKeyMaterial(hiu.CryptoAlg!, hiu.Curve!, hiu.DhPublicKey!.KeyValue!, hiu.Nonce!));
            if (encrypted.IsFailure)
            {
                foreach (var (reference, _) in documents)
                {
                    statuses[reference] = ("ERRORED", encrypted.Error!.Message);
                }
            }
            else
            {
                var (entries, hip) = encrypted.Value;
                var keyMaterial = new AbdmGatewayMessages.KeyMaterial(
                    hip.CryptoAlgorithm, hip.Curve, new AbdmGatewayMessages.DhPublicKey(hiu.DhPublicKey.Expiry, Fidelius.KeyParameters, hip.PublicKey), hip.Nonce);
                var chunks = entries.Chunk(PageSize).ToList();
                pages = chunks.Count;
                AbdmDataPush.IsAllowed(transfer.DataPushUrl, out var url);
                for (var page = 0; page < chunks.Count; page++)
                {
                    var pushed = await push.PushAsync(url!, new AbdmGatewayMessages.DataPush(
                        page,
                        chunks.Count,
                        transfer.TransactionId,
                        [.. chunks[page].Select(e => new AbdmGatewayMessages.PushEntry(e.Content, e.Media, e.Checksum, e.CareContextReference))],
                        keyMaterial), ct);
                    pagesSent += pushed ? 1 : 0;
                    foreach (var entry in chunks[page])
                    {
                        statuses[entry.CareContextReference] = pushed ? ("DELIVERED", "Delivered") : ("ERRORED", "The HIU did not accept the page.");
                    }
                }
            }
        }

        var now = time.GetUtcNow();
        var delivered = statuses.Where(s => s.Value.Status == "DELIVERED").Select(s => s.Key).ToList();
        var transferred = pages > 0 && pagesSent == pages;
        var results = ended is not null
            ? JsonSerializer.Deserialize<List<string>>(consent.CareContextsJson, AbdmGatewayMessages.Json)!
                .Select(r => new AbdmGatewayMessages.CareContextStatus(r, "ERRORED", ended)).ToList()
            : references.Select(r => new AbdmGatewayMessages.CareContextStatus(r, statuses[r].Status, statuses[r].Description)).ToList();
        transfer.Status = transferred ? AbdmTransferStatus.Transferred : AbdmTransferStatus.Failed;
        transfer.CompletedAt = now;
        transfer.ResultsJson = JsonSerializer.Serialize(results, AbdmGatewayMessages.Json);
        transfer.Error = transferred ? null : Truncate(results.FirstOrDefault(r => r.HiStatus != "DELIVERED")?.Description ?? "Nothing to send.", 500);
        await audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, Shared, $"Records sent through ABDM under the patient's consent: {delivered.Count}")
        {
            PatientId = transfer.PatientId,
            EntityType = "abdm.transfer",
            EntityId = transfer.TransactionId,
            Details = new { api = "abdm", consent = consent.ConsentId, hiu = consent.HiuId, delivered, failed = results.Count - delivered.Count },
        }, ct);
        await db.SaveChangesAsync(ct);

        var notified = await gateway.NotifyTransferAsync(new AbdmGatewayMessages.TransferNotify(new AbdmGatewayMessages.TransferNotification(
            consent.ConsentId,
            transfer.TransactionId,
            HttpAbdmGatewayClient.Timestamp(now),
            new AbdmGatewayMessages.Notifier("HIP", transfer.HipId),
            new AbdmGatewayMessages.StatusNotification(transferred ? "TRANSFERRED" : "FAILED", transfer.HipId, results))), ct);
        if (notified.IsFailure)
        {
            log.LogWarning("The ABDM gateway was not told of transfer {Transaction}: {Reason}", transfer.TransactionId, notified.Error!.Message);
        }
    }

    /// <summary>Why the HIU's key material cannot be used, or null: ECDH on Curve25519, a key, a nonce, not expired.</summary>
    private static string? KeyProblem(AbdmGatewayMessages.KeyMaterial? key, DateTimeOffset now) =>
        key is null || !string.Equals(key.CryptoAlg, Fidelius.CryptoAlgorithm, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(key.Curve, Fidelius.Curve, StringComparison.OrdinalIgnoreCase)
            ? "The key material must be ECDH on Curve25519."
        : string.IsNullOrWhiteSpace(key.DhPublicKey?.KeyValue) || string.IsNullOrWhiteSpace(key.Nonce) ? "The key material has no public key or nonce."
        : Instant(key.DhPublicKey.Expiry) is { } expiry && expiry <= now ? "The HIU's public key has expired."
        : null;

    /// <summary>An ABDM timestamp (ISO 8601, in UTC when it names no offset), or null.</summary>
    public static DateTimeOffset? Instant(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;

    private static string? Truncate(string? text, int length) =>
        string.IsNullOrEmpty(text) ? null : text.Length > length ? text[..length] : text;

    private async Task SendAsync(AbdmGatewayMessages.OnConsentNotify answer, CancellationToken ct) =>
        Log(await gateway.AnswerConsentNotifyAsync(answer, ct), "consent notify");

    private async Task SendAsync(AbdmGatewayMessages.OnHealthInformationRequest answer, CancellationToken ct) =>
        Log(await gateway.AnswerHealthInformationRequestAsync(answer, ct), "health-information request");

    private void Log(Result sent, string step)
    {
        if (sent.IsFailure)
        {
            log.LogWarning("The answer to the ABDM gateway's {Step} call was not delivered: {Reason}", step, sent.Error!.Message);
        }
    }
}

/// <summary>Carries out a transfer the request acknowledged (<see cref="AbdmDataFlowService.TransferAsync"/>).</summary>
internal sealed class AbdmTransferOutboxHandler(AbdmDataFlowService flow) : IOutboxHandler
{
    public string Type => AbdmDataFlowService.TransferType;

    public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken) =>
        flow.TransferAsync(message.Read<AbdmTransferJob>().TransferId, cancellationToken);
}
