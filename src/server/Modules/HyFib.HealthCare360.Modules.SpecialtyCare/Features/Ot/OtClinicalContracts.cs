using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;

public sealed record RecordChecklistRequest(IReadOnlyList<ChecklistInput> Items);

public sealed record AnaesthesiaRecordDto(
    Guid Id,
    Guid SurgeryBookingId,
    Guid PatientId,
    Guid? AnaesthetistId,
    AsaGrade? AsaGrade,
    bool AsaEmergency,
    AnaesthesiaType Technique,
    AirwayDevice Airway,
    string? AirwayNotes,
    DateTimeOffset? AnaesthesiaStartAt,
    DateTimeOffset? AnaesthesiaEndAt,
    IReadOnlyList<AnaesthesiaVitals> Vitals,
    IReadOnlyList<AnaesthesiaDrug> Drugs,
    IReadOnlyList<AnaesthesiaFluid> Fluids,
    int? EstimatedBloodLossMl,
    int? UrineOutputMl,
    string? Complications,
    string? RecoveryNotes,
    DateTimeOffset? SignedAt,
    string? SignedByName,
    uint Version);

public sealed record SaveAnaesthesiaRecordRequest(
    AsaGrade? AsaGrade,
    bool AsaEmergency,
    AnaesthesiaType Technique,
    AirwayDevice Airway,
    string? AirwayNotes,
    DateTimeOffset? AnaesthesiaStartAt,
    DateTimeOffset? AnaesthesiaEndAt,
    IReadOnlyList<AnaesthesiaDrug>? Drugs,
    IReadOnlyList<AnaesthesiaFluid>? Fluids,
    int? EstimatedBloodLossMl,
    int? UrineOutputMl,
    string? Complications,
    string? RecoveryNotes,
    bool Sign,
    uint? Version);

public sealed record AddAnaesthesiaVitalsRequest(IReadOnlyList<AnaesthesiaVitals> Rows);

public sealed record OperationNoteDto(
    Guid Id,
    Guid SurgeryBookingId,
    Guid PatientId,
    Guid? SurgeonId,
    string? PreOperativeDiagnosis,
    string? PostOperativeDiagnosis,
    string ProcedurePerformed,
    string? Findings,
    string? ProcedureDetails,
    IReadOnlyList<SurgicalSpecimen> Specimens,
    string? Drains,
    int? EstimatedBloodLossMl,
    string? Complications,
    string? PostOpOrders,
    DateTimeOffset? SignedAt,
    string? SignedByName,
    uint Version);

public sealed record SaveOperationNoteRequest(
    string? PreOperativeDiagnosis,
    string? PostOperativeDiagnosis,
    string ProcedurePerformed,
    string? Findings,
    string? ProcedureDetails,
    IReadOnlyList<SurgicalSpecimen>? Specimens,
    string? Drains,
    int? EstimatedBloodLossMl,
    string? Complications,
    string? PostOpOrders,
    bool Sign,
    uint? Version);

public sealed record SurgeryMaterialDto(
    Guid Id,
    MaterialKind Kind,
    Guid? SupplyItemId,
    string ItemName,
    string? Manufacturer,
    string? LotNumber,
    string? SerialNumber,
    DateOnly? ExpiryDate,
    decimal Quantity,
    decimal UnitPrice,
    decimal Amount,
    string? RecordedByName,
    DateTimeOffset RecordedAt,
    ChargeStatus ChargeStatus,
    string? ChargeError);

public sealed record AddSurgeryMaterialRequest(
    MaterialKind Kind,
    Guid? SupplyItemId,
    string ItemName,
    string? Manufacturer,
    string? LotNumber,
    string? SerialNumber,
    DateOnly? ExpiryDate,
    decimal Quantity,
    decimal UnitPrice);

/// <summary>Recall search result: who received implants of a given lot or serial number.</summary>
public sealed record ImplantTraceItem(
    Guid MaterialId,
    Guid SurgeryBookingId,
    string BookingNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    string? Phone,
    string ItemName,
    string? Manufacturer,
    string? LotNumber,
    string? SerialNumber,
    DateTimeOffset RecordedAt);

public sealed class RecordChecklistRequestValidator : AbstractValidator<RecordChecklistRequest>
{
    public RecordChecklistRequestValidator()
    {
        RuleFor(x => x.Items).NotEmpty().WithMessage("Record at least one checklist item.")
            .Must(items => items.Count <= 50).WithMessage("Too many items.");
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.Key).NotEmpty().MaximumLength(64);
            item.RuleFor(i => i.Remarks).SafeText(500);
        });
    }
}

public sealed class SaveAnaesthesiaRecordRequestValidator : AbstractValidator<SaveAnaesthesiaRecordRequest>
{
    public SaveAnaesthesiaRecordRequestValidator()
    {
        RuleFor(x => x.AirwayNotes).SafeText(1000);
        RuleFor(x => x.Complications).SafeText(2000);
        RuleFor(x => x.RecoveryNotes).SafeText(2000);
        RuleFor(x => x.EstimatedBloodLossMl).InclusiveBetween(0, 20_000);
        RuleFor(x => x.UrineOutputMl).InclusiveBetween(0, 20_000);
        RuleFor(x => x.AnaesthesiaEndAt).GreaterThan(x => x.AnaesthesiaStartAt)
            .When(x => x.AnaesthesiaStartAt is not null && x.AnaesthesiaEndAt is not null)
            .WithMessage("Anaesthesia end must be after its start.");
        RuleFor(x => x.Drugs).Must(d => d is null || d.Count <= 200).WithMessage("Too many drug entries.");
        RuleForEach(x => x.Drugs).ChildRules(d =>
        {
            d.RuleFor(r => r.Drug).NotEmpty().MaximumLength(100);
            d.RuleFor(r => r.Dose).NotEmpty().MaximumLength(50);
            d.RuleFor(r => r.Route).MaximumLength(30);
        });
        RuleFor(x => x.Fluids).Must(f => f is null || f.Count <= 100).WithMessage("Too many fluid entries.");
        RuleForEach(x => x.Fluids).ChildRules(f =>
        {
            f.RuleFor(r => r.Fluid).NotEmpty().MaximumLength(100);
            f.RuleFor(r => r.VolumeMl).InclusiveBetween(1, 10_000);
        });
    }
}

public sealed class AddAnaesthesiaVitalsRequestValidator : AbstractValidator<AddAnaesthesiaVitalsRequest>
{
    public AddAnaesthesiaVitalsRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.Rows).NotEmpty().Must(r => r.Count <= 100).WithMessage("Send between 1 and 100 rows.");
        RuleForEach(x => x.Rows).ChildRules(r =>
        {
            r.RuleFor(v => v.At).NotInFuture(time);
            r.RuleFor(v => v.HeartRate).Pulse();
            r.RuleFor(v => v.Systolic).Systolic();
            r.RuleFor(v => v.Diastolic).Diastolic();
            r.RuleFor(v => v.SpO2).Saturation();
            r.RuleFor(v => v.EtCo2).InclusiveBetween(0, 150);
            r.RuleFor(v => v.TemperatureC).TemperatureC();
            r.RuleFor(v => v.RespiratoryRate).InclusiveBetween(0, 80);
        });
    }
}

public sealed class SaveOperationNoteRequestValidator : AbstractValidator<SaveOperationNoteRequest>
{
    public SaveOperationNoteRequestValidator()
    {
        RuleFor(x => x.ProcedurePerformed).NotEmpty().WithMessage("Enter the procedure performed.").SafeText(500);
        RuleFor(x => x.PreOperativeDiagnosis).SafeText(500);
        RuleFor(x => x.PostOperativeDiagnosis).SafeText(500);
        RuleFor(x => x.Findings).SafeText(4000);
        RuleFor(x => x.ProcedureDetails).SafeText(8000);
        RuleFor(x => x.Drains).SafeText(500);
        RuleFor(x => x.Complications).SafeText(2000);
        RuleFor(x => x.PostOpOrders).SafeText(4000);
        RuleFor(x => x.EstimatedBloodLossMl).InclusiveBetween(0, 20_000);
        RuleFor(x => x.Specimens).Must(s => s is null || s.Count <= 30).WithMessage("Too many specimens.");
        RuleForEach(x => x.Specimens).ChildRules(s =>
        {
            s.RuleFor(r => r.Description).NotEmpty().MaximumLength(200);
            s.RuleFor(r => r.Label).MaximumLength(64);
        });
    }
}

public sealed class AddSurgeryMaterialRequestValidator : AbstractValidator<AddSurgeryMaterialRequest>
{
    public AddSurgeryMaterialRequestValidator()
    {
        RuleFor(x => x.ItemName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Manufacturer).MaximumLength(200);
        RuleFor(x => x.LotNumber).MaximumLength(64);
        RuleFor(x => x.SerialNumber).MaximumLength(64);
        RuleFor(x => x.Quantity).GreaterThan(0).LessThanOrEqualTo(1000);
        RuleFor(x => x.UnitPrice).GreaterThanOrEqualTo(0).LessThanOrEqualTo(10_000_000);
        RuleFor(x => x)
            .Must(x => x.Kind != MaterialKind.Implant || !string.IsNullOrWhiteSpace(x.LotNumber) || !string.IsNullOrWhiteSpace(x.SerialNumber))
            .WithName("lotNumber").WithMessage("Enter the lot or serial number of the implant (needed for device recalls).");
    }
}
