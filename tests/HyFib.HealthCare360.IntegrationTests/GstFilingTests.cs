using System.Net;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.EInvoices;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The record that a month's GSTR-1 was filed: the accountant marks it once with the portal's ARN, corrects it only with a
/// reason, both audited; the billing desk cannot.
/// </summary>
public sealed class GstFilingTests(HospitalServer server)
{
    private const string Filing = "/api/v1/billing/gst-returns/gstr1/filing";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_months_gstr1_is_marked_filed_once_and_corrected_only_with_a_reason()
    {
        await using var einvoicing = await server.EInvoicingAsync(EInvoicing.Today().AddDays(-1), Ct);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var today = EInvoicing.Today();
        var last = today.AddMonths(-1);
        var month = $"{last:yyyy-MM}";
        await ForgetAsync(last);
        try
        {
            (await accountant.GetAsync<Gstr1Return>($"/api/v1/billing/gst-returns/gstr1?month={month}")).Filing.ShouldBeNull();

            // Not before the month ended, not in the future; the billing desk cannot record it.
            await RefusedAsync(accountant, HttpMethod.Post, new MarkGstr1FiledRequest(month, "AA330000000002Z", last), HttpStatusCode.BadRequest, "before the month ended");
            await RefusedAsync(accountant, HttpMethod.Post, new MarkGstr1FiledRequest(month, "AA330000000002Z", today.AddDays(1)), HttpStatusCode.BadRequest, "future");
            using (var desk = await billing.SendRawAsync(HttpMethod.Post, Filing, new MarkGstr1FiledRequest(month, "AA330000000002Z", today)))
            {
                desk.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }

            var filed = await accountant.PostAsync<Gstr1Filing>(Filing, new MarkGstr1FiledRequest(month, "aa330000000002z", today));
            (filed.Year, filed.Month, filed.Arn, filed.FiledOn).ShouldBe((last.Year, last.Month, "AA330000000002Z", today));
            filed.RecordedByName.ShouldNotBeNullOrWhiteSpace();
            await RefusedAsync(accountant, HttpMethod.Post, new MarkGstr1FiledRequest(month, "AA330000000003Z", today), HttpStatusCode.Conflict, "already recorded as filed");

            // A mistyped ARN is corrected, with a reason; without one it is refused.
            await RefusedAsync(accountant, HttpMethod.Put, new CorrectGstr1FilingRequest(month, "AA330000000003Z", today, "", filed.Version), HttpStatusCode.BadRequest, null);
            var corrected = await accountant.PutAsync<Gstr1Filing>(Filing, new CorrectGstr1FilingRequest(month, "AA330000000003Z", today, "ARN mistyped", filed.Version));
            (corrected.Arn, corrected.CorrectionReason).ShouldBe(("AA330000000003Z", "ARN mistyped"));
            corrected.CorrectedAt.ShouldNotBeNull();
            (await accountant.GetAsync<Gstr1Return>($"/api/v1/billing/gst-returns/gstr1?month={month}")).Filing.ShouldNotBeNull().Arn.ShouldBe("AA330000000003Z");

            await using var scope = server.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var id = (await db.Set<GstReturnFiling>().AsNoTracking()
                .SingleAsync(f => f.Gstin == EInvoicing.Hospital && f.Year == last.Year && f.Month == last.Month, Ct)).Id.ToString();
            var actions = await db.Set<AuditEvent>().AsNoTracking()
                .Where(a => a.EntityType == nameof(GstReturnFiling) && a.EntityId == id)
                .Select(a => a.Action).ToListAsync(Ct);
            actions.ShouldContain("billing.gstr1_filed");
            actions.ShouldContain("billing.gstr1_filing_corrected");
        }
        finally
        {
            await ForgetAsync(last);
        }
    }

    private async Task ForgetAsync(DateOnly month)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<GstReturnFiling>()
            .Where(f => f.Gstin == EInvoicing.Hospital && f.Year == month.Year && f.Month == month.Month).ExecuteDeleteAsync(Ct);
    }

    private static async Task RefusedAsync(ApiClient api, HttpMethod method, object body, HttpStatusCode status, string? message)
    {
        using var response = await api.SendRawAsync(method, Filing, body);
        response.StatusCode.ShouldBe(status);
        if (message is not null)
        {
            (await response.Content.ReadAsStringAsync(Ct)).ShouldContain(message);
        }
    }
}
