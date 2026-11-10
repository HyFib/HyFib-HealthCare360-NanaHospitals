using HyFib.HealthCare360.WindowsInterop.Trust;
using Serilog;

namespace HyFib.HealthCare360.Desktop.Services;

/// <summary>
/// Finds the hospital server at another address when the configured one stops answering (SRS DED-03): an address it
/// answered on before, this computer for an all-in-one installation, or a server on the network that announces the
/// pinned CA. An address is used only once the server answers there over a connection pinned to the hospital CA, so no
/// other machine can take the server's place.
/// </summary>
internal static class ServerFallback
{
    /// <summary>How many addresses <see cref="DesktopSettings.ServerAddresses"/> keeps.</summary>
    public const int AddressesKept = 8;

    /// <summary>A server address as it is stored: scheme, host and port.</summary>
    public static string Normalize(Uri server) => server.GetLeftPart(UriPartial.Authority) + "/";

    /// <summary><paramref name="addresses"/> with <paramref name="server"/> first, each once, at most <see cref="AddressesKept"/>.</summary>
    public static IReadOnlyList<string> Remember(IEnumerable<string> addresses, Uri server) =>
        [.. new[] { Normalize(server) }.Concat(addresses).Distinct(StringComparer.OrdinalIgnoreCase).Take(AddressesKept)];

    /// <summary>
    /// The addresses to try instead of <paramref name="current"/>, in order: <paramref name="remembered"/>, this computer
    /// by name and as localhost when a server listens on it, then the addresses of servers found on the network that
    /// announce one of <paramref name="caFingerprints"/>, the CAs this workstation trusts the server through: while the
    /// hospital CA is rolled over, a server may announce the next CA the workstation holds (switched already) or the one the
    /// pinned CA replaced (a standby not switched yet). A server announcing another CA is not the hospital's.
    /// </summary>
    public static IReadOnlyList<Uri> Candidates(
        Uri current, IEnumerable<string> remembered, DiscoveredServer? thisComputer, IEnumerable<DiscoveredServer> discovered,
        IReadOnlyCollection<string> caFingerprints)
    {
        var candidates = new List<Uri>();
        foreach (var address in remembered)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            {
                candidates.Add(uri);
            }
        }

        if (thisComputer is not null)
        {
            candidates.Add(new Uri($"https://{thisComputer.HostName}:{thisComputer.Port}/"));
            candidates.Add(new Uri($"https://localhost:{thisComputer.Port}/"));
        }

        foreach (var server in discovered.Where(s => caFingerprints.Contains(s.CaFingerprint, StringComparer.OrdinalIgnoreCase)))
        {
            candidates.AddRange(server.CandidateUris());
        }

        var skip = Normalize(current);
        return
        [
            .. candidates
                .Select(Normalize)
                .Where(address => !string.Equals(address, skip, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(address => new Uri(address)),
        ];
    }

    /// <summary>
    /// The first of <paramref name="candidates"/> where the server answers over a connection pinned as
    /// <paramref name="pinned"/> makes it; null when it answers at none.
    /// </summary>
    public static async Task<Uri?> FirstAnsweringAsync(IEnumerable<Uri> candidates, Func<ServerTrust> pinned, CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            // A trust of its own, not the window's: a machine at an old address that is not the hospital server is passed
            // over, and does not lock the window as an intercepted connection to the server does.
            using var trust = pinned();
            using var client = new ServerClient(candidate, trust);
            if (await client.PingAsync(cancellationToken) is not null)
            {
                return candidate;
            }

            Log.Information("The server did not answer at {Candidate}", candidate);
        }

        return null;
    }

    /// <summary>
    /// Looks for the server at its other addresses (<see cref="Candidates"/>), and returns the first that answers over a
    /// connection pinned to the CAs this workstation trusts the server through (<paramref name="pins"/>).
    /// </summary>
    public static async Task<Uri?> FindAsync(Uri current, DesktopSettings settings, HospitalCaPins pins, CancellationToken cancellationToken)
    {
        var thisComputer = await ServerDiscovery.ThisComputerAsync(current.Port, cancellationToken);
        var discovered = await ServerDiscovery.BrowseAsync(TimeSpan.FromSeconds(3), cancellationToken);
        var candidates = Candidates(current, settings.ServerAddresses, thisComputer, discovered, pins.Fingerprints(DateTimeOffset.UtcNow));
        var (ca, previous, next) = (settings.CaCertificatePem!, settings.PreviousCaCertificatePem, settings.NextCaCertificatePem);
        return await FirstAnsweringAsync(candidates, () => new ServerTrust(ca, previous, next), cancellationToken);
    }
}
