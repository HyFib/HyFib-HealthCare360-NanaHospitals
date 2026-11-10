using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Quality.Features.Mvpi;

/// <summary>
/// /api/v1/quality/mvpi — adverse events with medical devices, reported to the Materiovigilance Programme of India on its
/// MDAE form. The quality team (the incident register's managers) raises them from incidents or for a patient, and records
/// them as sent with MvPI's reference.
/// </summary>
internal static class MvpiEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/quality/mvpi").WithTags("Quality");

        group.MapGet("", List).RequirePermission(QualityPermissions.IncidentsManage);
        group.MapGet("/prefill", Prefill).RequirePermission(QualityPermissions.IncidentsManage);
        group.MapGet("/{id:guid}", Get).RequirePermission(QualityPermissions.IncidentsManage);
        group.MapGet("/{id:guid}/document", Document).RequirePermission(QualityPermissions.IncidentsManage);
        group.MapGet("/{id:guid}/pdf", Pdf).RequirePermission(QualityPermissions.IncidentsManage);
        group.MapPost("", Raise).RequirePermission(QualityPermissions.IncidentsManage).WithValidation<SaveMvpiReportRequest>();
        group.MapPut("/{id:guid}", Correct).RequirePermission(QualityPermissions.IncidentsManage).WithValidation<SaveMvpiReportRequest>().RequireVersion();
        group.MapPost("/{id:guid}/submit", Submit).RequirePermission(QualityPermissions.IncidentsManage).WithValidation<SubmitMvpiReportRequest>();
        group.MapPost("/{id:guid}/reference", CorrectReference).RequirePermission(QualityPermissions.IncidentsManage)
            .WithValidation<CorrectMvpiReferenceRequest>();
        group.MapPost("/{id:guid}/follow-up", FollowUp).RequirePermission(QualityPermissions.IncidentsManage);
    }

    private static async Task<IResult> List(MvpiReportStatus? status, DateOnly? from, DateOnly? to, Guid? incidentId, MvpiService mvpi, CancellationToken ct) =>
        TypedResults.Ok(await mvpi.ListAsync(status, from, to, incidentId, ct));

    private static async Task<IResult> Prefill(Guid? incidentId, Guid? patientId, MvpiService mvpi, MvpiAccess access, CancellationToken ct)
    {
        var prefill = await mvpi.PrefillAsync(incidentId, patientId, ct);
        if (prefill.IsFailure)
        {
            return prefill.Error!.ToProblem();
        }

        if (prefill.Value.PatientId is { } patient)
        {
            var opened = await access.OpenPatientAsync(patient, "Looked up the devices billed to the patient for an MvPI report", ct);
            if (opened.IsFailure)
            {
                return opened.Error!.ToProblem();
            }
        }

        return TypedResults.Ok(prefill.Value);
    }

    private static async Task<IResult> Get(Guid id, MvpiService mvpi, MvpiAccess access, CancellationToken ct)
    {
        if (await mvpi.FindAsync(id, ct) is not { } report)
        {
            return Error.NotFound("MvPI report", id).ToProblem();
        }

        var opened = await access.OpenAsync(report, $"Viewed MvPI report {report.Number}", ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : TypedResults.Ok(await mvpi.DtoAsync(report, ct));
    }

    private static async Task<IResult> Document(Guid id, MvpiService mvpi, MvpiAccess access, DocumentAssets assets, CancellationToken ct)
    {
        var (document, problem) = await BuildAsync(id, "Printed", mvpi, access, assets, ct);
        return problem ?? TypedResults.Ok(document!.ToDto());
    }

    private static async Task<IResult> Pdf(Guid id, MvpiService mvpi, MvpiAccess access, DocumentAssets assets, IPdfRenderer renderer, CancellationToken ct)
    {
        var (document, problem) = await BuildAsync(id, "Downloaded as a PDF", mvpi, access, assets, ct);
        return problem ?? await document!.ToPdfAsync(renderer, ct);
    }

    private static async Task<IResult> Raise(SaveMvpiReportRequest request, MvpiService mvpi, MvpiAccess access, CancellationToken ct)
    {
        if (request.PatientId is { } patient && await access.OpenPatientAsync(patient, null, ct) is { IsFailure: true } refused)
        {
            return refused.Error!.ToProblem();
        }

        return (await mvpi.SaveAsync(null, request, ct)).ToOk();
    }

    private static async Task<IResult> Correct(Guid id, SaveMvpiReportRequest request, MvpiService mvpi, MvpiAccess access, CancellationToken ct)
    {
        if (await mvpi.FindAsync(id, ct) is not { } report)
        {
            return Error.NotFound("MvPI report", id).ToProblem();
        }

        // The patient it names now, and the one it is to name, must both be open to the officer.
        var opened = await access.OpenAsync(report, null, ct);
        if (opened.IsFailure)
        {
            return opened.Error!.ToProblem();
        }

        if (request.PatientId is { } patient && patient != report.PatientId && await access.OpenPatientAsync(patient, null, ct) is { IsFailure: true } refused)
        {
            return refused.Error!.ToProblem();
        }

        return (await mvpi.SaveAsync(id, request, ct)).ToOk();
    }

    // Sending and correcting the reference answer with the whole report, so they too need the patient open to the officer.
    private static async Task<IResult> Submit(Guid id, SubmitMvpiReportRequest request, MvpiService mvpi, MvpiAccess access, CancellationToken ct) =>
        await OpenedAsync(id, mvpi, access, ct) ?? (await mvpi.SubmitAsync(id, request, ct)).ToOk();

    private static async Task<IResult> CorrectReference(
        Guid id, CorrectMvpiReferenceRequest request, MvpiService mvpi, MvpiAccess access, CancellationToken ct) =>
        await OpenedAsync(id, mvpi, access, ct) ?? (await mvpi.CorrectReferenceAsync(id, request, ct)).ToOk();

    /// <summary>Null when the report exists and the officer may open the patient it names; otherwise the problem to answer.</summary>
    private static async Task<IResult?> OpenedAsync(Guid id, MvpiService mvpi, MvpiAccess access, CancellationToken ct)
    {
        if (await mvpi.FindAsync(id, ct) is not { } report)
        {
            return Error.NotFound("MvPI report", id).ToProblem();
        }

        var opened = await access.OpenAsync(report, null, ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : null;
    }

    private static async Task<IResult> FollowUp(Guid id, MvpiService mvpi, MvpiAccess access, CancellationToken ct)
    {
        if (await mvpi.FindAsync(id, ct) is not { } report)
        {
            return Error.NotFound("MvPI report", id).ToProblem();
        }

        var opened = await access.OpenAsync(report, null, ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : (await mvpi.FollowUpAsync(id, ct)).ToOk();
    }

    private static async Task<(PrintableDocument? Document, IResult? Problem)> BuildAsync(
        Guid id, string how, MvpiService mvpi, MvpiAccess access, DocumentAssets assets, CancellationToken ct)
    {
        if (await mvpi.FindAsync(id, ct) is not { } report)
        {
            return (null, Error.NotFound("MvPI report", id).ToProblem());
        }

        var opened = await access.OpenAsync(report, $"{how} MvPI report {report.Number}", ct);
        if (opened.IsFailure)
        {
            return (null, opened.Error!.ToProblem());
        }

        return (MvpiDocument.Build(await mvpi.DtoAsync(report, ct), await assets.LetterheadAsync(report.FacilityId, ct)), null);
    }
}

/// <summary>
/// Opening a report that names a patient: only when the officer may open the patient's record (a restricted record stays
/// closed), and a read is audited as PHI access.
/// </summary>
internal sealed class MvpiAccess(PatientAccessGuard guard, IAuditLogger audit)
{
    public Task<Result> OpenAsync(MvpiReport report, string? what, CancellationToken ct) =>
        report.PatientId is { } patient ? OpenPatientAsync(patient, what, ct) : Task.FromResult(Result.Success());

    public async Task<Result> OpenPatientAsync(Guid patientId, string? what, CancellationToken ct)
    {
        var allowed = await guard.EnsureCanOpenAsync(patientId, ct);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        if (what is not null)
        {
            await audit.PatientRecordAccessedAsync(patientId, what, ct);
        }

        return Result.Success();
    }
}

/// <summary>
/// The hospital's copy of the MvPI medical device adverse event (MDAE) reporting form, in the form's order: the report,
/// the patient, the event, the device, the reporter, and the record of sending it. Choices print as ticked boxes, as on
/// the paper form, so the copy reads like it.
/// </summary>
internal static class MvpiDocument
{
    private const string Styles = """
        .section { margin-top: 8px; break-inside: avoid; }
        .section h2 { font-size: 10.5pt; margin: 0 0 4px; background: #E8F1F5; padding: 2px 6px; }
        .choices { margin: 2px 0 6px; font-size: 9pt; }
        .choices .label { color: #555; margin-right: 8px; }
        .choice { margin-right: 12px; white-space: nowrap; }
        .box { display: inline-block; width: 2.6mm; height: 2.6mm; border: 1px solid #333; margin-right: 3px; vertical-align: -1px; }
        .box.on { background: #333; }
        .text { border: 1px solid #999; border-radius: 4px; padding: 4px 8px; margin-bottom: 6px; font-size: 9.5pt; }
        .sign-line { margin-top: 14mm; border-top: 1px solid #666; width: 60mm; text-align: center; font-size: 9pt; }

        """;

    public static PrintableDocument Build(MvpiReportDto r, DocumentLetterhead letterhead)
    {
        var d = r.Device;
        var reportType = r.FollowUpOfNumber is { } original
            ? $"Follow-up report to {original}{(r.FollowUpOfReference is { } reference ? $" (MvPI reference {reference})" : string.Empty)}"
            : "Initial report";
        var body = Html.Of($"""
            {DocumentParts.Letterhead(letterhead)}
            {DocumentParts.Title("Medical device adverse event reporting form (MvPI)")}
            {DocumentParts.Banner(r.Status == MvpiReportStatus.Draft ? "Draft: not yet sent to MvPI" : null)}
            {DocumentParts.Fields([
                ("Report no.", r.Number),
                ("Date of report", DocumentFormat.Date(r.ReportedOn)),
                ("Type of report", reportType),
                ("Incident report", r.IncidentNumber),
            ])}
            <section class="section">
              <h2>A. Patient information</h2>
              {(r.NoPatient
                  ? Html.Of($"<div class=\"choices\">{Box(true, "No patient involved")}</div>")
                  : DocumentParts.Fields([
                      ("Patient initials", r.PatientInitials),
                      ("Age at the time of the event", r.AgeYears is { } age ? $"{age} years" : null),
                      ("Sex", r.Sex),
                      ("Weight", r.WeightKg is { } kg ? $"{kg:0.#} kg" : null),
                  ]))}
            </section>
            <section class="section">
              <h2>B. Adverse event</h2>
              {DocumentParts.Fields([
                  ("Date of the event", DocumentFormat.Date(r.EventOn)),
                  ("Date the hospital became aware", DocumentFormat.Date(r.AwareOn)),
                  ("Location of the event", r.Location),
                  ("Date of death", r.DeathOn is { } died ? DocumentFormat.Date(died) : null),
              ])}
              <div class="text">{Html.Lines(r.Description ?? DocumentFormat.Missing)}</div>
              {Choices("Seriousness", Enum.GetValues<MvpiSeriousness>().Where(s => s != MvpiSeriousness.None).Select(s => (r.Seriousness.HasFlag(s), Label(s)))
                  .Prepend((r.Seriousness == MvpiSeriousness.None, "Not serious")))}
              {Choices("Patient outcome", Enum.GetValues<MvpiPatientOutcome>().Select(o => (r.Outcome == o, Label(o))))}
            </section>
            <section class="section">
              <h2>C. Medical device</h2>
              {DocumentParts.Fields([
                  ("Brand / trade name", d.Name),
                  ("Common name or category", d.GenericName),
                  ("Manufacturer", d.Manufacturer),
                  ("Manufacturer's address", d.ManufacturerAddress),
                  ("Importer (if imported)", d.Importer),
                  ("Model no.", d.ModelNumber),
                  ("Catalogue no.", d.CatalogueNumber),
                  ("Serial no.", d.SerialNumber),
                  ("Lot / batch no.", d.LotNumber),
                  ("UDI", d.Udi),
                  ("Date of manufacture", d.ManufacturedOn is { } made ? DocumentFormat.Date(made) : null),
                  ("Expiry date", d.ExpiresOn is { } expires ? DocumentFormat.Date(expires) : null),
                  ("Software version", d.SoftwareVersion),
                  ("Date of implant", d.ImplantedOn is { } implanted ? DocumentFormat.Date(implanted) : null),
                  ("Date of explant", d.ExplantedOn is { } explanted ? DocumentFormat.Date(explanted) : null),
              ])}
              {Choices("Risk class (MDR 2017)", Enum.GetValues<DeviceRiskClass>().Select(c => (d.RiskClass == c, $"Class {c}")).Append((d.RiskClass is null, "Not known")))}
              {Choices("Implantable", [(d.Implantable, "Yes"), (!d.Implantable, "No")])}
              {Choices("Use", [(d.Use == DeviceUse.SingleUse, "Single use"), (d.Use == DeviceUse.Reusable, "Reusable"), (d.Reprocessed, "Reprocessed")])}
              {Choices("Operator of the device", [(d.OperatedBy == DeviceOperator.HealthProfessional, "Health professional"), (d.OperatedBy == DeviceOperator.Patient, "Patient"), (d.OperatedBy == DeviceOperator.Other, "Other")])}
              {Choices("Available for evaluation", [
                  (d.Availability == DeviceAvailability.Available, d.KeptAt is { } kept ? $"Yes, kept at {kept}" : "Yes"),
                  (d.Availability == DeviceAvailability.NotAvailable, "No"),
                  (d.Availability == DeviceAvailability.ReturnedToManufacturer,
                      d.ReturnedOn is { } returned ? $"Returned to the manufacturer on {DocumentFormat.Date(returned)}" : "Returned to the manufacturer"),
              ])}
            </section>
            <section class="section">
              <h2>D. Reporter</h2>
              {DocumentParts.Fields([
                  ("Name", r.ReporterName),
                  ("Designation", r.ReporterDesignation),
                  ("Department", r.ReporterDepartment),
                  ("Institution", letterhead.Name),
                  ("Address", letterhead.Address),
                  ("Phone", letterhead.Phone),
                  ("E-mail", letterhead.Email),
                  ("Date of report", DocumentFormat.Date(r.ReportedOn)),
              ])}
              <div class="sign-line">Signature of the reporter</div>
            </section>
            <section class="section">
              <h2>E. Sent to MvPI</h2>
              {DocumentParts.Fields([
                  ("Sent on", r.SubmittedOn is { } sent ? DocumentFormat.Date(sent) : "Not yet sent"),
                  ("Sent by", r.SubmittedByName),
                  ("MvPI / MDMC reference", r.Reference),
                  ("Reference corrected", r.ReferenceCorrectionReason is { } why
                      ? $"{DocumentFormat.Date(r.ReferenceCorrectedAt)} by {r.ReferenceCorrectedByName}: {why}"
                      : null),
              ])}
            </section>
            """);
        return new PrintableDocument
        {
            Title = $"MvPI report {r.Number}",
            FileName = $"mvpi-{r.Number}",
            Body = body,
            Styles = DocumentParts.Styles + Styles,
            Footer = $"{r.PatientInitials ?? "No patient"} · {r.Number} · Confidential: the patient's and the reporter's identity are held in confidence",
            Template = letterhead.Template,
        };
    }

    private static Html Box(bool on, string label) =>
        Html.Of($"<span class=\"choice\"><span class=\"box{(on ? " on" : string.Empty)}\"></span>{label}</span>");

    private static Html Choices(string label, IEnumerable<(bool On, string Label)> choices) =>
        Html.Of($"<div class=\"choices\"><span class=\"label\">{label}</span>{Html.Join(choices.Select(c => Box(c.On, c.Label)))}</div>");

    private static string Label(MvpiSeriousness s) => s switch
    {
        MvpiSeriousness.Death => "Death",
        MvpiSeriousness.LifeThreatening => "Life-threatening",
        MvpiSeriousness.Hospitalisation => "Hospitalisation, initial or prolonged",
        MvpiSeriousness.Disability => "Disability",
        MvpiSeriousness.CongenitalAnomaly => "Congenital anomaly",
        MvpiSeriousness.RequiredIntervention => "Required intervention to prevent permanent impairment or damage",
        _ => "Other medically important",
    };

    private static string Label(MvpiPatientOutcome o) => o switch
    {
        MvpiPatientOutcome.Recovered => "Recovered",
        MvpiPatientOutcome.Recovering => "Recovering",
        MvpiPatientOutcome.NotRecovered => "Not recovered",
        MvpiPatientOutcome.Fatal => "Fatal",
        _ => "Unknown",
    };
}
