using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>A record received from another provider, as listed for staff (without its content).</summary>
public sealed record AbdmReceivedRecordSummary(
    Guid Id,
    string ArtefactId,
    string? HipName,
    string CareContextReference,
    string? Title,
    DateTimeOffset? RecordDate,
    DateTimeOffset ReceivedAt,
    bool ChecksumMatches,
    string? Error);

/// <summary>A received record with its FHIR bundle, as a doctor opens it.</summary>
public sealed record AbdmReceivedRecordView(AbdmReceivedRecordSummary Record, JsonElement? Bundle, DateTimeOffset EraseAt);

/// <summary>
/// The hospital as HIU receiving the records a patient consented to (ABDM M3 data flow), following NHA's wrapper but
/// not its mistakes (one key pair per process, a success reported as a failure, the checksum never looked at):
/// <list type="number">
/// <item><b>Request</b> (<see cref="RequestAsync"/>): for a granted artefact, a fresh Fidelius key pair and nonce for this
/// request alone, the private key field-encrypted; <c>POST /data-flow/v3/health-information/request</c> with the
/// artefact's dates, the hospital's <c>dataPushUrl</c> and the public key, valid until the artefact's erase date. The
/// consent manager answers at on-request with the transaction id (<see cref="OnRequestAsync"/>).</item>
/// <item><b>Push</b> (<see cref="ReceiveAsync"/>): the provider posts pages naming the transaction. Each is checked
/// (a transaction awaiting pages, the consent still granted and not past its erase date, the page within the count
/// first announced, ECDH on Curve25519), each entry decrypted with the request's key and the provider's, its care context
/// checked against the artefact, its content read as a FHIR bundle and its MD5 checksum compared. What cannot be read is
/// kept as an error, so the provider is told.</item>
/// <item><b>Notify</b>: when every page has arrived, the private key is dropped and the consent manager told which care
/// contexts arrived (<c>OK</c>) and which could not be read (<c>ERRORED</c>), <c>TRANSFERRED</c> when any arrived.</item>
/// <item><b>Erase</b> (<see cref="EraseAsync"/>): the records are deleted outright when the patient revokes the
/// consent, when it expires, and at its erase date (<see cref="AbdmReceivedRecordEraser"/>); none is shown past it.</item>
/// </list>
/// </summary>
internal sealed class AbdmHealthInformationService(
    HospitalDbContext db,
    AbdmGatewayConfiguration configuration,
    IAbdmGatewayClient gateway,
    IFieldProtector protector,
    IAuditLogger audit,
    TimeProvider time,
    ILogger<AbdmHealthInformationService> log)
{
    public const string Requested = "abdm.records_requested";
    public const string Received = "abdm.records_received";
    public const string Viewed = "abdm.records_viewed";
    public const string Erased = "abdm.records_erased";

    public const string Granted = "GRANTED";
    public const string FhirMedia = "application/fhir+json";

    /// <summary>Pages a provider may announce; NHA's wrapper sends seven documents a page.</summary>
    public const int MaxPages = 100;

    /// <summary>A request the consent manager has not answered in this time may be asked again.</summary>
    public static readonly TimeSpan Pending = TimeSpan.FromMinutes(15);

    /// <summary>Asks for the records under the patient's granted artefact again, for a doctor.</summary>
    public async Task<Result<Guid>> RequestAsync(Guid patientId, string artefactId, CancellationToken ct)
    {
        var artefact = await db.Set<AbdmConsentArtefact>().FirstOrDefaultAsync(a => a.PatientId == patientId && a.ArtefactId == artefactId, ct);
        return artefact is null
            ? Error.NotFound("abdm.artefact_not_found", "No consent of this patient's has that id.")
            : await RequestAsync(artefact, ct);
    }

    /// <summary>Asks the consent manager for the records under a granted artefact, with a key pair of this request's own.</summary>
    public async Task<Result<Guid>> RequestAsync(AbdmConsentArtefact artefact, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (artefact.Status != Granted || artefact.FetchedAt is null || artefact.DateFrom is null || artefact.DateTo is null)
        {
            return Error.Rule("abdm.artefact_not_granted", "The patient's consent for these records is not granted (or not yet fetched).");
        }

        if (artefact.DataEraseAt is not { } eraseAt || eraseAt <= now)
        {
            return Error.Rule("abdm.artefact_expired", "The patient's consent for these records has passed its erase date.");
        }

        var (settings, _, _) = await configuration.ReadAsync(ct);
        if (string.IsNullOrWhiteSpace(settings.DataPushUrl))
        {
            return Error.Rule("abdm.no_data_push_url", "No data-push URL is set (\"dataPushUrl\" in abdm.gateway): other providers would have nowhere to send the records.");
        }

        var cutoff = now - Pending;
        if (await db.Set<AbdmHealthInformationRequest>().AnyAsync(r => r.ArtefactEntityId == artefact.Id
                && ((r.Status == AbdmHealthInformationStatus.Sent && r.CreatedAt > cutoff) || (r.Status == AbdmHealthInformationStatus.Requested && r.CreatedAt > cutoff)), ct))
        {
            return Error.Conflict("abdm.records_pending", "These records were asked for a few minutes ago; wait for them to arrive.");
        }

        var keys = Fidelius.Generate();
        var request = new AbdmHealthInformationRequest
        {
            ArtefactEntityId = artefact.Id,
            PatientId = artefact.PatientId,
            GatewayRequestId = Guid.CreateVersion7(),
            Status = AbdmHealthInformationStatus.Sent,
            PrivateKeyCipher = protector.Protect(Convert.ToBase64String(keys.PrivateKey)),
            Nonce = keys.Nonce,
            KeyExpiresAt = eraseAt,
        };
        db.Add(request);
        audit.Add(new AuditEntry(AuditCategories.PhiAccess, Requested, $"Records asked for from {artefact.HipName ?? artefact.HipId ?? "another provider"} through ABDM")
        {
            PatientId = artefact.PatientId,
            EntityType = "abdm.consent_artefact",
            EntityId = artefact.ArtefactId,
            Details = new { api = "abdm", hip = artefact.HipId, from = artefact.DateFrom, to = artefact.DateTo },
        });

        // Saved before the call, so an on-request that comes back first finds it.
        await db.SaveChangesAsync(ct);

        var sent = await gateway.RequestHealthInformationAsync(
            new AbdmGatewayMessages.HiuHealthInformationRequest(
                request.GatewayRequestId.ToString("D"),
                HttpAbdmGatewayClient.Timestamp(now),
                new AbdmGatewayMessages.HiRequest(
                    new AbdmGatewayMessages.IdRef(artefact.ArtefactId),
                    new AbdmGatewayMessages.DateRange(HttpAbdmGatewayClient.Timestamp(artefact.DateFrom.Value), HttpAbdmGatewayClient.Timestamp(artefact.DateTo.Value)),
                    settings.DataPushUrl,
                    new AbdmGatewayMessages.KeyMaterial(
                        Fidelius.CryptoAlgorithm,
                        Fidelius.Curve,
                        new AbdmGatewayMessages.DhPublicKey(HttpAbdmGatewayClient.Timestamp(eraseAt), Fidelius.KeyParameters, keys.PublicKey),
                        keys.Nonce))),
            request.GatewayRequestId,
            ct);
        if (sent.IsFailure)
        {
            End(request, AbdmHealthInformationStatus.Failed, sent.Error!.Message);
            await db.SaveChangesAsync(ct);
            return sent.Error!;
        }

        return request.Id;
    }

    /// <summary>The consent manager's answer: the transaction the provider's pages will name, or why not.</summary>
    public async Task OnRequestAsync(AbdmGatewayMessages.HiuOnHealthInformationRequest answer, CancellationToken ct)
    {
        if (!Guid.TryParse(answer.Response?.RequestId, out var requestId)
            || await db.Set<AbdmHealthInformationRequest>().FirstOrDefaultAsync(r => r.GatewayRequestId == requestId, ct) is not { } request)
        {
            log.LogWarning("Ignored an on-request for no request for records of this hospital's");
            return;
        }

        if (request.Status != AbdmHealthInformationStatus.Sent)
        {
            return;
        }

        if (answer.Error is null && answer.HiRequest?.TransactionId?.Trim() is { Length: > 0 and <= 64 } transactionId)
        {
            request.TransactionId = transactionId;
            request.Status = AbdmHealthInformationStatus.Requested;
        }
        else
        {
            End(request, AbdmHealthInformationStatus.Failed, Describe(answer.Error) ?? "The consent manager did not pass the request on.");
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>A page of encrypted records posted by the provider; refused (and nothing kept) when it names no transfer awaiting pages.</summary>
    public async Task<Result> ReceiveAsync(AbdmGatewayMessages.ReceivedPush push, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var transactionId = push.TransactionId?.Trim();
        if (string.IsNullOrEmpty(transactionId) || transactionId.Length > 64)
        {
            return Error.Validation("abdm.push_transaction", "The page names no transaction.");
        }

        var request = await db.Set<AbdmHealthInformationRequest>().FirstOrDefaultAsync(r => r.TransactionId == transactionId, ct);
        if (request is null || request.Status != AbdmHealthInformationStatus.Requested || request.PrivateKeyCipher is null)
        {
            return Error.NotFound("abdm.push_unknown", "No transfer awaiting pages has that transaction id.");
        }

        var artefact = await db.Set<AbdmConsentArtefact>().FirstAsync(a => a.Id == request.ArtefactEntityId, ct);
        if (artefact.Status != Granted || artefact.DataEraseAt is not { } eraseAt || eraseAt <= now || request.KeyExpiresAt <= now)
        {
            return Error.Rule("abdm.push_consent", "The patient's consent for these records is no longer granted.");
        }

        if (push.PageNumber is not { } page || push.PageCount is not { } count || count is < 1 or > MaxPages || page < 1 || page > count
            || (request.PageCount is { } announced && announced != count))
        {
            return Error.Validation("abdm.push_page", "The page number or count is not valid for this transfer.");
        }

        var key = push.KeyMaterial;
        if (key?.DhPublicKey?.KeyValue is not { Length: > 0 } hipPublicKey || key.Nonce is not { Length: > 0 } hipNonce
            || !string.Equals(key.CryptoAlg, Fidelius.CryptoAlgorithm, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(key.Curve, Fidelius.Curve, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("abdm.push_key", "The page carries no ECDH Curve25519 key material.");
        }

        if (push.Entries is not { Count: > 0 } entries)
        {
            return Error.Validation("abdm.push_entries", "The page carries no records.");
        }

        if (await db.Set<AbdmReceivedRecord>().AnyAsync(r => r.RequestEntityId == request.Id && r.PageNumber == page, ct))
        {
            // The provider sent the page again: it is kept once.
            return Result.Success();
        }

        var careContexts = (JsonSerializer.Deserialize<List<AbdmGatewayMessages.ConsentCareContext>>(artefact.CareContextsJson, AbdmGatewayMessages.Json) ?? [])
            .Select(c => c.CareContextReference).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var privateKey = Convert.FromBase64String(protector.Unprotect(request.PrivateKeyCipher)!);
        var records = new List<AbdmReceivedRecord>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            records.Add(Read(entries[i], i, page, request, careContexts, privateKey, hipPublicKey, hipNonce));
        }

        request.PageCount ??= count;
        db.AddRange(records);
        audit.Add(new AuditEntry(AuditCategories.PhiAccess, Received, $"Records received from {artefact.HipName ?? artefact.HipId ?? "another provider"} through ABDM")
        {
            PatientId = request.PatientId,
            EntityType = "abdm.consent_artefact",
            EntityId = artefact.ArtefactId,
            Details = new { api = "abdm", page, count, read = records.Count(r => r.Error is null), unreadable = records.Count(r => r.Error is not null) },
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The same page posted twice at once: the other copy was kept.
            return Result.Success();
        }

        var pages = await db.Set<AbdmReceivedRecord>().Where(r => r.RequestEntityId == request.Id).Select(r => r.PageNumber).Distinct().CountAsync(ct);
        if (pages == count)
        {
            await CompleteAsync(request.Id, artefact, transactionId, ct);
        }

        return Result.Success();
    }

    /// <summary>The records received for the patient, newest first, while their consents last.</summary>
    public async Task<IReadOnlyList<AbdmReceivedRecordSummary>> ListAsync(Guid patientId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var rows = await (
            from r in db.Set<AbdmReceivedRecord>().AsNoTracking()
            join q in db.Set<AbdmHealthInformationRequest>() on r.RequestEntityId equals q.Id
            join a in db.Set<AbdmConsentArtefact>() on q.ArtefactEntityId equals a.Id
            where r.PatientId == patientId && a.Status == Granted && a.DataEraseAt > now
            orderby r.RecordDate descending, r.CreatedAt descending
            select new { r, a.ArtefactId, a.HipName }).Take(200).ToListAsync(ct);
        return [.. rows.Select(x => Summary(x.r, x.ArtefactId, x.HipName))];
    }

    /// <summary>A received record with its bundle, for a doctor; audited as a read of the patient's records.</summary>
    public async Task<Result<AbdmReceivedRecordView>> ReadAsync(Guid patientId, Guid recordId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var row = await (
            from r in db.Set<AbdmReceivedRecord>().AsNoTracking()
            join q in db.Set<AbdmHealthInformationRequest>() on r.RequestEntityId equals q.Id
            join a in db.Set<AbdmConsentArtefact>() on q.ArtefactEntityId equals a.Id
            where r.Id == recordId && r.PatientId == patientId && a.Status == Granted && a.DataEraseAt > now
            select new { r, a.ArtefactId, a.HipName, a.DataEraseAt }).FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return Error.NotFound("abdm.record_not_found", "No record received for this patient has that id, or its consent has ended.");
        }

        await audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, Viewed, $"Record from {row.HipName ?? "another provider"} received through ABDM opened")
        {
            PatientId = patientId,
            EntityType = "abdm.received_record",
            EntityId = recordId.ToString("D"),
            Details = new { api = "abdm", artefact = row.ArtefactId, careContext = row.r.CareContextReference },
        }, ct);

        JsonElement? bundle = null;
        if (row.r.Content is { } content)
        {
            using var document = JsonDocument.Parse(content);
            bundle = document.RootElement.Clone();
        }

        return new AbdmReceivedRecordView(Summary(row.r, row.ArtefactId, row.HipName), bundle, row.DataEraseAt!.Value);
    }

    /// <summary>Deletes outright the records received under these artefacts, and drops any key still kept for them.</summary>
    public async Task<int> EraseAsync(IReadOnlyCollection<Guid> artefactIds, string reason, CancellationToken ct)
    {
        if (artefactIds.Count == 0)
        {
            return 0;
        }

        var requests = db.Set<AbdmHealthInformationRequest>().IgnoreQueryFilters().Where(q => artefactIds.Contains(q.ArtefactEntityId));
        var patients = await db.Set<AbdmReceivedRecord>().IgnoreQueryFilters()
            .Where(r => requests.Any(q => q.Id == r.RequestEntityId))
            .GroupBy(r => r.PatientId).Select(g => new { PatientId = g.Key, Count = g.Count() }).ToListAsync(ct);
        var erased = await db.Set<AbdmReceivedRecord>().IgnoreQueryFilters()
            .Where(r => requests.Any(q => q.Id == r.RequestEntityId))
            .ExecuteDeleteAsync(ct);
        await requests.Where(q => q.PrivateKeyCipher != null)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.PrivateKeyCipher, (string?)null), ct);
        foreach (var patient in patients)
        {
            audit.Add(new AuditEntry(AuditCategories.PhiAccess, Erased, $"{patient.Count} record(s) received through ABDM erased: {reason}")
            {
                PatientId = patient.PatientId,
                EntityType = "abdm.received_record",
                Details = new { api = "abdm", erased = patient.Count, reason },
            });
        }

        if (patients.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return erased;
    }

    /// <summary>Erases what was received under artefacts past their erase date; run by <see cref="AbdmReceivedRecordEraser"/>.</summary>
    public async Task<int> EraseExpiredAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var due = await db.Set<AbdmConsentArtefact>()
            .Where(a => a.DataEraseAt <= now && db.Set<AbdmHealthInformationRequest>().Any(q => q.ArtefactEntityId == a.Id
                && (q.PrivateKeyCipher != null || db.Set<AbdmReceivedRecord>().Any(r => r.RequestEntityId == q.Id))))
            .Select(a => a.Id).ToListAsync(ct);
        return await EraseAsync(due, "the consent's erase date passed", ct);
    }

    private AbdmReceivedRecord Read(
        AbdmGatewayMessages.ReceivedEntry entry, int index, int page, AbdmHealthInformationRequest request,
        HashSet<string> careContexts, byte[] privateKey, string hipPublicKey, string hipNonce)
    {
        var record = new AbdmReceivedRecord
        {
            RequestEntityId = request.Id,
            PatientId = request.PatientId,
            CareContextReference = Clip(entry.CareContextReference?.Trim(), 100) ?? string.Empty,
            PageNumber = page,
            EntryIndex = index,
        };

        string? problem = null;
        if (record.CareContextReference.Length == 0 || !careContexts.Contains(record.CareContextReference))
        {
            problem = "The record names a care context the patient's consent does not cover.";
        }
        else if (string.IsNullOrEmpty(entry.Content))
        {
            problem = entry.Link is { Length: > 0 } ? "The record was sent as a link, which HC360 does not follow." : "The record has no content.";
        }
        else if (entry.Media is { Length: > 0 } media && !media.StartsWith(FhirMedia, StringComparison.OrdinalIgnoreCase))
        {
            problem = $"The record is {Clip(media, 60)}, not a FHIR bundle.";
        }

        if (problem is null)
        {
            var decrypted = Fidelius.Decrypt(entry.Content!, privateKey, request.Nonce, hipPublicKey, hipNonce);
            if (decrypted.IsFailure)
            {
                problem = decrypted.Error!.Message;
            }
            else
            {
                problem = ReadBundle(decrypted.Value, record);
                if (problem is null)
                {
                    record.Content = decrypted.Value;
                    record.ChecksumMatches = string.Equals(entry.Checksum?.Trim(), Md5(decrypted.Value), StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        record.Error = Clip(problem, 500);
        return record;
    }

    /// <summary>Null when the text is a FHIR bundle, with its title and date taken; else what is wrong.</summary>
    private static string? ReadBundle(string json, AbdmReceivedRecord record)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("resourceType", out var type) || type.GetString() != "Bundle")
            {
                return "The record is not a FHIR bundle.";
            }

            JsonElement? composition = null;
            if (root.TryGetProperty("entry", out var bundleEntries) && bundleEntries.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in bundleEntries.EnumerateArray())
                {
                    if (e.TryGetProperty("resource", out var resource) && resource.ValueKind == JsonValueKind.Object
                        && resource.TryGetProperty("resourceType", out var rt) && rt.GetString() == "Composition")
                    {
                        composition = resource;
                        break;
                    }
                }
            }

            record.Title = Clip(Text(composition, "title") ?? CodeText(composition) ?? "Health record", 200);
            record.RecordDate = AbdmDataFlowService.Instant(Text(composition, "date") ?? Text(root, "timestamp"));
            return null;
        }
        catch (JsonException)
        {
            return "The record is not valid JSON.";
        }
    }

    private static string? Text(JsonElement? element, string property) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? CodeText(JsonElement? composition)
    {
        if (composition is not { ValueKind: JsonValueKind.Object } c || !c.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return Text(type, "text") ?? (type.TryGetProperty("coding", out var coding) && coding.ValueKind == JsonValueKind.Array
            ? coding.EnumerateArray().Select(x => Text(x, "display")).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d))
            : null);
    }

    /// <summary>
    /// Marks the transfer received (once, however many pages arrive together), drops its private key, and tells the
    /// consent manager what arrived per care context.
    /// </summary>
    private async Task CompleteAsync(Guid requestId, AbdmConsentArtefact artefact, string transactionId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var completed = await db.Set<AbdmHealthInformationRequest>()
            .Where(r => r.Id == requestId && r.Status == AbdmHealthInformationStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, AbdmHealthInformationStatus.Received)
                .SetProperty(r => r.CompletedAt, now)
                .SetProperty(r => r.PrivateKeyCipher, (string?)null), ct);
        if (completed == 0)
        {
            return;
        }

        var received = await db.Set<AbdmReceivedRecord>().AsNoTracking().Where(r => r.RequestEntityId == requestId)
            .Select(r => new { r.CareContextReference, r.Error }).ToListAsync(ct);
        var statuses = received
            .GroupBy(r => r.CareContextReference, StringComparer.Ordinal)
            .Select(g => g.All(r => r.Error is null)
                ? new AbdmGatewayMessages.CareContextStatus(g.Key, "OK", "Received")
                : new AbdmGatewayMessages.CareContextStatus(g.Key, "ERRORED", g.First(r => r.Error is not null).Error!))
            .ToList();
        var (settings, _, _) = await configuration.ReadAsync(ct);
        var notified = await gateway.NotifyReceivedAsync(new AbdmGatewayMessages.TransferNotify(new AbdmGatewayMessages.TransferNotification(
            artefact.ArtefactId,
            transactionId,
            HttpAbdmGatewayClient.Timestamp(now),
            new AbdmGatewayMessages.Notifier("HIU", settings.HiuIdentity ?? string.Empty),
            new AbdmGatewayMessages.StatusNotification(
                statuses.Any(s => s.HiStatus == "OK") ? "TRANSFERRED" : "FAILED",
                artefact.HipId ?? string.Empty,
                statuses))), ct);
        if (notified.IsFailure)
        {
            log.LogWarning("The receipt of transfer {Transaction} was not reported: {Reason}", transactionId, notified.Error!.Message);
        }
    }

    private static void End(AbdmHealthInformationRequest request, AbdmHealthInformationStatus status, string error)
    {
        request.Status = status;
        request.PrivateKeyCipher = null;
        request.Error = Clip(error, 500);
    }

    private static AbdmReceivedRecordSummary Summary(AbdmReceivedRecord r, string artefactId, string? hipName) => new(
        r.Id, artefactId, hipName, r.CareContextReference, r.Title, r.RecordDate, r.CreatedAt, r.ChecksumMatches, r.Error);

    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Primitives",
        Justification = "ABDM's data push names each entry by the MD5 of its document; integrity comes from AES-GCM's tag.")]
    private static string Md5(string text) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));

    private static string? Describe(AbdmGatewayMessages.GatewayError? error) =>
        error is null ? null : string.Join(" ", new[] { error.Code, error.Message }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private static string? Clip(string? text, int length) =>
        string.IsNullOrEmpty(text) ? text : text.Length > length ? text[..length] : text;
}
