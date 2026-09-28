using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Integrations.Laboratory;

namespace HyFib.HealthCare360.Integrations.Features.Admin;

/// <summary>
/// Dashboard row for one integration. <c>Health</c> is <c>healthy</c>, <c>degraded</c> (failures in the last 24 h but
/// the latest call succeeded), <c>failing</c> (the latest call failed), <c>idle</c> (enabled, no traffic yet),
/// <c>disabled</c> or <c>not_configured</c>.
/// </summary>
public sealed record IntegrationStatusDto(
    string Name,
    string DisplayName,
    string? Provider,
    bool Enabled,
    bool Configured,
    string Health,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    string? LastError,
    int FailuresLast24Hours,
    IReadOnlyList<LabListenerStatus>? Listeners);

/// <summary>
/// Optional inputs for <c>POST /api/v1/admin/integrations/{name}/test</c>: <c>to</c> (phone or e-mail) for sms, whatsapp
/// and email; <c>dltTemplateId</c> for Indian SMS routes; <c>templateName</c>/<c>languageCode</c> for WhatsApp.
/// </summary>
public sealed record TestIntegrationRequest(string? To, string? Message, string? DltTemplateId, string? TemplateName, string? LanguageCode);

public sealed record IntegrationTestResult(bool Success, string Message, IReadOnlyDictionary<string, string?>? Details = null);

public sealed record IntegrationLogDto(
    Guid Id,
    DateTimeOffset CreatedAt,
    string Integration,
    string Provider,
    string Operation,
    IntegrationDirection Direction,
    bool Success,
    int? DurationMs,
    string? Reference,
    string? Request,
    string? Response,
    string? Error,
    string? CorrelationId);

public sealed record SimulatorMessageDto(
    Guid Id,
    DateTimeOffset CreatedAt,
    SimulatorChannel Channel,
    string? Recipient,
    string? Subject,
    string Body,
    string? Details,
    string? Reference,
    string ProviderMessageId);

public sealed record SimulatorTransactionDto(
    Guid Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    SimulatorTransactionKind Kind,
    string ExternalId,
    decimal Amount,
    string Currency,
    SimulatorTransactionStatus Status,
    string? Reference,
    string? TerminalId,
    string? PaymentId,
    decimal RefundedAmount);

public sealed record InboundWhatsAppMessageDto(
    Guid Id,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? SentAt,
    string FromPhoneMasked,
    string? ProfileName,
    string MessageType,
    string? Text,
    WhatsAppKeyword Keyword);

public sealed class TestIntegrationRequestValidator : AbstractValidator<TestIntegrationRequest>
{
    public TestIntegrationRequestValidator()
    {
        RuleFor(x => x.To).SafeText(254);
        RuleFor(x => x.Message).SafeText(500);
        RuleFor(x => x.DltTemplateId).Matches("^[0-9]{10,25}$").When(x => !string.IsNullOrWhiteSpace(x.DltTemplateId))
            .WithMessage("A DLT template id is a 10–25 digit number.");
        RuleFor(x => x.TemplateName).Matches("^[a-z0-9_]{1,512}$").When(x => !string.IsNullOrWhiteSpace(x.TemplateName))
            .WithMessage("WhatsApp template names use lower-case letters, digits and underscores.");
        RuleFor(x => x.LanguageCode).Matches("^[a-z]{2,3}(_[A-Z]{2})?$").When(x => !string.IsNullOrWhiteSpace(x.LanguageCode))
            .WithMessage("Use a language code such as en or en_US.");
    }
}
