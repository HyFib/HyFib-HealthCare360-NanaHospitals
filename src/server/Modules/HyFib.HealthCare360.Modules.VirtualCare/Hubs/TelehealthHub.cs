using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Features.Telehealth;
using HyFib.HealthCare360.Modules.VirtualCare.Services;
using HyFib.HealthCare360.Platform.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.VirtualCare.Hubs;

/// <summary>
/// WebRTC signalling for 1:1 video consultations at <c>/hubs/telehealth</c> (TEL-02/03/05). Three ways in:
/// staff with a JWT and <c>telehealth.consult</c>; portal patients with a JWT whose <c>PortalPatientScope</c> covers
/// the session's patient; and guests holding the time-limited join token from the SMS/WhatsApp link. Patients wait in
/// a virtual waiting room until a clinician admits them; only admitted participants may exchange offers, answers, ICE
/// candidates and chat. The hub itself allows anonymous connections (guests have no JWT) and authorises every call.
/// Media never passes through the server. Presence is kept in the database (with connection ids) so any API node
/// behind the Redis backplane can admit a patient who is connected to another node. While video consultations are
/// switched off for the consultation's facility nobody can join it (every join checks), so nobody can signal, admit or
/// chat in it either; a call already running is not cut off.
/// </summary>
[AllowAnonymous]
public sealed class TelehealthHub(
    HospitalDbContext db,
    IPermissionService permissions,
    VirtualCareSettings settings,
    TelehealthBroadcaster broadcaster,
    IFeatureFlags features,
    IAuditLogger audit,
    TimeProvider time,
    ILogger<TelehealthHub> logger) : Hub
{
    public const string Path = "/hubs/telehealth";

    private const int MaxSdpLength = 30_000;
    private const int MaxCandidateLength = 2_000;
    private const int MaxFailedJoins = 5;
    private const int RecentMessages = 50;
    private const string SeatPrefix = "tele-seat:";
    private const string QualityPrefix = "tele-quality:";
    private const string FailedJoinsKey = "tele-failed-joins";
    private static readonly TimeSpan QualityWriteInterval = TimeSpan.FromSeconds(15);

    private CancellationToken Aborted => Context.ConnectionAborted;

    // ------------------------------------------------------------------ joining

    /// <summary>A doctor opens the consultation: joins the room directly and sees the waiting room.</summary>
    public async Task<TelehealthJoinResult> JoinAsClinician(Guid sessionId)
    {
        var caller = new HubCallerUser(Context.User);
        if (caller.UserId is not { } userId || caller.IsPatient || caller.IsRestricted
            || !await permissions.HasPermissionAsync(userId, VirtualCarePermissions.TelehealthConsult, Aborted))
        {
            throw new HubException("You are not allowed to run video consultations.");
        }

        var session = await FindAsync(sessionId) ?? throw new HubException("Consultation not found.");
        if (caller.FacilityId is { } facilityId && facilityId != session.FacilityId)
        {
            throw new HubException("This consultation belongs to another branch. Switch facility first.");
        }

        if (session.IsClosed)
        {
            throw new HubException("This consultation is closed.");
        }

        await EnsureVideoOnAsync(session, forPatients: false);
        var now = time.GetUtcNow();
        var participant = new TelehealthParticipant
        {
            SessionId = sessionId,
            Role = TelehealthRole.Clinician,
            UserId = userId,
            DisplayName = caller.DisplayName ?? "Doctor",
            ConnectionId = Context.ConnectionId,
            JoinedAt = now,
            AdmittedAt = now,
        };
        db.Add(participant);
        await db.SaveChangesAsync(Aborted);

        await Groups.JoinAsync(Context.ConnectionId, TelehealthGroups.Room(sessionId), Aborted);
        await Groups.JoinAsync(Context.ConnectionId, TelehealthGroups.Clinicians(sessionId), Aborted);
        Remember(sessionId, new Seat(participant.Id, TelehealthRole.Clinician, participant.DisplayName, userId, Admitted: true));
        await Clients.OthersInGroup(TelehealthGroups.Room(sessionId)).SendAsync(
            "participantJoined", new { participantId = participant.Id, role = participant.Role, name = participant.DisplayName }, Aborted);

        await audit.WriteAsync(
            new AuditEntry(AuditCategories.PhiAccess, "patient.accessed", "Joined a telehealth consultation")
            {
                PatientId = session.PatientId,
                EntityType = nameof(TelehealthSession),
                EntityId = sessionId.ToString(),
                UserId = userId,
                UserName = caller.UserName,
            },
            Aborted);

        return await JoinResultAsync(session, participant);
    }

    /// <summary>A signed-in portal patient (or a family member sharing the verified mobile number) enters the waiting room.</summary>
    public async Task<TelehealthJoinResult> JoinAsPatient(Guid sessionId)
    {
        var caller = new HubCallerUser(Context.User);
        if (caller.UserId is not { } userId || !caller.IsPatient || caller.IsRestricted
            || !await permissions.HasPermissionAsync(userId, PatientsPermissions.Portal, Aborted))
        {
            throw new HubException("Please sign in to the patient portal first.");
        }

        // The hub is open to guests, so the portal switch is checked here rather than by the gate on signed-in requests.
        if (!await features.IsEnabledAsync(FeatureKeys.PatientPortal, null, Aborted))
        {
            throw new HubException("The patient portal is not available right now. Please join with the link sent to you by SMS.");
        }

        var session = await FindAsync(sessionId);
        if (session is null || !await new PortalPatientScope(db, caller).CanAccessAsync(session.PatientId, Aborted))
        {
            throw new HubException("This consultation is not linked to your mobile number.");
        }

        var name = await db.Set<Patient>().Where(p => p.Id == session.PatientId).Select(p => p.FullName).FirstOrDefaultAsync(Aborted);
        return await JoinPatientSideAsync(session, TelehealthRole.Patient, userId, name ?? "Patient");
    }

    /// <summary>The patient joins with the token from the SMS/WhatsApp link, without signing in.</summary>
    public async Task<TelehealthJoinResult> JoinAsGuest(Guid sessionId, string token)
    {
        var failed = Context.Items.TryGetValue(FailedJoinsKey, out var count) && count is int n ? n : 0;
        if (failed >= MaxFailedJoins)
        {
            Context.Abort();
            throw new HubException("Too many attempts. Open the link again.");
        }

        var session = await FindAsync(sessionId);
        var verified = session?.VerifyJoinToken(token, time.GetUtcNow());
        if (session is null || verified is null || verified.IsFailure)
        {
            Context.Items[FailedJoinsKey] = failed + 1;
            logger.LogWarning("Rejected a telehealth guest join for session {SessionId}", sessionId);
            throw new HubException(verified?.Error?.Code == "telehealth.link_expired"
                ? "This consultation link has expired."
                : "This consultation link is not valid. Please use the latest link sent to you.");
        }

        // Guests see only the first name: a forwarded link must not reveal the full identity.
        var firstName = await db.Set<Patient>().Where(p => p.Id == session.PatientId).Select(p => p.FirstName).FirstOrDefaultAsync(Aborted);
        return await JoinPatientSideAsync(session, TelehealthRole.Guest, null, firstName ?? "Patient");
    }

    /// <summary>Admits one waiting participant (or everyone waiting) and starts the consultation clock.</summary>
    public async Task Admit(Guid sessionId, Guid? participantId)
    {
        RequireSeat(sessionId, clinicianOnly: true);
        var session = await FindAsync(sessionId) ?? throw new HubException("Consultation not found.");
        if (session.IsClosed)
        {
            throw new HubException("This consultation is closed.");
        }

        var waiting = await db.Set<TelehealthParticipant>()
            .Where(p => p.SessionId == sessionId && p.Role != TelehealthRole.Clinician && p.AdmittedAt == null && p.LeftAt == null
                        && (participantId == null || p.Id == participantId))
            .ToListAsync(Aborted);
        if (waiting.Count == 0)
        {
            throw new HubException("Nobody is waiting to be admitted.");
        }

        var now = time.GetUtcNow();
        foreach (var participant in waiting)
        {
            participant.Admit(now);
        }

        session.Start(now);
        await SaveAsync();

        var room = TelehealthGroups.Room(sessionId);
        foreach (var participant in waiting)
        {
            await Groups.JoinAsync(participant.ConnectionId, room, Aborted);
            await Clients.Client(participant.ConnectionId).SendAsync("admitted", new { sessionId, participantId = participant.Id }, Aborted);
            await Clients.GroupExcept(room, [participant.ConnectionId]).SendAsync(
                "participantJoined", new { participantId = participant.Id, role = participant.Role, name = participant.DisplayName }, Aborted);
        }

        await broadcaster.SessionChangedAsync(session, Aborted);
    }

    /// <summary>
    /// The consultation as joining shows it (its status, the waiting room, recent messages, whether this participant is
    /// admitted), for a screen that may have missed what the hub sent it though its connection stayed open: with several
    /// servers, Redis was away (SCAL-02), and the server sent it <c>resync</c>. It puts the connection back in the groups its
    /// seat belongs in, in case adding it failed meanwhile (every join here is undone when it fails,
    /// <see cref="RealtimeGroups.JoinAsync"/>, so that this one joins), and changes nothing else: it is not a new participant.
    /// </summary>
    public async Task<TelehealthJoinResult> Refresh(Guid sessionId)
    {
        var seat = RequireSeat(sessionId, clinicianOnly: false);
        var session = await FindAsync(sessionId) ?? throw new HubException("Consultation not found.");
        var participant = await db.Set<TelehealthParticipant>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == seat.ParticipantId, Aborted)
            ?? throw new HubException("Join the consultation first.");
        if (participant.AdmittedAt is not null && participant.LeftAt is null)
        {
            await Groups.JoinAsync(Context.ConnectionId, TelehealthGroups.Room(sessionId), Aborted);
            Remember(sessionId, seat with { Admitted = true });
        }

        if (seat.Role == TelehealthRole.Clinician)
        {
            await Groups.JoinAsync(Context.ConnectionId, TelehealthGroups.Clinicians(sessionId), Aborted);
        }

        return await JoinResultAsync(session, participant);
    }

    // ------------------------------------------------------------------ signalling (admitted participants only)

    public Task SendOffer(Guid sessionId, string sdp) => RelayAsync(sessionId, "offer", sdp, MaxSdpLength);

    public Task SendAnswer(Guid sessionId, string sdp) => RelayAsync(sessionId, "answer", sdp, MaxSdpLength);

    public Task SendIceCandidate(Guid sessionId, string candidate) => RelayAsync(sessionId, "iceCandidate", candidate, MaxCandidateLength);

    /// <summary>
    /// Saves a chat message and shows it to the call. Returns it too, so the sender sees it even when it could not be pushed
    /// (with several servers, while Redis is away).
    /// </summary>
    public async Task<TelehealthMessageDto> SendChat(Guid sessionId, string text)
    {
        var seat = await RequireAdmittedAsync(sessionId);
        var created = TelehealthMessage.Create(sessionId, seat.Role, seat.UserId, seat.DisplayName, text, time.GetUtcNow());
        if (created.IsFailure)
        {
            throw new HubException(created.Error!.Message);
        }

        db.Add(created.Value);
        await db.SaveChangesAsync(Aborted);
        var message = TelehealthMessageDto.From(created.Value);
        await broadcaster.ChatAsync(sessionId, message, Aborted);
        return message;
    }

    /// <summary>Audio-only fallback (TEL-02): tells the other side and records it for the session metadata.</summary>
    public async Task ChangeMediaMode(Guid sessionId, bool audioOnly)
    {
        var seat = await RequireAdmittedAsync(sessionId);
        if (audioOnly)
        {
            await RecordQualityAsync(sessionId, seat, new ConnectionQualitySample(null, null, null, AudioOnly: true));
        }

        await Clients.OthersInGroup(TelehealthGroups.Room(sessionId)).SendAsync("mediaModeChanged", new { from = seat.ParticipantId, audioOnly }, Aborted);
    }

    /// <summary>Connection statistics from the client (TEL-05); stored at most every 15 seconds per connection.</summary>
    public async Task ReportQuality(Guid sessionId, ConnectionQualitySample sample)
    {
        var seat = RequireSeat(sessionId, clinicianOnly: false);
        var now = time.GetUtcNow();
        var key = QualityPrefix + sessionId.ToString("N");
        if (!sample.AudioOnly && Context.Items.TryGetValue(key, out var last) && last is DateTimeOffset previous && now - previous < QualityWriteInterval)
        {
            return;
        }

        await RecordQualityAsync(sessionId, seat, sample);
        Context.Items[key] = now;
    }

    public async Task Leave(Guid sessionId)
    {
        if (Context.Items.TryGetValue(SeatKey(sessionId), out var value) && value is Seat seat)
        {
            await LeaveAsync(sessionId, seat, Aborted);
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var seats = Context.Items
            .Where(kv => kv.Key is string key && key.StartsWith(SeatPrefix, StringComparison.Ordinal) && kv.Value is Seat)
            .Select(kv => (SessionId: Guid.Parse(((string)kv.Key)[SeatPrefix.Length..]), Seat: (Seat)kv.Value!))
            .ToList();

        foreach (var (sessionId, seat) in seats)
        {
            try
            {
                await LeaveAsync(sessionId, seat, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not record that a participant left telehealth session {SessionId}", sessionId);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<TelehealthJoinResult> JoinPatientSideAsync(TelehealthSession session, TelehealthRole role, Guid? userId, string displayName)
    {
        await EnsureVideoOnAsync(session, forPatients: true);
        var now = time.GetUtcNow();
        var joinable = session.EnsureJoinable(now, await settings.EarlyJoinAsync(session.FacilityId, Aborted));
        if (joinable.IsFailure)
        {
            throw new HubException(joinable.Error!.Message);
        }

        // A patient reconnecting after a WiFi/mobile-data drop goes straight back into a running call.
        var readmit = session.Status == TelehealthStatus.InProgress
                      && await db.Set<TelehealthParticipant>().AnyAsync(
                          p => p.SessionId == session.Id && p.Role == role && p.AdmittedAt != null && (userId == null || p.UserId == userId), Aborted);

        var participant = new TelehealthParticipant
        {
            SessionId = session.Id,
            Role = role,
            UserId = userId,
            DisplayName = displayName,
            ConnectionId = Context.ConnectionId,
            JoinedAt = now,
            AdmittedAt = readmit ? now : null,
        };
        db.Add(participant);
        if (!readmit)
        {
            session.EnterWaitingRoom(now);
        }

        await SaveAsync();
        Remember(session.Id, new Seat(participant.Id, role, displayName, userId, readmit));

        if (readmit)
        {
            await Groups.JoinAsync(Context.ConnectionId, TelehealthGroups.Room(session.Id), Aborted);
            await Clients.OthersInGroup(TelehealthGroups.Room(session.Id)).SendAsync(
                "participantJoined", new { participantId = participant.Id, role, name = displayName }, Aborted);
        }
        else
        {
            await Clients.Group(TelehealthGroups.Clinicians(session.Id)).SendAsync(
                "waitingRoomChanged", new WaitingParticipant(participant.Id, role, displayName, now), Aborted);
            await broadcaster.SessionChangedAsync(session, Aborted);
        }

        return await JoinResultAsync(session, participant);
    }

    private async Task<TelehealthJoinResult> JoinResultAsync(TelehealthSession session, TelehealthParticipant participant)
    {
        var iceServers = await settings.IceServersAsync(session.FacilityId, Aborted);
        var doctor = await db.Set<Practitioner>().Where(p => p.Id == session.PractitionerId).Select(p => p.DisplayName).FirstOrDefaultAsync(Aborted);
        var patient = participant.Role == TelehealthRole.Guest
            ? participant.DisplayName
            : await db.Set<Patient>().Where(p => p.Id == session.PatientId).Select(p => p.FullName).FirstOrDefaultAsync(Aborted);

        IReadOnlyList<WaitingParticipant> waiting = participant.Role == TelehealthRole.Clinician
            ? await db.Set<TelehealthParticipant>().AsNoTracking()
                .Where(p => p.SessionId == session.Id && p.Role != TelehealthRole.Clinician && p.AdmittedAt == null && p.LeftAt == null)
                .OrderBy(p => p.JoinedAt)
                .Select(p => new WaitingParticipant(p.Id, p.Role, p.DisplayName, p.JoinedAt))
                .ToListAsync(Aborted)
            : [];

        IReadOnlyList<TelehealthMessageDto> messages = participant.AdmittedAt is null
            ? []
            : (await db.Set<TelehealthMessage>().AsNoTracking()
                .Where(m => m.SessionId == session.Id)
                .OrderByDescending(m => m.SentAt)
                .Take(RecentMessages)
                .ToListAsync(Aborted))
                .OrderBy(m => m.SentAt)
                .Select(TelehealthMessageDto.From)
                .ToList();

        return new TelehealthJoinResult(
            session.Id, participant.Id, participant.Role, session.Status, participant.AdmittedAt is not null, session.ScheduledStartAt,
            doctor ?? "Doctor", patient ?? "Patient", iceServers.Select(IceServerDto.From).ToList(), waiting, messages);
    }

    private async Task RelayAsync(Guid sessionId, string method, string payload, int maxLength)
    {
        if (string.IsNullOrEmpty(payload) || payload.Length > maxLength)
        {
            throw new HubException("Invalid signalling message.");
        }

        var seat = await RequireAdmittedAsync(sessionId);
        await Clients.OthersInGroup(TelehealthGroups.Room(sessionId)).SendAsync(method, new { from = seat.ParticipantId, data = payload }, Aborted);
    }

    private async Task RecordQualityAsync(Guid sessionId, Seat seat, ConnectionQualitySample sample)
    {
        var participant = await db.Set<TelehealthParticipant>().FirstOrDefaultAsync(p => p.Id == seat.ParticipantId, Aborted);
        if (participant is null)
        {
            return;
        }

        participant.RecordQuality(sample.RoundTripMs, sample.PacketLossPercent, sample.BitrateKbps, sample.AudioOnly, time.GetUtcNow());
        if (sample.AudioOnly && await FindAsync(sessionId) is { } session)
        {
            session.NoteAudioOnly();
        }

        await SaveAsync();
    }

    private async Task LeaveAsync(Guid sessionId, Seat seat, CancellationToken ct)
    {
        Context.Items.Remove(SeatKey(sessionId));
        var now = time.GetUtcNow();
        var participant = await db.Set<TelehealthParticipant>().FirstOrDefaultAsync(p => p.Id == seat.ParticipantId, ct);
        participant?.Leave(now);

        TelehealthSession? session = null;
        var admitted = seat.Admitted || participant?.AdmittedAt is not null;
        if (seat.Role != TelehealthRole.Clinician && !admitted)
        {
            session = await FindAsync(sessionId, ct);
            var othersWaiting = await db.Set<TelehealthParticipant>().AnyAsync(
                p => p.SessionId == sessionId && p.Id != seat.ParticipantId && p.Role != TelehealthRole.Clinician && p.AdmittedAt == null && p.LeftAt == null, ct);
            if (!othersWaiting)
            {
                session?.LeaveWaitingRoom();
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("Telehealth session {SessionId} changed while a participant was leaving", sessionId);
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, TelehealthGroups.Room(sessionId), ct);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, TelehealthGroups.Clinicians(sessionId), ct);
        var notice = new { participantId = seat.ParticipantId, role = seat.Role };
        if (admitted)
        {
            await Clients.Group(TelehealthGroups.Room(sessionId)).SendAsync("participantLeft", notice, ct);
        }
        else
        {
            await Clients.Group(TelehealthGroups.Clinicians(sessionId)).SendAsync("waitingRoomLeft", notice, ct);
            if (session is not null)
            {
                await broadcaster.SessionChangedAsync(session, ct);
            }
        }
    }

    /// <summary>Nobody joins while video consultations are off for the session's facility (else hospital-wide).</summary>
    private async Task EnsureVideoOnAsync(TelehealthSession session, bool forPatients)
    {
        if (!await features.IsEnabledAsync(FeatureKeys.Telehealth, session.FacilityId, Aborted))
        {
            var feature = FeatureCatalog.Telehealth;
            throw new HubException(forPatients ? feature.OffDetailForPatients : $"{feature.OffTitle}. {feature.OffDetailForStaff}");
        }
    }

    private Task<TelehealthSession?> FindAsync(Guid sessionId) => FindAsync(sessionId, Aborted);

    /// <summary>Hub calls may run without the HTTP facility scope, so the facility is checked explicitly by the callers.</summary>
    private Task<TelehealthSession?> FindAsync(Guid sessionId, CancellationToken ct) =>
        db.Set<TelehealthSession>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(s => s.Id == sessionId, ct);

    private async Task SaveAsync()
    {
        try
        {
            await db.SaveChangesAsync(Aborted);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new HubException("The consultation changed at the same moment. Please try again.");
        }
    }

    private Seat RequireSeat(Guid sessionId, bool clinicianOnly)
    {
        if (!Context.Items.TryGetValue(SeatKey(sessionId), out var value) || value is not Seat seat)
        {
            throw new HubException("Join the consultation first.");
        }

        return clinicianOnly && seat.Role != TelehealthRole.Clinician ? throw new HubException("Only the doctor can do this.") : seat;
    }

    /// <summary>Admission can happen on another connection (even another node), so a not-yet-admitted seat is re-checked in the database.</summary>
    private async Task<Seat> RequireAdmittedAsync(Guid sessionId)
    {
        var seat = RequireSeat(sessionId, clinicianOnly: false);
        if (seat.Admitted)
        {
            return seat;
        }

        var admitted = await db.Set<TelehealthParticipant>().AsNoTracking()
            .AnyAsync(p => p.Id == seat.ParticipantId && p.AdmittedAt != null && p.LeftAt == null, Aborted);
        if (!admitted)
        {
            throw new HubException("Please wait until the doctor admits you.");
        }

        var updated = seat with { Admitted = true };
        Remember(sessionId, updated);
        return updated;
    }

    private void Remember(Guid sessionId, Seat seat) => Context.Items[SeatKey(sessionId)] = seat;

    private static string SeatKey(Guid sessionId) => SeatPrefix + sessionId.ToString("N");

    /// <summary>What this connection is in a session (kept in the connection's items for the life of the connection).</summary>
    private sealed record Seat(Guid ParticipantId, TelehealthRole Role, string DisplayName, Guid? UserId, bool Admitted);
}
