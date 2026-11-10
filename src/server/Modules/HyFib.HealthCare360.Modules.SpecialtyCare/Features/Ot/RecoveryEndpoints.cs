using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;

/// <summary>
/// /api/v1/ot/bookings/{id}/recovery — the recovery room (PACU) record: received after the WHO Sign-out, the anaesthetist's
/// handover, observations at intervals with the modified Aldrete score, and the discharge to the ward (on the score, or the
/// anaesthetist's override) or to ICU/HDU.
/// </summary>
internal static class RecoveryEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/ot/bookings/{id:guid}/recovery").WithTags("Operation Theatre");

        group.MapGet("", Get).RequirePermission(SpecialtyCarePermissions.OtRead);
        group.MapPost("/receive", async (Guid id, ReceiveInRecoveryRequest r, RecoveryService s, CancellationToken ct) =>
                (await s.ReceiveAsync(id, r, ct)).ToOk())
            .RequirePermission(SpecialtyCarePermissions.OtChecklists).WithValidation<ReceiveInRecoveryRequest>();
        group.MapPut("", async (Guid id, SaveRecoveryRecordRequest r, RecoveryService s, CancellationToken ct) =>
                (await s.SaveAsync(id, r, ct)).ToOk())
            .RequirePermission(SpecialtyCarePermissions.OtChecklists).WithValidation<SaveRecoveryRecordRequest>();
        group.MapPost("/handover", async (Guid id, RecoveryHandoverRequest r, RecoveryService s, CancellationToken ct) =>
                (await s.HandoverAsync(id, r, ct)).ToOk())
            .RequirePermission(SpecialtyCarePermissions.OtChecklists).WithValidation<RecoveryHandoverRequest>();
        group.MapPost("/observations", async (Guid id, AddRecoveryObservationRequest r, RecoveryService s, CancellationToken ct) =>
                (await s.AddObservationAsync(id, r, ct)).ToOk())
            .RequirePermission(SpecialtyCarePermissions.OtChecklists).WithValidation<AddRecoveryObservationRequest>();
        group.MapPost("/discharge", async (Guid id, DischargeFromRecoveryRequest r, RecoveryService s, CancellationToken ct) =>
                (await s.DischargeAsync(id, r, ct)).ToOk())
            .RequirePermission(SpecialtyCarePermissions.OtChecklists).WithValidation<DischargeFromRecoveryRequest>();

        // The anaesthetist's override, from their own sign-in: the patient goes to the ward below the score.
        group.MapPost("/discharge/override", async (Guid id, OverrideRecoveryDischargeRequest r, RecoveryService s, CancellationToken ct) =>
                (await s.OverrideAsync(id, r, ct)).ToOk())
            .RequirePermission(SpecialtyCarePermissions.OtAnaesthesia).WithValidation<OverrideRecoveryDischargeRequest>();
    }

    private static async Task<IResult> Get(
        Guid id, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var record = await db.Set<RecoveryRecord>().AsNoTracking().FirstOrDefaultAsync(r => r.SurgeryBookingId == id, ct);
        if (record is null)
        {
            return Error.NotFound(nameof(RecoveryRecord), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenChartAsync(record.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(record.PatientId, "Viewed recovery record", ct);
        var booking = await db.Set<SurgeryBooking>().AsNoTracking().FirstAsync(b => b.Id == id, ct);
        return TypedResults.Ok(RecoveryService.ToDto(record, booking, time.GetUtcNow()));
    }
}
