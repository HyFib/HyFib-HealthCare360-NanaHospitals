namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Physiotherapy;

/// <summary>
/// Treatment plan (PHY-02): modalities, exercise programme, number of sessions and frequency. One plan per
/// referral, revised in place (optimistic concurrency) when the therapist extends or changes treatment.
/// </summary>
public sealed class TreatmentPlan : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid ReferralId { get; set; }
    public Guid PatientId { get; set; }
    public List<PhysioModality> Modalities { get; set; } = [];
    public string? ExerciseProgramme { get; set; }
    public int SessionsPlanned { get; set; }
    public int SessionsPerWeek { get; set; }
    public int SessionMinutes { get; set; } = 30;
    public DateOnly StartDate { get; set; }
    public string? Precautions { get; set; }
    public string? Goals { get; set; }
    public int Revision { get; set; } = 1;

    /// <summary>Sessions to plan: the therapist's number when given, otherwise the number prescribed on the order.</summary>
    public static Result<int> SessionsToPlan(int? requested, int? ordered)
    {
        if (requested is { } planned)
        {
            return planned;
        }

        return ordered is { } fromOrder ? fromOrder : Error.Field("sessionsPlanned", "Enter the number of sessions planned.");
    }

    /// <summary>
    /// Sessions beyond the plan are refused: extra treatment must be a conscious revision of the plan, which also
    /// keeps the patient's bill aligned with what was prescribed.
    /// </summary>
    public Result EnsureCanSchedule(int sessionsAlreadyBooked, int additional)
    {
        if (additional < 1)
        {
            return Error.Field("count", "Schedule at least one session.");
        }

        return sessionsAlreadyBooked + additional > SessionsPlanned
            ? Error.Rule("physio.plan_exceeded", $"The plan allows {SessionsPlanned} session(s) and {sessionsAlreadyBooked} are already booked. Revise the plan to add more.")
            : Result.Success();
    }
}
