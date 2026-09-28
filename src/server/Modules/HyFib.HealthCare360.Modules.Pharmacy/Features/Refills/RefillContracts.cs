using HyFib.HealthCare360.Modules.Pharmacy.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Features.Refills;

/// <param name="QueueEntryId">The original (dispensed) prescription entry to refill.</param>
public sealed record CreateRefillRequest(Guid QueueEntryId, DateOnly? PreferredPickupDate, string? Notes);

public sealed record RefillRequestCreated(Guid Id, string Number);

public sealed record RefillRequestDto(
    Guid Id,
    string Number,
    Guid QueueEntryId,
    string PrescriptionNumber,
    Guid PatientId,
    string PatientName,
    string Uhid,
    RefillChannel Channel,
    RefillStatus Status,
    DateTimeOffset RequestedAt,
    DateOnly? PreferredPickupDate,
    string? Notes,
    string? RejectReason,
    Guid? RefillEntryId);

/// <summary>A chronic prescription the patient can still refill (portal list).</summary>
public sealed record RefillablePrescription(
    Guid QueueEntryId,
    string PrescriptionNumber,
    Guid PatientId,
    string PatientName,
    DateTimeOffset SignedAt,
    string? PrescriberName,
    int RefillsRemaining,
    DateOnly? RefillValidUntil,
    IReadOnlyList<string> Medicines,
    bool HasOpenRequest);

public sealed record PortalRefillView(IReadOnlyList<RefillablePrescription> Prescriptions, IReadOnlyList<RefillRequestDto> Requests);

public sealed class CreateRefillRequestValidator : AbstractValidator<CreateRefillRequest>
{
    public CreateRefillRequestValidator(TimeProvider time)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        RuleFor(x => x.QueueEntryId).NotEmpty();
        RuleFor(x => x.PreferredPickupDate).Must(d => d is null || (d >= today && d <= today.AddDays(30)))
            .WithMessage("Choose a pickup date within the next 30 days.");
        RuleFor(x => x.Notes).SafeText(500);
    }
}
