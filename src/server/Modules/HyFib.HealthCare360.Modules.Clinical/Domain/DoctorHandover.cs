namespace HyFib.HealthCare360.Modules.Clinical.Domain;

/// <summary>
/// A doctor hands their patients over to a colleague who covers for them (off duty, on leave, in theatre): from now until
/// <see cref="Until"/>, for every patient, or for one. The covering doctor may then acknowledge the critical results the
/// first doctor ordered (maker-checker, slice 15): someone answers for each critical value, and the record says on whose
/// handover. Recorded by the doctor who hands over, from their own sign-in; ended early by either of them.
/// </summary>
public sealed class DoctorHandover : AuditableEntity, IFacilityScoped
{
    /// <summary>The longest a handover lasts: a week's leave. A longer absence is handed over again.</summary>
    public static readonly TimeSpan LongestHandover = TimeSpan.FromDays(7);

    public Guid FacilityId { get; set; }

    /// <summary>The doctor who hands over.</summary>
    public Guid FromPractitionerId { get; set; }

    /// <summary>The covering doctor.</summary>
    public Guid ToPractitionerId { get; set; }

    /// <summary>One patient only; null for all of the first doctor's patients.</summary>
    public Guid? PatientId { get; set; }

    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset Until { get; private set; }
    public string? Note { get; set; }
    public DateTimeOffset? EndedAt { get; private set; }

    public bool CoversAt(DateTimeOffset at) => EndedAt is null && StartsAt <= at && at < Until;

    public static Result<DoctorHandover> Record(
        Guid facilityId, Guid from, Guid to, Guid? patientId, DateTimeOffset until, string? note, DateTimeOffset now)
    {
        if (from == to)
        {
            return Error.Field("toPractitionerId", "Hand over to another doctor.");
        }

        if (until <= now)
        {
            return Error.Field("until", "The handover ends in the future.");
        }

        if (until - now > LongestHandover)
        {
            return Error.Field("until", "A handover lasts up to 7 days; hand over again for a longer absence.");
        }

        return new DoctorHandover
        {
            FacilityId = facilityId,
            FromPractitionerId = from,
            ToPractitionerId = to,
            PatientId = patientId,
            StartsAt = now,
            Until = until,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        };
    }

    public Result End(DateTimeOffset now)
    {
        if (EndedAt is not null || Until <= now)
        {
            return Error.Rule("handover.ended", "This handover has already ended.");
        }

        EndedAt = now;
        return Result.Success();
    }
}
