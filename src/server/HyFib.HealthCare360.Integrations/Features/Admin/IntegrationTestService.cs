using System.Globalization;
using System.Net.Mail;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Labels;
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
    IIcdLookup icd,
    ILabelPrinterService labels,
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
            IntegrationNames.Icd => await IcdAsync(cancellationToken),
            IntegrationNames.Lab => Lab(),
            IntegrationNames.Labels => Labels(),
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
                new CreatePaymentOrder(1m, "INR", reference, "HC360 integration test", null, null, null), cancellationToken);
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
            var sale = await pos.StartSaleAsync(new PosSaleRequest("TEST", 1m, "integration-test"), cancellationToken);
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
            ? new IntegrationTestResult(true, $"ICD-API returned {results.Count} result(s), for example {results[0].Code} {results[0].Title}.")
            : new IntegrationTestResult(false, "No results. Check that online ICD lookup is enabled with provider \"who\".");
    }

    private IntegrationTestResult Lab()
    {
        var listeners = lab.Snapshot();
        if (listeners.Count == 0)
        {
            return new IntegrationTestResult(false, "No analyser listener is running. Enable the gateway and add instruments in integrations.lab.instruments.");
        }

        var failed = listeners.Where(l => l.State == "failed").ToList();
        return failed.Count == 0
            ? new IntegrationTestResult(true, $"{listeners.Count} analyser listener(s) running.")
            : new IntegrationTestResult(false, "Listeners failed: " + string.Join("; ", failed.Select(f => $"{f.InstrumentCode} ({f.LastError})")));
    }

    private IntegrationTestResult Labels()
    {
        var zpl = labels.Build(new LabelRequest("HC360-TEST", LabelSymbology.Code128, ["HC360 test label"]), "zpl");
        return new IntegrationTestResult(true, "Sample ZPL label built; print it from a workstation to check the printer.",
            new Dictionary<string, string?> { ["zpl"] = zpl });
    }

    private static IntegrationTestResult Sent(SendResult result, string success) =>
        result.Success
            ? new IntegrationTestResult(true, success, new Dictionary<string, string?> { ["providerMessageId"] = result.ProviderMessageId })
            : new IntegrationTestResult(false, result.Error ?? "The message was not sent.");
}
