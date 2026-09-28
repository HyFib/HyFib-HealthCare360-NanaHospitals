namespace HyFib.HealthCare360.Modules.Engagement.Domain;

public enum LeadSource
{
    Website = 0,
    WalkIn,
    Phone,
    HealthCamp,
    Referral,
    SocialMedia,
    Campaign,
    Corporate,
}

public enum LeadStatus
{
    New = 0,
    Contacted,
    Qualified,
    Converted,
    Lost,
}

/// <summary>
/// A prospective patient (CRM-01): an enquiry from the website, a health camp, a referral… worked by a CRM executive
/// until it converts into a registered patient and/or an appointment, or is lost (with a reason, for reporting).
/// </summary>
public sealed class Lead : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }

    /// <summary>"{FAC}LD{YY}{SEQ:5}".</summary>
    public string LeadNumber { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>E.164 mobile number.</summary>
    public string? Phone { get; set; }

    public string? Email { get; set; }
    public Gender? Gender { get; set; }
    public int? AgeYears { get; set; }
    public string? City { get; set; }
    public LeadSource Source { get; set; }

    /// <summary>Camp name, website form, corporate client…</summary>
    public string? SourceDetail { get; set; }

    public Guid? CampaignId { get; set; }
    public Guid? ReferringDoctorId { get; set; }
    public Guid? InterestDepartmentId { get; set; }

    /// <summary>Health package or service of interest (for example "Master Health Checkup – Executive").</summary>
    public string? InterestPackage { get; set; }

    public string? Notes { get; set; }
    public LeadStatus Status { get; set; }
    public string? LostReason { get; set; }
    public Guid? OwnerUserId { get; set; }
    public DateTimeOffset? NextFollowUpAt { get; set; }
    public DateTimeOffset? LastContactedAt { get; set; }
    public Guid? ConvertedPatientId { get; set; }
    public Guid? ConvertedAppointmentId { get; set; }
    public DateTimeOffset? ConvertedAt { get; set; }

    public bool IsOpen => Status is LeadStatus.New or LeadStatus.Contacted or LeadStatus.Qualified;

    public Result MoveTo(LeadStatus target, string? lostReason)
    {
        var check = LeadWorkflow.CanMove(Status, target, lostReason);
        if (check.IsFailure)
        {
            return check;
        }

        Status = target;
        LostReason = target == LeadStatus.Lost ? lostReason!.Trim() : null;
        if (target == LeadStatus.Lost)
        {
            NextFollowUpAt = null;
        }

        return Result.Success();
    }

    /// <summary>Logs a contact; a new lead becomes "contacted" and the next follow-up is (re)scheduled.</summary>
    public void RecordContact(DateTimeOffset at, DateTimeOffset? nextFollowUpAt)
    {
        LastContactedAt = at;
        if (Status == LeadStatus.New)
        {
            Status = LeadStatus.Contacted;
        }

        if (IsOpen)
        {
            NextFollowUpAt = nextFollowUpAt;
        }
    }

    /// <summary>Links the lead to the patient record and/or appointment it produced.</summary>
    public Result Convert(Guid? patientId, Guid? appointmentId, DateTimeOffset now)
    {
        if (Status == LeadStatus.Converted)
        {
            return Error.Rule("lead.converted", "This lead has already been converted.");
        }

        if (patientId is null && appointmentId is null)
        {
            return Error.Field("patientId", "Choose the patient or the appointment this lead converted into.");
        }

        Status = LeadStatus.Converted;
        ConvertedPatientId = patientId ?? ConvertedPatientId;
        ConvertedAppointmentId = appointmentId ?? ConvertedAppointmentId;
        ConvertedAt = now;
        NextFollowUpAt = null;
        LostReason = null;
        return Result.Success();
    }
}

/// <summary>Allowed manual status changes; conversion happens only through <see cref="Lead.Convert"/>.</summary>
public static class LeadWorkflow
{
    public static Result CanMove(LeadStatus from, LeadStatus to, string? lostReason)
    {
        if (to == LeadStatus.Converted)
        {
            return Error.Rule("lead.convert_explicitly", "Convert the lead to a patient or appointment instead.");
        }

        if (from == LeadStatus.Converted)
        {
            return Error.Rule("lead.converted", "A converted lead cannot change status.");
        }

        if (from == to)
        {
            return Error.Rule("lead.same_status", $"The lead is already {to}.");
        }

        if (to == LeadStatus.Lost && string.IsNullOrWhiteSpace(lostReason))
        {
            return Error.Field("lostReason", "Record why the lead was lost.");
        }

        // A lost lead can only be revived to the start of the funnel.
        if (from == LeadStatus.Lost && to is not (LeadStatus.New or LeadStatus.Contacted))
        {
            return Error.Rule("lead.reopen", "Reopen a lost lead as New or Contacted.");
        }

        return Result.Success();
    }
}

public enum LeadActivityType
{
    Call = 0,
    Sms,
    WhatsApp,
    Email,
    Visit,
    Note,
}

/// <summary>A logged interaction with a lead (call, SMS, WhatsApp, visit or note).</summary>
public sealed class LeadActivity : AuditableEntity
{
    public Guid LeadId { get; set; }
    public LeadActivityType Type { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? Outcome { get; set; }
    public string? Notes { get; set; }
    public Guid? PerformedByUserId { get; set; }
}
