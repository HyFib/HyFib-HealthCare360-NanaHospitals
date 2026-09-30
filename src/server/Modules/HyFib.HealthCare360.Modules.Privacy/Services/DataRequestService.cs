using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Features.Requests;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.Privacy.Services;

/// <summary>What a new request needs, however it arrived (portal, desk, e-mail, letter or phone).</summary>
public sealed record NewDataRequest(
    Guid FacilityId,
    DataRequestType Type,
    DataRequestChannel Channel,
    Guid? PatientId,
    string? PrincipalName,
    string? ContactPhone,
    string? ContactEmail,
    RequesterRelation RequestedBy,
    string? RequesterName,
    string? Details,
    IReadOnlyList<CorrectionInput>? Corrections,
    IdentityCheck? IdentityCheck,
    string? IdentityNote);

/// <summary>
/// Data-principal requests (DPDP Act 2023 s11 to s13): numbering, the response deadline, the workflow, and the messages
/// that tell the principal the request arrived and how it was answered. Every message names whom to contact (DPDP
/// Rules 2025, rule 9). Staff are alerted to new requests in the app; messages to them carry the number, never details.
/// </summary>
public sealed class DataRequestService(
    HospitalDbContext db,
    INumberSeries numbers,
    INotificationService notifications,
    IRealtimePublisher realtime,
    PrivacySettings privacy,
    PersonalDataInventory inventory,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public const string Series = "privacy.request";
    public const string Format = "{FAC}DPR{YY}{SEQ:5}";
    public const string ReceivedTemplate = "privacy.request.received";
    public const string ClosedTemplate = "privacy.request.closed";

    /// <summary>Open requests one patient may have at a time from the portal, so the queue cannot be flooded.</summary>
    public const int MaxOpenPortalRequests = 5;

    internal static readonly JsonSerializerOptions SnapshotJson = CreateSnapshotOptions();

    public async Task<Result<DataRequest>> CreateAsync(NewDataRequest input, CancellationToken ct)
    {
        Patient? patient = null;
        if (input.PatientId is { } patientId)
        {
            patient = await db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == patientId, ct);
            if (patient is null)
            {
                return Error.Field("patientId", "Choose a registered patient.");
            }

            if (patient.MergedIntoPatientId is not null)
            {
                return Error.Field("patientId", "This record was merged into another one. Choose the patient's current record.");
            }

            if (input.Channel == DataRequestChannel.Portal
                && await db.Set<DataRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                    .CountAsync(r => r.PatientId == patientId && (r.Status == DataRequestStatus.Submitted || r.Status == DataRequestStatus.InProgress), ct)
                >= MaxOpenPortalRequests)
            {
                return Error.Rule("privacy.too_many_open",
                    string.Create(CultureInfo.InvariantCulture, $"There are already {MaxOpenPortalRequests} open requests for this patient. Please wait for our replies."));
            }
        }

        var request = new DataRequest
        {
            FacilityId = input.FacilityId,
            Type = input.Type,
            Channel = input.Channel,
            PatientId = patient?.Id,
            PrincipalName = patient?.FullName ?? input.PrincipalName!.Trim(),
            ContactPhone = Phone.Normalize(input.ContactPhone) ?? patient?.Phone,
            ContactEmail = Clean(input.ContactEmail) ?? patient?.Email,
            RequestedBy = input.RequestedBy,
            RequesterName = input.RequestedBy == RequesterRelation.Self ? null : Clean(input.RequesterName),
            RequestedByUserId = currentUser.UserId,
            Details = Clean(input.Details) ?? string.Empty,
            Corrections = [.. (input.Corrections ?? []).Select(c => new CorrectionItem
            {
                Field = c.Field,
                CurrentValue = patient is null ? null : CorrectableFields.CurrentValue(patient, c.Field),
                RequestedValue = c.RequestedValue.Trim(),
            })],
        };

        var now = time.GetUtcNow();
        var responseDays = await privacy.ResponseDaysAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        request.RequestNumber = await numbers.NextAsync(Series, input.FacilityId, Format, NumberResetPolicy.Yearly, ct);
        request.Receive(now, responseDays, currentUser.UserId, Via(input.Channel));
        if (input.IdentityCheck is { } check)
        {
            request.VerifyIdentity(check, input.IdentityNote, currentUser.UserId, now);
        }

        db.Add(request);
        await QueueMessageAsync(request, ReceivedTemplate, new Dictionary<string, string>
        {
            ["reference"] = request.RequestNumber,
            ["type"] = TypeWords(request.Type),
            ["received"] = Date(request.ReceivedAt),
            ["due"] = Date(request.DueAt),
        }, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        await notifications.NotifyPermissionHoldersAsync(PrivacyPermissions.RequestsManage, request.FacilityId,
            $"New privacy request {request.RequestNumber}",
            $"{TypeWords(request.Type)} request, received {Via(request.Channel)}. Reply by {Date(request.DueAt)}.",
            $"/privacy/requests/{request.Id}", "privacy", request.Type == DataRequestType.Erasure ? "warning" : "info", ct);
        await realtime.ToFacilityAsync(request.FacilityId, "privacy.request.created", new { request.Id }, ct);
        return request;
    }

    public Task<Result> StartAsync(Guid id, CancellationToken ct) =>
        ChangeAsync(id, r => r.Start(currentUser.UserId, time.GetUtcNow()), ct);

    public Task<Result> VerifyIdentityAsync(Guid id, VerifyIdentityRequest request, CancellationToken ct) =>
        ChangeAsync(id, r => r.VerifyIdentity(request.Check, request.Note, currentUser.UserId, time.GetUtcNow()), ct);

    public async Task<Result> AssignAsync(Guid id, Guid? userId, CancellationToken ct)
    {
        string? name = null;
        if (userId is { } assignee)
        {
            name = await db.Set<User>().Where(u => u.Id == assignee && u.IsActive && u.Type == UserType.Staff).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
            if (name is null)
            {
                return Error.Field("userId", "Choose an active member of staff.");
            }
        }

        return await ChangeAsync(id, r => r.AssignTo(userId, name, currentUser.UserId, time.GetUtcNow()), ct);
    }

    public Task<Result> AddNoteAsync(Guid id, string text, CancellationToken ct) =>
        ChangeAsync(id, r => r.AddNote(text, currentUser.UserId, time.GetUtcNow()), ct);

    public Task<Result> DecideCorrectionsAsync(Guid id, DecideCorrectionsRequest request, CancellationToken ct) =>
        ChangeAsync(id, r => r.DecideCorrections([.. request.Items.Select(i => (i.Index, i.Decision, i.Note))], currentUser.UserId, time.GetUtcNow()), ct);

    /// <summary>
    /// Answers the request and tells the principal. Completing an access request keeps the data summary exactly as it
    /// was given, as the record of what the principal received.
    /// </summary>
    public async Task<Result> CloseAsync(Guid id, CloseDataRequestRequest input, CancellationToken ct)
    {
        var request = await db.Set<DataRequest>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
        {
            return Error.NotFound(nameof(DataRequest), id);
        }

        string? snapshot = null;
        if (request.Type == DataRequestType.Access && input.Outcome != DataRequestStatus.Declined)
        {
            snapshot = request.PatientId is { } patientId
                ? await inventory.SummariseAsync(patientId, ct) is { } summary ? JsonSerializer.Serialize(summary, SnapshotJson) : null
                : JsonSerializer.Serialize(new { note = "No patient record in HC360 is linked to this request." }, SnapshotJson);
        }

        var closed = request.Close(input.Outcome, input.Response, snapshot, currentUser.UserId, time.GetUtcNow());
        if (closed.IsFailure)
        {
            return closed;
        }

        await QueueMessageAsync(request, ClosedTemplate, new Dictionary<string, string>
        {
            ["reference"] = request.RequestNumber,
            ["outcome"] = OutcomeWords(request.Status),
            ["response"] = request.Response!,
        }, ct);
        await db.SaveChangesAsync(ct);
        await realtime.ToFacilityAsync(request.FacilityId, "privacy.request.updated", new { request.Id }, ct);
        return Result.Success();
    }

    /// <summary>The principal withdraws their own request from the portal.</summary>
    public Task<Result> WithdrawAsync(Guid id, CancellationToken ct) =>
        ChangeAsync(id, r => r.Withdraw(currentUser.UserId, time.GetUtcNow()), ct);

    public async Task<PagedResult<DataRequestListItem>> ListAsync(DataRequestFilter filter, PageRequest page, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var requests = db.Set<DataRequest>().AsNoTracking();
        if (filter.Status is { } status)
        {
            requests = requests.Where(r => r.Status == status);
        }
        else if (filter.OpenOnly || filter.OverdueOnly)
        {
            requests = requests.Where(r => r.Status == DataRequestStatus.Submitted || r.Status == DataRequestStatus.InProgress);
        }

        if (filter.OverdueOnly)
        {
            requests = requests.Where(r => r.DueAt < now);
        }

        if (filter.Type is { } type)
        {
            requests = requests.Where(r => r.Type == type);
        }

        if (filter.PatientId is { } patientId)
        {
            requests = requests.Where(r => r.PatientId == patientId);
        }

        if ((filter.Mine ? currentUser.UserId : filter.AssignedToUserId) is { } assignee)
        {
            requests = requests.Where(r => r.AssignedToUserId == assignee);
        }

        if (page.SearchTerm is { } term)
        {
            var number = term.ToUpperInvariant();
            requests = requests.Where(r => r.RequestNumber == number || r.RequestNumber.EndsWith(number));
        }

        // Open first, then the nearest deadline; closed ones newest first.
        var rows = await (
                from r in requests
                join p in db.Set<Patient>() on r.PatientId equals p.Id into patients
                from p in patients.DefaultIfEmpty()
                join u in db.Set<User>().IgnoreQueryFilters() on r.AssignedToUserId equals u.Id into users
                from u in users.DefaultIfEmpty()
                orderby r.Status == DataRequestStatus.Submitted || r.Status == DataRequestStatus.InProgress descending, r.DueAt
                select new { Request = r, Uhid = p != null ? p.Uhid : null, Assignee = u != null ? u.DisplayName : null })
            .ToPagedResultAsync(page, ct);

        return rows.Map(x => new DataRequestListItem(
            x.Request.Id, x.Request.RequestNumber, x.Request.Type, x.Request.Status, x.Request.Channel, x.Request.PatientId, x.Uhid,
            x.Request.PrincipalName, x.Request.RequestedBy, x.Request.ReceivedAt, x.Request.DueAt, x.Request.IsOverdue(now),
            x.Request.IsIdentityVerified, x.Request.AssignedToUserId, x.Assignee));
    }

    public async Task<DataRequestDetail?> DetailAsync(Guid id, CancellationToken ct)
    {
        var request = await db.Set<DataRequest>().AsNoTracking().Include(r => r.Events).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
        {
            return null;
        }

        var userIds = request.Events.Select(e => e.UserId)
            .Append(request.AssignedToUserId).Append(request.IdentityVerifiedByUserId).Append(request.ClosedByUserId)
            .OfType<Guid>().Distinct().ToList();
        var names = await db.Set<User>().IgnoreQueryFilters().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Type == UserType.Patient ? "Patient portal" : u.DisplayName, ct);
        var uhid = request.PatientId is { } patientId
            ? await db.Set<Patient>().Where(p => p.Id == patientId).Select(p => p.Uhid).FirstOrDefaultAsync(ct)
            : null;

        return new DataRequestDetail(
            request.Id, request.RequestNumber, request.Type, request.Status, request.Channel, request.PatientId, uhid, request.PrincipalName,
            request.ContactPhone, request.ContactEmail, request.RequestedBy, request.RequesterName, request.Details,
            [.. request.Corrections.Select((c, i) => new CorrectionItemDto(i, c.Field, CorrectableFields.Label(c.Field), c.CurrentValue, c.RequestedValue, c.Decision, c.Note))],
            request.ReceivedAt, request.DueAt, request.IsOverdue(time.GetUtcNow()), request.IdentityCheck, request.IdentityNote, request.IdentityVerifiedAt,
            Name(names, request.IdentityVerifiedByUserId), request.AssignedToUserId, Name(names, request.AssignedToUserId), request.Response, request.ClosedAt,
            Name(names, request.ClosedByUserId), request.ErasedItems, request.RetainedUntil, request.RetainedPermanently, request.SummarySnapshot is not null,
            [.. request.Events.OrderBy(e => e.At).Select(e => new DataRequestEventDto(e.At, e.Kind, e.Text, Name(names, e.UserId), e.IsPublic))],
            request.Version);
    }

    public async Task<PrivacyOverview> OverviewAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var week = now.AddDays(7);
        var open = db.Set<DataRequest>().AsNoTracking().Where(r => r.Status == DataRequestStatus.Submitted || r.Status == DataRequestStatus.InProgress);
        var breaches = db.Set<Breach>().AsNoTracking().Where(b => b.Status == BreachStatus.Open || b.Status == BreachStatus.BoardInformed);
        return new PrivacyOverview(
            await open.CountAsync(ct),
            await open.CountAsync(r => r.DueAt < now, ct),
            await open.CountAsync(r => r.DueAt >= now && r.DueAt < week, ct),
            await open.CountAsync(r => r.Type != DataRequestType.Grievance && r.IdentityVerifiedAt == null, ct),
            await breaches.CountAsync(ct),
            await breaches.CountAsync(b => (b.ExtendedUntil ?? b.AwareAt + Breach.ReportWindow) < now, ct));
    }

    private async Task<Result> ChangeAsync(Guid id, Func<DataRequest, Result> change, CancellationToken ct)
    {
        var request = await db.Set<DataRequest>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
        {
            return Error.NotFound(nameof(DataRequest), id);
        }

        var result = change(request);
        if (result.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
            await realtime.ToFacilityAsync(request.FacilityId, "privacy.request.updated", new { request.Id }, ct);
        }

        return result;
    }

    /// <summary>Queues a message to the principal by SMS and e-mail, where the request has them; it goes when the caller saves.</summary>
    private async Task QueueMessageAsync(DataRequest request, string template, Dictionary<string, string> values, CancellationToken ct)
    {
        var channels = new List<NotificationChannel>();
        if (request.ContactPhone is not null)
        {
            channels.Add(NotificationChannel.Sms);
        }

        if (request.ContactEmail is not null)
        {
            channels.Add(NotificationChannel.Email);
        }

        if (channels.Count == 0)
        {
            return;
        }

        values["contact"] = PrivacySettings.ContactLine(await privacy.PublishedContactAsync(ct));
        notifications.Queue(template, new NotificationRecipient(request.ContactPhone, request.ContactEmail, request.PatientId, request.PrincipalName),
            values, channels, facilityId: request.FacilityId);
    }

    internal static string Via(DataRequestChannel channel) => channel switch
    {
        DataRequestChannel.Portal => "through the patient portal",
        DataRequestChannel.FrontDesk => "at the front desk",
        DataRequestChannel.Email => "by e-mail",
        DataRequestChannel.Letter => "by letter",
        _ => "by phone",
    };

    internal static string TypeWords(DataRequestType type) => type switch
    {
        DataRequestType.Access => "Access",
        DataRequestType.Correction => "Correction",
        DataRequestType.Erasure => "Erasure",
        _ => "Grievance",
    };

    internal static string OutcomeWords(DataRequestStatus status) => status switch
    {
        DataRequestStatus.Completed => "completed",
        DataRequestStatus.PartlyCompleted => "partly completed",
        DataRequestStatus.Declined => "declined",
        _ => status.ToString().ToLowerInvariant(),
    };

    internal static string Date(DateTimeOffset instant) =>
        instant.ToOffset(TimeSpan.FromMinutes(330)).ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    private static string? Name(IReadOnlyDictionary<Guid, string> names, Guid? id) => id is { } key ? names.GetValueOrDefault(key) : null;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static JsonSerializerOptions CreateSnapshotOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>Filters for the staff queue.</summary>
public sealed record DataRequestFilter(
    DataRequestStatus? Status,
    DataRequestType? Type,
    Guid? PatientId,
    Guid? AssignedToUserId,
    bool Mine,
    bool OpenOnly,
    bool OverdueOnly);
