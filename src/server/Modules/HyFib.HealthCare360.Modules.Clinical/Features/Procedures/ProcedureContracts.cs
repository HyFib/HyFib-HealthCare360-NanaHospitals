using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Procedures;

/// <summary>What a new record and a correction both carry.</summary>
public interface IProcedureFields
{
    DateTimeOffset PerformedAt { get; }
    string? Code { get; }
    string? Display { get; }
    Guid? PerformerPractitionerId { get; }
    string? PerformerName { get; }
    ProcedureSide Side { get; }
    string? BodySite { get; }
    string? Outcome { get; }
    string? Notes { get; }
}

/// <summary>
/// A procedure done for the patient. <c>code</c> must be in the procedure catalogue (ICD-10-PCS); without one the
/// procedure is described in <c>display</c>, which otherwise defaults to the code's title. Without a performer the
/// recording doctor is taken to have done it.
/// </summary>
public sealed record RecordProcedureRequest(
    Guid? EncounterId,
    DateTimeOffset PerformedAt,
    string? Code,
    string? Display,
    Guid? PerformerPractitionerId,
    string? PerformerName,
    ProcedureSide Side,
    string? BodySite,
    string? Outcome,
    string? Notes) : IProcedureFields;

/// <summary>A correction of a recorded procedure, with the reason it was wrong (kept on the record and in the audit trail).</summary>
public sealed record CorrectProcedureRequest(
    DateTimeOffset PerformedAt,
    string? Code,
    string? Display,
    Guid? PerformerPractitionerId,
    string? PerformerName,
    ProcedureSide Side,
    string? BodySite,
    string? Outcome,
    string? Notes,
    string Reason) : IProcedureFields;

/// <summary>A recorded procedure; <see cref="PerformerName"/> is the performing practitioner's name, or the name recorded for one from elsewhere.</summary>
public sealed record ProcedureDto(
    Guid Id,
    Guid PatientId,
    Guid? EncounterId,
    Guid? AdmissionId,
    DateTimeOffset PerformedAt,
    string? Code,
    string? CodeSystem,
    string Display,
    Guid? PerformerPractitionerId,
    string? PerformerName,
    ProcedureSide Side,
    string? BodySite,
    string? Outcome,
    string? Notes,
    ProcedureSource Source,
    ProcedureStatus Status,
    string? StatusReason,
    DateTimeOffset? StatusChangedAt);

/// <summary>The rules a new record and a correction share.</summary>
internal sealed class ProcedureFieldsValidator : AbstractValidator<IProcedureFields>
{
    /// <summary>Clocks on the ward and on the server differ a little; a procedure "just done" must not be refused.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    public ProcedureFieldsValidator(TimeProvider time)
    {
        RuleFor(x => x.PerformedAt).Must(at => at <= time.GetUtcNow() + ClockSkew).WithMessage("The procedure cannot be in the future.");
        RuleFor(x => x.Code).MaximumLength(32);
        RuleFor(x => x.Display).NotEmpty().When(x => string.IsNullOrWhiteSpace(x.Code)).WithMessage("Describe the procedure, or choose its code.");
        RuleFor(x => x.Display).SafeText(300);
        RuleFor(x => x.PerformerName).SafeText(200);
        RuleFor(x => x.Side).IsInEnum();
        RuleFor(x => x.BodySite).SafeText(100);
        RuleFor(x => x.Outcome).SafeText(1000);
        RuleFor(x => x.Notes).SafeText(2000);
    }
}

public sealed class RecordProcedureRequestValidator : AbstractValidator<RecordProcedureRequest>
{
    public RecordProcedureRequestValidator(TimeProvider time) => Include(new ProcedureFieldsValidator(time));
}

public sealed class CorrectProcedureRequestValidator : AbstractValidator<CorrectProcedureRequest>
{
    public CorrectProcedureRequestValidator(TimeProvider time)
    {
        Include(new ProcedureFieldsValidator(time));
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(300);
    }
}
