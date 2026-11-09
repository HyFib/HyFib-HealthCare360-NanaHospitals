namespace HyFib.HealthCare360.Modules.Inpatient.Domain;

/// <summary>How the patient is restrained.</summary>
public enum RestraintType
{
    /// <summary>Limb or body holders, mittens, a bed with sides used to hold the patient.</summary>
    Physical = 0,

    /// <summary>A medicine given to control behaviour, not to treat the illness (rapid tranquillisation).</summary>
    Chemical,
}

public enum RestraintStatus
{
    Active = 0,
    Released,
}

/// <summary>A doctor's renewal of the order, after seeing the patient again: until when, and why it is still needed.</summary>
public sealed record RestraintRenewal(DateTimeOffset RenewedAt, DateTimeOffset Until, string Reason, Guid? ByUserId, string? ByName);

/// <summary>
/// A restraint of an in-patient (Mental Healthcare Act 2017, s.97; NABH COP): ordered by a doctor as the last resort, with
/// the reason and the less restrictive measures tried first, for a limited time (at most <see cref="MaxOrderHours"/> hours
/// at a time); watched by the nurses at the interval the doctor set (circulation, skin, fluids, toileting); renewed only
/// by a doctor who sees the patient again, or released; and talked over afterwards (the debrief). Never edited: the order,
/// each check and each renewal stay as they were made.
/// </summary>
public sealed class RestraintEpisode : AuditableEntity, IFacilityScoped
{
    /// <summary>The longest an order (or a renewal) runs before a doctor must see the patient again.</summary>
    public const int MaxOrderHours = 4;

    public const int MinCheckMinutes = 15;
    public const int MaxCheckMinutes = 60;

    public Guid FacilityId { get; set; }
    public Guid AdmissionId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? WardId { get; set; }

    public RestraintType Type { get; private set; }

    /// <summary>What is used: "soft wrist holders, both wrists"; for a chemical restraint the medicine, dose and route.</summary>
    public string Method { get; private set; } = string.Empty;

    /// <summary>Why: the harm to the patient or others it prevents.</summary>
    public string Reason { get; private set; } = string.Empty;

    /// <summary>The less restrictive measures tried first (talking down, a sitter, removing the trigger).</summary>
    public string AlternativesTried { get; private set; } = string.Empty;

    /// <summary>How often the nurse checks the patient, in minutes.</summary>
    public int CheckIntervalMinutes { get; private set; }

    public DateTimeOffset OrderedAt { get; private set; }
    public Guid? OrderedByUserId { get; private set; }
    public Guid? OrderedByPractitionerId { get; private set; }
    public string? OrderedByName { get; private set; }

    /// <summary>When the order (or its last renewal) lapses: the doctor renews it after seeing the patient, or releases them.</summary>
    public DateTimeOffset OrderedUntil { get; private set; }

    public List<RestraintRenewal> Renewals { get; private set; } = [];

    public RestraintStatus Status { get; private set; }

    /// <summary>When the last nurse's check was made, or null before the first.</summary>
    public DateTimeOffset? LastCheckedAt { get; private set; }

    public DateTimeOffset? ReleasedAt { get; private set; }
    public Guid? ReleasedByUserId { get; private set; }
    public string? ReleasedByName { get; private set; }
    public string? ReleaseReason { get; private set; }

    /// <summary>The talk afterwards, with the patient and the staff: what led to it, and what may prevent the next one.</summary>
    public string? Debrief { get; private set; }

    public DateTimeOffset? DebriefedAt { get; private set; }
    public string? DebriefedByName { get; private set; }

    public bool IsActive => Status == RestraintStatus.Active;

    /// <summary>When the next nurse's check is due: an interval after the last check, or after the order for the first.</summary>
    public DateTimeOffset NextCheckDue => (LastCheckedAt ?? OrderedAt).AddMinutes(CheckIntervalMinutes);

    public bool IsCheckOverdue(DateTimeOffset now) => IsActive && now > NextCheckDue;

    /// <summary>The order ran out with the patient still restrained: no doctor renewed it or released them.</summary>
    public bool IsOrderLapsed(DateTimeOffset now) => IsActive && now > OrderedUntil;

    public static Result<RestraintEpisode> Order(
        Guid facilityId, Guid admissionId, Guid patientId, Guid? wardId, RestraintType type, string method, string reason, string alternativesTried,
        int hours, int checkIntervalMinutes, Guid? userId, Guid? practitionerId, string? name, DateTimeOffset now)
    {
        if (CheckHours(hours) is { } badHours)
        {
            return badHours;
        }

        if (checkIntervalMinutes is < MinCheckMinutes or > MaxCheckMinutes)
        {
            return Error.Field("checkIntervalMinutes", $"Set the nurses' checks every {MinCheckMinutes} to {MaxCheckMinutes} minutes.");
        }

        if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(alternativesTried))
        {
            return Error.Field("reason", "Say what is used, why, and which less restrictive measures were tried first.");
        }

        return new RestraintEpisode
        {
            FacilityId = facilityId,
            AdmissionId = admissionId,
            PatientId = patientId,
            WardId = wardId,
            Type = type,
            Method = method.Trim(),
            Reason = reason.Trim(),
            AlternativesTried = alternativesTried.Trim(),
            CheckIntervalMinutes = checkIntervalMinutes,
            OrderedAt = now,
            OrderedByUserId = userId,
            OrderedByPractitionerId = practitionerId,
            OrderedByName = name,
            OrderedUntil = now.AddHours(hours),
            Status = RestraintStatus.Active,
        };
    }

    /// <summary>A nurse's check, made at <paramref name="checkedAt"/>: due again an interval later.</summary>
    public Result RecordCheck(DateTimeOffset checkedAt, DateTimeOffset now)
    {
        if (!IsActive)
        {
            return Error.Rule("restraint.released", "The patient has been released: there is nothing more to check.");
        }

        if (checkedAt > now.AddMinutes(5) || checkedAt < OrderedAt)
        {
            return Error.Field("checkedAt", "The check is between the order and now.");
        }

        if (LastCheckedAt is null || checkedAt > LastCheckedAt)
        {
            LastCheckedAt = checkedAt;
        }

        return Result.Success();
    }

    /// <summary>A doctor, having seen the patient again, renews the order for up to <see cref="MaxOrderHours"/> hours from now.</summary>
    public Result Renew(int hours, string reason, Guid? userId, string? name, DateTimeOffset now)
    {
        if (!IsActive)
        {
            return Error.Rule("restraint.released", "The patient has been released: a new restraint is a new order.");
        }

        if (CheckHours(hours) is { } badHours)
        {
            return badHours;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Field("reason", "Say why the restraint is still needed after seeing the patient.");
        }

        OrderedUntil = now.AddHours(hours);
        Renewals = [.. Renewals, new RestraintRenewal(now, OrderedUntil, reason.Trim(), userId, name)];
        return Result.Success();
    }

    /// <summary>A doctor releases the patient: the restraint ends now.</summary>
    public Result Release(string reason, Guid? userId, string? name, DateTimeOffset now)
    {
        if (!IsActive)
        {
            return Error.Rule("restraint.released", "The patient has already been released.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Field("reason", "Say why the patient is released (calm, the danger has passed).");
        }

        Status = RestraintStatus.Released;
        ReleasedAt = now;
        ReleasedByUserId = userId;
        ReleasedByName = name;
        ReleaseReason = reason.Trim();
        return Result.Success();
    }

    /// <summary>The debrief, once the patient is released, recorded once.</summary>
    public Result RecordDebrief(string debrief, string? name, DateTimeOffset now)
    {
        if (IsActive)
        {
            return Error.Rule("restraint.active", "Release the patient before the debrief.");
        }

        if (Debrief is not null)
        {
            return Error.Rule("restraint.debriefed", "The debrief is already recorded.");
        }

        if (string.IsNullOrWhiteSpace(debrief))
        {
            return Error.Field("debrief", "Say what led to the restraint and what may prevent the next one.");
        }

        Debrief = debrief.Trim();
        DebriefedAt = now;
        DebriefedByName = name;
        return Result.Success();
    }

    private static Error? CheckHours(int hours) =>
        hours is < 1 or > MaxOrderHours
            ? Error.Field("hours", $"A restraint order runs 1 to {MaxOrderHours} hours; a doctor renews it after seeing the patient again.")
            : null;
}

/// <summary>
/// A nurse's check of a restrained patient: the blood flow and skin beyond the holders, fluids offered, toileting offered,
/// and how the patient is. A concern (poor circulation, broken skin) is written down with what was done.
/// </summary>
public sealed class RestraintCheck : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid EpisodeId { get; set; }
    public Guid AdmissionId { get; set; }
    public Guid PatientId { get; set; }

    public DateTimeOffset CheckedAt { get; set; }

    /// <summary>Colour, warmth and pulse beyond the holders as they should be.</summary>
    public bool CirculationOk { get; set; }

    /// <summary>No redness, abrasion or swelling under the holders.</summary>
    public bool SkinIntact { get; set; }

    public bool FluidsOffered { get; set; }
    public bool ToiletingOffered { get; set; }

    /// <summary>How the patient is (calm, agitated, asleep), and what was done about any concern.</summary>
    public string? Notes { get; set; }

    public Guid? CheckedByUserId { get; set; }
    public string? CheckedByName { get; set; }

    /// <summary>Whether the check found something wrong: poor circulation or broken skin.</summary>
    public bool HasConcern => !CirculationOk || !SkinIntact;
}
