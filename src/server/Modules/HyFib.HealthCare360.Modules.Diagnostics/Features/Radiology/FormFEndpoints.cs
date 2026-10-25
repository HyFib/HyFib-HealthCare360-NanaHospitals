using System.Globalization;
using System.Text;
using HyFib.HealthCare360.Modules.Diagnostics.Documents;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;

/// <summary>A study's Form F, or none yet (<paramref name="Form"/> null).</summary>
public sealed record StudyFormF(FormFDto? Form);

/// <summary>
/// /api/v1/radiology: PCPNDT Form F of an ultrasound in pregnancy (record, complete, print) and the month's register
/// with its export for the Appropriate Authority. Every opening of a form is a PHI access, audited.
/// </summary>
internal static class FormFEndpoints
{
    private static readonly string[] FormReaders =
        [DiagnosticsPermissions.FormFRecord, DiagnosticsPermissions.FormFSign, DiagnosticsPermissions.FormFRegister];

    public static void Map(IEndpointRouteBuilder app)
    {
        var study = app.MapGroup("/api/v1/radiology/studies/{id:guid}/form-f").WithTags("Radiology");
        study.MapGet("/", Get).RequireAnyPermission(FormReaders);
        study.MapPut("/", Declare).RequireAnyPermission(DiagnosticsPermissions.FormFRecord, DiagnosticsPermissions.FormFSign)
            .WithValidation<SaveFormFDeclarationRequest>().FirstSaveCreates();
        study.MapPost("/complete", Complete).RequirePermission(DiagnosticsPermissions.FormFSign).WithValidation<CompleteFormFRequest>();
        study.MapGet("/document", Document).RequireAnyPermission(FormReaders);
        study.MapGet("/pdf", Pdf).RequireAnyPermission(FormReaders);

        var register = app.MapGroup("/api/v1/radiology/form-f").WithTags("Radiology");
        register.MapGet("/register", Register).RequirePermission(DiagnosticsPermissions.FormFRegister);
        register.MapGet("/register/export", Export).RequirePermission(DiagnosticsPermissions.FormFRegister);
    }

    private static async Task<IResult> Get(Guid id, FormFService forms, PatientAccessGuard guard, IAuditLogger audit, HospitalDbContext db, CancellationToken ct)
    {
        var patientId = await PatientOfAsync(db, id, ct);
        if (patientId is null)
        {
            return Error.NotFound(nameof(ImagingStudy), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(patientId.Value, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var form = await forms.GetAsync(id, ct);
        if (form is not null)
        {
            await audit.PatientRecordAccessedAsync(patientId.Value, $"Viewed PCPNDT Form F {form.FormNumber}", ct);
        }

        return TypedResults.Ok(new StudyFormF(form));
    }

    private static async Task<IResult> Declare(
        Guid id, SaveFormFDeclarationRequest request, FormFService forms, PatientAccessGuard guard, HospitalDbContext db, CancellationToken ct) =>
        await GuardAsync(db, guard, id, ct) ?? (await forms.DeclareAsync(id, request, ct)).ToOk();

    private static async Task<IResult> Complete(
        Guid id, CompleteFormFRequest request, FormFService forms, PatientAccessGuard guard, HospitalDbContext db, CancellationToken ct) =>
        await GuardAsync(db, guard, id, ct) ?? (await forms.CompleteAsync(id, request, ct)).ToOk();

    private static async Task<IResult> Document(
        Guid id, FormFService forms, DocumentAssets assets, PatientAccessGuard guard, IAuditLogger audit, HospitalDbContext db, CancellationToken ct)
    {
        var (document, problem) = await PrintableAsync(id, "Printed", forms, assets, guard, audit, db, ct);
        return problem ?? TypedResults.Ok(document!.ToDto());
    }

    private static async Task<IResult> Pdf(
        Guid id, FormFService forms, DocumentAssets assets, IPdfRenderer renderer, PatientAccessGuard guard, IAuditLogger audit, HospitalDbContext db,
        CancellationToken ct)
    {
        var (document, problem) = await PrintableAsync(id, "Downloaded", forms, assets, guard, audit, db, ct);
        return problem ?? await document!.ToPdfAsync(renderer, ct);
    }

    private static async Task<IResult> Register(int? year, int? month, FormFService forms, TimeProvider time, CancellationToken ct)
    {
        var (y, m) = Month(year, month, time);
        return m is < 1 or > 12 ? Error.Field("month", "Choose a month from 1 to 12.").ToProblem() : TypedResults.Ok(await forms.RegisterAsync(y, m, ct));
    }

    private static async Task<IResult> Export(int? year, int? month, FormFService forms, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var (y, m) = Month(year, month, time);
        if (m is < 1 or > 12)
        {
            return Error.Field("month", "Choose a month from 1 to 12.").ToProblem();
        }

        var register = await forms.RegisterAsync(y, m, ct);
        await audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, "radiology.form_f.exported",
            string.Create(CultureInfo.InvariantCulture, $"PCPNDT Form F register exported ({y}-{m:00}, {register.Rows.Count} forms)"))
        {
            Severity = AuditSeverity.Warning,
        }, ct);

        var lines = register.Rows.Select(r => Row(
                r.FormNumber, Date(r.ProcedureDate), r.PatientName, r.Uhid, r.AgeYears.ToString(CultureInfo.InvariantCulture), r.HusbandOrFatherName,
                r.LivingSons.ToString(CultureInfo.InvariantCulture), r.LivingDaughters.ToString(CultureInfo.InvariantCulture),
                r.WeeksOfPregnancy?.ToString(CultureInfo.InvariantCulture), Date(r.LastMenstrualPeriod), r.ReferredBy,
                string.Join("; ", r.Indications.Select(FormFDocument.Indication)), r.Result, r.MtpIndicated ? "Yes" : "No", r.DoctorName,
                r.Status switch { FormFStatus.Completed => "Completed", FormFStatus.Cancelled => "Scan cancelled", _ => "Not completed" }))
            .Prepend(Row("Form", "Date of ultrasound", "Woman", "UHID", "Age", "Husband/father", "Sons", "Daughters", "Weeks", "LMP", "Referred by",
                "Indications", "Result", "MTP indicated", "Doctor", "Status"));
        return TypedResults.File(Build(lines), "text/csv; charset=utf-8", string.Create(CultureInfo.InvariantCulture, $"form-f-register-{y}-{m:00}.csv"));
    }

    private static async Task<(PrintableDocument? Document, IResult? Problem)> PrintableAsync(
        Guid id, string verb, FormFService forms, DocumentAssets assets, PatientAccessGuard guard, IAuditLogger audit, HospitalDbContext db,
        CancellationToken ct)
    {
        if (await GuardAsync(db, guard, id, ct) is { } refused)
        {
            return (null, refused);
        }

        var form = await forms.GetAsync(id, ct);
        if (form is null)
        {
            return (null, Error.NotFound("Form F", id).ToProblem());
        }

        var facility = await db.Set<ImagingStudy>().AsNoTracking().Where(s => s.Id == id).Select(s => s.FacilityId).FirstAsync(ct);
        await audit.PatientRecordAccessedAsync(form.PatientId, $"{verb} PCPNDT Form F {form.FormNumber}", ct);
        return (FormFDocument.Build(form, await assets.LetterheadAsync(facility, ct)), null);
    }

    private static async Task<IResult?> GuardAsync(HospitalDbContext db, PatientAccessGuard guard, Guid studyId, CancellationToken ct)
    {
        var patientId = await PatientOfAsync(db, studyId, ct);
        if (patientId is null)
        {
            return Error.NotFound(nameof(ImagingStudy), studyId).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(patientId.Value, ct);
        return access.IsFailure ? access.Error!.ToProblem() : null;
    }

    private static Task<Guid?> PatientOfAsync(HospitalDbContext db, Guid studyId, CancellationToken ct) =>
        db.Set<ImagingStudy>().AsNoTracking().Where(s => s.Id == studyId).Select(s => (Guid?)s.PatientId).FirstOrDefaultAsync(ct);

    private static (int Year, int Month) Month(int? year, int? month, TimeProvider time)
    {
        var today = DiagnosticsTime.Today(time);
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
