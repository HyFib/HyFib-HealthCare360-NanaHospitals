using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Features.FollowUps;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Engagement.Services;

/// <summary>Follow-up call tasks (CRM-02): creation, assignment, call attempts with retry, cancellation and SMS reminders.</summary>
public sealed class FollowUpService(
    HospitalDbContext db,
    ICurrentUser currentUser,
    CommunicationConsentService consents,
    PublicLinks links,
    INotificationService notifications,
    TimeProvider time)
{
    public const string ReminderTemplate = "followup.reminder";

    public async Task<Result<Guid>> CreateAsync(CreateFollowUpRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        if (!await db.Set<Patient>().AnyAsync(p => p.Id == request.PatientId && p.IsActive, ct))
        {
            return Error.Field("patientId", "Choose an active registered patient.");
        }

        if (request.VisitId is { } visitId && !await db.Set<Visit>().AnyAsync(v => v.Id == visitId && v.PatientId == request.PatientId, ct))
        {
            return Error.Field("visitId", "The visit does not belong to this patient.");
        }

        if (request.DueDate < time.Today())
        {
            return Error.Field("dueDate", "The due date cannot be in the past.");
        }

        var problem = await ValidateAssigneeAsync(request.AssigneeUserId, ct);
        if (problem is not null)
        {
            return problem;
        }

        var task = new FollowUpTask
        {
            FacilityId = facilityId,
            PatientId = request.PatientId,
            VisitId = request.VisitId,
            Type = request.Type,
            DueDate = request.DueDate,
            AssigneeUserId = request.AssigneeUserId,
            Priority = request.Priority,
            Notes = Clean(request.Notes),
            Status = FollowUpStatus.Open,
            Source = FollowUpSource.Manual,
        };
        db.Add(task);
        await db.SaveChangesAsync(ct);
        return task.Id;
    }

    public async Task<Result> UpdateAsync(Guid id, UpdateFollowUpRequest request, CancellationToken ct)
    {
        var task = await db.Set<FollowUpTask>().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is null)
        {
            return Error.NotFound(nameof(FollowUpTask), id);
        }

        if (!task.IsOpen)
        {
            return Error.Rule("followup.closed", "This follow-up is already closed.");
        }

        var problem = await ValidateAssigneeAsync(request.AssigneeUserId, ct);
        if (problem is not null)
        {
            return problem;
        }

        db.SetExpectedVersion(task, request.Version);
        task.DueDate = request.DueDate;
        task.AssigneeUserId = request.AssigneeUserId;
        task.Priority = request.Priority;
        task.Notes = Clean(request.Notes);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public Task<Result> RecordAttemptAsync(Guid id, FollowUpAttemptRequest request, CancellationToken ct) =>
        ChangeAsync(id, task => task.RecordAttempt(request.Outcome, request.Notes, request.NextDueDate, time.Today(), time.GetUtcNow()), ct);

    public Task<Result> CancelAsync(Guid id, string reason, CancellationToken ct) => ChangeAsync(id, task => task.Cancel(reason), ct);

    /// <summary>Sends the "follow-up due" SMS, only to patients who accepted transactional updates.</summary>
    public async Task<Result> SendReminderAsync(Guid id, CancellationToken ct)
    {
        var task = await db.Set<FollowUpTask>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is null)
        {
            return Error.NotFound(nameof(FollowUpTask), id);
        }

        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == task.PatientId)
            .Select(p => new { p.Id, p.FullName, p.Phone, p.PreferredLanguage }).FirstOrDefaultAsync(ct);
        if (patient?.Phone is null)
        {
            return Error.Rule("followup.no_phone", "The patient has no mobile number on record.");
        }

        if (!await consents.HasTransactionalConsentAsync(patient.Id, ct))
        {
            return Error.Rule("followup.no_consent", "The patient has not agreed to SMS updates.");
        }

        var doctor = task.VisitId is { } visitId
            ? await (from v in db.Set<Visit>() join pr in db.Set<Practitioner>() on v.PractitionerId equals pr.Id where v.Id == visitId select pr.DisplayName).FirstOrDefaultAsync(ct)
            : null;
        var baseUrl = await links.BaseUrlAsync(task.FacilityId, ct);
        notifications.Queue(ReminderTemplate,
            new NotificationRecipient(patient.Phone, null, patient.Id, patient.FullName, CampaignService.LanguageCode(patient.PreferredLanguage)),
            new Dictionary<string, string>
            {
                ["doctor"] = doctor ?? "your doctor",
                ["date"] = task.DueDate.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture),
                ["link"] = baseUrl is null ? "the hospital front desk" : PublicLinks.PortalHome(baseUrl),
            },
            [NotificationChannel.Sms],
            TimeSpan.FromDays(1),
            task.FacilityId);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    private async Task<Result> ChangeAsync(Guid id, Func<FollowUpTask, Result> change, CancellationToken ct)
    {
        var task = await db.Set<FollowUpTask>().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is null)
        {
            return Error.NotFound(nameof(FollowUpTask), id);
        }

        var result = change(task);
        if (result.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return result;
    }

    private async Task<Error?> ValidateAssigneeAsync(Guid? assigneeUserId, CancellationToken ct) =>
        assigneeUserId is { } assignee && !await db.Set<User>().AnyAsync(u => u.Id == assignee && u.IsActive && u.Type == UserType.Staff, ct)
            ? Error.Field("assigneeUserId", "Choose an active staff member.")
            : null;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
