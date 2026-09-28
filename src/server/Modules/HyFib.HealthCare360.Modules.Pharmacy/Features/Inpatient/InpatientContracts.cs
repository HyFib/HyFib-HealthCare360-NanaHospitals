using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Features.Inpatient;

public sealed record PharmacyDocumentCreated(Guid Id, string Number);

public sealed record WardIndentLineInput(Guid ItemId, decimal Quantity, string? Remarks);

/// <param name="AdmissionId">The patient's current admission (the IP account the medicines are charged to).</param>
public sealed record RaiseWardIndentRequest(
    Guid PatientId,
    Guid AdmissionId,
    Guid? VisitId,
    string? WardName,
    string? BedLabel,
    IndentUrgency Urgency,
    Guid? PrescriptionId,
    string? Remarks,
    IReadOnlyList<WardIndentLineInput> Lines);

public sealed record WardIndentListItem(
    Guid Id,
    string Number,
    Guid PatientId,
    string PatientName,
    string Uhid,
    Guid AdmissionId,
    string? WardName,
    string? BedLabel,
    IndentUrgency Urgency,
    WardIndentStatus Status,
    DateTimeOffset RaisedAt,
    string? RaisedBy,
    int LineCount);

public sealed record WardIndentLineDto(
    Guid Id,
    Guid ItemId,
    string ItemName,
    decimal RequestedQuantity,
    decimal IssuedQuantity,
    decimal RemainingQuantity,
    string? Remarks,
    decimal? Available);

public sealed record WardIndentDetail(
    Guid Id,
    string Number,
    PatientSummary Patient,
    Guid AdmissionId,
    Guid? VisitId,
    string? WardName,
    string? BedLabel,
    IndentUrgency Urgency,
    WardIndentStatus Status,
    DateTimeOffset RaisedAt,
    string? RaisedBy,
    Guid? PrescriptionId,
    string? Remarks,
    string? CancelReason,
    IReadOnlyList<WardIndentLineDto> Lines,
    uint Version);

/// <param name="WardIndentLineId">Line of the ward indent being served (when issuing against an indent).</param>
/// <param name="QueueLineId">Line of the in-patient prescription being served (when issuing against the IP queue).</param>
public sealed record IpIssueLineInput(
    Guid ItemId,
    decimal Quantity,
    Guid? BatchId,
    Guid? WardIndentLineId,
    Guid? QueueLineId,
    string? SubstitutionReason,
    bool PrescriberConsent);

/// <summary>Issue to one admitted patient: against a ward indent, an in-patient prescription, or directly.</summary>
public sealed record IpIssueRequest(
    Guid StoreId,
    Guid? WardIndentId,
    Guid? QueueEntryId,
    Guid? PatientId,
    Guid? AdmissionId,
    Guid? VisitId,
    IReadOnlyList<IpIssueLineInput> Lines,
    bool ConfirmControlledDrugs);

public sealed record IpIssueCreated(Guid Id, string Number, decimal TotalAmount);

public sealed record IpIssueListItem(
    Guid Id,
    string Number,
    Guid PatientId,
    string PatientName,
    string Uhid,
    Guid AdmissionId,
    Guid? WardIndentId,
    DateTimeOffset IssuedAt,
    decimal TotalAmount,
    int LineCount,
    string? IssuedBy);

public sealed record IpIssueLineDto(
    Guid Id,
    Guid ItemId,
    string ItemName,
    string BatchNumber,
    DateOnly? ExpiryDate,
    decimal Quantity,
    decimal UnitMrp,
    decimal Amount,
    decimal ReturnedQuantity,
    Guid? ChargeId);

public sealed record WardReturnLineDto(Guid IpIssueLineId, string ItemName, string BatchNumber, decimal Quantity, decimal CreditAmount, bool Restocked, ChargeReversal ChargeReversal);

public sealed record WardReturnDto(Guid Id, string Number, DateTimeOffset ReturnedAt, string Reason, decimal CreditAmount, IReadOnlyList<WardReturnLineDto> Lines);

public sealed record IpIssueDetail(
    Guid Id,
    string Number,
    Guid StoreId,
    PatientSummary Patient,
    Guid AdmissionId,
    Guid? WardIndentId,
    Guid? QueueEntryId,
    DateTimeOffset IssuedAt,
    string? IssuedBy,
    decimal TotalAmount,
    IReadOnlyList<IpIssueLineDto> Lines,
    IReadOnlyList<WardReturnDto> Returns);

/// <param name="Restock">Sealed, in-date returns go back on the shelf; opened or damaged ones are only credited.</param>
public sealed record WardReturnLineInput(Guid IpIssueLineId, decimal Quantity, bool Restock);

public sealed record WardReturnRequest(IReadOnlyList<WardReturnLineInput> Lines, string Reason);

public sealed record WardReturnCreated(Guid Id, string Number, decimal CreditAmount);

public sealed class RaiseWardIndentRequestValidator : AbstractValidator<RaiseWardIndentRequest>
{
    public RaiseWardIndentRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.AdmissionId).NotEmpty();
        RuleFor(x => x.WardName).SafeText(100);
        RuleFor(x => x.BedLabel).MaximumLength(32);
        RuleFor(x => x.Remarks).SafeText(1000);
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Add at least one item.")
            .Must(l => l.Count <= 60)
            .Must(l => l.Select(x => x.ItemId).Distinct().Count() == l.Count).WithMessage("Each item can appear only once.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ItemId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0);
            line.RuleFor(l => l.Remarks).SafeText(300);
        });
    }
}

public sealed class IpIssueRequestValidator : AbstractValidator<IpIssueRequest>
{
    public IpIssueRequestValidator()
    {
        RuleFor(x => x.StoreId).NotEmpty();
        RuleFor(x => x)
            .Must(x => x.WardIndentId is not null || x.QueueEntryId is not null || (x.PatientId is not null && x.AdmissionId is not null))
            .WithName("admissionId").WithMessage("Issue against a ward indent, an in-patient prescription, or choose the admitted patient.");
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Choose what to issue.").Must(l => l.Count <= 100);
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ItemId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0);
            line.RuleFor(l => l.SubstitutionReason).SafeText(300);
        });
    }
}

public sealed class WardReturnRequestValidator : AbstractValidator<WardReturnRequest>
{
    public WardReturnRequestValidator()
    {
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Choose what is being returned.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.IpIssueLineId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0);
        });
        RuleFor(x => x.Reason).NotEmpty().SafeText(300);
    }
}
