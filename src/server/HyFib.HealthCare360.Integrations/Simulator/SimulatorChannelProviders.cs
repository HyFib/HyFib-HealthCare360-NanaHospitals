using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Integrations.Email;
using HyFib.HealthCare360.Integrations.Sms;
using HyFib.HealthCare360.Integrations.WhatsApp;

namespace HyFib.HealthCare360.Integrations.Simulator;

/// <summary>SMS "delivered" to the simulator inbox (Administration → Integrations → Simulator).</summary>
internal sealed class SimulatorSmsProvider(SimulatorInbox inbox, IntegrationTrace trace) : ISmsProvider
{
    public string Key => ProviderKeys.Simulator;

    public async Task<SendResult> SendAsync(SmsMessage message, SmsSettings settings, SmsSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        var id = await inbox.RecordAsync(SimulatorChannel.Sms, Phone.Mask(message.To), null, message.Text,
            $"DLT template: {message.DltTemplateId ?? "-"}; sender: {message.SenderId ?? settings.SenderId ?? "-"}", message.Reference, cancellationToken);
        await trace.LogAsync(IntegrationNames.Sms, Key, "send", true, started, message.Reference,
            $"{{\"to\":\"{Phone.Mask(message.To)}\",\"length\":{message.Text.Length}}}", id);
        return SendResult.Ok(id);
    }
}

/// <summary>WhatsApp template, document and text messages "delivered" to the simulator inbox.</summary>
internal sealed class SimulatorWhatsAppProvider(SimulatorInbox inbox, IntegrationTrace trace) : IWhatsAppProvider
{
    public string Key => ProviderKeys.Simulator;

    public async Task<SendResult> SendTemplateAsync(WhatsAppTemplateMessage message, WhatsAppSettings settings, WhatsAppSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        var document = message.HeaderDocument is { } file ? $", document {file.FileName} ({file.ContentType}, {file.Content.Length} bytes)" : "";
        var id = await inbox.RecordAsync(SimulatorChannel.WhatsApp, Phone.Mask(message.To), message.TemplateName,
            string.Join("\n", message.BodyParameters), $"template {message.TemplateName} ({message.Language}){document}", message.Reference, cancellationToken);
        await trace.LogAsync(IntegrationNames.WhatsApp, Key, "template", true, started, message.Reference,
            $"{{\"to\":\"{Phone.Mask(message.To)}\",\"template\":\"{message.TemplateName}\"}}", id);
        return SendResult.Ok(id);
    }

    public async Task<SendResult> SendDocumentAsync(WhatsAppDocumentMessage message, WhatsAppSettings settings, WhatsAppSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        var id = await inbox.RecordAsync(SimulatorChannel.WhatsApp, Phone.Mask(message.To), message.FileName,
            message.Caption ?? string.Empty, $"document {message.FileName} ({message.ContentType}, {message.Content.Length} bytes)", message.Reference, cancellationToken);
        await trace.LogAsync(IntegrationNames.WhatsApp, Key, "document", true, started, message.Reference,
            $"{{\"to\":\"{Phone.Mask(message.To)}\",\"bytes\":{message.Content.Length}}}", id);
        return SendResult.Ok(id);
    }

    public async Task<SendResult> SendTextAsync(WhatsAppTextMessage message, WhatsAppSettings settings, WhatsAppSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        var id = await inbox.RecordAsync(SimulatorChannel.WhatsApp, Phone.Mask(message.To), "Text message", message.Text,
            "free-form text (allowed within 24 hours of the patient's message)", message.Reference, cancellationToken);
        await trace.LogAsync(IntegrationNames.WhatsApp, Key, "text", true, started, message.Reference,
            $"{{\"to\":\"{Phone.Mask(message.To)}\",\"length\":{message.Text.Length}}}", id);
        return SendResult.Ok(id);
    }
}

/// <summary>E-mail "delivered" to the simulator inbox (attachments are listed by name and size, not stored).</summary>
internal sealed class SimulatorEmailProvider(SimulatorInbox inbox, IntegrationTrace trace) : IEmailProvider
{
    public string Key => ProviderKeys.Simulator;

    public async Task<SendResult> SendAsync(EmailMessage message, EmailSettings settings, EmailSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        var attachments = message.Attachments is { Count: > 0 } list
            ? "attachments: " + string.Join(", ", list.Select(a => $"{a.FileName} ({a.Content.Length} bytes)"))
            : null;
        var id = await inbox.RecordAsync(SimulatorChannel.Email, SmtpEmailProvider.MaskAddress(message.To), message.Subject, message.HtmlBody,
            attachments, message.Reference, cancellationToken);
        await trace.LogAsync(IntegrationNames.Email, Key, "send", true, started, message.Reference,
            $"{{\"to\":\"{SmtpEmailProvider.MaskAddress(message.To)}\",\"attachments\":{message.Attachments?.Count ?? 0}}}", id);
        return SendResult.Ok(id);
    }
}
