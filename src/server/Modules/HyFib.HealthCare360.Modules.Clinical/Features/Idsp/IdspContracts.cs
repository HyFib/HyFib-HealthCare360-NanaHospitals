using HyFib.HealthCare360.Modules.Clinical.Domain.Idsp;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Idsp;

/// <param name="Nikshay">Notified on Ni-kshay (tuberculosis), not on the IHIP forms.</param>
public sealed record IdspDiseaseDto(string Code, string Name, bool Immediate, bool Nikshay, IReadOnlyList<string> IcdCodes);

/// <summary>
/// A case as the IHIP line list (or Ni-kshay, for tuberculosis) asks for it. A restricted (VIP) patient the user may not
/// open shows initials only. <paramref name="Nikshay"/>: notified on Ni-kshay, under <paramref name="NikshayId"/> once
/// it is.
/// </summary>
public sealed record IdspCaseRow(
    Guid Id,
    string DiseaseCode,
    string Disease,
    bool Immediate,
    bool Nikshay,
    NotifiableCaseSource Source,
    NotifiableCaseStatus Status,
    Guid PatientId,
    string PatientName,
    string Uhid,
    string? Sex,
    int? AgeYears,
    string? Address,
    string? District,
    string? Phone,
    DateOnly? OnsetDate,
    DateTimeOffset? PresumedAt,
    string? IcdCode,
    DateTimeOffset? ConfirmedAt,
    string? TestName,
    string? TestResult,
    DateTimeOffset? AlertedAt,
    string? DiscardReason,
    string? NikshayId,
    DateOnly? NikshayNotifiedOn,
    uint Version);

/// <summary>Tuberculosis cases to notify on Ni-kshay, the longest waiting first, and those notified lately.</summary>
public sealed record IdspTuberculosisDto(IReadOnlyList<IdspCaseRow> ToNotify, IReadOnlyList<IdspCaseRow> Notified);

/// <summary>A tuberculosis case was notified on Ni-kshay, under <paramref name="NikshayId"/>, on <paramref name="NotifiedOn"/>.</summary>
public sealed record RecordNikshayRequest(string NikshayId, DateOnly NotifiedOn, uint Version);

public sealed record IdspFormReport(DateOnly ReportedOn, string? Reference, int CaseCount, DateTimeOffset ReportedAt, uint Version);

/// <summary>One week's P or L form: its cases, the report sent, how many cases came in after it, and whether it is late.</summary>
public sealed record IdspFormWeek(IdspForm Form, IReadOnlyList<IdspCaseRow> Cases, IdspFormReport? Report, int NotReported, bool Overdue);

public sealed record IdspWeekDto(
    int Year,
    int Week,
    DateOnly Monday,
    DateOnly Sunday,
    DateOnly DueOn,
    bool Ended,
    IdspFormWeek Presumptive,
    IdspFormWeek Laboratory,
    IReadOnlyList<IdspCaseRow> Discarded);

public sealed record IdspWeekSummary(
    int Year,
    int Week,
    DateOnly Monday,
    DateOnly Sunday,
    DateOnly DueOn,
    int PresumptiveCases,
    int LaboratoryCases,
    bool PresumptiveReported,
    bool LaboratoryReported,
    int NotReported,
    bool Overdue);

/// <summary>The weeks to report (the current one and the ones before it) and the outbreak-prone cases of the last two weeks.</summary>
public sealed record IdspOverview(IReadOnlyList<IdspWeekSummary> Weeks, IReadOnlyList<IdspCaseRow> Immediate);

/// <summary>A week's P or L form sent to IHIP, a nil report included. <paramref name="Version"/>: when correcting it.</summary>
public sealed record ReportIdspWeekRequest(DateOnly ReportedOn, string? Reference, uint? Version = null);

public sealed record DiscardIdspCaseRequest(string Reason, uint Version);

/// <summary>
/// A case the system did not catch: acute flaccid paralysis, a disease diagnosed without a code, or tuberculosis
/// diagnosed elsewhere and treated here. With a test result it is laboratory-confirmed (from an outside laboratory),
/// otherwise presumptive.
/// </summary>
public sealed record AddIdspCaseRequest(Guid PatientId, string DiseaseCode, DateOnly? OnsetDate, string? TestName, string? TestResult);

internal sealed class ReportIdspWeekRequestValidator : AbstractValidator<ReportIdspWeekRequest>
{
    public ReportIdspWeekRequestValidator()
    {
        RuleFor(x => x.Reference).MaximumLength(64);
    }
}

internal sealed class DiscardIdspCaseRequestValidator : AbstractValidator<DiscardIdspCaseRequest>
{
    public DiscardIdspCaseRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}

internal sealed class RecordNikshayRequestValidator : AbstractValidator<RecordNikshayRequest>
{
    public RecordNikshayRequestValidator()
    {
        RuleFor(x => x.NikshayId).NotEmpty().MaximumLength(32).Matches("^[A-Za-z0-9]+$")
            .WithMessage("Enter the Ni-kshay ID as Ni-kshay gave it: letters and digits.");
    }
}

internal sealed class AddIdspCaseRequestValidator : AbstractValidator<AddIdspCaseRequest>
{
    public AddIdspCaseRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.DiseaseCode).NotEmpty().Must(c => IdspDiseases.Find(c) is not null).WithMessage("Choose a disease from the IDSP list, or tuberculosis.");
        RuleFor(x => x.TestName).MaximumLength(200).NotEmpty().When(x => !string.IsNullOrWhiteSpace(x.TestResult))
            .WithMessage("Name the test that confirmed it.");
        RuleFor(x => x.TestResult).MaximumLength(200);
    }
}
