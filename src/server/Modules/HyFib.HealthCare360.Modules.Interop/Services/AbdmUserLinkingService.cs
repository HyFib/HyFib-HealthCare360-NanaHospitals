using System.Globalization;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Notifications;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>The records linked to a patient's ABHA (<see cref="AbdmLinkedCareContext"/>), whichever way they were linked.</summary>
internal static class AbdmCareContexts
{
    /// <summary>Adds the records not already linked to the ABHA address; the caller saves.</summary>
    public static async Task AddAsync(
        HospitalDbContext db, string hipId, Guid patientId, string abhaAddress, IReadOnlyList<AbdmLinkedRecord> records, AbdmLinkRoute route,
        DateTimeOffset at, CancellationToken ct)
    {
        var address = AbdmLinkToken.Normalize(abhaAddress);
        var references = records.Select(r => r.ReferenceNumber).ToList();
        var existing = (await db.Set<AbdmLinkedCareContext>()
            .Where(c => c.HipId == hipId && c.AbhaAddress == address && references.Contains(c.ReferenceNumber))
            .Select(c => c.ReferenceNumber)
            .ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var record in records.Where(r => !existing.Contains(r.ReferenceNumber)))
        {
            db.Add(new AbdmLinkedCareContext
            {
                HipId = hipId,
                PatientId = patientId,
                AbhaAddress = address,
                RecordType = record.Type,
                RecordId = record.RecordId,
                ReferenceNumber = record.ReferenceNumber,
                Display = record.Display,
                Route = route,
                LinkedAt = at,
            });
        }
    }

    /// <summary>The references of the records already linked to the ABHA address.</summary>
    public static async Task<HashSet<string>> LinkedAsync(HospitalDbContext db, string hipId, string abhaAddress, CancellationToken ct) =>
        (await db.Set<AbdmLinkedCareContext>().AsNoTracking()
            .Where(c => c.HipId == hipId && c.AbhaAddress == AbdmLinkToken.Normalize(abhaAddress))
            .Select(c => c.ReferenceNumber)
            .ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

    /// <summary>A record as a care context: its reference and what the patient sees, for example "Lab report, 12 Oct 2026".</summary>
    public static AbdmLinkedRecord Describe(AbdmRecordType type, Guid id, DateTimeOffset recordedAt)
    {
        var kind = AbdmBundleService.Describe(type);
        return new AbdmLinkedRecord(
            type, id, AbdmLinkingService.ReferenceNumber(type, id), $"{char.ToUpperInvariant(kind[0])}{kind[1..]}, {DocumentFormat.Date(recordedAt)}");
    }
}

/// <summary>
/// User-initiated linking (ABDM M2): a patient finds their records at the hospital from their PHR app and links them to
/// their ABHA. The gateway calls three times, and HC360 answers each call with a call of its own:
/// <list type="number">
/// <item><b>Discover</b>: the patient is found by their ABHA address, or else by the mobile number ABDM verified with
/// their gender, year of birth and name (and the UHID when they typed it); exactly one patient must match. The answer
/// offers their completed records not yet linked to that ABHA address (<see cref="AbdmRecordLoader.ListExportableAsync"/>).</item>
/// <item><b>Link init</b>: the patient chose some of those records. HC360 sends a code by SMS to the mobile number on
/// the patient's record and answers with a link reference. One code per search, and at most three codes to a patient
/// in fifteen minutes.</item>
/// <item><b>Link confirm</b>: the code the patient typed comes back under the link reference. The right code links the
/// records (<see cref="AbdmLinkedCareContext"/>); three wrong codes, or ten minutes, end the request.</item>
/// </list>
/// Each step is saved before its answer is sent. Answers the gateway refuses are logged; the patient starts again from
/// their app. Codes and error messages follow NHA's ABDM wrapper (<c>ABDM-1010</c> patient not found, <c>ABDM-1035</c>
/// incorrect code, <c>1000</c> otherwise).
/// </summary>
internal sealed class AbdmUserLinkingService(
    HospitalDbContext db,
    AbdmRecordLoader records,
    AbdmGatewayConfiguration configuration,
    IAbdmGatewayClient gateway,
    INotificationService notifications,
    IAuditLogger audit,
    TimeProvider time,
    ILogger<AbdmUserLinkingService> log)
{
    public const string Discovered = "abdm.discovered";
    public const string UserLinked = "abdm.user_linked";

    public const string NotFoundCode = "ABDM-1010";
    public const string WrongCodeCode = "ABDM-1035";
    public const string ErrorCode = "1000";

    public const string CodeTemplate = "abdm.link_code";

    /// <summary>At most this many records are offered at once, newest first.</summary>
    public const int MaxOffered = 100;

    /// <summary>Codes sent to one patient in <see cref="CodeWindow"/>, at most.</summary>
    public const int CodesPerWindow = 3;

    public static readonly TimeSpan CodeWindow = TimeSpan.FromMinutes(15);

    private static readonly AbdmGatewayMessages.GatewayError NotFound = new(NotFoundCode, "Patient not found");

    /// <summary>The gateway's discover call.</summary>
    public async Task DiscoverAsync(AbdmGatewayMessages.Discover call, string requestId, CancellationToken ct)
    {
        var transactionId = call.TransactionId?.Trim();
        if (string.IsNullOrEmpty(transactionId) || transactionId.Length > 64 || call.Patient is null)
        {
            log.LogWarning("An ABDM discover call came without a transaction id or a patient");
            return;
        }

        // The records are linked to the ABHA address the search came from, which the patient's app always sends.
        var abhaAddress = AbdmLinkToken.Normalize(call.Patient.Id ?? string.Empty);
        if (abhaAddress.Length is 0 or > 100)
        {
            await SendAsync(Answer(transactionId, requestId) with { Error = NotFound }, ct);
            return;
        }

        var answer = Answer(transactionId, requestId);
        if (await db.Set<AbdmDiscovery>().AsNoTracking().FirstOrDefaultAsync(d => d.TransactionId == transactionId, ct) is { } earlier)
        {
            // The gateway sent the same call again: the same answer, if it is still the one to give.
            await SendAsync(earlier.Status == AbdmDiscoveryStatus.Offered
                ? answer with { Patient = await GroupsAsync(earlier.PatientId, Records(earlier.OfferedJson), ct), MatchedBy = earlier.MatchedBy.Split(',') }
                : answer with { Error = new(ErrorCode, "This search was already answered.") }, ct);
            return;
        }

        var (patient, matchedBy, error) = await MatchAsync(call.Patient, ct);
        if (patient is null)
        {
            await SendAsync(answer with { Error = error }, ct);
            return;
        }

        var (settings, _, _) = await configuration.ReadAsync(ct);
        var linked = await AbdmCareContexts.LinkedAsync(db, settings.HipId!, abhaAddress, ct);
        var offered = (await records.ListExportableAsync(patient.Id, MaxOffered, ct))
            .Select(r => AbdmCareContexts.Describe(r.Type, r.Id, r.RecordedAt))
            .Where(r => !linked.Contains(r.ReferenceNumber))
            .ToList();
        if (offered.Count == 0)
        {
            await SendAsync(answer with { Error = new(ErrorCode, "No records at this hospital are waiting to be linked.") }, ct);
            return;
        }

        var discovery = new AbdmDiscovery
        {
            TransactionId = transactionId,
            HipId = settings.HipId!,
            PatientId = patient.Id,
            AbhaAddress = abhaAddress,
            MatchedBy = string.Join(',', matchedBy),
            OfferedJson = JsonSerializer.Serialize(offered, AbdmGatewayMessages.Json),
            Status = AbdmDiscoveryStatus.Offered,
            OfferedAt = time.GetUtcNow(),
        };
        db.Add(discovery);
        audit.Add(new AuditEntry(AuditCategories.PhiAccess, Discovered, $"Records offered to the patient's ABHA app: {offered.Count}")
        {
            PatientId = patient.Id,
            EntityType = "abdm.discovery",
            EntityId = discovery.Id.ToString("D"),
            Details = new { api = "abdm", matchedBy, records = offered.Select(r => r.ReferenceNumber).ToArray() },
        });
        await db.SaveChangesAsync(ct);

        await SendAsync(answer with { Patient = Groups(patient, offered), MatchedBy = matchedBy }, ct);
    }

    /// <summary>The gateway's link-init call: the records the patient chose; a code goes to their mobile number.</summary>
    public async Task InitAsync(AbdmGatewayMessages.LinkInit call, string requestId, CancellationToken ct)
    {
        var transactionId = call.TransactionId?.Trim() ?? string.Empty;
        var answer = new AbdmGatewayMessages.OnLinkInit(transactionId, null, new AbdmGatewayMessages.RespRequest(requestId), null);
        var discovery = transactionId.Length == 0 ? null : await db.Set<AbdmDiscovery>().FirstOrDefaultAsync(d => d.TransactionId == transactionId, ct);
        var now = time.GetUtcNow();
        // One code per search: a patient who needs another searches again, and each search counts against the limit.
        if (discovery is null || discovery.Status != AbdmDiscoveryStatus.Offered || now - discovery.OfferedAt > AbdmDiscovery.OfferLifetime)
        {
            await SendAsync(answer with { Error = new(ErrorCode, "The search for these records has ended. Search again from the app.") }, ct);
            return;
        }

        if (AbdmLinkToken.Normalize(call.AbhaAddress ?? string.Empty) != discovery.AbhaAddress)
        {
            await SendAsync(answer with { Error = new(ErrorCode, "The ABHA address is not the one these records were offered to.") }, ct);
            return;
        }

        var groups = call.Patient ?? [];
        if (groups.Count == 0 || groups.Any(g => g.ReferenceNumber != discovery.PatientId.ToString("D")))
        {
            await SendAsync(answer with { Error = new(ErrorCode, "Patient reference number is invalid") }, ct);
            return;
        }

        var offered = Records(discovery.OfferedJson);
        var chosen = groups.SelectMany(g => g.CareContexts ?? []).Select(c => c.ReferenceNumber).Distinct().ToList();
        var selected = offered.Where(r => chosen.Contains(r.ReferenceNumber)).ToList();
        if (chosen.Count == 0 || selected.Count != chosen.Count)
        {
            await SendAsync(answer with { Error = new(ErrorCode, "Care context reference numbers are invalid") }, ct);
            return;
        }

        var patient = await db.Set<Patient>().AsNoTracking().FirstAsync(p => p.Id == discovery.PatientId, ct);
        if (!Phone.IsIndianMobile(patient.Phone))
        {
            await SendAsync(answer with { Error = new(ErrorCode, "The hospital has no mobile number for this patient to send a code to.") }, ct);
            return;
        }

        var windowStart = now - CodeWindow;
        if (await db.Set<AbdmDiscovery>().CountAsync(d => d.PatientId == patient.Id && d.CodeSentAt > windowStart, ct) >= CodesPerWindow)
        {
            await SendAsync(answer with { Error = new(ErrorCode, "Several codes have just been sent to this patient. Try again in a few minutes.") }, ct);
            return;
        }

        var code = SecretTokens.NewNumericCode(6);
        discovery.SelectedJson = JsonSerializer.Serialize(selected, AbdmGatewayMessages.Json);
        discovery.LinkReference = Guid.CreateVersion7();
        discovery.CodeHash = HashOf(discovery.Id, code);
        discovery.CodeSentAt = now;
        discovery.CodeExpiresAt = now + AbdmDiscovery.CodeLifetime;
        discovery.Attempts = 0;
        discovery.Status = AbdmDiscoveryStatus.CodeSent;
        notifications.Queue(
            CodeTemplate,
            new NotificationRecipient(patient.Phone, null, patient.Id, patient.FullName),
            new Dictionary<string, string>
            {
                ["code"] = code,
                ["minutes"] = ((int)AbdmDiscovery.CodeLifetime.TotalMinutes).ToString(CultureInfo.InvariantCulture),
            },
            [NotificationChannel.Sms],
            AbdmDiscovery.CodeLifetime,
            patient.RegisteredFacilityId);
        await db.SaveChangesAsync(ct);

        await SendAsync(answer with
        {
            Link = new AbdmGatewayMessages.LinkReference(
                discovery.LinkReference.Value.ToString("D"),
                "MEDIATE",
                new AbdmGatewayMessages.LinkMeta("MOBILE", Phone.Mask(patient.Phone), HttpAbdmGatewayClient.Timestamp(discovery.CodeExpiresAt.Value))),
        }, ct);
    }

    /// <summary>The gateway's link-confirm call: the code the patient typed; the right one links the records.</summary>
    public async Task ConfirmAsync(AbdmGatewayMessages.LinkConfirm call, string requestId, CancellationToken ct)
    {
        var answer = new AbdmGatewayMessages.OnLinkConfirm(null, new AbdmGatewayMessages.RespRequest(requestId), null);
        var discovery = Guid.TryParse(call.Confirmation?.LinkRefNumber, out var reference)
            ? await db.Set<AbdmDiscovery>().FirstOrDefaultAsync(d => d.LinkReference == reference, ct)
            : null;
        if (discovery is null || discovery.Status != AbdmDiscoveryStatus.CodeSent)
        {
            await SendAsync(answer with { Error = new(ErrorCode, "This request to link records has ended. Start again from the app.") }, ct);
            return;
        }

        var now = time.GetUtcNow();
        if (discovery.CodeExpiresAt <= now)
        {
            End(discovery, "The code expired.");
            await db.SaveChangesAsync(ct);
            await SendAsync(answer with { Error = new(ErrorCode, "The code has expired. Start again from the app.") }, ct);
            return;
        }

        discovery.Attempts++;
        if (discovery.CodeHash is null || !SecretTokens.FixedTimeEquals(discovery.CodeHash, HashOf(discovery.Id, call.Confirmation?.Token?.Trim() ?? string.Empty)))
        {
            if (discovery.Attempts >= AbdmDiscovery.MaxAttempts)
            {
                End(discovery, "The code was entered wrongly too many times.");
            }

            await db.SaveChangesAsync(ct);
            await SendAsync(answer with { Error = new(WrongCodeCode, "Incorrect OTP") }, ct);
            return;
        }

        var selected = Records(discovery.SelectedJson ?? "[]");
        discovery.Status = AbdmDiscoveryStatus.Linked;
        discovery.LinkedAt = now;
        discovery.CodeHash = null;
        await AbdmCareContexts.AddAsync(db, discovery.HipId, discovery.PatientId, discovery.AbhaAddress, selected, AbdmLinkRoute.UserInitiated, now, ct);
        audit.Add(new AuditEntry(AuditCategories.PhiAccess, UserLinked, $"Records linked to the patient's ABHA from their app: {selected.Count}")
        {
            PatientId = discovery.PatientId,
            EntityType = "abdm.discovery",
            EntityId = discovery.Id.ToString("D"),
            Details = new { api = "abdm", records = selected.Select(r => r.ReferenceNumber).ToArray() },
        });
        await db.SaveChangesAsync(ct);

        var patient = await db.Set<Patient>().AsNoTracking().FirstAsync(p => p.Id == discovery.PatientId, ct);
        await SendAsync(answer with { Patient = Groups(patient, selected) }, ct);
    }

    /// <summary>
    /// The patient a discovery is for: by ABHA address, or else by the mobile number ABDM verified, narrowed by gender,
    /// year of birth and name, and by the UHID when the patient typed it. Exactly one active, unmerged patient must match.
    /// </summary>
    private async Task<(Patient? Patient, IReadOnlyList<string> MatchedBy, AbdmGatewayMessages.GatewayError? Error)> MatchAsync(
        AbdmGatewayMessages.DiscoverPatient wanted, CancellationToken ct)
    {
        var candidates = db.Set<Patient>().AsNoTracking().Where(p => p.IsActive && p.MergedIntoPatientId == null);
        var address = AbdmLinkToken.Normalize(wanted.Id ?? string.Empty);
        if (address.Length > 0)
        {
            var byAddress = await candidates.Where(p => p.AbhaAddress == address).Take(2).ToListAsync(ct);
            if (byAddress.Count == 1)
            {
                return (byAddress[0], ["ABHA_ADDRESS"], null);
            }

            if (byAddress.Count > 1)
            {
                return (null, [], Ambiguous());
            }
        }

        var mobile = Phone.Normalize(Identifier(wanted.VerifiedIdentifiers, "MOBILE"));
        if (!Phone.IsIndianMobile(mobile))
        {
            return (null, [], NotFound);
        }

        var matches = (await candidates.Where(p => p.Phone == mobile).Take(50).ToListAsync(ct))
            .Where(p => Demographics.Match(p, wanted))
            .ToList();
        var matchedBy = new List<string> { "MOBILE" };
        if (Identifier(wanted.UnverifiedIdentifiers, "MR") is { } uhid)
        {
            matches = [.. matches.Where(p => string.Equals(p.Uhid, uhid.Trim(), StringComparison.OrdinalIgnoreCase))];
            matchedBy.Add("MR");
        }

        return matches.Count switch
        {
            1 => (matches[0], matchedBy, null),
            0 => (null, [], NotFound),
            _ => (null, [], Ambiguous()),
        };

        static AbdmGatewayMessages.GatewayError Ambiguous() =>
            new(ErrorCode, "More than one patient at this hospital matches. Ask the hospital to link the records.");
    }

    private static string? Identifier(IReadOnlyList<AbdmGatewayMessages.Identifier>? identifiers, string type) =>
        identifiers?.FirstOrDefault(i => string.Equals(i.Type, type, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(i.Value))?.Value;

    private async Task<IReadOnlyList<AbdmGatewayMessages.CareContextGroup>> GroupsAsync(Guid patientId, IReadOnlyList<AbdmLinkedRecord> offered, CancellationToken ct) =>
        Groups(await db.Set<Patient>().AsNoTracking().FirstAsync(p => p.Id == patientId, ct), offered);

    private static IReadOnlyList<AbdmGatewayMessages.CareContextGroup> Groups(Patient patient, IReadOnlyList<AbdmLinkedRecord> careContexts) =>
        AbdmGatewayMessages.Groups(patient.Id, patient.FullName, careContexts.Select(c => (c.ReferenceNumber, c.Display, c.Type)));

    private static AbdmGatewayMessages.OnDiscover Answer(string transactionId, string requestId) =>
        new(transactionId, null, null, new AbdmGatewayMessages.RespRequest(requestId), null);

    private static IReadOnlyList<AbdmLinkedRecord> Records(string json) =>
        JsonSerializer.Deserialize<List<AbdmLinkedRecord>>(json, AbdmGatewayMessages.Json) ?? [];

    private static string HashOf(Guid discoveryId, string code) => SecretTokens.Hash($"{discoveryId:N}:{code}");

    private static void End(AbdmDiscovery discovery, string why)
    {
        discovery.Status = AbdmDiscoveryStatus.Failed;
        discovery.CodeHash = null;
        discovery.Error = why;
    }

    private async Task SendAsync(AbdmGatewayMessages.OnDiscover answer, CancellationToken ct) =>
        Log(await gateway.AnswerDiscoveryAsync(answer, ct), "discover");

    private async Task SendAsync(AbdmGatewayMessages.OnLinkInit answer, CancellationToken ct) =>
        Log(await gateway.AnswerLinkInitAsync(answer, ct), "link-init");

    private async Task SendAsync(AbdmGatewayMessages.OnLinkConfirm answer, CancellationToken ct) =>
        Log(await gateway.AnswerLinkConfirmAsync(answer, ct), "link-confirm");

    private void Log(Result sent, string step)
    {
        if (sent.IsFailure)
        {
            log.LogWarning("The answer to the ABDM gateway's {Step} call was not delivered: {Reason}", step, sent.Error!.Message);
        }
    }
}

/// <summary>
/// Whether a patient on record is the person an ABHA describes, when the mobile number matched: the same gender, the year
/// of birth within two years (five when the hospital estimated the date of birth), and the name: the same first name,
/// and, when both have a last name, last names with the same initial ("Meera K" is "Meera Krishnan").
/// </summary>
internal static class Demographics
{
    public static bool Match(Patient patient, AbdmGatewayMessages.DiscoverPatient wanted)
    {
        if (!string.Equals(AbdmGatewayMessages.Gender(patient.Gender), wanted.Gender?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (wanted.YearOfBirth is { } year && patient.DateOfBirth is { } born
            && Math.Abs(born.Year - year) > (patient.IsDateOfBirthEstimated ? 5 : 2))
        {
            return false;
        }

        return NamesMatch(patient.FullName, wanted.Name);
    }

    public static bool NamesMatch(string ours, string? theirs)
    {
        var a = Tokens(ours);
        var b = Tokens(theirs);
        if (a.Count == 0 || b.Count == 0 || a[0] != b[0])
        {
            return false;
        }

        return a.Count == 1 || b.Count == 1 || a[^1][0] == b[^1][0];
    }

    private static List<string> Tokens(string? name) =>
        [.. new string([.. (name ?? string.Empty).ToLowerInvariant().Select(c => char.IsLetter(c) ? c : ' ')])
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)];
}
