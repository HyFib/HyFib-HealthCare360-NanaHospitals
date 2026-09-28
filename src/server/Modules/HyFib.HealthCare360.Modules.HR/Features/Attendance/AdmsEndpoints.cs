using System.Text;
using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Services;

namespace HyFib.HealthCare360.Modules.HR.Features.Attendance;

/// <summary>
/// ZKTeco ADMS ("iclock") push protocol endpoints that terminals call directly (INT-BIO). They must be anonymous —
/// the protocol has no credentials — so only registered, active serial numbers (optionally IP-pinned) are served,
/// requests are rate-limited and bodies are size-capped. Replies are the protocol's plain text.
/// </summary>
internal static class AdmsEndpoints
{
    /// <summary>ATTLOG uploads are small (a few hundred bytes per punch); anything larger is refused.</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    private const string AttendanceTable = "ATTLOG";
    private const string PlainText = "text/plain";

    public static void Map(IEndpointRouteBuilder app)
    {
        var iclock = app.MapGroup("/iclock")
            .WithTags("Biometric devices")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Device);

        iclock.MapGet("/cdata", Handshake);
        iclock.MapPost("/cdata", Upload);
        iclock.MapGet("/getrequest", GetRequest);
    }

    /// <summary><c>GET /iclock/cdata?SN=…&amp;options=all</c> — the terminal asks what to send.</summary>
    private static async Task<IResult> Handshake(HttpRequest request, BiometricIngestionService ingestion, CancellationToken ct)
    {
        var device = await ingestion.FindDeviceAsync(request.Query["SN"].ToString(), request.HttpContext.Connection.RemoteIpAddress, ct);
        if (device is null)
        {
            return Refused();
        }

        await ingestion.RecordSeenAsync(device, request.HttpContext.Connection.RemoteIpAddress, ct);
        return TypedResults.Text(AdmsProtocol.HandshakeResponse(device.SerialNumber, device.AttLogStamp), PlainText);
    }

    /// <summary><c>POST /iclock/cdata?SN=…&amp;table=ATTLOG&amp;Stamp=…</c> — attendance records, one per line.</summary>
    private static async Task<IResult> Upload(HttpRequest request, BiometricIngestionService ingestion, CancellationToken ct)
    {
        var device = await ingestion.FindDeviceAsync(request.Query["SN"].ToString(), request.HttpContext.Connection.RemoteIpAddress, ct);
        if (device is null)
        {
            return Refused();
        }

        var body = await ReadBodyAsync(request, ct);
        if (body is null)
        {
            return TypedResults.Text("ERROR: payload too large", PlainText, statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        if (!string.Equals(request.Query["table"].ToString(), AttendanceTable, StringComparison.OrdinalIgnoreCase))
        {
            // Operation logs, photos and user records are acknowledged but never stored (data minimisation).
            return TypedResults.Text(AdmsProtocol.Acknowledged, PlainText);
        }

        var parsed = ZkTecoAttLogParser.Parse(body);
        var candidates = parsed.Records
            .Select(r => new PunchCandidate(r.Pin, HrClock.ToUtc(r.LocalTime), r.Direction, r.StatusCode, r.VerifyMode, r.WorkCode))
            .ToList();
        var result = await ingestion.IngestAsync(device, candidates, PunchSource.Adms, ct);
        await ingestion.RecordPushAsync(device, request.HttpContext.Connection.RemoteIpAddress, request.Query["Stamp"].ToString(), ct);

        // Duplicates count as received so the terminal advances its stamp instead of resending forever.
        return TypedResults.Text(AdmsProtocol.UploadAccepted(result.Accepted + result.Duplicates), PlainText);
    }

    /// <summary><c>GET /iclock/getrequest?SN=…</c> — command poll; HC360 never sends commands to terminals.</summary>
    private static async Task<IResult> GetRequest(HttpRequest request, BiometricIngestionService ingestion, CancellationToken ct)
    {
        var device = await ingestion.FindDeviceAsync(request.Query["SN"].ToString(), request.HttpContext.Connection.RemoteIpAddress, ct);
        if (device is null)
        {
            return Refused();
        }

        await ingestion.RecordSeenAsync(device, request.HttpContext.Connection.RemoteIpAddress, ct);
        return TypedResults.Text(AdmsProtocol.NoCommand, PlainText);
    }

    private static IResult Refused() => TypedResults.Text("ERROR: device not registered", PlainText, statusCode: StatusCodes.Status403Forbidden);

    /// <summary>Reads the text body, or returns null when it exceeds <see cref="MaxBodyBytes"/>.</summary>
    private static async Task<string?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
