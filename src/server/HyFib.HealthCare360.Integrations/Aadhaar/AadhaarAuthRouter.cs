using HyFib.HealthCare360.WindowsInterop.Biometrics;

namespace HyFib.HealthCare360.Integrations.Aadhaar;

/// <summary>
/// The platform's <see cref="IAadhaarAuthenticator"/> (INT-BIO): checks the request — the number is an Aadhaar number or a
/// Virtual ID, the capture is complete; the person's consent comes with the check, which cannot be made without it — and
/// asks the provider the facility uses (<c>integrations.aadhaar</c>). Until it is set up, every check fails with the reason.
/// </summary>
internal sealed class AadhaarAuthRouter(IEnumerable<IAadhaarAuthProvider> providers, IntegrationSettingsReader settings, ICurrentUser currentUser)
    : IAadhaarAuthenticator
{
    public async Task<AadhaarCaptureSetup> SetupAsync(Guid? facilityId, CancellationToken cancellationToken)
    {
        var (provider, config, error) = await ResolveAsync(facilityId, cancellationToken);
        if (provider is null)
        {
            return new AadhaarCaptureSetup(null, error);
        }

        var values = config!.Settings!;
        return new AadhaarCaptureSetup(
            provider.Key, null, values.Environment, (int)values.FingerDataType, values.FingerCount, values.CaptureTimeoutSeconds,
            Training: provider.Key == ProviderKeys.Simulator);
    }

    public async Task<AadhaarCheckResult> AuthenticateAsync(AadhaarFingerprintCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(check);
        var facilityId = check.FacilityId ?? currentUser.FacilityId;
        using var facility = IntegrationTrace.ForFacility(facilityId);
        var (provider, config, error) = await ResolveAsync(facilityId, cancellationToken);
        if (provider is null)
        {
            return AadhaarCheckResult.Fail("none", error!);
        }

        // The person's consent is not asked here: an AadhaarFingerprintCheck cannot be made without it.
        if (AadhaarNumber.Normalize(check.AadhaarNumber) is not { } uid)
        {
            return AadhaarCheckResult.Fail(provider.Key, "Enter the 12-digit Aadhaar number or the 16-digit Virtual ID.");
        }

        PidCapture capture;
        try
        {
            capture = PidCapture.Parse(check.PidData);
        }
        catch (FormatException)
        {
            return AadhaarCheckResult.Fail(provider.Key, "The fingerprint capture could not be read. Capture the finger again.");
        }

        if (!capture.Succeeded)
        {
            return AadhaarCheckResult.Fail(provider.Key, "The fingerprint capture is incomplete. Capture the finger again.");
        }

        return await provider.AuthenticateAsync(new AadhaarAuthRequest(uid, capture, TerminalCode(currentUser.DeviceId)), config!.Settings!, config.Secrets, cancellationToken);
    }

    /// <summary>
    /// The workstation's terminal code for UIDAI (<c>udc</c>, at most 20 letters and digits): <c>HC360</c> and the random
    /// end of the enrolled device's id. Null for a browser that is not an enrolled HC360 Desktop.
    /// </summary>
    internal static string? TerminalCode(Guid? deviceId) => deviceId is { } id ? "HC360" + id.ToString("N")[^15..].ToUpperInvariant() : null;

    private async Task<(IAadhaarAuthProvider? Provider, IntegrationConfig<AadhaarSettings, AadhaarSecrets>? Config, string? Error)> ResolveAsync(
        Guid? facilityId, CancellationToken cancellationToken)
    {
        var config = await settings.ReadAsync<AadhaarSettings, AadhaarSecrets>(IntegrationNames.Aadhaar, facilityId, requireEnabled: true, cancellationToken);
        if (config.ActiveProvider is not { } key)
        {
            return (null, null, config.Problem ?? "Aadhaar authentication is not available.");
        }

        if (config.Settings!.CaptureProblem() is { } problem)
        {
            return (null, null, problem);
        }

        var provider = providers.FirstOrDefault(p => p.Key == key);
        return provider is null ? (null, null, $"Unknown Aadhaar authentication provider '{key}'. Use uidai or simulator.") : (provider, config, null);
    }
}
