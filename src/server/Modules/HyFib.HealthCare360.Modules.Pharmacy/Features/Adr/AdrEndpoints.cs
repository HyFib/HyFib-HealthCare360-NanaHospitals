using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Pharmacy.Features.Adr;

/// <summary>
/// /api/v1/pharmacy/adr — suspected adverse drug reactions (PvPI): reported by any clinician, assessed (WHO-UMC) and sent
/// to the ADR monitoring centre by pharmacovigilance. A reporter sees their own reports; pharmacovigilance sees all.
/// </summary>
internal static class AdrEndpoints
{
    private static readonly string[] Readers = [PharmacyPermissions.AdrReport, PharmacyPermissions.AdrManage];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/pharmacy/adr").WithTags("Pharmacy");

        group.MapGet("", List).RequireAnyPermission(Readers);
        group.MapGet("/{id:guid}", Get).RequireAnyPermission(Readers);
        group.MapGet("/{id:guid}/document", Document).RequireAnyPermission(Readers);
        group.MapGet("/patients/{patientId:guid}/medicines", Medicines).RequireAnyPermission(Readers);
        group.MapPost("", Report).RequirePermission(PharmacyPermissions.AdrReport).WithValidation<SaveAdrReportRequest>();
        group.MapPut("/{id:guid}", Correct).RequireAnyPermission(Readers).WithValidation<SaveAdrReportRequest>().RequireVersion();
        group.MapPost("/{id:guid}/submit", Submit).RequirePermission(PharmacyPermissions.AdrManage).WithValidation<SubmitAdrReportRequest>();
    }

    private static async Task<IResult> List(AdrReportStatus? status, AdrService adr, IPermissionService permissions, ICurrentUser user, CancellationToken ct)
    {
        var all = user.UserId is { } id && await permissions.HasPermissionAsync(id, PharmacyPermissions.AdrManage, ct);
        return TypedResults.Ok(await adr.ListAsync(!all, status, ct));
    }

    private static async Task<IResult> Get(Guid id, AdrService adr, AdrAccess access, CancellationToken ct)
    {
        if (await adr.FindAsync(id, ct) is not { } report)
        {
            return Error.NotFound("ADR report", id).ToProblem();
        }

        var opened = await access.OpenAsync(report, $"Viewed ADR report {report.Number}", ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : TypedResults.Ok(await adr.DtoAsync(report, ct));
    }

    private static async Task<IResult> Document(Guid id, AdrService adr, AdrAccess access, DocumentAssets assets, CancellationToken ct)
    {
        if (await adr.FindAsync(id, ct) is not { } report)
        {
            return Error.NotFound("ADR report", id).ToProblem();
        }

        var opened = await access.OpenAsync(report, $"Printed ADR report {report.Number}", ct);
        return opened.IsFailure
            ? opened.Error!.ToProblem()
            : TypedResults.Ok(AdrDocument.Build(await adr.DtoAsync(report, ct), await assets.LetterheadAsync(report.FacilityId, ct)).ToDto());
    }

    private static async Task<IResult> Medicines(Guid patientId, AdrService adr, PatientAccessGuard guard, CancellationToken ct)
    {
        var allowed = await guard.EnsureCanOpenAsync(patientId, ct);
        return allowed.IsFailure ? allowed.Error!.ToProblem() : TypedResults.Ok(await adr.MedicinesAsync(patientId, ct));
    }

    private static async Task<IResult> Report(SaveAdrReportRequest request, AdrService adr, PatientAccessGuard guard, CancellationToken ct)
    {
        var allowed = await guard.EnsureCanOpenAsync(request.PatientId, ct);
        return allowed.IsFailure ? allowed.Error!.ToProblem() : (await adr.SaveAsync(null, request, ct)).ToOk();
    }

    private static async Task<IResult> Correct(Guid id, SaveAdrReportRequest request, AdrService adr, AdrAccess access, CancellationToken ct)
    {
        if (await adr.FindAsync(id, ct) is not { } report)
        {
            return Error.NotFound("ADR report", id).ToProblem();
        }

        var opened = await access.OpenAsync(report, null, ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : (await adr.SaveAsync(id, request, ct)).ToOk();
    }

    private static async Task<IResult> Submit(Guid id, SubmitAdrReportRequest request, AdrService adr, CancellationToken ct) =>
        (await adr.SubmitAsync(id, request, ct)).ToOk();
}

/// <summary>
/// Who may open a report: its reporter, or pharmacovigilance; and only if they may open the patient's record. A read is
/// audited as PHI access.
/// </summary>
internal sealed class AdrAccess(PatientAccessGuard guard, IPermissionService permissions, ICurrentUser user, IAuditLogger audit)
{
    public async Task<Result> OpenAsync(AdrReport report, string? what, CancellationToken ct)
    {
        var mine = user.UserId is { } me && report.ReportedBy == me;
        if (!mine && !(user.UserId is { } id && await permissions.HasPermissionAsync(id, PharmacyPermissions.AdrManage, ct)))
        {
            return Error.Forbidden("adr.not_yours", "Only the reporter and pharmacovigilance open this report.");
        }

        var allowed = await guard.EnsureCanOpenAsync(report.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        if (what is not null)
        {
            await audit.PatientRecordAccessedAsync(report.PatientId, what, ct);
        }

        return Result.Success();
    }
}

/// <summary>The hospital's copy of the PvPI suspected adverse drug reaction reporting form.</summary>
internal static class AdrDocument
{
    private const string Styles = """
        .section { margin-top: 10px; }
        .section h2 { font-size: 11pt; margin: 0 0 4px; }
        table.drugs { width: 100%; border-collapse: collapse; font-size: 9pt; }
        table.drugs th, table.drugs td { border: 1px solid #999; padding: 3px 5px; text-align: left; }

        """;

    public static PrintableDocument Build(AdrReportDto r, DocumentLetterhead letterhead)
    {
        var serious = r.Seriousness == AdrSeriousness.None
            ? "Not serious"
            : string.Join(", ", Enum.GetValues<AdrSeriousness>().Where(s => s != AdrSeriousness.None && r.Seriousness.HasFlag(s)).Select(Label));
        var body = Html.Of($"""
            {DocumentParts.Letterhead(letterhead)}
            {DocumentParts.Title("Suspected adverse drug reaction reporting form (PvPI)")}
            {DocumentParts.Fields([
                ("Report no.", r.Number),
                ("Incident report", r.IncidentNumber),
                ("Patient initials", r.PatientInitials),
                ("Age at onset", r.AgeYears is { } age ? $"{age} years" : null),
                ("Sex", r.Sex),
                ("Weight", r.WeightKg is { } kg ? $"{kg:0.#} kg" : null),
            ])}
            <section class="section">
              <h2>Suspected adverse reaction</h2>
              {DocumentParts.Fields([
                  ("Reaction started", DocumentFormat.Date(r.ReactionStartedOn)),
                  ("Reaction stopped", r.ReactionStoppedOn is { } stopped ? DocumentFormat.Date(stopped) : null),
                  ("Description", r.Reaction),
                  ("Relevant tests and laboratory data", r.RelevantTests),
                  ("Relevant medical history", r.MedicalHistory),
                  ("Seriousness", serious),
                  ("Outcome", r.Outcome.ToString()),
              ])}
            </section>
            <section class="section">
              <h2>Drugs</h2>
              <table class="drugs">
                <thead><tr><th></th><th>Drug (brand / generic)</th><th>Manufacturer</th><th>Batch, expiry</th><th>Dose, route, frequency</th><th>Started</th><th>Stopped</th><th>Indication</th><th>Action</th><th>Dechallenge</th><th>Rechallenge</th></tr></thead>
                <tbody>{Html.Join(r.Drugs.Select(d => Html.Of($"<tr><td>{(d.Suspected ? "Suspected" : "Concomitant")}</td><td>{d.Name}</td><td>{d.Manufacturer}</td><td>{d.BatchNumber} {(d.ExpiryDate is { } e ? DocumentFormat.Date(e) : null)}</td><td>{string.Join(", ", new[] { d.Dose, d.Route, d.Frequency }.Where(x => !string.IsNullOrEmpty(x)))}</td><td>{(d.StartedOn is { } s ? DocumentFormat.Date(s) : null)}</td><td>{(d.StoppedOn is { } t ? DocumentFormat.Date(t) : null)}</td><td>{d.Indication}</td><td>{d.Action}</td><td>{d.Dechallenge}</td><td>{d.Rechallenge}</td></tr>")))}</tbody>
              </table>
            </section>
            <section class="section">
              <h2>Reporter</h2>
              {DocumentParts.Fields([
                  ("Name", r.ReporterName),
                  ("Designation", r.ReporterDesignation),
                  ("Date of receipt of the information", DocumentFormat.Date(r.LearnedOn)),
                  ("Causality (WHO-UMC)", r.Causality?.ToString()),
                  ("Assessed by", r.AssessedByName),
                  ("Sent to the AMC on", r.SubmittedOn is { } sent ? DocumentFormat.Date(sent) : "Not yet sent"),
                  ("AMC reference", r.SubmissionReference),
              ])}
            </section>
            """);
        return new PrintableDocument
        {
            Title = $"ADR report {r.Number}",
            FileName = $"adr-{r.Number}",
            Body = body,
            Styles = DocumentParts.Styles + Styles,
            Footer = $"{r.PatientInitials} · {r.Number} · Confidential: the reporter's and patient's identity are not disclosed",
            Template = letterhead.Template,
        };
    }

    private static string Label(AdrSeriousness s) => s switch
    {
        AdrSeriousness.Death => "Death",
        AdrSeriousness.LifeThreatening => "Life-threatening",
        AdrSeriousness.Hospitalisation => "Hospitalisation, initial or prolonged",
        AdrSeriousness.Disability => "Disability",
        AdrSeriousness.CongenitalAnomaly => "Congenital anomaly",
        AdrSeriousness.RequiredIntervention => "Required intervention to prevent permanent impairment or damage",
        _ => "Other medically important condition",
    };
}
