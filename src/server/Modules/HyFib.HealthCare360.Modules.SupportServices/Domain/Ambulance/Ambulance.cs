namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Ambulance;

/// <summary>What a vehicle is equipped and staffed for (AIS-125 types): what it may be sent on, and how it is charged.</summary>
public enum AmbulanceKind
{
    /// <summary>Basic life support (type C): oxygen, suction, splints; an emergency medical technician.</summary>
    Bls = 0,

    /// <summary>Advanced life support (type D): monitor-defibrillator, ventilator, drugs; a paramedic or nurse.</summary>
    Als,

    /// <summary>Patient transport (type B): a stable patient to or from home or another hospital.</summary>
    PatientTransport,

    /// <summary>Hearse: a body from the mortuary.</summary>
    Hearse,
}

public enum TripPurpose
{
    /// <summary>An emergency call: the patient is picked up and brought in.</summary>
    EmergencyPickup = 0,

    /// <summary>A patient moved to or from another hospital.</summary>
    InterFacilityTransfer,

    /// <summary>A discharged patient taken home, or a patient brought in for a planned visit.</summary>
    ScheduledTransport,

    /// <summary>A body taken from the mortuary.</summary>
    BodyTransport,
}

public enum TripStatus
{
    Requested = 0,
    Dispatched,
    AtScene,
    Transporting,
    Completed,
    Cancelled,
}

/// <summary>
/// A vehicle of the hospital's fleet. It is sent out only while its fitness certificate, insurance and pollution
/// certificate are in force, and only on a trip its kind can take: an emergency needs a life-support ambulance, a body
/// goes only in a hearse, and a hearse carries nobody living.
/// </summary>
public sealed class AmbulanceVehicle : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }

    /// <summary>The registration number, as on the plate ("TN 49 AX 2071").</summary>
    public string RegistrationNumber { get; set; } = string.Empty;

    public AmbulanceKind Kind { get; set; }
    public DateOnly? FitnessValidUntil { get; set; }
    public DateOnly? InsuranceValidUntil { get; set; }
    public DateOnly? PollutionValidUntil { get; set; }
    public int OdometerKm { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>The certificates that have lapsed on <paramref name="today"/>; a vehicle with any is not sent out.</summary>
    public IReadOnlyList<string> Lapsed(DateOnly today) =>
        new (string Name, DateOnly? Until)[] { ("fitness certificate", FitnessValidUntil), ("insurance", InsuranceValidUntil), ("pollution certificate", PollutionValidUntil) }
            .Where(c => c.Until is null || c.Until < today)
            .Select(c => c.Name)
            .ToList();

    public static bool Suits(AmbulanceKind kind, TripPurpose purpose) => purpose switch
    {
        TripPurpose.EmergencyPickup => kind is AmbulanceKind.Bls or AmbulanceKind.Als,
        TripPurpose.InterFacilityTransfer => kind is AmbulanceKind.Bls or AmbulanceKind.Als or AmbulanceKind.PatientTransport,
        TripPurpose.ScheduledTransport => kind is AmbulanceKind.Bls or AmbulanceKind.PatientTransport,
        TripPurpose.BodyTransport => kind == AmbulanceKind.Hearse,
        _ => false,
    };
}

/// <summary>
/// A trip: the call, the vehicle and crew sent, the times it reached the patient and came back, and the kilometres run.
/// Each step follows the last; the odometer only goes forward. A completed trip for a patient is charged on their bill
/// (the base fare for the vehicle's kind, which covers the first kilometres, then each kilometre beyond) unless waived
/// with a reason, such as a free emergency transport scheme.
/// </summary>
public sealed class AmbulanceTrip : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public TripPurpose Purpose { get; set; }
    public Guid? PatientId { get; set; }

    /// <summary>The patient's name as the caller gave it, before they are registered.</summary>
    public string PatientName { get; set; } = string.Empty;

    public string CallerName { get; set; } = string.Empty;
    public string CallerPhone { get; set; } = string.Empty;
    public string PickupAddress { get; set; } = string.Empty;
    public string? DropAddress { get; set; }
    public string? Complaint { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public string RequestedByName { get; set; } = string.Empty;

    public TripStatus Status { get; private set; }
    public Guid? VehicleId { get; private set; }
    public string? VehicleNumber { get; private set; }
    public AmbulanceKind? VehicleKind { get; private set; }
    public string? DriverName { get; private set; }
    public string? CrewName { get; private set; }
    public int? StartOdometerKm { get; private set; }
    public int? EndOdometerKm { get; private set; }
    public DateTimeOffset? DispatchedAt { get; private set; }
    public DateTimeOffset? AtSceneAt { get; private set; }
    public DateTimeOffset? LeftSceneAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public string? CancelReason { get; private set; }

    /// <summary>Why the trip was not charged, when it was not.</summary>
    public string? ChargeWaivedReason { get; private set; }

    public int? Kilometres => EndOdometerKm - StartOdometerKm;

    public bool IsActive => Status is TripStatus.Requested or TripStatus.Dispatched or TripStatus.AtScene or TripStatus.Transporting;

    public Result Dispatch(AmbulanceVehicle vehicle, string driverName, string? crewName, DateOnly today, DateTimeOffset now)
    {
        if (Status != TripStatus.Requested)
        {
            return Error.Rule("ambulance.not_requested", "Only a trip waiting for a vehicle can be dispatched.");
        }

        if (!vehicle.IsActive)
        {
            return Error.Field("vehicleId", $"{vehicle.RegistrationNumber} is out of service.");
        }

        if (vehicle.Lapsed(today) is { Count: > 0 } lapsed)
        {
            return Error.Rule("ambulance.papers_lapsed", $"{vehicle.RegistrationNumber} cannot go out: its {string.Join(", ", lapsed)} is not on record or has lapsed.");
        }

        if (!AmbulanceVehicle.Suits(vehicle.Kind, Purpose))
        {
            return Error.Field("vehicleId", Purpose switch
            {
                TripPurpose.EmergencyPickup => "An emergency needs a life-support ambulance (BLS or ALS).",
                TripPurpose.BodyTransport => "A body goes in the hearse.",
                _ => vehicle.Kind == AmbulanceKind.Hearse ? "The hearse carries no patient." : "Send an ambulance suited to the transfer.",
            });
        }

        if ((Purpose is TripPurpose.EmergencyPickup or TripPurpose.InterFacilityTransfer) && vehicle.Kind != AmbulanceKind.PatientTransport
            && string.IsNullOrWhiteSpace(crewName))
        {
            return Error.Field("crewName", "Name the technician or paramedic going with the patient.");
        }

        Status = TripStatus.Dispatched;
        VehicleId = vehicle.Id;
        VehicleNumber = vehicle.RegistrationNumber;
        VehicleKind = vehicle.Kind;
        DriverName = driverName.Trim();
        CrewName = string.IsNullOrWhiteSpace(crewName) ? null : crewName.Trim();
        StartOdometerKm = vehicle.OdometerKm;
        DispatchedAt = now;
        return Result.Success();
    }

    public Result ReachedScene(DateTimeOffset now) => Step(TripStatus.Dispatched, TripStatus.AtScene, now, () => AtSceneAt = now, "reach the pickup");

    public Result LeftScene(DateTimeOffset now) => Step(TripStatus.AtScene, TripStatus.Transporting, now, () => LeftSceneAt = now, "leave the pickup");

    /// <summary>The vehicle is back: the kilometres run come from the odometer, and the vehicle's odometer moves on.</summary>
    public Result Complete(AmbulanceVehicle vehicle, int endOdometerKm, string? waiveReason, DateTimeOffset now)
    {
        if (Status != TripStatus.Transporting)
        {
            return Error.Rule("ambulance.not_transporting", "Complete the trip once the patient has been picked up and brought.");
        }

        if (endOdometerKm < StartOdometerKm)
        {
            return Error.Field("endOdometerKm", $"The odometer read {StartOdometerKm} km when the vehicle left; it cannot read less now.");
        }

        if (endOdometerKm - StartOdometerKm > 2000)
        {
            return Error.Field("endOdometerKm", "That is over 2,000 km for one trip; check the reading.");
        }

        Status = TripStatus.Completed;
        EndOdometerKm = endOdometerKm;
        CompletedAt = now;
        ChargeWaivedReason = string.IsNullOrWhiteSpace(waiveReason) ? null : waiveReason.Trim();
        vehicle.OdometerKm = endOdometerKm;
        return Result.Success();
    }

    public Result Cancel(string reason, DateTimeOffset now)
    {
        if (!IsActive)
        {
            return Error.Rule("ambulance.closed", "The trip is already closed.");
        }

        if (Status == TripStatus.Transporting)
        {
            return Error.Rule("ambulance.transporting", "The patient is on board: complete the trip instead.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Field("reason", "Say why the trip is cancelled.");
        }

        Status = TripStatus.Cancelled;
        CancelledAt = now;
        CancelReason = reason.Trim();
        return Result.Success();
    }

    /// <summary>How the base fare and the kilometres beyond those it covers are charged; nothing when waived or no patient.</summary>
    public (int Kilometres, int Beyond)? Chargeable(int includedKm) =>
        Status == TripStatus.Completed && PatientId is not null && ChargeWaivedReason is null && Kilometres is { } km
            ? (km, Math.Max(0, km - includedKm))
            : null;

    private Result Step(TripStatus from, TripStatus to, DateTimeOffset now, Action stamp, string what)
    {
        if (Status != from)
        {
            return Error.Rule("ambulance.out_of_order", $"The vehicle cannot {what} now: the trip is {Status}.");
        }

        Status = to;
        stamp();
        return Result.Success();
    }
}
