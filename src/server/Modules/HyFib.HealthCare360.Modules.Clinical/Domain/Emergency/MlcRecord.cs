namespace HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;

public enum MlcCause
{
    RoadTrafficAccident = 0,
    Assault,
    SexualAssault,
    Burns,
    Poisoning,
    Fall,
    Hanging,
    Drowning,
    AnimalBite,
    Firearm,
    IndustrialAccident,
    Unknown,
    Other,
}

public enum MlcBroughtBy
{
    Self = 0,
    Relative,
    Police,

    /// <summary>
    /// A member of the public: a Good Samaritan, who is not compelled to give their name, address or other personal details,
    /// for the medico-legal case form or otherwise (Motor Vehicles Act 1988, s.134A; Central Motor Vehicles Rules 1989,
    /// rule 168).
    /// </summary>
    Public,
    Ambulance,
}

public enum MlcIntimationMode
{
    Phone = 0,
    Written,
    InPerson,
}

/// <summary>
/// A medico-legal case: the register entry the hospital keeps for an injury or poisoning the police must know of — who
/// brought the patient, what is alleged to have happened, the injuries as the doctor found them — and the intimation to
/// the police station, with who received it. The number goes on the visit (and the stay, and the chart, which is then
/// kept without a retention limit). An entry is corrected, not deleted; once the police are informed the facts they
/// were told stay as they were.
/// </summary>
public sealed class MlcRecord : AuditableEntity, IFacilityScoped
{
    /// <summary>The hospital's rule for informing the police: within an hour of the case being identified.</summary>
    public static readonly TimeSpan IntimateWithin = TimeSpan.FromHours(1);

    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }
    public Guid VisitId { get; set; }
    public string Number { get; set; } = string.Empty;
    public DateTimeOffset IdentifiedAt { get; set; }
    public MlcBroughtBy BroughtBy { get; private set; }
    public string? BroughtByName { get; private set; }
    public MlcCause Cause { get; private set; }
    public DateTimeOffset? IncidentAt { get; private set; }
    public string? IncidentPlace { get; private set; }

    /// <summary>The injuries as examined: kind, site, size, and whether simple or grievous.</summary>
    public string Injuries { get; private set; } = string.Empty;

    public bool? ConsciousOnArrival { get; private set; }
    public bool? SmellOfAlcohol { get; private set; }
    public string DoctorName { get; set; } = string.Empty;
    public string? DoctorRegistrationNumber { get; set; }
    public string PoliceStation { get; private set; } = string.Empty;
    public DateTimeOffset? IntimatedAt { get; private set; }
    public MlcIntimationMode? IntimationMode { get; private set; }

    /// <summary>The officer who received the intimation, with their number.</summary>
    public string? ReceivedBy { get; private set; }

    /// <summary>The police station's general diary (GD/DD) entry number for the intimation.</summary>
    public string? PoliceEntryNumber { get; private set; }

    public bool IsIntimated => IntimatedAt is not null;

    public bool IntimationOverdue(DateTimeOffset now) => !IsIntimated && now - IdentifiedAt > IntimateWithin;

    public Result Describe(
        MlcBroughtBy broughtBy, string? broughtByName, MlcCause cause, DateTimeOffset? incidentAt, string? incidentPlace, string injuries,
        bool? consciousOnArrival, bool? smellOfAlcohol, string policeStation, DateTimeOffset now)
    {
        if (IsIntimated)
        {
            return Error.Rule("mlc.intimated", "The police have been informed of this case; its facts stay as they were told.");
        }

        if (string.IsNullOrWhiteSpace(injuries))
        {
            return Error.Field("injuries", "Describe the injuries as examined.");
        }

        if (string.IsNullOrWhiteSpace(policeStation))
        {
            return Error.Field("policeStation", "Name the police station to inform.");
        }

        if (incidentAt > now)
        {
            return Error.Field("incidentAt", "The incident cannot be in the future.");
        }

        // A Good Samaritan gives their name only if they wish to (Central Motor Vehicles Rules 1989, rule 168).
        if (broughtBy is not (MlcBroughtBy.Self or MlcBroughtBy.Public) && string.IsNullOrWhiteSpace(broughtByName))
        {
            return Error.Field("broughtByName", "Name who brought the patient (and the police officer's number, if police).");
        }

        BroughtBy = broughtBy;
        BroughtByName = string.IsNullOrWhiteSpace(broughtByName) ? null : broughtByName.Trim();
        Cause = cause;
        IncidentAt = incidentAt;
        IncidentPlace = string.IsNullOrWhiteSpace(incidentPlace) ? null : incidentPlace.Trim();
        Injuries = injuries.Trim();
        ConsciousOnArrival = consciousOnArrival;
        SmellOfAlcohol = smellOfAlcohol;
        PoliceStation = policeStation.Trim();
        return Result.Success();
    }

    public Result Intimate(DateTimeOffset at, MlcIntimationMode mode, string receivedBy, string? policeEntryNumber, DateTimeOffset now)
    {
        if (IsIntimated)
        {
            return Error.Rule("mlc.intimated", "The police intimation is already recorded.");
        }

        if (at > now || at < IdentifiedAt.AddMinutes(-5))
        {
            return Error.Field("intimatedAt", "The police are informed after the case is identified, and not in the future.");
        }

        if (string.IsNullOrWhiteSpace(receivedBy))
        {
            return Error.Field("receivedBy", "Name the officer who received the intimation, with their number.");
        }

        IntimatedAt = at;
        IntimationMode = mode;
        ReceivedBy = receivedBy.Trim();
        PoliceEntryNumber = string.IsNullOrWhiteSpace(policeEntryNumber) ? null : policeEntryNumber.Trim();
        return Result.Success();
    }
}
