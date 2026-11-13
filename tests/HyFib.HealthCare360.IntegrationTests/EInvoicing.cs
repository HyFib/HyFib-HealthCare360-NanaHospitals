using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Integrations.Irp;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.EInvoices;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// GST e-invoicing for a test: the hospital registered in Chennai with e-invoicing from a date, and the portal's signed QR
/// codes as it makes them.
/// </summary>
internal static class EInvoicing
{
    public const string Hospital = "33AABCN1234F1Z3";
    public const string KeralaCompany = "32AAACH7409R1ZA";
    public const string Register = "/api/v1/billing/einvoices";

    /// <summary>
    /// The hospital registered in Chennai, with e-invoicing <paramref name="from"/> a date (or off), for the rest of the
    /// test; then put back, because the server is shared by every test of the run.
    /// </summary>
    public static async Task<IAsyncDisposable> EInvoicingAsync(this HospitalServer server, DateOnly? from, CancellationToken ct)
    {
        var before = new Dictionary<Guid, (string? Gstin, Address Address, string? Setting)>();
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            foreach (var facility in await db.Set<Facility>().ToListAsync(ct))
            {
                before[facility.Id] = (facility.Gstin, facility.Address, await settings.GetAsync<string?>("gst.einvoice.from", facility.Id, ct));
                facility.Gstin = Hospital;
                facility.Address = new Address { Line1 = "12 Anna Salai", City = "Chennai", State = "Tamil Nadu", PinCode = "600018" };
                await settings.SetAsync<string?>("gst.einvoice.from", from?.ToString("yyyy-MM-dd"), facility.Id, cancellationToken: ct);
            }

            await db.SaveChangesAsync(ct);
        }

        return new Restore(async () =>
        {
            await using var scope = server.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            foreach (var facility in await db.Set<Facility>().ToListAsync(CancellationToken.None))
            {
                if (before.TryGetValue(facility.Id, out var was))
                {
                    (facility.Gstin, facility.Address) = (was.Gstin, was.Address);
                    await settings.SetAsync("gst.einvoice.from", was.Setting, facility.Id, cancellationToken: CancellationToken.None);
                }
            }

            await db.SaveChangesAsync(CancellationToken.None);
        });
    }

    /// <summary>
    /// A signed QR code as the portal makes it, generated a minute ago (or <paramref name="generatedAt"/>), for a document
    /// of that day (or <paramref name="date"/>): the document's details in the payload's <c>data</c>; <paramref name="type"/>
    /// is <c>INV</c> for an invoice and <c>CRN</c> for a credit note.
    /// </summary>
    public static string Qr(
        string number, decimal total, string buyer, string irn, int items, string type = "INV", DateOnly? date = null, DateTimeOffset? generatedAt = null)
    {
        var generated = (generatedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1)).ToOffset(TimeSpan.FromHours(5.5));
        var data = new JsonObject
        {
            ["SellerGstin"] = Hospital,
            ["BuyerGstin"] = buyer,
            ["DocNo"] = number,
            ["DocTyp"] = type,
            ["DocDt"] = (date ?? DateOnly.FromDateTime(generated.DateTime)).ToString("dd'/'MM'/'yyyy", CultureInfo.InvariantCulture),
            ["TotInvVal"] = total,
            ["ItemCnt"] = items,
            ["Irn"] = irn,
            ["IrnDt"] = generated.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        };
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Part("{\"alg\":\"RS256\"}")}.{Part(JsonSerializer.Serialize(new { data = data.ToJsonString(), iss = "NIC" }))}.c2lnbmF0dXJl";
    }

    public static string Irn() => Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());

    /// <summary>The e-invoice portal set to the training simulator (or switched off) for the rest of the test, then put back.</summary>
    public static async Task<IAsyncDisposable> IrpPortalAsync(this HospitalServer server, bool enabled)
    {
        const string key = "integrations.irp";
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var before = (await admin.GetAsync<List<SettingDto>>($"/api/v1/admin/settings?prefix={key}"))
            .FirstOrDefault(s => s.Key == key && s.FacilityId is null)?.Value;
        await server.SaveSettingAsync(admin, key, new SaveSettingRequest(new JsonObject { ["enabled"] = enabled, ["provider"] = "simulator" }, null, false, null));
        return new Restore(async () =>
            await server.SaveSettingAsync(admin, key, new SaveSettingRequest(before?.DeepClone() ?? new JsonObject { ["enabled"] = false }, null, false, null)));
    }

    /// <summary>
    /// Waits, for up to a minute, until the outbox has dealt with the request for the document's IRN queued as it became
    /// final: a test that connects the portal afterwards knows nothing will then ask for that IRN on its own.
    /// </summary>
    public static async Task EInvoiceQueueSettledAsync(this HospitalServer server, Guid documentId, CancellationToken ct)
    {
        for (var waited = TimeSpan.Zero; ; waited += TimeSpan.FromSeconds(1))
        {
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
                var pending = await db.Database.SqlQuery<int>($"""
                    SELECT count(*)::int AS "Value" FROM platform.outbox_messages
                     WHERE type = {EInvoiceGenerateHandler.MessageType} AND status = 'Pending' AND payload->>'id' = {documentId.ToString()}
                    """).SingleAsync(ct);
                if (pending == 0)
                {
                    return;
                }
            }

            if (waited >= TimeSpan.FromMinutes(1))
            {
                throw new ShouldAssertException($"The outbox did not deal with the IRN request of {documentId} within a minute.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    /// <summary>Moves an IRN's generation back by <paramref name="age"/>, here and on the simulated portal.</summary>
    public static async Task BackdateIrnAsync(this HospitalServer server, string irn, TimeSpan age, CancellationToken ct)
    {
        var ledger = server.Services.GetRequiredService<SimulatorIrpLedger>();
        var entry = ledger.Entries[irn];
        var at = DateTimeOffset.UtcNow - age;
        ledger.Entries[irn] = entry with { Irn = entry.Irn with { AckDate = at } };
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<EInvoice>().Where(e => e.Irn == irn)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.AckDate, at), ct);
    }

    /// <summary>
    /// The document's IRN from the connected portal: asked for here unless HC360 has it already (the outbox asks for it on
    /// its own once the document is final, and a 409 means it got there first), then the document as registered.
    /// </summary>
    public static async Task<EInvoiceDetail> IrnFromThePortalAsync(this ApiClient client, string source, Guid id, CancellationToken ct)
    {
        using (var asked = await client.SendRawAsync(HttpMethod.Post, $"{Register}/{source}/{id}/irn/generate"))
        {
            if (asked.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict))
            {
                throw new ShouldAssertException($"Asking for the IRN of {source} {id}: {(int)asked.StatusCode} {await asked.Content.ReadAsStringAsync(ct)}");
            }
        }

        return await client.EInvoiceWhenAsync(source, id, d => d.State == EInvoiceState.Registered, "the IRN", ct);
    }

    /// <summary>
    /// The document's e-invoice once <paramref name="condition"/> holds, read again every second for up to a minute (or
    /// <paramref name="within"/>): what the outbox does (it runs every two seconds, and backs off on a retry) is seen this way.
    /// </summary>
    public static async Task<EInvoiceDetail> EInvoiceWhenAsync(
        this ApiClient client, string source, Guid id, Func<EInvoiceDetail, bool> condition, string what, CancellationToken ct, TimeSpan? within = null)
    {
        EInvoiceDetail detail;
        var waited = TimeSpan.Zero;
        while (!condition(detail = await client.GetAsync<EInvoiceDetail>($"{Register}/{source}/{id}")))
        {
            if (waited >= (within ?? TimeSpan.FromMinutes(1)))
            {
                throw new ShouldAssertException($"Waited {waited.TotalSeconds:0} s for {what} of {source} {id}; it stands {detail.State}.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            waited += TimeSpan.FromSeconds(1);
        }

        return detail;
    }

    /// <summary>The hospital's (IST) day.</summary>
    public static DateOnly Today() => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).DateTime);

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
