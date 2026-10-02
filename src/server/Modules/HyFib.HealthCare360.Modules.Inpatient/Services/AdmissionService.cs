using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Realtime;
using Npgsql;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>
/// Admission, transfer, update and cancellation (ADT, WB-03/04). An admission creates the in-patient visit exactly the
/// way the Patients module's <see cref="VisitService"/> numbers and raises it, allocates the admission number, occupies
/// the bed and records the first bed assignment in one transaction. The bed's concurrency version plus the database's
/// one-active-admission indexes make double allocation impossible even when two desks click at the same moment.
/// </summary>
public sealed class AdmissionService(
    HospitalDbContext db,
    INumberSeries numbers,
    IFieldProtector protector,
    INotificationService notifications,
    IRealtimePublisher realtime,
    InpatientChargeService charges,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public async Task<Result<AdmissionCreated>> AdmitAsync(AdmitPatientRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var patient = await db.Set<Patient>().FirstOrDefaultAsync(p => p.Id == request.PatientId, ct);
        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), request.PatientId);
        }

        if (patient.MergedIntoPatientId is not null)
        {
            return Error.Rule("patient.merged", "This record was merged into another UHID. Use the surviving record.");
        }

        if (patient.IsDeceased || !patient.IsActive)
        {
            return Error.Rule("patient.inactive", "Inactive or deceased patients cannot be admitted.");
        }

        var existing = await db.Set<Admission>()
            .Where(a => a.PatientId == patient.Id && (a.Status == AdmissionStatus.Admitted || a.Status == AdmissionStatus.DischargeInitiated))
            .Select(a => new { a.Id, a.AdmissionNumber })
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return Error.Conflict("admission.already_admitted", $"This patient is already admitted ({existing.AdmissionNumber}).", new { admissionId = existing.Id });
        }

        var bed = await db.Set<Bed>().FirstOrDefaultAsync(b => b.Id == request.BedId, ct);
        if (bed is null)
        {
            return Error.Field("bedId", "Choose a bed.");
        }

        var ward = await db.Set<Ward>().FirstOrDefaultAsync(w => w.Id == bed.WardId, ct);
        if (ward is null || !ward.IsActive)
        {
            return Error.Rule("ward.inactive", "This ward is not in service.");
        }

        var now = time.GetUtcNow();
        if (!bed.IsAvailableFor(patient.Id, now))
        {
            return BedOperations.Unavailable(bed);
        }

        var eligibility = WardEligibility.Check(ward.Type, ward.GenderPolicy, patient.Gender, patient.DateOfBirth, time.Today());
        if (eligibility.IsFailure)
        {
            return eligibility.Error!;
        }

        var consultant = await ActivePractitionerAsync(request.ConsultantPractitionerId, ct);
        if (consultant is null)
        {
            return Error.Field("consultantPractitionerId", "Choose an active consultant.");
        }

        if (request.DepartmentId is { } departmentId && !await db.Set<Department>().AnyAsync(d => d.Id == departmentId && d.IsActive, ct))
        {
            return Error.Field("departmentId", "Choose an active department.");
        }

        if (request.SourceVisitId is { } sourceVisitId && !await db.Set<Visit>().AnyAsync(v => v.Id == sourceVisitId && v.PatientId == patient.Id, ct))
        {
            return Error.Field("sourceVisitId", "The source visit does not belong to this patient.");
        }

        var admittedAt = request.AdmittedAt ?? now;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var visit = new Visit
        {
            FacilityId = facilityId,
            VisitNumber = await numbers.NextAsync(PatientsNumberSeries.Visit, facilityId, ct),
            PatientId = patient.Id,
            Type = VisitType.InPatient,
            Status = VisitStatus.Open,
            DepartmentId = request.DepartmentId ?? ward.DepartmentId,
            PractitionerId = consultant.Value.Id,
            VisitedAt = admittedAt,
            PreviousVisitId = request.SourceVisitId,
            PayerType = request.PayerType,
            PayerId = request.PayerId,
            ReferralSource = request.Source == AdmissionSource.Referral ? ReferralSource.Doctor : ReferralSource.Self,
            ReferredBy = Clean(request.ReferredBy),
            ChiefComplaint = Clean(request.ProvisionalDiagnosis),
            IsMlc = request.IsMlc,
            MlcNumber = Clean(request.MlcNumber),
            Notes = "In-patient admission",
        };
        visit.MarkCreated();
        visit.Start();
        db.Add(visit);

        var admission = new Admission
        {
            FacilityId = facilityId,
            AdmissionNumber = await numbers.NextAsync(InpatientNumberSeries.Admission, facilityId, ct),
            PatientId = patient.Id,
            VisitId = visit.Id,
            SourceVisitId = request.SourceVisitId,
            Type = request.Type,
            Source = request.Source,
            Status = AdmissionStatus.Admitted,
            AdmittedAt = admittedAt,
            AdmittingPractitionerId = request.AdmittingPractitionerId ?? currentUser.PractitionerId ?? consultant.Value.Id,
            ConsultantPractitionerId = consultant.Value.Id,
            DepartmentId = request.DepartmentId ?? ward.DepartmentId,
            WardId = ward.Id,
            BedId = bed.Id,
            ProvisionalDiagnosis = Clean(request.ProvisionalDiagnosis),
            ReferredBy = Clean(request.ReferredBy),
            PayerType = request.PayerType,
            PayerId = request.PayerId,
            IsMlc = request.IsMlc,
            MlcNumber = Clean(request.MlcNumber),
            ExpectedDischargeDate = request.ExpectedDischargeDate,
            AttendantName = Clean(request.AttendantName),
            AttendantRelation = Clean(request.AttendantRelation),
            AttendantPhone = Phone.Normalize(request.AttendantPhone),
            Notes = Clean(request.Notes),
        };
        ApplyPolicyNumber(admission, request.PolicyNumber);

        var occupied = bed.Occupy(admission.Id, patient.Id, now);
        if (occupied.IsFailure)
        {
            return occupied.Error!;
        }

        db.Add(admission);
        db.Add(new BedAssignment
        {
            FacilityId = facilityId,
            AdmissionId = admission.Id,
            BedId = bed.Id,
            WardId = ward.Id,
            BedServiceItemId = ward.BedServiceItemId,
            IsCriticalCare = ward.IsCriticalCare,
            Reason = BedAssignmentReason.Admission,
            FromAt = admittedAt,
        });
        admission.MarkAdmitted(ward.BedServiceItemId);

        if (request.NotifyPatient && patient.Phone is not null)
        {
            notifications.Queue("admission.admitted",
                new NotificationRecipient(patient.Phone, patient.Email, patient.Id, patient.FullName),
                new Dictionary<string, string>
                {
                    ["name"] = patient.FullName,
                    ["ward"] = ward.Name,
                    ["bed"] = bed.Label,
                    ["doctor"] = consultant.Value.DisplayName,
                },
                [NotificationChannel.Sms],
                TimeSpan.FromHours(6));
        }

        var saved = await SaveAsync(bed, ct);
        if (saved.IsFailure)
        {
            return saved.Error!;
        }

        await transaction.CommitAsync(ct);
        await InpatientRealtime.BedChangedAsync(realtime, bed, ct);
        await InpatientRealtime.AdmissionChangedAsync(realtime, admission, ct);
        return new AdmissionCreated(admission.Id, admission.AdmissionNumber, visit.Id, visit.VisitNumber, bed.Id, bed.Label);
    }

    public async Task<Result> UpdateAsync(Guid admissionId, UpdateAdmissionRequest request, CancellationToken ct)
    {
        var admission = await db.Set<Admission>().FirstOrDefaultAsync(a => a.Id == admissionId, ct);
        if (admission is null)
        {
            return Error.NotFound(nameof(Admission), admissionId);
        }

        if (!admission.IsActive)
        {
            return Error.Rule("admission.closed", "A closed admission cannot be edited.");
        }

        var consultant = await ActivePractitionerAsync(request.ConsultantPractitionerId, ct);
        if (consultant is null)
        {
            return Error.Field("consultantPractitionerId", "Choose an active consultant.");
        }

        if (request.DepartmentId is { } departmentId && !await db.Set<Department>().AnyAsync(d => d.Id == departmentId && d.IsActive, ct))
        {
            return Error.Field("departmentId", "Choose an active department.");
        }

        db.SetExpectedVersion(admission, request.Version);
        admission.ConsultantPractitionerId = consultant.Value.Id;
        admission.DepartmentId = request.DepartmentId ?? admission.DepartmentId;
        admission.ProvisionalDiagnosis = Clean(request.ProvisionalDiagnosis);
        admission.ExpectedDischargeDate = request.ExpectedDischargeDate;
        admission.IsMlc = request.IsMlc;
        admission.MlcNumber = Clean(request.MlcNumber);
        admission.AttendantName = Clean(request.AttendantName);
        admission.AttendantRelation = Clean(request.AttendantRelation);
        admission.AttendantPhone = Phone.Normalize(request.AttendantPhone);
        admission.Notes = Clean(request.Notes);
        ApplyPolicyNumber(admission, request.PolicyNumber);

        // Keep the in-patient visit in step: doctor fee accounting and worklists read the visit's practitioner.
        var visit = await db.Set<Visit>().FirstOrDefaultAsync(v => v.Id == admission.VisitId, ct);
        if (visit is not null)
        {
            visit.PractitionerId = consultant.Value.Id;
            visit.DepartmentId = admission.DepartmentId;
            visit.IsMlc = admission.IsMlc;
            visit.MlcNumber = admission.MlcNumber;
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> TransferAsync(Guid admissionId, TransferPatientRequest request, CancellationToken ct)
    {
        var admission = await db.Set<Admission>().FirstOrDefaultAsync(a => a.Id == admissionId, ct);
        if (admission is null)
        {
            return Error.NotFound(nameof(Admission), admissionId);
        }

        var patient = await db.Set<Patient>().AsNoTracking()
            .Where(p => p.Id == admission.PatientId)
            .Select(p => new { p.Id, p.Gender, p.DateOfBirth })
            .FirstAsync(ct);
        var target = await db.Set<Bed>().FirstOrDefaultAsync(b => b.Id == request.ToBedId, ct);
        if (target is null)
        {
            return Error.Field("toBedId", "Choose the destination bed.");
        }

        var ward = await db.Set<Ward>().FirstOrDefaultAsync(w => w.Id == target.WardId, ct);
        if (ward is null || !ward.IsActive)
        {
            return Error.Rule("ward.inactive", "The destination ward is not in service.");
        }

        var now = time.GetUtcNow();
        if (target.Id != admission.BedId && !target.IsAvailableFor(patient.Id, now))
        {
            return BedOperations.Unavailable(target);
        }

        var eligibility = WardEligibility.Check(ward.Type, ward.GenderPolicy, patient.Gender, patient.DateOfBirth, time.Today());
        if (eligibility.IsFailure)
        {
            return eligibility;
        }

        if (request.ConsultantPractitionerId is { } newConsultant && await ActivePractitionerAsync(newConsultant, ct) is null)
        {
            return Error.Field("consultantPractitionerId", "Choose an active consultant.");
        }

        var at = request.TransferredAt ?? now;
        var current = await db.Set<BedAssignment>()
            .Where(a => a.AdmissionId == admissionId && a.ToAt == null)
            .OrderByDescending(a => a.FromAt)
            .FirstOrDefaultAsync(ct);
        if (current is not null && at < current.FromAt)
        {
            return Error.Rule("transfer.before_current_bed", "The transfer time cannot be before the patient moved into the current bed.");
        }

        var moved = admission.TransferTo(ward.Id, target.Id, ward.BedServiceItemId, at);
        if (moved.IsFailure)
        {
            return moved;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Phase 1: vacate the old bed (the unique "one bed per admission" index must never see two beds at once).
        var fromBed = current is null ? null : await db.Set<Bed>().FirstOrDefaultAsync(b => b.Id == current.BedId, ct);
        current?.Close(at);
        if (fromBed is not null && fromBed.CurrentAdmissionId == admission.Id)
        {
            var released = BedOperations.Release(db, fromBed, HousekeepingTrigger.Transfer, admission.Id, "Cleaning after transfer", now);
            if (released.IsFailure)
            {
                return released;
            }
        }

        admission.ConsultantPractitionerId = request.ConsultantPractitionerId ?? admission.ConsultantPractitionerId;
        admission.DepartmentId = request.DepartmentId ?? admission.DepartmentId;
        var vacated = await SaveAsync(target, ct);
        if (vacated.IsFailure)
        {
            return vacated;
        }

        // Phase 2: occupy the new bed and open the new stay.
        var occupied = target.Occupy(admission.Id, patient.Id, now);
        if (occupied.IsFailure)
        {
            return occupied;
        }

        db.Add(new BedAssignment
        {
            FacilityId = admission.FacilityId,
            AdmissionId = admission.Id,
            BedId = target.Id,
            WardId = ward.Id,
            BedServiceItemId = ward.BedServiceItemId,
            IsCriticalCare = ward.IsCriticalCare,
            Reason = BedAssignmentReason.Transfer,
            TransferReason = request.Reason.Trim(),
            FromAt = at,
        });

        var saved = await SaveAsync(target, ct);
        if (saved.IsFailure)
        {
            return saved;
        }

        await transaction.CommitAsync(ct);
        await InpatientRealtime.BedsChangedAsync(realtime, fromBed is null ? [target] : [fromBed, target], ct);
        await InpatientRealtime.AdmissionChangedAsync(realtime, admission, ct);
        return Result.Success();
    }

    /// <summary>
    /// Cancels an admission made in error: the visit is cancelled, the bed goes for cleaning and every charge this
    /// module posted is reversed in the same transaction (the cancellation fails if Billing has already billed them).
    /// </summary>
    public async Task<Result> CancelAsync(Guid admissionId, CancelAdmissionRequest request, CancellationToken ct)
    {
        var admission = await db.Set<Admission>().FirstOrDefaultAsync(a => a.Id == admissionId, ct);
        if (admission is null)
        {
            return Error.NotFound(nameof(Admission), admissionId);
        }

        var now = time.GetUtcNow();
        var reason = request.Reason.Trim();
        var cancelled = admission.Cancel(reason, now);
        if (cancelled.IsFailure)
        {
            return cancelled;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var visit = await db.Set<Visit>().FirstOrDefaultAsync(v => v.Id == admission.VisitId, ct);
        visit?.Cancel($"Admission cancelled: {reason}", now);

        var current = await db.Set<BedAssignment>().Where(a => a.AdmissionId == admissionId && a.ToAt == null).ToListAsync(ct);
        current.ForEach(a => a.Close(now));

        var bed = admission.BedId is { } bedId ? await db.Set<Bed>().FirstOrDefaultAsync(b => b.Id == bedId, ct) : null;
        if (bed is not null && bed.CurrentAdmissionId == admission.Id)
        {
            var released = BedOperations.Release(db, bed, HousekeepingTrigger.CancelledAdmission, admission.Id, "Cleaning after cancelled admission", now);
            if (released.IsFailure)
            {
                return released;
            }
        }

        var reversed = await charges.ReverseAllAsync(admission.Id, $"Admission {admission.AdmissionNumber} cancelled: {reason}", ct);
        if (reversed.IsFailure)
        {
            return Error.Rule("admission.charges_billed",
                $"Charges of this admission could not be reversed ({reversed.Error!.Message}). Ask Billing to credit them first.");
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        if (bed is not null)
        {
            await InpatientRealtime.BedChangedAsync(realtime, bed, ct);
        }

        await InpatientRealtime.AdmissionChangedAsync(realtime, admission, ct);
        return Result.Success();
    }

    private async Task<(Guid Id, string DisplayName)?> ActivePractitionerAsync(Guid id, CancellationToken ct)
    {
        var practitioner = await db.Set<Practitioner>().AsNoTracking()
            .Where(p => p.Id == id && p.IsActive)
            .Select(p => new { p.Id, p.DisplayName })
            .FirstOrDefaultAsync(ct);
        return practitioner is null ? null : (practitioner.Id, practitioner.DisplayName);
    }

    /// <summary>null leaves the number unchanged, empty removes it; stored encrypted with a masked copy for display.</summary>
    private void ApplyPolicyNumber(Admission admission, string? policyNumber)
    {
        if (policyNumber is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(policyNumber))
        {
            admission.PolicyNumberCipher = null;
            admission.PolicyNumberMasked = null;
            return;
        }

        var value = policyNumber.Trim();
        admission.PolicyNumberCipher = protector.Protect(value);
        admission.PolicyNumberMasked = Masking.MaskIdentifier(value);
    }

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
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } unique)
        {
            return unique.ConstraintName == "ux_admissions_active_patient"
                ? Error.Conflict("admission.already_admitted", "This patient was admitted from another desk a moment ago.")
                : Error.Conflict("bed.taken", $"Bed {bed.Label} was allocated from another desk a moment ago. Choose another bed.");
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
