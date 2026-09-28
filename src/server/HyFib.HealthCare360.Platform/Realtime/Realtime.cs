using System.Text.RegularExpressions;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace HyFib.HealthCare360.Platform.Realtime;

/// <summary>Topic naming for SignalR groups. Payloads carry ids/status only; clients refetch details with their own authorisation.</summary>
public static class Topics
{
    public static string User(Guid id) => $"user:{id}";
    public static string Facility(Guid id) => $"facility:{id}";
    public static string Department(Guid id) => $"dept:{id}";
    public static string Queue(Guid departmentId) => $"queue:{departmentId}";
    public static string Beds(Guid facilityId) => $"beds:{facilityId}";
    public static string Patient(Guid id) => $"patient:{id}";
    public static string Ward(Guid id) => $"ward:{id}";
    public static string Display(Guid id) => $"display:{id}";
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
/// Single real-time hub (<c>/hubs/events</c>). Every connection joins its user and facility groups;
/// screens subscribe to extra topics (queue, bed board, ward…). SignalR reconnects automatically after
/// WiFi drops and clients refetch on reconnect.
/// </summary>
[Authorize]
public sealed partial class EventsHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var user = Context.User;
        if (Guid.TryParse(user?.FindFirst(HcClaims.UserId)?.Value, out var userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, Topics.User(userId));
        }

        if (Guid.TryParse(user?.FindFirst(HcClaims.Facility)?.Value, out var facilityId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, Topics.Facility(facilityId));
        }

        await base.OnConnectedAsync();
    }

    public async Task Subscribe(string topic)
    {
        if (!IsAllowed(topic))
        {
            throw new HubException("Subscription not allowed.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, topic);
    }

    public Task Unsubscribe(string topic) => Groups.RemoveFromGroupAsync(Context.ConnectionId, topic);

    private bool IsAllowed(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic) || !TopicRegex().IsMatch(topic))
        {
            return false;
        }

        var user = Context.User;
        var isPatient = string.Equals(user?.FindFirst(HcClaims.UserType)?.Value, HcClaims.PatientType, StringComparison.Ordinal);
        if (!isPatient)
        {
            return !topic.StartsWith("user:", StringComparison.Ordinal);
        }

        // Portal users may only follow their own record and their telehealth rooms (validated by the telehealth module).
        var patientId = user?.FindFirst(HcClaims.Patient)?.Value;
        return (patientId is not null && topic == $"patient:{patientId}") || topic.StartsWith("tele:", StringComparison.Ordinal);
    }

    [GeneratedRegex("^(dept|queue|beds|patient|ward|display|lab|pharmacy|tele|ot|facility|user):[0-9a-fA-F-]{36}$")]
    private static partial Regex TopicRegex();
}
