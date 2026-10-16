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

    /// <summary>A payment link passed its expiry unpaid.</summary>
    Expired,

    /// <summary>A payment link was cancelled at the gateway.</summary>
    Cancelled,
}

/// <param name="PaidAt">When the gateway took the payment (the receipt's time), if it says.</param>
public sealed record PaymentStatusResult(
    string OrderId, GatewayPaymentStatus Status, string? PaymentId, decimal? AmountPaid, string? Method, string? Error, DateTimeOffset? PaidAt = null);

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

    /// <summary>
    /// Returns money to the payment's source. <paramref name="idempotencyKey"/> (at most 40 letters and digits) names
    /// the attempt: the gateway answers a repeated request with the same key with the refund it already made, so a
    /// request sent twice (a retry after a timeout, a second click) never refunds twice.
    /// </summary>
    Task<RefundResult> RefundAsync(
        string paymentId, decimal amount, string reason, string? idempotencyKey, Guid? facilityId,
        CancellationToken cancellationToken);

    /// <summary>Settlements paid to the bank on <paramref name="settledOn"/> (an Indian calendar day), with their transactions.</summary>
    Task<PaymentSettlementReport> GetSettlementsAsync(DateOnly settledOn, Guid? facilityId, CancellationToken cancellationToken);

    /// <summary>Where a refund stands at the gateway: pending until the bank confirms it, then processed or failed.</summary>
    Task<RefundStatusResult> GetRefundStatusAsync(string refundId, Guid? facilityId, CancellationToken cancellationToken);
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

/// <summary>
/// Every call names the facility whose terminal settings apply (null: the signed-in facility), because the background
/// re-check of sales the counter left open has no signed-in facility.
/// </summary>
public interface IPosTerminal
{
    string Provider { get; }

    Task<PosTransactionResult> StartSaleAsync(PosSaleRequest request, Guid? facilityId, CancellationToken cancellationToken);

    Task<PosTransactionResult> GetStatusAsync(string transactionId, Guid? facilityId, CancellationToken cancellationToken);

    Task<PosTransactionResult> CancelAsync(string transactionId, Guid? facilityId, CancellationToken cancellationToken);
}

// ---------- Biometric attendance terminals (read by HC360) ----------
/// <summary>A terminal HC360 reads punches from, as registered in HR.</summary>
/// <param name="FacilityId">Facility whose biometric settings apply.</param>
/// <param name="SerialNumber">The registered serial number: a terminal that reports another one is not read.</param>
/// <param name="Host">The terminal's IP address or host name on the device network.</param>
public sealed record AttendanceTerminal(Guid FacilityId, string SerialNumber, string Host, int Port);

/// <summary>One attendance record as the terminal holds it, in the terminal's own clock.</summary>
/// <param name="StatusCode">Punch state: 0 check-in, 1 check-out, 2 break-out, 3 break-in, 4 overtime-in, 5 overtime-out.</param>
/// <param name="VerifyMode">How the person was verified: 0 password, 1 fingerprint, 2 card, 15 face…</param>
public sealed record TerminalPunchRecord(string Pin, DateTime LocalTime, int StatusCode, int VerifyMode, string? WorkCode);

/// <param name="Error">Why the terminal could not be read, in words for HR staff.</param>
/// <param name="TerminalTime">The terminal's clock when it was read, to show how far it has drifted.</param>
/// <param name="Records">Every attendance record the terminal holds: terminals cannot be asked for a period.</param>
public sealed record AttendanceTerminalRead(bool Success, string? Error, DateTime? TerminalTime, IReadOnlyList<TerminalPunchRecord> Records)
{
    public static AttendanceTerminalRead Failed(string error) => new(false, error, null, []);
}

/// <summary>
/// Reads attendance terminals that cannot push to HC360 (INT-BIO "pull"). Only the attendance records and the clock
/// are read: users, fingerprints and faces are never asked for, and nothing on the terminal is changed apart from its
/// clock, when HR sets it.
/// </summary>
/// <param name="Provider"><c>zkteco</c>, <c>simulator</c>, or null when terminals cannot be read.</param>
/// <param name="Problem">Why terminals cannot be read, in words for HR staff.</param>
public sealed record AttendanceTerminalReading(string? Provider, string? Problem);

public interface IAttendanceTerminalClient
{
    /// <summary>How the facility's terminals are read, so HR sees before saving a terminal whether it can be.</summary>
    Task<AttendanceTerminalReading> ReadingAsync(Guid facilityId, CancellationToken cancellationToken);

    Task<AttendanceTerminalRead> ReadAsync(AttendanceTerminal terminal, CancellationToken cancellationToken);

    /// <summary>Sets the terminal's clock. Returns why it could not, or null.</summary>
    Task<string?> SetClockAsync(AttendanceTerminal terminal, DateTime localTime, CancellationToken cancellationToken);

    /// <summary>
    /// Training only: records a punch on the simulated terminal, as an employee would at a real one, for the next read
    /// to bring in. Returns why it could not (for example when the facility does not use the simulator), or null.
    /// </summary>
    Task<string?> PunchOnTrainingTerminalAsync(AttendanceTerminal terminal, string pin, DateTime localTime, CancellationToken cancellationToken);
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
/// <param name="StudyInstanceUid">One study by its UID (for a study whose accession number DICOM cannot carry).</param>
/// <param name="FacilityId">Facility whose PACS settings apply (null: the signed-in facility, then the all-facility settings).</param>
/// <param name="ExpectedPatientId">
/// The patient ID HC360 expects the study under. It is not a search key: a real PACS is searched without it, so a study
/// held under another patient is still found and refused. Only the training simulator uses it, answering with it as a
/// PACS holding the right study would.
/// </param>
public sealed record PacsStudyQuery(
    string? PatientId = null,
    string? AccessionNumber = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Modality = null,
    string? StudyInstanceUid = null,
    Guid? FacilityId = null,
    string? ExpectedPatientId = null);

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

/// <summary>
/// A JPEG or PNG image (<see cref="PacsImageStatus.Ok"/>), or why there is none. <see cref="Content"/> is the part of
/// the buffer the image was read into that it fills, so it is handed on without another copy.
/// </summary>
public sealed record PacsImage(
    PacsImageStatus Status, ReadOnlyMemory<byte> Content = default, string? ContentType = null);

/// <summary>
/// The patient and study that objects sent to the PACS are filed under (STOW-RS). HC360 writes these over the values the
/// machine recorded, so the objects reach the PACS under the patient's UHID and HC360's study; the machine's values are
/// kept in each object's Original Attributes Sequence. Values are already in DICOM form: the name as PN
/// (<c>FAMILY^GIVEN</c>), the birth date as <c>yyyyMMdd</c>, the sex as <c>M</c>, <c>F</c> or <c>O</c>, and an accession
/// number of at most 16 characters (null when HC360's is longer).
/// </summary>
public sealed record PacsStudyIdentity(
    string PatientId,
    string PatientName,
    string? BirthDate,
    string? Sex,
    string StudyInstanceUid,
    string? AccessionNumber,
    string? StudyDescription);

/// <summary>A file to send. <paramref name="Reference"/> is the caller's name for it (HC360's file id), echoed in its outcome.</summary>
public sealed record PacsStoreFile(string Reference, byte[] Content);

/// <param name="FacilityId">Facility whose PACS settings apply (null: the signed-in facility, then the all-facility settings).</param>
public sealed record PacsStoreRequest(PacsStudyIdentity Study, IReadOnlyList<PacsStoreFile> Files, Guid? FacilityId = null);

public enum PacsStoreStatus
{
    /// <summary>The PACS holds the object.</summary>
    Stored = 0,

    /// <summary>The PACS answered and refused the object; the message says why.</summary>
    Refused,

    /// <summary>The object did not reach the PACS (unreachable, timed out, a server error); sending it again may work.</summary>
    Failed,

    /// <summary>Not a DICOM object HC360 can send: unreadable, or without its SOP class and instance UIDs.</summary>
    NotDicom,
}

/// <summary>
/// What became of one file sent to the PACS. <paramref name="SopInstanceUid"/> is the object's UID when HC360 could read
/// it. <paramref name="Message"/> says why it was not stored, or the PACS's warning about one it stored.
/// </summary>
public sealed record PacsStoreOutcome(string Reference, PacsStoreStatus Status, string? SopInstanceUid, string? Message);

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
    /// Sends DICOM objects to the PACS (STOW-RS), one request per object, filed under <see cref="PacsStoreRequest.Study"/>.
    /// Returns one outcome per file, in order: a file that is not DICOM, or that the PACS refuses or cannot be reached
    /// for, gets its own outcome and the others are still sent. Sending an object again is safe: the PACS keeps one copy
    /// per SOP instance UID.
    /// </summary>
    /// <exception cref="HttpRequestException">PACS is not configured or disabled; the message is safe to show to staff.</exception>
    Task<IReadOnlyList<PacsStoreOutcome>> StoreAsync(PacsStoreRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The PACS that objects sent now for the facility go to: its provider and STOW-RS address (no credentials). A
    /// caller records it with each outcome, so an object stored in another PACS (before the PACS was changed, or in the
    /// simulator while training) is not taken as being in this one.
    /// </summary>
    /// <exception cref="HttpRequestException">PACS is not configured or disabled; the message is safe to show to staff.</exception>
    Task<string> StoreDestinationAsync(Guid? facilityId, CancellationToken cancellationToken);

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

/// <param name="Dpi">The label printer's resolution: 203 or 300 dots per inch.</param>
public sealed record LabelRequest(string Value, LabelSymbology Symbology, IReadOnlyList<string> TextLines, int WidthMm = 50, int HeightMm = 25, int Copies = 1, int Dpi = 203);

public interface ILabelPrinterService
{
    /// <summary>Raw printer language (ZPL for Zebra, TSPL for TSC) that HC360 Desktop sends to the label printer.</summary>
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
