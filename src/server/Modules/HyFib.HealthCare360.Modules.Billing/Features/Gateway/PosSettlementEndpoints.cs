using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using Microsoft.AspNetCore.Mvc;

namespace HyFib.HealthCare360.Modules.Billing.Features.Gateway;

/// <summary>
/// /api/v1/billing/pos/settlements — card and UPI terminal reconciliation (INT-POS "reconcile"): the acquirer's settlement
/// reports imported, matched with HC360's receipts and refunds, the payouts by day, what to look at, and the receipts not
/// settled yet. Everything is limited to the signed-in facility and needs <see cref="BillingPermissions.GatewayReconcile"/>.
/// </summary>
internal static class PosSettlementEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/billing/pos/settlements").WithTags("Billing - Card and UPI reconciliation")
            .RequirePermission(BillingPermissions.GatewayReconcile);
        group.MapGet("/summary", Summary);
        // Bearer-token API (no cookie authentication), so antiforgery does not apply. A report larger than a file can be is
        // refused before it is read.
        group.MapPost("/imports", Import).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(PosSettlementFile.MaxBytes + (64 * 1024)));
        group.MapGet("/imports", Imports);
        group.MapGet("/days", Days);
        group.MapGet("/lines", Lines);
        group.MapPost("/lines/{id:guid}/review", Review).WithValidation<ReviewPosSettlementLineRequest>();
        group.MapPost("/match", Match);
        group.MapGet("/unsettled", Unsettled);
        group.MapGet("/training-file", TrainingFile);
    }

    private static async Task<IResult> Summary(ICurrentUser user, PosSettlementService service, CancellationToken ct) =>
        user.FacilityId is { } facilityId ? TypedResults.Ok(await service.SummaryAsync(facilityId, ct)) : BillingDb.FacilityRequired.ToProblem();

    /// <summary>
    /// Multipart <c>file</c> (the acquirer's report, CSV or .xlsx, at most 10 MB), <c>acquirer</c> (its name) and, for a
    /// report without a settlement date column, <c>settledOn</c>. 201 with what was imported; 400 <c>file</c> when the
    /// report cannot be read (the message says why); 409 <c>billing.pos_settlement_imported</c> for a report already
    /// imported.
    /// </summary>
    private static async Task<IResult> Import(
        IFormFile? file, [FromForm] string? acquirer, [FromForm] DateOnly? settledOn, ICurrentUser user, PosSettlementService service, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return BillingDb.FacilityRequired.ToProblem();
        }

        if (file is null || file.Length == 0)
        {
            return Error.Field("file", "Choose the settlement report to import. " + PosSettlementFile.AcceptedFiles).ToProblem();
        }

        if (file.Length > PosSettlementFile.MaxBytes)
        {
            return Error.Field("file", "The file is larger than 10 MB. Import the report in parts, for example a week at a time.").ToProblem();
        }

        if (acquirer is { Length: > 64 } || acquirer?.Any(char.IsControl) == true)
        {
            return Error.Field("acquirer", "Name the acquirer in at most 64 characters, on one line.").ToProblem();
        }

        using var buffer = new MemoryStream((int)file.Length);
        await using (var stream = file.OpenReadStream())
        {
            await stream.CopyToAsync(buffer, ct);
        }

        var result = await service.ImportAsync(facilityId, buffer.ToArray(), file.FileName, acquirer, settledOn, ct);
        return result.IsFailure ? result.Error!.ToProblem() : TypedResults.Created("/api/v1/billing/pos/settlements/imports", result.Value);
    }

    private static async Task<IResult> Imports([AsParameters] PageQuery query, ICurrentUser user, PosSettlementService service, CancellationToken ct) =>
        user.FacilityId is { } facilityId ? TypedResults.Ok(await service.ImportsAsync(facilityId, query.ToRequest(), ct)) : BillingDb.FacilityRequired.ToProblem();

    /// <summary>The payouts between <paramref name="from"/> and <paramref name="to"/> by day (at most 92 days; the last month by default).</summary>
    private static async Task<IResult> Days(DateOnly? from, DateOnly? to, ICurrentUser user, PosSettlementService service, TimeProvider time, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return BillingDb.FacilityRequired.ToProblem();
        }

        var last = to ?? BillingTime.Today(time);
        var first = from ?? last.AddDays(-30);
        if (first > last)
        {
            return Error.Field("from", "The start must be on or before the end.").ToProblem();
        }

        if (last.DayNumber - first.DayNumber >= PosSettlementService.MaxListDays)
        {
            return Error.Field("from", $"Choose at most {PosSettlementService.MaxListDays} days.").ToProblem();
        }

        return TypedResults.Ok(await service.DaysAsync(facilityId, first, last, ct));
    }

    /// <summary>
    /// <c>attention=true</c>: the transactions to look at, worst first. <c>settledOn</c> with <c>utr</c> (or
    /// <c>noUtr=true</c>): one payout's transactions.
    /// </summary>
    private static async Task<IResult> Lines(
        [AsParameters] PageQuery query, bool? attention, DateOnly? settledOn, string? utr, bool? noUtr, ICurrentUser user, PosSettlementService service,
        CancellationToken ct) =>
        user.FacilityId is { } facilityId
            ? TypedResults.Ok(await service.LinesAsync(facilityId, query.ToRequest(), attention == true, settledOn, utr, noUtr == true, ct))
            : BillingDb.FacilityRequired.ToProblem();

    private static async Task<IResult> Review(
        Guid id, ReviewPosSettlementLineRequest request, ICurrentUser user, PosSettlementService service, CancellationToken ct) =>
        user.FacilityId is null ? BillingDb.FacilityRequired.ToProblem() : (await service.ReviewAsync(id, request, ct)).ToOk();

    private static async Task<IResult> Match(ICurrentUser user, PosSettlementService service, CancellationToken ct) =>
        user.FacilityId is { } facilityId ? (await service.RematchAsync(facilityId, ct)).ToOk() : BillingDb.FacilityRequired.ToProblem();

    private static async Task<IResult> Unsettled(ICurrentUser user, PosSettlementService service, CancellationToken ct) =>
        user.FacilityId is { } facilityId ? TypedResults.Ok(await service.UnsettledAsync(facilityId, ct)) : BillingDb.FacilityRequired.ToProblem();

    /// <summary>A settlement report of the training terminal's sales of <paramref name="date"/> (CSV), to practise with.</summary>
    private static async Task<IResult> TrainingFile(DateOnly? date, ICurrentUser user, PosSettlementService service, TimeProvider time, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return BillingDb.FacilityRequired.ToProblem();
        }

        var (content, fileName) = await service.TrainingFileAsync(facilityId, date ?? BillingTime.Today(time), ct);
        return TypedResults.File(content, "text/csv; charset=utf-8", fileName);
    }
}
