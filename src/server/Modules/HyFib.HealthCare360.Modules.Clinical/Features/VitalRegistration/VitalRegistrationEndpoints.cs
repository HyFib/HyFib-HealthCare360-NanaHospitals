using System.Globalization;
using System.Text;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Clinical.Features.VitalRegistration;

/// <summary>
/// /api/v1/mrd: births and deaths in the hospital. A birth is recorded against the mother (Form 1, Form 3 for a still
/// birth); a death with its medical certificate of cause of death (MCCD, Form 4), which marks the patient deceased. Each
/// is reported to the Registrar within 21 days; the month's registers and their CSV are for that report.
/// </summary>
internal static class VitalRegistrationEndpoints
{
    private static readonly string[] BirthReaders = [ClinicalPermissions.BirthsRecord, ClinicalPermissions.VitalRegister];
    private static readonly string[] DeathReaders = [ClinicalPermissions.DeathsCertify, ClinicalPermissions.VitalRegister];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/mrd").WithTags("Medical Records");

        group.MapGet("/births", BirthRegister).RequirePermission(ClinicalPermissions.VitalRegister);
        group.MapGet("/births/export", BirthExport).RequirePermission(ClinicalPermissions.VitalRegister);
        group.MapGet("/births/{id:guid}", GetBirth).RequireAnyPermission(BirthReaders);
        group.MapPost("/births", RecordBirth).RequirePermission(ClinicalPermissions.BirthsRecord).WithValidation<SaveBirthRequest>();
        group.MapPut("/births/{id:guid}", CorrectBirth).RequirePermission(ClinicalPermissions.BirthsRecord).WithValidation<SaveBirthRequest>()
            .RequireVersion();
        group.MapPost("/births/{id:guid}/report", ReportBirth).RequirePermission(ClinicalPermissions.VitalRegister)
            .WithValidation<ReportToRegistrarRequest>();
        group.MapPost("/births/{id:guid}/entered-in-error", BirthInError).RequireAnyPermission(BirthReaders)
            .WithValidation<VitalEntryErrorRequest>();
        group.MapGet("/births/{id:guid}/document", BirthDocument).RequireAnyPermission(BirthReaders);

        group.MapGet("/deaths", DeathRegister).RequirePermission(ClinicalPermissions.VitalRegister);
        group.MapGet("/deaths/export", DeathExport).RequirePermission(ClinicalPermissions.VitalRegister);
        group.MapGet("/deaths/{id:guid}", GetDeath).RequireAnyPermission(DeathReaders);
        group.MapPost("/deaths", CertifyDeath).RequirePermission(ClinicalPermissions.DeathsCertify).WithValidation<SaveDeathRequest>();
        group.MapPut("/deaths/{id:guid}", CorrectDeath).RequirePermission(ClinicalPermissions.DeathsCertify).WithValidation<SaveDeathRequest>()
            .RequireVersion();
        group.MapPost("/deaths/{id:guid}/report", ReportDeath).RequirePermission(ClinicalPermissions.VitalRegister)
            .WithValidation<ReportToRegistrarRequest>();
        group.MapPost("/deaths/{id:guid}/entered-in-error", DeathInError).RequireAnyPermission(DeathReaders)
            .WithValidation<VitalEntryErrorRequest>();
        group.MapGet("/deaths/{id:guid}/document", DeathDocument).RequireAnyPermission(DeathReaders);
    }

    private static async Task<IResult> BirthRegister(
        int? year, int? month, VitalRegistrationService vital, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var (y, m) = Month(year, month, time);
        if (m is < 1 or > 12)
        {
            return Error.Field("month", "Choose a month from 1 to 12.").ToProblem();
        }

        // The register names the mothers: who browsed it, and whom it showed, is PHI access.
        var register = await vital.BirthRegisterAsync(y, m, ct);
        await Viewed(audit, "mrd.births.viewed", $"Viewed the birth register ({y}-{m:00}, {register.Rows.Count} births)", register.Rows.Select(r => r.Id), ct);
        return TypedResults.Ok(register);
    }

    private static async Task<IResult> DeathRegister(
        int? year, int? month, VitalRegistrationService vital, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var (y, m) = Month(year, month, time);
        if (m is < 1 or > 12)
        {
            return Error.Field("month", "Choose a month from 1 to 12.").ToProblem();
        }

        var register = await vital.DeathRegisterAsync(y, m, ct);
        await Viewed(audit, "mrd.deaths.viewed", $"Viewed the death register ({y}-{m:00}, {register.Rows.Count} deaths)", register.Rows.Select(r => r.Id), ct);
        return TypedResults.Ok(register);
    }

    private static async Task<IResult> GetBirth(Guid id, VitalRegistrationService vital, ClinicalAccess access, CancellationToken ct)
    {
        if (await vital.FindBirthAsync(id, ct) is not { } birth)
        {
            return Error.NotFound("Birth", id).ToProblem();
        }

        var opened = await access.OpenAsync(birth.MotherPatientId, $"Viewed birth {birth.SerialNumber}", ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : TypedResults.Ok(await vital.BirthDtoAsync(birth, ct));
    }

    private static async Task<IResult> GetDeath(Guid id, VitalRegistrationService vital, ClinicalAccess access, CancellationToken ct)
    {
        if (await vital.FindDeathAsync(id, ct) is not { } death)
        {
            return Error.NotFound("Death", id).ToProblem();
        }

        var opened = await access.OpenAsync(death.PatientId, $"Viewed death certificate {death.SerialNumber}", ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : TypedResults.Ok(await vital.DeathDtoAsync(death, ct));
    }

    private static async Task<IResult> RecordBirth(SaveBirthRequest request, VitalRegistrationService vital, ClinicalAccess access, CancellationToken ct) =>
        await Refused(access, request.MotherPatientId, ct) ?? (await vital.SaveBirthAsync(null, request, ct)).ToOk();

    private static async Task<IResult> CorrectBirth(Guid id, SaveBirthRequest request, VitalRegistrationService vital, ClinicalAccess access, CancellationToken ct) =>
        await Refused(access, request.MotherPatientId, ct) ?? (await vital.SaveBirthAsync(id, request, ct)).ToOk();

    private static async Task<IResult> CertifyDeath(SaveDeathRequest request, VitalRegistrationService vital, ClinicalAccess access, CancellationToken ct) =>
        await Refused(access, request.PatientId, ct) ?? (await vital.SaveDeathAsync(null, request, ct)).ToOk();

    private static async Task<IResult> CorrectDeath(Guid id, SaveDeathRequest request, VitalRegistrationService vital, ClinicalAccess access, CancellationToken ct) =>
        await Refused(access, request.PatientId, ct) ?? (await vital.SaveDeathAsync(id, request, ct)).ToOk();

    private static async Task<IResult> ReportBirth(Guid id, ReportToRegistrarRequest request, VitalRegistrationService vital, CancellationToken ct) =>
        (await vital.ReportBirthAsync(id, request, ct)).ToOk();

    private static async Task<IResult> ReportDeath(Guid id, ReportToRegistrarRequest request, VitalRegistrationService vital, CancellationToken ct) =>
        (await vital.ReportDeathAsync(id, request, ct)).ToOk();

    private static async Task<IResult> BirthInError(Guid id, VitalEntryErrorRequest request, VitalRegistrationService vital, ClinicalAccess access, CancellationToken ct)
    {
        if (await vital.FindBirthAsync(id, ct) is not { } birth)
        {
            return Error.NotFound("Birth", id).ToProblem();
        }

        return await Refused(access, birth.MotherPatientId, ct) ?? (await vital.BirthInErrorAsync(id, request, ct)).ToOk();
    }

    private static async Task<IResult> DeathInError(Guid id, VitalEntryErrorRequest request, VitalRegistrationService vital, ClinicalAccess access, CancellationToken ct)
    {
        if (await vital.FindDeathAsync(id, ct) is not { } death)
        {
            return Error.NotFound("Death", id).ToProblem();
        }

        return await Refused(access, death.PatientId, ct) ?? (await vital.DeathInErrorAsync(id, request, ct)).ToOk();
    }

    private static async Task<IResult> BirthDocument(
        Guid id, VitalRegistrationService vital, ClinicalAccess access, DocumentAssets assets, CancellationToken ct)
    {
        if (await vital.FindBirthAsync(id, ct) is not { } birth)
        {
            return Error.NotFound("Birth", id).ToProblem();
        }

        var opened = await access.OpenAsync(birth.MotherPatientId, $"Printed birth {birth.SerialNumber}", ct);
        return opened.IsFailure
            ? opened.Error!.ToProblem()
            : TypedResults.Ok(VitalRegistrationDocuments.Birth(await vital.BirthDtoAsync(birth, ct), await assets.LetterheadAsync(birth.FacilityId, ct)).ToDto());
    }

    private static async Task<IResult> DeathDocument(
        Guid id, VitalRegistrationService vital, ClinicalAccess access, DocumentAssets assets, CancellationToken ct)
    {
        if (await vital.FindDeathAsync(id, ct) is not { } death)
        {
            return Error.NotFound("Death", id).ToProblem();
        }

        var opened = await access.OpenAsync(death.PatientId, $"Printed death certificate {death.SerialNumber}", ct);
        return opened.IsFailure
            ? opened.Error!.ToProblem()
            : TypedResults.Ok(VitalRegistrationDocuments.Death(await vital.DeathDtoAsync(death, ct), await assets.LetterheadAsync(death.FacilityId, ct)).ToDto());
    }

    private static async Task<IResult> BirthExport(int? year, int? month, VitalRegistrationService vital, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var (y, m) = Month(year, month, time);
        if (m is < 1 or > 12)
        {
            return Error.Field("month", "Choose a month from 1 to 12.").ToProblem();
        }

        var register = await vital.BirthRegisterAsync(y, m, ct);
        await Exported(audit, "mrd.births.exported", $"Birth register exported ({y}-{m:00}, {register.Rows.Count} births)", ct);
        var lines = register.Rows.Select(r => Row(
                r.SerialNumber, DateTime(r.BornAt), r.Outcome == BirthOutcome.LiveBirth ? "Live birth" : "Still birth", r.Sex.ToString(),
                r.WeightGrams?.ToString(CultureInfo.InvariantCulture), r.ChildName, r.MotherName, r.MotherUhid, r.FatherName, Date(r.DueBy),
                Date(r.ReportedOn), r.RegistrationNumber, r.Overdue ? "Overdue" : r.Status.ToString()))
            .Prepend(Row("Serial no.", "Date and time of birth", "Outcome", "Sex", "Weight (g)", "Child's name", "Mother", "Mother's UHID", "Father",
                "Report by", "Reported on", "Registration no.", "Status"));
        return TypedResults.File(Build(lines), "text/csv; charset=utf-8", string.Create(CultureInfo.InvariantCulture, $"birth-register-{y}-{m:00}.csv"));
    }

    private static async Task<IResult> DeathExport(int? year, int? month, VitalRegistrationService vital, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var (y, m) = Month(year, month, time);
        if (m is < 1 or > 12)
        {
            return Error.Field("month", "Choose a month from 1 to 12.").ToProblem();
        }

        var register = await vital.DeathRegisterAsync(y, m, ct);
        await Exported(audit, "mrd.deaths.exported", $"Death register exported ({y}-{m:00}, {register.Rows.Count} deaths)", ct);
        var lines = register.Rows.Select(r => Row(
                r.SerialNumber, DateTime(r.DiedAt), r.PatientName, r.Uhid, r.Gender, r.AgeYears?.ToString(CultureInfo.InvariantCulture), r.UnderlyingCause,
                VitalRegistrationDocuments.Manner(r.Manner), r.MedicoLegal ? "Yes" : "No", r.CertifiedBy, Date(r.DueBy), Date(r.ReportedOn),
                r.RegistrationNumber, r.Overdue ? "Overdue" : r.Status.ToString()))
            .Prepend(Row("Serial no.", "Date and time of death", "Name", "UHID", "Sex", "Age", "Underlying cause", "Manner", "Medico-legal", "Certified by",
                "Report by", "Reported on", "Registration no.", "Status"));
        return TypedResults.File(Build(lines), "text/csv; charset=utf-8", string.Create(CultureInfo.InvariantCulture, $"death-register-{y}-{m:00}.csv"));
    }

    private static async Task<IResult?> Refused(ClinicalAccess access, Guid patientId, CancellationToken ct)
    {
        var allowed = await access.EnsureCanOpenAsync(patientId, ct);
        return allowed.IsFailure ? allowed.Error!.ToProblem() : null;
    }

    private static Task Viewed(IAuditLogger audit, string action, string summary, IEnumerable<Guid> shown, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, action, summary) { Details = new { shown } }, ct);

    private static Task Exported(IAuditLogger audit, string action, string summary, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, action, summary) { Severity = AuditSeverity.Warning }, ct);

    private static (int Year, int Month) Month(int? year, int? month, TimeProvider time)
    {
        var today = time.Today();
        return (year ?? today.Year, month ?? today.Month);
    }

    private static string Date(DateOnly? value) => value?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string DateTime(DateTimeOffset value) =>
        value.ToOffset(TimeSpan.FromHours(5.5)).ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture);

    private static IReadOnlyList<string?> Row(params string?[] cells) => cells;

    // RFC 4180, with formula-like cells prefixed (CSV injection) and a BOM so Excel reads Indian names.
    private static byte[] Build(IEnumerable<IReadOnlyList<string?>> rows)
    {
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            text.AppendJoin(',', row.Select(Cell)).Append("\r\n");
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    private static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
    }
}
