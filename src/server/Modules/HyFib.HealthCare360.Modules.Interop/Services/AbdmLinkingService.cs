using System.Text.Json;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>A record to link: its kind and id, as the export names them.</summary>
public sealed record AbdmLinkRecordInput(AbdmRecordType Type, Guid Id);

/// <summary>Records of one patient to link to their ABHA address.</summary>
public sealed record AbdmLinkInput(IReadOnlyList<AbdmLinkRecordInput> Records);

public sealed record AbdmLinkedRecordView(AbdmRecordType Type, Guid Id, string ReferenceNumber, string Display);

/// <summary>A request to link records to a patient's ABHA and where it has got to.</summary>
public sealed record AbdmLinkView(
    Guid Id,
    Guid PatientId,
    string AbhaAddress,
    AbdmLinkStatus Status,
    IReadOnlyList<AbdmLinkedRecordView> Records,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? AnsweredAt,
    string? ErrorCode,
    string? Error);

internal sealed class AbdmLinkInputValidator : AbstractValidator<AbdmLinkInput>
{
    public const int MaxRecords = 50;

    public AbdmLinkInputValidator()
    {
        RuleFor(x => x.Records).NotEmpty().WithMessage("Choose at least one record to link.");
        RuleFor(x => x.Records).Must(r => r is null || r.Count <= MaxRecords).WithMessage($"Link at most {MaxRecords} records at a time.");
        RuleFor(x => x.Records).Must(r => r is null || r.Distinct().Count() == r.Count).WithMessage("A record is listed twice.");
        RuleForEach(x => x.Records).ChildRules(r =>
        {
            r.RuleFor(x => x.Type).IsInEnum();
            r.RuleFor(x => x.Id).NotEmpty();
        });
    }
}

/// <summary>
/// HIP-initiated linking (ABDM M2): records the hospital completed are linked to the patient's ABHA address as care
/// contexts, which the patient then sees in their PHR app and can consent to share. Each record must be one the export
/// would send (<see cref="AbdmRecordLoader.LocateAsync"/>: signed, released) and that the caller may open, and all must
/// be the same patient's, who must have an ABHA address and a year of birth.
/// <para>
/// The gateway answers asynchronously. With a link token already issued for the patient's ABHA address the care
/// contexts are sent at once; otherwise a token is asked for and they are sent when the gateway's
/// <c>on-generate-token</c> brings it. The gateway's <c>on_carecontext</c> then says whether they were linked. Each step
/// is saved before the call is made, so an answer that arrives first still finds its request.
/// </para>
/// <para>
/// Linking is idempotent before anything is sent, not only once the gateway has linked: a record already linked to the
/// ABHA address, or in a request still awaiting the gateway's answer, is left out of a new request
/// (<see cref="SortAsync"/>). The gateway is never asked twice for one record, which matters as it may refuse care
/// contexts already linked (NHA's mock gateway answers 208), and a refusal drops the kept link token. A kept token is
/// used for <see cref="LinkTokenLifetimeMonths"/> months after it was issued, as NHA's wrapper uses it; then a new one is
/// asked for.
/// </para>
/// </summary>
internal sealed class AbdmLinkingService(
    HospitalDbContext db,
    AbdmRecordLoader records,
    PatientAccessGuard guard,
    AbdmGatewayConfiguration configuration,
    DisabledAbdmGatewayClient refusal,
    IAbdmGatewayClient gateway,
    IFieldProtector protector,
    IAuditLogger audit,
    TimeProvider time,
    ILogger<AbdmLinkingService> log)
{
    public const string LinkRequested = "abdm.link_requested";

    /// <summary>Every record asked for is linked to the ABHA address already.</summary>
    public const string AlreadyLinkedCode = "abdm.already_linked";

    /// <summary>Every record asked for is in a request the gateway has not answered yet.</summary>
    public const string LinkPendingCode = "abdm.link_pending";

    /// <summary>
    /// The gateway did not answer within <see cref="AbdmGatewaySettings.AnswerTimeout"/> (<see cref="AbdmUnansweredRequests"/>).
    /// Unlike the gateway's own refusals, it is only HC360's guess: an answer that comes after it is still taken.
    /// </summary>
    public const string NoAnswerCode = "abdm.no_answer";

    /// <summary>
    /// How long a link token is used after the gateway issued it: six months, as NHA's wrapper keeps it
    /// (<c>Utils.setLinkTokenExpiry</c>). An older one is not sent: a new one is asked for, rather than a refusal that
    /// fails the request.
    /// </summary>
    public const int LinkTokenLifetimeMonths = 6;

    /// <summary>Whether a token issued at <paramref name="issuedAt"/> is still sent at <paramref name="now"/>.</summary>
    public static bool IsLinkTokenUsable(DateTimeOffset issuedAt, DateTimeOffset now) => now < issuedAt.AddMonths(LinkTokenLifetimeMonths);

    public async Task<Result<AbdmLinkView>> StartAsync(AbdmLinkInput input, CancellationToken ct)
    {
        if (await refusal.WhyNotAsync(ct) is { } off)
        {
            return off;
        }

        var linked = new List<AbdmLinkedRecord>();
        Guid? patientId = null;
        Guid facilityId = default;
        foreach (var record in input.Records)
        {
            var located = await records.LocateAsync(record.Type, record.Id, ct);
            if (located.IsFailure)
            {
                return located.Error!;
            }

            var source = located.Value;
            if (patientId is { } first && first != source.PatientId)
            {
                return Error.Rule("abdm.link_patients", "The records belong to different patients. Link each patient's records on their own.");
            }

            if (patientId is null && (await guard.EnsureCanOpenChartAsync(source.PatientId, ct)).Error is { } denied)
            {
                return denied.Type == ErrorType.Forbidden ? denied : AbdmRecordLoader.NotFound(record.Type, record.Id);
            }

            if (source.NotExportable is { } reason)
            {
                return Error.Rule("abdm.not_exportable", reason);
            }

            if (record.Type == AbdmRecordType.Prescription && await records.PrescriptionNotIssuedAsync(record.Id, ct) is { } notIssued)
            {
                return notIssued;
            }

            patientId = source.PatientId;
            facilityId = source.FacilityId;
            linked.Add(AbdmCareContexts.Describe(record.Type, record.Id, source.RecordedAt, source.Label));
        }

        return await LinkAsync(patientId!.Value, facilityId, linked, automatic: false, ct);
    }

    /// <summary>
    /// The hospital's own request (<see cref="AbdmAutoLinking"/>): the record a clinical event completed, linked as staff
    /// would link it but with no user, so with no check of anyone's access to the chart: a VIP patient's records and
    /// those in a sensitive category are left to staff. Waits while a prescription waits for its DSC signature, and
    /// while a link token for the patient's ABHA address is being asked for, so that one token serves every record of a
    /// discharge rather than each asking for its own. Nothing to do when the record cannot be linked, or is linked or
    /// awaiting the gateway's answer already.
    /// </summary>
    public async Task<AbdmAutoLinkResult> LinkAutomaticallyAsync(AbdmRecordType type, Guid id, CancellationToken ct)
    {
        if (await AbdmAutoLinking.OffAsync(configuration, refusal, ct) is { } off)
        {
            return AbdmAutoLinkResult.Nothing(off);
        }

        var located = await records.LocateAsync(type, id, ct);
        if (located.IsFailure)
        {
            return AbdmAutoLinkResult.Nothing(located.Error!.Message);
        }

        var source = located.Value;
        if (source.NotExportable is { } reason)
        {
            return AbdmAutoLinkResult.Nothing(reason);
        }

        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == source.PatientId)
            .Select(p => new { p.AbhaAddress, p.DateOfBirth, p.MergedIntoPatientId, p.IsVip, p.SensitiveCategories })
            .FirstOrDefaultAsync(ct);
        if (patient is null)
        {
            return AbdmAutoLinkResult.Nothing("The patient is not on record.");
        }

        if (AbdmAutoLinking.NotLinkedAutomatically(patient.AbhaAddress, patient.DateOfBirth, patient.MergedIntoPatientId, patient.IsVip, patient.SensitiveCategories)
            is { } notLinked)
        {
            return AbdmAutoLinkResult.Nothing(notLinked);
        }

        if (type == AbdmRecordType.Prescription && await records.PrescriptionNotIssuedAsync(id, ct) is { } notIssued)
        {
            return AbdmAutoLinkResult.Wait(AbdmAutoLinking.SignatureWait, notIssued.Message);
        }

        if (await Unscoped().AnyAsync(r => r.PatientId == source.PatientId && r.Status == AbdmLinkStatus.AwaitingToken, ct))
        {
            return AbdmAutoLinkResult.Wait(AbdmAutoLinking.TokenWait, "A link token for the patient's ABHA address is being asked for.");
        }

        var linked = await LinkAsync(source.PatientId, source.FacilityId, [AbdmCareContexts.Describe(type, id, source.RecordedAt, source.Label)], automatic: true, ct);
        return linked.IsSuccess ? AbdmAutoLinkResult.Requested(linked.Value) : AbdmAutoLinkResult.Nothing(linked.Error!.Message);
    }

    /// <summary>
    /// Links <paramref name="records"/>, located and exportable, all <paramref name="patientId"/>'s, to the patient's ABHA
    /// address, leaving out those already linked to it or awaiting the gateway's answer (and failing with
    /// <see cref="AlreadyLinkedCode"/> or <see cref="LinkPendingCode"/> when that leaves none). Staff's requests and the
    /// hospital's own (<paramref name="automatic"/>, after a clinical event) go the same way; the audit tells them apart.
    /// </summary>
    private async Task<Result<AbdmLinkView>> LinkAsync(Guid patientId, Guid facilityId, IReadOnlyList<AbdmLinkedRecord> records, bool automatic, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == patientId, ct);
        if (patient is null || patient.MergedIntoPatientId is not null)
        {
            return Error.Rule("abdm.link_merged", "This patient's records were merged into another UHID. Link them from the surviving patient.");
        }

        if (string.IsNullOrWhiteSpace(patient.AbhaAddress))
        {
            return Error.Rule("abdm.no_abha_address", "The patient has no ABHA address. Record it on the patient before linking their records.");
        }

        if (patient.DateOfBirth is null)
        {
            return Error.Rule("abdm.no_year_of_birth", "ABDM needs the patient's year of birth to link records. Record it on the patient first.");
        }

        var (settings, _, _) = await configuration.ReadAsync(ct);
        var address = patient.AbhaAddress.Trim();
        var (toSend, alreadyLinked, waiting) = await SortAsync(settings.HipId!, patient.Id, address, records, ct);
        if (toSend.Count == 0)
        {
            return waiting.Count > 0
                ? Error.Rule(LinkPendingCode,
                    $"ABDM has not answered an earlier request to link {(waiting.Count == 1 ? "this record" : "these records")} to {address} yet. "
                    + "If it does not answer, that request is marked failed and the record can be linked again.")
                : Error.Rule(AlreadyLinkedCode,
                    $"{(alreadyLinked.Count == 1 ? "This record is" : "These records are")} already linked to the patient's ABHA ({address}).");
        }

        var now = time.GetUtcNow();
        var request = new AbdmLinkRequest
        {
            FacilityId = facilityId,
            PatientId = patient.Id,
            AbhaAddress = address,
            CareContextsJson = JsonSerializer.Serialize(toSend, AbdmGatewayMessages.Json),
        };
        db.Add(request);

        var summary = automatic
            ? $"Records linked to the patient's ABHA automatically: {toSend.Count}"
            : $"Records linked to the patient's ABHA: {toSend.Count}";
        await audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, LinkRequested, summary)
        {
            PatientId = patient.Id,
            EntityType = "abdm.link",
            EntityId = request.Id.ToString("D"),
            Details = new { api = "abdm", records = toSend.Select(r => r.ReferenceNumber).ToArray(), automatic },
        }, ct);

        var token = await TokenAsync(settings.HipId!, request.AbhaAddress, now, ct);
        if (token is not null)
        {
            await SendCareContextsAsync(request, patient, settings.HipId!, token, now, ct);
        }
        else
        {
            request.Status = AbdmLinkStatus.AwaitingToken;
            request.TokenRequestId = Guid.CreateVersion7();
            request.SentAt = now;
            await db.SaveChangesAsync(ct);

            var asked = await gateway.RequestLinkTokenAsync(
                new AbdmLinkTokenRequest(request.AbhaAddress, patient.FullName, AbdmGatewayMessages.Gender(patient.Gender), patient.DateOfBirth.Value.Year),
                request.TokenRequestId.Value,
                ct);
            if (asked.IsFailure)
            {
                request.Fail(asked.Error!.Code, asked.Error.Message, time.GetUtcNow());
                await db.SaveChangesAsync(ct);
            }
        }

        return View(request);
    }

    public async Task<Result<AbdmLinkView>> GetAsync(Guid id, CancellationToken ct)
    {
        var request = await db.Set<AbdmLinkRequest>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
        {
            return Error.NotFound("ABDM link request", id);
        }

        return (await guard.EnsureCanOpenChartAsync(request.PatientId, ct)).Error is { } denied
            ? denied.Type == ErrorType.Forbidden ? denied : Error.NotFound("ABDM link request", id)
            : View(request);
    }

    /// <summary>
    /// The gateway's <c>on-generate-token</c>: the link token for the request it was asked under, kept for the ABHA
    /// address, and the care contexts sent with it. An answer to no request of this hospital's, or naming another ABHA
    /// address, is ignored. One that comes after the request was marked unanswered (<see cref="NoAnswerCode"/>) is taken
    /// all the same: the token is kept, and the records not linked or sent again since go with it.
    /// </summary>
    public async Task OnLinkTokenAsync(AbdmGatewayMessages.OnGenerateToken answer, CancellationToken ct)
    {
        if (!Guid.TryParse(answer.Response?.RequestId, out var requestId))
        {
            log.LogWarning("An ABDM link token arrived without the request it answers");
            return;
        }

        await AnswerAsync(() => TakeLinkTokenAsync(answer, requestId, ct));
    }

    private async Task TakeLinkTokenAsync(AbdmGatewayMessages.OnGenerateToken answer, Guid requestId, CancellationToken ct)
    {
        var request = await Unscoped().FirstOrDefaultAsync(r => r.TokenRequestId == requestId, ct);
        var late = request is { Status: AbdmLinkStatus.Failed, ErrorCode: NoAnswerCode, LinkRequestId: null };
        if (request is null || (request.Status != AbdmLinkStatus.AwaitingToken && !late))
        {
            log.LogWarning("An ABDM link token arrived for no waiting request ({RequestId})", requestId);
            return;
        }

        // The token is kept for the address it was issued for: one for another address is not this request's.
        if (!string.IsNullOrWhiteSpace(answer.AbhaAddress) && AbdmLinkToken.Normalize(answer.AbhaAddress) != AbdmLinkToken.Normalize(request.AbhaAddress))
        {
            log.LogWarning("An ABDM link token for another ABHA address arrived for request {RequestId}", requestId);
            return;
        }

        var now = time.GetUtcNow();
        if (answer.Error is { } error || string.IsNullOrWhiteSpace(answer.LinkToken))
        {
            request.Fail(answer.Error?.Code ?? "abdm.no_link_token", answer.Error?.Message ?? "The gateway issued no link token.", now);
            await db.SaveChangesAsync(ct);
            return;
        }

        var (settings, _, _) = await configuration.ReadAsync(ct);
        await KeepTokenAsync(settings.HipId!, request.AbhaAddress, answer.LinkToken, now, ct);

        if (late)
        {
            // Marked unanswered before the token came: the records linked since, or sent again in another request, stay out.
            var (toSend, _, _) = await SortAsync(settings.HipId!, request.PatientId, request.AbhaAddress, Records(request.CareContextsJson), ct);
            if (toSend.Count == 0)
            {
                await db.SaveChangesAsync(ct);
                log.LogInformation("A late ABDM link token was kept; the records of request {RequestId} were linked or sent again meanwhile", request.Id);
                return;
            }

            request.CareContextsJson = JsonSerializer.Serialize(toSend, AbdmGatewayMessages.Json);
            request.ErrorCode = null;
            request.Error = null;
            request.AnsweredAt = null;
        }

        var patient = await db.Set<Patient>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .FirstAsync(p => p.Id == request.PatientId, ct);
        await SendCareContextsAsync(request, patient, settings.HipId!, answer.LinkToken, now, ct);
    }

    /// <summary>
    /// The gateway's <c>on_carecontext</c>: whether the care contexts were linked. A refusal also drops the link token
    /// kept for the ABHA address, so the next request asks for a new one (it may have expired or been revoked). An answer
    /// that comes after the request was marked unanswered (<see cref="NoAnswerCode"/>) is taken all the same: the patient's
    /// PHR app shows what the gateway linked, and a consent naming it is honoured only if it is kept here.
    /// </summary>
    public async Task OnCareContextsAsync(AbdmGatewayMessages.OnAddCareContexts answer, CancellationToken ct)
    {
        if (!Guid.TryParse(answer.Response?.RequestId, out var requestId))
        {
            log.LogWarning("An ABDM linking answer arrived without the request it answers");
            return;
        }

        await AnswerAsync(() => TakeCareContextsAnswerAsync(answer, requestId, ct));
    }

    private async Task TakeCareContextsAnswerAsync(AbdmGatewayMessages.OnAddCareContexts answer, Guid requestId, CancellationToken ct)
    {
        var request = await Unscoped().FirstOrDefaultAsync(r => r.LinkRequestId == requestId, ct);
        if (request is null || (request.Status != AbdmLinkStatus.Sent && request is not { Status: AbdmLinkStatus.Failed, ErrorCode: NoAnswerCode }))
        {
            log.LogWarning("An ABDM linking answer arrived for no request awaiting one ({RequestId})", requestId);
            return;
        }

        var now = time.GetUtcNow();
        if (answer.Error is { } error)
        {
            request.Fail(error.Code ?? "abdm.link_refused", error.Message ?? "The gateway did not link the records.", now);
            var (settings, _, _) = await configuration.ReadAsync(ct);
            await DropTokenAsync(settings.HipId!, request.AbhaAddress, ct);
        }
        else
        {
            request.Status = AbdmLinkStatus.Linked;
            request.AnsweredAt = now;
            request.ErrorCode = null;
            request.Error = null;
            var (settings, _, _) = await configuration.ReadAsync(ct);
            var linked = Records(request.CareContextsJson);
            await AbdmCareContexts.AddAsync(db, settings.HipId!, request.PatientId, request.AbhaAddress, linked, AbdmLinkRoute.HipInitiated, now, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Takes a gateway answer once more if the request changed as it was taken: the sweeper marked it unanswered at that
    /// moment (<see cref="AbdmUnansweredRequests"/>). Read again, it is taken as a late answer, so the answer is not lost.
    /// </summary>
    private async Task AnswerAsync(Func<Task> take)
    {
        try
        {
            await take();
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            await take();
        }
    }

    /// <summary>
    /// Sorts <paramref name="records"/> into those to send, those already linked to the ABHA address, and those in a
    /// request of the patient's still awaiting the gateway's answer (in any facility: the gateway's answer to it decides).
    /// </summary>
    internal async Task<(List<AbdmLinkedRecord> ToSend, List<AbdmLinkedRecord> Linked, List<AbdmLinkedRecord> Waiting)> SortAsync(
        string hipId, Guid patientId, string abhaAddress, IReadOnlyList<AbdmLinkedRecord> records, CancellationToken ct)
    {
        var address = AbdmLinkToken.Normalize(abhaAddress);
        var references = records.Select(r => r.ReferenceNumber).ToList();
        var linked = (await db.Set<AbdmLinkedCareContext>().AsNoTracking()
            .Where(c => c.HipId == hipId && c.AbhaAddress == address && references.Contains(c.ReferenceNumber))
            .Select(c => c.ReferenceNumber)
            .ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var waitingJson = await Unscoped().AsNoTracking()
            .Where(r => r.PatientId == patientId && (r.Status == AbdmLinkStatus.AwaitingToken || r.Status == AbdmLinkStatus.Sent))
            .Select(r => r.CareContextsJson)
            .ToListAsync(ct);
        var waiting = waitingJson.SelectMany(Records).Select(r => r.ReferenceNumber).ToHashSet(StringComparer.Ordinal);
        return (
            [.. records.Where(r => !linked.Contains(r.ReferenceNumber) && !waiting.Contains(r.ReferenceNumber))],
            [.. records.Where(r => linked.Contains(r.ReferenceNumber))],
            [.. records.Where(r => !linked.Contains(r.ReferenceNumber) && waiting.Contains(r.ReferenceNumber))]);
    }

    /// <summary>The care contexts of a request as saved (<see cref="AbdmLinkRequest.CareContextsJson"/>).</summary>
    internal static List<AbdmLinkedRecord> Records(string careContextsJson) =>
        JsonSerializer.Deserialize<List<AbdmLinkedRecord>>(careContextsJson, AbdmGatewayMessages.Json) ?? [];

    /// <summary>A care context's reference: the record's kind and id, unique per patient and stable.</summary>
    public static string ReferenceNumber(AbdmRecordType type, Guid id) => type switch
    {
        AbdmRecordType.DischargeSummary => "discharge-summary",
        AbdmRecordType.LabReport => "lab-report",
        AbdmRecordType.RadiologyReport => "radiology-report",
        AbdmRecordType.CardiologyReport => "cardiology-report",
        AbdmRecordType.OpConsultation => "op-consultation",
        AbdmRecordType.Prescription => "prescription",
        AbdmRecordType.Immunization => "immunization",
        AbdmRecordType.ClinicalNote => "clinical-note",
        AbdmRecordType.HomeReadings => "home-readings",
        AbdmRecordType.Invoice => "invoice",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a kind of record HC360 exports."),
    } + "/" + id.ToString("D");

    /// <summary>
    /// Sends the care contexts with the link token. A refusal also drops the kept token (an expired or revoked one would
    /// otherwise be sent again with every request); a gateway that could not be reached leaves it.
    /// </summary>
    private async Task SendCareContextsAsync(AbdmLinkRequest request, Patient patient, string hipId, string token, DateTimeOffset now, CancellationToken ct)
    {
        request.Status = AbdmLinkStatus.Sent;
        request.LinkRequestId = Guid.CreateVersion7();
        request.SentAt = now;
        await db.SaveChangesAsync(ct);

        var careContexts = Records(request.CareContextsJson);
        var sent = await gateway.LinkCareContextsAsync(
            new AbdmCareContextLink(
                patient.Id,
                patient.FullName,
                request.AbhaAddress,
                protector.Unprotect(patient.AbhaNumberCipher),
                [.. careContexts.Select(c => new AbdmCareContext(c.ReferenceNumber, c.Display, c.Type))]),
            token,
            request.LinkRequestId.Value,
            ct);
        if (sent.IsFailure)
        {
            request.Fail(sent.Error!.Code, sent.Error.Message, time.GetUtcNow());
            if (sent.Error.Code == HttpAbdmGatewayClient.RefusedCode)
            {
                await DropTokenAsync(hipId, request.AbhaAddress, ct);
            }

            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>The token kept for the ABHA address, unless it is older than <see cref="LinkTokenLifetimeMonths"/>.</summary>
    private async Task<string?> TokenAsync(string hipId, string abhaAddress, DateTimeOffset now, CancellationToken ct)
    {
        var address = AbdmLinkToken.Normalize(abhaAddress);
        var kept = await db.Set<AbdmLinkToken>().AsNoTracking().FirstOrDefaultAsync(t => t.HipId == hipId && t.AbhaAddress == address, ct);
        return kept is null || !IsLinkTokenUsable(kept.IssuedAt, now) ? null : protector.Unprotect(kept.TokenCipher);
    }

    private async Task KeepTokenAsync(string hipId, string abhaAddress, string token, DateTimeOffset now, CancellationToken ct)
    {
        var address = AbdmLinkToken.Normalize(abhaAddress);
        var kept = await db.Set<AbdmLinkToken>().FirstOrDefaultAsync(t => t.HipId == hipId && t.AbhaAddress == address, ct);
        if (kept is null)
        {
            db.Add(new AbdmLinkToken { HipId = hipId, AbhaAddress = address, TokenCipher = protector.Protect(token), IssuedAt = now });
        }
        else
        {
            kept.TokenCipher = protector.Protect(token);
            kept.IssuedAt = now;
        }
    }

    private async Task DropTokenAsync(string hipId, string abhaAddress, CancellationToken ct)
    {
        var address = AbdmLinkToken.Normalize(abhaAddress);
        if (await db.Set<AbdmLinkToken>().FirstOrDefaultAsync(t => t.HipId == hipId && t.AbhaAddress == address, ct) is { } kept)
        {
            db.Remove(kept);
        }
    }

    // The gateway's callbacks come with no user and no facility: the request is found by the id it was sent under.
    private IQueryable<AbdmLinkRequest> Unscoped() => db.Set<AbdmLinkRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]);

    private static AbdmLinkView View(AbdmLinkRequest request)
    {
        var linked = Records(request.CareContextsJson);
        return new AbdmLinkView(
            request.Id,
            request.PatientId,
            request.AbhaAddress,
            request.Status,
            [.. linked.Select(r => new AbdmLinkedRecordView(r.Type, r.RecordId, r.ReferenceNumber, r.Display))],
            request.CreatedAt,
            request.SentAt,
            request.AnsweredAt,
            request.ErrorCode,
            request.Error);
    }
}
