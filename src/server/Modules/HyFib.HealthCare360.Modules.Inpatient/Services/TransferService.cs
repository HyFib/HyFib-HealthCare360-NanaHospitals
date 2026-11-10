using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Transfers;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Realtime;
using Npgsql;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>
/// Transfers between wards as a hand-off (see <see cref="TransferRequest"/>): the sending ward asks and the bed is held;
/// the receiving ward's nurse in charge, or a doctor who may transfer patients, accepts (the move is then made by
/// <see cref="AdmissionService.TransferAsync"/>, its rules unchanged) or declines (the bed is released). A move between
/// beds of the same ward needs nobody's acceptance and stays a direct transfer. An urgent step-up or step-down to or from
/// critical care moves at once and is accepted afterwards within the hour; past the hour it is flagged and the nurses in
/// charge are told once (<see cref="NotifyOverdueAcceptancesAsync"/>).
/// </summary>
public sealed class TransferService(
    HospitalDbContext db,
    AdmissionService admissions,
    IPermissionService permissions,
    INotificationService notifications,
    IAuditLogger audit,
    IRealtimePublisher realtime,
    ICurrentUser currentUser,
    TimeProvider time)
{
    private const string Category = "inpatient";
    private const string Link = "/inpatient/transfers";

    /// <summary>A direct transfer: a bed in the same ward only; a move to another ward is asked for.</summary>
    public async Task<Result> DirectAsync(Guid admissionId, TransferPatientRequest request, CancellationToken ct)
    {
        var fromWard = await db.Set<Admission>().AsNoTracking().Where(a => a.Id == admissionId).Select(a => (Guid?)a.WardId).FirstOrDefaultAsync(ct);
        var toWard = await db.Set<Bed>().AsNoTracking().Where(b => b.Id == request.ToBedId).Select(b => (Guid?)b.WardId).FirstOrDefaultAsync(ct);
        if (fromWard is not null && toWard is not null && fromWard != toWard)
        {
            return Error.Rule("transfer.request_required",
                "A move to another ward is asked of the receiving ward: send a transfer request with the reason and a clinical summary, and the receiving ward accepts it.");
        }

        return await admissions.TransferAsync(admissionId, request, ct);
    }

    public async Task<Result<TransferRequestDto>> RequestAsync(Guid admissionId, RequestTransferRequest request, CancellationToken ct)
    {
        var admission = await db.Set<Admission>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == admissionId, ct);
        if (admission is null)
        {
            return Error.NotFound(nameof(Admission), admissionId);
        }

        if (admission.Status != AdmissionStatus.Admitted)
        {
            return Error.Rule("admission.not_transferable",
                admission.Status == AdmissionStatus.DischargeInitiated
                    ? "Discharge is in progress. Revoke the discharge before transferring the patient."
                    : "This admission is closed.");
        }

        var target = await db.Set<Bed>().FirstOrDefaultAsync(b => b.Id == request.ToBedId, ct);
        if (target is null)
        {
            return Error.Field("toBedId", "Choose the destination bed.");
        }

        var toWard = await db.Set<Ward>().AsNoTracking().FirstOrDefaultAsync(w => w.Id == target.WardId, ct);
        if (toWard is null || !toWard.IsActive)
        {
            return Error.Rule("ward.inactive", "The destination ward is not in service.");
        }

        if (toWard.Id == admission.WardId)
        {
            return Error.Rule("transfer.same_ward", "A move to another bed of the same ward needs no request: transfer the patient directly.");
        }

        var fromWard = admission.WardId is { } fromId ? await db.Set<Ward>().AsNoTracking().FirstOrDefaultAsync(w => w.Id == fromId, ct) : null;
        if (request.IsUrgent && !toWard.IsCriticalCare && fromWard?.IsCriticalCare != true)
        {
            return Error.Rule("transfer.urgent_not_critical_care",
                "Only a step-up or step-down to or from critical care (ICU, NICU, PICU, HDU) moves at once as urgent; ask the receiving ward for any other move.");
        }

        if (await db.Set<TransferRequest>().AnyAsync(t => t.AdmissionId == admissionId && t.Status == TransferRequestStatus.Pending, ct))
        {
            return Error.Conflict("transfer.pending", "A transfer of this patient is already waiting for the receiving ward. Cancel it first.");
        }

        var now = time.GetUtcNow();
        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == admission.PatientId).Select(p => new { p.Gender, p.DateOfBirth }).FirstAsync(ct);
        if (!target.IsAvailableFor(admission.PatientId, now))
        {
            return BedOperations.Unavailable(target);
        }

        var eligibility = WardEligibility.Check(toWard.Type, toWard.GenderPolicy, patient.Gender, patient.DateOfBirth, time.Today());
        if (eligibility.IsFailure)
        {
            return eligibility.Error!;
        }

        var transfer = new TransferRequest
        {
            FacilityId = admission.FacilityId,
            AdmissionId = admission.Id,
            PatientId = admission.PatientId,
            FromWardId = admission.WardId,
            FromBedId = admission.BedId,
            ToWardId = toWard.Id,
            ToBedId = target.Id,
            Reason = request.Reason.Trim(),
            ClinicalSummary = request.ClinicalSummary.Trim(),
            ConsultantPractitionerId = request.ConsultantPractitionerId,
            DepartmentId = request.DepartmentId,
            IsUrgent = request.IsUrgent,
            RequestedAt = now,
            RequestedBy = currentUser.UserId,
            RequestedByName = currentUser.DisplayName,
        };
        db.Add(transfer);

        string title;
        if (request.IsUrgent)
        {
            // Moved now, on the transfer's own rules; the request is saved in the same transaction as the move.
            transfer.Status = TransferRequestStatus.MovedAwaitingAcceptance;
            transfer.TransferredAt = now;
            var moved = await admissions.TransferAsync(admission.Id,
                new TransferPatientRequest(target.Id, transfer.Reason, request.ConsultantPractitionerId, request.DepartmentId, now), ct);
            if (moved.IsFailure)
            {
                return moved.Error!;
            }

            title = $"Urgent transfer to {toWard.Name}: accept within the hour";
        }
        else
        {
            transfer.Status = TransferRequestStatus.Pending;
            transfer.HoldUntil = now + TransferRequest.BedHold;
            var held = target.Reserve(admission.PatientId, transfer.HoldUntil.Value, $"Held for a transfer from {fromWard?.Name ?? "another ward"}", now);
            if (held.IsFailure)
            {
                return held.Error!;
            }

            var saved = await SaveAsync(target, ct);
            if (saved.IsFailure)
            {
                return saved.Error!;
            }

            await InpatientRealtime.BedsChangedAsync(realtime, [target], ct);
            title = $"Transfer requested to {toWard.Name}";
        }

        var uhid = await UhidAsync(admission.PatientId, ct);
        await notifications.NotifyPermissionHoldersAsync(InpatientPermissions.NursingInCharge, admission.FacilityId, title,
            $"{uhid} from {fromWard?.Name ?? "-"} to {toWard.Name} bed {target.Label}: {transfer.Reason}", Link, Category,
            request.IsUrgent ? "warning" : "info", ct);
        await AuditAsync(transfer, request.IsUrgent ? "inpatient.transfer_urgent" : "inpatient.transfer_requested",
            $"{(request.IsUrgent ? "Moved on an urgent transfer" : "Asked for a transfer")} from {fromWard?.Name ?? "-"} to {toWard.Name} bed {target.Label}: {transfer.Reason}",
            request.IsUrgent ? AuditSeverity.Warning : AuditSeverity.Info, ct);
        return (await ListAsync(t => t.Id == transfer.Id, ct)).Single();
    }

    public async Task<Result<TransferRequestDto>> AcceptAsync(Guid id, AcceptTransferRequest request, CancellationToken ct)
    {
        var transfer = await db.Set<TransferRequest>().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (transfer is null)
        {
            return Error.NotFound("Transfer request", id);
        }

        var allowed = await CheckDeciderAsync(transfer, request.Version, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        var now = time.GetUtcNow();
        var wasUrgent = transfer.Status == TransferRequestStatus.MovedAwaitingAcceptance;
        transfer.Accept(currentUser.UserId, currentUser.DisplayName, request.Note, now, wasUrgent ? null : request.TransferredAt ?? now);
        if (wasUrgent)
        {
            await db.SaveChangesAsync(ct);
        }
        else
        {
            // The move, as any transfer: the held bed is the patient's, and the request is saved in the move's transaction.
            var moved = await admissions.TransferAsync(transfer.AdmissionId,
                new TransferPatientRequest(transfer.ToBedId, transfer.Reason, transfer.ConsultantPractitionerId, transfer.DepartmentId, request.TransferredAt), ct);
            if (moved.IsFailure)
            {
                return moved.Error!;
            }
        }

        await NotifyRequesterAsync(transfer, "Transfer accepted",
            $"{await UhidAsync(transfer.PatientId, ct)}: {transfer.DecidedByName} accepted the transfer{(wasUrgent ? string.Empty : "; the patient has moved")}.", ct);
        await AuditAsync(transfer, "inpatient.transfer_accepted",
            $"Accepted the transfer asked by {transfer.RequestedByName}{(transfer.WasAcceptedLate ? ", after the hour an urgent move allows" : string.Empty)}",
            transfer.WasAcceptedLate ? AuditSeverity.Warning : AuditSeverity.Info, ct);
        return (await ListAsync(t => t.Id == id, ct)).Single();
    }

    public Task<Result<TransferRequestDto>> DeclineAsync(Guid id, DecideTransferRequest request, CancellationToken ct) =>
        CloseAsync(id, request, decline: true, ct);

    public Task<Result<TransferRequestDto>> CancelAsync(Guid id, DecideTransferRequest request, CancellationToken ct) =>
        CloseAsync(id, request, decline: false, ct);

    public Task<List<TransferRequestDto>> ForAdmissionAsync(Guid admissionId, CancellationToken ct) => ListAsync(t => t.AdmissionId == admissionId, ct);

    /// <summary>
    /// Open requests (pending, and urgent moves not yet accepted) by default; <paramref name="wardId"/> matches either end,
    /// so a ward sees what it is asked to take and what it asked to send.
    /// </summary>
    public Task<List<TransferRequestDto>> ListAsync(Guid? wardId, bool openOnly, CancellationToken ct) =>
        ListAsync(t => (wardId == null || t.ToWardId == wardId || t.FromWardId == wardId)
                       && (!openOnly || t.Status == TransferRequestStatus.Pending || t.Status == TransferRequestStatus.MovedAwaitingAcceptance), ct);

    /// <summary>
    /// Tells the nurses in charge, once, of each urgent move to or from critical care not accepted within the hour (the job
    /// that tells them of orders not countersigned in time calls this too). Returns how many it told of.
    /// </summary>
    public async Task<int> NotifyOverdueAcceptancesAsync(CancellationToken ct)
    {
        var due = time.GetUtcNow() - TransferRequest.UrgentAcceptanceWindow;
        var overdue = await db.Set<TransferRequest>()
            .IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(t => t.Status == TransferRequestStatus.MovedAwaitingAcceptance && t.OverdueNotifiedAt == null && t.TransferredAt < due)
            .Take(200)
            .ToListAsync(ct);
        foreach (var transfer in overdue)
        {
            var uhid = await db.Set<Patient>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(p => p.Id == transfer.PatientId).Select(p => p.Uhid).FirstOrDefaultAsync(ct);
            await notifications.NotifyPermissionHoldersAsync(InpatientPermissions.NursingInCharge, transfer.FacilityId,
                "Urgent transfer not accepted in time", $"{uhid}: moved at {IndiaClock.LocalTime(transfer.TransferredAt!.Value):HH\\:mm} and not accepted by the receiving ward within the hour.",
                Link, Category, "warning", ct);
            transfer.OverdueNotifiedAt = time.GetUtcNow();
            await audit.WriteAsync(new AuditEntry(AuditCategories.Clinical, "inpatient.transfer_acceptance_overdue",
                "An urgent transfer was not accepted by the receiving ward within the hour")
            {
                PatientId = transfer.PatientId,
                EntityType = nameof(TransferRequest),
                EntityId = transfer.Id.ToString(),
                Severity = AuditSeverity.Warning,
            }, ct);
        }

        await db.SaveChangesAsync(ct);
        return overdue.Count;
    }

    private async Task<Result<TransferRequestDto>> CloseAsync(Guid id, DecideTransferRequest request, bool decline, CancellationToken ct)
    {
        var transfer = await db.Set<TransferRequest>().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (transfer is null)
        {
            return Error.NotFound("Transfer request", id);
        }

        if (request.Version != transfer.Version)
        {
            return Error.Conflict("transfer.stale", "Someone else changed this transfer request. Reload it and try again.");
        }

        if (decline)
        {
            var allowed = await CheckDeciderAsync(transfer, request.Version, ct);
            if (allowed.IsFailure)
            {
                return allowed.Error!;
            }
        }

        var now = time.GetUtcNow();
        var closed = decline
            ? transfer.Decline(currentUser.UserId, currentUser.DisplayName, request.Reason, now)
            : transfer.Cancel(currentUser.UserId, currentUser.DisplayName, request.Reason, now);
        if (closed.IsFailure)
        {
            return closed.Error!;
        }

        // Release the held bed, if it is still held for this patient.
        var bed = await db.Set<Bed>().FirstOrDefaultAsync(b => b.Id == transfer.ToBedId, ct);
        if (bed is { Status: BedStatus.Reserved } && bed.ReservedForPatientId == transfer.PatientId && bed.CancelReservation(now).IsSuccess)
        {
            var saved = await SaveAsync(bed, ct);
            if (saved.IsFailure)
            {
                return saved.Error!;
            }

            await InpatientRealtime.BedsChangedAsync(realtime, [bed], ct);
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }

        if (decline)
        {
            await NotifyRequesterAsync(transfer, "Transfer declined", $"{await UhidAsync(transfer.PatientId, ct)}: {transfer.DecidedByName} declined the transfer: {transfer.DecisionNote}", ct);
        }

        await AuditAsync(transfer, decline ? "inpatient.transfer_declined" : "inpatient.transfer_cancelled",
            $"{(decline ? "Declined" : "Cancelled")} the transfer: {transfer.DecisionNote}", AuditSeverity.Info, ct);
        return (await ListAsync(t => t.Id == id, ct)).Single();
    }

    /// <summary>The receiving ward decides: a nurse in charge, or a doctor who may transfer patients; never whoever asked.</summary>
    private async Task<Result> CheckDeciderAsync(TransferRequest transfer, uint version, CancellationToken ct)
    {
        if (version != transfer.Version)
        {
            return Error.Conflict("transfer.stale", "Someone else changed this transfer request. Reload it and try again.");
        }

        var state = transfer.CheckDecider(currentUser.UserId);
        if (state.IsFailure)
        {
            return state;
        }

        return await CanDecideAsync(ct)
            ? Result.Success()
            : Error.Forbidden("transfer.not_receiving_ward",
                "The receiving ward accepts a transfer: its nurse in charge, or a doctor who may transfer patients.");
    }

    private async Task<bool> CanDecideAsync(CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
        {
            return false;
        }

        if (await permissions.HasPermissionAsync(userId, InpatientPermissions.NursingInCharge, ct))
        {
            return true;
        }

        return currentUser.PractitionerId is not null && await permissions.HasPermissionAsync(userId, InpatientPermissions.Transfer, ct);
    }

    private async Task<List<TransferRequestDto>> ListAsync(Expression<Func<TransferRequest, bool>> filter, CancellationToken ct)
    {
        var rows = await (
                from t in db.Set<TransferRequest>().AsNoTracking().Where(filter)
                join a in db.Set<Admission>() on t.AdmissionId equals a.Id
                join p in db.Set<Patient>() on t.PatientId equals p.Id
                join tw in db.Set<Ward>() on t.ToWardId equals tw.Id
                join tb in db.Set<Bed>() on t.ToBedId equals tb.Id
                join fw in db.Set<Ward>() on t.FromWardId equals fw.Id into fromWards
                from fw in fromWards.DefaultIfEmpty()
                join fb in db.Set<Bed>() on t.FromBedId equals fb.Id into fromBeds
                from fb in fromBeds.DefaultIfEmpty()
                orderby t.RequestedAt descending
                select new
                {
                    Transfer = t,
                    a.AdmissionNumber,
                    p.Uhid,
                    p.FullName,
                    p.IsVip,
                    ToWard = tw.Name,
                    ToBed = tb.Label,
                    FromWard = fw != null ? fw.Name : null,
                    FromBed = fb != null ? fb.Label : null,
                })
            .Take(200)
            .ToListAsync(ct);

        var now = time.GetUtcNow();
        var canDecide = await CanDecideAsync(ct);
        var canSeeRestricted = currentUser.UserId is { } userId && await permissions.HasPermissionAsync(userId, PatientsPermissions.ReadRestricted, ct);
        return rows.Select(r =>
        {
            var t = r.Transfer;
            return new TransferRequestDto(t.Id, t.AdmissionId, r.AdmissionNumber, t.PatientId, r.Uhid,
                r.IsVip && !canSeeRestricted ? WardRoundQueries.RestrictedName : r.FullName,
                t.FromWardId, r.FromWard, r.FromBed, t.ToWardId, r.ToWard, t.ToBedId, r.ToBed, t.Reason, t.ClinicalSummary, t.IsUrgent, t.Status,
                t.RequestedAt, t.RequestedByName, t.HoldUntil, t.DecidedAt, t.DecidedByName, t.DecisionNote, t.TransferredAt, t.AcceptanceDueAt,
                t.IsAcceptanceOverdue(now), t.WasAcceptedLate, canDecide && t.IsOpen && t.RequestedBy != currentUser.UserId, t.Version);
        }).ToList();
    }

    private async Task NotifyRequesterAsync(TransferRequest transfer, string title, string body, CancellationToken ct)
    {
        if (transfer.RequestedBy is { } requester)
        {
            await notifications.NotifyUsersAsync([requester], title, body, Link, Category, "info", ct);
        }
    }

    private Task<string?> UhidAsync(Guid patientId, CancellationToken ct) =>
        db.Set<Patient>().Where(p => p.Id == patientId).Select(p => p.Uhid).FirstOrDefaultAsync(ct);

    private Task AuditAsync(TransferRequest transfer, string action, string summary, AuditSeverity severity, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry(AuditCategories.Clinical, action, summary.Length <= 1000 ? summary : summary[..999] + "…")
        {
            PatientId = transfer.PatientId,
            EntityType = nameof(TransferRequest),
            EntityId = transfer.Id.ToString(),
            Severity = severity,
        }, ct);

    private async Task<Result> SaveAsync(Bed bed, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("bed.taken", $"Bed {bed.Label} was changed from another desk a moment ago. Refresh the bed board and try again.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Error.Conflict("transfer.pending", "A transfer of this patient is already waiting for the receiving ward.");
        }
    }
}
