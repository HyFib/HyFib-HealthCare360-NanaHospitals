using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyFib.HealthCare360.Integrations.Aadhaar;
using HyFib.HealthCare360.Integrations.Biometric;
using HyFib.HealthCare360.Integrations.Features.Labels;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Labels;
using HyFib.HealthCare360.Integrations.Pacs.Worklist;
using HyFib.HealthCare360.Integrations.Pos;
using HyFib.HealthCare360.Integrations.WhatsApp;

namespace HyFib.HealthCare360.Integrations.Features.Admin;

/// <summary>
/// "Send a test" for the integrations dashboard. Tests are harmless by design: messages go only to the address the
/// administrator typed, the payment test creates a ₹1 order (no money moves until someone pays it), and POS/Tally/PACS
/// tests only check connectivity or configuration. Every test is audited.
/// </summary>
internal sealed class IntegrationTestService(
    ISmsSender sms,
    IWhatsAppSender whatsApp,
    IEmailSender email,
    IPaymentGateway payments,
    IPosTerminal pos,
    IAccountingExporter accounting,
    IPacsClient pacs,
    ModalityWorklistSelfTest worklist,
    IIcdLookup icd,
    ILabelPrinterService labels,
    LabelJobs labelJobs,
    IntegrationSettingsReader settings,
    LabGatewayState lab,
    IAuditLogger audit,
    TimeProvider time)
{
    public async Task<IntegrationTestResult?> RunAsync(string name, TestIntegrationRequest request, CancellationToken cancellationToken)
    {
        var reference = "test-" + time.GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        IntegrationTestResult? result = name switch
        {
            IntegrationNames.Sms => await SmsAsync(request, reference, cancellationToken),
            IntegrationNames.WhatsApp => await WhatsAppAsync(request, reference, cancellationToken),
            IntegrationNames.Email => await EmailAsync(request, reference, cancellationToken),
            IntegrationNames.Payment => await PaymentAsync(reference, cancellationToken),
            IntegrationNames.Pos => await PosAsync(cancellationToken),
            IntegrationNames.Tally => await accounting.PingAsync(cancellationToken)
                ? new IntegrationTestResult(true, "Tally answered and the company is open.")
                : new IntegrationTestResult(false, "Tally did not answer or the company is not open. See the integration log for details."),
            IntegrationNames.Pacs => await pacs.PingAsync(cancellationToken)
                ? new IntegrationTestResult(true, "The PACS answered a QIDO-RS study query.")
                : new IntegrationTestResult(false, "The PACS did not answer. See the integration log for details."),
            IntegrationNames.Mwl => await worklist.RunAsync(cancellationToken),
            IntegrationNames.Icd => await IcdAsync(cancellationToken),
            IntegrationNames.Lab => Lab(),
            IntegrationNames.Labels => await LabelsAsync(cancellationToken),
            IntegrationNames.Biometric => await BiometricAsync(cancellationToken),
            IntegrationNames.Aadhaar => await AadhaarAsync(cancellationToken),
            _ => null,
        };

        if (result is not null)
        {
            await audit.WriteAsync(new AuditEntry(AuditCategories.Integration, "integration.tested", $"Tested the {IntegrationNames.DisplayName(name)} integration")
            {
                EntityType = "Integration",
                EntityId = name,
                Details = new { name, result.Success },
            }, cancellationToken);
        }

        return result;
    }

    private async Task<IntegrationTestResult> SmsAsync(TestIntegrationRequest request, string reference, CancellationToken cancellationToken)
    {
        if (Phone.Normalize(request.To) is not { } to)
        {
            return new IntegrationTestResult(false, "Enter the mobile number to send the test SMS to.");
        }

        var text = string.IsNullOrWhiteSpace(request.Message) ? "HC360 test message. Please ignore." : request.Message.Trim();
        var result = await sms.SendAsync(new SmsMessage(to, text, request.DltTemplateId, null, reference), cancellationToken);
        return Sent(result, $"Test SMS sent to {Phone.Mask(to)}.");
    }

    private async Task<IntegrationTestResult> WhatsAppAsync(TestIntegrationRequest request, string reference, CancellationToken cancellationToken)
    {
        if (Phone.Normalize(request.To) is not { } to)
        {
            return new IntegrationTestResult(false, "Enter the mobile number to send the test WhatsApp message to.");
        }

        var configured = await settings.ReadAsync<WhatsAppSettings, WhatsAppSecrets>(IntegrationNames.WhatsApp, cancellationToken);
        var template = request.TemplateName ?? configured.Settings?.TestTemplateName ?? "hello_world";
        var language = request.LanguageCode ?? configured.Settings?.TestTemplateLanguage ?? "en_US";
        var result = await whatsApp.SendTemplateAsync(new WhatsAppTemplateMessage(to, template, language, [], reference), cancellationToken);
        return Sent(result, $"Test template '{template}' sent to {Phone.Mask(to)}.");
    }

    private async Task<IntegrationTestResult> EmailAsync(TestIntegrationRequest request, string reference, CancellationToken cancellationToken)
    {
        if (!MailAddress.TryCreate(request.To, out var to))
        {
            return new IntegrationTestResult(false, "Enter the e-mail address to send the test message to.");
        }

        var body = "<p>This is a test message from HyFib HealthCare360. No action is needed.</p>";
        var result = await email.SendAsync(new EmailMessage(to.Address, "HC360 test e-mail", body, null, reference), cancellationToken);
        return Sent(result, "Test e-mail sent.");
    }

    private async Task<IntegrationTestResult> PaymentAsync(string reference, CancellationToken cancellationToken)
    {
        try
        {
            var order = await payments.CreateOrderAsync(
                new CreatePaymentOrder(1m, "INR", reference, "HC360 integration test", null, null, null), null, cancellationToken);
            return new IntegrationTestResult(true, $"Created a ₹1 test order with {order.Provider}. It expires unpaid.",
                new Dictionary<string, string?> { ["provider"] = order.Provider, ["orderId"] = order.OrderId });
        }
        catch (InvalidOperationException ex)
        {
            return new IntegrationTestResult(false, ex.Message);
        }
    }

    /// <summary>Real terminals are only checked for configuration: pushing a sale would prompt a customer at the counter.</summary>
    private async Task<IntegrationTestResult> PosAsync(CancellationToken cancellationToken)
    {
        var config = await settings.ReadAsync<PosSettings, PosSecrets>(IntegrationNames.Pos, cancellationToken);
        if (config.ActiveProvider is null)
        {
            return new IntegrationTestResult(false, config.Problem ?? "The POS integration is not available.");
        }

        if (config.ActiveProvider == ProviderKeys.Simulator)
        {
            var sale = await pos.StartSaleAsync(new PosSaleRequest("TEST", 1m, "integration-test"), null, cancellationToken);
            return new IntegrationTestResult(sale.Status == PosTransactionStatus.AwaitingCustomer, $"Simulator sale {sale.TransactionId}: {sale.Status}.");
        }

        var terminals = config.Settings!.Terminals.Where(t => PosTransactionIds.IsValidTerminalId(t.Id)).Select(t => t.Id).ToList();
        var problems = new List<string>();
        if (terminals.Count == 0)
        {
            problems.Add("no terminals are configured");
        }

        if (string.IsNullOrWhiteSpace(config.Settings.MerchantId) || string.IsNullOrWhiteSpace(config.Secrets.SecurityToken))
        {
            problems.Add("merchant id or security token is missing");
        }

        return problems.Count == 0
            ? new IntegrationTestResult(true, $"Configuration looks complete for {terminals.Count} terminal(s): {string.Join(", ", terminals)}. Run a ₹1 sale from a billing counter to test end to end.")
            : new IntegrationTestResult(false, "POS configuration incomplete: " + string.Join("; ", problems) + ".");
    }

    /// <summary>Terminals are registered in HR, so the test checks the settings; HR reads each terminal with "Read now".</summary>
    private async Task<IntegrationTestResult> BiometricAsync(CancellationToken cancellationToken)
    {
        var config = await settings.ReadAsync<BiometricSettings, BiometricSecrets>(IntegrationNames.Biometric, cancellationToken);
        if (config.ActiveProvider is not { } provider)
        {
            return new IntegrationTestResult(false,
                (config.Problem ?? "Reading terminals is not available.") + " Terminals that push to HC360 do not need it.");
        }

        return provider switch
        {
            ProviderKeys.Simulator => new IntegrationTestResult(true,
                "The training terminal is ready. Register a terminal in HR › Attendance terminals to be read by HC360, punch on it there, then read it."),
            ProviderKeys.ZkTeco when !ZkTecoTerminal.TryCommKey(config.Secrets.CommKey, out _) => new IntegrationTestResult(false,
                "The communication key must be a number from 0 to 999999, or empty when the terminals have none."),
            ProviderKeys.ZkTeco => new IntegrationTestResult(true,
                "The settings are complete. Use Read now on a terminal in HR › Attendance terminals to check that HC360 reaches it."),
            _ => new IntegrationTestResult(false, $"Unknown biometric provider '{provider}'. Use zkteco or simulator."),
        };
    }

    /// <summary>
    /// An authentication needs a person's finger and their consent, so the test checks the settings and the AUA's
    /// certificate: it can sign (it holds its private key) and is within its dates.
    /// </summary>
    private async Task<IntegrationTestResult> AadhaarAsync(CancellationToken cancellationToken)
    {
        var config = await settings.ReadAsync<AadhaarSettings, AadhaarSecrets>(IntegrationNames.Aadhaar, cancellationToken);
        if (config.ActiveProvider is not { } provider)
        {
            return new IntegrationTestResult(false, config.Problem ?? "Aadhaar authentication is not available.");
        }

        if (provider == ProviderKeys.Simulator)
        {
            return config.Settings!.CaptureProblem() is { } capture
                ? new IntegrationTestResult(false, capture)
                : new IntegrationTestResult(true,
                    "The training simulator is ready. Verify a patient's identity from their profile; without a fingerprint reader, use the training fingerprint.");
        }

        if (provider != ProviderKeys.Uidai)
        {
            return new IntegrationTestResult(false, $"Unknown Aadhaar authentication provider '{provider}'. Use uidai or simulator.");
        }

        if (UidaiAadhaarAuthenticator.Problem(config.Settings!, config.Secrets) is { } problem)
        {
            return new IntegrationTestResult(false, problem);
        }

        try
        {
            using var certificate = X509CertificateLoader.LoadPkcs12(
                Convert.FromBase64String(config.Secrets.SigningCertificate!), config.Secrets.SigningCertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
            using var key = certificate.GetRSAPrivateKey();
            var now = time.GetUtcNow();
            if (key is null)
            {
                return new IntegrationTestResult(false, "The AUA signing certificate has no RSA private key, so it cannot sign requests.");
            }

            if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
            {
                return new IntegrationTestResult(false,
                    $"The AUA signing certificate is valid from {certificate.NotBefore:yyyy-MM-dd} to {certificate.NotAfter:yyyy-MM-dd}, not today. Get a current one from the AUA.");
            }

            return new IntegrationTestResult(true,
                $"The settings are complete and the AUA certificate ({certificate.GetNameInfo(X509NameType.SimpleName, false)}) can sign until {certificate.NotAfter:yyyy-MM-dd}. Verify a patient's identity with a fingerprint reader to test with UIDAI.");
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return new IntegrationTestResult(false, "The AUA signing certificate or its password could not be read. Enter them again.");
        }
    }

    private async Task<IntegrationTestResult> IcdAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<IcdSearchResult> results;
        try
        {
            results = await icd.SearchAsync("cholera", "ICD-11 MMS", 5, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // The router's message is staff-facing (no credentials or payloads).
            return new IntegrationTestResult(false, ex.Message);
        }

        return results.Count > 0
            ? new IntegrationTestResult(true, $"Online ICD lookup returned {results.Count} result(s), for example {results[0].Code} {results[0].Title}.")
            : new IntegrationTestResult(false, "No results. Check that online ICD lookup is enabled with the WHO ICD-API or the simulator.");
    }

    private IntegrationTestResult Lab()
    {
        var analysers = lab.Snapshot();
        if (analysers.Count == 0)
        {
            return new IntegrationTestResult(false,
                "No analyser is set up. Turn on the laboratory gateway, then set up each analyser's connection in Lab setup › Analysers.");
        }

        var listening = analysers.Count(a => a.Transport == "tcp" && a.State == "listening");
        var relayed = analysers.Count(a => a.Transport == "serial" && a.State == "connected");
        var training = analysers.Count(a => a.Transport == "simulator" && a.State == "training");
        var problems = analysers.Where(a => a.State is "failed" or "port_error" or "waiting").Select(Problem).ToList();
        var summary = $"{listening} network analyser(s) listening, {relayed} serial analyser(s) connected through their lab agent"
                      + (training > 0 ? $", {training} training analyser(s) taking results entered in Lab setup." : ".");
        return problems.Count == 0
            ? new IntegrationTestResult(true, summary)
            : new IntegrationTestResult(false, $"{summary} Not working: {string.Join("; ", problems)}");

        static string Problem(LabListenerStatus analyser) => analyser.State == "waiting"
            ? $"{analyser.InstrumentCode} (waiting for its lab agent {analyser.Agent})".Replace(" )", ")", StringComparison.Ordinal)
            : $"{analyser.InstrumentCode} ({analyser.LastError})";
    }

    private async Task<IntegrationTestResult> LabelsAsync(CancellationToken cancellationToken)
    {
        var zpl = labels.Build(new LabelRequest("HC360-TEST", LabelSymbology.Code128, ["HC360 test label"]), "zpl");
        var message = await labelJobs.IsSimulatorAsync(cancellationToken)
            ? "A sample ZPL label was built. Labels go to the simulator: staff see each label on screen instead of it printing, and the jobs are kept in Simulator › Messages."
            : "A sample ZPL label was built. To check a printer, open Printers on this computer in HC360 Desktop on that workstation and print a test.";
        return new IntegrationTestResult(true, message, new Dictionary<string, string?> { ["zpl"] = zpl });
    }

    private static IntegrationTestResult Sent(SendResult result, string success) =>
        result.Success
            ? new IntegrationTestResult(true, success, new Dictionary<string, string?> { ["providerMessageId"] = result.ProviderMessageId })
            : new IntegrationTestResult(false, result.Error ?? "The message was not sent.");
}
