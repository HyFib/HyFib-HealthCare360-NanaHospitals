namespace HyFib.HealthCare360.WindowsInterop.Discovery;

/// <summary>
/// The Server Node's mDNS/DNS-SD advertisement (SRS DED-02) as the server writes it and HC360 Desktop reads it when it
/// browses for servers (DED-03): the service type, the instance name and the TXT record's keys and formats. One copy, so
/// the desktop reads what the server writes.
/// </summary>
public static class ServerAdvertisement
{
    /// <summary>The DNS-SD service HC360 is advertised as, in the <c>local</c> domain.</summary>
    public const string ServiceType = "_hc360._tcp";

    /// <summary>The server's version, without build metadata.</summary>
    public const string VersionKey = "ver";

    /// <summary>The code of the hospital's facility.</summary>
    public const string FacilityKey = "fac";

    /// <summary>
    /// The SHA-256 fingerprint of the hospital CA, which workstations pin at enrolment (DED-05): 64 hexadecimal characters,
    /// or empty when the server has no CA of its own (an administrator-supplied certificate).
    /// </summary>
    public const string FingerprintKey = "fp";

    /// <summary>The server's machine name, which workstations connect to: it survives DHCP changes, unlike its addresses.</summary>
    public const string HostKey = "host";

    /// <summary>Where the staff app is on the server.</summary>
    public const string PathKey = "path";

    /// <summary>The length of a SHA-256 fingerprint in hexadecimal.</summary>
    private const int FingerprintLength = 64;

    /// <summary>The instance name of the server on the machine <paramref name="machineName"/>.</summary>
    public static string InstanceName(string machineName) => $"HC360-{machineName}";

    /// <summary>The TXT record's properties, in the order the server advertises them.</summary>
    /// <param name="version">The server's informational version; build metadata (after <c>+</c>) is left out.</param>
    public static IReadOnlyList<KeyValuePair<string, string>> Properties(string version, string facilityCode, string? caFingerprint, string hostName)
    {
        ArgumentNullException.ThrowIfNull(version);
        return
        [
            new(VersionKey, version.Split('+')[0]),
            new(FacilityKey, facilityCode),
            new(FingerprintKey, caFingerprint ?? string.Empty),
            new(HostKey, hostName),
            new(PathKey, "/"),
        ];
    }

    /// <summary>
    /// What a server's TXT record says, from its strings (<c>key=value</c>), as HC360 Desktop reads it: keys in any case,
    /// the first of a repeated key, a host name only when there is one, and the fingerprint only when it has a
    /// fingerprint's length, in upper case.
    /// </summary>
    public static AdvertisedProperties Read(IEnumerable<string> strings)
    {
        var txt = strings
            .Select(s => s.Split('=', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First()[1], StringComparer.OrdinalIgnoreCase);

        return new AdvertisedProperties(
            txt.GetValueOrDefault(VersionKey),
            txt.GetValueOrDefault(FacilityKey),
            txt.TryGetValue(FingerprintKey, out var fingerprint) && fingerprint.Length == FingerprintLength ? fingerprint.ToUpperInvariant() : null,
            txt.TryGetValue(HostKey, out var host) && !string.IsNullOrWhiteSpace(host) ? host : null);
    }
}

/// <summary>What a Server Node's TXT record says (<see cref="ServerAdvertisement.Read"/>).</summary>
public sealed record AdvertisedProperties(string? Version, string? FacilityCode, string? CaFingerprint, string? HostName);
