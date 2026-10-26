using System.Text.Json;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>Which identifier on the patient's record ABDM sends the code for.</summary>
public sealed record AbhaVerificationStart(AbhaLoginHint Method);

internal sealed class AbhaVerificationStartValidator : AbstractValidator<AbhaVerificationStart>
{
    public AbhaVerificationStartValidator() => RuleFor(x => x.Method).IsInEnum();
}

/// <summary>The code the patient read out.</summary>
public sealed record AbhaVerificationCode(string Code);

internal sealed class AbhaVerificationCodeValidator : AbstractValidator<AbhaVerificationCode>
{
    public AbhaVerificationCodeValidator() =>
        RuleFor(x => x.Code).NotEmpty().Matches("^[0-9]{6}$").WithMessage("Enter the six-digit code the patient received.");
}

/// <summary>A check of a patient's ABHA, as the desk sees it.</summary>
public sealed record AbhaVerificationView(
    Guid Id,
    Guid PatientId,
    AbhaLoginHint Method,
    AbhaVerificationStatus Status,
    string? SentMessage,
    DateTimeOffset CodeExpiresAt,
    int AttemptsLeft,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? AbhaNumberMasked,
    string? AbhaAddress,
    string? Name,
    string? Gender,
    int? YearOfBirth,
    bool? KycVerified,
    IReadOnlyList<string> Differences,
    string? Error);

/// <summary>
/// Checks a patient's ABHA with ABDM at the desk (ABDM M1, <see cref="AbhaClient"/>). Only an identifier already on the
/// patient's record is sent, never one typed at the desk: the ABHA number, or the mobile number. ABDM sends the patient
/// a code; the desk enters it; the ABHA it opens is compared with the record (the ABHA number, and the name, gender and
/// year of birth by the matching rule of <see cref="Demographics"/>, exact on the year unless the record's date of birth
/// is estimated). The record is not changed: what differs is shown, and the desk corrects the record if the patient
/// agrees. At most three codes to a patient in fifteen minutes, three tries at each, ten minutes each.
/// </summary>
internal sealed partial class AbhaVerificationService(
    HospitalDbContext db,
    AbhaClient abha,
    IFieldProtector protector,
    IAuditLogger audit,
    TimeProvider time,
    ILogger<AbhaVerificationService> log)
{
    public const string CodeSent = "abdm.abha_code_sent";
    public const string Checked = "abdm.abha_checked";

    public async Task<Result<AbhaVerificationView>> StartAsync(Guid patientId, AbhaVerificationStart input, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == patientId, ct);
        if (patient is null)
        {
            return Error.NotFound("patient.not_found", "Patient not found.");
        }

        if (!patient.IsActive || patient.MergedIntoPatientId is not null)
        {
            return Error.Rule("abdm.abha_patient_inactive", "This record is inactive or merged into another: check the ABHA on the patient's current record.");
        }

        string loginId;
        if (input.Method == AbhaLoginHint.AbhaNumber)
        {
            if (patient.AbhaNumberCipher is null)
            {
                return Error.Rule("abdm.abha_no_number", "There is no ABHA number on the patient's record. Add it, or check by mobile number.");
            }

            loginId = Abha.Digits(protector.Unprotect(patient.AbhaNumberCipher));
        }
        else
        {
            if (!Phone.IsIndianMobile(patient.Phone))
            {
                return Error.Rule("abdm.abha_no_mobile", "There is no Indian mobile number on the patient's record.");
            }

            loginId = patient.Phone![^10..];
        }

        var now = time.GetUtcNow();
        var since = now - AbdmAbhaVerification.Window;
        if (await db.Set<AbdmAbhaVerification>().CountAsync(v => v.PatientId == patientId && v.CreatedAt >= since, ct) >= AbdmAbhaVerification.MaxPerWindow)
        {
            return Error.Rule("abdm.abha_too_many", "Three codes have been sent to this patient in the last 15 minutes. Wait a little before sending another.");
        }

        var sent = await abha.RequestOtpAsync(input.Method, loginId, ct);
        if (sent.IsFailure)
        {
            return sent.Error!;
        }

        var check = new AbdmAbhaVerification
        {
            PatientId = patientId,
            Method = input.Method,
            TxnId = Clip(sent.Value.TxnId, 100)!,
            SentMessage = Clip(sent.Value.Message, 300),
            Status = AbhaVerificationStatus.CodeSent,
            CodeExpiresAt = now + AbdmAbhaVerification.CodeLifetime,
        };
        db.Add(check);
        audit.Add(new AuditEntry(AuditCategories.Data, CodeSent, "ABHA check: ABDM sent the patient a code")
        {
            PatientId = patientId,
            EntityType = "abdm.abha_check",
            EntityId = check.Id.ToString("D"),
            Details = new { api = "abdm", method = input.Method.ToString() },
        });
        await db.SaveChangesAsync(ct);
        return View(check);
    }

    public async Task<Result<AbhaVerificationView>> VerifyAsync(Guid id, AbhaVerificationCode input, CancellationToken ct)
    {
        var check = await db.Set<AbdmAbhaVerification>().FirstOrDefaultAsync(v => v.Id == id, ct);
        if (check is null)
        {
            return Error.NotFound("abdm.abha_check_not_found", "That ABHA check was not found.");
        }

        if (check.Status != AbhaVerificationStatus.CodeSent)
        {
            return Error.Conflict("abdm.abha_check_done", "This ABHA check is finished: send a new code to check again.");
        }

        var now = time.GetUtcNow();
        if (now >= check.CodeExpiresAt)
        {
            End(check, AbhaVerificationStatus.Expired, "The code was not entered within ten minutes.", now);
            await db.SaveChangesAsync(ct);
            return Error.Rule("abdm.abha_code_expired", "The code has expired: send a new one.");
        }

        var answer = await abha.VerifyAsync(check.TxnId, input.Code, ct);
        if (answer.IsFailure)
        {
            return answer.Error!;
        }

        var result = answer.Value;
        if (!result.Succeeded)
        {
            check.Attempts++;
            var left = AbdmAbhaVerification.MaxAttempts - check.Attempts;
            if (left <= 0)
            {
                End(check, AbhaVerificationStatus.Failed, "Three wrong codes.", now);
            }

            await db.SaveChangesAsync(ct);
            return Error.Rule(
                "abdm.abha_code_wrong",
                left > 0 ? $"ABDM did not accept the code ({result.Message ?? "wrong code"}). {left} {(left == 1 ? "try" : "tries")} left."
                    : "ABDM did not accept the code three times. Send a new code to check again.");
        }

        var patient = await db.Set<Patient>().AsNoTracking().FirstAsync(p => p.Id == check.PatientId, ct);
        var onRecord = patient.AbhaNumberCipher is null ? null : Abha.Digits(protector.Unprotect(patient.AbhaNumberCipher));
        var accounts = result.Accounts ?? [];
        var account = accounts.Count == 1 ? accounts[0]
            : accounts.FirstOrDefault(a => onRecord is not null && Abha.Digits(a.AbhaNumber) == onRecord);
        if (account is null)
        {
            End(check, AbhaVerificationStatus.Failed, accounts.Count == 0
                ? "ABDM's answer named no ABHA."
                : $"{accounts.Count} ABHAs use this mobile number, and none is the ABHA number on the record: check by ABHA number.", now);
            await db.SaveChangesAsync(ct);
            return View(check);
        }

        var digits = Abha.Digits(account.AbhaNumber);
        check.AbhaNumberMasked = Abha.IsValidNumber(digits) ? Abha.Mask(digits) : null;
        check.AbhaAddress = Clip(Abha.NormalizeAddress(account.PreferredAbhaAddress), 100);
        check.Name = Clip(account.Name?.Trim(), 200);
        check.Gender = account.Gender?.Trim().ToUpperInvariant() is { Length: 1 } gender ? gender : null;
        check.YearOfBirth = YearOf(account.Dob);
        check.KycVerified = account.KycVerified;

        var differences = Differences(patient, onRecord, digits, check);
        check.DifferencesJson = JsonSerializer.Serialize(differences);
        End(check, differences.Count == 0 ? AbhaVerificationStatus.Verified : AbhaVerificationStatus.Mismatch, null, now);
        audit.Add(new AuditEntry(AuditCategories.Data, Checked, differences.Count == 0
            ? "ABHA checked with ABDM: it is the patient on record"
            : $"ABHA checked with ABDM: {differences.Count} differences from the record")
        {
            PatientId = check.PatientId,
            EntityType = "abdm.abha_check",
            EntityId = check.Id.ToString("D"),
            Details = new { api = "abdm", status = check.Status.ToString(), differences, kycVerified = check.KycVerified },
        });
        await db.SaveChangesAsync(ct);
        log.LogInformation("ABHA check {Check} finished: {Status}", check.Id, check.Status);
        return View(check);
    }

    /// <summary>The patient's last five checks, newest first.</summary>
    public async Task<IReadOnlyList<AbhaVerificationView>> ListAsync(Guid patientId, CancellationToken ct) =>
        [.. (await db.Set<AbdmAbhaVerification>().AsNoTracking()
            .Where(v => v.PatientId == patientId)
            .OrderByDescending(v => v.CreatedAt)
            .Take(5)
            .ToListAsync(ct)).Select(View)];

    /// <summary>A year of birth from ABDM's date: <c>dd-MM-yyyy</c>, <c>yyyy-MM-dd</c> or the year alone.</summary>
    public static int? YearOf(string? dob) =>
        dob is not null && Year().Match(dob) is { Success: true } match && int.TryParse(match.Value, out var year) && year is >= 1900 and <= 2200 ? year : null;

    private static List<string> Differences(Patient patient, string? onRecord, string digits, AbdmAbhaVerification check)
    {
        var differences = new List<string>();
        if (onRecord is not null && digits != onRecord)
        {
            differences.Add($"The ABHA number is {check.AbhaNumberMasked ?? "missing"}, not the one on record ({Abha.Mask(onRecord)}).");
        }

        if (check.Name is { } name && !Demographics.NamesMatch(patient.FullName, name))
        {
            differences.Add($"The name on the ABHA is {name}.");
        }

        if (check.Gender is { } gender && gender != "U" && gender != AbdmGatewayMessages.Gender(patient.Gender))
        {
            differences.Add($"The gender on the ABHA is {gender switch { "M" => "male", "F" => "female", _ => "other" }}.");
        }

        if (check.YearOfBirth is { } year && patient.DateOfBirth is { } born
            && Math.Abs(born.Year - year) > (patient.IsDateOfBirthEstimated ? 5 : 0))
        {
            differences.Add($"The year of birth on the ABHA is {year}.");
        }

        return differences;
    }

    private static void End(AbdmAbhaVerification check, AbhaVerificationStatus status, string? error, DateTimeOffset now)
    {
        check.Status = status;
        check.Error = error;
        check.CompletedAt = now;
        check.TxnId = string.Empty;
    }

    private static AbhaVerificationView View(AbdmAbhaVerification v) => new(
        v.Id, v.PatientId, v.Method, v.Status, v.SentMessage, v.CodeExpiresAt, Math.Max(0, AbdmAbhaVerification.MaxAttempts - v.Attempts),
        v.CreatedAt, v.CompletedAt, v.AbhaNumberMasked, v.AbhaAddress, v.Name, v.Gender, v.YearOfBirth, v.KycVerified,
        JsonSerializer.Deserialize<List<string>>(v.DifferencesJson) ?? [], v.Error);

    private static string? Clip(string? text, int length) =>
        string.IsNullOrEmpty(text) ? text : text.Length > length ? text[..length] : text;

    [GeneratedRegex(@"(19|20|21)\d{2}")]
    private static partial Regex Year();
}
