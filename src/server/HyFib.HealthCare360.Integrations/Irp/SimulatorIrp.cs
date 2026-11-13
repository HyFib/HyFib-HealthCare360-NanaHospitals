using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace HyFib.HealthCare360.Integrations.Irp;

/// <summary>The calls the IRP simulator answers, as its log names them, for <see cref="SimulatorIrpLedger.Fail"/>.</summary>
internal static class SimulatorIrpOperations
{
    public const string Generate = "irn.generate";
    public const string Cancel = "irn.cancel";
    public const string Get = "irn.get";
    public const string EwayBill = "ewaybill.generate";
}

/// <summary>How the training portal fails a call, to practise and test what HC360 does when the portal's answer is missing.</summary>
internal enum SimulatorIrpFault
{
    /// <summary>The portal does what was asked, and its answer is lost on the way back.</summary>
    LoseAnswer,

    /// <summary>The portal is not reached: nothing is done.</summary>
    Down,
}

/// <summary>
/// What the IRP simulator has registered since the server started (it keeps nothing across restarts), and the faults set
/// on its next calls.
/// </summary>
internal sealed class SimulatorIrpLedger
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<SimulatorIrpFault>> _faults = new(StringComparer.Ordinal);

    public sealed record Entry(JsonObject Payload, IrpIrn Irn, bool Cancelled, string? Gstin, DateTimeOffset? CancelledAt = null);

    public ConcurrentDictionary<string, Entry> Entries { get; } = new(StringComparer.Ordinal);

    private long _sequence;

    public long Next() => Interlocked.Increment(ref _sequence);

    /// <summary>
    /// Makes the next <paramref name="times"/> calls of <paramref name="operation"/> (<see cref="SimulatorIrpOperations"/>)
    /// fail as <paramref name="fault"/> says. Disposing the handle clears what is left of the operation's faults.
    /// </summary>
    public IDisposable Fail(string operation, SimulatorIrpFault fault, int times = 1)
    {
        var queue = _faults.GetOrAdd(operation, _ => new ConcurrentQueue<SimulatorIrpFault>());
        for (var i = 0; i < times; i++)
        {
            queue.Enqueue(fault);
        }

        return new Faults(() => _faults.TryRemove(operation, out _));
    }

    /// <summary>The fault set on the next call of <paramref name="operation"/>, taken; null when it goes as usual.</summary>
    public SimulatorIrpFault? TakeFault(string operation) =>
        _faults.TryGetValue(operation, out var queue) && queue.TryDequeue(out var fault) ? fault : null;

    private sealed class Faults(Action clear) : IDisposable
    {
        public void Dispose() => clear();
    }
}

/// <summary>
/// The training Invoice Registration Portal (<c>simulator</c>), which only a training or test server can use (a live
/// server refuses it before any call, <see cref="IntegrationSettingsReader"/>). It answers as the portal does in the cases
/// HC360 relies on: the IRN is the portal's own hash (SHA-256 of the supplier's GSTIN, the financial year, the document
/// type and its number), so the same document asked for twice is a duplicate (2150) that names the IRN it holds; the signed
/// QR code carries the document's details; an IRN is cancelled only within 24 hours of its generation (2270 after); its
/// details say whether it is active (<c>ACT</c>) or cancelled (<c>CNL</c>, and when), to the GSTIN it belongs to only; and
/// an e-way bill is generated from a live IRN, valid one day for each 200 km. A call can be made to fail as the portal's
/// do (<see cref="SimulatorIrpLedger.Fail"/>): its answer lost after the work is done, or the portal not reached at all.
/// Its signatures are not the portal's: an IRN from it is not a real one, and the simulator sweep lists any found on a live
/// server.
/// </summary>
internal sealed class SimulatorIrp(SimulatorIrpLedger ledger, IntegrationTrace trace, TimeProvider time) : IIrpProvider
{
    private static readonly TimeSpan India = TimeSpan.FromHours(5.5);

    public IReadOnlyCollection<string> Keys { get; } = [ProviderKeys.Simulator];

    public Task<IrpResult<IrpIrn>> GenerateAsync(IrpContext context, JsonObject payload, CancellationToken cancellationToken) =>
        AnswerAsync(SimulatorIrpOperations.Generate, context, () => Generate(context, payload));

    public Task<IrpResult<IrpCancellation>> CancelAsync(IrpContext context, string irn, int reason, string remark, CancellationToken cancellationToken) =>
        AnswerAsync(SimulatorIrpOperations.Cancel, context, () => Cancel(irn));

    /// <summary>The IRN's details as the portal gives them to the GSTIN it belongs to: active (<c>ACT</c>) or cancelled (<c>CNL</c>, with when).</summary>
    public Task<IrpResult<IrpIrn>> GetByIrnAsync(IrpContext context, string irn, CancellationToken cancellationToken) =>
        AnswerAsync(SimulatorIrpOperations.Get, context, () => Details(context, irn));

    public Task<IrpResult<IrpEwayBill>> GenerateEwayBillAsync(IrpContext context, JsonObject request, CancellationToken cancellationToken) =>
        AnswerAsync(SimulatorIrpOperations.EwayBill, context, () => EwayBill(request));

    /// <summary>
    /// Answers a call, or fails it as the fault set on its operation says: not reached, nothing is done; the answer lost, the
    /// work is done and the caller hears nothing. Either way the caller gets what an unanswered call to the portal gives.
    /// </summary>
    private async Task<IrpResult<T>> AnswerAsync<T>(string operation, IrpContext context, Func<IrpResult<T>> call)
        where T : class
    {
        var started = trace.Start();
        var fault = ledger.TakeFault(operation);
        IrpResult<T> result;
        if (fault == SimulatorIrpFault.Down)
        {
            result = IrpResult<T>.Fail(IrpError.Unreachable, "The e-invoice portal could not be reached (the training simulator was set to be down).");
        }
        else
        {
            result = call();
            if (fault == SimulatorIrpFault.LoseAnswer)
            {
                result = IrpResult<T>.Fail(IrpError.Unreachable,
                    "The e-invoice portal did not answer in time (the training simulator answered, and was set to lose its answer).");
            }
        }

        await LogAsync(operation, started, result.Error, context);
        return result;
    }

    private IrpResult<IrpCancellation> Cancel(string irn)
    {
        var now = time.GetUtcNow();
        if (!ledger.Entries.TryGetValue(irn, out var entry))
        {
            return IrpResult<IrpCancellation>.Fail("9999", "The simulator holds no such IRN (it forgets them when the server restarts).");
        }

        if (entry.Cancelled)
        {
            return IrpResult<IrpCancellation>.Fail("9999", "Invoice is not active.");
        }

        if (now - entry.Irn.AckDate > TimeSpan.FromHours(24))
        {
            return IrpResult<IrpCancellation>.Fail("2270", "The allowed cancellation time limit is crossed, you cannot cancel the IRN.");
        }

        ledger.Entries[irn] = entry with { Cancelled = true, CancelledAt = now };
        return IrpResult<IrpCancellation>.Ok(new IrpCancellation(irn, now));
    }

    private IrpResult<IrpIrn> Details(IrpContext context, string irn)
    {
        if (!ledger.Entries.TryGetValue(irn, out var entry))
        {
            return IrpResult<IrpIrn>.Fail("9999", "The simulator holds no such IRN (it forgets them when the server restarts).");
        }

        if (!string.Equals(entry.Gstin, context.Gstin, StringComparison.OrdinalIgnoreCase))
        {
            return IrpResult<IrpIrn>.Fail("9999", "The IRN is not one of the GSTIN these credentials are for.");
        }

        return IrpResult<IrpIrn>.Ok(entry.Irn with
        {
            Status = entry.Cancelled ? IrpIrn.CancelledStatus : IrpIrn.ActiveStatus,
            CancelledAt = entry.CancelledAt,
        });
    }

    private IrpResult<IrpEwayBill> EwayBill(JsonObject request)
    {
        var irn = IrpAnswers.Text(request["Irn"]) ?? string.Empty;
        var distance = IrpAnswers.Number(request["Distance"]) ?? 0;
        if (!ledger.Entries.TryGetValue(irn, out var entry) || entry.Cancelled)
        {
            return IrpResult<IrpEwayBill>.Fail("9999", "An e-way bill is generated only from a live IRN.");
        }

        if (entry.Irn.EwbNo is not null)
        {
            return IrpResult<IrpEwayBill>.Fail("4002", "An e-way bill is already generated for this IRN.");
        }

        if (distance is < 1 or > 4000)
        {
            return IrpResult<IrpEwayBill>.Fail("9999", "The distance must be from 1 to 4000 km.");
        }

        if (IrpAnswers.Text(request["TransId"]) is null && IrpAnswers.Text(request["VehNo"]) is null)
        {
            return IrpResult<IrpEwayBill>.Fail("9999", "Give the transporter's id or the vehicle number.");
        }

        var now = time.GetUtcNow().ToOffset(India);
        var number = 100000000000L + ledger.Next();
        var days = (int)Math.Ceiling(distance / 200m);
        var validTill = new DateTimeOffset(now.Date.AddDays(days - 1).AddHours(23).AddMinutes(59), India);
        ledger.Entries[irn] = entry with { Irn = entry.Irn with { EwbNo = number, EwbDate = now, EwbValidTill = validTill } };
        return IrpResult<IrpEwayBill>.Ok(new IrpEwayBill(number, now, validTill));
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
            Jwt(new JsonObject { ["data"] = qrData.ToJsonString(), ["iss"] = "HC360 IRP simulator" }), Status: IrpIrn.ActiveStatus);
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
