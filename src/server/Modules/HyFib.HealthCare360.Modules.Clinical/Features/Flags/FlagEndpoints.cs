using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Safety;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Flags;

/// <summary>
/// /api/v1/emr — allergies and patient alerts, the flags of the persistent patient banner (EHR-03). Every change is
/// pushed on the patient's real-time topic so open banners refresh immediately.
/// </summary>
internal static class FlagEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/emr").WithTags("EMR");

        group.MapGet("/patients/{patientId:guid}/allergies", ListAllergies).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapPost("/patients/{patientId:guid}/allergies", RecordAllergy).RequirePermission(ClinicalPermissions.AllergiesManage).WithValidation<RecordAllergyRequest>();
        group.MapPut("/allergies/{id:guid}", UpdateAllergy).RequirePermission(ClinicalPermissions.AllergiesManage).WithValidation<UpdateAllergyRequest>();

        group.MapGet("/patients/{patientId:guid}/alerts", ListAlerts).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapPost("/patients/{patientId:guid}/alerts", RaiseAlert).RequirePermission(ClinicalPermissions.AlertsManage).WithValidation<RaiseAlertRequest>();
        group.MapPost("/alerts/{id:guid}/deactivate", DeactivateAlert).RequirePermission(ClinicalPermissions.AlertsManage).WithValidation<DeactivateAlertRequest>();
    }

    private static async Task<IResult> ListAllergies(Guid patientId, bool? includeInactive, ClinicalAccess access, HospitalDbContext db, CancellationToken ct)
    {
        var opened = await access.OpenAsync(patientId, "Viewed allergies", ct);
        if (opened.IsFailure)
        {
            return opened.Error!.ToProblem();
        }

        var allergies = db.Set<Allergy>().AsNoTracking().Where(a => a.PatientId == patientId);
        if (includeInactive != true)
        {
            allergies = allergies.Where(a => a.Status == AllergyStatus.Active);
        }

        // Severity is stored as text, so sort by clinical weight in memory (lists are short).
        return TypedResults.Ok((await allergies.ToListAsync(ct)).OrderByDescending(a => a.Severity).ThenBy(a => a.Allergen).Select(ToDto).ToList());
    }

    private static async Task<IResult> RecordAllergy(Guid patientId, RecordAllergyRequest request, ClinicalAccess access, HospitalDbContext db,
        ICurrentUser user, IRealtimePublisher realtime, TimeProvider time, CancellationToken ct)
    {
        var allowed = await access.EnsureCanRecordAsync(patientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        // Duplicates are rejected so a later status change cannot leave an identical allergy silently active.
        var allergen = DrugNames.Normalize(request.Allergen);
        var active = await db.Set<Allergy>().Where(a => a.PatientId == patientId && a.Status == AllergyStatus.Active).Select(a => new { a.Id, a.Allergen }).ToListAsync(ct);
        if (active.FirstOrDefault(a => DrugNames.Normalize(a.Allergen) == allergen) is { } existing)
        {
            return Error.Conflict("allergy.duplicate", "This allergy is already recorded. Update the existing entry instead.", new { allergyId = existing.Id }).ToProblem();
        }

        var allergy = new Allergy
        {
            PatientId = patientId,
            Allergen = request.Allergen.Trim(),
            Type = request.Type,
            Reaction = request.Reaction?.Trim(),
            Severity = request.Severity,
            OnsetDate = request.OnsetDate,
            RecordedAt = time.GetUtcNow(),
            RecordedByUserId = user.UserId,
            Notes = request.Notes?.Trim(),
        };
        db.Add(allergy);
        await db.SaveChangesAsync(ct);
        await realtime.PublishAsync(Topics.Patient(patientId), "clinical.flags.changed", new { allergy.Id }, ct);
        return TypedResults.Created($"/api/v1/emr/patients/{patientId}/allergies", ToDto(allergy));
    }

    private static async Task<IResult> UpdateAllergy(Guid id, UpdateAllergyRequest request, ClinicalAccess access, HospitalDbContext db,
        IRealtimePublisher realtime, CancellationToken ct)
    {
        var allergy = await db.Set<Allergy>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (allergy is null)
        {
            return Error.NotFound(nameof(Allergy), id).ToProblem();
        }

        var allowed = await access.EnsureCanOpenAsync(allergy.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        db.SetExpectedVersion(allergy, request.Version);
        var changed = allergy.ChangeStatus(request.Status, request.StatusReason);
        if (changed.IsFailure)
        {
            return changed.Error!.ToProblem();
        }

        allergy.Reaction = request.Reaction?.Trim();
        allergy.Severity = request.Severity;
        allergy.Notes = request.Notes?.Trim();
        await db.SaveChangesAsync(ct);
        await realtime.PublishAsync(Topics.Patient(allergy.PatientId), "clinical.flags.changed", new { allergy.Id }, ct);
        return TypedResults.Ok(ToDto(allergy));
    }

    private static async Task<IResult> ListAlerts(Guid patientId, bool? includeInactive, ClinicalAccess access, HospitalDbContext db, TimeProvider time,
        CancellationToken ct)
    {
        var opened = await access.OpenAsync(patientId, "Viewed patient alerts", ct);
        if (opened.IsFailure)
        {
            return opened.Error!.ToProblem();
        }

        var alerts = db.Set<PatientAlert>().AsNoTracking().Where(a => a.PatientId == patientId);
        if (includeInactive != true)
        {
            alerts = alerts.Where(a => a.IsActive);
        }

        var today = time.Today();
        return TypedResults.Ok((await alerts.ToListAsync(ct)).OrderByDescending(a => a.Severity).ThenByDescending(a => a.EffectiveFrom)
            .Select(a => ToDto(a, today)).ToList());
    }

    private static async Task<IResult> RaiseAlert(Guid patientId, RaiseAlertRequest request, ClinicalAccess access, HospitalDbContext db, ICurrentUser user,
        IRealtimePublisher realtime, TimeProvider time, CancellationToken ct)
    {
        var allowed = await access.EnsureCanRecordAsync(patientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        var alert = new PatientAlert
        {
            PatientId = patientId,
            Type = request.Type,
            Title = string.IsNullOrWhiteSpace(request.Title) ? DefaultTitle(request.Type) : request.Title.Trim(),
            Detail = request.Detail?.Trim(),
            Severity = request.Severity ?? PatientAlert.DefaultSeverity(request.Type),
            EffectiveFrom = time.GetUtcNow(),
            ExpiresOn = request.ExpiresOn,
            RaisedByUserId = user.UserId,
        };
        db.Add(alert);
        await db.SaveChangesAsync(ct);
        await realtime.PublishAsync(Topics.Patient(patientId), "clinical.flags.changed", new { alert.Id }, ct);
        return TypedResults.Created($"/api/v1/emr/patients/{patientId}/alerts", ToDto(alert, time.Today()));
    }

    private static async Task<IResult> DeactivateAlert(Guid id, DeactivateAlertRequest request, ClinicalAccess access, HospitalDbContext db, ICurrentUser user,
        IRealtimePublisher realtime, TimeProvider time, CancellationToken ct)
    {
        var alert = await db.Set<PatientAlert>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (alert is null)
        {
            return Error.NotFound("Alert", id).ToProblem();
        }

        var allowed = await access.EnsureCanOpenAsync(alert.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        var result = alert.Deactivate(request.Reason, user.UserId, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        await db.SaveChangesAsync(ct);
        await realtime.PublishAsync(Topics.Patient(alert.PatientId), "clinical.flags.changed", new { alert.Id }, ct);
        return TypedResults.Ok(ToDto(alert, time.Today()));
    }

    private static string DefaultTitle(PatientAlertType type) => type switch
    {
        PatientAlertType.Dnr => "Do not resuscitate (DNR)",
        PatientAlertType.FallRisk => "Fall risk",
        PatientAlertType.Isolation => "Isolation precautions",
        PatientAlertType.InfectionControl => "Infection control",
        PatientAlertType.Pregnancy => "Pregnant",
        _ => "Alert",
    };

    internal static AllergyDto ToDto(Allergy a) => new(
        a.Id, a.PatientId, a.Allergen, a.Type, a.Reaction, a.Severity, a.Status, a.StatusReason, a.OnsetDate, a.RecordedAt, a.RecordedByUserId, a.Notes, a.Version);

    internal static PatientAlertDto ToDto(PatientAlert a, DateOnly today) => new(
        a.Id, a.PatientId, a.Type, a.Title, a.Detail, a.Severity, a.EffectiveFrom, a.ExpiresOn, a.IsActive, a.IsCurrent(today), a.DeactivatedAt,
        a.DeactivationReason);
}
