using HyFib.HealthCare360.Modules.SupportServices.Domain.Ambulance;

namespace HyFib.HealthCare360.Modules.SupportServices.Features.Ambulance;

/// <param name="Lapsed">The certificates not on record or lapsed today; the vehicle is not sent out while any are.</param>
/// <param name="OnTripId">The trip it is out on, if any.</param>
public sealed record AmbulanceVehicleDto(
    Guid Id,
    string RegistrationNumber,
    AmbulanceKind Kind,
    DateOnly? FitnessValidUntil,
    DateOnly? InsuranceValidUntil,
    DateOnly? PollutionValidUntil,
    int OdometerKm,
    bool IsActive,
    IReadOnlyList<string> Lapsed,
    Guid? OnTripId,
    uint Version);

/// <summary>A vehicle added (no id) or changed (with its version).</summary>
public sealed record SaveVehicleRequest(
    Guid? Id,
    string RegistrationNumber,
    AmbulanceKind Kind,
    DateOnly? FitnessValidUntil,
    DateOnly? InsuranceValidUntil,
    DateOnly? PollutionValidUntil,
    int OdometerKm,
    bool IsActive,
    uint? Version);

public sealed record RequestTripRequest(
    TripPurpose Purpose,
    Guid? PatientId,
    string? PatientName,
    string CallerName,
    string CallerPhone,
    string PickupAddress,
    string? DropAddress,
    string? Complaint);

public sealed record DispatchRequest(Guid VehicleId, string DriverName, string? CrewName, uint Version);

public sealed record TripStepRequest(uint Version);

public sealed record CompleteTripRequest(int EndOdometerKm, string? WaiveReason, uint Version);

public sealed record CancelTripRequest(string Reason, uint Version);

/// <param name="ResponseMinutes">Call to the vehicle reaching the patient.</param>
public sealed record AmbulanceTripDto(
    Guid Id,
    string Number,
    TripPurpose Purpose,
    Guid? PatientId,
    string? Uhid,
    string PatientName,
    string CallerName,
    string CallerPhone,
    string PickupAddress,
    string? DropAddress,
    string? Complaint,
    DateTimeOffset RequestedAt,
    string RequestedByName,
    TripStatus Status,
    string? VehicleNumber,
    AmbulanceKind? VehicleKind,
    string? DriverName,
    string? CrewName,
    DateTimeOffset? DispatchedAt,
    DateTimeOffset? AtSceneAt,
    DateTimeOffset? LeftSceneAt,
    DateTimeOffset? CompletedAt,
    int? StartOdometerKm,
    int? EndOdometerKm,
    int? Kilometres,
    int? ResponseMinutes,
    string? ChargeWaivedReason,
    string? CancelReason,
    uint Version);

/// <summary>The trips of a period, and how quickly the fleet answered emergency calls.</summary>
/// <param name="MedianDispatchMinutes">Call to dispatch, for emergency pickups.</param>
/// <param name="MedianResponseMinutes">Call to reaching the patient, for emergency pickups.</param>
public sealed record TripLog(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<AmbulanceTripDto> Trips,
    int Completed,
    int Cancelled,
    int Kilometres,
    double? MedianDispatchMinutes,
    double? MedianResponseMinutes);

public sealed class SaveVehicleRequestValidator : AbstractValidator<SaveVehicleRequest>
{
    public SaveVehicleRequestValidator()
    {
        RuleFor(x => x.RegistrationNumber).NotEmpty().SafeText(20);
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.OdometerKm).InclusiveBetween(0, 5_000_000);
        RuleFor(x => x.Version).NotNull().When(x => x.Id is not null).WithMessage("Send the version of the vehicle you changed.");
    }
}

public sealed class RequestTripRequestValidator : AbstractValidator<RequestTripRequest>
{
    public RequestTripRequestValidator()
    {
        RuleFor(x => x.Purpose).IsInEnum();
        RuleFor(x => x.PatientName).NotEmpty().SafeText(200).When(x => x.PatientId is null)
            .WithMessage("Give the patient's name, or the deceased's for a body.");
        RuleFor(x => x.CallerName).NotEmpty().SafeText(200);
        RuleFor(x => x.CallerPhone).NotEmpty().Matches(@"^\+?[0-9 -]{6,16}$").WithMessage("Enter the caller's phone number.");
        RuleFor(x => x.PickupAddress).NotEmpty().SafeText(500);
        RuleFor(x => x.DropAddress).SafeText(500);
        RuleFor(x => x.DropAddress).NotEmpty().When(x => x.Purpose != TripPurpose.EmergencyPickup)
            .WithMessage("Say where the trip goes.");
        RuleFor(x => x.Complaint).SafeText(500);
    }
}

public sealed class DispatchRequestValidator : AbstractValidator<DispatchRequest>
{
    public DispatchRequestValidator()
    {
        RuleFor(x => x.VehicleId).NotEmpty();
        RuleFor(x => x.DriverName).NotEmpty().SafeText(200);
        RuleFor(x => x.CrewName).SafeText(200);
    }
}

public sealed class TripStepRequestValidator : AbstractValidator<TripStepRequest>;

public sealed class CompleteTripRequestValidator : AbstractValidator<CompleteTripRequest>
{
    public CompleteTripRequestValidator()
    {
        RuleFor(x => x.EndOdometerKm).GreaterThanOrEqualTo(0);
        RuleFor(x => x.WaiveReason).SafeText(300);
    }
}

public sealed class CancelTripRequestValidator : AbstractValidator<CancelTripRequest>
{
    public CancelTripRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().SafeText(300);
    }
}
