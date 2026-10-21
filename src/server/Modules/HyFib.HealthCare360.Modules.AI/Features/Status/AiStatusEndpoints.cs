using HyFib.HealthCare360.Modules.AI.Domain;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Modules.AI.Services;
using HyFib.HealthCare360.Modules.AI.Services.Speech;

namespace HyFib.HealthCare360.Modules.AI.Features.Status;

/// <summary>/api/v1/ai/status and /api/v1/ai/usage — feature availability for the UI and usage for administrators.</summary>
internal static class AiStatusEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/ai").WithTags("AI assistant");
        group.MapGet("/status", Status).RequireAnyPermission(AIPermissions.AssistantUse, AIPermissions.DictationUse, AIPermissions.SettingsManage);
        group.MapGet("/usage", Usage).RequirePermission(AIPermissions.SettingsManage);
    }

    private static async Task<IResult> Status(IAiGateway ai, SpeechToTextRouter speech, CancellationToken ct)
    {
        var text = await ai.GetAvailabilityAsync(ct);
        var stt = await speech.GetConfigAsync(ct);
        return TypedResults.Ok(new AiStatusDto(text.Enabled, text.Provider, text.Model, text.Reason, stt.IsUsable, stt.Settings.Provider, stt.Reason,
            (int)text.Timeout.TotalSeconds));
    }

    private static async Task<IResult> Usage(
        [AsParameters] PageQuery query, string? purpose, Guid? userId, DateTimeOffset? from, DateTimeOffset? to,
        HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var page = query.ToRequest();
        var facility = user.FacilityId;
        var usage = db.Set<AiUsage>().AsNoTracking().Where(u => u.FacilityId == null || u.FacilityId == facility);
        if (!string.IsNullOrWhiteSpace(purpose))
        {
            usage = usage.Where(u => u.Purpose == purpose);
        }

        if (userId is not null)
        {
            usage = usage.Where(u => u.UserId == userId);
        }

        if (from is not null)
        {
            usage = usage.Where(u => u.CreatedAt >= from);
        }

        if (to is not null)
        {
            usage = usage.Where(u => u.CreatedAt < to);
        }

        var result = await usage.OrderByDescending(u => u.CreatedAt)
            .Select(u => new AiUsageDto(u.Id, u.CreatedAt, u.UserId, u.PatientId, u.Purpose, u.Provider, u.Model, u.InputTokens, u.OutputTokens,
                u.LatencyMs, u.Success, u.Error, db.Set<User>().Where(x => x.Id == u.UserId).Select(x => x.DisplayName).FirstOrDefault()))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }
}
