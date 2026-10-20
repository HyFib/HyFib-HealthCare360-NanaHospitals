namespace HyFib.HealthCare360.Platform.Integrations;

/// <summary>
/// Used when the Integrations project is not loaded (module tests): fingerprints cannot be checked, and every check
/// fails with the reason, never with a silent success.
/// </summary>
internal sealed class UnconfiguredAadhaarAuthenticator : IAadhaarAuthenticator
{
    private const string Reason = "Aadhaar authentication is not set up. Set it up in Administration → Integrations.";

    public Task<AadhaarCaptureSetup> SetupAsync(Guid? facilityId, CancellationToken cancellationToken) =>
        Task.FromResult(new AadhaarCaptureSetup(null, Reason));

    public Task<AadhaarCheckResult> AuthenticateAsync(AadhaarFingerprintCheck check, CancellationToken cancellationToken) =>
        Task.FromResult(AadhaarCheckResult.Fail("none", Reason));
}
