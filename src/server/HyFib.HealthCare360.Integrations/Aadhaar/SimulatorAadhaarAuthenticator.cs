namespace HyFib.HealthCare360.Integrations.Aadhaar;

/// <summary>
/// Training (<c>simulator</c>): answers as UIDAI would, without UIDAI. A training capture says whether it stands for a
/// finger that matches; a real reader's capture, which only UIDAI could read, is taken as matching. Nothing leaves the
/// server, and each check is logged as the real ones are.
/// </summary>
internal sealed class SimulatorAadhaarAuthenticator(IntegrationTrace trace) : IAadhaarAuthProvider
{
    public string Key => ProviderKeys.Simulator;

    public async Task<AadhaarCheckResult> AuthenticateAsync(
        AadhaarAuthRequest request, AadhaarSettings settings, AadhaarSecrets secrets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = trace.Start();
        var transactionId = UidaiAuthXml.NewTransactionId();
        var capture = request.Capture;
        var matches = !capture.IsTraining || capture.IsMatchingTrainingCapture;
        var device = capture.Device!;
        var result = matches
            ? new AadhaarCheckResult(AadhaarCheckOutcome.Verified,
                capture.IsTraining
                    ? "Training: the simulator took the training fingerprint as matching. UIDAI was not asked."
                    : "Training: the simulator accepted the fingerprint without UIDAI, which alone can check it.",
                Key, null, transactionId, "SIMULATED", device.ProviderId, device.ModelId)
            : new AadhaarCheckResult(AadhaarCheckOutcome.NotVerified, "Training: the fingerprint did not match this Aadhaar number (UIDAI code 300).",
                Key, "300", transactionId, "SIMULATED", device.ProviderId, device.ModelId);
        await trace.LogAsync(IntegrationNames.Aadhaar, Key, "authenticate", true, started, transactionId,
            $"uid ****{AadhaarNumber.Last4(request.Uid)}; device {device.ProviderId}", matches ? "ret=y" : "ret=n err=300");
        return result;
    }
}
