using System.Security.Claims;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Features.Telehealth;
using HyFib.HealthCare360.Platform.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace HyFib.HealthCare360.Modules.VirtualCare.Hubs;

/// <summary>Group names of the telehealth hub (its own namespace, separate from the /hubs/events topics).</summary>
public static class TelehealthGroups
{
    /// <summary>Admitted participants; signalling and chat are relayed only inside this group.</summary>
    public static string Room(Guid sessionId) => $"tele-room:{sessionId:N}";

    /// <summary>Clinicians of the session; they see who is in the waiting room.</summary>
    public static string Clinicians(Guid sessionId) => $"tele-staff:{sessionId:N}";
}

/// <summary>
/// <see cref="ICurrentUser"/> over the hub caller's claims. Hub invocations do not have a dependable HttpContext on
/// every transport, so the hub evaluates <c>PortalPatientScope</c> with the connection's own principal instead.
/// </summary>
internal sealed class HubCallerUser(ClaimsPrincipal? principal) : ICurrentUser
{
    public bool IsAuthenticated => principal?.Identity?.IsAuthenticated == true;

    public Guid? UserId => Read(HcClaims.UserId);

    public string? UserName => principal?.FindFirst(HcClaims.UserName)?.Value;

    public string? DisplayName => principal?.FindFirst(HcClaims.DisplayName)?.Value;

    public Guid? FacilityId => Read(HcClaims.Facility);

    public Guid? SessionId => Read(HcClaims.Session);

    public Guid? DeviceId => Read(HcClaims.Device);

    public Guid? PractitionerId => Read(HcClaims.Practitioner);

    public Guid? PatientId => Read(HcClaims.Patient);

    public bool IsPatient => string.Equals(principal?.FindFirst(HcClaims.UserType)?.Value, HcClaims.PatientType, StringComparison.Ordinal);

    public IReadOnlyCollection<string> Roles => principal?.FindAll(HcClaims.Role).Select(c => c.Value).ToArray() ?? [];

    public string? IpAddress => null;

    public string? CorrelationId => null;

    private Guid? Read(string claim) =>
        IsAuthenticated && Guid.TryParse(principal!.FindFirst(claim)?.Value, out var value) ? value : null;
}

/// <summary>
/// Pushes session changes to everyone watching: call screens (the room group, whose members include every clinician
/// in the session, plus waiting connections) and staff worklists (the facility topic on /hubs/events). Payloads carry
/// ids and status only. Messages go to one group at a time because SignalR does not de-duplicate a connection that
/// belongs to several groups.
/// </summary>
public sealed class TelehealthBroadcaster(IHubContext<TelehealthHub> hub, IRealtimePublisher realtime)
{
    public const string ChangedEvent = "telehealth.changed";

    public async Task SessionChangedAsync(TelehealthSession session, CancellationToken ct)
    {
        var payload = new { session.Id, session.Status, session.PractitionerId, session.PatientWaitingSince };
        await hub.Clients.Group(TelehealthGroups.Room(session.Id)).SendAsync("sessionUpdated", payload, ct);
        await realtime.ToFacilityAsync(session.FacilityId, ChangedEvent, payload, ct);
    }

    /// <summary>Tells admitted participants and anyone still in the waiting room that the consultation ended or was cancelled.</summary>
    public async Task SessionClosedAsync(TelehealthSession session, IReadOnlyList<string> waitingConnections, CancellationToken ct)
    {
        var payload = new { session.Id, session.Status };
        await hub.Clients.Group(TelehealthGroups.Room(session.Id)).SendAsync("sessionEnded", payload, ct);
        if (waitingConnections.Count > 0)
        {
            await hub.Clients.Clients(waitingConnections).SendAsync("sessionEnded", payload, ct);
        }

        await realtime.ToFacilityAsync(session.FacilityId, ChangedEvent, new { session.Id, session.Status, session.PractitionerId }, ct);
    }

    public Task ChatAsync(Guid sessionId, TelehealthMessageDto message, CancellationToken ct) =>
        hub.Clients.Group(TelehealthGroups.Room(sessionId)).SendAsync("chatMessage", message, ct);
}
