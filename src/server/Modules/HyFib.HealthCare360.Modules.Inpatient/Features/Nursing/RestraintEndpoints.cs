using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.Nursing;

public sealed record RestraintCheckDto(
    Guid Id, DateTimeOffset CheckedAt, bool CirculationOk, bool SkinIntact, bool FluidsOffered, bool ToiletingOffered, string? Notes, string? CheckedByName,
    bool HasConcern);

public sealed record RestraintEpisodeDto(
    Guid Id,
    Guid AdmissionId,
    RestraintType Type,
    string Method,
    string Reason,
    string AlternativesTried,
    int CheckIntervalMinutes,
    DateTimeOffset OrderedAt,
    string? OrderedByName,
    DateTimeOffset OrderedUntil,
    IReadOnlyList<RestraintRenewal> Renewals,
    RestraintStatus Status,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset NextCheckDue,
    bool CheckOverdue,
    bool OrderLapsed,
    DateTimeOffset? ReleasedAt,
    string? ReleasedByName,
    string? ReleaseReason,
    string? Debrief,
    DateTimeOffset? DebriefedAt,
    string? DebriefedByName,
    IReadOnlyList<RestraintCheckDto> Checks);

/// <summary>A restrained patient on the ward: when the next check is due, and whether it, or the order, has run out.</summary>
public sealed record WardRestraintItem(
    Guid EpisodeId, Guid AdmissionId, Guid PatientId, string Uhid, string PatientName, string? BedLabel, RestraintType Type, string Method,
    DateTimeOffset? LastCheckedAt, DateTimeOffset NextCheckDue, bool CheckOverdue, int MinutesOverdue, DateTimeOffset OrderedUntil, bool OrderLapsed);

/// <summary>A doctor's restraint order: what, why, what was tried first, for how many hours, and how often the nurses check.</summary>
public sealed record OrderRestraintRequest(RestraintType Type, string Method, string Reason, string AlternativesTried, int Hours, int CheckIntervalMinutes);

public sealed record RecordRestraintCheckRequest(
    DateTimeOffset CheckedAt, bool CirculationOk, bool SkinIntact, bool FluidsOffered, bool ToiletingOffered, string? Notes);

public sealed record RenewRestraintRequest(int Hours, string Reason);

public sealed record ReleaseRestraintRequest(string Reason);

public sealed record RestraintDebriefRequest(string Debrief);

public sealed class OrderRestraintRequestValidator : AbstractValidator<OrderRestraintRequest>
{
    public OrderRestraintRequestValidator()
    {
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.Method).NotEmpty().SafeText(300);
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(10).SafeText(1000);
        RuleFor(x => x.AlternativesTried).NotEmpty().MinimumLength(5).SafeText(1000)
            .WithMessage("Say which less restrictive measures were tried first (a restraint is the last resort).");
        RuleFor(x => x.Hours).InclusiveBetween(1, RestraintEpisode.MaxOrderHours);
        RuleFor(x => x.CheckIntervalMinutes).InclusiveBetween(RestraintEpisode.MinCheckMinutes, RestraintEpisode.MaxCheckMinutes);
    }
}

public sealed class RecordRestraintCheckRequestValidator : AbstractValidator<RecordRestraintCheckRequest>
{
    public RecordRestraintCheckRequestValidator()
    {
        RuleFor(x => x.Notes).SafeText(1000);
        RuleFor(x => x.Notes).NotEmpty().When(x => !x.CirculationOk || !x.SkinIntact)
            .WithMessage("Poor circulation or broken skin: write down what you found and what was done.");
    }
}

public sealed class RenewRestraintRequestValidator : AbstractValidator<RenewRestraintRequest>
{
    public RenewRestraintRequestValidator()
    {
        RuleFor(x => x.Hours).InclusiveBetween(1, RestraintEpisode.MaxOrderHours);
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(10).SafeText(1000);
    }
}

public sealed class ReleaseRestraintRequestValidator : AbstractValidator<ReleaseRestraintRequest>
{
    public ReleaseRestraintRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(1000);
}

public sealed class RestraintDebriefRequestValidator : AbstractValidator<RestraintDebriefRequest>
{
    public RestraintDebriefRequestValidator() => RuleFor(x => x.Debrief).NotEmpty().MinimumLength(10).SafeText(2000);
}

/// <summary>
/// /api/v1/nursing — restraint of an in-patient (Mental Healthcare Act 2017 s.97, NABH COP). A doctor orders it (and only a
/// doctor renews or ends it, <see cref="InpatientPermissions.RestraintOrder"/>), for at most four hours at a time; the
/// nurses check the patient at the interval set (<see cref="InpatientPermissions.NursingChart"/>), and the ward's list shows
/// whose check, or whose order, has run out.
/// </summary>
internal static class RestraintEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/nursing").WithTags("Nursing");
        group.MapGet("/admissions/{admissionId:guid}/restraints", List).RequirePermission(InpatientPermissions.NursingChart);
        group.MapPost("/admissions/{admissionId:guid}/restraints", Order).RequirePermission(InpatientPermissions.RestraintOrder)
            .WithValidation<OrderRestraintRequest>();
        group.MapPost("/restraints/{id:guid}/checks", Check).RequirePermission(InpatientPermissions.NursingChart)
            .WithValidation<RecordRestraintCheckRequest>();
        group.MapPost("/restraints/{id:guid}/renew", Renew).RequirePermission(InpatientPermissions.RestraintOrder)
            .WithValidation<RenewRestraintRequest>();
        group.MapPost("/restraints/{id:guid}/release", Release).RequirePermission(InpatientPermissions.RestraintOrder)
            .WithValidation<ReleaseRestraintRequest>();
        group.MapPost("/restraints/{id:guid}/debrief", Debrief).RequirePermission(InpatientPermissions.NursingChart)
            .WithValidation<RestraintDebriefRequest>();
        group.MapGet("/wards/{wardId:guid}/restraints", WardList).RequirePermission(InpatientPermissions.NursingChart);
    }

    private static async Task<IResult> List(Guid admissionId, AdmissionAccess admissions, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var access = await admissions.OpenForReadAsync(admissionId, "Viewed restraints", ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var episodes = await db.Set<RestraintEpisode>().AsNoTracking().Where(e => e.AdmissionId == admissionId).OrderByDescending(e => e.OrderedAt).Take(100).ToListAsync(ct);
        var ids = episodes.Select(e => e.Id).ToList();
        var checks = (await db.Set<RestraintCheck>().AsNoTracking().Where(c => ids.Contains(c.EpisodeId)).OrderByDescending(c => c.CheckedAt).ToListAsync(ct))
            .ToLookup(c => c.EpisodeId);
        var now = time.GetUtcNow();
        return TypedResults.Ok(episodes.Select(e => Dto(e, checks[e.Id], now)).ToList());
    }

    private static async Task<IResult> Order(
        Guid admissionId, OrderRestraintRequest request, AdmissionAccess admissions, HospitalDbContext db, ICurrentUser user, IAuditLogger audit,
        TimeProvider time, CancellationToken ct)
    {
        var access = await admissions.OpenForChartingAsync(admissionId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var admission = access.Value;
        if (admission.Status is not (AdmissionStatus.Admitted or AdmissionStatus.DischargeInitiated))
        {
            return Error.Rule("restraint.stay_closed", "The stay has ended: there is no patient on the ward to restrain.").ToProblem();
        }

        if (user.PractitionerId is not { } doctor)
        {
            return Error.Forbidden("restraint.doctor_required", "A restraint is ordered by a doctor, signed in as themselves.").ToProblem();
        }

        if (await db.Set<RestraintEpisode>().AnyAsync(e => e.AdmissionId == admissionId && e.Status == RestraintStatus.Active, ct))
        {
            return Error.Conflict("restraint.already_active", "The patient is already restrained: renew that order, or release them first.").ToProblem();
        }

        var now = time.GetUtcNow();
        var ordered = RestraintEpisode.Order(
            admission.FacilityId, admission.Id, admission.PatientId, admission.WardId, request.Type, request.Method, request.Reason, request.AlternativesTried,
            request.Hours, request.CheckIntervalMinutes, user.UserId, doctor, user.DisplayName, now);
        if (ordered.IsFailure)
        {
            return ordered.Error!.ToProblem();
        }

        var episode = ordered.Value;
        db.Add(episode);
        Audit(audit, episode, "restraint.ordered",
            $"{episode.Type} restraint ordered for {request.Hours} h, checks every {episode.CheckIntervalMinutes} min: {episode.Method} ({episode.Reason})");
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(Dto(episode, [], now));
    }

    private static async Task<IResult> Check(
        Guid id, RecordRestraintCheckRequest request, AdmissionAccess admissions, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        var (episode, problem) = await OpenAsync(id, admissions, db, ct);
        if (episode is null)
        {
            return problem!;
        }

        var now = time.GetUtcNow();
        var recorded = episode.RecordCheck(request.CheckedAt, now);
        if (recorded.IsFailure)
        {
            return recorded.Error!.ToProblem();
        }

        db.Add(new RestraintCheck
        {
            FacilityId = episode.FacilityId,
            EpisodeId = episode.Id,
            AdmissionId = episode.AdmissionId,
            PatientId = episode.PatientId,
            CheckedAt = request.CheckedAt,
            CirculationOk = request.CirculationOk,
            SkinIntact = request.SkinIntact,
            FluidsOffered = request.FluidsOffered,
            ToiletingOffered = request.ToiletingOffered,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            CheckedByUserId = user.UserId,
            CheckedByName = user.DisplayName,
        });
        await db.SaveChangesAsync(ct);
        return await DtoAsync(db, episode, now, ct);
    }

    private static async Task<IResult> Renew(
        Guid id, RenewRestraintRequest request, AdmissionAccess admissions, HospitalDbContext db, ICurrentUser user, IAuditLogger audit, TimeProvider time,
        CancellationToken ct)
    {
        if (user.PractitionerId is null)
        {
            return Error.Forbidden("restraint.doctor_required", "A restraint is renewed by a doctor who has seen the patient, signed in as themselves.").ToProblem();
        }

        var (episode, problem) = await OpenAsync(id, admissions, db, ct);
        if (episode is null)
        {
            return problem!;
        }

        var now = time.GetUtcNow();
        var renewed = episode.Renew(request.Hours, request.Reason, user.UserId, user.DisplayName, now);
        if (renewed.IsFailure)
        {
            return renewed.Error!.ToProblem();
        }

        Audit(audit, episode, "restraint.renewed",
            $"{episode.Type} restraint renewed for {request.Hours} h (renewal {episode.Renewals.Count}): {request.Reason.Trim()}");
        await db.SaveChangesAsync(ct);
        return await DtoAsync(db, episode, now, ct);
    }

    private static async Task<IResult> Release(
        Guid id, ReleaseRestraintRequest request, AdmissionAccess admissions, HospitalDbContext db, ICurrentUser user, IAuditLogger audit, TimeProvider time,
        CancellationToken ct)
    {
        if (user.PractitionerId is null)
        {
            return Error.Forbidden("restraint.doctor_required", "A restraint is ended by a doctor, signed in as themselves.").ToProblem();
        }

        var (episode, problem) = await OpenAsync(id, admissions, db, ct);
        if (episode is null)
        {
            return problem!;
        }

        var now = time.GetUtcNow();
        var released = episode.Release(request.Reason, user.UserId, user.DisplayName, now);
        if (released.IsFailure)
        {
            return released.Error!.ToProblem();
        }

        Audit(audit, episode, "restraint.released",
            $"{episode.Type} restraint ended after {(int)(now - episode.OrderedAt).TotalMinutes} min: {request.Reason.Trim()}");
        await db.SaveChangesAsync(ct);
        return await DtoAsync(db, episode, now, ct);
    }

    private static async Task<IResult> Debrief(
        Guid id, RestraintDebriefRequest request, AdmissionAccess admissions, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        var (episode, problem) = await OpenAsync(id, admissions, db, ct);
        if (episode is null)
        {
            return problem!;
        }

        var now = time.GetUtcNow();
        var recorded = episode.RecordDebrief(request.Debrief, user.DisplayName, now);
        if (recorded.IsFailure)
        {
            return recorded.Error!.ToProblem();
        }

        await db.SaveChangesAsync(ct);
        return await DtoAsync(db, episode, now, ct);
    }

    /// <summary>The ward's restrained patients, the check due first (an overdue check, or a lapsed order, at the top).</summary>
    private static async Task<IResult> WardList(Guid wardId, HospitalDbContext db, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var rows = await (
                from e in db.Set<RestraintEpisode>().AsNoTracking()
                where e.WardId == wardId && e.Status == RestraintStatus.Active
                join p in db.Set<Patient>() on e.PatientId equals p.Id
                join a in db.Set<Admission>() on e.AdmissionId equals a.Id
                join b in db.Set<Bed>() on a.BedId equals b.Id into beds
                from b in beds.DefaultIfEmpty()
                select new { Episode = e, p.Uhid, p.FullName, BedLabel = b != null ? b.Label : null })
            .Take(200)
            .ToListAsync(ct);

        await audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, "ward.restraints.viewed", "Viewed the ward's restrained patients")
        {
            EntityType = nameof(Domain.Ward),
            EntityId = wardId.ToString(),
        }, ct);
        var now = time.GetUtcNow();
        return TypedResults.Ok(rows
            .Select(r => new WardRestraintItem(
                r.Episode.Id, r.Episode.AdmissionId, r.Episode.PatientId, r.Uhid, r.FullName, r.BedLabel, r.Episode.Type, r.Episode.Method,
                r.Episode.LastCheckedAt, r.Episode.NextCheckDue, r.Episode.IsCheckOverdue(now),
                r.Episode.IsCheckOverdue(now) ? (int)(now - r.Episode.NextCheckDue).TotalMinutes : 0,
                r.Episode.OrderedUntil, r.Episode.IsOrderLapsed(now)))
            .OrderByDescending(r => r.OrderLapsed || r.CheckOverdue)
            .ThenBy(r => r.NextCheckDue)
            .ToList());
    }

    /// <summary>The episode, tracked, with its stay open for charting (a closed stay is charted late through Medical Records).</summary>
    private static async Task<(RestraintEpisode? Episode, IResult? Problem)> OpenAsync(Guid id, AdmissionAccess admissions, HospitalDbContext db, CancellationToken ct)
    {
        var episode = await db.Set<RestraintEpisode>().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (episode is null)
        {
            return (null, Error.NotFound("Restraint", id).ToProblem());
        }

        var access = await admissions.OpenForChartingAsync(episode.AdmissionId, ct);
        return access.IsFailure ? (null, access.Error!.ToProblem()) : (episode, null);
    }

    private static async Task<IResult> DtoAsync(HospitalDbContext db, RestraintEpisode episode, DateTimeOffset now, CancellationToken ct)
    {
        var checks = await db.Set<RestraintCheck>().AsNoTracking().Where(c => c.EpisodeId == episode.Id).OrderByDescending(c => c.CheckedAt).ToListAsync(ct);
        return TypedResults.Ok(Dto(episode, checks, now));
    }

    private static void Audit(IAuditLogger audit, RestraintEpisode episode, string action, string description) =>
        audit.Add(new AuditEntry(AuditCategories.Clinical, action, description)
        {
            PatientId = episode.PatientId,
            EntityType = nameof(RestraintEpisode),
            EntityId = episode.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { admissionId = episode.AdmissionId, type = episode.Type.ToString(), until = episode.OrderedUntil },
        });

    private static RestraintEpisodeDto Dto(RestraintEpisode e, IEnumerable<RestraintCheck> checks, DateTimeOffset now) =>
        new(e.Id, e.AdmissionId, e.Type, e.Method, e.Reason, e.AlternativesTried, e.CheckIntervalMinutes, e.OrderedAt, e.OrderedByName, e.OrderedUntil,
            e.Renewals, e.Status, e.LastCheckedAt, e.NextCheckDue, e.IsCheckOverdue(now), e.IsOrderLapsed(now), e.ReleasedAt, e.ReleasedByName,
            e.ReleaseReason, e.Debrief, e.DebriefedAt, e.DebriefedByName,
            [.. checks.Select(c => new RestraintCheckDto(c.Id, c.CheckedAt, c.CirculationOk, c.SkinIntact, c.FluidsOffered, c.ToiletingOffered, c.Notes, c.CheckedByName, c.HasConcern))]);
}
