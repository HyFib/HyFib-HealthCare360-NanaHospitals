using HyFib.HealthCare360.WindowsInterop.Biometrics;

namespace HyFib.HealthCare360.Integrations.Aadhaar;

/// <summary>
/// Non-secret settings for checking fingerprints with UIDAI (<c>integrations.aadhaar</c>, INT-BIO): the hospital's AUA
/// licence, as its authentication service agency (ASA) gives the details, and what workstations capture.
/// </summary>
internal sealed record AadhaarSettings : IIntegrationSettings
{
    public bool Enabled { get; init; }

    /// <summary><c>uidai</c> (Aadhaar Auth API 2.5 through the ASA) or <c>simulator</c>.</summary>
    public string? Provider { get; init; }

    /// <summary>
    /// The UIDAI environment of the licence: <c>P</c> production, <c>PP</c> pre-production, <c>S</c> staging. Readers
    /// encrypt each capture for it, so it must match the licence.
    /// </summary>
    public string Environment { get; init; } = "P";

    /// <summary><c>FMR</c>, <c>FIR</c> or <c>FMR,FIR</c> (both, which UIDAI asks of L1 registered devices).</summary>
    public string FingerData { get; init; } = "FMR,FIR";

    /// <summary>Fingers per capture, 1 or 2.</summary>
    public int FingerCount { get; init; } = 1;

    /// <summary>How long a reader waits for a finger, 5 to 60 seconds.</summary>
    public int CaptureTimeoutSeconds { get; init; } = 15;

    /// <summary>
    /// The https address the ASA takes Auth XML at. <c>{ac}</c>, <c>{uid0}</c>, <c>{uid1}</c> and <c>{asalk}</c> are
    /// filled in (UIDAI's form is <c>https://&lt;host&gt;/2.5/{ac}/{uid0}/{uid1}/{asalk}</c>).
    /// </summary>
    public string? AuthUrl { get; init; }

    /// <summary>The AUA code (<c>ac</c>).</summary>
    public string? AuaCode { get; init; }

    /// <summary>The sub-AUA code (<c>sa</c>): the hospital's own when it is a sub-AUA, otherwise the AUA code.</summary>
    public string? SubAuaCode { get; init; }

    /// <summary>
    /// The <c>tid</c> UIDAI expects for a registered device: <c>registered</c> in Auth API 2.5. UIDAI answers 520 to a
    /// value it does not take, and the ASA says which one its channel uses.
    /// </summary>
    public string RegisteredDeviceTid { get; init; } = "registered";

    /// <summary>How long UIDAI is given to answer, 5 to 60 seconds.</summary>
    public int TimeoutSeconds { get; init; } = 20;

    /// <summary>
    /// UIDAI's signing certificate (PEM), from the ASA. When it is set, every answer must carry UIDAI's signature over
    /// the whole answer, made with it; without it, the TLS connection to the ASA is all that vouches for the answer.
    /// </summary>
    public string? UidaiCertificate { get; init; }

    /// <summary>The <c>fType</c> readers are asked for.</summary>
    public FingerDataType FingerDataType => FingerData.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant() switch
    {
        "FMR" => FingerDataType.Minutiae,
        "FIR" => FingerDataType.Image,
        _ => FingerDataType.MinutiaeAndImage,
    };

    /// <summary>What is wrong with the capture settings, in words for an administrator, or null.</summary>
    public string? CaptureProblem()
    {
        if (Environment is not ("P" or "PP" or "S"))
        {
            return "The Aadhaar authentication environment must be P, PP or S.";
        }

        if (FingerData.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant() is not ("FMR" or "FIR" or "FMR,FIR"))
        {
            return "The fingerprint data must be FMR, FIR or FMR,FIR.";
        }

        if (FingerCount is < 1 or > FingerprintCaptureOptions.MaxFingers)
        {
            return "Capture 1 or 2 fingers.";
        }

        return CaptureTimeoutSeconds is < 5 or > 60 ? "The reader may wait 5 to 60 seconds for a finger." : null;
    }
}

/// <summary>Secret settings (<c>integrations.aadhaar.secret</c>).</summary>
internal sealed record AadhaarSecrets
{
    /// <summary>The AUA licence key (<c>lk</c>).</summary>
    public string? LicenceKey { get; init; }

    /// <summary>The ASA licence key (<c>asalk</c>), part of the address Auth XML is sent to.</summary>
    public string? AsaLicenceKey { get; init; }

    /// <summary>The AUA's signing certificate with its private key: a PKCS#12 (.pfx) file, base64.</summary>
    public string? SigningCertificate { get; init; }

    public string? SigningCertificatePassword { get; init; }
}

/// <summary>A number and a capture the router has checked: the number is well formed and the capture complete.</summary>
/// <param name="Uid">The 12-digit Aadhaar number or 16-digit Virtual ID, digits only.</param>
/// <param name="TerminalCode">The terminal code (<c>udc</c>) of the workstation, or null when it is not an enrolled one.</param>
internal sealed record AadhaarAuthRequest(string Uid, PidCapture Capture, string? TerminalCode);

internal interface IAadhaarAuthProvider
{
    string Key { get; }

    Task<AadhaarCheckResult> AuthenticateAsync(AadhaarAuthRequest request, AadhaarSettings settings, AadhaarSecrets secrets, CancellationToken cancellationToken);
}
