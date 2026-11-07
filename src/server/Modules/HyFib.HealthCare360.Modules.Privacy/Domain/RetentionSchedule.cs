using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Privacy.Domain;

/// <summary>What happens to data of a kind once it is past its retention period.</summary>
public enum RetentionAction
{
    /// <summary>Deleted automatically by the nightly clean-up.</summary>
    Delete = 0,

    /// <summary>Listed for the medical records team to review; never deleted automatically.</summary>
    Review,

    /// <summary>Kept; nothing is removed.</summary>
    Keep,
}

public enum RetentionUnit
{
    Days = 0,
    Years,
}

/// <summary>
/// One line of the retention schedule. A null <see cref="DefaultValue"/> means "kept permanently". The minimum comes
/// from the law or the SRS: the period can be lengthened, never shortened below it.
/// </summary>
/// <param name="SystemManaged">
/// Kept permanently until super administrators set a period, two people agreeing, outside this schedule's own editing
/// (the audit trail: <see cref="AuditRetention"/>). The period stored is then in force, within its range.
/// </param>
public sealed record RetentionRule(
    string Key,
    string Title,
    string Description,
    string Basis,
    RetentionAction Action,
    RetentionUnit Unit,
    int? DefaultValue,
    int? MinimumValue,
    int MaximumValue,
    bool Editable,
    bool SystemManaged = false)
{
    /// <summary>The setting that holds a changed period.</summary>
    public string SettingKey => "privacy.retention." + Key;

    /// <summary>
    /// The period in force: the stored value within the allowed range, else the default. A rule kept permanently by
    /// default stays so, whatever is stored, unless super administrators set its period (<see cref="SystemManaged"/>).
    /// </summary>
    public int? Effective(int? stored) => DefaultValue is { } fallback
        ? Math.Clamp(stored ?? fallback, MinimumValue ?? 1, MaximumValue)
        : SystemManaged && stored is { } set ? Math.Clamp(set, MinimumValue ?? 1, MaximumValue) : null;

    public DateTimeOffset Cutoff(DateTimeOffset now, int value) =>
        Unit == RetentionUnit.Years ? now.AddYears(-value) : now.AddDays(-value);
}

/// <summary>
/// How long HC360 keeps each kind of data (SRS PRV-02, DPDP Act s8(7)). Only operational records are deleted
/// automatically: medical records are reviewed by the medical records team after their period, and medico-legal
/// records, the audit trail and financial records are kept. Documents, deleted ones included, stay with the medical
/// record: the file store keeps a deleted document's encrypted file for medico-legal reasons. The defaults are a
/// starting point for the hospital's medical records committee and legal adviser to confirm.
/// </summary>
public static class RetentionSchedule
{
    public const string Outbox = "messages.outbox";
    public const string MessageLog = "messages.log";
    public const string IntegrationLog = "integrations.log";
    public const string SignInLog = "signin.log";
    public const string OutpatientRecords = "records.outpatient";
    public const string InpatientRecords = "records.inpatient";
    public const string MedicoLegalRecords = "records.mlc";
    public const string Finance = "finance";
    public const string Audit = "audit";
    public const string Backups = "backups";

    private const string LogsBasis = "DPDP Rules 2025 (rules 6 and 8): logs of processing are kept for at least one year.";

    /// <summary>
    /// How long logs are kept at the least: a year has at most 366 days, so 366 keeps every line a full year, across a
    /// leap day too. 365 would delete the line a day early whenever the year held a 29 February.
    /// </summary>
    public const int LogDays = 366;

    public static IReadOnlyList<RetentionRule> Rules { get; } =
    [
        new(Outbox, "Messages being sent",
            "The full text of each SMS, WhatsApp message and e-mail, sign-in codes included, kept while it is sent. The message log keeps a record without the text.",
            "Needed only to deliver the message.", RetentionAction.Delete, RetentionUnit.Days, 30, 7, 365, Editable: true),
        new(MessageLog, "Message log",
            "Which message went to which number or address (masked), and whether it was delivered.",
            LogsBasis, RetentionAction.Delete, RetentionUnit.Days, LogDays, LogDays, 3650, Editable: true),
        new(IntegrationLog, "Integration log",
            "Calls to laboratory machines and to payment, messaging and other providers, with personal data removed from what was sent, and the payment gateway's notifications (their ids only).",
            LogsBasis, RetentionAction.Delete, RetentionUnit.Days, LogDays, LogDays, 3650, Editable: true),
        new(SignInLog, "Sign-in records",
            "Requests for sign-in codes, sessions that have ended (when, from which address and on which device), and emergency access to restricted records once it has ended (the audit trail keeps every one).",
            LogsBasis, RetentionAction.Delete, RetentionUnit.Days, LogDays, LogDays, 3650, Editable: true),
        new(OutpatientRecords, "Out-patient medical records",
            "The chart of each out-patient visit, counted from when the chart was completed.",
            "At least 3 years (SRS PRV-02). Check the state's rules and NABH before changing it.",
            RetentionAction.Review, RetentionUnit.Years, RecordRetention.MinimumOutpatientYears, RecordRetention.MinimumOutpatientYears,
            RecordRetention.MaximumYears, Editable: true),
        new(InpatientRecords, "In-patient medical records",
            "The chart of each hospital stay, counted from discharge.",
            "At least 5 years (SRS PRV-02). Check the state's rules and NABH before changing it.",
            RetentionAction.Review, RetentionUnit.Years, RecordRetention.MinimumInpatientYears, RecordRetention.MinimumInpatientYears,
            RecordRetention.MaximumYears, Editable: true),
        new(MedicoLegalRecords, "Medico-legal records",
            "Charts of medico-legal cases.",
            "Kept permanently (SRS PRV-02).", RetentionAction.Keep, RetentionUnit.Years, null, null, RecordRetention.MaximumYears, Editable: false),
        new(Finance, "Bills, payments and insurance claims",
            "Bills, receipts, refunds and insurance claims, counted from the bill date.",
            "Income-tax and GST law require accounts to be kept for several years; 8 years covers both.",
            RetentionAction.Keep, RetentionUnit.Years, 8, 8, RecordRetention.MaximumYears, Editable: true),
        new(Audit, "Audit trail",
            "Who opened, changed, printed or exported each record, and when.",
            "Tamper-evident and never edited; kept permanently, as the hospital decided (ADR-015), unless two super administrators set a period of at least 8 years: "
            + "it also records changes to bills and payments, whose books of account and audit trail are kept 8 years (Companies Act 2013 s.128(5)). "
            + "Old months are removed only by a purge a second super administrator approves, exported first.",
            RetentionAction.Keep, RetentionUnit.Years, null, AuditRetention.MinimumYears, AuditRetention.MaximumYears, Editable: false, SystemManaged: true),
        new(Backups, "Database backups",
            "Encrypted copies of the whole database. Data erased from HC360 leaves the backups as they expire.",
            "Set in the server's backup settings (HealthCare360:Backup:RetentionDays).", RetentionAction.Delete, RetentionUnit.Days, 30, 1, 3650,
            Editable: false),
    ];

    public static RetentionRule Get(string key) => Rules.First(r => r.Key == key);

    public static RetentionRule? Find(string key) => Rules.FirstOrDefault(r => r.Key == key);
}
