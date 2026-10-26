using System.Text.Json;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Organization;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>What the doctor asks the patient to share from other providers.</summary>
public sealed record AbdmConsentRequestInput(
    string Purpose, IReadOnlyList<string> HiTypes, DateOnly From, DateOnly To, DateOnly EraseAfter);

internal sealed class AbdmConsentRequestInputValidator : AbstractValidator<AbdmConsentRequestInput>
{
    public AbdmConsentRequestInputValidator(TimeProvider time)
    {
        RuleFor(x => x.Purpose).Must(p => AbdmConsentRequestService.Purposes.ContainsKey(p ?? string.Empty))
            .WithMessage("Choose why the records are needed.");
        RuleFor(x => x.HiTypes).NotEmpty().WithMessage("Choose at least one kind of record.");
        RuleForEach(x => x.HiTypes).Must(t => AbdmConsentRequestService.HiTypes.Contains(t)).WithMessage("That is not a kind of record ABDM shares.");
        RuleFor(x => x.HiTypes).Must(t => t is null || t.Distinct().Count() == t.Count).WithMessage("A kind of record is listed twice.");
        RuleFor(x => x.From).LessThanOrEqualTo(x => x.To).WithMessage("The dates are the wrong way round.");
        RuleFor(x => x.To).Must(to => to <= DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime.AddDays(1)))
            .WithMessage("Records cannot be asked for from the future.");
        RuleFor(x => x.EraseAfter).Must(d => d > DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime)
                && d <= DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime.AddYears(1)))
            .WithMessage("Keep the records for at most a year, ending after today.");
    }
}

/// <summary>
/// A consent artefact as staff see it: the provider, how many records it covers, and the latest request for them
/// (<paramref name="Transfer"/>, null before the first) with how many arrived.
/// </summary>
public sealed record AbdmConsentArtefactView(
    string ArtefactId, string Status, string? HipId, string? HipName, int Records, DateTimeOffset? FetchedAt,
    AbdmHealthInformationStatus? Transfer, string? TransferError, int Received);

/// <summary>A consent request as staff see it.</summary>
public sealed record AbdmConsentRequestView(
    Guid Id,
    Guid PatientId,
    AbdmConsentRequestStatus Status,
    string Purpose,
    IReadOnlyList<string> HiTypes,
    DateTimeOffset DateFrom,
    DateTimeOffset DateTo,
    DateTimeOffset DataEraseAt,
    string RequesterName,
    DateTimeOffset RequestedAt,
    DateTimeOffset? DecidedAt,
    string? Error,
    IReadOnlyList<AbdmConsentArtefactView> Artefacts);

/// <summary>
/// The hospital asking a patient for their records at other providers, as an HIU (ABDM M3), following NHA's wrapper:
/// <list type="number">
/// <item>The doctor asks (<see cref="RequestAsync"/>): <c>POST /consent/v3/request/init</c> with the patient's ABHA
/// address, the purpose, the kinds of records, the dates, the erase date and the doctor as requester (name and
/// medical-council registration). The consent manager answers at on-init with its id for the request.</item>
/// <item>The patient decides in their ABHA app; the consent manager notifies the hospital (<see cref="NotifyAsync"/>):
/// granted, with one artefact per provider, or denied; later revoked or expired. HC360 acknowledges
/// (<c>/consent/v3/request/hiu/on-notify</c>, one acknowledgement per artefact) and fetches each granted artefact
/// (<c>/consent/v3/fetch</c>), which comes back at on-fetch (<see cref="OnFetchAsync"/>) with the provider, the records
/// and the terms.</item>
/// </list>
/// A fetched artefact's records are then asked for, received and erased by <see cref="AbdmHealthInformationService"/>;
/// a revocation or expiry erases what was received under it.
/// </summary>
internal sealed class AbdmConsentRequestService(
    HospitalDbContext db,
    AbdmGatewayConfiguration configuration,
    IAbdmGatewayClient gateway,
    AbdmHealthInformationService records,
    ICurrentUser user,
    IAuditLogger audit,
    TimeProvider time,
    ILogger<AbdmConsentRequestService> log)
{
    public const string Requested = "abdm.consent_requested";
    public const string Decided = "abdm.consent_decided";

    /// <summary>The purposes of use a doctor may ask under, with ABDM's codes.</summary>
    public static readonly IReadOnlyDictionary<string, string> Purposes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CAREMGT"] = "Care Management",
        ["BTG"] = "Break the Glass",
        ["PATRQT"] = "Self Requested",
    };

    /// <summary>The kinds of health information ABDM shares.</summary>
    public static readonly IReadOnlySet<string> HiTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "OPConsultation", "Prescription", "DischargeSummary", "DiagnosticReport", "ImmunizationRecord", "HealthDocumentRecord", "WellnessRecord",
    };

    private const string PurposeSystem = "http://terminology.hl7.org/ValueSet/v3-PurposeOfUse";
    private const string RegistrationSystem = "https://www.nmc.org.in";

    public async Task<Result<AbdmConsentRequestView>> RequestAsync(Guid patientId, AbdmConsentRequestInput input, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == patientId, ct);
        if (patient is null)
        {
            return Error.NotFound("patient.not_found", "Patient not found.");
        }

        if (!patient.IsActive || patient.MergedIntoPatientId is not null)
        {
            return Error.Rule("abdm.consent_patient_inactive", "This record is inactive or merged into another: ask from the patient's current record.");
        }

        if (string.IsNullOrWhiteSpace(patient.AbhaAddress))
        {
            return Error.Rule("abdm.no_abha_address", "The patient has no ABHA address on record: add it before asking for their records.");
        }

        var practitioner = user.PractitionerId is { } practitionerId
            ? await db.Set<Practitioner>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == practitionerId, ct)
            : null;
        if (practitioner is null || string.IsNullOrWhiteSpace(practitioner.RegistrationNumber))
        {
            return Error.Rule("abdm.consent_requester", "Only a doctor with a medical-council registration number on their staff record can ask for a patient's records.");
        }

        var (settings, _, _) = await configuration.ReadAsync(ct);
        var now = time.GetUtcNow();
        var request = new AbdmConsentRequest
        {
            PatientId = patientId,
            AbhaAddress = patient.AbhaAddress,
            GatewayRequestId = Guid.CreateVersion7(),
            Status = AbdmConsentRequestStatus.Sent,
            Purpose = input.Purpose,
            HiTypes = string.Join(",", input.HiTypes),
            DateFrom = new DateTimeOffset(input.From.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            DateTo = new DateTimeOffset(input.To.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero),
            DataEraseAt = new DateTimeOffset(input.EraseAfter.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero),
            RequesterPractitionerId = practitioner.Id,
            RequesterName = practitioner.DisplayName,
        };
        db.Add(request);
        audit.Add(new AuditEntry(AuditCategories.PhiAccess, Requested, $"Patient asked through ABDM to share records from other providers: {request.HiTypes}")
        {
            PatientId = patientId,
            EntityType = "abdm.consent_request",
            EntityId = request.Id.ToString("D"),
            Details = new { api = "abdm", purpose = request.Purpose, hiTypes = input.HiTypes, from = input.From, to = input.To, eraseAfter = input.EraseAfter },
        });

        // Saved before the call, so an on-init that comes back first finds it.
        await db.SaveChangesAsync(ct);

        var sent = await gateway.RequestConsentAsync(new AbdmGatewayMessages.ConsentInit(
            request.GatewayRequestId.ToString("D"),
            HttpAbdmGatewayClient.Timestamp(now),
            new AbdmGatewayMessages.ConsentRequestBody(
                new AbdmGatewayMessages.Purpose(Purposes[input.Purpose], input.Purpose, PurposeSystem),
                new AbdmGatewayMessages.IdRef(patient.AbhaAddress),
                new AbdmGatewayMessages.IdRef(settings.HiuIdentity),
                new AbdmGatewayMessages.Requester(practitioner.DisplayName, new AbdmGatewayMessages.RequesterIdentifier("REGNO", practitioner.RegistrationNumber!, RegistrationSystem)),
                input.HiTypes,
                new AbdmGatewayMessages.Permission(
                    "VIEW",
                    new AbdmGatewayMessages.DateRange(HttpAbdmGatewayClient.Timestamp(request.DateFrom), HttpAbdmGatewayClient.Timestamp(request.DateTo)),
                    HttpAbdmGatewayClient.Timestamp(request.DataEraseAt),
                    new AbdmGatewayMessages.Frequency("HOUR", 1, 0)))),
            request.GatewayRequestId,
            ct);
        if (sent.IsFailure)
        {
            request.Status = AbdmConsentRequestStatus.Failed;
            request.Error = Clip(sent.Error!.Message, 500);
            await db.SaveChangesAsync(ct);
        }

        return await ViewAsync(request.Id, ct);
    }

    /// <summary>The patient's consent requests, newest first, with their artefacts.</summary>
    public async Task<IReadOnlyList<AbdmConsentRequestView>> ListAsync(Guid patientId, CancellationToken ct)
    {
        var requests = await db.Set<AbdmConsentRequest>().AsNoTracking()
            .Where(r => r.PatientId == patientId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(20)
            .ToListAsync(ct);
        var ids = requests.Select(r => r.Id).ToList();
        var artefacts = await db.Set<AbdmConsentArtefact>().AsNoTracking()
            .Where(a => ids.Contains(a.ConsentRequestEntityId))
            .ToListAsync(ct);
        var transfers = await TransfersAsync([.. artefacts.Select(a => a.Id)], ct);
        return [.. requests.Select(r => View(r, artefacts.Where(a => a.ConsentRequestEntityId == r.Id), transfers))];
    }

    /// <summary>The consent manager has the request (or refused it).</summary>
    public async Task OnInitAsync(AbdmGatewayMessages.ConsentOnInit answer, CancellationToken ct)
    {
        if (!Guid.TryParse(answer.Response?.RequestId, out var requestId)
            || await db.Set<AbdmConsentRequest>().FirstOrDefaultAsync(r => r.GatewayRequestId == requestId, ct) is not { } request)
        {
            log.LogWarning("Ignored an on-init for no consent request of this hospital's");
            return;
        }

        if (request.Status is not (AbdmConsentRequestStatus.Sent or AbdmConsentRequestStatus.Failed))
        {
            return;
        }

        if (answer.ConsentRequest?.Id is { Length: > 0 and <= 64 } id)
        {
            request.ConsentRequestId = id;
            request.Status = AbdmConsentRequestStatus.Requested;
            request.Error = null;
        }
        else
        {
            request.Status = AbdmConsentRequestStatus.Failed;
            request.Error = Clip(Describe(answer.Error) ?? "The consent manager did not take the request.", 500);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>The patient's decision, or a later revocation or expiry: kept, acknowledged, and granted artefacts fetched.</summary>
    public async Task NotifyAsync(AbdmGatewayMessages.HiuNotify call, string requestId, CancellationToken ct)
    {
        var notification = call.Notification;
        var consentRequestId = notification?.ConsentRequestId?.Trim();
        var request = string.IsNullOrEmpty(consentRequestId) ? null
            : await db.Set<AbdmConsentRequest>().FirstOrDefaultAsync(r => r.ConsentRequestId == consentRequestId, ct);
        if (request is null || notification?.Status is not { Length: > 0 } status)
        {
            log.LogWarning("Ignored a consent notification for no request of this hospital's");
            return;
        }

        var artefactIds = (notification.ConsentArtefacts ?? []).Select(a => a.Id?.Trim()).OfType<string>()
            .Where(id => id.Length is > 0 and <= 64).Distinct(StringComparer.Ordinal).ToList();
        var now = time.GetUtcNow();
        var kept = await db.Set<AbdmConsentArtefact>().Where(a => a.ConsentRequestEntityId == request.Id).ToListAsync(ct);
        var toFetch = new List<AbdmConsentArtefact>();
        var ended = new List<Guid>();
        switch (status.ToUpperInvariant())
        {
            case "GRANTED":
                request.Status = AbdmConsentRequestStatus.Granted;
                foreach (var id in artefactIds.Where(id => kept.All(a => a.ArtefactId != id)))
                {
                    var artefact = new AbdmConsentArtefact
                    {
                        ConsentRequestEntityId = request.Id,
                        PatientId = request.PatientId,
                        ArtefactId = id,
                        FetchRequestId = Guid.CreateVersion7(),
                    };
                    db.Add(artefact);
                    toFetch.Add(artefact);
                }

                break;
            case "DENIED":
                request.Status = AbdmConsentRequestStatus.Denied;
                break;
            case "REVOKED" or "EXPIRED":
                request.Status = status.Equals("REVOKED", StringComparison.OrdinalIgnoreCase) ? AbdmConsentRequestStatus.Revoked : AbdmConsentRequestStatus.Expired;
                foreach (var artefact in kept.Where(a => artefactIds.Count == 0 || artefactIds.Contains(a.ArtefactId)))
                {
                    artefact.Status = status.ToUpperInvariant();
                    ended.Add(artefact.Id);
                }

                break;
            default:
                log.LogWarning("Ignored a consent notification with status {Status}", status);
                return;
        }

        request.DecidedAt ??= now;
        audit.Add(new AuditEntry(AuditCategories.PhiAccess, Decided, $"ABDM consent request {request.Status.ToString().ToLowerInvariant()}")
        {
            PatientId = request.PatientId,
            EntityType = "abdm.consent_request",
            EntityId = request.Id.ToString("D"),
            Details = new { api = "abdm", status = request.Status.ToString(), artefacts = artefactIds },
        });
        await db.SaveChangesAsync(ct);

        // What was received under a revoked or expired consent is erased now, not at its erase date.
        await records.EraseAsync(ended, $"the patient's consent was {status.ToLowerInvariant()}", ct);

        var acknowledged = await gateway.AnswerHiuNotifyAsync(new AbdmGatewayMessages.HiuOnNotify(
            [.. artefactIds.Select(id => new AbdmGatewayMessages.Acknowledgement("OK", id))], new AbdmGatewayMessages.RespRequest(requestId), null), ct);
        if (acknowledged.IsFailure)
        {
            log.LogWarning("The consent decision was not acknowledged: {Reason}", acknowledged.Error!.Message);
        }

        foreach (var artefact in toFetch)
        {
            var fetched = await gateway.FetchConsentAsync(
                new AbdmGatewayMessages.ConsentFetch(artefact.FetchRequestId!.Value.ToString("D"), HttpAbdmGatewayClient.Timestamp(now), artefact.ArtefactId),
                artefact.FetchRequestId.Value,
                ct);
            if (fetched.IsFailure)
            {
                log.LogWarning("Consent artefact {Artefact} could not be fetched: {Reason}", artefact.ArtefactId, fetched.Error!.Message);
            }
        }
    }

    /// <summary>A granted artefact: the provider, the records and the terms, kept for asking for the records.</summary>
    public async Task OnFetchAsync(AbdmGatewayMessages.ConsentOnFetch answer, CancellationToken ct)
    {
        if (!Guid.TryParse(answer.Response?.RequestId, out var fetchId)
            || await db.Set<AbdmConsentArtefact>().FirstOrDefaultAsync(a => a.FetchRequestId == fetchId, ct) is not { } artefact)
        {
            log.LogWarning("Ignored an on-fetch for no consent artefact of this hospital's");
            return;
        }

        var detail = answer.Consent?.ConsentDetail;
        if (answer.Error is not null || detail is null)
        {
            log.LogWarning("Consent artefact {Artefact} was not fetched: {Reason}", artefact.ArtefactId, Describe(answer.Error) ?? "no artefact");
            return;
        }

        if (!string.Equals(detail.ConsentId, artefact.ArtefactId, StringComparison.Ordinal))
        {
            log.LogWarning("Ignored an on-fetch whose artefact {Fetched} is not the one asked for ({Artefact})", detail.ConsentId, artefact.ArtefactId);
            return;
        }

        artefact.Status = Clip(answer.Consent!.Status?.ToUpperInvariant(), 16) ?? "GRANTED";
        artefact.HipId = Clip(detail.Hip?.Id, 64);
        artefact.HipName = Clip(detail.Hip?.Name, 200);
        artefact.CareContextsJson = JsonSerializer.Serialize(
            (detail.CareContexts ?? []).Where(c => !string.IsNullOrWhiteSpace(c.CareContextReference)).ToList(), AbdmGatewayMessages.Json);
        artefact.HiTypes = Clip(string.Join(",", detail.HiTypes ?? []), 300);
        artefact.DateFrom = AbdmDataFlowService.Instant(detail.Permission?.DateRange?.From);
        artefact.DateTo = AbdmDataFlowService.Instant(detail.Permission?.DateRange?.To);
        artefact.DataEraseAt = AbdmDataFlowService.Instant(detail.Permission?.DataEraseAt);
        artefact.FetchedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        // The doctor asked for the records: they are asked for as soon as the artefact says where they are.
        if (artefact.Status == AbdmHealthInformationService.Granted)
        {
            var asked = await records.RequestAsync(artefact, ct);
            if (asked.IsFailure)
            {
                log.LogWarning("The records under consent artefact {Artefact} were not asked for: {Reason}", artefact.ArtefactId, asked.Error!.Message);
            }
        }
    }

    private async Task<AbdmConsentRequestView> ViewAsync(Guid id, CancellationToken ct)
    {
        var request = await db.Set<AbdmConsentRequest>().AsNoTracking().FirstAsync(r => r.Id == id, ct);
        var artefacts = await db.Set<AbdmConsentArtefact>().AsNoTracking().Where(a => a.ConsentRequestEntityId == id).ToListAsync(ct);
        return View(request, artefacts, await TransfersAsync([.. artefacts.Select(a => a.Id)], ct));
    }

    /// <summary>Per artefact, its latest request for records and how many records are kept from all its requests.</summary>
    private async Task<Dictionary<Guid, (AbdmHealthInformationStatus Status, string? Error, int Received)>> TransfersAsync(List<Guid> artefactIds, CancellationToken ct)
    {
        var latest = await db.Set<AbdmHealthInformationRequest>().AsNoTracking()
            .Where(q => artefactIds.Contains(q.ArtefactEntityId))
            .GroupBy(q => q.ArtefactEntityId)
            .Select(g => g.OrderByDescending(q => q.CreatedAt).First())
            .ToListAsync(ct);
        var received = await (
            from r in db.Set<AbdmReceivedRecord>().AsNoTracking()
            join q in db.Set<AbdmHealthInformationRequest>() on r.RequestEntityId equals q.Id
            where artefactIds.Contains(q.ArtefactEntityId) && r.Error == null
            group r by q.ArtefactEntityId into g
            select new { Artefact = g.Key, Count = g.Count() }).ToListAsync(ct);
        return latest.ToDictionary(
            q => q.ArtefactEntityId,
            q => (q.Status, q.Error, received.FirstOrDefault(x => x.Artefact == q.ArtefactEntityId)?.Count ?? 0));
    }

    private static AbdmConsentRequestView View(
        AbdmConsentRequest r, IEnumerable<AbdmConsentArtefact> artefacts, Dictionary<Guid, (AbdmHealthInformationStatus Status, string? Error, int Received)> transfers) => new(
        r.Id, r.PatientId, r.Status, r.Purpose, r.HiTypes.Split(',', StringSplitOptions.RemoveEmptyEntries), r.DateFrom, r.DateTo,
        r.DataEraseAt, r.RequesterName, r.CreatedAt, r.DecidedAt, r.Error,
        [.. artefacts.OrderBy(a => a.HipName).Select(a => new AbdmConsentArtefactView(
            a.ArtefactId, a.Status, a.HipId, a.HipName,
            (JsonSerializer.Deserialize<List<AbdmGatewayMessages.ConsentCareContext>>(a.CareContextsJson, AbdmGatewayMessages.Json) ?? []).Count,
            a.FetchedAt,
            transfers.TryGetValue(a.Id, out var t) ? t.Status : null,
            transfers.TryGetValue(a.Id, out var e) ? e.Error : null,
            transfers.TryGetValue(a.Id, out var n) ? n.Received : 0))]);

    private static string? Describe(AbdmGatewayMessages.GatewayError? error) =>
        error is null ? null : string.Join(" ", new[] { error.Code, error.Message }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private static string? Clip(string? text, int length) =>
        string.IsNullOrEmpty(text) ? text : text.Length > length ? text[..length] : text;
}
