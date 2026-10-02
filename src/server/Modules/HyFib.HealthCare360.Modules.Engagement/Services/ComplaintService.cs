using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Features.Complaints;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.Engagement.Services;

/// <summary>
/// Complaint handling (FB-03): numbering, routing to the quality/CRM team, the resolution workflow with SLA and
/// turnaround time. Notifications carry the number and area only — never the patient's comment.
/// </summary>
public sealed class ComplaintService(
    HospitalDbContext db,
    INumberSeries numbers,
    INotificationService notifications,
    IRealtimePublisher realtime,
    TimeProvider time)
{
    public const string ComplaintSeries = "feedback.complaint";
    public const string ComplaintFormat = "{FAC}CMP{YY}{SEQ:5}";

    /// <summary>Adds a numbered complaint to the current unit of work (the caller owns the transaction and save).</summary>
    public async Task<Complaint> AddAsync(Complaint complaint, CancellationToken ct)
    {
        complaint.ComplaintNumber = await numbers.NextAsync(ComplaintSeries, complaint.FacilityId, ComplaintFormat, NumberResetPolicy.Yearly, ct);
        complaint.Status = ComplaintStatus.Open;
        complaint.Register(time.GetUtcNow());
        db.Add(complaint);
        return complaint;
    }

    public async Task<Result<Guid>> CreateAsync(Guid facilityId, CreateComplaintRequest request, CancellationToken ct)
    {
        if (request.PatientId is { } patientId && !await db.Set<Patient>().AnyAsync(p => p.Id == patientId, ct))
        {
            return Error.Field("patientId", "Choose a registered patient.");
        }

        var problem = await ValidateOwnerAsync(request.OwnerUserId, ct) ?? await ValidateDepartmentAsync(request.DepartmentId, ct);
        if (problem is not null)
        {
            return problem;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var complaint = await AddAsync(new Complaint
        {
            FacilityId = facilityId,
            Source = request.Source,
            PatientId = request.PatientId,
            DepartmentId = request.DepartmentId,
            Category = Clean(request.Category),
            Description = request.Description.Trim(),
            ContactName = Clean(request.ContactName),
            ContactPhone = Phone.Normalize(request.ContactPhone),
            Priority = request.Priority,
            OwnerUserId = request.OwnerUserId,
        }, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        await NotifyNewAsync(complaint, ct);
        return complaint.Id;
    }

    public async Task<Result> UpdateAsync(Guid id, UpdateComplaintRequest request, CancellationToken ct)
    {
        var complaint = await db.Set<Complaint>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (complaint is null)
        {
            return Error.NotFound(nameof(Complaint), id);
        }

        var problem = await ValidateOwnerAsync(request.OwnerUserId, ct) ?? await ValidateDepartmentAsync(request.DepartmentId, ct);
        if (problem is not null)
        {
            return problem;
        }

        db.SetExpectedVersion(complaint, request.Version);
        var newOwner = request.OwnerUserId is { } owner && owner != complaint.OwnerUserId ? owner : (Guid?)null;
        complaint.OwnerUserId = request.OwnerUserId;
        complaint.ChangePriority(request.Priority);
        complaint.DepartmentId = request.DepartmentId;
        complaint.Category = Clean(request.Category);
        await db.SaveChangesAsync(ct);

        if (newOwner is { } assignee)
        {
            await notifications.NotifyUsersAsync([assignee], $"Complaint {complaint.ComplaintNumber} assigned to you",
                $"Resolve by {complaint.DueAt.ToOffset(TimeSpan.FromMinutes(330)):dd MMM HH:mm}.", $"/feedback/complaints/{complaint.Id}", "feedback",
                complaint.Priority == ComplaintPriority.High ? "warning" : "info", ct);
        }

        return Result.Success();
    }

    public async Task<Result> ChangeStatusAsync(Guid id, ChangeComplaintStatusRequest request, CancellationToken ct)
    {
        var complaint = await db.Set<Complaint>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (complaint is null)
        {
            return Error.NotFound(nameof(Complaint), id);
        }

        var result = complaint.MoveTo(request.Status, request.Note, time.GetUtcNow());
        if (result.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return result;
    }

    /// <summary>Alerts complaint handlers (quality managers and CRM executives) and refreshes open screens.</summary>
    public async Task NotifyNewAsync(Complaint complaint, CancellationToken ct)
    {
        await notifications.NotifyPermissionHoldersAsync(EngagementPermissions.ComplaintsManage, complaint.FacilityId,
            $"New {(complaint.Priority == ComplaintPriority.High ? "high-priority " : string.Empty)}complaint {complaint.ComplaintNumber}",
            $"{complaint.Source} complaint about {complaint.Category ?? "the hospital"}.", $"/feedback/complaints/{complaint.Id}", "feedback",
            complaint.Priority == ComplaintPriority.High ? "warning" : "info", ct);
        await realtime.ToFacilityAsync(complaint.FacilityId, "feedback.complaint.created", new { complaint.Id }, ct);
    }

    /// <summary>
    /// A complaint past its deadline (FB-03) is escalated to the holders of <see cref="EngagementPermissions.ComplaintEscalations"/>
    /// (quality managers and hospital administrators by default), and its owner hears that it was; open screens refresh.
    /// </summary>
    public async Task NotifyEscalatedAsync(Complaint complaint, CancellationToken ct)
    {
        var due = EngagementClock.ToLocal(complaint.DueAt).ToString("dd MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var about = $"{complaint.Source} complaint about {complaint.Category ?? "the hospital"}, due {due}, is unresolved.";
        var link = $"/feedback/complaints/{complaint.Id}";
        await notifications.NotifyPermissionHoldersAsync(EngagementPermissions.ComplaintEscalations, complaint.FacilityId,
            $"Complaint {complaint.ComplaintNumber} escalated: past its deadline", about, link, "feedback", "warning", ct);
        if (complaint.OwnerUserId is { } owner)
        {
            await notifications.NotifyUsersAsync([owner], $"Complaint {complaint.ComplaintNumber} is past its deadline",
                $"{about} It has been escalated.", link, "feedback", "warning", ct);
        }

        await realtime.ToFacilityAsync(complaint.FacilityId, "feedback.complaint.escalated", new { complaint.Id }, ct);
    }

    private async Task<Error?> ValidateDepartmentAsync(Guid? departmentId, CancellationToken ct) =>
        departmentId is { } id && !await db.Set<Department>().AnyAsync(d => d.Id == id && d.IsActive, ct)
            ? Error.Field("departmentId", "Choose an active department of this facility.")
            : null;

    private async Task<Error?> ValidateOwnerAsync(Guid? ownerUserId, CancellationToken ct) =>
        ownerUserId is { } owner && !await db.Set<User>().AnyAsync(u => u.Id == owner && u.IsActive && u.Type == UserType.Staff, ct)
            ? Error.Field("ownerUserId", "Choose an active staff member.")
            : null;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
