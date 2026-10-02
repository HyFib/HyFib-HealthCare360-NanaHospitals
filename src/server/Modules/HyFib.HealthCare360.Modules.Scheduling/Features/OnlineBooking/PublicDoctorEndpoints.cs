using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Services;

namespace HyFib.HealthCare360.Modules.Scheduling.Features.OnlineBooking;

/// <summary>
/// /api/v1/public/doctors — the anonymous Online Doctor Appointment directory (OA-01/02): doctors who accept online
/// bookings, their fees, languages, branches and live slots. Anonymous and rate-limited ("public" policy); requests
/// carry no facility, so every branch is listed unless the caller filters by <c>facilityId</c>. Booking itself needs
/// a patient-portal sign-in (see <c>/api/v1/portal/appointments</c>), so the directory is off while the portal is. While
/// video consultations are off (for the branch asked for, else hospital-wide) no doctor offers them and only in-person
/// times are listed.
/// </summary>
internal static class PublicDoctorEndpoints
{
    public const string RateLimitPolicy = RateLimitPolicies.Public;
    public const string Currency = "INR";
    private const int MaxPageSize = 50;
    private const int MaxSlotDays = 14;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/public/doctors")
            .WithTags("Online Appointments")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy)
            .RequireFeature(FeatureKeys.PatientPortal);

        group.MapGet("/", ListDoctors);
        group.MapGet("/filters", Filters);
        group.MapGet("/{id:guid}", Profile);
        group.MapGet("/{id:guid}/slots", Slots);
    }

    private static async Task<IResult> ListDoctors(
        [AsParameters] PageQuery query,
        Guid? specialtyId,
        Guid? departmentId,
        Guid? facilityId,
        string? language,
        bool? telehealth,
        HospitalDbContext db,
        AvailabilityService availability,
        SchedulingSettings settings,
        IFeatureFlags features,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest() with { PageSize = Math.Clamp(query.PageSize ?? 12, 1, MaxPageSize) };
        var video = await features.IsEnabledAsync(FeatureKeys.Telehealth, facilityId, ct);
        if (telehealth == true && !video)
        {
            return TypedResults.Ok(PagedResult<PublicDoctor>.Empty(page));
        }

        var doctors = Bookable(db, facilityId);
        if (specialtyId is not null)
        {
            doctors = doctors.Where(p => p.SpecialtyId == specialtyId);
        }

        if (departmentId is not null)
        {
            doctors = doctors.Where(p => p.DepartmentId == departmentId);
        }

        if (telehealth == true)
        {
            doctors = doctors.Where(p => p.AvailableForTelehealth);
        }

        if (!string.IsNullOrWhiteSpace(language))
        {
            var wanted = language.Trim();
            doctors = doctors.Where(p => p.Languages.Contains(wanted));
        }

        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            var specialties = db.Set<Specialty>().Where(s => EF.Functions.ILike(s.Name, like)).Select(s => (Guid?)s.Id);
            doctors = doctors.Where(p => EF.Functions.ILike(p.DisplayName, like)
                                         || (p.Qualification != null && EF.Functions.ILike(p.Qualification, like))
                                         || specialties.Contains(p.SpecialtyId));
        }

        var result = await doctors.OrderBy(p => p.DisplayName).ToPagedResultAsync(page, ct);
        var cards = await CardsAsync(result.Items, facilityId, video, db, availability, settings, time, ct);
        return TypedResults.Ok(new PagedResult<PublicDoctor>(cards, result.Page, result.PageSize, result.TotalCount));
    }

    private static async Task<IResult> Filters(Guid? facilityId, HospitalDbContext db, SchedulingSettings settings, CancellationToken ct)
    {
        var doctors = await Bookable(db, facilityId)
            .Select(p => new { p.Id, p.SpecialtyId, p.DepartmentId, p.Languages })
            .ToListAsync(ct);
        var ids = doctors.Select(d => d.Id).ToList();

        var specialtyIds = doctors.Where(d => d.SpecialtyId != null).Select(d => d.SpecialtyId!.Value).Distinct().ToList();
        var specialtyNames = await db.Set<Specialty>().AsNoTracking().Where(s => specialtyIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var departmentIds = doctors.Where(d => d.DepartmentId != null).Select(d => d.DepartmentId!.Value).Distinct().ToList();
        var departmentNames = await db.Set<Department>().AsNoTracking().Where(d => departmentIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        var facilityIds = await db.Set<DoctorSchedule>().AsNoTracking()
            .Where(s => ids.Contains(s.PractitionerId) && s.IsActive)
            .Select(s => s.FacilityId)
            .Distinct()
            .ToListAsync(ct);
        var facilities = await db.Set<Facility>().AsNoTracking()
            .Where(f => facilityIds.Contains(f.Id) && f.IsActive)
            .OrderBy(f => f.Name)
            .Select(f => new PublicFacility(f.Id, f.Name, f.Address.City))
            .ToListAsync(ct);

        var policy = await settings.OnlinePolicyAsync(facilityId, ct);
        return TypedResults.Ok(new DirectoryFilters(
            doctors.Where(d => d.SpecialtyId is { } s && specialtyNames.ContainsKey(s))
                .GroupBy(d => d.SpecialtyId!.Value)
                .Select(g => new DirectoryOption(g.Key, specialtyNames[g.Key], g.Count()))
                .OrderBy(o => o.Name)
                .ToList(),
            doctors.Where(d => d.DepartmentId is { } dep && departmentNames.ContainsKey(dep))
                .GroupBy(d => d.DepartmentId!.Value)
                .Select(g => new DirectoryOption(g.Key, departmentNames[g.Key], g.Count()))
                .OrderBy(o => o.Name)
                .ToList(),
            doctors.SelectMany(d => d.Languages).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            facilities,
            (int)policy.Cutoff.TotalMinutes,
            policy.WindowDays));
    }

    private static async Task<IResult> Profile(
        Guid id,
        Guid? facilityId,
        HospitalDbContext db,
        AvailabilityService availability,
        SchedulingSettings settings,
        IFeatureFlags features,
        TimeProvider time,
        CancellationToken ct)
    {
        var doctor = await Bookable(db, facilityId).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (doctor is null)
        {
            return Error.NotFound("Doctor", id).ToProblem();
        }

        var video = await features.IsEnabledAsync(FeatureKeys.Telehealth, facilityId, ct);
        var card = (await CardsAsync([doctor], facilityId, video, db, availability, settings, time, ct))[0];
        var today = time.Today();
        var sessions = await db.Set<DoctorSchedule>().AsNoTracking()
            .Where(s => s.PractitionerId == id && s.IsActive && (facilityId == null || s.FacilityId == facilityId)
                        && (s.EffectiveTo == null || s.EffectiveTo >= today))
            .Select(s => new PublicSession(s.Weekday, s.StartTime, s.EndTime, s.Mode, s.FacilityId))
            .ToListAsync(ct);

        return TypedResults.Ok(new PublicDoctorProfile(
            card,
            doctor.Bio,
            doctor.FollowUpValidityDays,
            sessions.Where(s => video || SlotEngine.Offers(s.Mode, ConsultationMode.InPerson))
                .Select(s => s with { Mode = Offered(s.Mode, video) })
                .OrderBy(s => s.Weekday)
                .ThenBy(s => s.StartTime)
                .ToList()));
    }

    /// <summary>Bookable slots for up to 14 days, never inside the online cut-off or beyond the booking window.</summary>
    private static async Task<IResult> Slots(
        Guid id,
        DateOnly? from,
        DateOnly? to,
        bool? telehealth,
        Guid? facilityId,
        HospitalDbContext db,
        AvailabilityService availability,
        SchedulingSettings settings,
        IFeatureFlags features,
        TimeProvider time,
        CancellationToken ct)
    {
        var doctor = await Bookable(db, facilityId).Where(p => p.Id == id).Select(p => new { p.Id, p.AvailableForTelehealth }).FirstOrDefaultAsync(ct);
        if (doctor is null)
        {
            return Error.NotFound("Doctor", id).ToProblem();
        }

        var video = await features.IsEnabledAsync(FeatureKeys.Telehealth, facilityId, ct);
        if (telehealth == true && !video)
        {
            return FeatureProblems.DisabledError(FeatureCatalog.Telehealth, forPatients: true).ToProblem();
        }

        if (telehealth == true && !doctor.AvailableForTelehealth)
        {
            return Error.Rule("appointment.telehealth_unavailable", "This doctor does not offer video consultations.").ToProblem();
        }

        var now = time.GetUtcNow();
        var today = time.Today();
        var policy = await settings.OnlinePolicyAsync(facilityId, ct);
        var start = from is { } f && f > today ? f : today;
        var end = to ?? start.AddDays(6);
        var lastDay = today.AddDays(policy.WindowDays);
        if (end > lastDay)
        {
            end = lastDay;
        }

        if (end.DayNumber - start.DayNumber >= MaxSlotDays)
        {
            end = start.AddDays(MaxSlotDays - 1);
        }

        if (end < start)
        {
            return TypedResults.Ok(Array.Empty<PublicSlotDay>());
        }

        var mode = telehealth switch
        {
            true => ConsultationMode.Telehealth,
            false => ConsultationMode.InPerson,
            _ => video ? (ConsultationMode?)null : ConsultationMode.InPerson,
        };
        var windowEnd = now.AddDays(policy.WindowDays);
        var slots = await availability.ComputeAsync(id, new SlotQuery(start, end, now + policy.Cutoff) { Mode = mode, FacilityId = facilityId }, ct);
        var days = slots
            .Where(s => s.StartsAt <= windowEnd)
            .GroupBy(s => s.Date)
            .Select(g => new PublicSlotDay(g.Key, g.Select(s => new PublicSlot(s.StartsAt, s.EndsAt, s.LocalStart, Offered(s.Mode, video), s.FacilityId, s.Available)).ToList()))
            .ToList();
        return TypedResults.Ok(days);
    }

    /// <summary>How a session or slot is offered to patients: without video consultations, "both" is in person only.</summary>
    internal static ConsultationMode Offered(ConsultationMode mode, bool video) =>
        video || mode != ConsultationMode.Both ? mode : ConsultationMode.InPerson;

    /// <summary>Active doctors that accept online bookings and have at least one active OPD session.</summary>
    private static IQueryable<Practitioner> Bookable(HospitalDbContext db, Guid? facilityId) =>
        db.Set<Practitioner>().AsNoTracking()
            .Where(p => p.IsActive && p.AvailableForOnlineBooking
                        && db.Set<DoctorSchedule>().Any(s => s.PractitionerId == p.Id && s.IsActive && (facilityId == null || s.FacilityId == facilityId)));

    /// <param name="video">Whether video consultations are on: when off, no doctor offers them.</param>
    private static async Task<List<PublicDoctor>> CardsAsync(
        IReadOnlyList<Practitioner> doctors,
        Guid? facilityId,
        bool video,
        HospitalDbContext db,
        AvailabilityService availability,
        SchedulingSettings settings,
        TimeProvider time,
        CancellationToken ct)
    {
        if (doctors.Count == 0)
        {
            return [];
        }

        var ids = doctors.Select(d => d.Id).ToList();
        var specialtyIds = doctors.Where(d => d.SpecialtyId != null).Select(d => d.SpecialtyId!.Value).Distinct().ToList();
        var specialties = await db.Set<Specialty>().AsNoTracking().Where(s => specialtyIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var departmentIds = doctors.Where(d => d.DepartmentId != null).Select(d => d.DepartmentId!.Value).Distinct().ToList();
        var departments = await db.Set<Department>().AsNoTracking().Where(d => departmentIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        var branches = await db.Set<DoctorSchedule>().AsNoTracking()
            .Where(s => ids.Contains(s.PractitionerId) && s.IsActive && (facilityId == null || s.FacilityId == facilityId))
            .Select(s => new { s.PractitionerId, s.FacilityId })
            .Distinct()
            .ToListAsync(ct);
        var facilityIds = branches.Select(b => b.FacilityId).Distinct().ToList();
        var facilities = await db.Set<Facility>().AsNoTracking()
            .Where(f => facilityIds.Contains(f.Id))
            .Select(f => new PublicFacility(f.Id, f.Name, f.Address.City))
            .ToListAsync(ct);

        var now = time.GetUtcNow();
        var today = time.Today();
        var policy = await settings.OnlinePolicyAsync(facilityId, ct);
        var slots = await availability.ComputeAsync(ids, new SlotQuery(today, today.AddDays(policy.WindowDays), now + policy.Cutoff) { FacilityId = facilityId }, ct);

        return doctors.Select(d =>
        {
            var mine = slots.GetValueOrDefault(d.Id) ?? [];
            var next = mine.FirstOrDefault(s => SlotEngine.Offers(s.Mode, ConsultationMode.InPerson));
            var offersVideo = video && d.AvailableForTelehealth;
            var nextVideo = offersVideo ? mine.FirstOrDefault(s => SlotEngine.Offers(s.Mode, ConsultationMode.Telehealth)) : null;
            var doctorBranches = branches.Where(b => b.PractitionerId == d.Id).Select(b => b.FacilityId).ToHashSet();
            return new PublicDoctor(
                d.Id,
                d.DisplayName,
                d.Gender,
                d.Qualification,
                d.SpecialtyId,
                d.SpecialtyId is { } s ? specialties.GetValueOrDefault(s) : null,
                d.DepartmentId,
                d.DepartmentId is { } dep ? departments.GetValueOrDefault(dep) : null,
                d.Languages,
                d.ConsultationFeeNew,
                d.ConsultationFeeFollowUp,
                Currency,
                offersVideo,
                facilities.Where(f => doctorBranches.Contains(f.Id)).ToList(),
                next?.StartsAt,
                nextVideo?.StartsAt);
        }).ToList();
    }
}
