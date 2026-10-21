using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HyFib.HealthCare360.WindowsInterop.Biometrics;

namespace HyFib.HealthCare360.Integrations.Aadhaar;

/// <summary>
/// Aadhaar Auth API 2.5 through the hospital's authentication service agency (<c>uidai</c>): builds the Auth XML from
/// the capture, signs it with the AUA's certificate and posts it to the ASA. Each call is logged with the number's last
/// four digits, the transaction id and UIDAI's codes; the number, the capture and the request are never logged or kept.
/// </summary>
internal sealed class UidaiAadhaarAuthenticator(HttpClient http, IntegrationTrace trace) : IAadhaarAuthProvider
{
    public string Key => ProviderKeys.Uidai;

    public async Task<AadhaarCheckResult> AuthenticateAsync(
        AadhaarAuthRequest request, AadhaarSettings settings, AadhaarSecrets secrets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(secrets);
        if (request.Capture.IsTraining)
        {
            return Fail("A training capture cannot be sent to UIDAI. Capture the finger on a fingerprint reader.");
        }

        if (request.TerminalCode is null)
        {
            return Fail("Aadhaar authentication works only from HC360 Desktop on an enrolled workstation.");
        }

        if (Problem(settings, secrets) is { } problem)
        {
            return Fail(problem);
        }

        X509Certificate2 signing;
        X509Certificate2? uidai;
        try
        {
            signing = X509CertificateLoader.LoadPkcs12(
                Convert.FromBase64String(secrets.SigningCertificate!), secrets.SigningCertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
            uidai = string.IsNullOrWhiteSpace(settings.UidaiCertificate) ? null : X509Certificate2.CreateFromPem(settings.UidaiCertificate);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return Fail("The AUA signing certificate or UIDAI's certificate could not be read. Enter them again in Administration → Integrations.");
        }

        using (signing)
        using (uidai)
        {
            var transactionId = UidaiAuthXml.NewTransactionId();
            string body;
            try
            {
                body = UidaiAuthXml.Sign(UidaiAuthXml.Build(request, settings, secrets.LicenceKey!, transactionId), signing);
            }
            catch (CryptographicException)
            {
                return Fail("The request could not be signed with the AUA certificate. Check that it holds its private key.");
            }

            var address = UidaiAuthXml.Address(settings.AuthUrl!, settings.AuaCode!, request.Uid, secrets.AsaLicenceKey ?? string.Empty);
            return await SendAsync(request, address, body, transactionId, settings, uidai, cancellationToken);
        }
    }

    /// <summary>What is wrong with the settings for this provider, in words for an administrator, or null.</summary>
    internal static string? Problem(AadhaarSettings settings, AadhaarSecrets secrets)
    {
        if (string.IsNullOrWhiteSpace(settings.AuthUrl)
            || !IntegrationHttp.TryCreateEndpoint(Fill(settings.AuthUrl), requireHttps: true, out _))
        {
            return "Enter the ASA's https address for Aadhaar authentication in Administration → Integrations.";
        }

        if (!IsCode(settings.AuaCode) || !IsCode(settings.SubAuaCode))
        {
            return "Enter the AUA code and the sub-AUA code (up to 10 letters or digits each) in Administration → Integrations.";
        }

        if (string.IsNullOrWhiteSpace(settings.RegisteredDeviceTid) || settings.RegisteredDeviceTid.Length > 16 || !settings.RegisteredDeviceTid.All(char.IsAsciiLetter))
        {
            return "The registered device type (tid) must be letters only, such as registered.";
        }

        if (settings.TimeoutSeconds is < 5 or > 60)
        {
            return "UIDAI may be given 5 to 60 seconds to answer.";
        }

        if (string.IsNullOrWhiteSpace(secrets.LicenceKey) || string.IsNullOrWhiteSpace(secrets.SigningCertificate)
            || (settings.AuthUrl.Contains("{asalk}", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(secrets.AsaLicenceKey)))
        {
            return "Enter the AUA licence key, the ASA licence key and the AUA signing certificate in Administration → Integrations.";
        }

        return settings.CaptureProblem();
    }

    private async Task<AadhaarCheckResult> SendAsync(
        AadhaarAuthRequest request, Uri address, string body, string transactionId, AadhaarSettings settings, X509Certificate2? uidai, CancellationToken cancellationToken)
    {
        var device = request.Capture.Device!;
        var summary = string.Create(CultureInfo.InvariantCulture,
            $"uid ****{AadhaarNumber.Last4(request.Uid)}; bt={RdService.BiometricType(request.Capture.DataType ?? settings.FingerDataType)}; device {device.ProviderId} {device.ModelId}; udc {request.TerminalCode}");
        var started = trace.Start();
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, address) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };
            using var response = await http.SendAsync(message, timer.Token);
            var text = await response.Content.ReadAsStringAsync(timer.Token);
            if (!response.IsSuccessStatusCode)
            {
                await trace.LogAsync(IntegrationNames.Aadhaar, Key, "authenticate", false, started, transactionId, summary,
                    $"HTTP {(int)response.StatusCode}", "The ASA refused the request.");
                return Fail(string.Create(CultureInfo.InvariantCulture,
                    $"The ASA answered HTTP {(int)response.StatusCode}. Nothing is known about the fingerprint; try again, and tell your administrator if it goes on."), transactionId);
            }

            AuthResponse answer;
            try
            {
                answer = UidaiAuthXml.ParseResponse(text);
            }
            catch (FormatException)
            {
                await trace.LogAsync(IntegrationNames.Aadhaar, Key, "authenticate", false, started, transactionId, summary, "HTTP 200", "Not an AuthRes document.");
                return Fail("The ASA's answer could not be read. Nothing is known about the fingerprint; tell your administrator.", transactionId);
            }

            var outcome = Judge(answer, transactionId, uidai is null || UidaiAuthXml.HasUidaiSignature(text, uidai), device);
            var response200 = string.Create(CultureInfo.InvariantCulture,
                $"HTTP 200 ret={(answer.Authenticated ? "y" : "n")} err={answer.ErrorCode ?? "-"} code={answer.Code ?? "-"}");
            await trace.LogAsync(IntegrationNames.Aadhaar, Key, "authenticate", outcome.Outcome != AadhaarCheckOutcome.Failed, started, transactionId,
                summary, response200, outcome.Outcome == AadhaarCheckOutcome.Failed ? outcome.Message : null);
            return outcome;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            var why = ex is OperationCanceledException ? "UIDAI did not answer in time." : "The ASA could not be reached.";
            await trace.LogAsync(IntegrationNames.Aadhaar, Key, "authenticate", false, started, transactionId, summary, error: why);
            return Fail($"{why} Nothing is known about the fingerprint; try again in a minute.", transactionId);
        }
    }

    /// <summary>The outcome of an answer that was read: UIDAI's word, if the answer is UIDAI's and about this request.</summary>
    internal static AadhaarCheckResult Judge(AuthResponse answer, string transactionId, bool signedByUidai, RdDevice device)
    {
        if (!signedByUidai)
        {
            return new AadhaarCheckResult(AadhaarCheckOutcome.Failed,
                "The answer is not signed with UIDAI's certificate, so it was not believed. Nothing is known about the fingerprint; tell your administrator.",
                ProviderKeys.Uidai, TransactionId: transactionId);
        }

        if (!string.Equals(answer.TransactionId, transactionId, StringComparison.Ordinal))
        {
            return new AadhaarCheckResult(AadhaarCheckOutcome.Failed,
                "The answer is for another request, so it was not believed. Nothing is known about the fingerprint; try again.",
                ProviderKeys.Uidai, TransactionId: transactionId);
        }

        if (answer.Authenticated)
        {
            return new AadhaarCheckResult(AadhaarCheckOutcome.Verified, "UIDAI authenticated the fingerprint for this Aadhaar number.",
                ProviderKeys.Uidai, null, transactionId, answer.Code, device.ProviderId, device.ModelId);
        }

        var (outcome, message) = UidaiAuthXml.Refusal(answer.ErrorCode);
        return new AadhaarCheckResult(outcome, message, ProviderKeys.Uidai, answer.ErrorCode, transactionId, answer.Code, device.ProviderId, device.ModelId);
    }

    /// <summary>The template with sample values, to check it makes an address.</summary>
    private static string Fill(string template) =>
        template.Replace("{ac}", "AUA", StringComparison.Ordinal).Replace("{uid0}", "2", StringComparison.Ordinal)
            .Replace("{uid1}", "3", StringComparison.Ordinal).Replace("{asalk}", "KEY", StringComparison.Ordinal);

    private static bool IsCode(string? code) => code is { Length: > 0 and <= 10 } && code.All(char.IsAsciiLetterOrDigit);

    private static AadhaarCheckResult Fail(string message, string? transactionId = null) =>
        new(AadhaarCheckOutcome.Failed, message, ProviderKeys.Uidai, TransactionId: transactionId);
}
