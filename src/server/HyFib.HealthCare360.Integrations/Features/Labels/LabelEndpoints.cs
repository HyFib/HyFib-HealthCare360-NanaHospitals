namespace HyFib.HealthCare360.Integrations.Features.Labels;

/// <summary>/api/v1/integrations/labels — ZPL/TSPL for UHID cards, wristbands, sample, pharmacy and asset labels (INT-BAR).</summary>
internal static class LabelEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/integrations/labels", Build)
            .WithTags("Integrations")
            .RequirePermission(IntegrationsPermissions.LabelsPrint)
            .WithValidation<PrintLabelRequest>();
    }

    private static IResult Build(PrintLabelRequest request, ILabelPrinterService labels)
    {
        try
        {
            var language = request.Language.Trim().ToLowerInvariant();
            var content = labels.Build(
                new LabelRequest(request.Value.Trim(), request.Symbology, request.TextLines ?? [], request.WidthMm, request.HeightMm, request.Copies),
                language);
            return TypedResults.Ok(new PrintLabelResponse(language, content, request.Copies));
        }
        catch (ArgumentException ex)
        {
            // Layout limits the validator cannot know (for example a value too long for the chosen label width).
            return Error.Field("value", ex.Message.Split(" (Parameter", 2)[0]).ToProblem();
        }
    }
}
