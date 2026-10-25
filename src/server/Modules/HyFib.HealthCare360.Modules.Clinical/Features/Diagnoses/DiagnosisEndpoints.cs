using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Diagnoses;

/// <summary>/api/v1/emr — diagnoses of encounters and the patient's problem list (EMR-04, EHR-01).</summary>
internal static class DiagnosisEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/emr").WithTags("EMR");

        group.MapGet("/patients/{patientId:guid}/diagnoses", List).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapPost("/encounters/{encounterId:guid}/diagnoses", Add).RequirePermission(ClinicalPermissions.EmrWrite).WithValidation<AddDiagnosisRequest>();
        group.MapPut("/diagnoses/{id:guid}", Update).RequirePermission(ClinicalPermissions.EmrWrite).WithValidation<UpdateDiagnosisRequest>();
    }

    /// <summary><c>problemsOnly</c> returns the active/chronic problem list (differentials excluded).</summary>
    private static async Task<IResult> List(Guid patientId, Guid? encounterId, bool? problemsOnly, ClinicalAccess access, HospitalDbContext db, CancellationToken ct)
    {
        var opened = await access.OpenAsync(patientId, "Viewed diagnoses", ct);
        if (opened.IsFailure)
        {
            return opened.Error!.ToProblem();
        }

        var diagnoses = db.Set<Diagnosis>().AsNoTracking().Where(d => d.PatientId == patientId);
        if (encounterId is not null)
        {
            diagnoses = diagnoses.Where(d => d.EncounterId == encounterId);
        }

        if (problemsOnly == true)
        {
            diagnoses = diagnoses.Where(d => d.Type != DiagnosisType.Differential && (d.Status == DiagnosisStatus.Active || d.Status == DiagnosisStatus.Chronic));
        }

        var rows = await diagnoses.OrderByDescending(d => d.RecordedAt).Take(500).ToListAsync(ct);
        return TypedResults.Ok(rows.Select(ToDto).ToList());
    }

    private static async Task<IResult> Add(Guid encounterId, AddDiagnosisRequest request, ClinicalAccess access, NotifiableDiseases notifiable,
        HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        var encounter = await db.Set<Encounter>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(e => e.Id == encounterId, ct);
        if (encounter is null)
        {
            return Error.NotFound(nameof(Encounter), encounterId).ToProblem();
        }

        var allowed = await access.EnsureCanRecordAsync(encounter.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        string? code = null;
        var title = request.Title?.Trim();
        if (!string.IsNullOrWhiteSpace(request.IcdCode))
        {
            var normalized = IcdSearchRanker.NormalizeCode(request.IcdCode);
            var catalogue = await db.Set<IcdCode>().AsNoTracking().Where(c => c.Code == normalized && c.IsActive)
                .OrderBy(c => c.Release == CodeReleases.Icd10 ? 0 : 1).FirstOrDefaultAsync(ct);
            if (catalogue is null)
            {
                return Error.Field("icdCode", $"{normalized} is not in the ICD catalogue.").ToProblem();
            }

            code = catalogue.Code;
            title = catalogue.Title;
        }

        if (request.IsPrimary)
        {
            await ClearPrimaryAsync(db, encounterId, null, ct);
        }

        var diagnosis = new Diagnosis
        {
            PatientId = encounter.PatientId,
            EncounterId = encounterId,
            IcdCode = code,
            Title = title!,
            Type = request.Type,
            IsPrimary = request.IsPrimary,
            OnsetDate = request.OnsetDate,
            RecordedAt = time.GetUtcNow(),
            RecordedByUserId = user.UserId,
            Notes = request.Notes?.Trim(),
        };
        diagnosis.ChangeStatus(request.Status, time.Today());
        db.Add(diagnosis);

        // An HIV, mental-illness or abortion diagnosis makes the record confidential (R-4), in the same save.
        await access.FlagSensitiveAsync(encounter.PatientId, code, ct);

        // A notifiable disease (IDSP) goes on the week's P form, in the same save.
        await notifiable.FromDiagnosisAsync(encounter.FacilityId, diagnosis, ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/emr/patients/{encounter.PatientId}/diagnoses", ToDto(diagnosis));
    }

    private static async Task<IResult> Update(Guid id, UpdateDiagnosisRequest request, ClinicalAccess access, HospitalDbContext db, TimeProvider time,
        CancellationToken ct)
    {
        var diagnosis = await db.Set<Diagnosis>().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (diagnosis is null)
        {
            return Error.NotFound(nameof(Diagnosis), id).ToProblem();
        }

        var allowed = await access.EnsureCanOpenAsync(diagnosis.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        db.SetExpectedVersion(diagnosis, request.Version);
        if (request.IsPrimary && !diagnosis.IsPrimary && diagnosis.EncounterId is { } encounterId)
        {
            await ClearPrimaryAsync(db, encounterId, diagnosis.Id, ct);
        }

        diagnosis.Type = request.Type;
        diagnosis.IsPrimary = request.IsPrimary;
        diagnosis.OnsetDate = request.OnsetDate;
        diagnosis.Notes = request.Notes?.Trim();
        diagnosis.ChangeStatus(request.Status, time.Today());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(diagnosis));
    }

    /// <summary>One primary diagnosis per encounter: setting a new primary demotes the previous one.</summary>
    private static async Task ClearPrimaryAsync(HospitalDbContext db, Guid encounterId, Guid? except, CancellationToken ct)
    {
        foreach (var other in await db.Set<Diagnosis>().Where(d => d.EncounterId == encounterId && d.IsPrimary && d.Id != except).ToListAsync(ct))
        {
            other.IsPrimary = false;
        }
    }

    internal static DiagnosisDto ToDto(Diagnosis d) => new(
        d.Id, d.PatientId, d.EncounterId, d.IcdCode, d.Title, d.Type, d.IsPrimary, d.OnsetDate, d.Status, d.ResolvedOn, d.RecordedAt, d.Notes, d.Version);
}
