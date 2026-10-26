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
            linked.Add(AbdmCareContexts.Describe(record.Type, record.Id, source.RecordedAt));
        }

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
        var now = time.GetUtcNow();
        var request = new AbdmLinkRequest
        {
            FacilityId = facilityId,
            PatientId = patient.Id,
            AbhaAddress = patient.AbhaAddress.Trim(),
            CareContextsJson = JsonSerializer.Serialize(linked, AbdmGatewayMessages.Json),
        };
        db.Add(request);

        await audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, LinkRequested, $"Records linked to the patient's ABHA: {linked.Count}")
        {
            PatientId = patient.Id,
            EntityType = "abdm.link",
            EntityId = request.Id.ToString("D"),
            Details = new { api = "abdm", records = linked.Select(r => r.ReferenceNumber).ToArray() },
        }, ct);

        var token = await TokenAsync(settings.HipId!, request.AbhaAddress, ct);
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
    /// address, is ignored.
    /// </summary>
    public async Task OnLinkTokenAsync(AbdmGatewayMessages.OnGenerateToken answer, CancellationToken ct)
    {
        if (!Guid.TryParse(answer.Response?.RequestId, out var requestId))
        {
            log.LogWarning("An ABDM link token arrived without the request it answers");
            return;
        }

        var request = await Unscoped().FirstOrDefaultAsync(r => r.TokenRequestId == requestId, ct);
        if (request is null || request.Status != AbdmLinkStatus.AwaitingToken)
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

        var patient = await db.Set<Patient>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .FirstAsync(p => p.Id == request.PatientId, ct);
        await SendCareContextsAsync(request, patient, settings.HipId!, answer.LinkToken, now, ct);
    }

    /// <summary>
    /// The gateway's <c>on_carecontext</c>: whether the care contexts were linked. A refusal also drops the link token
    /// kept for the ABHA address, so the next request asks for a new one (it may have expired or been revoked).
    /// </summary>
    public async Task OnCareContextsAsync(AbdmGatewayMessages.OnAddCareContexts answer, CancellationToken ct)
    {
        if (!Guid.TryParse(answer.Response?.RequestId, out var requestId))
        {
            log.LogWarning("An ABDM linking answer arrived without the request it answers");
            return;
        }

        var request = await Unscoped().FirstOrDefaultAsync(r => r.LinkRequestId == requestId, ct);
        if (request is null || request.Status != AbdmLinkStatus.Sent)
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
            var (settings, _, _) = await configuration.ReadAsync(ct);
            var linked = JsonSerializer.Deserialize<List<AbdmLinkedRecord>>(request.CareContextsJson, AbdmGatewayMessages.Json) ?? [];
            await AbdmCareContexts.AddAsync(db, settings.HipId!, request.PatientId, request.AbhaAddress, linked, AbdmLinkRoute.HipInitiated, now, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>A care context's reference: the record's kind and id, unique per patient and stable.</summary>
    public static string ReferenceNumber(AbdmRecordType type, Guid id) => type switch
    {
        AbdmRecordType.DischargeSummary => "discharge-summary",
        AbdmRecordType.LabReport => "lab-report",
        AbdmRecordType.RadiologyReport => "radiology-report",
        AbdmRecordType.CardiologyReport => "cardiology-report",
        AbdmRecordType.OpConsultation => "op-consultation",
        _ => "prescription",
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

        var careContexts = JsonSerializer.Deserialize<List<AbdmLinkedRecord>>(request.CareContextsJson, AbdmGatewayMessages.Json) ?? [];
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

    private async Task<string?> TokenAsync(string hipId, string abhaAddress, CancellationToken ct)
    {
        var address = AbdmLinkToken.Normalize(abhaAddress);
        var kept = await db.Set<AbdmLinkToken>().AsNoTracking().FirstOrDefaultAsync(t => t.HipId == hipId && t.AbhaAddress == address, ct);
        return kept is null ? null : protector.Unprotect(kept.TokenCipher);
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
        var linked = JsonSerializer.Deserialize<List<AbdmLinkedRecord>>(request.CareContextsJson, AbdmGatewayMessages.Json) ?? [];
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
