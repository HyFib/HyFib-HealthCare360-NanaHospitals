using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;

/// <summary>/api/v1/mrd — clinical coding at discharge (MRD-02) and the morbidity index (MRD-06).</summary>
internal static class CodingEndpoints
{
    /// <summary>Longest morbidity-report period (keeps the report query bounded).</summary>
    private const int MaxReportDays = 366;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/mrd").WithTags("Medical Records");

        group.MapGet("/codings", List).RequirePermission(ClinicalPermissions.MrdCoding);
        group.MapPost("/codings", Create).RequirePermission(ClinicalPermissions.MrdCoding).WithValidation<CreateCodingRequest>();
        group.MapGet("/codings/{id:guid}", Get).RequirePermission(ClinicalPermissions.MrdCoding);
        group.MapPut("/codings/{id:guid}", Save).RequirePermission(ClinicalPermissions.MrdCoding).WithValidation<SaveCodingRequest>();
        group.MapPost("/codings/{id:guid}/verify", Verify).RequirePermission(ClinicalPermissions.MrdCoding);
        group.MapGet("/codings/{id:guid}/suggestions", Suggestions).RequirePermission(ClinicalPermissions.MrdCoding);
        group.MapGet("/reports/morbidity", Morbidity).RequirePermission(ClinicalPermissions.MrdRecords);
    }

    /// <summary>Coding worklist (pending first), optionally by status and episode date (IST).</summary>
    private static async Task<IResult> List([AsParameters] PageQuery query, CodingStatus? status, DateOnly? from, DateOnly? to, HospitalDbContext db,
        CancellationToken ct)
    {
        var codings = db.Set<EncounterCoding>().AsNoTracking();
        if (status is not null)
        {
            codings = codings.Where(c => c.Status == status);
        }

        if (from is not null)
        {
            codings = codings.Where(c => c.EpisodeDate >= from);
        }

        if (to is not null)
        {
            codings = codings.Where(c => c.EpisodeDate <= to);
        }

        var page = await (
                from c in codings
                join p in db.Set<Patient>() on c.PatientId equals p.Id
                // Enums are stored as strings, so order explicitly: pending work first, oldest episodes first.
                orderby c.Status == CodingStatus.Pending descending, c.Status == CodingStatus.Coded descending, c.EpisodeDate
                select new { Coding = c, p.Uhid, p.FullName })
            .ToPagedResultAsync(query.ToRequest(), ct);
        return TypedResults.Ok(page.Map(x => new CodingDto(x.Coding.Id, x.Coding.PatientId, x.Uhid, x.FullName, x.Coding.EncounterId, x.Coding.AdmissionId,
            x.Coding.VisitId, x.Coding.EpisodeDate, x.Coding.Status, x.Coding.CoderUserId, x.Coding.CodedAt, x.Coding.VerifiedByUserId, x.Coding.VerifiedAt,
            x.Coding.Notes, [], x.Coding.Version)));
    }

    private static async Task<IResult> Create(CreateCodingRequest request, CodingService coding, CancellationToken ct) =>
        (await coding.CreateAsync(request, ct)).ToCreated(c => $"/api/v1/mrd/codings/{c.Id}");

    private static async Task<IResult> Get(Guid id, CodingService coding, ClinicalAccess access, CancellationToken ct)
    {
        var entry = await coding.Codings().AsNoTracking().Include(c => c.Entries).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entry is null)
        {
            return Error.NotFound("Coding", id).ToProblem();
        }

        var opened = await access.OpenAsync(entry.PatientId, "Viewed clinical coding", ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : TypedResults.Ok(await coding.ToDtoAsync(entry, ct));
    }

    private static async Task<IResult> Save(Guid id, SaveCodingRequest request, CodingService coding, CancellationToken ct) =>
        (await coding.SaveAsync(id, request, ct)).ToOk();

    private static async Task<IResult> Verify(Guid id, VerifyCodingRequest request, CodingService coding, CancellationToken ct) =>
        (await coding.VerifyAsync(id, request.Version, ct)).ToOk();

    private static async Task<IResult> Suggestions(Guid id, CodingService coding, CancellationToken ct) =>
        (await coding.SuggestionsAsync(id, ct)).ToOk();

    /// <summary><c>groupBy</c>: code | block | chapter. <c>source</c>: clinical (doctors' diagnoses) | coded (MRD coding).</summary>
    private static async Task<IResult> Morbidity(DateOnly? from, DateOnly? to, string? groupBy, string? source, CodingService coding, TimeProvider time,
        CancellationToken ct)
    {
        var end = to ?? time.Today();
        var start = from ?? end.AddDays(-29);
        if (start > end || end.DayNumber - start.DayNumber >= MaxReportDays)
        {
            return Error.Field("from", "Choose a period of up to one year.").ToProblem();
        }

        return TypedResults.Ok(await coding.MorbidityAsync(start, end, groupBy ?? "code", source ?? "clinical", ct));
    }
}
