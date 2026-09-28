using HyFib.HealthCare360.Integrations.Domain;

namespace HyFib.HealthCare360.Integrations.Features.Admin;

/// <summary>
/// /api/v1/admin/integrations — status dashboard, send-a-test, integration log and the simulator inboxes.
/// Settings themselves are edited through the generic settings API (<c>/api/v1/admin/settings/integrations.*</c>).
/// </summary>
internal static class IntegrationAdminEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/integrations")
            .WithTags("Administration")
            .RequirePermission(PlatformPermissions.IntegrationsManage);

        group.MapGet("/", List);
        group.MapPost("/{name}/test", Test).WithValidation<TestIntegrationRequest>();
        group.MapGet("/logs", Logs);
        group.MapGet("/simulator/messages", SimulatorMessages);
        group.MapGet("/simulator/transactions", SimulatorTransactions);
        group.MapGet("/whatsapp/inbound", InboundWhatsApp);
    }

    private static async Task<IResult> List(IntegrationStatusService status, CancellationToken ct) =>
        TypedResults.Ok(await status.ListAsync(ct));

    private static async Task<IResult> Test(string name, TestIntegrationRequest request, IntegrationTestService tests, CancellationToken ct)
    {
        var key = name.Trim().ToLowerInvariant();
        var result = IntegrationNames.All.Contains(key) ? await tests.RunAsync(key, request, ct) : null;
        return result is null ? Error.NotFound("Integration", name).ToProblem() : TypedResults.Ok(result);
    }

    private static async Task<IResult> Logs(
        [AsParameters] PageQuery query, string? integration, bool? success, DateTimeOffset? from, DateTimeOffset? to,
        HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var page = query.ToRequest();
        var facility = user.FacilityId;
        var logs = db.Set<IntegrationLog>().AsNoTracking().Where(l => l.FacilityId == null || l.FacilityId == facility);
        if (!string.IsNullOrWhiteSpace(integration))
        {
            var key = integration.Trim().ToLowerInvariant();
            logs = logs.Where(l => l.Integration == key);
        }

        if (success is not null)
        {
            logs = logs.Where(l => l.Success == success);
        }

        if (from is not null)
        {
            logs = logs.Where(l => l.CreatedAt >= from);
        }

        if (to is not null)
        {
            logs = logs.Where(l => l.CreatedAt < to);
        }

        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            logs = logs.Where(l => EF.Functions.ILike(l.Reference!, like) || EF.Functions.ILike(l.Operation, like) || EF.Functions.ILike(l.Provider, like));
        }

        var result = await logs.OrderByDescending(l => l.CreatedAt)
            .Select(l => new IntegrationLogDto(l.Id, l.CreatedAt, l.Integration, l.Provider, l.Operation, l.Direction, l.Success, l.DurationMs,
                l.Reference, l.Request, l.Response, l.Error, l.CorrelationId))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> SimulatorMessages(
        [AsParameters] PageQuery query, SimulatorChannel? channel, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var page = query.ToRequest();
        var facility = user.FacilityId;
        var messages = db.Set<SimulatorMessage>().AsNoTracking().Where(m => m.FacilityId == null || m.FacilityId == facility);
        if (channel is not null)
        {
            messages = messages.Where(m => m.Channel == channel);
        }

        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            messages = messages.Where(m => EF.Functions.ILike(m.Body, like) || EF.Functions.ILike(m.Reference!, like) || EF.Functions.ILike(m.Subject!, like));
        }

        var result = await messages.OrderByDescending(m => m.CreatedAt)
            .Select(m => new SimulatorMessageDto(m.Id, m.CreatedAt, m.Channel, m.Recipient, m.Subject, m.Body, m.Details, m.Reference, m.ProviderMessageId))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> SimulatorTransactions(
        [AsParameters] PageQuery query, SimulatorTransactionKind? kind, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var page = query.ToRequest();
        var facility = user.FacilityId;
        var transactions = db.Set<SimulatorTransaction>().AsNoTracking().Where(t => t.FacilityId == null || t.FacilityId == facility);
        if (kind is not null)
        {
            transactions = transactions.Where(t => t.Kind == kind);
        }

        var result = await transactions.OrderByDescending(t => t.CreatedAt)
            .Select(t => new SimulatorTransactionDto(t.Id, t.CreatedAt, t.CompletedAt, t.Kind, t.ExternalId, t.Amount, t.Currency, t.Status, t.Reference,
                t.TerminalId, t.PaymentId, t.RefundedAmount))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> InboundWhatsApp(
        [AsParameters] PageQuery query, WhatsAppKeyword? keyword, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var page = query.ToRequest();
        var facility = user.FacilityId;
        var messages = db.Set<InboundWhatsAppMessage>().AsNoTracking().Where(m => m.FacilityId == null || m.FacilityId == facility);
        if (keyword is not null)
        {
            messages = messages.Where(m => m.Keyword == keyword);
        }

        var rows = await messages.OrderByDescending(m => m.ReceivedAt)
            .Select(m => new { m.Id, m.ReceivedAt, m.SentAt, m.FromPhone, m.ProfileName, m.MessageType, m.Text, m.Keyword })
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(rows.Map(m => new InboundWhatsAppMessageDto(m.Id, m.ReceivedAt, m.SentAt, Phone.Mask(m.FromPhone), m.ProfileName, m.MessageType, m.Text, m.Keyword)));
    }
}
