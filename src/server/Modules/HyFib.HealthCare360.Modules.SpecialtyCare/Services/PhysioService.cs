using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Physiotherapy;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Physio;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// Physiotherapy workflow (PHY-01..04): referral → assessment → treatment plan → scheduled sessions → attendance,
/// progress notes and per-session charges. Sessions cannot exceed the plan, and treatment needs an assessment first.
/// </summary>
public sealed class PhysioService(
    HospitalDbContext db,
    SpecialtyChargeService charges,
    IRealtimePublisher realtime,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public async Task<Result<Guid>> CreateReferralAsync(CreatePhysioReferralRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var patient = await SpecialtyLookups.EnsurePatientAsync(db, request.PatientId, ct);
        if (patient.IsFailure)
        {
            return patient.Error!;
        }

        var referrer = request.ReferredByPractitionerId ?? currentUser.PractitionerId;
        var practitioner = await SpecialtyLookups.EnsurePractitionersAsync(db, [("referredByPractitionerId", referrer)], ct);
        if (practitioner.IsFailure)
        {
            return practitioner.Error!;
        }

        var (admissionId, visitId) = await SpecialtyLookups.EncounterAsync(db, request.PatientId, request.AdmissionId, request.VisitId, ct);
        var referral = new PhysioReferral
        {
            FacilityId = facilityId,
            PatientId = request.PatientId,
            VisitId = visitId,
            AdmissionId = admissionId,
            ReferredByPractitionerId = referrer,
            Priority = request.Priority,
            Reason = request.Reason.Trim(),
            SessionsOrdered = request.SessionsOrdered,
            ReferredAt = time.GetUtcNow(),
            Status = PhysioReferralStatus.New,
        };
        db.Add(referral);
        await db.SaveChangesAsync(ct);
        await PublishAsync(facilityId, ct);
        return referral.Id;
    }

    public Task<Result> CancelReferralAsync(Guid referralId, string reason, CancellationToken ct) =>
        ChangeReferralAsync(referralId, async referral =>
        {
            var attended = await db.Set<PhysioSession>().AnyAsync(s => s.ReferralId == referralId && s.Status == PhysioSessionStatus.Attended, ct);
            var result = referral.Cancel(reason, attended, time.GetUtcNow());
            if (result.IsSuccess)
            {
                await CloseOpenSessionsAsync(referralId, "Referral cancelled", ct);
            }

            return result;
        }, ct);

    public Task<Result> CompleteReferralAsync(Guid referralId, string summary, CancellationToken ct) =>
        ChangeReferralAsync(referralId, async referral =>
        {
            var result = referral.Complete(summary, time.GetUtcNow());
            if (result.IsSuccess)
            {
                await CloseOpenSessionsAsync(referralId, "Treatment completed", ct);
            }

            return result;
        }, ct);

    public async Task<Result<Guid>> RecordAssessmentAsync(Guid referralId, RecordPhysioAssessmentRequest request, CancellationToken ct)
    {
        var referral = await db.Set<PhysioReferral>().FirstOrDefaultAsync(r => r.Id == referralId, ct);
        if (referral is null)
        {
            return Error.NotFound(nameof(PhysioReferral), referralId);
        }

        var open = referral.EnsureOpen();
        if (open.IsFailure)
        {
            return open.Error!;
        }

        var stamp = currentUser.Stamp(time.GetUtcNow());
        var assessment = new PhysioAssessment
        {
            FacilityId = referral.FacilityId,
            ReferralId = referral.Id,
            PatientId = referral.PatientId,
            Type = request.Type,
            AssessedAt = stamp.At,
            AssessedByName = stamp.Name,
            ChiefComplaint = request.ChiefComplaint.Trim(),
            PainScore = request.PainScore,
            PainLocation = Clean(request.PainLocation),
            RangeOfMotion = [.. request.RangeOfMotion ?? []],
            Strength = [.. request.Strength ?? []],
            FunctionalStatus = Clean(request.FunctionalStatus),
            Goals = Clean(request.Goals),
            Notes = Clean(request.Notes),
        };
        db.Add(assessment);
        referral.MarkAssessed();
        referral.AssignedTherapistUserId ??= stamp.UserId;
        referral.AssignedTherapistName ??= stamp.Name;
        await db.SaveChangesAsync(ct);
        await PublishAsync(referral.FacilityId, ct);
        return assessment.Id;
    }

    public async Task<Result<Guid>> SavePlanAsync(Guid referralId, SaveTreatmentPlanRequest request, CancellationToken ct)
    {
        var referral = await db.Set<PhysioReferral>().FirstOrDefaultAsync(r => r.Id == referralId, ct);
        if (referral is null)
        {
            return Error.NotFound(nameof(PhysioReferral), referralId);
        }

        var started = referral.StartTreatment();
        if (started.IsFailure)
        {
            return started.Error!;
        }

        var sessionsPlanned = TreatmentPlan.SessionsToPlan(request.SessionsPlanned, referral.SessionsOrdered);
        if (sessionsPlanned.IsFailure)
        {
            return sessionsPlanned.Error!;
        }

        var booked = await db.Set<PhysioSession>().CountAsync(s => s.ReferralId == referralId && s.Status != PhysioSessionStatus.Cancelled, ct);
        if (sessionsPlanned.Value < booked)
        {
            return Error.Rule("physio.plan_below_booked", $"{booked} session(s) are already booked; the plan cannot allow fewer.");
        }

        var plan = await db.Set<TreatmentPlan>().FirstOrDefaultAsync(p => p.ReferralId == referralId, ct);
        if (plan is null)
        {
            plan = new TreatmentPlan { FacilityId = referral.FacilityId, ReferralId = referral.Id, PatientId = referral.PatientId };
            db.Add(plan);
        }
        else
        {
            db.SetExpectedVersion(plan, request.Version);

            plan.Revision++;
        }

        plan.Modalities = request.Modalities.Distinct().ToList();
        plan.ExerciseProgramme = Clean(request.ExerciseProgramme);
        plan.SessionsPlanned = sessionsPlanned.Value;
        plan.SessionsPerWeek = request.SessionsPerWeek;
        plan.SessionMinutes = request.SessionMinutes;
        plan.StartDate = request.StartDate;
        plan.Precautions = Clean(request.Precautions);
        plan.Goals = Clean(request.Goals);
        await db.SaveChangesAsync(ct);
        await PublishAsync(referral.FacilityId, ct);
        return plan.Id;
    }

    /// <summary>Books a series of sessions; spacing defaults to the plan frequency (3/week → every 2 days).</summary>
    public async Task<Result<IReadOnlyList<Guid>>> ScheduleSessionsAsync(Guid referralId, SchedulePhysioSessionsRequest request, CancellationToken ct)
    {
        var referral = await db.Set<PhysioReferral>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == referralId, ct);
        if (referral is null)
        {
            return Error.NotFound(nameof(PhysioReferral), referralId);
        }

        if (referral.Status != PhysioReferralStatus.InTreatment)
        {
            return Error.Rule("physio.plan_required", "Save a treatment plan before scheduling sessions.");
        }

        var plan = await db.Set<TreatmentPlan>().AsNoTracking().FirstAsync(p => p.ReferralId == referralId, ct);
        var sessions = await db.Set<PhysioSession>().Where(s => s.ReferralId == referralId).Select(s => new { s.SessionNumber, s.Status }).ToListAsync(ct);
        var allowed = plan.EnsureCanSchedule(sessions.Count(s => s.Status != PhysioSessionStatus.Cancelled), request.Count);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        if (request.FirstSessionAt < time.GetUtcNow().AddHours(-12))
        {
            return Error.Field("firstSessionAt", "Sessions cannot be scheduled in the past.");
        }

        var interval = request.IntervalDays ?? Math.Max(1, 7 / Math.Max(1, plan.SessionsPerWeek));
        var next = sessions.Count == 0 ? 1 : sessions.Max(s => s.SessionNumber) + 1;
        var created = Enumerable.Range(0, request.Count).Select(i => new PhysioSession
        {
            FacilityId = referral.FacilityId,
            ReferralId = referral.Id,
            PatientId = referral.PatientId,
            SessionNumber = next + i,
            ScheduledAt = request.FirstSessionAt.AddDays(i * interval),
            Status = PhysioSessionStatus.Scheduled,
        }).ToList();
        db.AddRange(created);
        await db.SaveChangesAsync(ct);
        await PublishAsync(referral.FacilityId, ct);
        return created.Select(s => s.Id).ToList();
    }

    public Task<Result> RescheduleAsync(Guid sessionId, DateTimeOffset scheduledAt, uint version, CancellationToken ct) =>
        ChangeSessionAsync(sessionId, s =>
        {
            db.SetExpectedVersion(s, version);
            return Task.FromResult(s.Reschedule(scheduledAt));
        }, ct);

    public Task<Result> CloseSessionAsync(Guid sessionId, ClosePhysioSessionRequest request, CancellationToken ct) =>
        ChangeSessionAsync(sessionId, s => Task.FromResult(s.Close(request.Missed ? PhysioSessionStatus.Missed : PhysioSessionStatus.Cancelled, request.Reason)), ct);

    /// <summary>Records the session and bills it per modality (PHY-04).</summary>
    public Task<Result> AttendAsync(Guid sessionId, AttendPhysioSessionRequest request, CancellationToken ct) =>
        ChangeSessionAsync(sessionId, async session =>
        {
            var referral = await db.Set<PhysioReferral>().AsNoTracking().FirstAsync(r => r.Id == session.ReferralId, ct);
            var open = referral.EnsureOpen();
            if (open.IsFailure)
            {
                return open;
            }

            var result = session.Attend(request.Modalities, request.ProgressNote, request.PainBefore, request.PainAfter, request.DurationMinutes,
                currentUser.Stamp(time.GetUtcNow()));
            if (result.IsSuccess)
            {
                var outcome = await PostChargesAsync(session, referral, ct);
                session.RecordCharges(outcome.Status, outcome.Error);
            }

            return result;
        }, ct);

    public async Task<Result<ChargeOutcome>> RetryChargesAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db.Set<PhysioSession>().FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return Error.NotFound(nameof(PhysioSession), sessionId);
        }

        if (session.Status != PhysioSessionStatus.Attended)
        {
            return Error.Rule("physio.session_not_attended", "Only attended sessions are charged.");
        }

        var referral = await db.Set<PhysioReferral>().AsNoTracking().FirstAsync(r => r.Id == session.ReferralId, ct);
        var outcome = await PostChargesAsync(session, referral, ct);
        session.RecordCharges(outcome.Status, outcome.Error);
        await db.SaveChangesAsync(ct);
        return outcome;
    }

    private Task<ChargeOutcome> PostChargesAsync(PhysioSession session, PhysioReferral referral, CancellationToken ct) =>
        charges.PostAsync(PhysioTariff.ChargeCodesFor(session.ModalitiesGiven)
            .Select(code => new ChargeLine(ChargeSources.Physio, $"{session.Id}:{code}", session.PatientId, referral.VisitId, referral.AdmissionId, code,
                Description: $"Physiotherapy session {session.SessionNumber}", ServiceDate: session.AttendedAt))
            .ToList(), ct);

    private async Task CloseOpenSessionsAsync(Guid referralId, string reason, CancellationToken ct)
    {
        var open = await db.Set<PhysioSession>().Where(s => s.ReferralId == referralId && s.Status == PhysioSessionStatus.Scheduled).ToListAsync(ct);
        foreach (var session in open)
        {
            session.Close(PhysioSessionStatus.Cancelled, reason);
        }
    }

    private async Task<Result> ChangeReferralAsync(Guid referralId, Func<PhysioReferral, Task<Result>> change, CancellationToken ct)
    {
        var referral = await db.Set<PhysioReferral>().FirstOrDefaultAsync(r => r.Id == referralId, ct);
        if (referral is null)
        {
            return Error.NotFound(nameof(PhysioReferral), referralId);
        }

        var result = await change(referral);
        if (result.IsFailure)
        {
            return result;
        }

        await db.SaveChangesAsync(ct);
        await PublishAsync(referral.FacilityId, ct);
        return Result.Success();
    }

    private async Task<Result> ChangeSessionAsync(Guid sessionId, Func<PhysioSession, Task<Result>> change, CancellationToken ct)
    {
        var session = await db.Set<PhysioSession>().FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return Error.NotFound(nameof(PhysioSession), sessionId);
        }

        var result = await change(session);
        if (result.IsFailure)
        {
            return result;
        }

        await db.SaveChangesAsync(ct);
        await PublishAsync(session.FacilityId, ct);
        return Result.Success();
    }

    private Task PublishAsync(Guid facilityId, CancellationToken ct) =>
        realtime.FacilityChangedAsync(facilityId, SpecialtyRealtime.PhysioChanged, new { facilityId }, ct);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
