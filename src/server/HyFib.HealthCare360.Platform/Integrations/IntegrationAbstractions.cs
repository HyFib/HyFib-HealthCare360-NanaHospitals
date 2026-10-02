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

/// <param name="Reference">
/// HC360's stable key for the voucher (a bill, receipt or refund number). The accounting system keeps it as the voucher's
/// remote id (<see cref="RemoteId"/>), so sending the same voucher again alters it instead of adding a copy.
/// </param>
public sealed record AccountingVoucher(string VoucherType, DateOnly Date, string Number, string Narration, IReadOnlyList<AccountingEntry> Entries, string? Reference = null)
{
    /// <summary>Starts every remote id HC360 gives the accounting system, which tells HC360's vouchers from the hospital's own.</summary>
    public const string RemoteIdPrefix = "HC360-";

    /// <summary>The id the accounting system keeps for the voucher (Tally's <c>REMOTEID</c>); null without a reference.</summary>
    public string? RemoteId => string.IsNullOrWhiteSpace(Reference) ? null : RemoteIdPrefix + Reference.Trim();

    /// <summary>The key HC360 tracks the voucher by: its reference, else its number.</summary>
    public string Key => string.IsNullOrWhiteSpace(Reference) ? Number.Trim() : Reference.Trim();

    /// <summary>The voucher's value: the total of its debit lines, which equals its credit lines when it balances.</summary>
    public decimal Amount => Entries.Where(e => e.IsDebit).Sum(e => e.Amount);
}

/// <summary>What the accounting system did with one voucher it was sent.</summary>
public enum AccountingVoucherOutcome
{
    /// <summary>Not sent: the import stopped before it, because the accounting system could not be reached.</summary>
    NotSent = 0,

    Created,
    Altered,

    /// <summary>Accepted in a batch whose counts do not say whether it was created or altered.</summary>
    Accepted,

    /// <summary>Refused; the answer's message says why.</summary>
    Rejected,

    /// <summary>Sent, but the answer does not account for it (the accounting system ignored it, for example).</summary>
    Unconfirmed,
}

/// <param name="Key">The voucher's <see cref="AccountingVoucher.Key"/>.</param>
public sealed record AccountingVoucherAnswer(string Key, AccountingVoucherOutcome Outcome, string? Message = null);

public sealed record AccountingExportResult(bool Success, int Created, int Altered, int Errors, string? Message)
{
    /// <summary>The answer for each voucher, in the order sent; empty when nothing was sent.</summary>
    public IReadOnlyList<AccountingVoucherAnswer> Vouchers { get; init; } = [];

    /// <summary>No accounting system is set up for the facility, so nothing was sent and a retry would not help.</summary>
    public bool NotConfigured { get; init; }
}

public enum AccountingConnectionState
{
    /// <summary>No accounting system is set up for the facility, or it is switched off: day books are imported from a file.</summary>
    NotConfigured = 0,

    Ready,

    /// <summary>Set up, but the settings cannot be used; the message says why.</summary>
    Invalid,
}

public sealed record AccountingConnection(AccountingConnectionState State, string? Provider, string? Message);

/// <summary>How a voucher HC360 sent compares with the accounting system's copy.</summary>
public enum AccountingVoucherMatch
{
    Matched = 0,

    /// <summary>Not in the accounting system on the voucher's date: deleted there, moved to another date, or never sent.</summary>
    Missing,

    /// <summary>Present, with another amount.</summary>
    AmountDiffers,

    /// <summary>Present with the same amount, but another voucher type, date or other ledgers.</summary>
    Changed,

    /// <summary>Cancelled, or made optional, in the accounting system, so it is not in the books.</summary>
    NotInBooks,
}

/// <param name="Key">The voucher's <see cref="AccountingVoucher.Key"/>.</param>
/// <param name="FoundAmount">The amount the accounting system holds, when it holds the voucher.</param>
/// <param name="FoundNumber">The accounting system's voucher number (it may renumber vouchers it numbers itself).</param>
public sealed record AccountingVoucherCheck(string Key, AccountingVoucherMatch Match, decimal? FoundAmount, string? FoundNumber, string? Detail);

/// <summary>
/// A voucher the accounting system holds on the date with an HC360 remote id that was not among the vouchers compared:
/// left over from an earlier version of a day book, or sent for another facility.
/// </summary>
/// <param name="Key">The remote id without <see cref="AccountingVoucher.RemoteIdPrefix"/>.</param>
public sealed record AccountingVoucherFound(string Key, string? VoucherType, string? Number, DateOnly Date, decimal Amount, bool InBooks);

public sealed record AccountingReadBack(bool Success, string? Message, IReadOnlyList<AccountingVoucherCheck> Vouchers, IReadOnlyList<AccountingVoucherFound> Others)
{
    /// <summary>No accounting system is set up for the facility, so nothing could be read.</summary>
    public bool NotConfigured { get; init; }
}

public interface IAccountingExporter
{
    string Provider { get; }

    Task<bool> PingAsync(CancellationToken cancellationToken);

    /// <summary>Whether an accounting system is set up for the facility (null: the signed-in facility, then the all-facility settings).</summary>
    Task<AccountingConnection> GetConnectionAsync(Guid? facilityId, CancellationToken cancellationToken);

    /// <summary>
    /// Sends vouchers with the facility's settings (null: the signed-in facility, then the all-facility settings). Background
    /// posting has no signed-in facility, so it names the facility whose day book it sends.
    /// </summary>
    Task<AccountingExportResult> PushVouchersAsync(IReadOnlyList<AccountingVoucher> vouchers, Guid? facilityId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads back the vouchers the accounting system holds for <paramref name="date"/> and compares each of
    /// <paramref name="expected"/> with its copy there, by remote id: present, amount, type and ledgers.
    /// </summary>
    Task<AccountingReadBack> ReadBackAsync(DateOnly date, IReadOnlyList<AccountingVoucher> expected, Guid? facilityId, CancellationToken cancellationToken);

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
