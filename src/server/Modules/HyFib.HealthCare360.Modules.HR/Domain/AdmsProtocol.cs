namespace HyFib.HealthCare360.Modules.HR.Domain;

/// <summary>
/// Plain-text replies of the ZKTeco ADMS ("iclock") push protocol. The handshake deliberately asks only for
/// attendance logs (<c>TransFlag=TransData AttLog</c>) and suppresses operation logs and photos, so fingerprint
/// templates, face images and user pictures never leave the terminal (DPDP data minimisation).
/// </summary>
public static class AdmsProtocol
{
    /// <summary>Reply to <c>GET /iclock/getrequest</c> when no command is pending (HC360 never pushes commands).</summary>
    public const string NoCommand = "OK";

    /// <summary>Reply acknowledging tables HC360 does not store (OPERLOG, ATTPHOTO…) so the terminal stops resending.</summary>
    public const string Acknowledged = "OK";

    public const int MaxSerialLength = 64;

    /// <summary>Reply to the <c>GET /iclock/cdata?SN=…&amp;options=all</c> handshake.</summary>
    public static string HandshakeResponse(string serialNumber, string? attLogStamp) => string.Join('\n',
        $"GET OPTION FROM: {serialNumber}",
        $"ATTLOGStamp={(IsValidStamp(attLogStamp) ? attLogStamp : "None")}",
        "OPERLOGStamp=9999",
        "ATTPHOTOStamp=None",
        "ErrorDelay=60",
        "Delay=30",
        "TransTimes=00:00;14:05",
        "TransInterval=1",
        "TransFlag=TransData AttLog",
        "Realtime=1",
        "Encrypt=None") + "\n";

    /// <summary>Reply to an ATTLOG upload: the terminal advances its stamp when it sees "OK".</summary>
    public static string UploadAccepted(int count) => $"OK: {Math.Max(count, 0)}";

    public static bool IsValidSerial(string? serial) =>
        !string.IsNullOrWhiteSpace(serial) && serial.Length <= MaxSerialLength && serial.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Stamps are opaque counters issued by the terminal; only digits are accepted and echoed.</summary>
    public static bool IsValidStamp(string? stamp) =>
        !string.IsNullOrEmpty(stamp) && stamp.Length <= 20 && stamp.All(char.IsAsciiDigit);
}
