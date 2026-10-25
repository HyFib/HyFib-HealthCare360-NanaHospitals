using HyFib.HealthCare360.Modules.Pharmacy.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Features.Adr;

public sealed record AdrReportDto(
    Guid Id,
    string Number,
    Guid PatientId,
    string PatientInitials,
    string Uhid,
    string? Sex,
    int? AgeYears,
    string? IncidentNumber,
    AdrReportStatus Status,
    DateOnly LearnedOn,
    DateOnly? DueBy,
    bool Overdue,
    string Reaction,
    DateOnly ReactionStartedOn,
    DateOnly? ReactionStoppedOn,
    AdrSeriousness Seriousness,
    AdrOutcome Outcome,
    decimal? WeightKg,
    string? RelevantTests,
    string? MedicalHistory,
    IReadOnlyList<AdrDrug> Drugs,
    string ReporterName,
    string? ReporterDesignation,
    WhoUmcCausality? Causality,
    string? AssessedByName,
    DateOnly? SubmittedOn,
    string? SubmissionReference,
    uint Version);

public sealed record AdrReportListItem(
    Guid Id,
    string Number,
    string PatientInitials,
    string Uhid,
    string Reaction,
    string SuspectedDrugs,
    bool Serious,
    AdrReportStatus Status,
    DateOnly LearnedOn,
    DateOnly? DueBy,
    bool Overdue,
    string ReporterName,
    WhoUmcCausality? Causality);

/// <summary>A medicine the pharmacy gave the patient lately, to fill in a suspected or concomitant drug.</summary>
public sealed record DispensedMedicine(string Name, string? GenericName, string? Manufacturer, string BatchNumber, DateOnly? ExpiryDate, DateTimeOffset GivenAt);

/// <summary>The reaction as reported. <paramref name="Version"/>: when correcting a draft.</summary>
public sealed record SaveAdrReportRequest(
    Guid PatientId,
    string? IncidentNumber,
    DateOnly LearnedOn,
    string Reaction,
    DateOnly ReactionStartedOn,
    DateOnly? ReactionStoppedOn,
    AdrSeriousness Seriousness,
    AdrOutcome Outcome,
    decimal? WeightKg,
    string? RelevantTests,
    string? MedicalHistory,
    IReadOnlyList<AdrDrug> Drugs,
    string? ReporterDesignation,
    uint? Version = null);

public sealed record SubmitAdrReportRequest(WhoUmcCausality Causality, DateOnly SubmittedOn, string? Reference, uint Version);

internal sealed class SaveAdrReportRequestValidator : AbstractValidator<SaveAdrReportRequest>
{
    public SaveAdrReportRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.IncidentNumber).MaximumLength(24);
        RuleFor(x => x.Reaction).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.Seriousness).Must(s => (s & ~(AdrSeriousness)127) == 0).WithMessage("Choose from the listed reasons.");
        RuleFor(x => x.Outcome).IsInEnum();
        RuleFor(x => x.WeightKg).InclusiveBetween(0.3m, 300m).When(x => x.WeightKg is not null);
        RuleFor(x => x.RelevantTests).MaximumLength(2000);
        RuleFor(x => x.MedicalHistory).MaximumLength(2000);
        RuleFor(x => x.ReporterDesignation).MaximumLength(120);
        RuleFor(x => x.Drugs).NotEmpty().Must(d => d.Count <= 20).WithMessage("List at most 20 drugs.");
        RuleForEach(x => x.Drugs).ChildRules(drug =>
        {
            drug.RuleFor(d => d.Name).NotEmpty().MaximumLength(200);
            drug.RuleFor(d => d.Manufacturer).MaximumLength(200);
            drug.RuleFor(d => d.BatchNumber).MaximumLength(40);
            drug.RuleFor(d => d.Dose).MaximumLength(60);
            drug.RuleFor(d => d.Route).MaximumLength(60);
            drug.RuleFor(d => d.Frequency).MaximumLength(60);
            drug.RuleFor(d => d.Indication).MaximumLength(200);
            drug.RuleFor(d => d.Action).IsInEnum();
            drug.RuleFor(d => d.Dechallenge).IsInEnum();
            drug.RuleFor(d => d.Rechallenge).IsInEnum();
        });
    }
}

internal sealed class SubmitAdrReportRequestValidator : AbstractValidator<SubmitAdrReportRequest>
{
    public SubmitAdrReportRequestValidator()
    {
        RuleFor(x => x.Causality).IsInEnum();
        RuleFor(x => x.Reference).MaximumLength(64);
    }
}
