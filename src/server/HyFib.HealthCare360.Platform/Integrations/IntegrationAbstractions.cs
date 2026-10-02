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
/// <param name="FacilityId">Facility whose SMS settings apply (null: the signed-in facility, then the all-facility settings).</param>
public sealed record SmsMessage(string To, string Text, string? DltTemplateId = null, string? SenderId = null, string? Reference = null, Guid? FacilityId = null);

public interface ISmsSender
{
    string Provider { get; }

    Task<SendResult> SendAsync(SmsMessage message, CancellationToken cancellationToken);
}

// ---------- WhatsApp ----------
/// <param name="HeaderDocument">
/// For a template approved with a document header (a report's PDF, say): the file sent at the top of the message.
/// Unlike a free-form document, it reaches the person outside the 24-hour customer-service window.
/// </param>
public sealed record WhatsAppTemplateMessage(
    string To,
    string TemplateName,
    string Language,
    IReadOnlyList<string> BodyParameters,
    string? Reference = null,
    Guid? FacilityId = null,
    WhatsAppHeaderDocument? HeaderDocument = null);

public sealed record WhatsAppHeaderDocument(string FileName, byte[] Content, string ContentType);

public sealed record WhatsAppDocumentMessage(string To, string FileName, byte[] Content, string ContentType, string? Caption = null, string? Reference = null, Guid? FacilityId = null);

/// <summary>
/// Free-form text. WhatsApp accepts it only within 24 hours of the person's last message to the hospital (the
/// customer-service window), so it suits replies; everything else must be an approved template.
/// </summary>
public sealed record WhatsAppTextMessage(string To, string Text, string? Reference = null, Guid? FacilityId = null);

public interface IWhatsAppSender
{
    string Provider { get; }

    Task<SendResult> SendTemplateAsync(WhatsAppTemplateMessage message, CancellationToken cancellationToken);

    Task<SendResult> SendDocumentAsync(WhatsAppDocumentMessage message, CancellationToken cancellationToken);

    Task<SendResult> SendTextAsync(WhatsAppTextMessage message, CancellationToken cancellationToken);
}

// ---------- E-mail ----------
public sealed record EmailAttachment(string FileName, byte[] Content, string ContentType);

public sealed record EmailMessage(string To, string Subject, string HtmlBody, IReadOnlyList<EmailAttachment>? Attachments = null, string? Reference = null, Guid? FacilityId = null);

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

    // Every call names the facility whose merchant account applies (null: the signed-in user's facility, then the
    // all-facility settings). Webhooks, patient checkouts and background re-checks have no signed-in facility, and a
    // facility may have its own account and webhook secret, so callers pass the facility of the order.

    Task<PaymentOrder> CreateOrderAsync(CreatePaymentOrder request, Guid? facilityId, CancellationToken cancellationToken);

    /// <summary>A shareable link (SMS/WhatsApp) for remote payment of dues.</summary>
    Task<PaymentOrder> CreatePaymentLinkAsync(CreatePaymentOrder request, Guid? facilityId, CancellationToken cancellationToken);

    Task<PaymentStatusResult> GetStatusAsync(string orderId, Guid? facilityId, CancellationToken cancellationToken);

    /// <summary>Verifies a webhook or checkout signature with the provider secret of the facility's account (constant-time).</summary>
    bool VerifySignature(string payload, string signature, Guid? facilityId);

    Task<RefundResult> RefundAsync(string paymentId, decimal amount, string reason, Guid? facilityId, CancellationToken cancellationToken);
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
/// <param name="StudyInstanceUid">One study by its UID (for a study whose accession number DICOM cannot carry).</param>
/// <param name="FacilityId">Facility whose PACS settings apply (null: the signed-in facility, then the all-facility settings).</param>
public sealed record PacsStudyQuery(
    string? PatientId = null,
    string? AccessionNumber = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Modality = null,
    string? StudyInstanceUid = null,
    Guid? FacilityId = null);

public sealed record ImagingStudyInfo(string StudyInstanceUid, string? AccessionNumber, string? PatientId, string? PatientName, DateTimeOffset? StudyDate, string? Modalities, string? Description, int? SeriesCount, int? InstanceCount);

/// <summary>One series of a study as the PACS reports it (QIDO-RS).</summary>
public sealed record ImagingSeriesInfo(string SeriesInstanceUid, string? Modality, int? SeriesNumber, string? Description, string? BodyPart, int? InstanceCount);

/// <summary>One object of a series (QIDO-RS). <paramref name="Frames"/> is 1 for single-frame images.</summary>
public sealed record ImagingInstanceInfo(string SopInstanceUid, string? SopClassUid, int? InstanceNumber, int Frames, int? Rows, int? Columns);

public enum PacsImageKind
{
    /// <summary>The image, rendered by the PACS as JPEG or PNG (WADO-RS rendered).</summary>
    Rendered = 0,

    /// <summary>A small picture of a series (WADO-RS thumbnail).</summary>
    Thumbnail,
}

/// <summary>
/// An image to fetch through the PACS. Without <paramref name="SopInstanceUid"/> it is the thumbnail of the series.
/// <paramref name="Frame"/> is 1-based, for multi-frame objects. <paramref name="Size"/> is the longest side wanted, in
/// pixels; the PACS fits the image inside it.
/// </summary>
/// <param name="FacilityId">Facility whose PACS settings apply (null: the signed-in facility, then the all-facility settings).</param>
public sealed record PacsImageRequest(
    string StudyInstanceUid,
    string SeriesInstanceUid,
    string? SopInstanceUid,
    int? Frame,
    PacsImageKind Kind,
    int Size,
    Guid? FacilityId = null);

public enum PacsImageStatus
{
    Ok = 0,

    /// <summary>The PACS has no such object.</summary>
    NotFound,

    /// <summary>The PACS does not offer this kind of image (many have no thumbnails).</summary>
    NotAvailable,
}

/// <summary>A JPEG or PNG image (<see cref="PacsImageStatus.Ok"/>), or why there is none.</summary>
public sealed record PacsImage(PacsImageStatus Status, byte[]? Content = null, string? ContentType = null);

/// <summary>
/// The PACS (INT-PACS, IMG-02). Searches and image retrieval that cannot run (PACS not configured, disabled, unreachable,
/// or an answer HC360 will not pass on) throw <see cref="HttpRequestException"/> whose message is safe to show to staff.
/// Credentials stay on the server: callers get study data and finished images, never a PACS address.
/// </summary>
public interface IPacsClient
{
    Task<bool> PingAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ImagingStudyInfo>> FindStudiesAsync(PacsStudyQuery query, CancellationToken cancellationToken);

    /// <summary>The series of a study (QIDO-RS).</summary>
    Task<IReadOnlyList<ImagingSeriesInfo>> FindSeriesAsync(string studyInstanceUid, Guid? facilityId, CancellationToken cancellationToken);

    /// <summary>The objects of a series, in instance-number order (QIDO-RS).</summary>
    Task<IReadOnlyList<ImagingInstanceInfo>> FindInstancesAsync(string studyInstanceUid, string seriesInstanceUid, Guid? facilityId, CancellationToken cancellationToken);

    /// <summary>A rendered image or a thumbnail (WADO-RS): only JPEG or PNG, within a size limit.</summary>
    Task<PacsImage> RetrieveImageAsync(PacsImageRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Viewer launch URL built from the configured template (for example OHIF/Weasis/vendor viewer). Null when no
    /// template is configured, or when it needs a value that is not given: a template with <c>{studyUid}</c> needs the
    /// study UID, one with <c>{accession}</c> the accession number.
    /// </summary>
    string? BuildViewerUrl(string? studyInstanceUid, string? accessionNumber);
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
