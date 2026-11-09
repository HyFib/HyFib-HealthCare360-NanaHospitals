using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Emr;

/// <summary>Hands the doctor's patients (all, or one) over to a covering colleague until <paramref name="Until"/>.</summary>
public sealed record RecordHandoverRequest(Guid ToPractitionerId, DateTimeOffset Until, Guid? PatientId, string? Note);

/// <summary>A handover between two doctors, given by the signed-in doctor or received by them.</summary>
public sealed record DoctorHandoverDto(
    Guid Id,
    Guid FromPractitionerId,
    string? FromName,
    Guid ToPractitionerId,
    string? ToName,
    Guid? PatientId,
    DateTimeOffset StartsAt,
    DateTimeOffset Until,
    DateTimeOffset? EndedAt,
    string? Note,
    uint Version);

public sealed record EndHandoverRequest(uint Version);

public sealed class RecordHandoverRequestValidator : AbstractValidator<RecordHandoverRequest>
{
    public RecordHandoverRequestValidator()
    {
        RuleFor(x => x.ToPractitionerId).NotEmpty();
        RuleFor(x => x.Note).SafeText(500);
    }
}

public sealed class EndHandoverRequestValidator : AbstractValidator<EndHandoverRequest>
{
    public EndHandoverRequestValidator() => RuleFor(x => x.Version).SeenVersion();
}

/// <summary>
/// /api/v1/emr/handovers — a doctor hands their patients over to a covering colleague, who may then acknowledge the
/// critical results the first doctor ordered (maker-checker, slice 15). Only the doctor handing over records it, from their
/// own sign-in; either of the two ends it early.
/// </summary>
internal static class HandoverEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/emr/handovers").WithTags("EMR");
        group.MapGet("/", (DoctorHandovers handovers, CancellationToken ct) => handovers.MineAsync(ct)).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapPost("/", async (RecordHandoverRequest request, DoctorHandovers handovers, CancellationToken ct) => (await handovers.RecordAsync(request, ct)).ToOk())
            .RequirePermission(ClinicalPermissions.EmrWrite).WithValidation<RecordHandoverRequest>();
        group.MapPost("/{id:guid}/end", async (Guid id, EndHandoverRequest request, DoctorHandovers handovers, CancellationToken ct) =>
                (await handovers.EndAsync(id, request.Version, ct)).ToOk())
            .RequirePermission(ClinicalPermissions.EmrWrite).WithValidation<EndHandoverRequest>();
    }
}

/// <summary>Doctors' handovers, and the slice 15 rule for acknowledging another doctor's critical result that reads them.</summary>
public sealed class DoctorHandovers(HospitalDbContext db, ICurrentUser currentUser, ISettingsService settings, IStayCareTeams careTeams, IAuditLogger audit, TimeProvider time)
{
    private static readonly PractitionerKind[] Doctors = [.. PractitionerKinds.Doctors];

    public async Task<IReadOnlyList<DoctorHandoverDto>> MineAsync(CancellationToken ct)
    {
        if (currentUser.PractitionerId is not { } me)
        {
            return [];
        }

        var now = time.GetUtcNow();
        var rows = await db.Set<DoctorHandover>().AsNoTracking()
            .Where(h => (h.FromPractitionerId == me || h.ToPractitionerId == me) && h.EndedAt == null && h.Until > now)
            .OrderBy(h => h.Until).ToListAsync(ct);
        return await DtosAsync(rows, ct);
    }

    public async Task<Result<DoctorHandoverDto>> RecordAsync(RecordHandoverRequest request, CancellationToken ct)
    {
        if (currentUser.PractitionerId is not { } me || currentUser.FacilityId is not { } facilityId)
        {
            return Error.Forbidden("handover.doctor_only", "A handover is recorded by the doctor who hands over, from an account linked to them.");
        }

        if (!await db.Set<Practitioner>().AnyAsync(p => p.Id == request.ToPractitionerId && p.IsActive && Doctors.Contains(p.Kind), ct))
        {
            return Error.Field("toPractitionerId", "Hand over to an active doctor in the practitioner list.");
        }

        var recorded = DoctorHandover.Record(facilityId, me, request.ToPractitionerId, request.PatientId, request.Until, request.Note, time.GetUtcNow());
        if (recorded.IsFailure)
        {
            return recorded.Error!;
        }

        db.Add(recorded.Value);
        await db.SaveChangesAsync(ct);
        return (await DtosAsync([recorded.Value], ct))[0];
    }

    public async Task<Result<DoctorHandoverDto>> EndAsync(Guid id, uint version, CancellationToken ct)
    {
        var handover = await db.Set<DoctorHandover>().FirstOrDefaultAsync(h => h.Id == id, ct);
        if (handover is null)
        {
            return Error.NotFound("Handover", id);
        }

        if (currentUser.PractitionerId is not { } me || (handover.FromPractitionerId != me && handover.ToPractitionerId != me))
        {
            return Error.Forbidden("handover.not_party", "Only the two doctors of a handover end it.");
        }

        db.SetExpectedVersion(handover, version);
        var ended = handover.End(time.GetUtcNow());
        if (ended.IsFailure)
        {
            return ended.Error!;
        }

        await db.SaveChangesAsync(ct);
        return (await DtosAsync([handover], ct))[0];
    }

    /// <summary>
    /// Whether the signed-in user may acknowledge <paramref name="review"/>. A result that is not critical, or the hospital
    /// relaxing the rule (<see cref="ClinicalSignOff.CriticalResultCoveringSetting"/> off, approved by a second administrator),
    /// lets any doctor; otherwise another doctor's critical result is acknowledged only by the ordering doctor or one covering
    /// for them (<see cref="ClinicalSignOff.MayAcknowledgeCritical"/>). An acknowledgement on someone's behalf is audited
    /// naming whose result it was and why the acknowledger covers it.
    /// </summary>
    public async Task<Result> EnsureMayAcknowledgeAsync(ResultReview review, CancellationToken ct)
    {
        if (!review.HasCriticalFindings || review.PractitionerId is not { } orderedBy || orderedBy == currentUser.PractitionerId)
        {
            return Result.Success();
        }

        if (await settings.GetAsync<bool?>(ClinicalSignOff.CriticalResultCoveringSetting, review.FacilityId, ct) == false)
        {
            return Result.Success();
        }

        var now = time.GetUtcNow();
        var stay = await careTeams.CurrentStayAsync(review.PatientId, ct);
        var handovers = currentUser.PractitionerId is { } me
            ? await db.Set<DoctorHandover>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(h => h.FromPractitionerId == orderedBy && h.ToPractitionerId == me && h.EndedAt == null && h.Until > now).ToListAsync(ct)
            : [];
        if (!ClinicalSignOff.MayAcknowledgeCritical(orderedBy, currentUser.PractitionerId, review.PatientId, stay, handovers, now))
        {
            var name = (await PrintQueries.PractitionerNamesAsync(db, [orderedBy], ct)).GetValueOrDefault(orderedBy) ?? "the ordering doctor";
            return Error.Forbidden("result.not_covering",
                $"This critical result is {name}'s. It is acknowledged by {name}, a doctor on the same stay's care team, or a doctor {name} handed over to.");
        }

        audit.Add(new AuditEntry(AuditCategories.Clinical, "result.acknowledged_covering",
            $"Critical result {review.Title} acknowledged for the ordering doctor, as {(handovers.Count > 0 ? "the covering doctor on a handover" : "a doctor on the stay's care team")}")
        {
            PatientId = review.PatientId,
            EntityType = nameof(ResultReview),
            EntityId = review.Id.ToString(),
            Details = new { orderedBy, handover = handovers.Select(h => h.Id).FirstOrDefault() },
        });
        return Result.Success();
    }

    private async Task<List<DoctorHandoverDto>> DtosAsync(IReadOnlyList<DoctorHandover> rows, CancellationToken ct)
    {
        var names = await PrintQueries.PractitionerNamesAsync(db, rows.SelectMany(h => new Guid?[] { h.FromPractitionerId, h.ToPractitionerId }), ct);
        return rows.Select(h => new DoctorHandoverDto(
            h.Id, h.FromPractitionerId, names.GetValueOrDefault(h.FromPractitionerId), h.ToPractitionerId, names.GetValueOrDefault(h.ToPractitionerId), h.PatientId,
            h.StartsAt, h.Until, h.EndedAt, h.Note, h.Version)).ToList();
    }
}
