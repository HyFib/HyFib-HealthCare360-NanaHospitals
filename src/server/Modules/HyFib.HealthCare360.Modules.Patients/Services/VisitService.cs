using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
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
    TimeProvider time)
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

        var now = time.GetUtcNow();
        var today = time.Today();
        var dayStart = StartOfLocalDay(now);

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

        var visit = new Visit
        {
            FacilityId = facilityId,
            VisitNumber = await numbers.NextAsync(PatientsNumberSeries.Visit, facilityId, ct),
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
            PayerId = request.PayerId,
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
                    new NotificationRecipient(patient.Phone, patient.Email, patient.Id, patient.FullName),
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

    public async Task<Result> CancelAsync(Guid visitId, string reason, CancellationToken ct)
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

        var result = visit.Cancel(reason.Trim(), time.GetUtcNow());
        if (result.IsFailure)
        {
            return result;
        }

        var token = await db.Set<QueueToken>().FirstOrDefaultAsync(t => t.VisitId == visitId, ct);
        if (token is not null && token.Status is not (TokenStatus.Completed or TokenStatus.Cancelled))
        {
            token.Status = TokenStatus.Cancelled;
        }

        // In a transaction of its own: the modules that withdraw the visit's work hold its lock from their reads to this
        // commit, so an order being placed on the visit is either seen by them or sees the visit cancelled.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (token is not null)
        {
            await PublishQueueChangeAsync(token, ct);
        }

        return Result.Success();
    }

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
