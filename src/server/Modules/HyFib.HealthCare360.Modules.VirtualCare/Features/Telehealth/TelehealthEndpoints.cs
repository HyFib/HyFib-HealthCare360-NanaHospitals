using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Hubs;
using HyFib.HealthCare360.Modules.VirtualCare.Services;

namespace HyFib.HealthCare360.Modules.VirtualCare.Features.Telehealth;

/// <summary>
/// /api/v1/telehealth — the doctor's and front desk's view of video consultations (TEL-01..06). While video consultations
/// are switched off every endpoint answers 503 <c>feature.disabled</c>, except ending and cancelling: a call already in
/// progress can still be closed, and a booked one called off.
/// </summary>
internal static class TelehealthEndpoints
{
    private const int MaxDays = 31;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/telehealth").WithTags("Telehealth");
        group.MapPost("/sessions/{id:guid}/end", End).RequirePermission(VirtualCarePermissions.TelehealthConsult).WithValidation<EndTelehealthSessionRequest>();
        group.MapPost("/sessions/{id:guid}/cancel", Cancel).RequirePermission(VirtualCarePermissions.TelehealthManage).WithValidation<CancelTelehealthSessionRequest>();

        var video = group.MapGroup(string.Empty).RequireFeature(FeatureKeys.Telehealth);
        // Staff only: patients get the ICE servers of their own consultation from the portal's join endpoint.
        video.MapGet("/config", Config).RequirePermission(VirtualCarePermissions.TelehealthRead);
        video.MapGet("/sessions", List).RequirePermission(VirtualCarePermissions.TelehealthRead);
        video.MapGet("/sessions/{id:guid}", Get).RequirePermission(VirtualCarePermissions.TelehealthRead);
        video.MapPost("/sessions", Create).RequirePermission(VirtualCarePermissions.TelehealthManage).WithValidation<CreateTelehealthSessionRequest>();
        video.MapPost("/sessions/{id:guid}/start", Start).RequirePermission(VirtualCarePermissions.TelehealthConsult);
        video.MapPost("/sessions/{id:guid}/resend-link", ResendLink).RequirePermission(VirtualCarePermissions.TelehealthManage);
        video.MapPost("/sessions/{id:guid}/recording-consent", RecordConsent).RequirePermission(VirtualCarePermissions.TelehealthConsult);
        video.MapGet("/sessions/{id:guid}/messages", Messages).RequirePermission(VirtualCarePermissions.TelehealthRead);
        video.MapPost("/sessions/{id:guid}/messages", PostMessage).RequirePermission(VirtualCarePermissions.TelehealthConsult).WithValidation<PostTelehealthMessageRequest>();
    }

    /// <summary>ICE servers (STUN/TURN) and call settings (TEL-06).</summary>
    private static async Task<IResult> Config(VirtualCareSettings settings, ICurrentUser user, CancellationToken ct)
    {
        var iceServers = await settings.IceServersAsync(user.FacilityId, ct);
        var earlyJoin = await settings.EarlyJoinAsync(user.FacilityId, ct);
        return TypedResults.Ok(new TelehealthConfig(iceServers.Select(IceServerDto.From).ToList(), (int)earlyJoin.TotalMinutes, TelehealthHub.Path));
    }

    /// <summary>A doctor's (or the facility's) consultations for a day or range; <c>mine=true</c> lists the signed-in doctor's own.</summary>
    private static async Task<IResult> List(
        DateOnly? date,
        DateOnly? to,
        Guid? practitionerId,
        TelehealthStatus? status,
        bool? mine,
        HospitalDbContext db,
        ICurrentUser user,
        TimeProvider time,
        CancellationToken ct)
    {
        var from = date ?? time.Today();
        var until = to ?? from;
        if (until < from || until.DayNumber - from.DayNumber >= MaxDays)
        {
            return Error.Field("to", $"Choose a range of 1 to {MaxDays} days.").ToProblem();
        }

        var calendar = HospitalCalendar.India;
        var start = calendar.StartOfDay(from);
        var end = calendar.StartOfDay(until.AddDays(1));
        var sessions = db.Set<TelehealthSession>().AsNoTracking().Where(s => s.ScheduledStartAt >= start && s.ScheduledStartAt < end);

        var doctor = practitionerId ?? (mine == true ? user.PractitionerId : null);
        if (doctor is not null)
        {
            sessions = sessions.Where(s => s.PractitionerId == doctor);
        }

        if (status is not null)
        {
            sessions = sessions.Where(s => s.Status == status);
        }

        return TypedResults.Ok(await TelehealthQueries.ListAsync(db, sessions, time.Today(), ct));
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var detail = await TelehealthQueries.DetailAsync(db, id, time.Today(), ct);
        if (detail is null)
        {
            return Error.NotFound(nameof(TelehealthSession), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(detail.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(detail.PatientId, "Viewed a telehealth consultation", ct);
        return TypedResults.Ok(detail);
    }

    private static async Task<IResult> Create(CreateTelehealthSessionRequest request, TelehealthSessionService sessions, CancellationToken ct) =>
        (await sessions.CreateAsync(request, ct)).ToCreated(link => $"/api/v1/telehealth/sessions/{link.SessionId}");

    /// <summary>Starts the consultation (creates the telehealth visit for EMR notes and e-prescriptions).</summary>
    private static async Task<IResult> Start(Guid id, TelehealthSessionService sessions, HospitalDbContext db, TimeProvider time, CancellationToken ct) =>
        await DetailResultAsync(await sessions.StartAsync(id, ct), db, time, ct);

    private static async Task<IResult> End(Guid id, EndTelehealthSessionRequest request, TelehealthSessionService sessions, HospitalDbContext db, TimeProvider time, CancellationToken ct) =>
        await DetailResultAsync(await sessions.EndAsync(id, request.QualityNotes, ct), db, time, ct);

    private static async Task<IResult> Cancel(Guid id, CancelTelehealthSessionRequest request, TelehealthSessionService sessions, HospitalDbContext db, TimeProvider time, CancellationToken ct) =>
        await DetailResultAsync(await sessions.CancelAsync(id, request.Reason, ct), db, time, ct);

    private static async Task<IResult> ResendLink(Guid id, TelehealthSessionService sessions, CancellationToken ct) =>
        (await sessions.ResendLinkAsync(id, ct)).ToOk();

    private static async Task<IResult> RecordConsent(Guid id, RecordingConsentRequest request, TelehealthSessionService sessions, CancellationToken ct) =>
        (await sessions.RecordConsentAsync(id, request.Granted, ct)).ToNoContent();

    private static async Task<IResult> Messages(Guid id, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var patientId = await db.Set<TelehealthSession>().Where(s => s.Id == id).Select(s => (Guid?)s.PatientId).FirstOrDefaultAsync(ct);
        if (patientId is null)
        {
            return Error.NotFound(nameof(TelehealthSession), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(patientId.Value, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(patientId.Value, "Viewed telehealth chat", ct);
        return TypedResults.Ok(await TelehealthQueries.MessagesAsync(db, id, ct));
    }

    private static async Task<IResult> PostMessage(Guid id, PostTelehealthMessageRequest request, TelehealthSessionService sessions, CancellationToken ct) =>
        (await sessions.PostMessageAsync(id, request.Text, ct)).ToCreated(m => $"/api/v1/telehealth/sessions/{id}/messages");

    private static async Task<IResult> DetailResultAsync(Result<TelehealthSession> result, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        var detail = await TelehealthQueries.DetailAsync(db, result.Value.Id, time.Today(), ct);
        return detail is null ? Error.NotFound(nameof(TelehealthSession), result.Value.Id).ToProblem() : TypedResults.Ok(detail);
    }
}
