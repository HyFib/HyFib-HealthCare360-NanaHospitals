namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;

/// <summary>
/// Enrolment of a maintenance-dialysis patient in this unit (DIA-01): dry weight, vascular access, weekly slot,
/// sero status and dialyzer reuse policy. One active profile per patient per facility.
/// </summary>
public sealed class DialysisPatientProfile : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }

    /// <summary>Target post-dialysis weight set by the nephrologist; drives the ultrafiltration goal.</summary>
    public decimal DryWeightKg { get; set; }

    public VascularAccessType VascularAccess { get; set; }

    /// <summary>For example "Left radio-cephalic AVF" or "Right IJV tunnelled catheter".</summary>
    public string? AccessSite { get; set; }

    public DateOnly? AccessCreatedOn { get; set; }
    public List<Weekday> ScheduleDays { get; set; } = [];
    public DialysisShift Shift { get; set; }
    public int SessionMinutes { get; set; } = 240;
    public SeroStatus SeroStatus { get; set; }
    public DateOnly? SeroTestedOn { get; set; }
    public DialyzerReusePolicy ReusePolicy { get; set; }

    /// <summary>Maximum uses per dialyzer when reuse is allowed (unit policy, commonly 6–10).</summary>
    public int MaxDialyzerUses { get; set; } = 1;

    public Guid? NephrologistId { get; set; }
    public string? PrimaryDiagnosis { get; set; }
    public string? AnticoagulationNotes { get; set; }
    public DateOnly EnrolledOn { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Single use is forced for HBsAg-positive patients whatever the unit policy says.</summary>
    public int EffectiveMaxDialyzerUses =>
        ReusePolicy == DialyzerReusePolicy.SingleUse || !SeroSegregation.AllowsDialyzerReuse(SeroStatus) ? 1 : Math.Max(1, MaxDialyzerUses);

    public bool IsDueOn(DateOnly date) => IsActive && ScheduleDays.Contains(ToWeekday(date.DayOfWeek));

    public static Weekday ToWeekday(DayOfWeek day) => day == DayOfWeek.Sunday ? Weekday.Sunday : (Weekday)(int)day;
}
