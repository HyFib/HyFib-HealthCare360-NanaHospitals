using System.Globalization;
using System.Text;
using HyFib.HealthCare360.Modules.Clinical.Domain.Idsp;
using HyFib.HealthCare360.Modules.Clinical.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Idsp;

/// <summary>
/// /api/v1/idsp: notifiable diseases (IDSP). Cases come from diagnoses (P form) and released positive tests (L form);
/// the surveillance officer reports each week's forms to IHIP, a nil report too, and downloads the line list to upload.
/// Tuberculosis cases are notified on Ni-kshay instead, and their Ni-kshay IDs recorded here.
/// </summary>
internal static class IdspEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/idsp").WithTags("Disease Surveillance").RequirePermission(ClinicalPermissions.IdspReport);

        group.MapGet("/diseases", Diseases);
        group.MapGet("/weeks", Overview);
        group.MapGet("/weeks/{year:int}/{week:int}", Week);
        group.MapGet("/weeks/{year:int}/{week:int}/export", Export);
        group.MapPut("/weeks/{year:int}/{week:int}/{form}", Report).WithValidation<ReportIdspWeekRequest>().FirstSaveCreates();
        group.MapGet("/cases/{id:guid}", Case);
        group.MapPost("/cases", Add).WithValidation<AddIdspCaseRequest>();
        group.MapPost("/cases/{id:guid}/discard", Discard).WithValidation<DiscardIdspCaseRequest>();
        group.MapGet("/tuberculosis", Tuberculosis);
        group.MapPost("/cases/{id:guid}/nikshay", RecordNikshay).WithValidation<RecordNikshayRequest>();
    }

    private static async Task<IResult> Tuberculosis(IdspService idsp, IAuditLogger audit, CancellationToken ct)
    {
        var dto = await idsp.TuberculosisAsync(ct);
        var shown = dto.ToNotify.Concat(dto.Notified).Select(c => c.Id).ToList();
        await Viewed(audit, "idsp.tuberculosis.viewed", $"Viewed the tuberculosis cases for Ni-kshay ({dto.ToNotify.Count} to notify)", shown, ct);
        return TypedResults.Ok(dto);
    }

    private static async Task<IResult> RecordNikshay(Guid id, RecordNikshayRequest request, IdspService idsp, CancellationToken ct) =>
        (await idsp.RecordNikshayAsync(id, request, ct)).ToOk();

    private static IResult Diseases() =>
        TypedResults.Ok(IdspDiseases.Notifiable.Select(d => new IdspDiseaseDto(d.Code, d.Name, d.Immediate, d.Nikshay, d.IcdPrefixes)).ToList());

    private static async Task<IResult> Overview(IdspService idsp, IAuditLogger audit, CancellationToken ct)
    {
        var overview = await idsp.OverviewAsync(ct);
        await Viewed(audit, "idsp.overview.viewed", $"Viewed the IDSP weeks ({overview.Immediate.Count} outbreak-prone cases)", overview.Immediate.Select(c => c.Id), ct);
        return TypedResults.Ok(overview);
    }

    private static async Task<IResult> Week(int year, int week, IdspService idsp, IAuditLogger audit, CancellationToken ct)
    {
        if (!IdspWeek.IsValid(year, week))
        {
            return Error.Field("week", "Choose a week of the year.").ToProblem();
        }

        // The line list names the patients: who browsed it, and whom it showed, is PHI access.
        var dto = await idsp.WeekAsync(new IdspWeek(year, week), ct);
        var shown = dto.Presumptive.Cases.Concat(dto.Laboratory.Cases).Concat(dto.Discarded).Select(c => c.Id).Distinct().ToList();
        await Viewed(audit, "idsp.week.viewed", $"Viewed IDSP week {dto.Year}-W{dto.Week:00} ({shown.Count} cases)", shown, ct);
        return TypedResults.Ok(dto);
    }

    private static async Task<IResult> Case(Guid id, IdspService idsp, IAuditLogger audit, CancellationToken ct)
    {
        if (await idsp.CaseAsync(id, ct) is not { } row)
        {
            return Error.NotFound("Case", id).ToProblem();
        }

        await Viewed(audit, "idsp.case.viewed", $"Viewed the IDSP case of {row.Disease}", [row.Id], ct);
        return TypedResults.Ok(row);
    }

    private static async Task<IResult> Report(int year, int week, string form, ReportIdspWeekRequest request, IdspService idsp, CancellationToken ct)
    {
        if (!IdspWeek.IsValid(year, week))
        {
            return Error.Field("week", "Choose a week of the year.").ToProblem();
        }

        if (!Enum.TryParse<IdspForm>(form, ignoreCase: true, out var which) || !Enum.IsDefined(which))
        {
            return Error.Field("form", "Choose the P (Presumptive) or L (Laboratory) form.").ToProblem();
        }

        return (await idsp.ReportAsync(new IdspWeek(year, week), which, request, ct)).ToOk();
    }

    private static async Task<IResult> Add(AddIdspCaseRequest request, IdspService idsp, ClinicalAccess access, CancellationToken ct)
    {
        var allowed = await access.EnsureCanOpenAsync(request.PatientId, ct);
        return allowed.IsFailure ? allowed.Error!.ToProblem() : (await idsp.AddAsync(request, ct)).ToOk();
    }

    private static async Task<IResult> Discard(Guid id, DiscardIdspCaseRequest request, IdspService idsp, CancellationToken ct) =>
        (await idsp.DiscardAsync(id, request, ct)).ToOk();

    private static async Task<IResult> Export(int year, int week, string? form, IdspService idsp, IAuditLogger audit, CancellationToken ct)
    {
        if (!IdspWeek.IsValid(year, week))
        {
            return Error.Field("week", "Choose a week of the year.").ToProblem();
        }

        if (!Enum.TryParse<IdspForm>(form ?? nameof(IdspForm.Presumptive), ignoreCase: true, out var which) || !Enum.IsDefined(which))
        {
            return Error.Field("form", "Choose the P (Presumptive) or L (Laboratory) form.").ToProblem();
        }

        var dto = await idsp.WeekAsync(new IdspWeek(year, week), ct);
        var cases = which == IdspForm.Presumptive ? dto.Presumptive.Cases : dto.Laboratory.Cases;
        var letter = which == IdspForm.Presumptive ? "P" : "L";
        await audit.WriteAsync(
            new AuditEntry(AuditCategories.PhiAccess, "idsp.week.exported", $"IDSP {letter} form exported ({dto.Year}-W{dto.Week:00}, {cases.Count} cases)")
            {
                Severity = AuditSeverity.Warning,
                Details = new { shown = cases.Select(c => c.Id) },
            },
            ct);

        var header = which == IdspForm.Presumptive
            ? Row("Disease", "Name", "UHID", "Age", "Sex", "Address", "District", "Mobile", "Date of onset", "Date of diagnosis", "ICD-10")
            : Row("Disease", "Name", "UHID", "Age", "Sex", "Address", "District", "Mobile", "Date of onset", "Date of result", "Test", "Result");
        var lines = cases.Select(c => which == IdspForm.Presumptive
                ? Row(c.Disease, c.PatientName, c.Uhid, Number(c.AgeYears), c.Sex, c.Address, c.District, Mobile(c.Phone), Date(c.OnsetDate), Date(c.PresumedAt), c.IcdCode)
                : Row(c.Disease, c.PatientName, c.Uhid, Number(c.AgeYears), c.Sex, c.Address, c.District, Mobile(c.Phone), Date(c.OnsetDate), Date(c.ConfirmedAt), c.TestName,
                    c.TestResult))
            .Prepend(header);
        return TypedResults.File(Build(lines), "text/csv; charset=utf-8", string.Create(CultureInfo.InvariantCulture, $"idsp-{letter}-{dto.Year}-W{dto.Week:00}.csv"));
    }

    private static Task Viewed(IAuditLogger audit, string action, string summary, IEnumerable<Guid> shown, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, action, summary) { Details = new { shown } }, ct);

    // IHIP takes the ten-digit Indian number; a leading "+" would also read as a formula.
    private static string? Mobile(string? e164) =>
        e164 is not null && e164.StartsWith("+91", StringComparison.Ordinal) ? e164[3..] : e164?.TrimStart('+');

    private static string? Number(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string Date(DateOnly? value) => value?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Date(DateTimeOffset? value) =>
        value is { } at ? Date(Domain.VitalRegistration.LocalDate(at)) : string.Empty;

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
