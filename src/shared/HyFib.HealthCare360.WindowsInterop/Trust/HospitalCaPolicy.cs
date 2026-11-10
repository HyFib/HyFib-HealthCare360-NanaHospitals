using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HyFib.HealthCare360.WindowsInterop.Trust;

/// <summary>What a workstation keeps of the hospital CAs it holds, under an IT policy that names one (<see cref="HospitalCaPolicy"/>).</summary>
public enum HospitalCaPolicyOutcome
{
    /// <summary>It keeps the CAs it holds.</summary>
    Keep,

    /// <summary>The policy names the next CA it holds: it pins that CA alone.</summary>
    PinNextAlone,

    /// <summary>The policy names no CA it holds, or one nothing vouches for: it is set up again.</summary>
    SetUpAgain,
}

/// <summary>
/// Whether a workstation keeps a hospital CA that IT's machine policy names (HC360 Desktop's <c>-CaFingerprint</c>) through a
/// rollover (docs/deployment/CA-ROLLOVER.md). The user's own settings file holds the CAs and can be edited, so the
/// fingerprints are those of the certificates themselves, and a CA is kept only on the word of the one the policy names:
/// <list type="bullet">
/// <item>the CA pinned is the one the policy names: kept;</item>
/// <item>the CA pinned replaced the one the policy names in a switch the workstation followed, and that CA's signature on
/// the pinned CA's key, kept since, shows it (<see cref="HospitalCaSuccession.Succeeded"/>): kept;</item>
/// <item>the policy names the next CA the workstation holds, before it switched: it pins that CA alone, from the policy's
/// own fingerprint. The CA it pinned until then goes: the next CA never vouched for it, and the file could hold any CA
/// there, with a word for the next CA signed by that CA's own key.</item>
/// </list>
/// Anything else is set up again. HC360 Desktop decides with this code; the server's tests check it.
/// </summary>
public static class HospitalCaPolicy
{
    public static HospitalCaPolicyOutcome Decide(string policyFingerprint, string caPem, string? previousCaPem, string? crossPem, string? nextCaPem)
    {
        ArgumentNullException.ThrowIfNull(policyFingerprint);
        ArgumentNullException.ThrowIfNull(caPem);
        try
        {
            using var pinned = X509Certificate2.CreateFromPem(caPem);
            if (IsNamed(pinned, policyFingerprint))
            {
                return HospitalCaPolicyOutcome.Keep;
            }

            if (previousCaPem is not null && crossPem is not null)
            {
                using var previous = X509Certificate2.CreateFromPem(previousCaPem);
                using var cross = X509Certificate2.CreateFromPem(crossPem);
                if (IsNamed(previous, policyFingerprint) && HospitalCaSuccession.Succeeded(previous, pinned, cross))
                {
                    return HospitalCaPolicyOutcome.Keep;
                }
            }

            if (nextCaPem is not null)
            {
                using var next = X509Certificate2.CreateFromPem(nextCaPem);
                if (IsNamed(next, policyFingerprint))
                {
                    return HospitalCaPolicyOutcome.PinNextAlone;
                }
            }

            return HospitalCaPolicyOutcome.SetUpAgain;
        }
        catch (CryptographicException)
        {
            return HospitalCaPolicyOutcome.SetUpAgain;
        }
    }

    private static bool IsNamed(X509Certificate2 ca, string fingerprint) =>
        string.Equals(HospitalCaPins.ComputeFingerprint(ca), fingerprint, StringComparison.Ordinal);
}
