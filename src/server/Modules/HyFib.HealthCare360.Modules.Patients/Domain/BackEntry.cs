namespace HyFib.HealthCare360.Modules.Patients.Domain;

/// <summary>
/// The kinds of record entered afterwards from paper, each with its own back-entry window: how far back the time on the
/// paper may be when it is entered (<see cref="BackEntryWindows"/>). Past the window an entry needs a second person's
/// approval first (<see cref="LateBackEntryGrant"/>).
/// </summary>
public enum BackEntryRecord
{
    /// <summary>A patient registered on paper under a downtime UHID.</summary>
    Registration = 0,

    /// <summary>A visit checked in on paper under a downtime visit number.</summary>
    Visit,

    /// <summary>A bill written on paper under a downtime bill number.</summary>
    Bill,

    /// <summary>A receipt written in the manual receipt book under a downtime receipt number.</summary>
    Receipt,

    /// <summary>An admission dated in the past.</summary>
    Admission,

    /// <summary>A bed transfer dated in the past.</summary>
    Transfer,

    /// <summary>A consultant's ward visit dated in the past.</summary>
    ConsultantVisit,

    /// <summary>Nursing notes and intake/output charted for a time in the past.</summary>
    NursingChart,

    /// <summary>A pharmacy sale written on paper at the counter under a downtime pharmacy sale number.</summary>
    PharmacySale,
}

/// <summary>
/// One record type's window: its setting, what HC360 does with none set, and the longest an administrator may set
/// (bounded, so a window cannot be opened without limit; past it, a second person approves each late batch).
/// </summary>
public sealed record BackEntryWindow(BackEntryRecord Record, string Name, int DefaultHours, int MaxHours)
{
    public string SettingKey => BackEntryWindows.SettingKey(Record);

    /// <summary>The hours HC360 acts on for a value set: the value when it is from 1 to the maximum, the default for any other.</summary>
    public int Effective(int? hours) => hours is { } h && h >= 1 && h <= MaxHours ? h : DefaultHours;
}

/// <summary>The back-entry windows (NABH IMS: records kept on paper during a downtime are entered afterwards).</summary>
public static class BackEntryWindows
{
    public const string SettingPrefix = "downtime.backEntryHours.";

    /// <summary>The longest window an administrator may set for any record but a registration: 14 days.</summary>
    public const int MaxHours = 14 * 24;

    /// <summary>
    /// A paper registration keeps the 90 days it had before windows were configurable: the UHID is the patient's for
    /// life, and the block it came from already bounds the date (never before the block was issued).
    /// </summary>
    public const int RegistrationHours = 90 * 24;

    public static IReadOnlyList<BackEntryWindow> All { get; } =
    [
        new(BackEntryRecord.Registration, "Registration from paper", RegistrationHours, RegistrationHours),
        new(BackEntryRecord.Visit, "Visit from paper", 72, MaxHours),
        new(BackEntryRecord.Bill, "Bill from paper", 72, MaxHours),
        new(BackEntryRecord.Receipt, "Receipt from the manual receipt book", 72, MaxHours),
        new(BackEntryRecord.Admission, "Admission time", 72, MaxHours),
        new(BackEntryRecord.Transfer, "Bed transfer time", 24, MaxHours),
        new(BackEntryRecord.ConsultantVisit, "Consultant's ward visit time", 48, MaxHours),
        new(BackEntryRecord.NursingChart, "Nursing notes and intake/output", 24, MaxHours),
        new(BackEntryRecord.PharmacySale, "Pharmacy sale from paper", 72, MaxHours),
    ];

    public static BackEntryWindow For(BackEntryRecord record) => All.First(w => w.Record == record);

    public static string SettingKey(BackEntryRecord record) => SettingPrefix + record switch
    {
        BackEntryRecord.Registration => "registration",
        BackEntryRecord.Visit => "visit",
        BackEntryRecord.Bill => "bill",
        BackEntryRecord.Receipt => "receipt",
        BackEntryRecord.Admission => "admission",
        BackEntryRecord.Transfer => "transfer",
        BackEntryRecord.ConsultantVisit => "consultantVisit",
        BackEntryRecord.NursingChart => "nursingChart",
        BackEntryRecord.PharmacySale => "pharmacySale",
        _ => throw new ArgumentOutOfRangeException(nameof(record)),
    };
}

/// <summary>
/// A second person's approval to enter records older than their window: one user, one record type, paper dated within
/// a span, at most so many records, for a few days after it is approved. After a long downtime the area in charge asks
/// once for the batch (with the count from the back-entry tally) rather than for every form.
/// </summary>
public sealed class LateBackEntryGrant : AuditableEntity
{
    /// <summary>How long an approved grant can be used.</summary>
    public static readonly TimeSpan UsableFor = TimeSpan.FromDays(3);

    public Guid FacilityId { get; set; }
    public BackEntryRecord Record { get; set; }

    /// <summary>The earliest and latest time on the paper the grant covers.</summary>
    public DateTimeOffset PaperFrom { get; set; }

    public DateTimeOffset PaperTo { get; set; }

    /// <summary>How many records it covers, and how many have been entered under it.</summary>
    public int MaxRecords { get; set; }

    public int Used { get; set; }

    /// <summary>Who asked, and the only one who may enter under it.</summary>
    public Guid RequestedBy { get; set; }

    public string RequestedByName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public Guid ApprovalRequestId { get; set; }
    public Guid? ApprovedBy { get; set; }
    public string? ApprovedByName { get; set; }
    public DateTimeOffset ApprovedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    public bool Covers(BackEntryRecord record, Guid userId, DateTimeOffset paperAt, DateTimeOffset now) =>
        Record == record && RequestedBy == userId && paperAt >= PaperFrom && paperAt <= PaperTo && now < ExpiresAt && Used < MaxRecords;
}
