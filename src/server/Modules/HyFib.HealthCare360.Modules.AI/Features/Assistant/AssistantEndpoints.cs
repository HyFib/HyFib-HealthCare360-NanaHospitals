using HyFib.HealthCare360.Modules.AI.Services;
using HyFib.HealthCare360.Modules.AI.Services.Assistant;

namespace HyFib.HealthCare360.Modules.AI.Features.Assistant;

/// <summary>/api/v1/ai/assistant — voice/typed commands (always answered: offline rules first, AI only as a fallback).</summary>
internal static class AssistantEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/ai/assistant/command", Command)
            .WithTags("AI assistant")
            .RequirePermission(AIPermissions.AssistantUse)
            .RequireRateLimiting(AiRateLimits.Policy)
            .WithValidation<AssistantCommandRequest>();
    }

    private static async Task<IResult> Command(AssistantCommandRequest request, AssistantCommandService assistant, CancellationToken ct) =>
        TypedResults.Ok(await assistant.ExecuteAsync(request, ct));
}
