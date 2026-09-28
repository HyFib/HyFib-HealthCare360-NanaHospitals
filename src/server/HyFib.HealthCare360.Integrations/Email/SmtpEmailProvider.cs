using System.Net;
using System.Net.Mail;
using System.Text;

namespace HyFib.HealthCare360.Integrations.Email;

/// <summary>
/// SMTP via System.Net.Mail with STARTTLS (<see cref="SmtpClient.EnableSsl"/> upgrades a submission-port connection).
/// Credentials come only from the encrypted secret settings. <see cref="SmtpClient.Timeout"/> applies to synchronous
/// sends only, so the asynchronous send is bounded by a cancellation timeout instead.
/// </summary>
internal sealed class SmtpEmailProvider(IntegrationTrace trace) : IEmailProvider
{
    public string Key => ProviderKeys.Smtp;

    public async Task<SendResult> SendAsync(EmailMessage message, EmailSettings settings, EmailSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        var summary = $"{{\"to\":\"{MaskAddress(message.To)}\",\"attachments\":{message.Attachments?.Count ?? 0}}}";
        if (Validate(settings) is { } problem)
        {
            return await FailAsync(message, started, summary, problem);
        }

        if (!MailAddress.TryCreate(message.To, out var to))
        {
            return await FailAsync(message, started, summary, "The recipient e-mail address is not valid.");
        }

        var attachmentBytes = message.Attachments?.Sum(a => (long)a.Content.Length) ?? 0;
        if (attachmentBytes > Math.Clamp(settings.MaxAttachmentMegabytes, 1, 25) * 1024L * 1024L)
        {
            return await FailAsync(message, started, summary, "The attachments are larger than the configured e-mail limit.");
        }

        var from = new MailAddress(settings.FromAddress!, settings.FromName);
        var messageId = $"<{(string.IsNullOrWhiteSpace(message.Reference) ? Guid.CreateVersion7().ToString("N") : message.Reference)}@{from.Host}>";
        using var mail = BuildMessage(message, from, to, messageId);
        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = settings.UseStartTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
        };
        var user = string.IsNullOrWhiteSpace(secrets.UserName) ? settings.UserName : secrets.UserName;
        if (!string.IsNullOrWhiteSpace(user))
        {
            client.Credentials = new NetworkCredential(user, secrets.Password);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 5, 120)));
        try
        {
            await client.SendMailAsync(mail, timeout.Token);
            await trace.LogAsync(IntegrationNames.Email, Key, "send", true, started, message.Reference, summary, messageId);
            return SendResult.Ok(messageId);
        }
        catch (SmtpException ex)
        {
            // Server replies can quote the recipient; the platform logger masks addresses before persisting.
            return await FailAsync(message, started, summary, $"The mail server refused the message ({ex.StatusCode}): {IntegrationTrace.Truncate(ex.Message, 200)}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await FailAsync(message, started, summary, "The mail server did not respond in time.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Security.Authentication.AuthenticationException)
        {
            return await FailAsync(message, started, summary, $"The e-mail could not be sent ({ex.GetType().Name}).");
        }
    }

    internal static string? Validate(EmailSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Host) || Uri.CheckHostName(settings.Host.Trim()) == UriHostNameType.Unknown)
        {
            return "The SMTP host is not configured.";
        }

        if (settings.Port is < 1 or > 65535)
        {
            return "The SMTP port is not valid.";
        }

        if (settings.Port == 465)
        {
            return "Port 465 (implicit TLS) is not supported; use port 587 with STARTTLS.";
        }

        return MailAddress.TryCreate(settings.FromAddress, out _) ? null : "The sender (from) e-mail address is not valid.";
    }

    /// <summary>First character and domain only, like notification logs (a***@example.com).</summary>
    internal static string MaskAddress(string? address)
    {
        var at = address?.IndexOf('@', StringComparison.Ordinal) ?? -1;
        return at <= 0 ? "***" : address![0] + "***" + address[at..];
    }

    private static MailMessage BuildMessage(EmailMessage message, MailAddress from, MailAddress to, string messageId)
    {
        var mail = new MailMessage
        {
            From = from,
            Subject = message.Subject.ReplaceLineEndings(" "),
            SubjectEncoding = Encoding.UTF8,
            Body = message.HtmlBody,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = true,
        };
        mail.To.Add(to);
        mail.Headers.Add("Message-ID", messageId);
        foreach (var attachment in message.Attachments ?? [])
        {
            mail.Attachments.Add(new Attachment(new MemoryStream(attachment.Content), attachment.FileName, attachment.ContentType));
        }

        return mail;
    }

    private async Task<SendResult> FailAsync(EmailMessage message, long started, string summary, string error)
    {
        await trace.LogAsync(IntegrationNames.Email, Key, "send", false, started, message.Reference, summary, error: error);
        return SendResult.Fail(error);
    }
}
