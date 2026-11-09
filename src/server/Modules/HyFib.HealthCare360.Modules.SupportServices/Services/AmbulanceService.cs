using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Ambulance;
using HyFib.HealthCare360.Modules.SupportServices.Features.Ambulance;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Settings;

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
/// report it, and the trip log with the response times. A completed trip for a registered patient goes on their bill;
/// waiving that charge waits for a second person who approves waivers (maker-checker, slice 15), the charge standing
/// until then, unless the hospital lets the desk waive alone (<see cref="WaiverApprovalSetting"/> off, two administrators).
/// </summary>
internal sealed class AmbulanceService(
    HospitalDbContext db, INumberSeries numbers, ICurrentUser currentUser, IChargePoster charges, IApprovals approvals, ISettingsService settings,
    IAuditLogger audit, TimeProvider time)
{
    /// <summary>The approval kind of a trip charge's waiver.</summary>
    public const string WaiverKind = "ambulance.charge_waiver";

    /// <summary>Facility setting (bool, on unless false): a waiver waits for a second person. Switching it off waits for a second administrator.</summary>
    public const string WaiverApprovalSetting = "ambulance.waiverApproval";

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

    /// <summary>
    /// The vehicle is back; the trip is charged to a registered patient in the same save. A waiver given with it is asked
    /// for at once after (<see cref="WaiveAsync"/>): the charge stands until a second person approves it.
    /// </summary>
    public async Task<Result<AmbulanceTripDto>> CompleteAsync(Guid id, CompleteTripRequest request, CancellationToken ct)
    {
        var trip = await db.Set<AmbulanceTrip>().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (trip is null)
        {
            return Error.NotFound("Trip", id);
        }

        var vehicle = await db.Set<AmbulanceVehicle>().FirstAsync(v => v.Id == trip.VehicleId, ct);
        db.SetExpectedVersion(trip, request.Version);
        var completed = trip.Complete(vehicle, request.EndOdometerKm, time.GetUtcNow(), request.WaiveReason);
        if (completed.IsFailure)
        {
            return completed.Error!;
        }

        var posted = await ChargeAsync(trip, ct);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        await db.SaveChangesAsync(ct);
        if (trip.PatientId is not null && !string.IsNullOrWhiteSpace(request.WaiveReason))
        {
            var waived = await WaiveAsync(trip.Id, new WaiveTripChargeRequest(request.WaiveReason, trip.Version), ct);
            if (waived.IsFailure)
            {
                return waived.Error!;
            }

            return waived.Value.Trip ?? await DtoAsync(await db.Set<AmbulanceTrip>().AsNoTracking().FirstAsync(t => t.Id == id, ct), ct);
        }

        return await DtoAsync(trip, ct);
    }

    /// <summary>
    /// Waives a completed trip's charge: asked for, and made only when someone else who approves waivers approves it
    /// (<see cref="WaiveApprovedAsync"/>). Where the hospital lets the desk waive alone, it is made at once, audited.
    /// </summary>
    public async Task<Result<WaiverOutcome>> WaiveAsync(Guid id, WaiveTripChargeRequest request, CancellationToken ct)
    {
        var trip = await db.Set<AmbulanceTrip>().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (trip is null)
        {
            return Error.NotFound("Trip", id);
        }

        if (trip.Version != request.Version)
        {
            return Error.Conflict("concurrency", "The trip changed since you opened it. Reload it and try again.");
        }

        var waivable = trip.CanWaive();
        if (waivable.IsFailure)
        {
            return waivable.Error!;
        }

        var name = currentUser.DisplayName ?? currentUser.UserName ?? "Staff";
        if (await settings.GetAsync<bool?>(WaiverApprovalSetting, trip.FacilityId, ct) == false)
        {
            var withdrawn = await WithdrawChargesAsync(trip, request.Reason, ct);
            if (withdrawn.IsFailure)
            {
                return withdrawn.Error!;
            }

            trip.Waive(request.Reason, currentUser.UserId, name, null, time.GetUtcNow());
            Audit(trip, $"Charge of ambulance trip {trip.Number} waived by {name} alone (a second person is not required here): {request.Reason.Trim()}", null);
            await db.SaveChangesAsync(ct);
            return new WaiverOutcome(await DtoAsync(trip, ct), null);
        }

        var amount = await AmountAsync(trip, ct);
        List<ApprovalDetail> details =
        [
            new("Trip", $"{trip.Number}, {trip.VehicleNumber}, {trip.Kilometres} km, completed {Documents(trip.CompletedAt)}"),
            new("Patient", trip.PatientName),
            new("Charge withdrawn", amount is { } a ? $"₹{Money.FormatInr(a)} (base fare and kilometres)" : "The base fare and kilometres"),
            new("Reason", request.Reason.Trim()),
            new("If billed meanwhile", "The approval fails and a credit note is asked for at the billing desk instead."),
        ];
        var asked = await approvals.RequestAsync(WaiverKind, new ApprovalSubject(nameof(AmbulanceTrip), trip.Id, trip.Version, trip.FacilityId, trip.PatientId),
            new AmbulanceWaiverPayload(trip.Id, request.Reason.Trim()), $"Waive ambulance trip {trip.Number} ({trip.PatientName})", details, request.Reason, ct);
        return asked.IsFailure ? asked.Error! : new WaiverOutcome(null, asked.Value);
    }

    /// <summary>
    /// Waives an approved trip's charge in the approval's transaction (the approval saves): only the trip as it was asked
    /// for; its charges are withdrawn from the bill, or the approval fails when they were billed meanwhile.
    /// </summary>
    internal async Task<Result<string>> WaiveApprovedAsync(ApprovalRequest approval, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<AmbulanceWaiverPayload>(approval.Payload);
        var trip = await db.Set<AmbulanceTrip>().FirstOrDefaultAsync(t => t.Id == asked.TripId, ct);
        if (trip is null)
        {
            return Error.NotFound("Trip", asked.TripId);
        }

        if (trip.Version != approval.SubjectVersion || trip.CanWaive() is { IsFailure: true })
        {
            return Error.Rule("approval.stale", $"Ambulance trip {trip.Number} changed after the waiver was asked for, so nothing was changed. Ask again.");
        }

        var withdrawn = await WithdrawChargesAsync(trip, asked.Reason, ct);
        if (withdrawn.IsFailure)
        {
            return withdrawn.Error!;
        }

        var name = currentUser.DisplayName ?? currentUser.UserName ?? "Unknown user";
        trip.Waive(asked.Reason, currentUser.UserId, name, approval.RequestedByName, time.GetUtcNow());
        var outcome = $"Charge of ambulance trip {trip.Number} waived";
        Audit(trip, $"{outcome}. Asked for by {approval.RequestedByName}, approved by {name}: {asked.Reason}", approval);
        return outcome;
    }

    private async Task<Result> ChargeAsync(AmbulanceTrip trip, CancellationToken ct)
    {
        if (trip.Chargeable(AmbulanceCharges.IncludedKm) is not { } km)
        {
            return Result.Success();
        }

        var codes = new[] { AmbulanceCharges.BaseFare(trip.VehicleKind!.Value), AmbulanceCharges.PerKm };
        var services = await db.Set<ServiceItem>().AsNoTracking().Where(s => codes.Contains(s.Code) && s.IsActive)
            .ToDictionaryAsync(s => s.Code, s => s.Id, ct);
        if (!services.TryGetValue(codes[0], out var baseFare) || (km.Beyond > 0 && !services.ContainsKey(codes[1])))
        {
            return Error.Rule("ambulance.no_tariff", $"The service {(services.ContainsKey(codes[0]) ? codes[1] : codes[0])} is missing from the catalogue; add it before completing the trip.");
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

        return posted.IsFailure ? posted.Error! : Result.Success();
    }

    /// <summary>The trip's charges taken off the bill (the module cancels its own charges); refused when already billed.</summary>
    private async Task<Result> WithdrawChargesAsync(AmbulanceTrip trip, string reason, CancellationToken ct)
    {
        if (trip.Chargeable(AmbulanceCharges.IncludedKm) is not { } km)
        {
            return Result.Success();
        }

        var why = $"Ambulance charge waived: {reason.Trim()}";
        var cancelled = await charges.CancelAsync(AmbulanceCharges.Source, AmbulanceCharges.BaseReference(trip.Id), why, ct);
        if (cancelled.IsSuccess && km.Beyond > 0)
        {
            cancelled = await charges.CancelAsync(AmbulanceCharges.Source, AmbulanceCharges.KmReference(trip.Id), why, ct);
        }

        return cancelled.IsFailure
            ? Error.Rule("ambulance.charge_billed", $"Trip {trip.Number}'s charge could not be withdrawn ({cancelled.Error!.Message}). If it is on a final bill, ask for a credit note at the billing desk.")
            : Result.Success();
    }

    private async Task<decimal?> AmountAsync(AmbulanceTrip trip, CancellationToken ct)
    {
        if (trip.Chargeable(AmbulanceCharges.IncludedKm) is not { } km || trip.VehicleKind is not { } kind)
        {
            return null;
        }

        var codes = new[] { AmbulanceCharges.BaseFare(kind), AmbulanceCharges.PerKm };
        var prices = await db.Set<ServiceItem>().AsNoTracking().Where(s => codes.Contains(s.Code)).ToDictionaryAsync(s => s.Code, s => s.BasePrice, ct);
        return prices.TryGetValue(codes[0], out var fare) ? fare + (km.Beyond * prices.GetValueOrDefault(codes[1])) : null;
    }

    private void Audit(AmbulanceTrip trip, string summary, ApprovalRequest? approval) =>
        audit.Add(new AuditEntry(AuditCategories.Data, "ambulance.charge_waived", summary.Length <= 1000 ? summary : summary[..999] + "…")
        {
            PatientId = trip.PatientId,
            EntityType = nameof(AmbulanceTrip),
            EntityId = trip.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { trip.Number, approval = approval?.Id, requestedBy = approval?.RequestedBy, waivedBy = currentUser.UserId },
        });

    private static string Documents(DateTimeOffset? at) => at is { } value ? HyFib.HealthCare360.Platform.Documents.DocumentFormat.DateTime(value) : "—";

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
        var completed = trips.Where(t => t.Status == TripStatus.Completed && t.ChargeWaivedReason is null).Select(t => t.Id).ToList();
        var now = time.GetUtcNow();
        var reader = currentUser.UserId;
        var requests = completed.Count == 0 ? [] : await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == WaiverKind && r.SubjectType == nameof(AmbulanceTrip) && completed.Contains(r.SubjectId) && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .Select(r => new { r.SubjectId, r.Id, r.Reason, r.RequestedByName, r.RequestedAt, r.RequestedBy })
            .ToListAsync(ct);
        var waiting = requests.GroupBy(r => r.SubjectId)
            .ToDictionary(g => g.Key, g => g.Select(r => new PendingWaiver(r.Id, r.Reason, r.RequestedByName, r.RequestedAt, r.RequestedBy == reader)).First());
        return trips.Select(t => new AmbulanceTripDto(
            t.Id, t.Number, t.Purpose, t.PatientId, t.PatientId is { } p ? uhids.GetValueOrDefault(p) : null, t.PatientName, t.CallerName, t.CallerPhone,
            t.PickupAddress, t.DropAddress, t.Complaint, t.RequestedAt, t.RequestedByName, t.Status, t.VehicleNumber, t.VehicleKind, t.DriverName,
            t.CrewName, t.DispatchedAt, t.AtSceneAt, t.LeftSceneAt, t.CompletedAt, t.StartOdometerKm, t.EndOdometerKm, t.Kilometres,
            t.AtSceneAt is { } at ? (int)Math.Round((at - t.RequestedAt).TotalMinutes) : null, t.ChargeWaivedReason, t.CancelReason, t.Version)
        {
            ChargeWaivedByName = t.ChargeWaivedByName,
            WaiverRequestedByName = t.WaiverRequestedByName,
            WaiverPending = waiting.GetValueOrDefault(t.Id),
        }).ToList();
    }

    private static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? null
            : sorted.Count % 2 == 1 ? Math.Round(sorted[sorted.Count / 2], 1)
            : Math.Round((sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2, 1);
    }
}

/// <summary>A waiver made at once (<see cref="Trip"/>, the hospital lets the desk waive alone), or asked for (<see cref="Asked"/>).</summary>
public sealed record WaiverOutcome(AmbulanceTripDto? Trip, ApprovalRequestDto? Asked);

/// <summary>The waiver asked for: the trip and why.</summary>
public sealed record AmbulanceWaiverPayload(Guid TripId, string Reason);

/// <summary>A completed ambulance trip's charge waived: the desk asks, and someone who approves waivers (accountants and hospital administrators by default) approves.</summary>
internal sealed class AmbulanceWaiverApproval(AmbulanceService ambulance) : IApprovalAction
{
    public string Kind => AmbulanceService.WaiverKind;

    public string Title => "Ambulance charge waiver";

    public string ApprovePermission => SupportServicesPermissions.AmbulanceWaiverApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => ambulance.WaiveApprovedAsync(request, ct);
}
