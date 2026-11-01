using System.Net;
using System.Net.Http;

namespace HyFib.HealthCare360.WindowsInterop.Trust;

/// <summary>
/// How a device follows the hospital CA's two-step rollover (docs/deployment/CA-ROLLOVER.md), a minute after it starts and
/// every six hours after: over a connection pinned to the CAs it holds, it asks the server for the next CA, once
/// 'certificates prepare-ca' made one, and holds it on the pinned CA's word; and, while it holds one, whether the server
/// switched to it ('certificates switch-ca'). HC360 Desktop and the lab agent follow with this one copy of the code, and the
/// server's tests run it over TLS against a real server.
/// </summary>
public static class HospitalCaFollow
{
    /// <summary>The server's CA certificate (PEM).</summary>
    public const string CaPath = "api/v1/system/ca-certificate";

    /// <summary>While a next CA is prepared: it, then the certificate the current CA signed for its key (PEM); 404 otherwise.</summary>
    public const string NextCaPath = "api/v1/system/ca-certificate/next";

    /// <summary>
    /// What the device is to do now, asked of the server over <paramref name="pinned"/>, a client whose connections are pinned
    /// to <paramref name="pins"/>: switch to the next CA it holds when the server's CA is that one; else hold the next CA the
    /// server hands out, when the CA the device pins vouches for it; else nothing (null).
    /// </summary>
    /// <exception cref="HttpRequestException">The server could not be asked; the device asks again later.</exception>
    public static async Task<HospitalCaStep?> CheckAsync(HttpClient pinned, HospitalCaPins pins, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        ArgumentNullException.ThrowIfNull(pins);
        if (pins.NextCa is { } held && pins.IsNext(await pinned.GetStringAsync(CaPath, cancellationToken)))
        {
            return new HospitalCaStep.SwitchToNext(HospitalCaPins.ComputeFingerprint(held));
        }

        using var response = await pinned.GetAsync(NextCaPath, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return pins.NextIn(await response.Content.ReadAsStringAsync(cancellationToken), now) is { } next
            ? new HospitalCaStep.HoldNext(next)
            : null;
    }
}

/// <summary>A step of the hospital CA's rollover a device takes (<see cref="HospitalCaFollow.CheckAsync"/>).</summary>
public abstract record HospitalCaStep
{
    private HospitalCaStep()
    {
    }

    /// <summary>The server prepared <paramref name="Next"/>, and the CA the device pins vouches for it: hold it as a second pin.</summary>
    public sealed record HoldNext(HospitalCaNext Next) : HospitalCaStep;

    /// <summary>The server switched to the next CA the device holds (<paramref name="Fingerprint"/>): pin it.</summary>
    public sealed record SwitchToNext(string Fingerprint) : HospitalCaStep;
}
