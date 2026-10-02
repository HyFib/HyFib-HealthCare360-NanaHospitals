using System.Net;
using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Integrations.Pacs.Worklist;

/// <summary>
/// The DICOM modality worklist server (<c>integrations.mwl</c>, one row for all facilities): where it listens, how it
/// answers, and the modalities allowed to ask (IMG-01, IMG-02, INT-PACS). It is off until an administrator turns it on.
/// </summary>
internal sealed record WorklistSettings
{
    public const string Key = "integrations.mwl";

    public bool Enabled { get; init; }

    /// <summary>Interface to listen on; use the imaging network's address to keep the port off other networks.</summary>
    public string BindAddress { get; init; } = "0.0.0.0";

    /// <summary>TCP port; 11112 is the port registered for DICOM.</summary>
    public int Port { get; init; } = 11112;

    /// <summary>The server's own AE title, which modalities call.</summary>
    public string AeTitle { get; init; } = "HC360MWL";

    /// <summary><c>ISO_IR 100</c> (Latin-1, the default), <c>ISO_IR 192</c> (UTF-8) or empty (plain ASCII).</summary>
    public string? CharacterSet { get; init; } = WorklistConfig.Latin1;

    /// <summary>Associations served at once; more are refused until one ends.</summary>
    public int MaxAssociations { get; init; } = 10;

    /// <summary>An association that sends nothing for this long is closed.</summary>
    public int IdleTimeoutSeconds { get; init; } = 60;

    /// <summary>Days before today whose studies are still offered (unscheduled orders and arrivals wait a day or two).</summary>
    public int DaysBack { get; init; } = 3;

    /// <summary>Days after today whose appointments are offered.</summary>
    public int DaysAhead { get; init; } = 1;

    /// <summary>Most entries in one answer.</summary>
    public int MaxResults { get; init; } = 500;

    /// <summary>The modalities allowed to ask (the allow-list).</summary>
    public IReadOnlyList<WorklistModalitySettings>? Modalities { get; init; }
}

/// <summary>One modality allowed to query the worklist (an element of <c>integrations.mwl.modalities</c>).</summary>
internal sealed record WorklistModalitySettings
{
    /// <summary>The modality's own (calling) AE title.</summary>
    public string AeTitle { get; init; } = string.Empty;

    public string? Name { get; init; }

    /// <summary>The modality's IP addresses; at least one is required.</summary>
    public IReadOnlyList<string>? AllowedIps { get; init; }

    /// <summary>The facility whose studies it sees, by code (for example NH); may be left out when there is one facility.</summary>
    public string? Facility { get; init; }

    /// <summary>The kinds of study it performs, as DICOM codes: CR or DX (X-ray), CT, MR, US, MG, RF.</summary>
    public IReadOnlyList<string>? Modalities { get; init; }

    public bool Enabled { get; init; } = true;
}

/// <summary>A modality on the allow-list, checked and resolved to its facility.</summary>
internal sealed record WorklistPeer(string AeTitle, string? Name, IReadOnlySet<IPAddress> Addresses, Guid FacilityId, IReadOnlyList<string> Modalities);

/// <summary>Where the server listens; a change restarts the listener (allow-list changes do not).</summary>
internal sealed record WorklistListener(string BindAddress, int Port, string AeTitle, int MaxAssociations);

/// <summary>
/// The worklist server's settings as checked and in force: limits clamped to safe bounds, modalities resolved to their
/// facility, and every entry that cannot be used listed in <see cref="Problems"/> (and left out) instead of failing the
/// whole server.
/// </summary>
internal sealed partial record WorklistConfig(
    bool Enabled,
    WorklistListener? Listener,
    string CharacterSet,
    TimeSpan IdleTimeout,
    int DaysBack,
    int DaysAhead,
    int MaxResults,
    IReadOnlyList<WorklistPeer> Peers,
    IReadOnlyList<string> Problems)
{
    public const string Latin1 = "ISO_IR 100";
    public const string Utf8 = "ISO_IR 192";

    /// <summary>Modality codes HC360 has studies for; XR is accepted as another name for CR.</summary>
    private static readonly HashSet<string> KnownModalities = new(StringComparer.Ordinal) { "CR", "DX", "CT", "MR", "US", "MG", "RF" };

    public static WorklistConfig Off { get; } = new(false, null, Latin1, TimeSpan.FromSeconds(60), 3, 1, 500, [], []);

    public string AeTitle => Listener?.AeTitle ?? string.Empty;

    /// <summary>The modality with this calling AE title (compared without regard to case), or null.</summary>
    public WorklistPeer? Peer(string? callingAe)
    {
        var title = callingAe?.Trim();
        return string.IsNullOrEmpty(title) ? null : Peers.FirstOrDefault(p => string.Equals(p.AeTitle, title, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when some allowed modality has this address.</summary>
    public bool IsKnownAddress(IPAddress address) => Peers.Any(p => p.Addresses.Contains(address));

    public static WorklistConfig From(WorklistSettings? settings, IReadOnlyCollection<(Guid Id, string Code)> facilities)
    {
        if (settings is null)
        {
            return Off;
        }

        var problems = new List<string>();
        var aeTitle = settings.AeTitle?.Trim() ?? string.Empty;
        WorklistListener? listener = null;
        if (!IsAeTitle(aeTitle))
        {
            problems.Add("The server's AE title must have 1 to 16 letters, digits, spaces, '.', '-' or '_'. The worklist is not served.");
        }
        else if (settings.Port is < 1 or > 65535)
        {
            problems.Add("The port must be from 1 to 65535. The worklist is not served.");
        }
        else if (!IPAddress.TryParse(settings.BindAddress?.Trim(), out _))
        {
            problems.Add($"The listen address \"{Clip(settings.BindAddress)}\" is not an IP address. The worklist is not served.");
        }
        else
        {
            listener = new WorklistListener(settings.BindAddress!.Trim(), settings.Port, aeTitle, Math.Clamp(settings.MaxAssociations, 1, 64));
        }

        var characterSet = settings.CharacterSet?.Trim() ?? string.Empty;
        if (characterSet is not (Latin1 or Utf8 or ""))
        {
            problems.Add($"The character set \"{Clip(characterSet)}\" is not supported; {Latin1} is used. Use {Latin1}, {Utf8} or leave it empty.");
            characterSet = Latin1;
        }

        var peers = new List<WorklistPeer>();
        foreach (var modality in settings.Modalities ?? [])
        {
            if (!modality.Enabled)
            {
                continue;
            }

            if (Peer(modality, facilities, out var problem) is { } peer)
            {
                if (peers.Any(p => string.Equals(p.AeTitle, peer.AeTitle, StringComparison.OrdinalIgnoreCase)))
                {
                    problems.Add($"Modality {peer.AeTitle} is listed twice; only the first entry is used.");
                }
                else if (string.Equals(peer.AeTitle, aeTitle, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"Modality {peer.AeTitle} has the server's own AE title; give the modality its own.");
                }
                else
                {
                    peers.Add(peer);
                }
            }
            else
            {
                problems.Add(problem!);
            }
        }

        return new WorklistConfig(
            settings.Enabled,
            listener,
            characterSet,
            TimeSpan.FromSeconds(Math.Clamp(settings.IdleTimeoutSeconds, 10, 3600)),
            Math.Clamp(settings.DaysBack, 0, 30),
            Math.Clamp(settings.DaysAhead, 0, 30),
            Math.Clamp(settings.MaxResults, 1, 1000),
            peers,
            problems);
    }

    /// <summary>An AE title: 1–16 characters (letters, digits, inner spaces, '.', '-', '_'); case is not significant.</summary>
    public static bool IsAeTitle(string? value) => value is not null && AeTitlePattern().IsMatch(value);

    private static WorklistPeer? Peer(WorklistModalitySettings modality, IReadOnlyCollection<(Guid Id, string Code)> facilities, out string? problem)
    {
        var title = modality.AeTitle?.Trim() ?? string.Empty;
        var label = IsAeTitle(title) ? $"Modality {title}" : $"Modality \"{Clip(title)}\"";
        problem = null;
        if (!IsAeTitle(title))
        {
            problem = $"{label} is ignored: an AE title has 1 to 16 letters, digits, spaces, '.', '-' or '_'.";
            return null;
        }

        var addresses = (modality.AllowedIps ?? [])
            .Select(a => IPAddress.TryParse(a?.Trim(), out var ip) ? Normalize(ip) : null)
            .OfType<IPAddress>()
            .ToHashSet();
        if (addresses.Count == 0)
        {
            problem = $"{label} is ignored: list its IP address in allowedIps.";
            return null;
        }

        Guid? facility = null;
        if (string.IsNullOrWhiteSpace(modality.Facility))
        {
            facility = facilities.Count == 1 ? facilities.First().Id : null;
            problem = facility is null ? $"{label} is ignored: say which facility's studies it sees (facility: the facility code)." : null;
        }
        else
        {
            var code = modality.Facility.Trim();
            var id = Guid.TryParse(code, out var parsed) ? parsed : Guid.Empty;
            facility = facilities.Where(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase) || f.Id == id)
                .Select(f => (Guid?)f.Id).FirstOrDefault();
            problem = facility is null ? $"{label} is ignored: there is no active facility {Clip(code)}." : null;
        }

        if (facility is not { } facilityId)
        {
            return null;
        }

        var codes = (modality.Modalities ?? [])
            .Select(m => m?.Trim().ToUpperInvariant() is "XR" ? "CR" : m?.Trim().ToUpperInvariant())
            .OfType<string>()
            .Where(KnownModalities.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (codes.Count == 0)
        {
            problem = $"{label} is ignored: list the kinds of study it does in modalities (CR or DX for X-ray, CT, MR, US, MG, RF).";
            return null;
        }

        return new WorklistPeer(title, string.IsNullOrWhiteSpace(modality.Name) ? null : modality.Name.Trim(), addresses, facilityId, codes);
    }

    /// <summary>An IPv4 address seen through an IPv6 socket is the IPv4 address.</summary>
    public static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static string Clip(string? value) =>
        new string((value ?? string.Empty).Where(c => !char.IsControl(c)).Take(40).ToArray());

    [GeneratedRegex("^[A-Za-z0-9._-](?:[A-Za-z0-9 ._-]{0,14}[A-Za-z0-9._-])?$")]
    private static partial Regex AeTitlePattern();
}
