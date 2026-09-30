using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace HyFib.HealthCare360.Platform.Realtime;

/// <summary>Topic naming for SignalR groups. Payloads carry ids/status only; clients refetch details with their own authorisation.</summary>
public static class Topics
{
    public static string User(Guid id) => $"user:{id}";
    public static string Facility(Guid id) => $"facility:{id}";
    public static string Queue(Guid departmentId) => $"queue:{departmentId}";
    public static string Beds(Guid facilityId) => $"beds:{facilityId}";
    public static string Patient(Guid id) => $"patient:{id}";
    public static string Ward(Guid id) => $"ward:{id}";
    public static string Lab(Guid facilityId) => $"lab:{facilityId}";
    public static string Pharmacy(Guid facilityId) => $"pharmacy:{facilityId}";
    public static string Telehealth(Guid sessionId) => $"tele:{sessionId}";
}

public sealed record RealtimeEnvelope(string Topic, string Name, object? Payload, DateTimeOffset At);

public interface IRealtimePublisher
{
    Task PublishAsync(string topic, string eventName, object? payload = null, CancellationToken cancellationToken = default);

    Task ToUserAsync(Guid userId, string eventName, object? payload = null, CancellationToken cancellationToken = default);

    Task ToFacilityAsync(Guid facilityId, string eventName, object? payload = null, CancellationToken cancellationToken = default);
}

internal sealed class SignalRRealtimePublisher(IHubContext<EventsHub> hub, TimeProvider time) : IRealtimePublisher
{
    public Task PublishAsync(string topic, string eventName, object? payload = null, CancellationToken cancellationToken = default) =>
        hub.Clients.Group(topic).SendAsync("event", new RealtimeEnvelope(topic, eventName, payload, time.GetUtcNow()), cancellationToken);

    public Task ToUserAsync(Guid userId, string eventName, object? payload = null, CancellationToken cancellationToken = default) =>
        PublishAsync(Topics.User(userId), eventName, payload, cancellationToken);

    public Task ToFacilityAsync(Guid facilityId, string eventName, object? payload = null, CancellationToken cancellationToken = default) =>
        PublishAsync(Topics.Facility(facilityId), eventName, payload, cancellationToken);
}

/// <summary>
/// Single real-time hub (<c>/hubs/events</c>). Every connection joins its user's group, and a staff session with full
/// access also its facility's (<see cref="RealtimeTopicAuthorizer.FacilityToJoin"/>). Screens subscribe to extra topics
/// (queue, bed board, ward…), each checked by <see cref="RealtimeTopicAuthorizer"/>.
/// SignalR reconnects automatically after WiFi drops and clients refetch on reconnect.
/// </summary>
[Authorize]
public sealed class EventsHub(RealtimeTopicAuthorizer topics) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var user = Context.User;
        if (Guid.TryParse(user?.FindFirst(HcClaims.UserId)?.Value, out var userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, Topics.User(userId));
        }

        if (RealtimeTopicAuthorizer.FacilityToJoin(user) is { } facilityId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, Topics.Facility(facilityId));
        }

        await base.OnConnectedAsync();
    }

    public async Task Subscribe(string topic)
    {
        var allowed = await topics.AuthorizeAsync(Context.User, topic, Context.ConnectionAborted)
            ?? throw new HubException("Subscription not allowed.");
        await Groups.AddToGroupAsync(Context.ConnectionId, allowed);
    }

    public Task Unsubscribe(string topic) => Groups.RemoveFromGroupAsync(Context.ConnectionId, topic);
}
