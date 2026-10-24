using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;

/// <summary>/api/v1/radiology/templates and /procedures: reporting templates (IMG-03) and the imaging procedure master (IMG-01).</summary>
internal static class RadiologyMasterEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var templates = app.MapGroup("/api/v1/radiology/templates").WithTags("Radiology masters");
        templates.MapGet("/", ListTemplates).RequireAnyPermission(DiagnosticsPermissions.RadiologyReportsWrite, DiagnosticsPermissions.RadiologyReportsVerify);
        templates.MapPost("/", (SaveReportTemplateRequest r, HospitalDbContext db, CancellationToken ct) => SaveTemplate(null, r, db, ct))
            .RequirePermission(DiagnosticsPermissions.RadiologyReportsVerify).WithValidation<SaveReportTemplateRequest>();
        templates.MapPut("/{id:guid}", (Guid id, SaveReportTemplateRequest r, HospitalDbContext db, CancellationToken ct) => SaveTemplate(id, r, db, ct))
            .RequirePermission(DiagnosticsPermissions.RadiologyReportsVerify).WithValidation<SaveReportTemplateRequest>().RequireVersion();

        var procedures = app.MapGroup("/api/v1/radiology/procedures").WithTags("Radiology masters");
        procedures.MapGet("/", ListProcedures)
            .RequireAnyPermission(DiagnosticsPermissions.RadiologyRead, DiagnosticsPermissions.RadiologyWorklist, DiagnosticsPermissions.RadiologyReportsVerify,
                PlatformPermissions.MastersManage);
        procedures.MapPost("/", (SaveImagingProcedureRequest r, HospitalDbContext db, CancellationToken ct) => SaveProcedure(null, r, db, ct))
            .RequireAnyPermission(DiagnosticsPermissions.RadiologyReportsVerify, PlatformPermissions.MastersManage).WithValidation<SaveImagingProcedureRequest>();
        procedures.MapPut("/{id:guid}", (Guid id, SaveImagingProcedureRequest r, HospitalDbContext db, CancellationToken ct) => SaveProcedure(id, r, db, ct))
            .RequireAnyPermission(DiagnosticsPermissions.RadiologyReportsVerify, PlatformPermissions.MastersManage).WithValidation<SaveImagingProcedureRequest>().RequireVersion();
    }

    /// <summary>Templates for a modality; those matching the body part come first, then generic ones.</summary>
    private static async Task<IResult> ListTemplates(Modality? modality, string? bodyPart, bool? includeInactive, HospitalDbContext db, CancellationToken ct)
    {
        var templates = await db.Set<ReportTemplate>().AsNoTracking()
            .Where(t => (includeInactive == true || t.IsActive) && (modality == null || t.Modality == modality))
            .ToListAsync(ct);
        var part = bodyPart?.Trim();
        return TypedResults.Ok(templates
            .OrderByDescending(t => part is not null && string.Equals(t.BodyPart, part, StringComparison.OrdinalIgnoreCase))
            .ThenBy(t => t.BodyPart is null)
            .ThenBy(t => t.Name)
            .Select(t => new ReportTemplateDto(t.Id, t.Code, t.Name, t.Modality, t.BodyPart, t.Technique, t.Findings, t.Impression, t.IsActive, t.Version))
            .ToList());
    }

    private static async Task<IResult> SaveTemplate(Guid? id, SaveReportTemplateRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim();
        ReportTemplate template;
        if (id is { } existingId)
        {
            var found = await db.Set<ReportTemplate>().FirstOrDefaultAsync(t => t.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(ReportTemplate), existingId).ToProblem();
            }

            template = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(template, version);
            }
        }
        else
        {
            template = new ReportTemplate();
            db.Add(template);
        }

        if (!string.Equals(template.Code, code, StringComparison.Ordinal)
            && await db.Set<ReportTemplate>().IgnoreQueryFilters().AnyAsync(t => t.Code == code && t.Id != template.Id, ct))
        {
            return Error.Conflict("radiology.template_code_exists", $"A template with code {code} already exists.").ToProblem();
        }

        template.Code = code;
        template.Name = request.Name.Trim();
        template.Modality = request.Modality;
        template.BodyPart = string.IsNullOrWhiteSpace(request.BodyPart) ? null : request.BodyPart.Trim();
        template.Technique = string.IsNullOrWhiteSpace(request.Technique) ? null : request.Technique.Trim();
        template.Findings = request.Findings.Trim();
        template.Impression = request.Impression.Trim();
        template.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/radiology/templates/{template.Id}", new { template.Id }) : TypedResults.NoContent();
    }

    private static async Task<IResult> ListProcedures(Modality? modality, bool? includeInactive, HospitalDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Set<ImagingProcedure>().AsNoTracking()
            .Where(p => (includeInactive == true || p.IsActive) && (modality == null || p.Modality == modality))
            .OrderBy(p => p.Modality).ThenBy(p => p.Name)
            .Select(p => new ImagingProcedureDto(
                p.Id, p.Code, p.Name, p.Modality, p.BodyPart, p.ServiceItemId, p.DurationMinutes, p.RequiresContrast, p.PatientPreparation, p.IsActive, p.Version))
            .ToListAsync(ct));

    private static async Task<IResult> SaveProcedure(Guid? id, SaveImagingProcedureRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim();
        ImagingProcedure procedure;
        if (id is { } existingId)
        {
            var found = await db.Set<ImagingProcedure>().FirstOrDefaultAsync(p => p.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(ImagingProcedure), existingId).ToProblem();
            }

            procedure = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(procedure, version);
            }
        }
        else
        {
            procedure = new ImagingProcedure();
            db.Add(procedure);
        }

        if (!string.Equals(procedure.Code, code, StringComparison.Ordinal)
            && await db.Set<ImagingProcedure>().IgnoreQueryFilters().AnyAsync(p => p.Code == code && p.Id != procedure.Id, ct))
        {
            return Error.Conflict("radiology.procedure_code_exists", $"A procedure with code {code} already exists.").ToProblem();
        }

        if (request.ServiceItemId is { } serviceItemId && !await db.Set<ServiceItem>().AnyAsync(s => s.Id == serviceItemId, ct))
        {
            return Error.Field("serviceItemId", "Choose an existing billable service.").ToProblem();
        }

        procedure.Code = code;
        procedure.Name = request.Name.Trim();
        procedure.Modality = request.Modality;
        procedure.BodyPart = request.BodyPart.Trim();
        procedure.ServiceItemId = request.ServiceItemId;
        procedure.DurationMinutes = request.DurationMinutes;
        procedure.RequiresContrast = request.RequiresContrast;
        procedure.PatientPreparation = string.IsNullOrWhiteSpace(request.PatientPreparation) ? null : request.PatientPreparation.Trim();
        procedure.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/radiology/procedures/{procedure.Id}", new { procedure.Id }) : TypedResults.NoContent();
    }
}
