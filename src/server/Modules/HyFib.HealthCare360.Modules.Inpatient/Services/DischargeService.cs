using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>
/// Discharge workflow: initiate (Billing prepares the final bill), check billing clearance, complete (the visit closes,
/// the bed goes for cleaning, the family is informed) or revoke. Every bed day up to discharge is charged before the
/// clearance check, so the patient never leaves with charges missing from the final bill.
/// </summary>
public sealed class DischargeService(
    HospitalDbContext db,
    IBillingStatusProvider billing,
    InpatientChargeService charges,
    INotificationService notifications,
    IRealtimePublisher realtime,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public async Task<Result<DischargeResult>> InitiateAsync(Guid admissionId, InitiateDischargeRequest request, CancellationToken ct)
    {
        var admission = await db.Set<Admission>().FirstOrDefaultAsync(a => a.Id == admissionId, ct);
        if (admission is null)
        {
            return Error.NotFound(nameof(Admission), admissionId);
        }

        var now = time.GetUtcNow();
        var initiated = admission.InitiateDischarge(currentUser.UserId, request.ExpectedDischargeDate, now);
        if (initiated.IsFailure)
        {
            return initiated.Error!;
        }

        if (!string.IsNullOrWhiteSpace(request.Notes))
        {
            admission.DischargeNotes = request.Notes.Trim();
        }

        // Everything billable up to now goes on the bill before Billing prepares the final bill: the due days are staged
        // in this unit of work, so Billing's DischargeInitiated handler, which runs inside this save, drafts the final
        // bill with them.
        var posted = await charges.StageDueBedDaysAsync(admission.Id, now, ct);
        await db.SaveChangesAsync(ct);

        await realtime.ToFacilityAsync(admission.FacilityId, InpatientRealtime.DischargeInitiated, new { admissionId = admission.Id }, ct);
        await InpatientRealtime.AdmissionChangedAsync(realtime, admission, ct);
        return ToResult(admission, posted);
    }

    public async Task<Result<DischargeResult>> RevokeAsync(Guid admissionId, RevokeDischargeRequest request, CancellationToken ct)
    {
        var admission = await db.Set<Admission>().FirstOrDefaultAsync(a => a.Id == admissionId, ct);
        if (admission is null)
        {
            return Error.NotFound(nameof(Admission), admissionId);
        }

        var revoked = admission.RevokeDischarge(time.GetUtcNow());
        if (revoked.IsFailure)
        {
            return revoked.Error!;
        }

        admission.DischargeNotes = $"Discharge revoked: {request.Reason.Trim()}";
        await db.SaveChangesAsync(ct);
        await InpatientRealtime.AdmissionChangedAsync(realtime, admission, ct);
        return ToResult(admission, 0);
    }

    public async Task<Result<DischargeClearanceDto>> ClearanceAsync(Guid admissionId, CancellationToken ct)
    {
        if (!await db.Set<Admission>().AnyAsync(a => a.Id == admissionId, ct))
        {
            return Error.NotFound(nameof(Admission), admissionId);
        }

        var clearance = await billing.GetDischargeClearanceAsync(admissionId, ct);
        return new DischargeClearanceDto(admissionId, clearance.Cleared, clearance.Outstanding, clearance.Reason);
    }

    public async Task<Result<DischargeResult>> CompleteAsync(Guid admissionId, CompleteDischargeRequest request, CancellationToken ct)
    {
        var admission = await db.Set<Admission>().FirstOrDefaultAsync(a => a.Id == admissionId, ct);
        if (admission is null)
        {
            return Error.NotFound(nameof(Admission), admissionId);
        }

        if (!admission.IsActive)
        {
            return Error.Rule("admission.closed", "This admission is already closed.");
        }

        var type = request.DischargeType;
        if (admission.Status == AdmissionStatus.Admitted && DischargeRules.RequiresInitiation(type))
        {
            return Error.Rule("discharge.not_initiated", "Initiate the discharge first so billing can prepare the final bill.");
        }

        var now = time.GetUtcNow();
        var dischargedAt = request.DischargedAt ?? now;
        var posted = 0;
        if (DischargeRules.RequiresBillingClearance(type))
        {
            posted = await charges.PostDueBedDaysAsync(admission.Id, dischargedAt, ct);
            var clearance = await billing.GetDischargeClearanceAsync(admission.Id, ct);
            if (!clearance.Cleared)
            {
                return new Error("discharge.billing_pending",
                    clearance.Reason ?? $"Billing clearance is pending: Rs. {Money.FormatInr(clearance.Outstanding)} outstanding.",
                    ErrorType.BusinessRule)
                {
                    Data = new { clearance.Outstanding },
                };
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var completed = admission.CompleteDischarge(type, currentUser.UserId, request.Notes, dischargedAt);
        if (completed.IsFailure)
        {
            return completed.Error!;
        }

        var visit = await db.Set<Visit>().FirstOrDefaultAsync(v => v.Id == admission.VisitId, ct);
        visit?.Complete(dischargedAt);

        var openStays = await db.Set<BedAssignment>().Where(a => a.AdmissionId == admission.Id && a.ToAt == null).ToListAsync(ct);
        openStays.ForEach(a => a.Close(dischargedAt));

        var bed = admission.BedId is { } bedId ? await db.Set<Bed>().FirstOrDefaultAsync(b => b.Id == bedId, ct) : null;
        if (bed is not null && bed.CurrentAdmissionId == admission.Id)
        {
            var released = BedOperations.Release(db, bed, HousekeepingTrigger.Discharge, admission.Id, "Cleaning after discharge", now);
            if (released.IsFailure)
            {
                return released.Error!;
            }
        }

        if (request.NotifyPatient && DischargeRules.NotifiesPatient(type))
        {
            var patient = await db.Set<Patient>().AsNoTracking()
                .Where(p => p.Id == admission.PatientId)
                .Select(p => new { p.Id, p.FullName, p.Phone, p.Email })
                .FirstOrDefaultAsync(ct);
            if (patient?.Phone is not null)
            {
                notifications.Queue("discharge.completed",
                    new NotificationRecipient(patient.Phone, patient.Email, patient.Id, patient.FullName),
                    new Dictionary<string, string> { ["name"] = patient.FullName },
                    [NotificationChannel.Sms],
                    TimeSpan.FromHours(12));
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        if (!DischargeRules.RequiresBillingClearance(type))
        {
            // Death or abscondence is recorded first; the stay's remaining days are billed afterwards (retried hourly).
            posted = await charges.PostDueBedDaysAsync(admission.Id, null, ct);
        }

        if (bed is not null)
        {
            await InpatientRealtime.BedChangedAsync(realtime, bed, ct);
        }

        await InpatientRealtime.AdmissionChangedAsync(realtime, admission, ct);
        return ToResult(admission, posted);
    }

    private static DischargeResult ToResult(Admission a, int chargesPosted) =>
        new(a.Id, a.Status, a.DischargeInitiatedAt, a.DischargedAt, chargesPosted);
}
