using System.Globalization;
using System.Text;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Mtp;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.Mtp;

/// <summary>
/// /api/v1/mtp: terminations of pregnancy (consent, opinion, termination, printed forms), the month's admission register
/// with its export and the month's report to the Chief Medical Officer. Under s.5A of the MTP Act every case opens only
/// to staff given the MTP permissions, and every opening is a PHI access, audited.
/// </summary>
internal static class MtpEndpoints
{
    private static readonly string[] CaseReaders = [SpecialtyCarePermissions.MtpRead, SpecialtyCarePermissions.MtpCertify];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/mtp").WithTags("Termination of pregnancy");
        group.MapGet("/cases", List).RequireAnyPermission(CaseReaders);
        group.MapGet("/cases/{id:guid}", Get).RequireAnyPermission(CaseReaders);
        group.MapPost("/cases", Open).RequirePermission(SpecialtyCarePermissions.MtpCertify).WithValidation<SaveMtpCaseRequest>();
        group.MapPut("/cases/{id:guid}", Correct).RequirePermission(SpecialtyCarePermissions.MtpCertify).WithValidation<SaveMtpCaseRequest>()
            .RequireVersion();
        group.MapPost("/cases/{id:guid}/termination", Terminate).RequirePermission(SpecialtyCarePermissions.MtpCertify)
            .WithValidation<RecordMtpTerminationRequest>();
        group.MapPost("/cases/{id:guid}/cancel", Cancel).RequirePermission(SpecialtyCarePermissions.MtpCertify).WithValidation<CancelMtpCaseRequest>();
        group.MapGet("/cases/{id:guid}/document", Document).RequireAnyPermission(CaseReaders);
        group.MapGet("/cases/{id:guid}/pdf", Pdf).RequireAnyPermission(CaseReaders);

        group.MapGet("/register", Register).RequirePermission(SpecialtyCarePermissions.MtpRegister);
        group.MapGet("/register/export", Export).RequirePermission(SpecialtyCarePermissions.MtpRegister);
        group.MapGet("/monthly-report", MonthlyReport).RequirePermission(SpecialtyCarePermissions.MtpRegister);
    }

    private static async Task<IResult> List(Guid? patientId, MtpCaseStatus? status, MtpService mtp, PatientAccessGuard guard, CancellationToken ct)
    {
        if (patientId is { } patient && await guard.EnsureCanOpenAsync(patient, ct) is { IsFailure: true } refused)
        {
            return refused.Error!.ToProblem();
        }

        return TypedResults.Ok(await mtp.ListAsync(patientId, status, ct));
    }

    private static async Task<IResult> Get(Guid id, MtpService mtp, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        if (await GuardAsync(mtp, guard, id, ct) is { } refused)
        {
            return refused;
        }

        var found = (await mtp.GetAsync(id, ct))!;
        await audit.PatientRecordAccessedAsync(found.PatientId, $"Viewed MTP case {found.SerialNumber}", ct);
        return TypedResults.Ok(found);
    }

    private static async Task<IResult> Open(SaveMtpCaseRequest request, MtpService mtp, PatientAccessGuard guard, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(request.PatientId, ct);
        return access.IsFailure ? access.Error!.ToProblem() : (await mtp.SaveAsync(null, request, ct)).ToOk();
    }

    private static async Task<IResult> Correct(Guid id, SaveMtpCaseRequest request, MtpService mtp, PatientAccessGuard guard, CancellationToken ct) =>
        await GuardAsync(mtp, guard, id, ct) ?? (await mtp.SaveAsync(id, request, ct)).ToOk();

    private static async Task<IResult> Terminate(
        Guid id, RecordMtpTerminationRequest request, MtpService mtp, PatientAccessGuard guard, CancellationToken ct) =>
        await GuardAsync(mtp, guard, id, ct) ?? (await mtp.TerminateAsync(id, request, ct)).ToOk();

    private static async Task<IResult> Cancel(Guid id, CancelMtpCaseRequest request, MtpService mtp, PatientAccessGuard guard, CancellationToken ct) =>
        await GuardAsync(mtp, guard, id, ct) ?? (await mtp.CancelAsync(id, request, ct)).ToOk();

    private static async Task<IResult> Document(
        Guid id, MtpService mtp, DocumentAssets assets, PatientAccessGuard guard, IAuditLogger audit, HospitalDbContext db, CancellationToken ct)
    {
        var (document, problem) = await PrintableAsync(id, "Printed", mtp, assets, guard, audit, db, ct);
        return problem ?? TypedResults.Ok(document!.ToDto());
    }

    private static async Task<IResult> Pdf(
        Guid id, MtpService mtp, DocumentAssets assets, IPdfRenderer renderer, PatientAccessGuard guard, IAuditLogger audit, HospitalDbContext db,
        CancellationToken ct)
    {
        var (document, problem) = await PrintableAsync(id, "Downloaded", mtp, assets, guard, audit, db, ct);
        return problem ?? await document!.ToPdfAsync(renderer, ct);
    }

    private static async Task<IResult> Register(int? year, int? month, MtpService mtp, TimeProvider time, CancellationToken ct)
    {
        var (y, m) = Month(year, month, time);
        return m is < 1 or > 12 ? Error.Field("month", "Choose a month from 1 to 12.").ToProblem() : TypedResults.Ok(await mtp.RegisterAsync(y, m, ct));
    }

    private static async Task<IResult> MonthlyReport(int? year, int? month, MtpService mtp, TimeProvider time, CancellationToken ct)
    {
        var (y, m) = Month(year, month, time);
        return m is < 1 or > 12 ? Error.Field("month", "Choose a month from 1 to 12.").ToProblem() : TypedResults.Ok(await mtp.MonthlyReportAsync(y, m, ct));
    }

    private static async Task<IResult> Export(int? year, int? month, MtpService mtp, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var (y, m) = Month(year, month, time);
        if (m is < 1 or > 12)
        {
            return Error.Field("month", "Choose a month from 1 to 12.").ToProblem();
        }

        var register = await mtp.RegisterAsync(y, m, ct);
        await audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, "mtp.register.exported",
            string.Create(CultureInfo.InvariantCulture, $"MTP admission register exported ({y}-{m:00}, {register.Rows.Count} cases)"))
        {
            Severity = AuditSeverity.Warning,
        }, ct);

        var lines = register.Rows.Select(r => Row(
                r.SerialNumber, Date(r.AdmittedOn), r.PatientName, r.Uhid, r.GuardianName, r.AgeYears.ToString(CultureInfo.InvariantCulture), r.Religion,
                r.Address, r.WeeksOfGestation.ToString(CultureInfo.InvariantCulture), string.Join("; ", r.Grounds.Select(MtpLabels.Ground)),
                r.Category is { } c ? MtpLabels.Category(c) : null, Date(r.TerminatedOn), r.Method is { } method ? MtpLabels.Method(method) : null,
                Date(r.DischargedOn), r.Outcome, r.OpinionBy, r.PerformedBy, r.TerminatedOn is null ? "Not yet done" : r.Remarks))
            .Prepend(Row("Serial no.", "Date of admission", "Name of the patient", "UHID", "Wife/daughter of", "Age", "Religion", "Address",
                "Duration of pregnancy (weeks)", "Reasons", "Category (rule 3B)", "Date of termination", "Method", "Date of discharge", "Result",
                "Opinion formed by", "Performed by", "Remarks"));
        return TypedResults.File(Build(lines), "text/csv; charset=utf-8", string.Create(CultureInfo.InvariantCulture, $"mtp-register-{y}-{m:00}.csv"));
    }

    private static async Task<(PrintableDocument? Document, IResult? Problem)> PrintableAsync(
        Guid id, string verb, MtpService mtp, DocumentAssets assets, PatientAccessGuard guard, IAuditLogger audit, HospitalDbContext db,
        CancellationToken ct)
    {
        if (await GuardAsync(mtp, guard, id, ct) is { } refused)
        {
            return (null, refused);
        }

        var found = (await mtp.GetAsync(id, ct))!;
        var facility = await db.Set<MtpCase>().AsNoTracking().Where(c => c.Id == id).Select(c => c.FacilityId).FirstAsync(ct);
        await audit.PatientRecordAccessedAsync(found.PatientId, $"{verb} MTP case {found.SerialNumber}", ct);
        return (MtpDocument.Build(found, await assets.LetterheadAsync(facility, ct)), null);
    }

    /// <summary>A case is not found, or it is the case of a restricted (VIP) record the user may not open.</summary>
    private static async Task<IResult?> GuardAsync(MtpService mtp, PatientAccessGuard guard, Guid id, CancellationToken ct)
    {
        if (await mtp.PatientOfAsync(id, ct) is not { } patientId)
        {
            return Error.NotFound(nameof(MtpCase), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        return access.IsFailure ? access.Error!.ToProblem() : null;
    }

    private static (int Year, int Month) Month(int? year, int? month, TimeProvider time)
    {
        var today = time.Today();
        return (year ?? today.Year, month ?? today.Month);
    }

    private static string Date(DateOnly? value) => value?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture) ?? string.Empty;

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
