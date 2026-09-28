using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Physiotherapy;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.Physio;

/// <summary>/api/v1/physio — referrals, assessments, treatment plans, sessions and the day worklist.</summary>
internal static class PhysioEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/physio").WithTags("Physiotherapy");

        group.MapGet("/referrals", ListReferrals).RequirePermission(SpecialtyCarePermissions.PhysioRead);
        group.MapGet("/referrals/{id:guid}", GetReferral).RequirePermission(SpecialtyCarePermissions.PhysioRead);
        group.MapPost("/referrals", async (CreatePhysioReferralRequest r, PhysioService s, CancellationToken ct) =>
                (await s.CreateReferralAsync(r, ct)).ToHttpResult(id => TypedResults.Created($"/api/v1/physio/referrals/{id}", new { id })))
            .RequirePermission(SpecialtyCarePermissions.PhysioReferrals).WithValidation<CreatePhysioReferralRequest>();
        group.MapPost("/referrals/{id:guid}/cancel", async (Guid id, PhysioReasonRequest r, PhysioService s, CancellationToken ct) =>
                (await s.CancelReferralAsync(id, r.Reason, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.PhysioReferrals).WithValidation<PhysioReasonRequest>();
        group.MapPost("/referrals/{id:guid}/complete", async (Guid id, PhysioReasonRequest r, PhysioService s, CancellationToken ct) =>
                (await s.CompleteReferralAsync(id, r.Reason, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.PhysioTreatment).WithValidation<PhysioReasonRequest>();
        group.MapPost("/referrals/{id:guid}/assessments", async (Guid id, RecordPhysioAssessmentRequest r, PhysioService s, CancellationToken ct) =>
                (await s.RecordAssessmentAsync(id, r, ct)).ToHttpResult(a => TypedResults.Created($"/api/v1/physio/referrals/{id}", new { id = a })))
            .RequirePermission(SpecialtyCarePermissions.PhysioTreatment).WithValidation<RecordPhysioAssessmentRequest>();
        group.MapPut("/referrals/{id:guid}/plan", async (Guid id, SaveTreatmentPlanRequest r, PhysioService s, CancellationToken ct) =>
                (await s.SavePlanAsync(id, r, ct)).ToHttpResult(p => TypedResults.Ok(new { id = p })))
            .RequirePermission(SpecialtyCarePermissions.PhysioTreatment).WithValidation<SaveTreatmentPlanRequest>();
        group.MapPost("/referrals/{id:guid}/sessions", async (Guid id, SchedulePhysioSessionsRequest r, PhysioService s, CancellationToken ct) =>
                (await s.ScheduleSessionsAsync(id, r, ct)).ToOk())
            .RequirePermission(SpecialtyCarePermissions.PhysioTreatment).WithValidation<SchedulePhysioSessionsRequest>();

        group.MapGet("/sessions", Worklist).RequirePermission(SpecialtyCarePermissions.PhysioRead);
        group.MapPut("/sessions/{id:guid}", async (Guid id, ReschedulePhysioSessionRequest r, PhysioService s, CancellationToken ct) =>
                (await s.RescheduleAsync(id, r.ScheduledAt, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.PhysioTreatment).WithValidation<ReschedulePhysioSessionRequest>();
        group.MapPost("/sessions/{id:guid}/attend", async (Guid id, AttendPhysioSessionRequest r, PhysioService s, CancellationToken ct) =>
                (await s.AttendAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.PhysioTreatment).WithValidation<AttendPhysioSessionRequest>();
        group.MapPost("/sessions/{id:guid}/close", async (Guid id, ClosePhysioSessionRequest r, PhysioService s, CancellationToken ct) =>
                (await s.CloseSessionAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.PhysioTreatment).WithValidation<ClosePhysioSessionRequest>();
        group.MapPost("/sessions/{id:guid}/charges/retry", async (Guid id, PhysioService s, CancellationToken ct) =>
                (await s.RetryChargesAsync(id, ct)).ToOk())
            .RequirePermission(SpecialtyCarePermissions.PhysioTreatment);
    }

    private static async Task<IResult> ListReferrals(
        [AsParameters] PageQuery query, PhysioReferralStatus? status, bool? openOnly, HospitalDbContext db, IFieldProtector protector, TimeProvider time, CancellationToken ct)
    {
        var page = query.ToRequest();
        var referrals = db.Set<PhysioReferral>().AsNoTracking();
        if (status is not null)
        {
            referrals = referrals.Where(r => r.Status == status);
        }
        else if (openOnly != false)
        {
            referrals = referrals.Where(r => r.Status != PhysioReferralStatus.Completed && r.Status != PhysioReferralStatus.Cancelled);
        }

        if (page.SearchTerm is { } term)
        {
            var matching = db.Set<Patient>().Search(term, protector).Select(p => p.Id);
            referrals = referrals.Where(r => matching.Contains(r.PatientId));
        }

        var result = await (
                from r in referrals
                join p in db.Set<Patient>() on r.PatientId equals p.Id
                // Priority is stored as text: rank it explicitly (stat, urgent, routine) rather than alphabetically.
                orderby r.Priority == OrderPriority.Stat ? 0 : r.Priority == OrderPriority.Urgent ? 1 : 2, r.ReferredAt
                select new { r, p.Uhid, p.FullName, p.Gender, p.DateOfBirth })
            .ToPagedResultAsync(page, ct);

        var today = time.Today();
        var names = await SpecialtyLookups.PractitionerNamesAsync(db, result.Items.Select(i => i.r.ReferredByPractitionerId), ct);
        return TypedResults.Ok(result.Map(i => new PhysioReferralListItem(
            i.r.Id, i.r.PatientId, i.Uhid, i.FullName, i.Gender, i.DateOfBirth is { } dob ? Age.Format(dob, today) : null, i.r.ServiceName, i.r.Reason,
            i.r.SessionsOrdered, i.r.Priority, i.r.Status, i.r.ReferredAt, i.r.ReferredByPractitionerId,
            i.r.ReferredByPractitionerId is { } by ? names.GetValueOrDefault(by) : null, i.r.AssignedTherapistName, i.r.AdmissionId is not null)));
    }

    private static async Task<IResult> GetReferral(Guid id, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var referral = await db.Set<PhysioReferral>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (referral is null)
        {
            return Error.NotFound(nameof(PhysioReferral), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(referral.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(referral.PatientId, "Viewed physiotherapy record", ct);
        var assessments = await db.Set<PhysioAssessment>().AsNoTracking().Where(a => a.ReferralId == id).OrderBy(a => a.AssessedAt).ToListAsync(ct);
        var plan = await db.Set<TreatmentPlan>().AsNoTracking().FirstOrDefaultAsync(p => p.ReferralId == id, ct);
        var sessions = await db.Set<PhysioSession>().AsNoTracking().Where(s => s.ReferralId == id).OrderBy(s => s.SessionNumber).ToListAsync(ct);
        var patients = await PatientQueries.SummariesAsync(db, [referral.PatientId], time.Today(), ct);
        var names = await SpecialtyLookups.PractitionerNamesAsync(db, [referral.ReferredByPractitionerId], ct);

        return TypedResults.Ok(new PhysioReferralDetail(
            referral.Id, patients.GetValueOrDefault(referral.PatientId), referral.PatientId, referral.VisitId, referral.AdmissionId, referral.OrderItemId,
            referral.OrderNumber, referral.ServiceName, referral.SessionsOrdered, referral.ReferredByPractitionerId,
            referral.ReferredByPractitionerId is { } by ? names.GetValueOrDefault(by) : null, referral.Priority, referral.Reason, referral.ReferredAt,
            referral.Status, referral.AssignedTherapistName, referral.DischargeSummary, referral.ClosedAt, referral.CancelReason,
            assessments.Select(a => new PhysioAssessmentDto(a.Id, a.Type, a.AssessedAt, a.AssessedByName, a.ChiefComplaint, a.PainScore, a.PainLocation,
                a.RangeOfMotion, a.Strength, a.FunctionalStatus, a.Goals, a.Notes)).ToList(),
            plan is null ? null : new TreatmentPlanDto(plan.Id, plan.Modalities, plan.ExerciseProgramme, plan.SessionsPlanned, plan.SessionsPerWeek,
                plan.SessionMinutes, plan.StartDate, plan.Precautions, plan.Goals, plan.Revision, plan.Version),
            sessions.Select(ToDto).ToList(),
            referral.Version));
    }

    /// <summary>Sessions for one local day (default today) — the physiotherapist's worklist.</summary>
    private static async Task<IResult> Worklist(DateOnly? date, PhysioSessionStatus? status, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var (start, end) = SpecialtyClock.RangeUtc(date ?? time.Today(), date ?? time.Today());
        var rows = await (
                from s in db.Set<PhysioSession>().AsNoTracking()
                join p in db.Set<Patient>() on s.PatientId equals p.Id
                join t in db.Set<TreatmentPlan>() on s.ReferralId equals t.ReferralId into plans
                from t in plans.DefaultIfEmpty()
                where s.ScheduledAt >= start && s.ScheduledAt < end && (status == null || s.Status == status)
                orderby s.ScheduledAt
                select new PhysioWorklistItem(s.Id, s.ReferralId, s.PatientId, p.Uhid, p.FullName, s.SessionNumber, t != null ? t.SessionsPlanned : null,
                    s.ScheduledAt, s.Status, s.TherapistName))
            .ToListAsync(ct);
        return TypedResults.Ok(rows);
    }

    private static PhysioSessionDto ToDto(PhysioSession s) => new(
        s.Id, s.ReferralId, s.PatientId, s.SessionNumber, s.ScheduledAt, s.Status, s.ModalitiesGiven, s.ProgressNote, s.PainBefore, s.PainAfter,
        s.DurationMinutes, s.AttendedAt, s.TherapistName, s.MissedReason, s.ChargeStatus, s.ChargeError);
}
