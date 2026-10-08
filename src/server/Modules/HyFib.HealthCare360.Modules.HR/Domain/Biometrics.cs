using System.Net;
using System.Net.Sockets;

namespace HyFib.HealthCare360.Modules.HR.Domain;

/// <summary>
/// A registered attendance terminal (ZKTeco-compatible, INT-BIO). Devices authenticate the ADMS push protocol by
/// serial number only (a limitation of the protocol), so only registered, active serials are accepted, optionally
/// pinned to one IP address. The JSON punch API additionally requires a device key, stored hashed.
/// </summary>
public sealed class BiometricDevice : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string SerialNumber { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Location { get; set; }
    public Guid? DepartmentId { get; set; }
    public string? Model { get; set; }

    /// <summary>When set, pushes from any other address are refused (devices sit on a fixed-IP device VLAN).</summary>
    public string? AllowedIpAddress { get; set; }

    /// <summary>SHA-256 of the device key for the JSON punch API (the key itself is shown once).</summary>
    [SensitiveData]
    public string? ApiKeyHash { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastPushAt { get; set; }
    public DateTimeOffset? LastHandshakeAt { get; set; }
    public string? LastIpAddress { get; set; }

    /// <summary>ADMS "ATTLOGStamp" echoed back at handshake so the terminal resumes where it left off.</summary>
    public string? AttLogStamp { get; set; }

    /// <summary>Whether the terminal sends its punches (ADMS or the punch API) or HC360 reads them from it.</summary>
    public BiometricConnection Connection { get; set; }

    /// <summary>A terminal HC360 reads: its IP address on the device network.</summary>
    public string? PullAddress { get; set; }

    /// <summary>A terminal HC360 reads: its TCP port (4370 on ZKTeco and eSSL terminals).</summary>
    public int PullPort { get; set; } = DefaultPullPort;

    public int PullEveryMinutes { get; set; } = DefaultPullMinutes;

    /// <summary>When HC360 last tried to read the terminal, and why it could not (null when it could).</summary>
    public DateTimeOffset? LastPullAt { get; set; }

    public string? LastPullError { get; set; }

    /// <summary>The latest punch read from the terminal: later reads look only a little before it.</summary>
    public DateTimeOffset? PulledUpTo { get; set; }

    /// <summary>How far the terminal's clock was ahead (positive) or behind when it was last read.</summary>
    public int? ClockOffsetSeconds { get; set; }

    /// <summary>When HR was told the terminal had gone quiet; cleared when it is heard from again.</summary>
    public DateTimeOffset? OfflineSince { get; set; }

    public const int DefaultPullPort = 4370;
    public const int DefaultPullMinutes = 10;
    public const int MaxPullMinutes = 1440;
    public const int MaxPullAddressLength = 45;

    /// <summary>
    /// A terminal is read only at an IP address on a network, never at a host name, this server's own address, a
    /// link-local address (where cloud metadata services answer) or a multicast or broadcast address.
    /// </summary>
    public static bool IsAllowedPullAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > MaxPullAddressLength || !IPAddress.TryParse(address.Trim(), out var ip))
        {
            return false;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast)
            || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal)
        {
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return bytes[0] is not (0 or >= 224) && !(bytes[0] == 169 && bytes[1] == 254);
        }

        return true;
    }
}

public enum BiometricConnection
{
    /// <summary>The terminal sends its punches over ADMS or the punch API.</summary>
    Push = 0,

    /// <summary>HC360 reads the terminal over the network (ZKTeco protocol, TCP port 4370).</summary>
    Pull,
}

public enum PunchDirection
{
    Unknown = 0,
    In,
    Out,
}

public enum PunchSource
{
    Adms = 0,
    DeviceApi,
    Regularisation,

    /// <summary>Read from the terminal by HC360.</summary>
    Pull,
}

/// <summary>
/// A raw punch exactly as received. Append-only and high volume, so it is not data-audited; punches from PINs not
/// yet mapped to an employee are kept (EmployeeId null) and linked when HR assigns the PIN.
/// </summary>
[NotAudited]
public sealed class AttendancePunch : Entity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid? EmployeeId { get; set; }
    public Guid? BiometricDeviceId { get; set; }
    public string Pin { get; set; } = string.Empty;

    /// <summary>UTC instant (device local time converted with the facility time zone).</summary>
    public DateTimeOffset PunchedAt { get; set; }

    public PunchDirection Direction { get; set; }

    /// <summary>Raw terminal status (0 check-in, 1 check-out, 2 break-out, 3 break-in, 4 OT-in, 5 OT-out).</summary>
    public int? StatusCode { get; set; }

    /// <summary>Raw verification mode (1 fingerprint, 15 face, 0 password, 2 card…).</summary>
    public int? VerifyMode { get; set; }

    public string? WorkCode { get; set; }
    public PunchSource Source { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
