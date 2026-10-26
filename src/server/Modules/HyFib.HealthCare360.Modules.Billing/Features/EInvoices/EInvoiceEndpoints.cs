using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Services;

namespace HyFib.HealthCare360.Modules.Billing.Features.EInvoices;

/// <summary>
/// /api/v1/billing/einvoices — the GST e-invoice register: B2B tax invoices (<c>bill</c>: bills to payers registered for
/// GST; <c>pharmacy</c>: the pharmacy's B2B sales), their e-invoice JSON for the portal, and the IRNs it gave.
/// </summary>
internal static class EInvoiceEndpoints
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/billing/einvoices").WithTags("Billing").RequirePermission(BillingPermissions.EInvoice);

        group.MapGet("", async (DateOnly? from, DateOnly? to, EInvoiceService einvoices, CancellationToken ct) =>
        {
            if (from is { } start && to is { } end && (end < start || end.DayNumber - start.DayNumber > 92))
            {
                return Error.Field("to", "Choose up to 92 days, ending on or after the start.").ToProblem();
            }

            return TypedResults.Ok(await einvoices.RegisterAsync(from, to, ct));
        });
        group.MapGet("/{source:regex(^(bill|pharmacy)$)}/{id:guid}", async (string source, Guid id, EInvoiceService einvoices, CancellationToken ct) =>
            (await einvoices.DetailAsync(source, id, ct)).ToOk());
        group.MapGet("/{source:regex(^(bill|pharmacy)$)}/{id:guid}/json", async (string source, Guid id, EInvoiceService einvoices, CancellationToken ct) =>
        {
            var built = await einvoices.PayloadAsync(source, id, ct);
            if (built.IsFailure)
            {
                return built.Error!.ToProblem();
            }

            // The portal's bulk upload takes a list of invoices.
            var json = JsonSerializer.Serialize(new[] { built.Value.Payload }, Indented);
            return TypedResults.File(Encoding.UTF8.GetBytes(json), "application/json", $"einvoice-{built.Value.Number}.json");
        });
        group.MapPost("/{source:regex(^(bill|pharmacy)$)}/{id:guid}/irn",
                async (string source, Guid id, RecordIrnRequest request, EInvoiceService einvoices, CancellationToken ct) =>
                    (await einvoices.RecordAsync(source, id, request, ct)).ToOk())
            .WithValidation<RecordIrnRequest>();
        group.MapPost("/{source:regex(^(bill|pharmacy)$)}/{id:guid}/irn/cancel",
                async (string source, Guid id, CancelIrnRequest request, EInvoiceService einvoices, CancellationToken ct) =>
                    (await einvoices.CancelAsync(source, id, request, ct)).ToOk())
            .WithValidation<CancelIrnRequest>();
    }
}
