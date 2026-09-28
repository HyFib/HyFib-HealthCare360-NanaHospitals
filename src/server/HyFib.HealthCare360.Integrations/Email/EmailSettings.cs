namespace HyFib.HealthCare360.Integrations.Email;

/// <summary>Non-secret e-mail configuration (<c>integrations.email</c>).</summary>
internal sealed record EmailSettings : IIntegrationSettings
{
    public bool Enabled { get; init; }

    /// <summary><c>smtp</c> or <c>simulator</c>.</summary>
    public string? Provider { get; init; }

    public string? Host { get; init; }

    /// <summary>587 (submission with STARTTLS) by default. Implicit TLS on 465 is not supported by System.Net.Mail.</summary>
    public int Port { get; init; } = 587;

    /// <summary>Upgrade the connection with STARTTLS before authenticating (keep on for any server outside the LAN).</summary>
    public bool UseStartTls { get; init; } = true;

    public string? FromAddress { get; init; }

    public string? FromName { get; init; }

    /// <summary>SMTP user name, when it is not kept with the password in the secret settings.</summary>
    public string? UserName { get; init; }

    public int TimeoutSeconds { get; init; } = 20;

    public int MaxAttachmentMegabytes { get; init; } = 15;
}

/// <summary>Secret e-mail configuration (<c>integrations.email.secret</c>).</summary>
internal sealed record EmailSecrets
{
    public string? UserName { get; init; }

    public string? Password { get; init; }
}

internal interface IEmailProvider
{
    string Key { get; }

    Task<SendResult> SendAsync(EmailMessage message, EmailSettings settings, EmailSecrets secrets, CancellationToken cancellationToken);
}
