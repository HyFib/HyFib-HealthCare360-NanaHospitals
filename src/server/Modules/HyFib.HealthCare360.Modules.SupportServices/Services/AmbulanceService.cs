using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Ambulance;
using HyFib.HealthCare360.Modules.SupportServices.Features.Ambulance;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SupportServices.Services;

/// <summary>Charge keys and service codes for ambulance trips.</summary>
public static class AmbulanceCharges
{
    public const string Source = "supportservices.ambulance";

    /// <summary>The base fare covers this many kilometres; each one beyond is charged as <see cref="PerKm"/>.</summary>
    public const int IncludedKm = 10;

    public const string PerKm = "AMB-KM";

    public static string BaseFare(AmbulanceKind kind) => kind switch
    {
        AmbulanceKind.Als => "AMB-ALS",
        AmbulanceKind.PatientTransport => "AMB-PTS",
        AmbulanceKind.Hearse => "AMB-HEARSE",
        _ => "AMB-BLS",
    };

    public static string BaseReference(Guid tripId) => $"{tripId}:base";

    public static string KmReference(Guid tripId) => $"{tripId}:km";
}

/// <summary>
/// The ambulance desk: the fleet with its certificates, calls taken and vehicles sent, each step of the trip as the crew
/// report it, and the trip log with the response times. A completed trip for a registered patient goes on their bill.
/// </summary>
internal sealed class AmbulanceService(
    HospitalDbContext db, INumberSeries numbers, ICurrentUser currentUser, IChargePoster charges, TimeProvider time)
{
    public async Task<IReadOnlyList<AmbulanceVehicleDto>> VehiclesAsync(CancellationToken ct)
    {
        var vehicles = await db.Set<AmbulanceVehicle>().AsNoTracking().OrderBy(v => v.Kind).ThenBy(v => v.RegistrationNumber).ToListAsync(ct);
        var onTrip = await db.Set<AmbulanceTrip>().AsNoTracking()
            .Where(t => t.VehicleId != null && (t.Status == TripStatus.Dispatched || t.Status == TripStatus.AtScene || t.Status == TripStatus.Transporting))
            .Select(t => new { VehicleId = t.VehicleId!.Value, t.Id }).ToListAsync(ct);
        var today = time.Today();
        return vehicles.Select(v => new AmbulanceVehicleDto(v.Id, v.RegistrationNumber, v.Kind, v.FitnessValidUntil, v.InsuranceValidUntil,
            v.PollutionValidUntil, v.OdometerKm, v.IsActive, v.Lapsed(today), onTrip.FirstOrDefault(t => t.VehicleId == v.Id)?.Id, v.Version)).ToList();
    }

    public async Task<Result<IReadOnlyList<AmbulanceVehicleDto>>> SaveVehicleAsync(SaveVehicleRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facility)
        {
            return Error.Rule("facility.required", "Choose the facility first.");
        }

        var number = string.Join(' ', request.RegistrationNumber.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        AmbulanceVehicle? vehicle;
        if (request.Id is { } id)
        {
            vehicle = await db.Set<AmbulanceVehicle>().FirstOrDefaultAsync(v => v.Id == id, ct);
            if (vehicle is null)
            {
                return Error.NotFound("Vehicle", id);
            }

            db.SetExpectedVersion(vehicle, request.Version!.Value);
            if (request.OdometerKm < vehicle.OdometerKm)
            {
                return Error.Field("odometerKm", $"The odometer reads {vehicle.OdometerKm} km; it does not go back.");
            }
        }
        else
        {
            vehicle = new AmbulanceVehicle { FacilityId = facility };
            db.Add(vehicle);
        }

        if (await db.Set<AmbulanceVehicle>().AnyAsync(v => v.RegistrationNumber == number && v.Id != vehicle.Id, ct))
        {
            return Error.Conflict("ambulance.duplicate", $"{number} is already in the fleet.");
        }

        vehicle.RegistrationNumber = number;
        vehicle.Kind = request.Kind;
        vehicle.FitnessValidUntil = request.FitnessValidUntil;
        vehicle.InsuranceValidUntil = request.InsuranceValidUntil;
        vehicle.PollutionValidUntil = request.PollutionValidUntil;
        vehicle.OdometerKm = request.OdometerKm;
        vehicle.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return Result<IReadOnlyList<AmbulanceVehicleDto>>.Ok(await VehiclesAsync(ct));
    }

    public Task<AmbulanceTrip?> FindAsync(Guid id, CancellationToken ct) => db.Set<AmbulanceTrip>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);

    /// <summary>The trips still open, oldest call first.</summary>
    public async Task<IReadOnlyList<AmbulanceTripDto>> ActiveAsync(CancellationToken ct)
    {
        var trips = await db.Set<AmbulanceTrip>().AsNoTracking()
            .Where(t => t.Status == TripStatus.Requested || t.Status == TripStatus.Dispatched || t.Status == TripStatus.AtScene
                || t.Status == TripStatus.Transporting)
            .OrderBy(t => t.RequestedAt).ToListAsync(ct);
        return await DtosAsync(trips, ct);
    }

    public async Task<Result<AmbulanceTripDto>> RequestAsync(RequestTripRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facility)
        {
            return Error.Rule("facility.required", "Choose the facility first.");
        }

        var trip = new AmbulanceTrip
        {
            FacilityId = facility,
            Purpose = request.Purpose,
            CallerName = request.CallerName.Trim(),
            CallerPhone = request.CallerPhone.Trim(),
            PickupAddress = request.PickupAddress.Trim(),
            DropAddress = string.IsNullOrWhiteSpace(request.DropAddress) ? null : request.DropAddress.Trim(),
            Complaint = string.IsNullOrWhiteSpace(request.Complaint) ? null : request.Complaint.Trim(),
            RequestedAt = time.GetUtcNow(),
            RequestedByName = currentUser.DisplayName ?? currentUser.UserName ?? "Staff",
        };

        if (request.PatientId is { } patientId)
        {
            var patient = await db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == patientId, ct);
            if (patient is null)
            {
                return Error.NotFound(nameof(Patient), patientId);
            }

            if (patient.IsDeceased != (request.Purpose == TripPurpose.BodyTransport))
            {
                return Error.Field("purpose", patient.IsDeceased ? "The patient's death is on record: book the hearse." : "A body transport is for a patient whose death is on record.");
            }

            trip.PatientId = patientId;
            trip.PatientName = patient.FullName;
        }
        else
        {
            trip.PatientName = request.PatientName!.Trim();
        }

        trip.Number = await numbers.NextAsync(SupportServicesNumberSeries.AmbulanceTrip, facility, ct);
        db.Add(trip);
        await db.SaveChangesAsync(ct);
        return await DtoAsync(trip, ct);
    }

    public async Task<Result<AmbulanceTripDto>> DispatchAsync(Guid id, DispatchRequest request, CancellationToken ct)
    {
        var trip = await db.Set<AmbulanceTrip>().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (trip is null)
        {
            return Error.NotFound("Trip", id);
        }

        var vehicle = await db.Set<AmbulanceVehicle>().FirstOrDefaultAsync(v => v.Id == request.VehicleId, ct);
        if (vehicle is null)
        {
            return Error.Field("vehicleId", "Choose a vehicle of the fleet.");
        }

        if (await db.Set<AmbulanceTrip>().AnyAsync(t => t.VehicleId == vehicle.Id && t.Id != id
                && (t.Status == TripStatus.Dispatched || t.Status == TripStatus.AtScene || t.Status == TripStatus.Transporting), ct))
        {
            return Error.Conflict("ambulance.vehicle_busy", $"{vehicle.RegistrationNumber} is out on another trip.");
        }

        db.SetExpectedVersion(trip, request.Version);
        var dispatched = trip.Dispatch(vehicle, request.DriverName, request.CrewName, time.Today(), time.GetUtcNow());
        if (dispatched.IsFailure)
        {
            return dispatched.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await DtoAsync(trip, ct);
    }

    public Task<Result<AmbulanceTripDto>> ReachedSceneAsync(Guid id, uint version, CancellationToken ct) =>
        StepAsync(id, version, (t, now) => t.ReachedScene(now), ct);

    public Task<Result<AmbulanceTripDto>> LeftSceneAsync(Guid id, uint version, CancellationToken ct) =>
        StepAsync(id, version, (t, now) => t.LeftScene(now), ct);

    public Task<Result<AmbulanceTripDto>> CancelAsync(Guid id, CancelTripRequest request, CancellationToken ct) =>
        StepAsync(id, request.Version, (t, now) => t.Cancel(request.Reason, now), ct);

    /// <summary>The vehicle is back; the trip is charged to a registered patient unless waived, in the same save.</summary>
    public async Task<Result<AmbulanceTripDto>> CompleteAsync(Guid id, CompleteTripRequest request, CancellationToken ct)
    {
        var trip = await db.Set<AmbulanceTrip>().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (trip is null)
        {
            return Error.NotFound("Trip", id);
        }

        var vehicle = await db.Set<AmbulanceVehicle>().FirstAsync(v => v.Id == trip.VehicleId, ct);
        db.SetExpectedVersion(trip, request.Version);
        var completed = trip.Complete(vehicle, request.EndOdometerKm, request.WaiveReason, time.GetUtcNow());
        if (completed.IsFailure)
        {
            return completed.Error!;
        }

        if (trip.Chargeable(AmbulanceCharges.IncludedKm) is { } km)
        {
            var codes = new[] { AmbulanceCharges.BaseFare(trip.VehicleKind!.Value), AmbulanceCharges.PerKm };
            var services = await db.Set<ServiceItem>().AsNoTracking().Where(s => codes.Contains(s.Code) && s.IsActive)
                .ToDictionaryAsync(s => s.Code, s => s.Id, ct);
            if (!services.TryGetValue(codes[0], out var baseFare) || (km.Beyond > 0 && !services.ContainsKey(codes[1])))
            {
                return Error.Rule("ambulance.no_tariff", $"The service {(services.ContainsKey(codes[0]) ? codes[1] : codes[0])} is missing from the catalogue; add it, or waive the charge.");
            }

            var posted = await charges.PostAsync(new ChargeRequest
            {
                PatientId = trip.PatientId!.Value,
                ServiceItemId = baseFare,
                Description = $"Ambulance {trip.Number} ({trip.VehicleNumber}), first {AmbulanceCharges.IncludedKm} km",
                SourceModule = AmbulanceCharges.Source,
                SourceReference = AmbulanceCharges.BaseReference(trip.Id),
                ServiceDate = trip.CompletedAt,
            }, ct);
            if (posted.IsSuccess && km.Beyond > 0)
            {
                posted = await charges.PostAsync(new ChargeRequest
                {
                    PatientId = trip.PatientId!.Value,
                    ServiceItemId = services[codes[1]],
                    Quantity = km.Beyond,
                    Description = $"Ambulance {trip.Number}: {km.Beyond} km beyond the first {AmbulanceCharges.IncludedKm}",
                    SourceModule = AmbulanceCharges.Source,
                    SourceReference = AmbulanceCharges.KmReference(trip.Id),
                    ServiceDate = trip.CompletedAt,
                }, ct);
            }

            if (posted.IsFailure)
            {
                return posted.Error!;
            }
        }

        await db.SaveChangesAsync(ct);
        return await DtoAsync(trip, ct);
    }

    public async Task<TripLog> LogAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5));
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5));
        var trips = await db.Set<AmbulanceTrip>().AsNoTracking().Where(t => t.RequestedAt >= start && t.RequestedAt < end)
            .OrderByDescending(t => t.RequestedAt).Take(1000).ToListAsync(ct);
        var emergencies = trips.Where(t => t.Purpose == TripPurpose.EmergencyPickup).ToList();
        return new TripLog(
            from, to, await DtosAsync(trips, ct),
            trips.Count(t => t.Status == TripStatus.Completed),
            trips.Count(t => t.Status == TripStatus.Cancelled),
            trips.Sum(t => t.Kilometres ?? 0),
            Median(emergencies.Where(t => t.DispatchedAt is not null).Select(t => (t.DispatchedAt!.Value - t.RequestedAt).TotalMinutes)),
            Median(emergencies.Where(t => t.AtSceneAt is not null).Select(t => (t.AtSceneAt!.Value - t.RequestedAt).TotalMinutes)));
    }

    public async Task<AmbulanceTripDto> DtoAsync(AmbulanceTrip trip, CancellationToken ct) => (await DtosAsync([trip], ct))[0];

    private async Task<Result<AmbulanceTripDto>> StepAsync(Guid id, uint version, Func<AmbulanceTrip, DateTimeOffset, Result> step, CancellationToken ct)
    {
        var trip = await db.Set<AmbulanceTrip>().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (trip is null)
        {
            return Error.NotFound("Trip", id);
        }

        db.SetExpectedVersion(trip, version);
        var done = step(trip, time.GetUtcNow());
        if (done.IsFailure)
        {
            return done.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await DtoAsync(trip, ct);
    }

    private async Task<List<AmbulanceTripDto>> DtosAsync(IReadOnlyCollection<AmbulanceTrip> trips, CancellationToken ct)
    {
        var ids = trips.Where(t => t.PatientId is not null).Select(t => t.PatientId!.Value).Distinct().ToList();
        var uhids = await db.Set<Patient>().AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Uhid, ct);
        return trips.Select(t => new AmbulanceTripDto(
            t.Id, t.Number, t.Purpose, t.PatientId, t.PatientId is { } p ? uhids.GetValueOrDefault(p) : null, t.PatientName, t.CallerName, t.CallerPhone,
            t.PickupAddress, t.DropAddress, t.Complaint, t.RequestedAt, t.RequestedByName, t.Status, t.VehicleNumber, t.VehicleKind, t.DriverName,
            t.CrewName, t.DispatchedAt, t.AtSceneAt, t.LeftSceneAt, t.CompletedAt, t.StartOdometerKm, t.EndOdometerKm, t.Kilometres,
            t.AtSceneAt is { } at ? (int)Math.Round((at - t.RequestedAt).TotalMinutes) : null, t.ChargeWaivedReason, t.CancelReason, t.Version)).ToList();
    }

    private static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? null
            : sorted.Count % 2 == 1 ? Math.Round(sorted[sorted.Count / 2], 1)
            : Math.Round((sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2, 1);
    }
}
