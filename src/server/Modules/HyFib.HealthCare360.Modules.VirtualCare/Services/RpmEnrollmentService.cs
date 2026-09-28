using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Features.Rpm;

namespace HyFib.HealthCare360.Modules.VirtualCare.Services;

/// <summary>
/// RPM enrolment, thresholds and device registration (RPM-01..03). A new enrolment starts with the programme's default
/// limits (<see cref="DefaultThresholds"/>) so alerts work from the first reading; clinicians then tailor them. Device
/// API keys are returned exactly once.
/// </summary>
public sealed class RpmEnrollmentService(HospitalDbContext db, ICurrentUser currentUser, TimeProvider time)
{
    public const int MaxStartOffsetDays = 30;

    public async Task<Result<RpmEnrollment>> EnrollAsync(EnrollPatientRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var patient = await db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PatientId, ct);
        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), request.PatientId);
        }

        if (!patient.IsActive || patient.IsDeceased || patient.MergedIntoPatientId is not null)
        {
            return Error.Rule("patient.inactive", "Only active patients can be enrolled.");
        }

        var program = await db.Set<RpmProgram>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.ProgramId && p.IsActive, ct);
        if (program is null)
        {
            return Error.Field("programId", "Choose an active programme.");
        }

        var team = await CareTeamAsync(request.CareTeamPractitionerIds, request.PrimaryPractitionerId, ct);
        if (team.IsFailure)
        {
            return team.Error!;
        }

        var duplicate = await db.Set<RpmEnrollment>().AnyAsync(
            e => e.PatientId == patient.Id && e.ProgramId == program.Id && (e.Status == RpmEnrollmentStatus.Active || e.Status == RpmEnrollmentStatus.Paused), ct);
        if (duplicate)
        {
            return Error.Conflict("rpm.already_enrolled", $"{patient.FullName} is already enrolled in {program.Name}.");
        }

        var today = time.Today();
        var start = request.StartDate ?? today;
        if (Math.Abs(start.DayNumber - today.DayNumber) > MaxStartOffsetDays)
        {
            return Error.Field("startDate", $"The start date must be within {MaxStartOffsetDays} days of today.");
        }

        var now = time.GetUtcNow();
        var enrollment = new RpmEnrollment
        {
            FacilityId = facilityId,
            PatientId = patient.Id,
            ProgramId = program.Id,
            CareTeamPractitionerIds = team.Value,
            PrimaryPractitionerId = request.PrimaryPractitionerId ?? team.Value[0],
            Status = RpmEnrollmentStatus.Active,
            StartDate = start,
            Goals = Clean(request.Goals),
            Notes = Clean(request.Notes),
        };
        enrollment.TouchThresholds(now);
        db.Add(enrollment);

        foreach (var (type, range) in DefaultThresholds.For(program.Kind))
        {
            var threshold = new RpmThreshold { EnrollmentId = enrollment.Id, Type = type };
            threshold.Apply(range);
            db.Add(threshold);
        }

        await db.SaveChangesAsync(ct);
        return enrollment;
    }

    public async Task<Result> UpdateAsync(Guid id, UpdateEnrollmentRequest request, CancellationToken ct)
    {
        var enrollment = await db.Set<RpmEnrollment>().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (enrollment is null)
        {
            return Error.NotFound(nameof(RpmEnrollment), id);
        }

        db.SetExpectedVersion(enrollment, request.Version);
        var team = await CareTeamAsync(request.CareTeamPractitionerIds, request.PrimaryPractitionerId, ct);
        if (team.IsFailure)
        {
            return team.Error!;
        }

        var status = enrollment.ChangeStatus(request.Status, request.StatusReason, time.Today());
        if (status.IsFailure)
        {
            return status;
        }

        enrollment.CareTeamPractitionerIds = team.Value;
        enrollment.PrimaryPractitionerId = request.PrimaryPractitionerId ?? team.Value[0];
        enrollment.Goals = Clean(request.Goals);
        enrollment.Notes = Clean(request.Notes);

        if (enrollment.IsClosed)
        {
            // Devices go back to the pool: they must not keep feeding a closed enrolment.
            var now = time.GetUtcNow();
            foreach (var device in await db.Set<RpmDevice>().Where(d => d.EnrollmentId == id).ToListAsync(ct))
            {
                device.AssignTo(null, now);
            }
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// Removes an enrolment created by mistake. Once readings or alerts exist the enrolment is part of the clinical
    /// record and can only be completed or withdrawn.
    /// </summary>
    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct)
    {
        var enrollment = await db.Set<RpmEnrollment>().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (enrollment is null)
        {
            return Error.NotFound(nameof(RpmEnrollment), id);
        }

        if (await db.Set<RpmReading>().AnyAsync(r => r.EnrollmentId == id, ct) || await db.Set<RpmAlert>().AnyAsync(a => a.EnrollmentId == id, ct))
        {
            return Error.Rule("rpm.enrollment_in_use", "This enrolment already has readings or alerts. Complete or withdraw it instead.");
        }

        var now = time.GetUtcNow();
        foreach (var device in await db.Set<RpmDevice>().Where(d => d.EnrollmentId == id).ToListAsync(ct))
        {
            device.AssignTo(null, now);
        }

        db.RemoveRange(await db.Set<RpmThreshold>().Where(t => t.EnrollmentId == id).ToListAsync(ct));
        db.Remove(enrollment);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>Replaces the enrolment's limits (types left out are removed).</summary>
    public async Task<Result> SetThresholdsAsync(Guid id, SetThresholdsRequest request, CancellationToken ct)
    {
        var enrollment = await db.Set<RpmEnrollment>().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (enrollment is null)
        {
            return Error.NotFound(nameof(RpmEnrollment), id);
        }

        for (var i = 0; i < request.Thresholds.Count; i++)
        {
            var input = request.Thresholds[i];
            var valid = new ThresholdRange(input.Min, input.Max, input.CriticalMin, input.CriticalMax).Validate();
            if (valid.IsFailure)
            {
                var field = valid.Error!.Fields?.Keys.FirstOrDefault() ?? "min";
                return Error.Field($"thresholds[{i}].{field}", $"{ReadingUnits.Label(input.Type)}: {valid.Error.Message}");
            }
        }

        db.SetExpectedVersion(enrollment, request.Version);
        var existing = await db.Set<RpmThreshold>().Where(t => t.EnrollmentId == id).ToListAsync(ct);
        foreach (var input in request.Thresholds)
        {
            var threshold = existing.FirstOrDefault(t => t.Type == input.Type);
            if (threshold is null)
            {
                threshold = new RpmThreshold { EnrollmentId = id, Type = input.Type };
                db.Add(threshold);
            }

            threshold.Apply(new ThresholdRange(input.Min, input.Max, input.CriticalMin, input.CriticalMax));
        }

        foreach (var removed in existing.Where(t => request.Thresholds.All(i => i.Type != t.Type)))
        {
            db.Remove(removed);
        }

        enrollment.TouchThresholds(time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result<DeviceKeyIssued>> RegisterDeviceAsync(RegisterDeviceRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var serial = request.SerialNumber.Trim().ToUpperInvariant();
        if (await db.Set<RpmDevice>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AnyAsync(d => d.SerialNumber == serial, ct))
        {
            return Error.Conflict("rpm.device_exists", $"A device with serial number {serial} is already registered.");
        }

        var now = time.GetUtcNow();
        var device = new RpmDevice
        {
            FacilityId = facilityId,
            SerialNumber = serial,
            Type = request.Type,
            Manufacturer = Clean(request.Manufacturer),
            Model = Clean(request.Model),
        };

        if (request.EnrollmentId is { } enrollmentId)
        {
            var assigned = await AssignableEnrollmentAsync(enrollmentId, ct);
            if (assigned.IsFailure)
            {
                return assigned.Error!;
            }

            device.AssignTo(assigned.Value, now);
        }

        var key = device.IssueApiKey(now);
        db.Add(device);
        await db.SaveChangesAsync(ct);
        return new DeviceKeyIssued(device.Id, device.SerialNumber, key, DeviceIngestion.Path, DeviceIngestion.KeyHeader);
    }

    public async Task<Result<DeviceKeyIssued>> RotateKeyAsync(Guid id, CancellationToken ct)
    {
        var device = await db.Set<RpmDevice>().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
        {
            return Error.NotFound(nameof(RpmDevice), id);
        }

        if (!device.IsActive)
        {
            return Error.Rule("rpm.device_inactive", "Reactivate this device by registering it again.");
        }

        var key = device.IssueApiKey(time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return new DeviceKeyIssued(device.Id, device.SerialNumber, key, DeviceIngestion.Path, DeviceIngestion.KeyHeader);
    }

    public async Task<Result> AssignDeviceAsync(Guid id, Guid? enrollmentId, CancellationToken ct)
    {
        var device = await db.Set<RpmDevice>().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
        {
            return Error.NotFound(nameof(RpmDevice), id);
        }

        if (!device.IsActive)
        {
            return Error.Rule("rpm.device_inactive", "This device is deactivated.");
        }

        RpmEnrollment? enrollment = null;
        if (enrollmentId is { } wanted)
        {
            var assignable = await AssignableEnrollmentAsync(wanted, ct);
            if (assignable.IsFailure)
            {
                return assignable.Error!;
            }

            enrollment = assignable.Value;
        }

        device.AssignTo(enrollment, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> DeactivateDeviceAsync(Guid id, CancellationToken ct)
    {
        var device = await db.Set<RpmDevice>().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
        {
            return Error.NotFound(nameof(RpmDevice), id);
        }

        device.Deactivate();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    private async Task<Result<RpmEnrollment>> AssignableEnrollmentAsync(Guid enrollmentId, CancellationToken ct)
    {
        var enrollment = await db.Set<RpmEnrollment>().FirstOrDefaultAsync(e => e.Id == enrollmentId, ct);
        if (enrollment is null)
        {
            return Error.Field("enrollmentId", "Choose an enrolment of this facility.");
        }

        return enrollment.IsClosed
            ? Error.Rule("rpm.enrollment_closed", "This enrolment has ended; devices cannot be assigned to it.")
            : enrollment;
    }

    /// <summary>Care-team members must be active practitioners; the primary (if given) must be one of them.</summary>
    private async Task<Result<List<Guid>>> CareTeamAsync(IReadOnlyList<Guid> ids, Guid? primary, CancellationToken ct)
    {
        var wanted = ids.Where(i => i != Guid.Empty).Distinct().ToList();
        var found = await db.Set<Practitioner>().AsNoTracking().Where(p => wanted.Contains(p.Id) && p.IsActive).Select(p => p.Id).ToListAsync(ct);
        if (wanted.Count == 0 || found.Count != wanted.Count)
        {
            return Error.Field("careTeamPractitionerIds", "Choose active doctors or nurses for the care team.");
        }

        if (primary is { } p && !wanted.Contains(p))
        {
            return Error.Field("primaryPractitionerId", "The primary clinician must be in the care team.");
        }

        return wanted;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
