namespace HyFib.HealthCare360.Modules.Privacy.Domain;

/// <summary>The right a data principal exercises (DPDP Act 2023, sections 11 to 13).</summary>
public enum DataRequestType
{
    /// <summary>A summary of the personal data held, how it is used and who it was shared with (s11).</summary>
    Access = 0,

    /// <summary>Correct, complete or update personal data (s12(1) and (2)).</summary>
    Correction,

    /// <summary>Erase personal data, except what the law requires the hospital to keep (s12(3)).</summary>
    Erasure,

    /// <summary>A grievance about how personal data was handled (s13). It must be answered before the Board hears it.</summary>
    Grievance,
}

public enum DataRequestStatus
{
    Submitted = 0,
    InProgress,
    Completed,

    /// <summary>Done in part, for example erasure where the law requires medical records to be kept.</summary>
    PartlyCompleted,
    Declined,

    /// <summary>The principal withdrew the request.</summary>
    Withdrawn,
}

public enum DataRequestChannel
{
    Portal = 0,
    FrontDesk,
    Email,
    Letter,
    Phone,
}

/// <summary>Who made the request, relative to the data principal.</summary>
public enum RequesterRelation
{
    Self = 0,

    /// <summary>For a child or a person who cannot act for themselves (DPDP Act s2(j)).</summary>
    ParentOrGuardian,

    /// <summary>The principal's nominee, after the principal's death or incapacity (s14).</summary>
    Nominee,
    FamilyMember,
    AuthorisedRepresentative,
}

/// <summary>How the requester's identity, and their authority to act for the principal, was checked.</summary>
public enum IdentityCheck
{
    /// <summary>Signed in to the patient portal with a one-time code sent to the principal's registered mobile number.</summary>
    PortalSignIn = 0,
    PhotoId,
    KnownToStaff,
    Other,
}

public enum CorrectionDecision
{
    Pending = 0,
    Accepted,
    Declined,
}

/// <summary>One detail the principal asks to have corrected, with the value on record when they asked.</summary>
public sealed class CorrectionItem
{
    public string Field { get; set; } = string.Empty;
    public string? CurrentValue { get; set; }
    public string RequestedValue { get; set; } = string.Empty;
    public CorrectionDecision Decision { get; set; }
    public string? Note { get; set; }
}

public enum DataRequestEventKind
{
    Received = 0,
    IdentityVerified,
    Started,
    Assigned,
    Note,
    CorrectionsDecided,
    Erased,
    Closed,
    Withdrawn,
}

/// <summary>A line in a request's history. Public lines are shown to the principal in the portal; notes are not.</summary>
public sealed class DataRequestEvent : Entity
{
    public Guid DataRequestId { get; set; }
    public DateTimeOffset At { get; set; }
    public DataRequestEventKind Kind { get; set; }
    public string Text { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    public bool IsPublic { get; set; }
}

/// <summary>
/// A data principal's request under the DPDP Act 2023 (SRS PRV-01): access, correction, erasure or a grievance. It is
/// answered by a deadline the hospital publishes, at most 90 days (DPDP Rules 2025). Personal data is given out, changed
/// or erased only after the requester's identity, and their authority to act for the principal, has been checked.
/// </summary>
public sealed class DataRequest : AuditableEntity, IFacilityScoped
{
    /// <summary>The longest response period the DPDP Rules 2025 allow a hospital to publish.</summary>
    public const int MaxResponseDays = 90;

    public Guid FacilityId { get; set; }

    /// <summary>"{FAC}DPR{YY}{SEQ:5}".</summary>
    public string RequestNumber { get; set; } = string.Empty;

    public DataRequestType Type { get; set; }
    public DataRequestStatus Status { get; private set; }
    public DataRequestChannel Channel { get; set; }

    /// <summary>The data principal, when they are a registered patient.</summary>
    public Guid? PatientId { get; set; }

    public string PrincipalName { get; set; } = string.Empty;
    public string? ContactPhone { get; set; }
    public string? ContactEmail { get; set; }

    public RequesterRelation RequestedBy { get; set; }

    /// <summary>The requester's name when they are not the principal.</summary>
    public string? RequesterName { get; set; }

    /// <summary>The portal account that made the request, or the member of staff who recorded it.</summary>
    public Guid? RequestedByUserId { get; set; }

    public string Details { get; set; } = string.Empty;
    public List<CorrectionItem> Corrections { get; set; } = [];

    public DateTimeOffset ReceivedAt { get; private set; }
    public DateTimeOffset DueAt { get; private set; }

    public IdentityCheck? IdentityCheck { get; private set; }
    public string? IdentityNote { get; private set; }
    public DateTimeOffset? IdentityVerifiedAt { get; private set; }
    public Guid? IdentityVerifiedByUserId { get; private set; }

    public Guid? AssignedToUserId { get; private set; }

    /// <summary>The reply given to the principal when the request was closed.</summary>
    public string? Response { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }
    public Guid? ClosedByUserId { get; private set; }

    /// <summary>Erasure: what was erased (see <c>ErasureService</c>).</summary>
    public List<string> ErasedItems { get; set; } = [];

    /// <summary>Erasure: the date until which the rest must be kept by law; null with <see cref="RetainedPermanently"/> false when nothing is kept.</summary>
    public DateOnly? RetainedUntil { get; private set; }

    /// <summary>Erasure: some records (medico-legal) must be kept permanently.</summary>
    public bool RetainedPermanently { get; private set; }

    /// <summary>Access: the summary exactly as it was given (JSON), the record of what the principal received.</summary>
    public string? SummarySnapshot { get; private set; }

    public List<DataRequestEvent> Events { get; set; } = [];

    public bool IsOpen => Status is DataRequestStatus.Submitted or DataRequestStatus.InProgress;

    public bool IsIdentityVerified => IdentityVerifiedAt is not null;

    /// <summary>Personal data is given out, changed or erased only for a checked requester; a grievance can be answered without.</summary>
    public bool NeedsVerifiedIdentity => Type is not DataRequestType.Grievance;

    public bool IsOverdue(DateTimeOffset now) => IsOpen && now > DueAt;

    /// <summary>Starts the response clock. The deadline is the hospital's published period, capped at the legal maximum.</summary>
    public void Receive(DateTimeOffset now, int responseDays, Guid? userId, string via)
    {
        Status = DataRequestStatus.Submitted;
        ReceivedAt = now;
        DueAt = now.AddDays(Math.Clamp(responseDays, 1, MaxResponseDays));
        Log(DataRequestEventKind.Received, $"Request received {via}.", userId, now, isPublic: true);
    }

    public Result VerifyIdentity(IdentityCheck check, string? note, Guid? userId, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Closed();
        }

        IdentityCheck = check;
        IdentityNote = Clean(note);
        IdentityVerifiedAt = now;
        IdentityVerifiedByUserId = userId;
        Log(DataRequestEventKind.IdentityVerified, check == Domain.IdentityCheck.PortalSignIn
            ? "Identity confirmed by signing in with a code sent to the registered mobile number."
            : "Identity checked.", userId, now, isPublic: true);
        return Result.Success();
    }

    /// <summary>Takes the request up; whoever starts it becomes its owner if it has none.</summary>
    public Result Start(Guid? userId, DateTimeOffset now)
    {
        if (Status != DataRequestStatus.Submitted)
        {
            return Error.Rule("privacy.request_not_new", "Only a new request can be started.");
        }

        Status = DataRequestStatus.InProgress;
        AssignedToUserId ??= userId;
        Log(DataRequestEventKind.Started, "We are working on your request.", userId, now, isPublic: true);
        return Result.Success();
    }

    public Result AssignTo(Guid? assigneeId, string? assigneeName, Guid? userId, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Closed();
        }

        AssignedToUserId = assigneeId;
        Log(DataRequestEventKind.Assigned, assigneeName is null ? "Unassigned." : $"Assigned to {assigneeName}.", userId, now, isPublic: false);
        return Result.Success();
    }

    public Result AddNote(string text, Guid? userId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Error.Field("text", "Write the note.");
        }

        Log(DataRequestEventKind.Note, text.Trim(), userId, now, isPublic: false);
        return Result.Success();
    }

    /// <summary>Records the decision on each requested change (by its position in <see cref="Corrections"/>).</summary>
    public Result DecideCorrections(IReadOnlyList<(int Index, CorrectionDecision Decision, string? Note)> decisions, Guid? userId, DateTimeOffset now)
    {
        if (Type != DataRequestType.Correction)
        {
            return Error.Rule("privacy.not_correction", "Only a correction request has changes to decide.");
        }

        if (!IsOpen)
        {
            return Closed();
        }

        foreach (var (index, decision, note) in decisions)
        {
            if (index < 0 || index >= Corrections.Count)
            {
                return Error.Field("items", "One of the changes is not part of this request.");
            }

            if (decision == CorrectionDecision.Declined && string.IsNullOrWhiteSpace(note))
            {
                return Error.Field("items", $"Say why the change to {CorrectableFields.Label(Corrections[index].Field).ToLowerInvariant()} was declined.");
            }
        }

        foreach (var (index, decision, note) in decisions)
        {
            Corrections[index].Decision = decision;
            Corrections[index].Note = Clean(note);
        }

        // Reassigning the list tells change tracking the JSON column changed.
        Corrections = [.. Corrections];
        var accepted = Corrections.Count(c => c.Decision == CorrectionDecision.Accepted);
        var declined = Corrections.Count(c => c.Decision == CorrectionDecision.Declined);
        Log(DataRequestEventKind.CorrectionsDecided, $"Changes decided: {accepted} accepted, {declined} declined.", userId, now, isPublic: false);
        return Result.Success();
    }

    /// <summary>Whether data may be erased for this request now: an open erasure request from a checked requester.</summary>
    public Result CanRecordErasure()
    {
        if (Type != DataRequestType.Erasure)
        {
            return Error.Rule("privacy.not_erasure", "Only an erasure request can erase data.");
        }

        if (!IsOpen)
        {
            return Closed();
        }

        return IsIdentityVerified ? Result.Success() : Unverified();
    }

    /// <summary>Records an erasure carried out for this request, and until when the rest must be kept.</summary>
    public Result RecordErasure(IReadOnlyList<string> erased, string summary, DateOnly? retainedUntil, bool retainedPermanently, Guid? userId, DateTimeOffset now)
    {
        var allowed = CanRecordErasure();
        if (allowed.IsFailure)
        {
            return allowed;
        }

        ErasedItems = [.. ErasedItems.Union(erased, StringComparer.Ordinal)];
        RetainedUntil = retainedUntil;
        RetainedPermanently = retainedPermanently;
        Log(DataRequestEventKind.Erased, summary, userId, now, isPublic: true);
        return Result.Success();
    }

    /// <summary>
    /// Answers the request. The reply is required: it tells the principal what was done, or why not and on what legal
    /// ground. Declining is possible without a checked identity (for example when the requester could not be verified).
    /// </summary>
    public Result Close(DataRequestStatus outcome, string? response, string? summarySnapshot, Guid? userId, DateTimeOffset now)
    {
        if (outcome is not (DataRequestStatus.Completed or DataRequestStatus.PartlyCompleted or DataRequestStatus.Declined))
        {
            return Error.Field("outcome", "Choose completed, partly completed or declined.");
        }

        if (!IsOpen)
        {
            return Closed();
        }

        if (string.IsNullOrWhiteSpace(response))
        {
            return Error.Field("response", outcome == DataRequestStatus.Declined
                ? "Write the reply, with the reason the request is declined."
                : "Write the reply that tells the requester what was done.");
        }

        if (outcome != DataRequestStatus.Declined)
        {
            if (NeedsVerifiedIdentity && !IsIdentityVerified)
            {
                return Unverified();
            }

            if (Type == DataRequestType.Correction && Corrections.Any(c => c.Decision == CorrectionDecision.Pending))
            {
                return Error.Rule("privacy.corrections_pending", "Accept or decline every requested change first.");
            }

            if (Type == DataRequestType.Access && summarySnapshot is null)
            {
                return Error.Rule("privacy.summary_missing", "The data summary could not be prepared, so the request cannot be completed.");
            }
        }

        Status = outcome;
        Response = response.Trim();
        ClosedAt = now;
        ClosedByUserId = userId;
        SummarySnapshot = outcome == DataRequestStatus.Declined ? null : summarySnapshot;
        Log(DataRequestEventKind.Closed, Response, userId, now, isPublic: true);
        return Result.Success();
    }

    /// <summary>The principal (or the requester) withdraws a request that has not been answered yet.</summary>
    public Result Withdraw(Guid? userId, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Closed();
        }

        Status = DataRequestStatus.Withdrawn;
        ClosedAt = now;
        ClosedByUserId = userId;
        Log(DataRequestEventKind.Withdrawn, "Request withdrawn.", userId, now, isPublic: true);
        return Result.Success();
    }

    private void Log(DataRequestEventKind kind, string text, Guid? userId, DateTimeOffset now, bool isPublic) =>
        Events.Add(new DataRequestEvent { DataRequestId = Id, At = now, Kind = kind, Text = text, UserId = userId, IsPublic = isPublic });

    private static Error Closed() => Error.Rule("privacy.request_closed", "This request is already closed.");

    private static Error Unverified() =>
        Error.Rule("privacy.identity_unverified", "Check the requester's identity, and their authority to act for the patient, first.");

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
