using System.Text.Json;
using FluentValidation;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Documents;

/// <summary>One kind of document in Hospital setup › Print templates.</summary>
/// <param name="Papers">The papers to choose from, the usual one first; empty when the paper is fixed.</param>
/// <param name="HospitalWide">The template every facility prints with unless it has its own: the defaults until one is saved.</param>
/// <param name="Facility">The signed-in user's facility's own template; null when it prints with the hospital-wide one.</param>
/// <param name="HospitalWideVersion">The saved hospital-wide template's version; null while it is the defaults.</param>
/// <param name="FacilityVersion">The facility's own template's version; null when it has none.</param>
public sealed record PrintTemplateItem(
    string Key,
    string Name,
    string Area,
    string Covers,
    IReadOnlyList<DocumentPageSize> Papers,
    bool AllowsPreprinted,
    PrintTemplate HospitalWide,
    PrintTemplate? Facility,
    uint? HospitalWideVersion = null,
    uint? FacilityVersion = null);

/// <param name="FacilityName">The signed-in user's facility, whose own templates <see cref="PrintTemplateItem.Facility"/> shows.</param>
/// <param name="FacilityCount">How many facilities the hospital has: with one, there is no choice between hospital-wide and its own.</param>
public sealed record PrintTemplateList(Guid? FacilityId, string? FacilityName, int FacilityCount, IReadOnlyList<PrintTemplateItem> Templates);

/// <param name="ForThisFacility">True: the signed-in user's facility's own template. False: the hospital-wide one.</param>
/// <param name="Version">
/// The version of the template the change was made to (<see cref="PrintTemplateItem.HospitalWideVersion"/> or
/// <see cref="PrintTemplateItem.FacilityVersion"/>): 409 when someone saved it since (REL-04). Omit it when none was saved.
/// </param>
public sealed record SavePrintTemplateRequest(PrintTemplate? Template, bool ForThisFacility, uint? Version = null);

/// <summary>A template is sent; its rules depend on the kind of document (<see cref="PrintTemplateRules.Problems"/>), named by the route.</summary>
public sealed class SavePrintTemplateRequestValidator : AbstractValidator<SavePrintTemplateRequest>
{
    public SavePrintTemplateRequestValidator() => RuleFor(r => r.Template).NotNull().WithMessage("Send the template.");
}

/// <summary>
/// What the staff app prints its own documents with (80 mm bills and receipts, pharmacy, canteen and store slips): the
/// signed-in user's facility's letterhead, as the server's documents print it, and how each kind of document is printed
/// there.
/// </summary>
/// <param name="Logo">The logo as a data: URI ready to embed; null when the facility has none.</param>
/// <param name="Templates">Every template in <see cref="PrintTemplateCatalog"/> by key, within the rules.</param>
public sealed record PrintLetterheadDto(
    string Name,
    string? LegalName,
    string? Address,
    string? Phone,
    string? Email,
    string? Website,
    string? RegistrationNumber,
    string? NabhNumber,
    string? Gstin,
    string? Pan,
    string? Note,
    string? Logo,
    IReadOnlyDictionary<string, PrintTemplate> Templates);

/// <summary>
/// /api/v1/admin/print-templates (SRS HMS-04): how each kind of document is printed, hospital-wide or for one facility.
/// Changes apply to the next document printed. The server's documents read the templates through
/// <see cref="IPrintTemplates"/>, the staff app's own prints through /api/v1/print/letterhead (UX-09), which every staff
/// member may read: the letterhead is on every document the hospital hands out.
/// </summary>
internal static class PrintTemplateEndpoints
{
    public const string AuditAction = "print.template.changed";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Error FacilityRequired = Error.Rule("facility.required", "Select a facility first.");
    private static readonly Error StaffOnly = Error.Forbidden("print.staff_only", "Only hospital staff print with the letterhead.");

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/print-templates").WithTags("Administration").RequirePermission(PlatformPermissions.PrintManage);

        group.MapGet("/", List);
        group.MapPut("/{key}", Save).WithValidation<SavePrintTemplateRequest>().FirstSaveCreates();
        group.MapDelete("/{key}/facility", UseHospitalWide);

        app.MapGet("/api/v1/print/letterhead", Letterhead).WithTags("Documents").RequireAuthorization();
    }

    private static async Task<IResult> Letterhead(DocumentAssets assets, IPrintTemplates templates, ICurrentUser user, CancellationToken ct)
    {
        if (user.IsPatient)
        {
            return StaffOnly.ToProblem();
        }

        var letterhead = await assets.LetterheadAsync(user.FacilityId, ct);
        var byKey = new Dictionary<string, PrintTemplate>(StringComparer.Ordinal);
        foreach (var definition in PrintTemplateCatalog.All)
        {
            byKey[definition.Key] = await templates.GetAsync(definition.Key, user.FacilityId, ct);
        }

        return TypedResults.Ok(new PrintLetterheadDto(
            letterhead.Name,
            letterhead.LegalName,
            letterhead.Address,
            letterhead.Phone,
            letterhead.Email,
            letterhead.Website,
            letterhead.RegistrationNumber,
            letterhead.NabhNumber,
            letterhead.Gstin,
            letterhead.Pan,
            letterhead.Note,
            letterhead.Logo?.DataUri,
            byKey));
    }

    private static async Task<IResult> List(HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var keys = PrintTemplateCatalog.All.Select(d => PrintTemplateCatalog.SettingKey(d.Key)).ToList();
        var rows = await db.Set<SystemSetting>().AsNoTracking()
            .Where(s => keys.Contains(s.Key) && (s.FacilityId == null || s.FacilityId == user.FacilityId))
            .ToListAsync(ct);
        var facilityName = user.FacilityId is { } facilityId
            ? await db.Set<Facility>().AsNoTracking().Where(f => f.Id == facilityId).Select(f => f.Name).FirstOrDefaultAsync(ct)
            : null;
        var facilityCount = await db.Set<Facility>().CountAsync(ct);

        var templates = PrintTemplateCatalog.All.Select(definition =>
        {
            var key = PrintTemplateCatalog.SettingKey(definition.Key);
            var hospitalWideRow = rows.FirstOrDefault(r => r.Key == key && r.FacilityId == null);
            var ownRow = user.FacilityId is null ? null : rows.FirstOrDefault(r => r.Key == key && r.FacilityId == user.FacilityId);
            var hospitalWide = Read(definition, hospitalWideRow) ?? PrintTemplateRules.Normalize(definition, PrintTemplate.Default);
            var own = Read(definition, ownRow);
            return new PrintTemplateItem(
                definition.Key, definition.Name, definition.Area, definition.Covers, definition.Papers, definition.AllowsPreprinted, hospitalWide, own,
                hospitalWideRow?.Version, ownRow?.Version);
        }).ToList();
        return TypedResults.Ok(new PrintTemplateList(user.FacilityId, facilityName, facilityCount, templates));
    }

    private static async Task<IResult> Save(
        string key, SavePrintTemplateRequest request, ISettingsService settings, ICurrentUser user, IAuditLogger audit, CancellationToken ct)
    {
        if (PrintTemplateCatalog.Find(key) is not { } definition)
        {
            return Error.NotFound("Print template", key).ToProblem();
        }

        var template = request.Template!;
        var problems = PrintTemplateRules.Problems(definition, template);
        if (problems.Count > 0)
        {
            var fields = problems.GroupBy(p => p.Field).ToDictionary(g => g.Key, g => g.Select(p => p.Message).ToArray());
            return Error.Validation("validation", problems[0].Message, fields).ToProblem();
        }

        Guid? facilityId = null;
        if (request.ForThisFacility)
        {
            if (user.FacilityId is not { } id)
            {
                return FacilityRequired.ToProblem();
            }

            facilityId = id;
        }

        var saved = PrintTemplateRules.Normalize(definition, template);
        await settings.SetAsync(PrintTemplateCatalog.SettingKey(key), saved, facilityId, secret: false, request.Version, ct);
        await audit.WriteAsync(new AuditEntry(AuditCategories.Admin, AuditAction,
            $"Changed how {definition.Name.ToLowerInvariant()} print{(facilityId is null ? " hospital-wide" : " at this facility")}")
        {
            EntityType = "PrintTemplate",
            EntityId = key,
            Details = new { facilityId, template = saved },
        }, ct);
        return TypedResults.NoContent();
    }

    /// <summary>Removes the facility's own template, so it prints with the hospital-wide one again.</summary>
    private static async Task<IResult> UseHospitalWide(string key, ISettingsService settings, ICurrentUser user, IAuditLogger audit, CancellationToken ct)
    {
        if (PrintTemplateCatalog.Find(key) is not { } definition)
        {
            return Error.NotFound("Print template", key).ToProblem();
        }

        if (user.FacilityId is not { } facilityId)
        {
            return FacilityRequired.ToProblem();
        }

        if (await settings.RemoveAsync(PrintTemplateCatalog.SettingKey(key), facilityId, ct))
        {
            await audit.WriteAsync(new AuditEntry(AuditCategories.Admin, AuditAction,
                $"{definition.Name} print with the hospital-wide template again at this facility")
            {
                EntityType = "PrintTemplate",
                EntityId = key,
                Details = new { facilityId, hospitalWide = true },
            }, ct);
        }

        return TypedResults.NoContent();
    }

    /// <summary>A stored template within the rules; null when there is none, or it is not a template (set by hand).</summary>
    private static PrintTemplate? Read(PrintTemplateDefinition definition, SystemSetting? row)
    {
        if (row is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PrintTemplate>(row.Value, JsonOptions) is { } template
                ? PrintTemplateRules.Normalize(definition, template)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
