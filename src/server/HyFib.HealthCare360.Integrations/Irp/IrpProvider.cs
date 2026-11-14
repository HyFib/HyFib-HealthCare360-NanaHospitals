using System.Globalization;
using System.Text.Json.Nodes;

namespace HyFib.HealthCare360.Integrations.Irp;

/// <summary>Who is calling the portal: the facility, the GSTIN its credentials are for, and its settings.</summary>
internal sealed record IrpContext(Guid FacilityId, string Gstin, IrpSettings Settings, IrpSecrets Secrets);

/// <summary>One way of reaching the portal (<see cref="NicIrpClient"/> or <see cref="SimulatorIrp"/>); errors come back as the portal's codes.</summary>
internal interface IIrpProvider
{
    /// <summary>The <c>provider</c> setting values it serves.</summary>
    IReadOnlyCollection<string> Keys { get; }

    Task<IrpResult<IrpIrn>> GenerateAsync(IrpContext context, JsonObject payload, CancellationToken cancellationToken);

    Task<IrpResult<IrpCancellation>> CancelAsync(IrpContext context, string irn, int reason, string remark, CancellationToken cancellationToken);

    Task<IrpResult<IrpIrn>> GetByIrnAsync(IrpContext context, string irn, CancellationToken cancellationToken);

    Task<IrpResult<IrpEwayBill>> GenerateEwayBillAsync(IrpContext context, JsonObject request, CancellationToken cancellationToken);

    Task<IrpResult<IrpEwayBill>> GetEwayBillAsync(IrpContext context, string irn, CancellationToken cancellationToken);
}

/// <summary>Reads the portal's answers (NIC e-invoice API v1.03), whoever passed them on.</summary>
internal static class IrpAnswers
{
    private static readonly TimeSpan India = TimeSpan.FromHours(5.5);

    /// <summary>The portal's times: "yyyy-MM-dd HH:mm:ss", India time.</summary>
    public static DateTimeOffset? Time(JsonNode? node) =>
        Text(node) is { } text
        && DateTime.TryParseExact(text, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd hh:mm:ss tt", "dd/MM/yyyy hh:mm:ss tt", "dd/MM/yyyy HH:mm:ss"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? new DateTimeOffset(time, India)
            : null;

    public static string? Text(JsonNode? node) => node is JsonValue value ? value.ToString() : null;

    public static long? Number(JsonNode? node) =>
        Text(node) is { } text && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;

    /// <summary>
    /// An IRN as the portal gives it (generated, or its details), with its <c>Status</c> (<c>ACT</c> or <c>CNL</c>) and the
    /// time it was cancelled (<c>CancelDate</c>) where the answer says. The details of a cancelled IRN are read for its state
    /// even without the acknowledgement or the QR code; a live IRN needs both.
    /// </summary>
    public static IrpResult<IrpIrn> Irn(JsonObject data)
    {
        var irn = Text(data["Irn"]);
        var ack = Text(data["AckNo"]);
        var ackDate = Time(data["AckDt"]);
        var qr = Text(data["SignedQRCode"]);
        var status = Text(data["Status"])?.Trim().ToUpperInvariant();
        var cancelled = status == IrpIrn.CancelledStatus;
        if (irn is null || (!cancelled && (ack is null || ackDate is null || qr is null)))
        {
            return IrpResult<IrpIrn>.Fail(IrpError.Unreachable, "The portal's answer did not hold the IRN, its acknowledgement and the signed QR code.");
        }

        return IrpResult<IrpIrn>.Ok(new IrpIrn(irn.ToLowerInvariant(), ack ?? string.Empty, ackDate ?? DateTimeOffset.MinValue,
            Text(data["SignedInvoice"]) ?? string.Empty, qr ?? string.Empty, Number(data["EwbNo"]), Time(data["EwbDt"]), Time(data["EwbValidTill"]),
            string.IsNullOrEmpty(status) ? null : status, Time(data["CancelDate"])));
    }

    /// <summary>
    /// An e-way bill as the portal gives it (generated from an IRN, or read back by it), with its <c>Status</c> (<c>ACT</c>
    /// or <c>CNL</c>) where the answer says. A cancelled one is read for its state even without its date; any other needs it.
    /// </summary>
    public static IrpResult<IrpEwayBill> EwayBill(JsonObject data)
    {
        var number = Number(data["EwbNo"]);
        var date = Time(data["EwbDt"]);
        var status = Text(data["Status"])?.Trim().ToUpperInvariant();
        if (number is null || (date is null && status != IrpIrn.CancelledStatus))
        {
            return IrpResult<IrpEwayBill>.Fail(IrpError.Unreachable, "The portal's answer did not hold the e-way bill's number and date.");
        }

        return IrpResult<IrpEwayBill>.Ok(new IrpEwayBill(number.Value, date ?? DateTimeOffset.MinValue, Time(data["EwbValidTill"]),
            string.IsNullOrEmpty(status) ? null : status));
    }

    /// <summary>The first error of a refused call, and the IRN the portal already holds for a duplicate (its <c>InfoDtls</c>).</summary>
    public static IrpError Error(JsonObject answer)
    {
        var first = (answer["ErrorDetails"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        var code = Text(first?["ErrorCode"]) ?? "unknown";
        var message = Text(first?["ErrorMessage"]) ?? "The portal refused the request without saying why.";
        string? existing = null;
        foreach (var info in (answer["InfoDtls"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (Text(info["InfCd"]) == "DUPIRN" && info["Desc"] is JsonObject desc)
            {
                existing = Text(desc["Irn"])?.ToLowerInvariant();
            }
        }

        return new IrpError(code, message, existing);
    }
}
