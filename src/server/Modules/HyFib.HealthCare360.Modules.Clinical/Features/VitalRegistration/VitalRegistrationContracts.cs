using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Features.VitalRegistration;

public sealed record BirthRecordDto(
    Guid Id,
    string SerialNumber,
    Guid MotherPatientId,
    string MotherUhid,
    string MotherName,
    Guid? BabyPatientId,
    string? BabyUhid,
    Guid? AdmissionId,
    VitalReportStatus Status,
    DateTimeOffset BornAt,
    BirthOutcome Outcome,
    BirthSex Sex,
    int? WeightGrams,
    int? WeeksOfGestation,
    DeliveryMethod Method,
    BirthAttendant Attendant,
    string? ChildName,
    string? FatherName,
    int MotherAgeYears,
    int? LiveChildren,
    string InformantName,
    string? StillBirthCause,
    DateOnly DueBy,
    DateOnly? ReportedOn,
    string? RegistrationNumber,
    string? ErrorReason,
    uint Version);

public sealed record DeathRecordDto(
    Guid Id,
    string SerialNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    string? Gender,
    Guid? AdmissionId,
    VitalReportStatus Status,
    DateTimeOffset DiedAt,
    int? AgeYears,
    IReadOnlyList<CauseOfDeath> Causes,
    string UnderlyingCause,
    string? OtherConditions,
    DeathManner Manner,
    bool MedicoLegal,
    PregnancyAtDeath Pregnancy,
    string CertifiedBy,
    string CertifierRegistrationNumber,
    DateTimeOffset CertifiedAt,
    DateOnly DueBy,
    DateOnly? ReportedOn,
    string? RegistrationNumber,
    string? ErrorReason,
    uint Version);

/// <summary>A birth in the hospital. <paramref name="Version"/>: when correcting it.</summary>
public sealed record SaveBirthRequest(
    Guid MotherPatientId,
    Guid? BabyPatientId,
    Guid? AdmissionId,
    DateTimeOffset BornAt,
    BirthOutcome Outcome,
    BirthSex Sex,
    int? WeightGrams,
    int? WeeksOfGestation,
    DeliveryMethod Method,
    BirthAttendant Attendant,
    string? ChildName,
    string? FatherName,
    int MotherAgeYears,
    int? LiveChildren,
    string InformantName,
    string? StillBirthCause,
    uint? Version = null);

/// <summary>A death and its medical certificate of cause of death. <paramref name="AgeYears"/>: for a patient registered without a date of birth.</summary>
public sealed record SaveDeathRequest(
    Guid PatientId,
    Guid? AdmissionId,
    DateTimeOffset DiedAt,
    int? AgeYears,
    IReadOnlyList<CauseOfDeath> Causes,
    string? OtherConditions,
    DeathManner Manner,
    bool MedicoLegal,
    PregnancyAtDeath Pregnancy,
    string CertifierRegistrationNumber,
    uint? Version = null);

public sealed record ReportToRegistrarRequest(DateOnly ReportedOn, string RegistrationNumber, uint Version);

public sealed record VitalEntryErrorRequest(string Reason, uint Version);

public sealed record BirthRegisterRow(
    Guid Id, string SerialNumber, DateTimeOffset BornAt, BirthOutcome Outcome, BirthSex Sex, int? WeightGrams, string MotherName, string MotherUhid,
    string? FatherName, string? ChildName, VitalReportStatus Status, DateOnly DueBy, bool Overdue, DateOnly? ReportedOn, string? RegistrationNumber);

public sealed record DeathRegisterRow(
    Guid Id, string SerialNumber, DateTimeOffset DiedAt, string PatientName, string Uhid, string? Gender, int? AgeYears, string UnderlyingCause,
    DeathManner Manner, bool MedicoLegal, string CertifiedBy, VitalReportStatus Status, DateOnly DueBy, bool Overdue, DateOnly? ReportedOn,
    string? RegistrationNumber);

/// <summary>The month's births, live and still; <paramref name="Overdue"/>: those past the 21 days and still not reported.</summary>
public sealed record BirthRegister(int Year, int Month, IReadOnlyList<BirthRegisterRow> Rows, int LiveBirths, int StillBirths, int Overdue);

public sealed record DeathRegister(int Year, int Month, IReadOnlyList<DeathRegisterRow> Rows, int MedicoLegal, int Overdue);

public sealed class SaveBirthRequestValidator : AbstractValidator<SaveBirthRequest>
{
    public SaveBirthRequestValidator()
    {
        RuleFor(x => x.MotherPatientId).NotEmpty();
        RuleFor(x => x.Outcome).IsInEnum();
        RuleFor(x => x.Sex).IsInEnum();
        RuleFor(x => x.Method).IsInEnum();
        RuleFor(x => x.Attendant).IsInEnum();
        RuleFor(x => x.ChildName).MaximumLength(200);
        RuleFor(x => x.FatherName).MaximumLength(200);
        RuleFor(x => x.LiveChildren).InclusiveBetween(0, 20).When(x => x.LiveChildren is not null);
        RuleFor(x => x.InformantName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.StillBirthCause).MaximumLength(500);
        RuleFor(x => x.BabyPatientId).Must((r, baby) => baby != r.MotherPatientId).WithMessage("The baby has a record of its own.");
    }
}

public sealed class SaveDeathRequestValidator : AbstractValidator<SaveDeathRequest>
{
    public SaveDeathRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.AgeYears).InclusiveBetween(0, 130).When(x => x.AgeYears is not null);
        RuleFor(x => x.Causes).NotNull().Must(c => c.Count is >= 1 and <= DeathRecord.MaxCauses)
            .WithMessage("Enter the causes in Part I, lines (a) to (d).");
        RuleForEach(x => x.Causes).ChildRules(c =>
        {
            c.RuleFor(x => x.Condition).NotEmpty().MaximumLength(300);
            c.RuleFor(x => x.Interval).MaximumLength(50);
        });
        RuleFor(x => x.OtherConditions).MaximumLength(1000);
        RuleFor(x => x.Manner).IsInEnum();
        RuleFor(x => x.Pregnancy).IsInEnum();
        RuleFor(x => x.CertifierRegistrationNumber).NotEmpty().WithMessage("Enter your medical council registration number.").MaximumLength(64);
    }
}

public sealed class ReportToRegistrarRequestValidator : AbstractValidator<ReportToRegistrarRequest>
{
    public ReportToRegistrarRequestValidator()
    {
        RuleFor(x => x.RegistrationNumber).NotEmpty().WithMessage("Enter the registration number the Registrar gave.").MaximumLength(64);
    }
}

public sealed class VitalEntryErrorRequestValidator : AbstractValidator<VitalEntryErrorRequest>
{
    public VitalEntryErrorRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Enter why the entry was made in error.").MaximumLength(500);
    }
}
