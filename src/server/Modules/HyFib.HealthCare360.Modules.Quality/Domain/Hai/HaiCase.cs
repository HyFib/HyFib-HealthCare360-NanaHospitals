namespace HyFib.HealthCare360.Modules.Quality.Domain.Hai;

public enum HaiCaseStatus
{
    /// <summary>Raised from a positive culture on a patient with a device, or by the infection control team; not yet reviewed.</summary>
    Suspected = 0,

    /// <summary>Reviewed and meets the surveillance definition: counted in the rates.</summary>
    Confirmed,

    /// <summary>Reviewed and does not meet it (contaminant, present on admission, secondary, not device-associated).</summary>
    RuledOut,
}

public enum HaiCaseOrigin
{
    /// <summary>Raised by HC360 from a verified culture with an organism on a patient with the matching device or operation.</summary>
    CultureAlert = 0,
    Manual,
}

/// <summary>
/// A possible hospital-acquired infection under review by the infection control team: the patient, the device episode
/// (CLABSI, CAUTI, VAP) or the operation (SSI) it would be associated with, the culture that raised it, the date of event,
/// and the checklist of the NHSN definition. Confirmed only when the checklist meets the definition; ruled out with a reason.
/// </summary>
public sealed class HaiCase : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }

    /// <summary>"{FAC}HAI{YY}{SEQ:4}".</summary>
    public string CaseNumber { get; set; } = string.Empty;

    public HaiType Type { get; set; }
    public HaiCaseOrigin Origin { get; set; }
    public Guid PatientId { get; set; }
    public Guid? AdmissionId { get; set; }
    public Guid? DeviceEpisodeId { get; set; }

    /// <summary>The verified culture result (Diagnostics) that raised the case.</summary>
    public Guid? CultureResultId { get; set; }

    public string? Organism { get; set; }
    public string? Specimen { get; set; }
    public DateTimeOffset? SpecimenCollectedAt { get; set; }

    /// <summary>SSI: the operation (theatre booking) and the day it was done.</summary>
    public Guid? SurgeryBookingId { get; set; }

    public string? SurgeryReference { get; set; }
    public DateOnly? OperatedOn { get; set; }

    /// <summary>The date the first element of the definition was met (NHSN date of event), local calendar day.</summary>
    public DateOnly EventOn { get; set; }

    public List<string> CriteriaMet { get; set; } = [];
    public HaiCaseStatus Status { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public string? ReviewedByName { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? DecisionNote { get; set; }
    public string? Notes { get; set; }

    /// <summary>Days from the operation to the date of event, the day of the operation being day 1.</summary>
    public int? DaysAfterOperation => OperatedOn is { } operated ? EventOn.DayNumber - operated.DayNumber + 1 : null;

    public Result Confirm(HaiEvaluation evaluation, Guid userId, string? userName, string? note, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        if (Status != HaiCaseStatus.Suspected)
        {
            return Error.Rule("hai.reviewed", "This case has been reviewed. Reopen it to change the decision.");
        }

        if (!evaluation.Meets)
        {
            return Error.Rule("hai.definition_not_met", "The checklist does not meet the surveillance definition: " + string.Join("; ", evaluation.Missing) + ".");
        }

        Decide(HaiCaseStatus.Confirmed, userId, userName, note, now);
        return Result.Success();
    }

    public Result RuleOut(Guid userId, string? userName, string reason, DateTimeOffset now)
    {
        if (Status != HaiCaseStatus.Suspected)
        {
            return Error.Rule("hai.reviewed", "This case has been reviewed. Reopen it to change the decision.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Field("note", "Say why it is ruled out.");
        }

        Decide(HaiCaseStatus.RuledOut, userId, userName, reason, now);
        return Result.Success();
    }

    /// <summary>Back to suspected, with a reason, so the decision can be made again (new evidence, a mistake).</summary>
    public Result Reopen(string reason)
    {
        if (Status == HaiCaseStatus.Suspected)
        {
            return Error.Rule("hai.not_reviewed", "This case has not been reviewed yet.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Field("note", "Say why it is reopened.");
        }

        Status = HaiCaseStatus.Suspected;
        DecisionNote = $"Reopened: {reason.Trim()}";
        ReviewedAt = null;
        ReviewedByUserId = null;
        ReviewedByName = null;
        return Result.Success();
    }

    private void Decide(HaiCaseStatus status, Guid userId, string? userName, string? note, DateTimeOffset now)
    {
        Status = status;
        ReviewedByUserId = userId;
        ReviewedByName = userName;
        ReviewedAt = now;
        DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }
}
