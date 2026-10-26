using System.Globalization;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>A patient on the counter's queue, as the desk sees it: contact details masked.</summary>
public sealed record AbdmShareRow(
    Guid Id,
    int TokenNumber,
    string Counter,
    string Name,
    string? Gender,
    int? YearOfBirth,
    string AbhaAddress,
    string? AbhaNumberMasked,
    string? PhoneMasked,
    AbdmShareStatus Status,
    DateTimeOffset SharedAt,
    DateTimeOffset ExpiresAt,
    AbdmSharePatient? Patient,
    string? MatchedBy);

/// <summary>The patient on record a share matched, or was registered as.</summary>
public sealed record AbdmSharePatient(Guid Id, string Uhid, string FullName);

/// <summary>Everything the share carries, to fill in the registration form: the full ABHA number and mobile number.</summary>
public sealed record AbdmShareDetail(
    Guid Id,
    int TokenNumber,
    string Counter,
    string Name,
    string? Gender,
    int? YearOfBirth,
    int? MonthOfBirth,
    int? DayOfBirth,
    string AbhaAddress,
    string? AbhaNumber,
    string? Phone,
    string? AddressLine,
    string? District,
    string? State,
    string? Pincode,
    AbdmShareStatus Status,
    AbdmSharePatient? Patient,
    string? MatchedBy);

/// <summary>The patient staff registered from a share, or found on record for it.</summary>
public sealed record AbdmShareRegisteredInput(Guid PatientId);

internal sealed class AbdmShareRegisteredInputValidator : AbstractValidator<AbdmShareRegisteredInput>
{
    public AbdmShareRegisteredInputValidator() => RuleFor(x => x.PatientId).NotEmpty();
}

/// <summary>
/// Scan and share (ABDM M1). A patient scans the hospital's QR code at a counter with their PHR app; the gateway passes
/// their ABHA profile on (<c>POST /api/v3/hip/patient/share</c>), and HC360 answers with a token number for the counter
/// (<c>on-share</c>), as NHA's wrapper does:
/// <list type="bullet">
/// <item>Token numbers run from 1 each day (in India), across the hospital, and are given as four digits (<c>0007</c>).
/// The same ABHA address scanning again at the same counter within the hour, while still waiting, gets the same token,
/// with the profile brought up to date.</item>
/// <item>The share is matched to a patient on record: by ABHA address, else ABHA number, else the mobile number with
/// gender, year of birth and name (<see cref="Demographics"/>). Only a single match is kept; the desk decides.</item>
/// <item>The desk sees the day's queue (<see cref="ListAsync"/>), registers the patient with the form filled in from the
/// share or opens the matched record, and marks the share registered (<see cref="RegisteredAsync"/>) or dismisses it.</item>
/// </list>
/// Shares are deleted a week after their day (<see cref="AbdmProfileShare.KeptDays"/>).
/// </summary>
internal sealed class AbdmProfileShareService(
    HospitalDbContext db,
    AbdmGatewayConfiguration configuration,
    IAbdmGatewayClient gateway,
    IFieldProtector protector,
    IAuditLogger audit,
    TimeProvider time,
    ILogger<AbdmProfileShareService> log)
{
    public const string Shared = "abdm.profile_shared";
    public const string ShareRegistered = "abdm.share_registered";
    public const string ShareDismissed = "abdm.share_dismissed";

    public const string ErrorCode = "1000";

    /// <summary>A profile shared at a counter: kept on the queue, and answered with the patient's token number.</summary>
    public async Task ShareAsync(AbdmGatewayMessages.ProfileShare call, string requestId, CancellationToken ct)
    {
        var response = new AbdmGatewayMessages.RespRequest(requestId);
        var patient = call.Profile?.Patient;
        var address = Abha.NormalizeAddress(patient?.AbhaAddress) ?? string.Empty;
        var context = call.MetaData?.Context?.Trim() ?? string.Empty;
        var name = patient?.Name?.Trim() ?? string.Empty;
        if (patient is null || !Abha.IsValidAddress(address) || context.Length is 0 or > 64 || name.Length is 0 or > 200)
        {
            await SendAsync(new AbdmGatewayMessages.OnShare(null, response, new(ErrorCode, "Invalid profile share request")), ct);
            return;
        }

        var (settings, _, _) = await configuration.ReadAsync(ct);
        var now = time.GetUtcNow();
        var today = time.Today();
        await db.Set<AbdmProfileShare>().IgnoreQueryFilters()
            .Where(s => s.TokenDate < today.AddDays(-AbdmProfileShare.KeptDays))
            .ExecuteDeleteAsync(ct);

        var share = await db.Set<AbdmProfileShare>()
            .Where(s => s.HipId == settings.HipId && s.AbhaAddress == address && s.Context == context
                && s.Status == AbdmShareStatus.Waiting && s.ExpiresAt > now && s.TokenDate == today)
            .OrderByDescending(s => s.SharedAt)
            .FirstOrDefaultAsync(ct);
        var again = share is not null;
        share ??= new AbdmProfileShare { HipId = settings.HipId!, AbhaAddress = address, Context = context, TokenDate = today, SharedAt = now };
        Fill(share, patient, name);
        share.ExpiresAt = now + AbdmProfileShare.TokenLifetime;
        (share.PatientId, share.MatchedBy) = await MatchAsync(share, patient.AbhaNumber, ct);
        audit.Add(new AuditEntry(AuditCategories.Data, Shared, $"ABHA profile shared at counter {context}")
        {
            PatientId = share.PatientId,
            EntityType = "abdm.share",
            EntityId = address,
            Details = new { api = "abdm", counter = context, matchedBy = share.MatchedBy, again },
        });

        if (again)
        {
            await db.SaveChangesAsync(ct);
        }
        else if (!await AddWithNextTokenAsync(share, ct))
        {
            await SendAsync(new AbdmGatewayMessages.OnShare(null, response, new(ErrorCode, "FAILURE at HIP")), ct);
            return;
        }

        await SendAsync(new AbdmGatewayMessages.OnShare(
            new("SUCCESS", address, new(context, Token(share.TokenNumber), HttpAbdmGatewayClient.Timestamp(share.ExpiresAt))), response, null), ct);
    }

    /// <summary>Today's shares, the waiting first, each in token order.</summary>
    public async Task<IReadOnlyList<AbdmShareRow>> ListAsync(CancellationToken ct)
    {
        var today = time.Today();
        var shares = await db.Set<AbdmProfileShare>().AsNoTracking()
            .Where(s => s.TokenDate == today)
            .OrderBy(s => s.Status != AbdmShareStatus.Waiting).ThenBy(s => s.TokenNumber)
            .ToListAsync(ct);
        var patients = await PatientsAsync(shares.Select(s => s.PatientId), ct);
        return [.. shares.Select(s => Row(s, patients))];
    }

    /// <summary>One share in full, to fill in the registration form.</summary>
    public async Task<Result<AbdmShareDetail>> GetAsync(Guid id, CancellationToken ct)
    {
        var s = await db.Set<AbdmProfileShare>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null)
        {
            return Error.NotFound("abdm.share_not_found", "That ABHA share is not on the queue.");
        }

        var patients = await PatientsAsync([s.PatientId], ct);
        return new AbdmShareDetail(
            s.Id, s.TokenNumber, s.Context, s.Name, s.Gender, s.YearOfBirth, s.MonthOfBirth, s.DayOfBirth, s.AbhaAddress,
            s.AbhaNumberCipher is null ? null : Abha.Format(protector.Unprotect(s.AbhaNumberCipher)!),
            s.Phone, s.AddressLine, s.District, s.State, s.Pincode, s.Status,
            s.PatientId is { } patientId ? patients.GetValueOrDefault(patientId) : null, s.MatchedBy);
    }

    /// <summary>Takes a waiting share off the queue as the patient staff registered from it, or found on record.</summary>
    public async Task<Result<AbdmShareRow>> RegisteredAsync(Guid id, AbdmShareRegisteredInput input, CancellationToken ct)
    {
        var share = await db.Set<AbdmProfileShare>().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (share is null)
        {
            return Error.NotFound("abdm.share_not_found", "That ABHA share is not on the queue.");
        }

        if (share.Status != AbdmShareStatus.Waiting)
        {
            return Error.Conflict("abdm.share_handled", "This ABHA share has already been dealt with.");
        }

        var patient = await db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == input.PatientId, ct);
        if (patient is null)
        {
            return Error.NotFound("patient.not_found", "Patient not found.");
        }

        if (!patient.IsActive || patient.MergedIntoPatientId is not null)
        {
            return Error.Rule("abdm.share_patient_inactive", "Choose the patient's current record: this one is inactive or merged into another.");
        }

        share.Status = AbdmShareStatus.Registered;
        share.PatientId = patient.Id;
        share.HandledAt = time.GetUtcNow();
        audit.Add(new AuditEntry(AuditCategories.Data, ShareRegistered, $"ABHA share at counter {share.Context} registered as {patient.Uhid}")
        {
            PatientId = patient.Id,
            EntityType = "abdm.share",
            EntityId = share.Id.ToString("D"),
            Details = new { api = "abdm", token = share.TokenNumber, matchedBy = share.MatchedBy },
        });
        await db.SaveChangesAsync(ct);
        return Row(share, await PatientsAsync([share.PatientId], ct));
    }

    /// <summary>Takes a waiting share off the queue without registering anyone.</summary>
    public async Task<Result<AbdmShareRow>> DismissAsync(Guid id, CancellationToken ct)
    {
        var share = await db.Set<AbdmProfileShare>().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (share is null)
        {
            return Error.NotFound("abdm.share_not_found", "That ABHA share is not on the queue.");
        }

        if (share.Status != AbdmShareStatus.Waiting)
        {
            return Error.Conflict("abdm.share_handled", "This ABHA share has already been dealt with.");
        }

        share.Status = AbdmShareStatus.Dismissed;
        share.PatientId = null;
        share.HandledAt = time.GetUtcNow();
        audit.Add(new AuditEntry(AuditCategories.Data, ShareDismissed, $"ABHA share at counter {share.Context} dismissed")
        {
            EntityType = "abdm.share",
            EntityId = share.Id.ToString("D"),
            Details = new { api = "abdm", token = share.TokenNumber },
        });
        await db.SaveChangesAsync(ct);
        return Row(share, await PatientsAsync([share.PatientId], ct));
    }

    private static AbdmShareRow Row(AbdmProfileShare s, Dictionary<Guid, AbdmSharePatient> patients) => new(
        s.Id, s.TokenNumber, s.Context, s.Name, s.Gender, s.YearOfBirth, s.AbhaAddress, s.AbhaNumberMasked,
        s.Phone is null ? null : Phone.Mask(s.Phone), s.Status, s.SharedAt, s.ExpiresAt,
        s.PatientId is { } id ? patients.GetValueOrDefault(id) : null, s.MatchedBy);

    /// <summary>Four digits, as the wrapper gives them.</summary>
    public static string Token(int number) => number.ToString("D4", CultureInfo.InvariantCulture);

    /// <summary>
    /// Adds the share under the day's next token number. Two shares at once may pick the same number; the unique index
    /// refuses the second, which takes the next.
    /// </summary>
    private async Task<bool> AddWithNextTokenAsync(AbdmProfileShare share, CancellationToken ct)
    {
        db.Add(share);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            share.TokenNumber = 1 + (await db.Set<AbdmProfileShare>().AsNoTracking()
                .Where(s => s.HipId == share.HipId && s.TokenDate == share.TokenDate)
                .MaxAsync(s => (int?)s.TokenNumber, ct) ?? 0);
            try
            {
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                log.LogInformation("Token {Token} was taken by another share; trying the next", share.TokenNumber);
            }
        }

        log.LogWarning("No token number could be given to an ABHA share at counter {Counter}", share.Context);
        return false;
    }

    private void Fill(AbdmProfileShare share, AbdmGatewayMessages.SharePatient patient, string name)
    {
        var digits = Abha.Digits(patient.AbhaNumber);
        share.Name = name;
        share.Gender = patient.Gender?.Trim().ToUpperInvariant() is { Length: 1 } gender && "MFOU".Contains(gender, StringComparison.Ordinal) ? gender : null;
        share.YearOfBirth = patient.YearOfBirth is >= 1900 and <= 2200 ? patient.YearOfBirth : null;
        share.MonthOfBirth = patient.MonthOfBirth is >= 1 and <= 12 ? patient.MonthOfBirth : null;
        share.DayOfBirth = patient.DayOfBirth is >= 1 and <= 31 ? patient.DayOfBirth : null;
        share.AbhaNumberCipher = Abha.IsValidNumber(digits) ? protector.Protect(digits) : null;
        share.AbhaNumberMasked = share.AbhaNumberCipher is null ? null : Abha.Mask(digits);
        share.Phone = Phone.Normalize(patient.PhoneNumber) is { } phone && Phone.IsIndianMobile(phone) ? phone : null;
        share.AddressLine = Clip(patient.Address?.Line, 500);
        share.District = Clip(patient.Address?.District, 100);
        share.State = Clip(patient.Address?.State, 100);
        share.Pincode = Clip(patient.Address?.Pincode, 10);
    }

    /// <summary>The one active, unmerged patient the share is: by ABHA address, ABHA number, or mobile and demographics.</summary>
    private async Task<(Guid? PatientId, string? MatchedBy)> MatchAsync(AbdmProfileShare share, string? abhaNumber, CancellationToken ct)
    {
        var candidates = db.Set<Patient>().AsNoTracking().Where(p => p.IsActive && p.MergedIntoPatientId == null);
        var byAddress = await candidates.Where(p => p.AbhaAddress == share.AbhaAddress).Select(p => p.Id).Take(2).ToListAsync(ct);
        if (byAddress.Count > 0)
        {
            return byAddress.Count == 1 ? (byAddress[0], "ABHA_ADDRESS") : (null, null);
        }

        var digits = Abha.Digits(abhaNumber);
        if (Abha.IsValidNumber(digits))
        {
            var indexes = protector.BlindIndexes(Masking.Normalize(digits)).ToArray();
            var byNumber = await candidates.Where(p => p.AbhaNumberIndex != null && indexes.Contains(p.AbhaNumberIndex)).Select(p => p.Id).Take(2).ToListAsync(ct);
            if (byNumber.Count > 0)
            {
                return byNumber.Count == 1 ? (byNumber[0], "ABHA_NUMBER") : (null, null);
            }
        }

        if (share.Phone is null)
        {
            return (null, null);
        }

        var wanted = new AbdmGatewayMessages.DiscoverPatient(share.AbhaAddress, share.Name, share.Gender, share.YearOfBirth, null, null);
        var byMobile = (await candidates.Where(p => p.Phone == share.Phone).Take(50).ToListAsync(ct))
            .Where(p => Demographics.Match(p, wanted))
            .Select(p => p.Id)
            .ToList();
        return byMobile.Count == 1 ? (byMobile[0], "MOBILE") : (null, null);
    }

    private async Task<Dictionary<Guid, AbdmSharePatient>> PatientsAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.OfType<Guid>().Distinct().ToList();
        return wanted.Count == 0
            ? []
            : await db.Set<Patient>().AsNoTracking()
                .Where(p => wanted.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => new AbdmSharePatient(p.Id, p.Uhid, p.FullName), ct);
    }

    private static string? Clip(string? text, int length)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > length ? trimmed[..length] : trimmed;
    }

    private async Task SendAsync(AbdmGatewayMessages.OnShare answer, CancellationToken ct)
    {
        var sent = await gateway.AnswerShareAsync(answer, ct);
        if (sent.IsFailure)
        {
            log.LogWarning("The answer to the ABDM gateway's profile share was not delivered: {Reason}", sent.Error!.Message);
        }
    }
}
