namespace HyFib.HealthCare360.Platform.Integrations;

// Provider-neutral contracts for every external integration (SRS §5). Implementations live in the
// Integrations project and are chosen per facility in settings; each has a Simulator provider for
// training and testing without real credentials.

public sealed record SendResult(bool Success, string? ProviderMessageId = null, string? Error = null)
{
    public static SendResult Ok(string? providerMessageId = null) => new(true, providerMessageId);

    public static SendResult Fail(string error) => new(false, null, error);
}

// ---------- SMS ----------
public sealed record SmsMessage(string To, string Text, string? DltTemplateId = null, string? SenderId = null, string? Reference = null);

public interface ISmsSender
{
    string Provider { get; }

    Task<SendResult> SendAsync(SmsMessage message, CancellationToken cancellationToken);
}

// ---------- WhatsApp ----------
public sealed record WhatsAppTemplateMessage(string To, string TemplateName, string Language, IReadOnlyList<string> BodyParameters, string? Reference = null);

public sealed record WhatsAppDocumentMessage(string To, string FileName, byte[] Content, string ContentType, string? Caption = null, string? Reference = null);

public interface IWhatsAppSender
{
    string Provider { get; }

    Task<SendResult> SendTemplateAsync(WhatsAppTemplateMessage message, CancellationToken cancellationToken);

    Task<SendResult> SendDocumentAsync(WhatsAppDocumentMessage message, CancellationToken cancellationToken);
}

// ---------- E-mail ----------
public sealed record EmailAttachment(string FileName, byte[] Content, string ContentType);

public sealed record EmailMessage(string To, string Subject, string HtmlBody, IReadOnlyList<EmailAttachment>? Attachments = null, string? Reference = null);

public interface IEmailSender
{
    Task<SendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

// ---------- Payment gateway ----------
public sealed record CreatePaymentOrder(decimal Amount, string Currency, string Receipt, string Description, string? CustomerName, string? CustomerPhone, string? CustomerEmail, IReadOnlyDictionary<string, string>? Notes = null);

public sealed record PaymentOrder(string Provider, string OrderId, decimal Amount, string Currency, string? CheckoutKey, string? PaymentLinkUrl);

public enum GatewayPaymentStatus
{
    Created = 0,
    Pending,
    Captured,
    Failed,
    Refunded,
}

public sealed record PaymentStatusResult(string OrderId, GatewayPaymentStatus Status, string? PaymentId, decimal? AmountPaid, string? Method, string? Error);

public sealed record RefundResult(bool Success, string? RefundId, string? Error);

public interface IPaymentGateway
{
    string Provider { get; }

    Task<PaymentOrder> CreateOrderAsync(CreatePaymentOrder request, CancellationToken cancellationToken);

    /// <summary>A shareable link (SMS/WhatsApp) for remote payment of dues.</summary>
    Task<PaymentOrder> CreatePaymentLinkAsync(CreatePaymentOrder request, CancellationToken cancellationToken);

    Task<PaymentStatusResult> GetStatusAsync(string orderId, CancellationToken cancellationToken);

    /// <summary>Verifies a webhook or checkout signature with the provider secret (constant-time).</summary>
    bool VerifySignature(string payload, string signature);

    Task<RefundResult> RefundAsync(string paymentId, decimal amount, string reason, CancellationToken cancellationToken);
}

// ---------- Card/UPI POS terminal ----------
public sealed record PosSaleRequest(string TerminalId, decimal Amount, string Reference, string? Mode = null);

public enum PosTransactionStatus
{
    Initiated = 0,
    AwaitingCustomer,
    Approved,
    Declined,
    Cancelled,
    TimedOut,
}

public sealed record PosTransactionResult(string TransactionId, PosTransactionStatus Status, string? ApprovalCode, string? CardLast4, string? Rrn, string? Message);

public interface IPosTerminal
{
    string Provider { get; }

    Task<PosTransactionResult> StartSaleAsync(PosSaleRequest request, CancellationToken cancellationToken);

    Task<PosTransactionResult> GetStatusAsync(string transactionId, CancellationToken cancellationToken);

    Task<PosTransactionResult> CancelAsync(string transactionId, CancellationToken cancellationToken);
}

// ---------- Accounting (Tally) ----------
public sealed record AccountingEntry(string LedgerName, decimal Amount, bool IsDebit, string? CostCentre = null);

public sealed record AccountingVoucher(string VoucherType, DateOnly Date, string Number, string Narration, IReadOnlyList<AccountingEntry> Entries, string? Reference = null);

public sealed record AccountingExportResult(bool Success, int Created, int Altered, int Errors, string? Message);

public interface IAccountingExporter
{
    string Provider { get; }

    Task<bool> PingAsync(CancellationToken cancellationToken);

    Task<AccountingExportResult> PushVouchersAsync(IReadOnlyList<AccountingVoucher> vouchers, CancellationToken cancellationToken);

    /// <summary>Import file for manual loading when the accounting server is unreachable.</summary>
    string BuildImportFile(IReadOnlyList<AccountingVoucher> vouchers);
}

// ---------- PACS / DICOMweb ----------
public sealed record PacsStudyQuery(string? PatientId = null, string? AccessionNumber = null, DateOnly? From = null, DateOnly? To = null, string? Modality = null);

public sealed record ImagingStudyInfo(string StudyInstanceUid, string? AccessionNumber, string? PatientId, string? PatientName, DateTimeOffset? StudyDate, string? Modalities, string? Description, int? SeriesCount, int? InstanceCount);

public interface IPacsClient
{
    Task<bool> PingAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ImagingStudyInfo>> FindStudiesAsync(PacsStudyQuery query, CancellationToken cancellationToken);

    /// <summary>Viewer launch URL built from the configured template (for example OHIF/Weasis/vendor viewer).</summary>
    string? BuildViewerUrl(string studyInstanceUid, string? accessionNumber);
}

// ---------- ICD / procedure coding ----------
public sealed record IcdSearchResult(string Code, string Title, string Release, string? Chapter = null, bool IsLeaf = true);

public interface IIcdLookup
{
    /// <summary>Online lookup (WHO ICD-API). Local catalogue search is served by the Clinical module.</summary>
    Task<IReadOnlyList<IcdSearchResult>> SearchAsync(string text, string release, int limit, CancellationToken cancellationToken);
}

// ---------- Barcode / label printing ----------
public enum LabelSymbology
{
    Code128 = 0,
    QrCode,
}

public sealed record LabelRequest(string Value, LabelSymbology Symbology, IReadOnlyList<string> TextLines, int WidthMm = 50, int HeightMm = 25, int Copies = 1);

public interface ILabelPrinterService
{
    /// <summary>Raw printer language (ZPL for Zebra, TSPL for TSC) sent through the Desktop bridge.</summary>
    string Build(LabelRequest request, string language);
}

// ---------- AI ----------
public sealed record AiMessage(string Role, string Content);

public sealed record AiRequest(string Purpose, string SystemPrompt, IReadOnlyList<AiMessage> Messages, int MaxTokens = 1024, string? JsonSchema = null);

public sealed record AiCompletion(bool Success, string? Text, string? Model, int InputTokens, int OutputTokens, string? Error);

public interface IAiTextService
{
    bool IsEnabled { get; }

    Task<AiCompletion> CompleteAsync(AiRequest request, CancellationToken cancellationToken);
}

public interface ISpeechToText
{
    bool IsEnabled { get; }

    Task<string> TranscribeAsync(Stream audio, string contentType, string language, CancellationToken cancellationToken);
}
