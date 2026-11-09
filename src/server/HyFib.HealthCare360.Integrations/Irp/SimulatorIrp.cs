using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace HyFib.HealthCare360.Integrations.Irp;

/// <summary>What the IRP simulator has registered since the server started (it keeps nothing across restarts).</summary>
internal sealed class SimulatorIrpLedger
{
    public sealed record Entry(JsonObject Payload, IrpIrn Irn, bool Cancelled, string? Gstin);

    public ConcurrentDictionary<string, Entry> Entries { get; } = new(StringComparer.Ordinal);

    private long _sequence;

    public long Next() => Interlocked.Increment(ref _sequence);
}

/// <summary>
/// The training Invoice Registration Portal (<c>simulator</c>), which only a training or test server can use (a live
/// server refuses it before any call, <see cref="IntegrationSettingsReader"/>). It answers as the portal does in the cases
/// HC360 relies on: the IRN is the portal's own hash (SHA-256 of the supplier's GSTIN, the financial year, the document
/// type and its number), so the same document asked for twice is a duplicate (2150) that names the IRN it holds; the signed
/// QR code carries the document's details; an IRN is cancelled only within 24 hours of its generation (2270 after); and
/// an e-way bill is generated from a live IRN, valid one day for each 200 km. Its signatures are not the portal's: an IRN
/// from it is not a real one, and the simulator sweep lists any found on a live server.
/// </summary>
internal sealed class SimulatorIrp(SimulatorIrpLedger ledger, IntegrationTrace trace, TimeProvider time) : IIrpProvider
{
    private static readonly TimeSpan India = TimeSpan.FromHours(5.5);

    public IReadOnlyCollection<string> Keys { get; } = [ProviderKeys.Simulator];

    public async Task<IrpResult<IrpIrn>> GenerateAsync(IrpContext context, JsonObject payload, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        var result = Generate(context, payload);
        await LogAsync("irn.generate", started, result.Error, context);
        return result;
    }

    public async Task<IrpResult<IrpCancellation>> CancelAsync(IrpContext context, string irn, int reason, string remark, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        IrpResult<IrpCancellation> result;
        var now = time.GetUtcNow();
        if (!ledger.Entries.TryGetValue(irn, out var entry))
        {
            result = IrpResult<IrpCancellation>.Fail("9999", "The simulator holds no such IRN (it forgets them when the server restarts).");
        }
        else if (entry.Cancelled)
        {
            result = IrpResult<IrpCancellation>.Fail("9999", "Invoice is not active.");
        }
        else if (now - entry.Irn.AckDate > TimeSpan.FromHours(24))
        {
            result = IrpResult<IrpCancellation>.Fail("2270", "The allowed cancellation time limit is crossed, you cannot cancel the IRN.");
        }
        else
        {
            ledger.Entries[irn] = entry with { Cancelled = true };
            result = IrpResult<IrpCancellation>.Ok(new IrpCancellation(irn, now));
        }

        await LogAsync("irn.cancel", started, result.Error, context);
        return result;
    }

    public async Task<IrpResult<IrpIrn>> GetByIrnAsync(IrpContext context, string irn, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        var result = ledger.Entries.TryGetValue(irn, out var entry)
            ? IrpResult<IrpIrn>.Ok(entry.Irn)
            : IrpResult<IrpIrn>.Fail("9999", "The simulator holds no such IRN (it forgets them when the server restarts).");
        await LogAsync("irn.get", started, result.Error, context);
        return result;
    }

    public async Task<IrpResult<IrpEwayBill>> GenerateEwayBillAsync(IrpContext context, JsonObject request, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        IrpResult<IrpEwayBill> result;
        var irn = IrpAnswers.Text(request["Irn"]) ?? string.Empty;
        var distance = IrpAnswers.Number(request["Distance"]) ?? 0;
        if (!ledger.Entries.TryGetValue(irn, out var entry) || entry.Cancelled)
        {
            result = IrpResult<IrpEwayBill>.Fail("9999", "An e-way bill is generated only from a live IRN.");
        }
        else if (entry.Irn.EwbNo is not null)
        {
            result = IrpResult<IrpEwayBill>.Fail("4002", "An e-way bill is already generated for this IRN.");
        }
        else if (distance is < 1 or > 4000)
        {
            result = IrpResult<IrpEwayBill>.Fail("9999", "The distance must be from 1 to 4000 km.");
        }
        else if (IrpAnswers.Text(request["TransId"]) is null && IrpAnswers.Text(request["VehNo"]) is null)
        {
            result = IrpResult<IrpEwayBill>.Fail("9999", "Give the transporter's id or the vehicle number.");
        }
        else
        {
            var now = time.GetUtcNow().ToOffset(India);
            var number = 100000000000L + ledger.Next();
            var days = (int)Math.Ceiling(distance / 200m);
            var validTill = new DateTimeOffset(now.Date.AddDays(days - 1).AddHours(23).AddMinutes(59), India);
            var bill = new IrpEwayBill(number, now, validTill);
            ledger.Entries[irn] = entry with { Irn = entry.Irn with { EwbNo = number, EwbDate = now, EwbValidTill = validTill } };
            result = IrpResult<IrpEwayBill>.Ok(bill);
        }

        await LogAsync("ewaybill.generate", started, result.Error, context);
        return result;
    }

    private IrpResult<IrpIrn> Generate(IrpContext context, JsonObject payload)
    {
        var seller = IrpAnswers.Text(payload["SellerDtls"]?["Gstin"]);
        var buyer = IrpAnswers.Text(payload["BuyerDtls"]?["Gstin"]);
        var type = IrpAnswers.Text(payload["DocDtls"]?["Typ"]);
        var number = IrpAnswers.Text(payload["DocDtls"]?["No"]);
        var dateText = IrpAnswers.Text(payload["DocDtls"]?["Dt"]);
        if (seller is null || buyer is null || type is null || number is null
            || !DateOnly.TryParseExact(dateText, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return IrpResult<IrpIrn>.Fail("2172", "The document's supplier, buyer, type, number or date is missing.");
        }

        if (!string.Equals(seller, context.Gstin, StringComparison.OrdinalIgnoreCase))
        {
            return IrpResult<IrpIrn>.Fail("2163", $"The supplier GSTIN {seller} is not the GSTIN these credentials are for.");
        }

        var irn = IrnOf(seller, date, type, number);
        if (ledger.Entries.TryGetValue(irn, out var existing) && !existing.Cancelled)
        {
            return IrpResult<IrpIrn>.Fail(IrpError.DuplicateIrn, "Duplicate IRN", irn);
        }

        if (existing is { Cancelled: true })
        {
            return IrpResult<IrpIrn>.Fail("2150", "The IRN of this document number was cancelled; the portal does not register the number again.", null);
        }

        var now = time.GetUtcNow().ToOffset(India);
        var ack = string.Create(CultureInfo.InvariantCulture, $"{now:yy}{ledger.Next():D13}");
        var items = payload["ItemList"] as JsonArray ?? [];
        var mainHsn = items.OfType<JsonObject>()
            .OrderByDescending(i => decimal.TryParse(IrpAnswers.Text(i["AssAmt"]), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0m)
            .Select(i => IrpAnswers.Text(i["HsnCd"]))
            .FirstOrDefault();
        var total = decimal.TryParse(IrpAnswers.Text(payload["ValDtls"]?["TotInvVal"]), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : 0m;
        var irnDate = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var qrData = new JsonObject
        {
            ["SellerGstin"] = seller,
            ["BuyerGstin"] = buyer,
            ["DocNo"] = number,
            ["DocTyp"] = type,
            ["DocDt"] = dateText,
            ["TotInvVal"] = total,
            ["ItemCnt"] = items.Count,
            ["MainHsnCode"] = mainHsn,
            ["Irn"] = irn,
            ["IrnDt"] = irnDate,
        };
        var signed = new IrpIrn(irn, ack, new DateTimeOffset(now.DateTime.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond)), India),
            Jwt(new JsonObject { ["data"] = payload.ToJsonString(), ["iss"] = "HC360 IRP simulator" }),
            Jwt(new JsonObject { ["data"] = qrData.ToJsonString(), ["iss"] = "HC360 IRP simulator" }));
        ledger.Entries[irn] = new SimulatorIrpLedger.Entry(payload.DeepClone().AsObject(), signed, false, context.Gstin);
        return IrpResult<IrpIrn>.Ok(signed);
    }

    /// <summary>The portal's IRN: SHA-256 of the supplier's GSTIN, the financial year (2026-27), the document type and its number.</summary>
    internal static string IrnOf(string sellerGstin, DateOnly date, string type, string number)
    {
        var start = date.Month >= 4 ? date.Year : date.Year - 1;
        var year = string.Create(CultureInfo.InvariantCulture, $"{start}-{(start + 1) % 100:D2}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sellerGstin.ToUpperInvariant() + year + type + number.ToUpperInvariant()))).ToLowerInvariant();
    }

    /// <summary>A JWT shaped as the portal's (header, payload, signature), with a signature that is the simulator's, not NIC's.</summary>
    private static string Jwt(JsonObject payload)
    {
        static string Part(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Part("""{"alg":"none","typ":"JWT","kid":"HC360-SIMULATOR"}""");
        var body = Part(payload.ToJsonString());
        var signature = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(header + "." + body))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{header}.{body}.{signature}";
    }

    private Task LogAsync(string operation, long started, IrpError? error, IrpContext context) =>
        trace.LogAsync(IntegrationNames.Irp, ProviderKeys.Simulator, operation, error is null, started, error: error is null ? null : $"{error.Code}: {error.Message}",
            response: error is null ? "answered by the training simulator" : null, facilityId: context.FacilityId);
}

/// <summary>
/// The platform's <see cref="IInvoiceRegistrationPortal"/>: reads the facility's <c>integrations.irp</c> settings and
/// passes the call to the provider they name, logging under the facility.
/// </summary>
internal sealed class IrpRouter(IntegrationSettingsReader settings, IEnumerable<IIrpProvider> providers) : IInvoiceRegistrationPortal
{
    public async Task<IrpConnection> ConnectionAsync(Guid facilityId, CancellationToken cancellationToken)
    {
        var config = await settings.ReadAsync<IrpSettings, IrpSecrets>(IntegrationNames.Irp, facilityId, requireEnabled: true, cancellationToken);
        if (config.ActiveProvider is not { } provider)
        {
            return new IrpConnection(false, null, false, config.Problem ?? "The e-invoice portal (IRP) is not connected.");
        }

        return providers.Any(p => p.Keys.Contains(provider))
            ? new IrpConnection(true, provider, TrainingSimulators.IsSimulator(provider), null)
            : new IrpConnection(false, provider, false, $"\"{provider}\" is not an e-invoice portal provider HC360 knows (nic, gsp or simulator).");
    }

    public Task<IrpResult<IrpIrn>> GenerateAsync(Guid facilityId, string sellerGstin, JsonObject payload, CancellationToken cancellationToken) =>
        CallAsync(facilityId, sellerGstin, (p, c) => p.GenerateAsync(c, payload, cancellationToken), cancellationToken);

    public Task<IrpResult<IrpCancellation>> CancelAsync(Guid facilityId, string sellerGstin, string irn, int reason, string remark, CancellationToken cancellationToken) =>
        CallAsync(facilityId, sellerGstin, (p, c) => p.CancelAsync(c, irn, reason, remark, cancellationToken), cancellationToken);

    public Task<IrpResult<IrpIrn>> GetByIrnAsync(Guid facilityId, string sellerGstin, string irn, CancellationToken cancellationToken) =>
        CallAsync(facilityId, sellerGstin, (p, c) => p.GetByIrnAsync(c, irn, cancellationToken), cancellationToken);

    public Task<IrpResult<IrpEwayBill>> GenerateEwayBillAsync(Guid facilityId, string sellerGstin, JsonObject request, CancellationToken cancellationToken) =>
        CallAsync(facilityId, sellerGstin, (p, c) => p.GenerateEwayBillAsync(c, request, cancellationToken), cancellationToken);

    private async Task<IrpResult<T>> CallAsync<T>(
        Guid facilityId, string sellerGstin, Func<IIrpProvider, IrpContext, Task<IrpResult<T>>> call, CancellationToken cancellationToken)
        where T : class
    {
        var config = await settings.ReadAsync<IrpSettings, IrpSecrets>(IntegrationNames.Irp, facilityId, requireEnabled: true, cancellationToken);
        if (config.ActiveProvider is not { } key || config.Settings is not { } values)
        {
            return IrpResult<T>.Fail(IrpError.NotConnected, config.Problem ?? "The e-invoice portal (IRP) is not connected.");
        }

        var provider = providers.FirstOrDefault(p => p.Keys.Contains(key));
        if (provider is null)
        {
            return IrpResult<T>.Fail(IrpError.NotConnected, $"\"{key}\" is not an e-invoice portal provider HC360 knows (nic, gsp or simulator).");
        }

        using var scope = IntegrationTrace.ForFacility(facilityId);
        return await call(provider, new IrpContext(facilityId, sellerGstin.Trim().ToUpperInvariant(), values, config.Secrets));
    }
}
