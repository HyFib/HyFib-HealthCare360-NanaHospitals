using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Emergency;

/// <summary>
/// /api/v1/emergency — the emergency department: the ER board, ESI triage, the doctor's first look and the disposition,
/// and the medico-legal case register with its police intimation. A case's patient is opened through the access guard,
/// and the reads are audited as PHI access.
/// </summary>
internal static class EmergencyEndpoints
{
    private static readonly string[] CaseReaders =
    [
        ClinicalPermissions.ErBoard, ClinicalPermissions.ErTriage, ClinicalPermissions.ErDisposition, ClinicalPermissions.ErMlc,
    ];

    private static readonly string[] MlcReaders = [ClinicalPermissions.ErMlc, ClinicalPermissions.MlcRegister];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/emergency").WithTags("Emergency");

        group.MapGet("/board", async (EmergencyService er, CancellationToken ct) => TypedResults.Ok(await er.BoardAsync(ct)))
            .RequireAnyPermission(CaseReaders);
        group.MapGet("/cases/{id:guid}", Get).RequireAnyPermission(CaseReaders);
        group.MapPost("/cases/{id:guid}/triage/preview", Preview).RequirePermission(ClinicalPermissions.ErTriage).WithValidation<TriagePreviewRequest>();
        group.MapPost("/cases/{id:guid}/triage", Triage).RequirePermission(ClinicalPermissions.ErTriage).WithValidation<TriageRequest>();
        group.MapPost("/cases/{id:guid}/seen", Seen).RequirePermission(ClinicalPermissions.ErDisposition).WithValidation<CaseVersionRequest>();
        group.MapPost("/cases/{id:guid}/disposition", Dispose).RequirePermission(ClinicalPermissions.ErDisposition).WithValidation<DispositionRequest>();
        group.MapPut("/cases/{id:guid}/mlc", SaveMlc).RequirePermission(ClinicalPermissions.ErMlc).WithValidation<SaveMlcRequest>().FirstSaveCreates();
        group.MapPost("/cases/{id:guid}/mlc/intimation", Intimate).RequirePermission(ClinicalPermissions.ErMlc).WithValidation<MlcIntimationRequest>();
        group.MapGet("/cases/{id:guid}/mlc/document", Document).RequireAnyPermission(MlcReaders);
        group.MapGet("/mlc", Register).RequireAnyPermission(MlcReaders);
    }

    private static async Task<IResult> Get(Guid id, EmergencyService er, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var opened = await OpenAsync(id, er, guard, ct);
        if (opened.IsFailure)
        {
            return opened.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(opened.Value.PatientId, "Viewed the emergency case", ct);
        return TypedResults.Ok(await er.DetailAsync(opened.Value, ct));
    }

    private static async Task<IResult> Preview(Guid id, TriagePreviewRequest request, EmergencyService er, PatientAccessGuard guard, CancellationToken ct)
    {
        var opened = await OpenAsync(id, er, guard, ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : TypedResults.Ok(await er.PreviewAsync(opened.Value, request, ct));
    }

    private static async Task<IResult> Triage(Guid id, TriageRequest request, EmergencyService er, PatientAccessGuard guard, CancellationToken ct)
    {
        var opened = await OpenAsync(id, er, guard, ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : (await er.TriageAsync(id, request, ct)).ToOk();
    }

    private static async Task<IResult> Seen(Guid id, CaseVersionRequest request, EmergencyService er, PatientAccessGuard guard, CancellationToken ct)
    {
        var opened = await OpenAsync(id, er, guard, ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : (await er.SeenAsync(id, request.Version, ct)).ToOk();
    }

    private static async Task<IResult> Dispose(Guid id, DispositionRequest request, EmergencyService er, PatientAccessGuard guard, CancellationToken ct)
    {
        var opened = await OpenAsync(id, er, guard, ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : (await er.DisposeAsync(id, request, ct)).ToOk();
    }

    private static async Task<IResult> SaveMlc(Guid id, SaveMlcRequest request, EmergencyService er, PatientAccessGuard guard, CancellationToken ct)
    {
        var opened = await OpenAsync(id, er, guard, ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : (await er.SaveMlcAsync(id, request, ct)).ToOk();
    }

    private static async Task<IResult> Intimate(Guid id, MlcIntimationRequest request, EmergencyService er, PatientAccessGuard guard, CancellationToken ct)
    {
        var opened = await OpenAsync(id, er, guard, ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : (await er.IntimateAsync(id, request, ct)).ToOk();
    }

    private static async Task<IResult> Document(
        Guid id, EmergencyService er, PatientAccessGuard guard, IAuditLogger audit, DocumentAssets assets, CancellationToken ct)
    {
        var opened = await OpenAsync(id, er, guard, ct);
        if (opened.IsFailure)
        {
            return opened.Error!.ToProblem();
        }

        if (await er.MlcAsync(opened.Value, ct) is not { } mlc)
        {
            return Error.NotFound("MLC entry", id).ToProblem();
        }

        await audit.PatientRecordAccessedAsync(opened.Value.PatientId, $"Printed MLC intimation {mlc.Entry.Number}", ct);
        return TypedResults.Ok(MlcDocument.Build(mlc.Entry, mlc.Patient, await assets.LetterheadAsync(mlc.Entry.FacilityId, ct)).ToDto());
    }

    private static async Task<IResult> Register(DateOnly? from, DateOnly? to, EmergencyService er, TimeProvider time, CancellationToken ct)
    {
        var end = to ?? time.Today();
        var start = from ?? end.AddDays(-30);
        if (end < start || end.DayNumber - start.DayNumber > 366)
        {
            return Error.Field("to", "Choose up to a year, ending on or after the start.").ToProblem();
        }

        return TypedResults.Ok(await er.RegisterAsync(start, end, ct));
    }

    private static async Task<Result<EmergencyCase>> OpenAsync(Guid id, EmergencyService er, PatientAccessGuard guard, CancellationToken ct)
    {
        if (await er.FindAsync(id, ct) is not { } c)
        {
            return Error.NotFound("Emergency case", id);
        }

        var allowed = await guard.EnsureCanOpenAsync(c.PatientId, ct);
        return allowed.IsFailure ? allowed.Error! : c;
    }
}

/// <summary>The hospital's intimation of a medico-legal case to the police station, with the register's facts.</summary>
internal static class MlcDocument
{
    public static PrintableDocument Build(MlcRecord m, Patient p, DocumentLetterhead letterhead)
    {
        var body = Html.Of($"""
            {DocumentParts.Letterhead(letterhead)}
            {DocumentParts.Title("Medico-legal case: intimation to the police")}
            <p>To the Station House Officer, {m.PoliceStation}.</p>
            <p>This is to inform you that the patient below was brought to this hospital and is a medico-legal case.</p>
            {DocumentParts.Fields([
                ("MLC no.", m.Number),
                ("Identified", DocumentFormat.DateTime(m.IdentifiedAt)),
                ("Patient", p.FullName),
                ("UHID", p.Uhid),
                ("Sex", p.Gender.ToString()),
                ("Brought by", m.BroughtByName is { } name ? $"{m.BroughtBy}: {name}" : m.BroughtBy.ToString()),
                ("Alleged cause", m.Cause.ToString()),
                ("Incident", string.Join(", ", new[] { m.IncidentAt is { } at ? DocumentFormat.DateTime(at) : null, m.IncidentPlace }.Where(s => s is not null))),
                ("Conscious on arrival", m.ConsciousOnArrival is { } c ? (c ? "Yes" : "No") : null),
                ("Smell of alcohol", m.SmellOfAlcohol is { } a ? (a ? "Yes" : "No") : null),
                ("Injuries", m.Injuries),
                ("Examining doctor", m.DoctorRegistrationNumber is { } reg ? $"{m.DoctorName} (Reg. {reg})" : m.DoctorName),
                ("Intimated", m.IntimatedAt is { } i ? $"{DocumentFormat.DateTime(i)}, {m.IntimationMode}, received by {m.ReceivedBy}" : "Not yet"),
                ("Police entry no.", m.PoliceEntryNumber),
            ])}
            """);
        return new PrintableDocument
        {
            Title = $"MLC intimation {m.Number}",
            FileName = $"mlc-{m.Number}",
            Body = body,
            Styles = DocumentParts.Styles,
            Footer = $"{m.Number} · Medico-legal: confidential",
            Template = letterhead.Template,
        };
    }
}
