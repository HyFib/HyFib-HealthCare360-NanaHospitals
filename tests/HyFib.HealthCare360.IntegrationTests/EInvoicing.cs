using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// A signed QR code as the portal makes it, generated a minute ago: the document's details in the payload's
    /// <c>data</c>; <paramref name="type"/> is <c>INV</c> for an invoice and <c>CRN</c> for a credit note.
    /// </summary>
    public static string Qr(string number, decimal total, string buyer, string irn, int items, string type = "INV")
    {
        var generated = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).AddMinutes(-1);
        var data = new JsonObject
        {
            ["SellerGstin"] = Hospital,
            ["BuyerGstin"] = buyer,
            ["DocNo"] = number,
            ["DocTyp"] = type,
            ["DocDt"] = generated.ToString("dd'/'MM'/'yyyy", CultureInfo.InvariantCulture),
            ["TotInvVal"] = total,
            ["ItemCnt"] = items,
            ["Irn"] = irn,
            ["IrnDt"] = generated.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        };
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Part("{\"alg\":\"RS256\"}")}.{Part(JsonSerializer.Serialize(new { data = data.ToJsonString(), iss = "NIC" }))}.c2lnbmF0dXJl";
    }

    public static string Irn() => Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());

    /// <summary>The hospital's (IST) day.</summary>
    public static DateOnly Today() => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).DateTime);

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
