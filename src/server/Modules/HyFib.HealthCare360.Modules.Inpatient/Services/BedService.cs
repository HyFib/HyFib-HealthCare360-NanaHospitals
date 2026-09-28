using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>
/// Bed operations outside admission and discharge (WB-02/05): reservations, blocking, maintenance and the housekeeping
/// queue. Every change is validated by the bed state machine and pushed to the bed board in real time.
/// </summary>
public sealed class BedService(HospitalDbContext db, IRealtimePublisher realtime, ICurrentUser currentUser, TimeProvider time)
{
    public async Task<Result<BedDto>> ReserveAsync(Guid bedId, ReserveBedRequest request, CancellationToken ct)
    {
        if (!await db.Set<Patient>().AnyAsync(p => p.Id == request.PatientId && p.IsActive && p.MergedIntoPatientId == null, ct))
        {
            return Error.Field("patientId", "Choose an active patient.");
        }

        return await ChangeAsync(bedId, (bed, now) => Task.FromResult(bed.Reserve(request.PatientId, request.ReservedUntil, request.Note, now)), ct);
    }

    public Task<Result<BedDto>> CancelReservationAsync(Guid bedId, CancellationToken ct) =>
        ChangeAsync(bedId, (bed, now) => Task.FromResult(bed.CancelReservation(now)), ct);

    public Task<Result<BedDto>> BlockAsync(Guid bedId, string reason, CancellationToken ct) =>
        ChangeAsync(bedId, async (bed, now) =>
        {
            var result = bed.Block(reason, now);
            if (result.IsSuccess)
            {
                await CancelOpenCleaningAsync(bed.Id, "Bed blocked", ct);
            }

            return result;
        }, ct);

    /// <summary>Returns a clean blocked bed straight to Vacant; send it for cleaning instead when the block may have soiled it.</summary>
    public Task<Result<BedDto>> UnblockAsync(Guid bedId, CancellationToken ct) =>
        ChangeAsync(bedId, (bed, now) => Task.FromResult(bed.Unblock(now)), ct);

    public Task<Result<BedDto>> StartMaintenanceAsync(Guid bedId, string reason, CancellationToken ct) =>
        ChangeAsync(bedId, async (bed, now) =>
        {
            var result = bed.StartMaintenance(reason, now);
            if (result.IsSuccess)
            {
                await CancelOpenCleaningAsync(bed.Id, "Bed sent for maintenance", ct);
            }

            return result;
        }, ct);

    public Task<Result<BedDto>> EndMaintenanceAsync(Guid bedId, CancellationToken ct) =>
        ChangeAsync(bedId, (bed, now) =>
        {
            var result = bed.EndMaintenance(now);
            if (result.IsSuccess)
            {
                BedOperations.QueueCleaning(db, bed, HousekeepingTrigger.Maintenance, null, null, now);
            }

            return Task.FromResult(result);
        }, ct);

    public Task<Result<BedDto>> MarkForCleaningAsync(Guid bedId, string? notes, CancellationToken ct) =>
        ChangeAsync(bedId, (bed, now) =>
        {
            var result = bed.MarkForCleaning(notes ?? "Cleaning requested", now);
            if (result.IsSuccess)
            {
                BedOperations.QueueCleaning(db, bed, HousekeepingTrigger.Manual, null, notes, now);
            }

            return Task.FromResult(result);
        }, ct);

    public async Task<Result<HousekeepingTaskDto>> StartCleaningAsync(Guid taskId, CancellationToken ct)
    {
        var task = await db.Set<HousekeepingTask>().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
        {
            return Error.NotFound("Housekeeping task", taskId);
        }

        var result = task.Start(currentUser.UserId, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await BedBoardService.TaskDtoAsync(db, task, time.GetUtcNow(), ct);
    }

    /// <summary>Terminal cleaning done: the task closes with its turnaround time and the bed becomes Vacant.</summary>
    public async Task<Result<HousekeepingTaskDto>> CompleteCleaningAsync(Guid taskId, string? notes, CancellationToken ct)
    {
        var task = await db.Set<HousekeepingTask>().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
        {
            return Error.NotFound("Housekeeping task", taskId);
        }

        var bed = await db.Set<Bed>().FirstOrDefaultAsync(b => b.Id == task.BedId, ct);
        if (bed is null)
        {
            return Error.NotFound(nameof(Bed), task.BedId);
        }

        var now = time.GetUtcNow();
        var completed = task.Complete(currentUser.UserId, notes, now);
        if (completed.IsFailure)
        {
            return completed.Error!;
        }

        var released = bed.CompleteCleaning(now);
        if (released.IsFailure)
        {
            return released.Error!;
        }

        await db.SaveChangesAsync(ct);
        await InpatientRealtime.BedChangedAsync(realtime, bed, ct);
        return await BedBoardService.TaskDtoAsync(db, task, now, ct);
    }

    private async Task CancelOpenCleaningAsync(Guid bedId, string note, CancellationToken ct)
    {
        var open = await db.Set<HousekeepingTask>()
            .Where(t => t.BedId == bedId && (t.Status == HousekeepingStatus.Pending || t.Status == HousekeepingStatus.InProgress))
            .ToListAsync(ct);
        foreach (var task in open)
        {
            task.Cancel(note);
        }
    }

    private async Task<Result<BedDto>> ChangeAsync(Guid bedId, Func<Bed, DateTimeOffset, Task<Result>> change, CancellationToken ct)
    {
        var bed = await db.Set<Bed>().FirstOrDefaultAsync(b => b.Id == bedId, ct);
        if (bed is null)
        {
            return Error.NotFound(nameof(Bed), bedId);
        }

        var result = await change(bed, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(ct);
        await InpatientRealtime.BedChangedAsync(realtime, bed, ct);
        return await BedOperations.ToDtoAsync(db, bed, ct);
    }
}

/// <summary>Bed steps shared by admission, transfer, discharge and bed management.</summary>
internal static class BedOperations
{
    /// <summary>Moves an occupied bed to Cleaning and queues its housekeeping task.</summary>
    public static Result Release(HospitalDbContext db, Bed bed, HousekeepingTrigger trigger, Guid? admissionId, string reason, DateTimeOffset now)
    {
        var released = bed.Release(reason, now);
        if (released.IsSuccess)
        {
            QueueCleaning(db, bed, trigger, admissionId, null, now);
        }

        return released;
    }

    public static void QueueCleaning(HospitalDbContext db, Bed bed, HousekeepingTrigger trigger, Guid? admissionId, string? notes, DateTimeOffset now) =>
        db.Add(new HousekeepingTask
        {
            FacilityId = bed.FacilityId,
            BedId = bed.Id,
            WardId = bed.WardId,
            AdmissionId = admissionId,
            Trigger = trigger,
            Status = HousekeepingStatus.Pending,
            RequestedAt = now,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
        });

    public static Error Unavailable(Bed bed) => bed.IsActive
        ? Error.Conflict("bed.unavailable", $"Bed {bed.Label} is {BedStateMachine.Describe(bed.Status)}. Choose another bed.")
        : Error.Conflict("bed.unavailable", $"Bed {bed.Label} is not in service.");

    public static async Task<BedDto> ToDtoAsync(HospitalDbContext db, Bed bed, CancellationToken ct)
    {
        var wardName = await db.Set<Ward>().Where(w => w.Id == bed.WardId).Select(w => w.Name).FirstOrDefaultAsync(ct);
        var roomNumber = bed.RoomId is { } roomId
            ? await db.Set<Room>().Where(r => r.Id == roomId).Select(r => r.Number).FirstOrDefaultAsync(ct)
            : null;
        return ToDto(bed, wardName, roomNumber);
    }

    public static BedDto ToDto(Bed b, string? wardName, string? roomNumber) => new(
        b.Id, b.WardId, wardName, b.RoomId, roomNumber, b.Label, b.Status, b.StatusReason, b.StatusChangedAt, b.CurrentAdmissionId,
        b.ReservedForPatientId, b.ReservedUntil, b.HasOxygen, b.HasVentilator, b.HasMonitor, b.DisplayOrder, b.IsActive,
        BedStateMachine.NextStatuses(b.Status), b.Version);
}
