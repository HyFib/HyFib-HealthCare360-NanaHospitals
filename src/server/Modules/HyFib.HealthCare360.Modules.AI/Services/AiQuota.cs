using System.Threading.RateLimiting;
using HyFib.HealthCare360.Modules.AI.Domain;

namespace HyFib.HealthCare360.Modules.AI.Services;

/// <summary>
/// Per-user hourly and daily AI quotas (<c>ai.limits</c>), counted from the usage records so they hold across server
/// nodes and restarts and keep provider spend predictable.
/// </summary>
internal sealed class AiQuota(HospitalDbContext db, AiSettingsReader settings, ICurrentUser user, TimeProvider time) : IAiQuota
{
    public async Task<string?> CheckAsync(CancellationToken cancellationToken)
    {
        if (user.UserId is not { } userId)
        {
            return "Sign in to use the AI assistant.";
        }

        var limits = await settings.GetLimitsAsync(cancellationToken);
        var now = time.GetUtcNow();
        var hourAgo = now.AddHours(-1);
        var dayAgo = now.AddDays(-1);
        var usage = db.Set<AiUsage>().AsNoTracking().Where(u => u.UserId == userId && u.CreatedAt >= dayAgo);
        if (await usage.CountAsync(u => u.CreatedAt >= hourAgo, cancellationToken) >= Math.Max(1, limits.PerUserPerHour))
        {
            return $"You have reached the limit of {limits.PerUserPerHour} AI requests per hour. Please try again later.";
        }

        return await usage.CountAsync(cancellationToken) >= Math.Max(1, limits.PerUserPerDay)
            ? $"You have reached the limit of {limits.PerUserPerDay} AI requests per day."
            : null;
    }
}

/// <summary>
/// Burst limiter for the AI endpoints: 20 requests per minute per signed-in user (sliding window), so a stuck
/// push-to-talk button or a script cannot flood the provider. Longer-term quotas are enforced by <see cref="AiQuota"/>.
/// </summary>
internal static class AiRateLimits
{
    public const string Policy = "ai";

    public static RateLimitPartition<string> Partition(HttpContext http) =>
        RateLimitPartition.GetSlidingWindowLimiter(
            http.User.FindFirst(HcClaims.UserId)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 4,
                QueueLimit = 0,
            });
}
