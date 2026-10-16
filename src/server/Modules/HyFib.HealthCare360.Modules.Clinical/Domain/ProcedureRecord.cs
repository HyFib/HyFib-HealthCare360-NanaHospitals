namespace HyFib.HealthCare360.Modules.Clinical.Domain;

/// <summary>
/// A recorded procedure is <see cref="Completed"/>; a correction leaves it <see cref="Amended"/> (with the reason, the
/// previous values are in the data-change audit) and a record made for the wrong patient or never done is
/// <see cref="EnteredInError"/>: it stays for the audit trail but leaves the timeline.
/// </summary>
public enum ProcedureStatus
{
    Completed = 0,
    Amended,
    EnteredInError,
}

/// <summary>Where the record came from: written on the chart, or from an operation completed in the theatre (OT-03).</summary>
public enum ProcedureSource
{
    Recorded = 0,
    OperationTheatre,
}

/// <summary>
/// What a clinician records about a procedure. <see cref="Code"/> is a code of the procedure catalogue (ICD-10-PCS)
/// with its <see cref="CodeSystem"/>, or null for a procedure described in words only; <see cref="Display"/> is always
/// set. <see cref="PerformerName"/> names a performer from elsewhere, who has no practitioner record here.
/// </summary>
public sealed record ProcedureDetails(
    DateTimeOffset PerformedAt,
    string? Code,
    string? CodeSystem,
    string Display,
    Guid? PerformerPractitionerId,
    string? PerformerName,
    ProcedureSide Side,
    string? BodySite,
    string? Outcome,
    string? Notes);

/// <summary>
/// A procedure done for the patient (EHR-01): bedside and minor procedures recorded on the chart, and operations
/// recorded when the theatre completes them. Kept apart from the free-text "Procedure" note and from MRD coding so the
/// longitudinal record can list, filter and correct procedures as structured data.
/// </summary>
public sealed class ProcedureRecord : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? EncounterId { get; set; }
    public Guid? AdmissionId { get; set; }
    public Guid? VisitId { get; set; }
    public DateTimeOffset PerformedAt { get; private set; }
    public string? Code { get; private set; }
    public string? CodeSystem { get; private set; }
    public string Display { get; private set; } = string.Empty;
    public Guid? PerformerPractitionerId { get; private set; }
    public string? PerformerName { get; private set; }
    public ProcedureSide Side { get; private set; }
    public string? BodySite { get; private set; }
    public string? Outcome { get; private set; }
    public string? Notes { get; private set; }
    public ProcedureSource Source { get; private set; }

    /// <summary>The theatre booking an operation was recorded from; at most one record per booking.</summary>
    public Guid? SurgeryBookingId { get; private set; }

    public Guid? RecordedByUserId { get; private set; }
    public ProcedureStatus Status { get; private set; }

    /// <summary>Why the record was last corrected, or why it was marked as entered in error.</summary>
    public string? StatusReason { get; private set; }

    public DateTimeOffset? StatusChangedAt { get; private set; }
    public Guid? StatusChangedByUserId { get; private set; }

    public bool IsEnteredInError => Status == ProcedureStatus.EnteredInError;

    public static ProcedureRecord Record(Guid patientId, ProcedureDetails details, Guid? recordedByUserId)
    {
        var record = new ProcedureRecord { PatientId = patientId, RecordedByUserId = recordedByUserId };
        record.Apply(details);
        return record;
    }

    /// <summary>The operation a theatre booking completed, recorded without a user (the theatre's sign-out is its attestation).</summary>
    public static ProcedureRecord FromSurgery(Guid patientId, Guid surgeryBookingId, ProcedureDetails details)
    {
        var record = Record(patientId, details, null);
        record.Source = ProcedureSource.OperationTheatre;
        record.SurgeryBookingId = surgeryBookingId;
        return record;
    }

    public Result Correct(ProcedureDetails details, string reason, Guid? userId, DateTimeOffset now)
    {
        if (IsEnteredInError)
        {
            return Error.Rule("procedure.entered_in_error", "This procedure is marked as entered in error and can no longer be corrected.");
        }

        Apply(details);
        Status = ProcedureStatus.Amended;
        SetStatusReason(reason, userId, now);
        return Result.Success();
    }

    public Result MarkEnteredInError(string reason, Guid? userId, DateTimeOffset now)
    {
        if (IsEnteredInError)
        {
            return Error.Rule("procedure.already_voided", "This procedure is already marked as entered in error.");
        }

        Status = ProcedureStatus.EnteredInError;
        SetStatusReason(reason, userId, now);
        return Result.Success();
    }

    private void Apply(ProcedureDetails details)
    {
        PerformedAt = details.PerformedAt;
        Code = Clean(details.Code)?.ToUpperInvariant();
        CodeSystem = Code is null ? null : Clean(details.CodeSystem);
        Display = details.Display.Trim();
        PerformerPractitionerId = details.PerformerPractitionerId;
        PerformerName = Clean(details.PerformerName);
        Side = details.Side;
        BodySite = Clean(details.BodySite);
        Outcome = Clean(details.Outcome);
        Notes = Clean(details.Notes);
    }

    private void SetStatusReason(string reason, Guid? userId, DateTimeOffset now)
    {
        StatusReason = reason.Trim();
        StatusChangedAt = now;
        StatusChangedByUserId = userId;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
