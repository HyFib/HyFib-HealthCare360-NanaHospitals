using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;

/// <summary>
/// /api/v1/ot/bookings/{id}/… — pre-op and WHO checklists, anaesthesia record, operation note and post-op orders,
/// implants and consumables.
/// </summary>
internal static class OtClinicalEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/ot/bookings/{id:guid}").WithTags("Operation Theatre");

        group.MapPut("/pre-op-checklist", async (Guid id, RecordChecklistRequest r, OtClinicalService s, CancellationToken ct) =>
                (await s.RecordPreOpAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.OtChecklists).WithValidation<RecordChecklistRequest>();
        group.MapPost("/safety-checklist/{phase}", ConfirmSafety)
            .RequirePermission(SpecialtyCarePermissions.OtChecklists).WithValidation<RecordChecklistRequest>();

        group.MapGet("/anaesthesia", GetAnaesthesia).RequirePermission(SpecialtyCarePermissions.OtRead);
        group.MapPut("/anaesthesia", async (Guid id, SaveAnaesthesiaRecordRequest r, OtClinicalService s, CancellationToken ct) =>
                (await s.SaveAnaesthesiaAsync(id, r, ct)).ToOk())
            .RequirePermission(SpecialtyCarePermissions.OtAnaesthesia).WithValidation<SaveAnaesthesiaRecordRequest>();
        group.MapPost("/anaesthesia/vitals", async (Guid id, AddAnaesthesiaVitalsRequest r, OtClinicalService s, CancellationToken ct) =>
                (await s.AddVitalsAsync(id, r, ct)).ToOk())
            .RequirePermission(SpecialtyCarePermissions.OtAnaesthesia).WithValidation<AddAnaesthesiaVitalsRequest>();

        group.MapGet("/operation-note", GetOperationNote).RequirePermission(SpecialtyCarePermissions.OtRead);
        group.MapPut("/operation-note", async (Guid id, SaveOperationNoteRequest r, OtClinicalService s, CancellationToken ct) =>
                (await s.SaveOperationNoteAsync(id, r, ct)).ToOk())
            .RequirePermission(SpecialtyCarePermissions.OtSurgery).WithValidation<SaveOperationNoteRequest>();

        group.MapGet("/materials", ListMaterials).RequirePermission(SpecialtyCarePermissions.OtRead);
        group.MapPost("/materials", async (Guid id, AddSurgeryMaterialRequest r, OtClinicalService s, CancellationToken ct) =>
                (await s.AddMaterialAsync(id, r, ct)).ToCreated(m => $"/api/v1/ot/bookings/{id}/materials/{m.Id}"))
            .RequirePermission(SpecialtyCarePermissions.OtMaterials).WithValidation<AddSurgeryMaterialRequest>();
        group.MapDelete("/materials/{materialId:guid}", async (Guid id, Guid materialId, OtClinicalService s, CancellationToken ct) =>
                (await s.RemoveMaterialAsync(id, materialId, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.OtMaterials);
    }

    /// <summary>Phase in the route: sign-in, time-out or sign-out (enum names are accepted too).</summary>
    private static async Task<IResult> ConfirmSafety(Guid id, string phase, RecordChecklistRequest request, OtClinicalService service, CancellationToken ct)
    {
        SafetyPhase? parsed = phase.Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant() switch
        {
            "SIGNIN" => SafetyPhase.SignIn,
            "TIMEOUT" => SafetyPhase.TimeOut,
            "SIGNOUT" => SafetyPhase.SignOut,
            _ => null,
        };

        return parsed is { } value
            ? (await service.ConfirmSafetyAsync(id, value, request, ct)).ToNoContent()
            : Error.Field("phase", "Use sign-in, time-out or sign-out.").ToProblem();
    }

    private static async Task<IResult> GetAnaesthesia(Guid id, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var record = await db.Set<AnaesthesiaRecord>().AsNoTracking().FirstOrDefaultAsync(r => r.SurgeryBookingId == id, ct);
        if (record is null)
        {
            return Error.NotFound(nameof(AnaesthesiaRecord), id).ToProblem();
        }

        var access = await OpenAsync(record.PatientId, "Viewed anaesthesia record", guard, audit, ct);
        return access ?? TypedResults.Ok(OtClinicalService.ToDto(record));
    }

    private static async Task<IResult> GetOperationNote(Guid id, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var note = await db.Set<OperationNote>().AsNoTracking().FirstOrDefaultAsync(n => n.SurgeryBookingId == id, ct);
        if (note is null)
        {
            return Error.NotFound(nameof(OperationNote), id).ToProblem();
        }

        var access = await OpenAsync(note.PatientId, "Viewed operation note", guard, audit, ct);
        return access ?? TypedResults.Ok(OtClinicalService.ToDto(note));
    }

    private static async Task<IResult> ListMaterials(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var materials = await db.Set<SurgeryMaterial>().AsNoTracking()
            .Where(m => m.SurgeryBookingId == id)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);
        return TypedResults.Ok(materials.Select(OtClinicalService.ToDto).ToList());
    }

    /// <summary>Restricted-record guard plus PHI access audit; returns a problem result when access is refused.</summary>
    private static async Task<IResult?> OpenAsync(Guid patientId, string what, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(patientId, what, ct);
        return null;
    }
}
