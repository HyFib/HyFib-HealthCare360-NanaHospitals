using System.Globalization;

namespace HyFib.HealthCare360.Modules.Clinical.Domain.Idsp;

/// <summary>The IHIP form a case is reported on: presumptive (a doctor's diagnosis) or laboratory-confirmed.</summary>
public enum IdspForm
{
    Presumptive = 0,
    Laboratory,
}

public enum NotifiableCaseSource
{
    Diagnosis = 0,
    LabResult,
    Manual,
}

public enum NotifiableCaseStatus
{
    Open = 0,

    /// <summary>Ruled out or recorded on the wrong patient; it leaves the forms, the reason kept.</summary>
    Discarded,
}

/// <summary>
/// The IDSP reporting week: Monday to Sunday by the date in India, numbered as ISO 8601 numbers weeks, so week 1 is
/// the one with the year's first Thursday. A week's P and L forms are due the Monday after it ends.
/// </summary>
public readonly record struct IdspWeek(int Year, int Week)
{
    public static IdspWeek Of(DateOnly date) => new(ISOWeek.GetYear(date.ToDateTime(TimeOnly.MinValue)), ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue)));

    public static IdspWeek Of(DateTimeOffset at) => Of(VitalRegistration.LocalDate(at));

    public static bool IsValid(int year, int week) => year is >= 2000 and <= 9999 && week >= 1 && week <= ISOWeek.GetWeeksInYear(year);

    /// <summary>Year and week as one sortable number (202643), stored on the case for the week's query.</summary>
    public int Key => (Year * 100) + Week;

    public DateOnly Monday => DateOnly.FromDateTime(ISOWeek.ToDateTime(Year, Week, DayOfWeek.Monday));

    public DateOnly Sunday => Monday.AddDays(6);

    public DateOnly DueOn => Sunday.AddDays(1);

    public IdspWeek Previous => Of(Monday.AddDays(-1));

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Year}-W{Week:00}");
}

/// <summary>
/// One patient's episode of a notifiable disease (IDSP). A diagnosis presumes it (P form), a released positive test
/// confirms it (L form); both may happen, in either order, and the case is then on both forms, each in the week it
/// happened. A second diagnosis or result of the same disease within the disease's episode
/// (<see cref="IdspDisease.EpisodeDays"/>) belongs to the same case.
/// </summary>
public sealed class NotifiableCase : AuditableEntity, IFacilityScoped
{
    /// <summary>
    /// Within this many days of the case's last event, the same disease in the same patient is the same episode, unless
    /// the disease sets its own (tuberculosis: a year).
    /// </summary>
    public const int EpisodeDays = 30;

    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }
    public string DiseaseCode { get; set; } = string.Empty;
    public NotifiableCaseSource Source { get; set; }
    public NotifiableCaseStatus Status { get; private set; }
    public DateOnly? OnsetDate { get; set; }

    public DateTimeOffset? PresumedAt { get; private set; }
    public int? PresumedWeek { get; private set; }
    public Guid? DiagnosisId { get; private set; }
    public string? IcdCode { get; private set; }

    public DateTimeOffset? ConfirmedAt { get; private set; }
    public int? ConfirmedWeek { get; private set; }
    public Guid? LabResultId { get; private set; }
    public string? TestName { get; private set; }
    public string? TestResult { get; private set; }

    /// <summary>When the surveillance officer was alerted: about an outbreak-prone disease, or a TB case to notify.</summary>
    public DateTimeOffset? AlertedAt { get; private set; }

    /// <summary>For tuberculosis: the Ni-kshay ID the case was notified under, and the day it was notified.</summary>
    public string? NikshayId { get; private set; }

    public DateOnly? NikshayNotifiedOn { get; private set; }
    public Guid? NikshayRecordedBy { get; private set; }
    public DateTimeOffset? NikshayRecordedAt { get; private set; }

    public string? DiscardReason { get; private set; }
    public Guid? DiscardedBy { get; private set; }
    public DateTimeOffset? DiscardedAt { get; private set; }

    public DateTimeOffset LastEventAt => ConfirmedAt is { } c && (PresumedAt is not { } p || c > p) ? c : PresumedAt ?? CreatedAt;

    /// <summary>A diagnosis of this disease, made at <paramref name="at"/>; a later diagnosis of a case already presumed changes nothing.</summary>
    public void Presume(Guid? diagnosisId, string? icdCode, DateTimeOffset at)
    {
        if (PresumedAt is not null)
        {
            return;
        }

        PresumedAt = at;
        PresumedWeek = IdspWeek.Of(at).Key;
        DiagnosisId = diagnosisId;
        IcdCode = icdCode;
    }

    /// <summary>A released positive result of a test that confirms this disease; the first one confirms the case.</summary>
    public void Confirm(Guid? labResultId, string testName, string result, DateTimeOffset at)
    {
        if (ConfirmedAt is not null)
        {
            return;
        }

        ConfirmedAt = at;
        ConfirmedWeek = IdspWeek.Of(at).Key;
        LabResultId = labResultId;
        TestName = testName.Length > 200 ? testName[..200] : testName;
        TestResult = result.Length > 200 ? result[..200] : result;
    }

    /// <summary>
    /// The confirming result was amended to negative: the case leaves the L form. A case that was only ever confirmed is
    /// then not a case at all: it is discarded, keeping the result it was opened on.
    /// </summary>
    public void WithdrawConfirmation(string reason, DateTimeOffset at)
    {
        if (PresumedAt is null)
        {
            if (Status == NotifiableCaseStatus.Open)
            {
                Status = NotifiableCaseStatus.Discarded;
                DiscardReason = reason;
                DiscardedAt = at;
            }

            return;
        }

        ConfirmedAt = null;
        ConfirmedWeek = null;
        LabResultId = null;
        TestName = null;
        TestResult = null;
    }

    public void MarkAlerted(DateTimeOffset at) => AlertedAt ??= at;

    /// <summary>
    /// Records that the tuberculosis case was notified on Ni-kshay, under <paramref name="nikshayId"/>, on
    /// <paramref name="notifiedOn"/>; recording it again corrects it. <paramref name="today"/> is the hospital's date.
    /// </summary>
    public Result RecordNikshay(string nikshayId, DateOnly notifiedOn, DateOnly today, Guid? by, DateTimeOffset at)
    {
        if (DiseaseCode != IdspDiseases.TuberculosisCode)
        {
            return Error.Rule("idsp.not_nikshay", "Only tuberculosis is notified on Ni-kshay.");
        }

        if (Status == NotifiableCaseStatus.Discarded)
        {
            return Error.Rule("idsp.case_discarded", "This case is discarded.");
        }

        var id = nikshayId.Trim();
        if (id.Length is 0 or > 32 || !id.All(char.IsAsciiLetterOrDigit))
        {
            return Error.Field("nikshayId", "Enter the Ni-kshay ID as Ni-kshay gave it: letters and digits.");
        }

        var first = VitalRegistration.LocalDate(PresumedAt ?? ConfirmedAt ?? CreatedAt);
        if (notifiedOn > today || notifiedOn < first)
        {
            return Error.Field("notifiedOn", $"Give the day it was notified: from {first:dd-MM-yyyy}, and not later than today.");
        }

        NikshayId = id;
        NikshayNotifiedOn = notifiedOn;
        NikshayRecordedBy = by;
        NikshayRecordedAt = at;
        return Result.Success();
    }

    public Result Discard(string reason, Guid? by, DateTimeOffset at)
    {
        if (Status == NotifiableCaseStatus.Discarded)
        {
            return Error.Rule("idsp.case_discarded", "This case is already discarded.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Field("reason", "Say why the case is discarded.");
        }

        Status = NotifiableCaseStatus.Discarded;
        DiscardReason = reason.Trim();
        DiscardedBy = by;
        DiscardedAt = at;
        return Result.Success();
    }

    /// <summary>Whether the case is on <paramref name="form"/> for <paramref name="week"/>.</summary>
    public bool IsOn(IdspForm form, IdspWeek week) =>
        Status == NotifiableCaseStatus.Open && (form == IdspForm.Presumptive ? PresumedWeek : ConfirmedWeek) == week.Key;
}

/// <summary>
/// A week's P or L form sent to IHIP, even with no cases: the district unit counts a missing report as a silent
/// facility, so a nil report is a report. <see cref="CaseCount"/> is the number sent, so cases added to the week later
/// show as not yet reported.
/// </summary>
public sealed class IdspWeeklyReport : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public int Year { get; set; }
    public int Week { get; set; }
    public IdspForm Form { get; set; }
    public DateOnly ReportedOn { get; set; }

    /// <summary>The IHIP upload or acknowledgement number, if the portal gave one.</summary>
    public string? Reference { get; set; }

    public int CaseCount { get; set; }
    public Guid? ReportedBy { get; set; }
    public DateTimeOffset ReportedAt { get; set; }
}
