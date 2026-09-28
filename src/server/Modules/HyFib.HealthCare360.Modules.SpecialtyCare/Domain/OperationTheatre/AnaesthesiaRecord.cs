namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

/// <summary>ASA physical status classification (I–VI); the "E" suffix is stored separately.</summary>
public enum AsaGrade
{
    I = 1,
    II,
    III,
    IV,
    V,
    VI,
}

public enum AirwayDevice
{
    None = 0,
    FaceMask,
    SupraglotticAirway,
    EndotrachealTube,
    Tracheostomy,
}

/// <summary>One row of the intra-operative vitals chart (typically every 5–15 minutes).</summary>
public sealed record AnaesthesiaVitals(
    DateTimeOffset At,
    int? HeartRate,
    int? Systolic,
    int? Diastolic,
    int? SpO2,
    int? EtCo2,
    decimal? TemperatureC,
    int? RespiratoryRate);

public sealed record AnaesthesiaDrug(DateTimeOffset At, string Drug, string Dose, string? Route);

public sealed record AnaesthesiaFluid(DateTimeOffset At, string Fluid, int VolumeMl);

/// <summary>
/// Anaesthesia record (OT-04). The vitals timeline, drugs and fluids are JSON rows because they are always read
/// and printed as one chart with the record. Once signed by the anaesthetist the record is locked (medico-legal).
/// </summary>
public sealed class AnaesthesiaRecord : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid SurgeryBookingId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? AnaesthetistId { get; set; }

    public AsaGrade? AsaGrade { get; set; }

    /// <summary>"E" suffix: emergency surgery (raises the anaesthetic risk independent of the grade).</summary>
    public bool AsaEmergency { get; set; }

    public AnaesthesiaType Technique { get; set; }
    public AirwayDevice Airway { get; set; }

    /// <summary>Airway details: Mallampati class, tube size, Cormack–Lehane grade, attempts.</summary>
    public string? AirwayNotes { get; set; }

    public DateTimeOffset? AnaesthesiaStartAt { get; set; }
    public DateTimeOffset? AnaesthesiaEndAt { get; set; }
    public List<AnaesthesiaVitals> Vitals { get; set; } = [];
    public List<AnaesthesiaDrug> Drugs { get; set; } = [];
    public List<AnaesthesiaFluid> Fluids { get; set; } = [];
    public int? EstimatedBloodLossMl { get; set; }
    public int? UrineOutputMl { get; set; }
    public string? Complications { get; set; }
    public string? RecoveryNotes { get; set; }

    public DateTimeOffset? SignedAt { get; set; }
    public string? SignedByName { get; set; }

    public bool IsSigned => SignedAt is not null;

    public Result EnsureEditable() =>
        IsSigned ? Error.Rule("anaesthesia.signed", "This anaesthesia record is signed and can no longer be changed.") : Result.Success();

    /// <summary>Adds chart rows, keeping the timeline in time order (rows are often entered late, after a crisis).</summary>
    public Result AddVitals(IEnumerable<AnaesthesiaVitals> rows)
    {
        var editable = EnsureEditable();
        if (editable.IsFailure)
        {
            return editable;
        }

        Vitals = Vitals.Concat(rows).OrderBy(r => r.At).ToList();
        return Result.Success();
    }

    public Result Sign(string signedBy, DateTimeOffset now)
    {
        var editable = EnsureEditable();
        if (editable.IsFailure)
        {
            return editable;
        }

        if (AsaGrade is null)
        {
            return Error.Field("asaGrade", "Record the ASA grade before signing.");
        }

        SignedAt = now;
        SignedByName = signedBy;
        return Result.Success();
    }
}
