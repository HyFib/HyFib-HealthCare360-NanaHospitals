namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Mortuary;

/// <summary>A place in a cold chamber; it holds one body.</summary>
public sealed class MortuarySlot : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }

    /// <summary>The label on the chamber door, such as "A-3".</summary>
    public string Code { get; set; } = string.Empty;

    public string? Chamber { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum BodySource
{
    /// <summary>A patient who died in the hospital, whose death is certified or whose stay was discharged as expired.</summary>
    InHospitalDeath = 0,

    /// <summary>Brought to the emergency department without signs of life.</summary>
    BroughtDead,

    /// <summary>Brought by the police for the hospital's mortuary (an inquest body).</summary>
    Police,
}

public enum BodyStatus
{
    InStorage = 0,
    Released,
}

public enum ReleaseTo
{
    Relatives = 0,
    PoliceForPostMortem,
    BodyDonation,

    /// <summary>An unclaimed body handed to the municipality for disposal, on the police's clearance.</summary>
    Municipality,
}

/// <summary>Who received the body, and on what authority.</summary>
public sealed record BodyRelease(
    ReleaseTo To, string ReceivedBy, string? Relationship, string? IdProof, string? PoliceClearance, bool BelongingsHandedOver, string? Notes);

/// <summary>
/// The mortuary register: a body received into a cold-storage slot, with who brought it and what came with it, and its
/// release to the relatives, the police for a post-mortem, a medical college or the municipality. A medico-legal body
/// goes to relatives only once the police have cleared it (the inquest or post-mortem is done); an unclaimed body is
/// handed to the municipality on the police's clearance too.
/// </summary>
public sealed class BodyRecord : AuditableEntity, IFacilityScoped
{
    /// <summary>After this long in storage an unreleased body is unclaimed, and the police are to be told.</summary>
    public static readonly TimeSpan UnclaimedAfter = TimeSpan.FromHours(72);

    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public Guid? PatientId { get; set; }
    public BodySource Source { get; set; }

    /// <summary>The name, or a description for an unidentified body ("Unknown male, about 40").</summary>
    public string Name { get; set; } = string.Empty;

    public bool Identified { get; set; } = true;
    public string? Sex { get; set; }
    public int? AgeYears { get; set; }
    public DateTimeOffset? DiedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string ReceivedByName { get; set; } = string.Empty;
    public string? BroughtBy { get; set; }
    public bool IsMlc { get; set; }
    public string? MlcNumber { get; set; }
    public string? PoliceStation { get; set; }
    public string? IdentificationMarks { get; set; }

    /// <summary>Jewellery, money and clothes received with the body, listed.</summary>
    public string? Belongings { get; set; }

    /// <summary>The slot the body is in; null once released.</summary>
    public Guid? SlotId { get; private set; }

    /// <summary>The slot it was kept in, kept after the release for the register.</summary>
    public string? SlotCode { get; private set; }
    public BodyStatus Status { get; private set; }
    public DateTimeOffset? ReleasedAt { get; private set; }
    public ReleaseTo? ReleasedTo { get; private set; }
    public string? ReceivedBy { get; private set; }
    public string? Relationship { get; private set; }
    public string? IdProof { get; private set; }
    public string? PoliceClearance { get; private set; }
    public bool BelongingsHandedOver { get; private set; }
    public string? ReleaseNotes { get; private set; }
    public string? ReleasedByName { get; private set; }

    public bool Unclaimed(DateTimeOffset now) => Status == BodyStatus.InStorage && now - ReceivedAt > UnclaimedAfter;

    public void Store(MortuarySlot slot)
    {
        SlotId = slot.Id;
        SlotCode = slot.Code;
    }

    public Result Release(BodyRelease release, string byName, DateTimeOffset now)
    {
        if (Status == BodyStatus.Released)
        {
            return Error.Rule("mortuary.released", "This body has already been released.");
        }

        if (string.IsNullOrWhiteSpace(release.ReceivedBy))
        {
            return Error.Field("receivedBy", "Name who receives the body.");
        }

        switch (release.To)
        {
            case ReleaseTo.Relatives:
                if (string.IsNullOrWhiteSpace(release.Relationship) || string.IsNullOrWhiteSpace(release.IdProof))
                {
                    return Error.Field("idProof", "Record the relative's relationship and their identity proof.");
                }

                if (!Identified)
                {
                    return Error.Rule("mortuary.unidentified", "An unidentified body goes to relatives only once the police identify it.");
                }

                if (IsMlc && string.IsNullOrWhiteSpace(release.PoliceClearance))
                {
                    return Error.Field("policeClearance", "A medico-legal body goes to relatives only on the police's clearance: record the inquest or post-mortem reference.");
                }

                break;
            case ReleaseTo.PoliceForPostMortem:
                if (string.IsNullOrWhiteSpace(release.IdProof))
                {
                    return Error.Field("idProof", "Record the officer's name, number and police station.");
                }

                break;
            case ReleaseTo.Municipality:
                if (string.IsNullOrWhiteSpace(release.PoliceClearance))
                {
                    return Error.Field("policeClearance", "An unclaimed body goes for disposal only on the police's clearance.");
                }

                break;
            case ReleaseTo.BodyDonation:
                if (IsMlc)
                {
                    return Error.Rule("mortuary.mlc_donation", "A medico-legal body cannot be donated.");
                }

                break;
        }

        Status = BodyStatus.Released;
        ReleasedAt = now;
        ReleasedTo = release.To;
        ReceivedBy = release.ReceivedBy.Trim();
        Relationship = Trim(release.Relationship);
        IdProof = Trim(release.IdProof);
        PoliceClearance = Trim(release.PoliceClearance);
        BelongingsHandedOver = release.BelongingsHandedOver;
        ReleaseNotes = Trim(release.Notes);
        ReleasedByName = byName;
        SlotId = null;
        return Result.Success();
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
