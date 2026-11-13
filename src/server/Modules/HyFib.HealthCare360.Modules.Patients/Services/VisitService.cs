using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// Visits and department queues (PMS-03): check-in creates a visit and a daily token; doctors call, start and
/// complete tokens; every change is pushed in real time to the doctor's worklist and the waiting-area display.
/// </summary>
public sealed class VisitService(
    HospitalDbContext db,
    INumberSeries numbers,
    IRealtimePublisher realtime,
    INotificationService notifications,
    ICurrentUser currentUser,
    IPayerDirectory payers,
    IApprovals approvals,
    IVisitClinicalWork clinicalWork,
    IAuditLogger audit,
    TimeProvider time,
    DowntimeNumberService downtimeNumbers)
{
    public async Task<Result<VisitDetail>> CreateAsync(CreateVisitRequest request, CancellationToken ct)
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
            return Error.Rule("patient.inactive", "Visits cannot be created for inactive or deceased patients.");
        }

        // A visit entered from paper keeps the number and the time on the paper (downtime visit numbers).
        var now = request.Paper?.At ?? time.GetUtcNow();
        var today = request.Paper is null ? time.Today() : DateOnly.FromDateTime(HyFib.HealthCare360.Platform.Numbering.NumberFormat.ToHospitalTime(now).Date);
        var dayStart = StartOfLocalDay(now);

        // The desk names a third party that pays only when a second person already stands behind it: the patient's policy
        // or membership with it, in date today and verified by someone other than whoever recorded it (PayerChoice).
        if (PayerChoice.IsThirdParty(request.PayerType))
        {
            var standing = request.PayerId is { } payerId ? await payers.StandingAsync(patient.Id, payerId, today, ct) : null;
            if (PayerChoice.Refusal(request.PayerType, request.PayerId, standing) is { } refused)
            {
                return refused;
            }
        }

        if (request.Type == VisitType.OutPatient)
        {
            var open = await db.Set<Visit>()
                .Where(v => v.PatientId == request.PatientId && v.Type == VisitType.OutPatient && v.DepartmentId == request.DepartmentId
                            && (v.Status == VisitStatus.Open || v.Status == VisitStatus.InProgress) && v.VisitedAt >= dayStart)
                .Select(v => new { v.Id, v.VisitNumber })
                .FirstOrDefaultAsync(ct);
            if (open is not null)
            {
                return Error.Conflict("visit.already_open", $"This patient already has an open visit today in this department ({open.VisitNumber}).", new { visitId = open.Id });
            }
        }

        Visit? previous = null;
        if (request.PractitionerId is { } practitionerId && request.Type is VisitType.OutPatient or VisitType.Telehealth)
        {
            var validityDays = await db.Set<Practitioner>().Where(p => p.Id == practitionerId).Select(p => (int?)p.FollowUpValidityDays).FirstOrDefaultAsync(ct);
            if (validityDays is > 0)
            {
                var since = now.AddDays(-validityDays.Value);
                previous = await db.Set<Visit>()
                    .Where(v => v.PatientId == request.PatientId && v.PractitionerId == practitionerId && v.Status == VisitStatus.Completed
                                && !v.IsFreeFollowUp && v.VisitedAt >= since)
                    .OrderByDescending(v => v.VisitedAt)
                    .FirstOrDefaultAsync(ct);
            }
        }

        Department? department = null;
        if (request.DepartmentId is { } departmentId)
        {
            department = await db.Set<Department>().FirstOrDefaultAsync(d => d.Id == departmentId && d.IsActive, ct);
            if (department is null)
            {
                return Error.Field("departmentId", "Choose an active department.");
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var visitId = Guid.CreateVersion7();
        string visitNumber;
        if (request.Paper is { } paper)
        {
            var claimed = await downtimeNumbers.ClaimAsync(DowntimeNumberKind.Visit, paper, nameof(Visit), visitId, patient.Id, ct);
            if (claimed.IsFailure)
            {
                return claimed.Error!;
            }

            visitNumber = claimed.Value.Number;
        }
        else
        {
            visitNumber = await numbers.NextAsync(PatientsNumberSeries.Visit, facilityId, ct);
        }

        var visit = new Visit
        {
            Id = visitId,
            FacilityId = facilityId,
            VisitNumber = visitNumber,
            PatientId = patient.Id,
            Type = request.Type,
            Status = VisitStatus.Open,
            DepartmentId = request.DepartmentId,
            PractitionerId = request.PractitionerId,
            AppointmentId = request.AppointmentId,
            VisitedAt = now,
            IsFollowUp = previous is not null,
            IsFreeFollowUp = previous is not null,
            PreviousVisitId = previous?.Id,
            PayerType = request.PayerType,
            // Only a third party is a payer of the payer master: the patient, staff and charity price on their own plans.
            PayerId = PayerChoice.IsThirdParty(request.PayerType) ? request.PayerId : null,
            ReferralSource = request.ReferralSource,
            ReferredBy = request.ReferredBy?.Trim(),
            ChiefComplaint = request.ChiefComplaint?.Trim(),
            IsMlc = request.IsMlc,
            MlcNumber = request.MlcNumber?.Trim(),
            Notes = request.Notes?.Trim(),
        };
        visit.MarkCreated();
        db.Add(visit);

        QueueToken? token = null;
        if (request.IssueToken && department is not null)
        {
            var sequence = int.Parse(
                await numbers.NextAsync(PatientsNumberSeries.QueueToken(department.Id), facilityId, ct),
                System.Globalization.CultureInfo.InvariantCulture);
            token = new QueueToken
            {
                FacilityId = facilityId,
                VisitId = visit.Id,
                PatientId = patient.Id,
                DepartmentId = department.Id,
                PractitionerId = request.PractitionerId,
                TokenDate = today,
                Sequence = sequence,
                Label = $"{department.TokenPrefix ?? department.Code}-{sequence:000}",
                Status = TokenStatus.Waiting,
                Priority = request.Priority,
                IssuedAt = now,
            };
            db.Add(token);

            if (request.NotifyPatient && patient.Phone is not null)
            {
                notifications.Queue("visit.token",
                    NotificationRecipient.Patient(patient.Phone, patient.Email, patient.Id, patient.FullName, patient.PreferredLanguage),
                    new Dictionary<string, string> { ["department"] = department.Name, ["token"] = token.Label },
                    [NotificationChannel.Sms],
                    TimeSpan.FromHours(2));
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        if (token is not null)
        {
            await PublishQueueChangeAsync(token, ct);
        }

        return ToDetail(visit, token);
    }

    // An in-patient's visit is the admission's: it closes with the discharge, and Billing reads a cancelled in-patient visit
    // as a cancelled admission (it reverses the IP account), so the front desk cannot close or cancel one on its own.
    private static readonly Error InpatientVisitClosesWithTheStay =
        Error.Rule("visit.inpatient", "An in-patient visit closes with the stay. Discharge the patient or cancel the admission instead.");

    /// <summary>A visit the doctor has started, cancelled at the desk, waiting for a second person.</summary>
    public const string CancelKind = "patients.visit_cancel";

    /// <summary>
    /// Cancels a visit from the desk: at once while the doctor has not started it; once the consultation is under way, or
    /// the visit has an encounter, a note, an order or a prescription, it waits for someone else (a front-office
    /// supervisor, or the treating doctor) who holds <see cref="PatientsPermissions.VisitsCancelApprove"/>, and nothing
    /// changes until then: the token, the charges and the orders follow the cancellation only once approved.
    /// </summary>
    public async Task<Result<VisitCancelOutcome>> AskCancelAsync(Guid visitId, string reason, CancellationToken ct)
    {
        var visit = await db.Set<Visit>().FirstOrDefaultAsync(v => v.Id == visitId, ct);
        if (visit is null)
        {
            return Error.NotFound(nameof(Visit), visitId);
        }

        if (visit.Type == VisitType.InPatient)
        {
            return InpatientVisitClosesWithTheStay;
        }

        // Under the visit's lock, which an order or an encounter placed on it holds until it is saved: the work read here
        // is all there is, and none comes until the cancellation is saved or asked for.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await EncounterLocks.LockAsync(db, [visit.Id], ct);
        await db.Entry(visit).ReloadAsync(ct);
        if (visit.Status is VisitStatus.Completed or VisitStatus.Cancelled)
        {
            return visit.Cancel(reason.Trim(), time.GetUtcNow()).Error!;
        }

        var work = await clinicalWork.OfVisitAsync(visit.Id, ct);
        if (!CancelNeedsApproval(visit.Status, work))
        {
            var cancelled = await CancelVisitAsync(visit, reason.Trim(), ct);
            if (cancelled.IsFailure)
            {
                return cancelled.Error!;
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            if (cancelled.Value is { } token)
            {
                await PublishQueueChangeAsync(token, ct);
            }

            return new VisitCancelOutcome(null);
        }

        var patient = await db.Set<Patient>().Where(p => p.Id == visit.PatientId).Select(p => new { p.Uhid, p.FullName }).FirstOrDefaultAsync(ct);
        var who = patient is null ? "the patient" : $"{patient.FullName} ({patient.Uhid})";
        var doctor = visit.PractitionerId is { } pid
            ? await db.Set<Practitioner>().Where(p => p.Id == pid).Select(p => p.DisplayName).FirstOrDefaultAsync(ct)
            : null;
        List<ApprovalDetail> details =
        [
            new("Visit", $"{visit.VisitNumber}, checked in {DocumentClock(visit.VisitedAt)}" + (doctor is null ? string.Empty : $", with {doctor}")),
            new("Patient", who),
            new("Status", $"{StatusWords(visit.Status)} → cancelled"),
            new("Clinical record", work.Words()),
            new("What follows", "The queue token is cancelled, charges not yet billed are withdrawn, and lab tests not yet drawn and studies not yet done are withdrawn with their order lines. Bills already finalized, notes and prescriptions stay as they are."),
            new("Why it waits", "The doctor has started this visit: its cancellation is approved by a front-office supervisor or the treating doctor, not the person who asks"),
        ];
        var requested = await approvals.RequestAsync(
            CancelKind,
            new ApprovalSubject(nameof(Visit), visit.Id, visit.Version, visit.FacilityId, visit.PatientId),
            new VisitCancelPayload(visit.Id, reason.Trim()),
            Fit($"Cancel visit {visit.VisitNumber} of {who}, which the doctor has started", 300),
            details,
            reason,
            ct);
        if (requested.IsFailure)
        {
            return requested.Error!;
        }

        await transaction.CommitAsync(ct);
        return new VisitCancelOutcome(requested.Value);
    }

    /// <summary>The doctor has started the visit: the consultation is under way, or the record holds work on it.</summary>
    public static bool CancelNeedsApproval(VisitStatus status, VisitClinicalWork work) => status == VisitStatus.InProgress || work.Any;

    /// <summary>The visits among these whose cancellation waits for approval, and when it was asked for.</summary>
    public Task<IReadOnlyDictionary<Guid, DateTimeOffset>> CancellationsAskedAsync(IReadOnlyCollection<Guid> visitIds, CancellationToken ct) =>
        approvals.WaitingSinceAsync(CancelKind, nameof(Visit), visitIds, ct);

    /// <summary>
    /// Cancels a visit whose cancellation was approved, in the approval's transaction (the approval saves): only while it
    /// is still open (<c>approval.stale</c>). A doctor's account approves only a visit it treats: the visit's doctor, or one
    /// who opened an encounter, ordered or prescribed on it; anyone else holding the permission (a front-office supervisor)
    /// approves any. The token, charges and orders then follow the cancellation as when the desk cancels a visit not started.
    /// </summary>
    internal async Task<Result<string>> CancelApprovedAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<VisitCancelPayload>(request.Payload);
        var visit = await db.Set<Visit>().FirstOrDefaultAsync(v => v.Id == asked.VisitId, ct);
        if (visit is null)
        {
            return Error.NotFound(nameof(Visit), asked.VisitId);
        }

        if (visit.Status is VisitStatus.Completed or VisitStatus.Cancelled)
        {
            return Error.Rule("approval.stale", visit.Status == VisitStatus.Cancelled
                ? $"Visit {visit.VisitNumber} was cancelled already, so nothing was changed."
                : $"Visit {visit.VisitNumber} was completed after this was asked for, so it was not cancelled.");
        }

        if (currentUser.PractitionerId is { } practitionerId)
        {
            var work = await clinicalWork.OfVisitAsync(visit.Id, ct);
            if (visit.PractitionerId != practitionerId && !work.Practitioners.Contains(practitionerId))
            {
                return Error.Forbidden("approval.not_treating_doctor",
                    "A doctor approves the cancellation only of a visit they treat. Ask the treating doctor or a front-office supervisor.");
            }
        }

        await EncounterLocks.LockAsync(db, [visit.Id], ct);
        var cancelled = await CancelVisitAsync(visit, Fit(asked.Reason, 300), ct);
        if (cancelled.IsFailure)
        {
            return cancelled.Error!;
        }

        var checker = currentUser.DisplayName ?? currentUser.UserName ?? "Unknown user";
        audit.Add(new AuditEntry(AuditCategories.Data, "patients.visit.cancelled",
            Fit($"Visit {visit.VisitNumber} cancelled after the doctor had started it: {asked.Reason} (asked for by {request.RequestedByName}, approved by {checker})", 1000))
        {
            PatientId = visit.PatientId,
            EntityType = nameof(Visit),
            EntityId = visit.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { visit.VisitNumber, approval = request.Id, requestedBy = request.RequestedBy, approvedBy = currentUser.UserId },
        });
        return $"Visit {visit.VisitNumber} cancelled";
    }

    /// <summary>
    /// Cancels the visit and its queue token (the token changed, if any, comes back); the caller saves, in the
    /// transaction that holds the visit's lock. The modules that follow (Billing, the lab and imaging, Scheduling) react to
    /// <see cref="VisitCancelled"/> in that save, holding the same lock from their reads to its commit, so an order being
    /// placed on the visit is either seen by them or sees the visit cancelled.
    /// </summary>
    private async Task<Result<QueueToken?>> CancelVisitAsync(Visit visit, string reason, CancellationToken ct)
    {
        var result = visit.Cancel(reason, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error!;
        }

        var token = await db.Set<QueueToken>().FirstOrDefaultAsync(t => t.VisitId == visit.Id, ct);
        if (token is not null && token.Status is not (TokenStatus.Completed or TokenStatus.Cancelled))
        {
            token.Status = TokenStatus.Cancelled;
            return token;
        }

        return (QueueToken?)null;
    }

    private static string StatusWords(VisitStatus status) => status == VisitStatus.InProgress ? "In consultation" : "Waiting";

    private static string DocumentClock(DateTimeOffset at) => at.ToOffset(TimeSpan.FromMinutes(330)).ToString("dd MMM yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    public async Task<Result> CompleteAsync(Guid visitId, CancellationToken ct)
    {
        var visit = await db.Set<Visit>().FirstOrDefaultAsync(v => v.Id == visitId, ct);
        if (visit is null)
        {
            return Error.NotFound(nameof(Visit), visitId);
        }

        if (visit.Type == VisitType.InPatient)
        {
            return InpatientVisitClosesWithTheStay;
        }

        var completed = await CompleteWithTokenAsync(visit, time.GetUtcNow(), ct);
        if (completed.IsFailure)
        {
            return completed;
        }

        await db.SaveChangesAsync(ct);
        if (completed.Value is { } token)
        {
            await PublishQueueChangeAsync(token, ct);
        }

        return Result.Success();
    }

    /// <summary>
    /// Completes the visit and finishes its queue token, as closing the visit at the desk does, without saving: an admission
    /// from the OPD closes the visit it came from in its own unit of work. Returns the token, if any, whose change the caller
    /// pushes to the queue screens (<see cref="PublishQueueChangeAsync"/>) once it has saved.
    /// </summary>
    public async Task<Result<QueueToken?>> CompleteWithTokenAsync(Visit visit, DateTimeOffset now, CancellationToken ct)
    {
        var result = visit.Complete(now);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        var token = await db.Set<QueueToken>().FirstOrDefaultAsync(t => t.VisitId == visit.Id, ct);
        token?.Finish(now);
        return Result<QueueToken?>.Ok(token);
    }

    // ------------------------------------------------------------------ queue

    public async Task<Result<QueueTokenDto>> CallAsync(Guid tokenId, string? room, CancellationToken ct)
    {
        var token = await db.Set<QueueToken>().FirstOrDefaultAsync(t => t.Id == tokenId, ct);
        if (token is null)
        {
            return Error.NotFound("Token", tokenId);
        }

        var result = token.Call(room, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(ct);
        await PublishQueueChangeAsync(token, ct, called: true);
        return ToDto(token);
    }

    /// <summary>
    /// Calls the next waiting token (highest priority, then lowest number). Two doctors pressing "Next" at the same
    /// moment are resolved by the token's concurrency version: the loser simply takes the following token.
    /// </summary>
    public async Task<Result<QueueTokenDto?>> CallNextAsync(Guid departmentId, Guid? practitionerId, string? room, CancellationToken ct)
    {
        var today = time.Today();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var next = await db.Set<QueueToken>()
                .Where(t => t.DepartmentId == departmentId && t.TokenDate == today && t.Status == TokenStatus.Waiting
                            && (practitionerId == null || t.PractitionerId == null || t.PractitionerId == practitionerId))
                // Enums are stored as text, so ordering by Priority in SQL would be alphabetical and call emergencies
                // last. Rank explicitly: Emergency, then Priority, then Normal, each in token order.
                .OrderByDescending(t => t.Priority == TokenPriority.Emergency)
                .ThenByDescending(t => t.Priority == TokenPriority.Priority)
                .ThenBy(t => t.Sequence)
                .FirstOrDefaultAsync(ct);
            if (next is null)
            {
                return Result<QueueTokenDto?>.Ok(null);
            }

            next.Call(room, time.GetUtcNow());
            next.PractitionerId ??= practitionerId;
            try
            {
                await db.SaveChangesAsync(ct);
                await PublishQueueChangeAsync(next, ct, called: true);
                return ToDto(next);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
            }
        }

        return Error.Conflict("queue.busy", "The queue is changing quickly. Please try again.");
    }

    public Task<Result<QueueTokenDto>> StartAsync(Guid tokenId, CancellationToken ct) =>
        UpdateTokenAsync(tokenId, async (token, now) =>
        {
            var result = token.Start(now);
            if (result.IsSuccess)
            {
                var visit = await db.Set<Visit>().FirstOrDefaultAsync(v => v.Id == token.VisitId, ct);
                visit?.Start();
            }

            return result;
        }, ct);

    public Task<Result<QueueTokenDto>> FinishAsync(Guid tokenId, CancellationToken ct) =>
        UpdateTokenAsync(tokenId, async (token, now) =>
        {
            var result = token.Finish(now);
            if (result.IsSuccess)
            {
                var visit = await db.Set<Visit>().FirstOrDefaultAsync(v => v.Id == token.VisitId, ct);
                if (visit is { Type: VisitType.OutPatient })
                {
                    visit.Complete(now);
                }
            }

            return result;
        }, ct);

    public Task<Result<QueueTokenDto>> SkipAsync(Guid tokenId, CancellationToken ct) =>
        UpdateTokenAsync(tokenId, (token, _) => Task.FromResult(token.Skip()), ct);

    public Task<Result<QueueTokenDto>> RequeueAsync(Guid tokenId, CancellationToken ct) =>
        UpdateTokenAsync(tokenId, (token, _) => Task.FromResult(token.Requeue()), ct);

    private async Task<Result<QueueTokenDto>> UpdateTokenAsync(Guid tokenId, Func<QueueToken, DateTimeOffset, Task<Result>> change, CancellationToken ct)
    {
        var token = await db.Set<QueueToken>().FirstOrDefaultAsync(t => t.Id == tokenId, ct);
        if (token is null)
        {
            return Error.NotFound("Token", tokenId);
        }

        var result = await change(token, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(ct);
        await PublishQueueChangeAsync(token, ct);
        return ToDto(token);
    }

    /// <summary>Tells the department's queue screens and the facility's visit lists that the token changed.</summary>
    public async Task PublishQueueChangeAsync(QueueToken token, CancellationToken ct, bool called = false)
    {
        var payload = new { token.Id, token.DepartmentId, token.PractitionerId, token.Label, token.Status, token.Room };
        await realtime.PublishAsync(Topics.Queue(token.DepartmentId), called ? "queue.called" : "queue.changed", payload, ct);
        await realtime.ToFacilityAsync(token.FacilityId, "visits.changed", new { token.VisitId }, ct);
    }

    internal static DateTimeOffset StartOfLocalDay(DateTimeOffset utcNow)
    {
        var zone = HospitalTimeZone.Zone;
        var local = TimeZoneInfo.ConvertTime(utcNow, zone);
        return new DateTimeOffset(local.Date, zone.GetUtcOffset(local.Date)).ToUniversalTime();
    }

    internal static QueueTokenDto ToDto(QueueToken t) => new(
        t.Id, t.VisitId, t.PatientId, t.DepartmentId, t.PractitionerId, t.TokenDate, t.Sequence, t.Label, t.Status, t.Priority, t.IssuedAt,
        t.CalledAt, t.ServiceStartedAt, t.CompletedAt, t.Room, t.CallCount);

    internal static VisitDetail ToDetail(Visit v, QueueToken? token) => new(
        v.Id, v.VisitNumber, v.PatientId, v.Type, v.Status, v.DepartmentId, v.PractitionerId, v.AppointmentId, v.VisitedAt, v.CompletedAt,
        v.IsFollowUp, v.IsFreeFollowUp, v.PreviousVisitId, v.PayerType, v.PayerId, v.ReferralSource, v.ReferredBy, v.ChiefComplaint, v.IsMlc,
        v.MlcNumber, v.Notes, v.CancelReason, token is null ? null : ToDto(token), v.Version);
}

/// <summary>What the desk asked: cancel this visit, for this reason.</summary>
public sealed record VisitCancelPayload(Guid VisitId, string Reason);

/// <summary>The cancellation done at once (<paramref name="Asked"/> null), or the approval it waits for.</summary>
public sealed record VisitCancelOutcome(ApprovalRequestDto? Asked);

/// <summary>
/// A visit the doctor has started, cancelled at the desk: the desk asks (<c>visits.manage</c>), and someone else decides:
/// a front-office supervisor, or the treating doctor (<see cref="PatientsPermissions.VisitsCancelApprove"/>).
/// </summary>
internal sealed class VisitCancelApproval(VisitService visits) : IApprovalAction
{
    public string Kind => VisitService.CancelKind;

    public string Title => "Visit cancelled after the doctor started it";

    public string ApprovePermission => PatientsPermissions.VisitsCancelApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => visits.CancelApprovedAsync(request, ct);
}
