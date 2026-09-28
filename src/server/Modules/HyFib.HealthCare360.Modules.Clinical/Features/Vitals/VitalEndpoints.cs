using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Vitals;

/// <summary>/api/v1/emr — vital signs with age-aware abnormal-range flags (EMR-03, EHR-01).</summary>
internal static class VitalEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/emr").WithTags("EMR");

        group.MapGet("/patients/{patientId:guid}/vitals", List).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapPost("/patients/{patientId:guid}/vitals", Record).RequirePermission(ClinicalPermissions.VitalsRecord).WithValidation<RecordVitalsRequest>();
        group.MapPost("/patients/{patientId:guid}/vitals/preview", Preview).RequirePermission(ClinicalPermissions.VitalsRecord).WithValidation<RecordVitalsRequest>();
        group.MapPost("/vitals/{id:guid}/entered-in-error", EnteredInError).RequirePermission(ClinicalPermissions.VitalsRecord).WithValidation<EnteredInErrorRequest>();
    }

    private static async Task<IResult> List(Guid patientId, [AsParameters] PageQuery query, Guid? encounterId, DateOnly? from, DateOnly? to,
        bool? includeEnteredInError, ClinicalAccess access, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var opened = await access.OpenAsync(patientId, "Viewed vital signs", ct);
        if (opened.IsFailure)
        {
            return opened.Error!.ToProblem();
        }

        var vitals = db.Set<VitalSigns>().AsNoTracking().Where(v => v.PatientId == patientId);
        if (includeEnteredInError != true)
        {
            vitals = vitals.Where(v => !v.IsEnteredInError);
        }

        if (encounterId is not null)
        {
            vitals = vitals.Where(v => v.EncounterId == encounterId);
        }

        if (from is { } f)
        {
            var start = ClinicalClock.StartOfDayUtc(f);
            vitals = vitals.Where(v => v.RecordedAt >= start);
        }

        if (to is { } t)
        {
            var end = ClinicalClock.StartOfDayUtc(t.AddDays(1));
            vitals = vitals.Where(v => v.RecordedAt < end);
        }

        var dob = await DateOfBirthAsync(db, patientId, ct);
        var today = time.Today();
        var page = await vitals.OrderByDescending(v => v.RecordedAt).ToPagedResultAsync(query.ToRequest(), ct);
        return TypedResults.Ok(page.Map(v => ToDto(v, dob, today)));
    }

    private static async Task<IResult> Record(Guid patientId, RecordVitalsRequest request, ClinicalAccess access, HospitalDbContext db, ICurrentUser user,
        INotificationService notifications, IRealtimePublisher realtime, TimeProvider time, CancellationToken ct)
    {
        var allowed = await access.EnsureCanRecordAsync(patientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        Encounter? encounter = null;
        if (request.EncounterId is { } encounterId)
        {
            encounter = await db.Set<Encounter>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(e => e.Id == encounterId, ct);
            if (encounter is null || encounter.PatientId != patientId)
            {
                return Error.Field("encounterId", "This encounter does not belong to the patient.").ToProblem();
            }
        }

        var vitals = Build(patientId, request, time);
        vitals.FacilityId = user.FacilityId;
        vitals.RecordedByUserId = user.UserId;
        db.Add(vitals);
        await db.SaveChangesAsync(ct);

        var dto = ToDto(vitals, await DateOfBirthAsync(db, patientId, ct), time.Today());
        await realtime.PublishAsync(Topics.Patient(patientId), "clinical.vitals.recorded", new { vitals.Id }, ct);
        if (dto.Flags.Any(f => f.Severity == FlagSeverity.Critical) && encounter is not null)
        {
            // Critical vitals taken by nursing staff must reach the treating doctor immediately (EHR-03).
            var users = await db.Set<User>().Where(u => u.PractitionerId == encounter.PractitionerId && u.IsActive).Select(u => u.Id).ToListAsync(ct);
            var uhid = await db.Set<Patient>().Where(p => p.Id == patientId).Select(p => p.Uhid).FirstOrDefaultAsync(ct);
            await notifications.NotifyUsersAsync(users, "Critical vital signs",
                $"{uhid}: {string.Join("; ", dto.Flags.Where(f => f.Severity == FlagSeverity.Critical).Select(f => f.Message))}",
                $"/emr/patients/{patientId}", "clinical", "critical", ct);
        }

        return TypedResults.Created($"/api/v1/emr/patients/{patientId}/vitals", dto);
    }

    /// <summary>Flags for values being typed, before saving (live banner colouring on the vitals form).</summary>
    private static async Task<IResult> Preview(Guid patientId, RecordVitalsRequest request, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var vitals = Build(patientId, request, time);
        return TypedResults.Ok(VitalSignsAssessor.Assess(vitals.ToReading(), await DateOfBirthAsync(db, patientId, ct), time.Today()));
    }

    private static async Task<IResult> EnteredInError(Guid id, EnteredInErrorRequest request, ClinicalAccess access, HospitalDbContext db, TimeProvider time,
        CancellationToken ct)
    {
        var vitals = await db.Set<VitalSigns>().FirstOrDefaultAsync(v => v.Id == id, ct);
        if (vitals is null)
        {
            return Error.NotFound("Vital signs", id).ToProblem();
        }

        var allowed = await access.EnsureCanOpenAsync(vitals.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        var result = vitals.MarkEnteredInError(request.Reason);
        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(vitals, await DateOfBirthAsync(db, vitals.PatientId, ct), time.Today()));
    }

    private static VitalSigns Build(Guid patientId, RecordVitalsRequest r, TimeProvider time)
    {
        var vitals = new VitalSigns
        {
            PatientId = patientId,
            EncounterId = r.EncounterId,
            RecordedAt = r.RecordedAt ?? time.GetUtcNow(),
            Temperature = r.Temperature,
            TemperatureUnit = r.TemperatureUnit,
            Pulse = r.Pulse,
            RespiratoryRate = r.RespiratoryRate,
            SystolicBp = r.SystolicBp,
            DiastolicBp = r.DiastolicBp,
            SpO2 = r.SpO2,
            OnSupplementalOxygen = r.OnSupplementalOxygen,
            HeightCm = r.HeightCm,
            WeightKg = r.WeightKg,
            PainScore = r.PainScore,
            BloodGlucose = r.BloodGlucose,
            GlucoseTiming = r.GlucoseTiming,
            Gcs = r.Gcs,
            Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim(),
        };
        vitals.ComputeBmi();
        return vitals;
    }

    private static Task<DateOnly?> DateOfBirthAsync(HospitalDbContext db, Guid patientId, CancellationToken ct) =>
        db.Set<Patient>().Where(p => p.Id == patientId).Select(p => p.DateOfBirth).FirstOrDefaultAsync(ct);

    internal static VitalsDto ToDto(VitalSigns v, DateOnly? dateOfBirth, DateOnly today) => new(
        v.Id, v.PatientId, v.EncounterId, v.RecordedAt, v.RecordedByUserId, v.Temperature, v.TemperatureUnit, v.TemperatureCelsius, v.Pulse, v.RespiratoryRate,
        v.SystolicBp, v.DiastolicBp, v.SpO2, v.OnSupplementalOxygen, v.HeightCm, v.WeightKg, v.Bmi, v.PainScore, v.BloodGlucose, v.GlucoseTiming, v.Gcs,
        v.Notes, v.IsEnteredInError, v.EnteredInErrorReason,
        v.IsEnteredInError ? [] : VitalSignsAssessor.Assess(v.ToReading(), dateOfBirth, today));
}
