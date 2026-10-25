using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Mtp;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.Mtp;

public sealed record MtpCaseDto(
    Guid Id,
    string SerialNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Guid? AdmissionId,
    MtpCaseStatus Status,
    string ApprovalNumber,
    int AgeYears,
    bool MentallyIll,
    MtpConsentBy ConsentBy,
    string? GuardianName,
    string? GuardianRelation,
    DateOnly ConsentOn,
    Guid? ConsentFileId,
    int WeeksOfGestation,
    IReadOnlyList<MtpGround> Grounds,
    MtpRule3BCategory? Category,
    string? MedicalBoardReference,
    IReadOnlyList<MtpOpinion> Opinions,
    DateTimeOffset CertifiedAt,
    string? CertifiedBy,
    DateOnly? TerminatedOn,
    MtpMethod? Method,
    string? PerformedBy,
    DateOnly? DischargedOn,
    string? Outcome,
    string? Remarks,
    string? CancelReason,
    uint Version);

public sealed record MtpCaseListItem(
    Guid Id, string SerialNumber, Guid PatientId, string Uhid, string PatientName, MtpCaseStatus Status, int WeeksOfGestation,
    DateTimeOffset CertifiedAt, DateOnly? TerminatedOn);

/// <summary>
/// The woman's consent (Form C) and the doctors' opinion (Form I). <paramref name="Id"/> and <paramref name="Version"/>:
/// when correcting a case before the termination; without them a new case is opened.
/// </summary>
public sealed record SaveMtpCaseRequest(
    Guid PatientId,
    Guid? AdmissionId,
    int AgeYears,
    bool MentallyIll,
    MtpConsentBy ConsentBy,
    string? GuardianName,
    string? GuardianRelation,
    DateOnly ConsentOn,
    Guid? ConsentFileId,
    int WeeksOfGestation,
    IReadOnlyList<MtpGround> Grounds,
    MtpRule3BCategory? Category,
    string? MedicalBoardReference,
    IReadOnlyList<MtpOpinion> Opinions,
    uint? Version = null);

public sealed record RecordMtpTerminationRequest(
    DateOnly TerminatedOn, MtpMethod Method, string PerformedBy, DateOnly? DischargedOn, string? Outcome, string? Remarks, uint Version);

public sealed record CancelMtpCaseRequest(string Reason, uint Version);

/// <summary>One line of the admission register (Form III).</summary>
public sealed record MtpRegisterRow(
    Guid Id,
    string SerialNumber,
    DateOnly? AdmittedOn,
    string PatientName,
    string Uhid,
    string? GuardianName,
    int AgeYears,
    string? Religion,
    string Address,
    int WeeksOfGestation,
    IReadOnlyList<MtpGround> Grounds,
    MtpRule3BCategory? Category,
    DateOnly? TerminatedOn,
    MtpMethod? Method,
    DateOnly? DischargedOn,
    string? Outcome,
    string OpinionBy,
    string? PerformedBy,
    string? Remarks);

public sealed record MtpRegister(int Year, int Month, string ApprovalNumber, IReadOnlyList<MtpRegisterRow> Rows, int AwaitingTermination);

public sealed record MtpCount(string Label, int Count);

/// <summary>The month's report to the Chief Medical Officer of the district (Form II).</summary>
public sealed record MtpMonthlyReport(
    int Year,
    int Month,
    string ApprovalNumber,
    int Total,
    IReadOnlyList<MtpCount> ByGestation,
    IReadOnlyList<MtpCount> ByReligion,
    IReadOnlyList<MtpCount> ByGround,
    IReadOnlyList<MtpCount> ByMethod,
    IReadOnlyList<MtpCount> ByAge);

public sealed class SaveMtpCaseRequestValidator : AbstractValidator<SaveMtpCaseRequest>
{
    public SaveMtpCaseRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.ConsentBy).IsInEnum();
        RuleFor(x => x.GuardianName).MaximumLength(200);
        RuleFor(x => x.GuardianRelation).MaximumLength(100);
        RuleFor(x => x.Grounds).NotNull().Must(g => g.Count <= Enum.GetValues<MtpGround>().Length);
        RuleForEach(x => x.Grounds).IsInEnum();
        RuleFor(x => x.Category).IsInEnum().When(x => x.Category is not null);
        RuleFor(x => x.MedicalBoardReference).MaximumLength(500);
        RuleFor(x => x.Opinions).NotNull().Must(o => o.Count is >= 1 and <= 3).WithMessage("Enter the doctors who formed the opinion.");
        RuleForEach(x => x.Opinions).ChildRules(o =>
        {
            o.RuleFor(x => x.DoctorName).NotEmpty().MaximumLength(200);
            o.RuleFor(x => x.RegistrationNumber).NotEmpty().MaximumLength(64);
        });
    }
}

public sealed class RecordMtpTerminationRequestValidator : AbstractValidator<RecordMtpTerminationRequest>
{
    public RecordMtpTerminationRequestValidator()
    {
        RuleFor(x => x.Method).IsInEnum();
        RuleFor(x => x.PerformedBy).NotEmpty().WithMessage("Enter the doctor who performed the termination.").MaximumLength(200);
        RuleFor(x => x.Outcome).MaximumLength(500);
        RuleFor(x => x.Remarks).MaximumLength(2000);
    }
}

public sealed class CancelMtpCaseRequestValidator : AbstractValidator<CancelMtpCaseRequest>
{
    public CancelMtpCaseRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Enter why the termination will not be done here.").MaximumLength(500);
    }
}
