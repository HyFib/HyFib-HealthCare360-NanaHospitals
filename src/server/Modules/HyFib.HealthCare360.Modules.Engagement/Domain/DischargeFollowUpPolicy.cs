namespace HyFib.HealthCare360.Modules.Engagement.Domain;

/// <summary>
/// What Engagement does after an in-patient discharge (CRM-02, FB-02). The discharge type arrives as text on the
/// platform event (the Inpatient module's names: Normal, Referred, Lama, Dama, Absconded, Expired).
/// <list type="bullet">
/// <item>A death never triggers a call or a survey to the family.</item>
/// <item>Patients who left against medical advice or absconded get a high-priority safety call.</item>
/// <item>No satisfaction survey goes to a patient who absconded.</item>
/// </list>
/// </summary>
public static class DischargeFollowUpPolicy
{
    /// <summary>The CRM team calls two days after discharge (SRS CRM-02).</summary>
    public const int CallAfterDays = 2;

    public static bool ShouldCall(string? dischargeType) => !Is(dischargeType, "Expired");

    public static bool ShouldRequestFeedback(string? dischargeType) => !Is(dischargeType, "Expired") && !Is(dischargeType, "Absconded");

    public static FollowUpPriority PriorityFor(string? dischargeType) =>
        Is(dischargeType, "Lama") || Is(dischargeType, "Dama") || Is(dischargeType, "Absconded") ? FollowUpPriority.High : FollowUpPriority.Normal;

    /// <summary>Due date from the hospital-local discharge date.</summary>
    public static DateOnly CallDate(DateOnly localDischargeDate) => localDischargeDate.AddDays(CallAfterDays);

    /// <summary>Idempotency key of the automatic task, so a replayed discharge event never creates a second call.</summary>
    public static string SourceReference(Guid admissionId) => $"discharge:{admissionId:N}";

    private static bool Is(string? value, string type) => string.Equals(value?.Trim(), type, StringComparison.OrdinalIgnoreCase);
}
