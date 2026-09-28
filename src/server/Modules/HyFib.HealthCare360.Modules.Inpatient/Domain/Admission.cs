namespace HyFib.HealthCare360.Modules.Inpatient.Domain;

public enum AdmissionType
{
    Elective = 0,
    Emergency,
    DayCare,
    Maternity,
    TransferIn,
}

/// <summary>Where the admission came from (OPD advice, emergency department, external referral or direct walk-in).</summary>
public enum AdmissionSource
{
    OutPatient = 0,
    Emergency,
    Referral,
    Direct,
}

public enum AdmissionStatus
{
    Admitted = 0,
    DischargeInitiated,
    Discharged,
    Cancelled,
    Absconded,
    Expired,
    Lama,
    Dama,
}

/// <summary>
/// How the stay ended. LAMA = left against medical advice; DAMA = discharged against medical advice (on the family's
/// request, with a signed form); Referred = transferred out to another hospital.
/// </summary>
public enum DischargeType
{
    Normal = 0,
    Referred,
    Lama,
    Dama,
    Absconded,
    Expired,
}

/// <summary>
/// An in-patient stay (ADT). It is anchored to an in-patient <c>Visit</c> so orders, charges and bills of the stay share
/// one visit id, and its bed history lives in <see cref="BedAssignment"/>. State changes raise the platform integration
/// events Billing, Pharmacy and Dietary rely on (PatientAdmitted, PatientTransferred, DischargeInitiated,
/// PatientDischarged); the events are dispatched before commit, so a failing handler rolls the whole change back.
/// </summary>
public sealed class Admission : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string AdmissionNumber { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public Guid VisitId { get; set; }

    /// <summary>OPD/ER visit the admission was advised from (null for direct admissions).</summary>
    public Guid? SourceVisitId { get; set; }

    public AdmissionType Type { get; set; }
    public AdmissionSource Source { get; set; }
    public AdmissionStatus Status { get; set; }
    public DateTimeOffset AdmittedAt { get; set; }
    public Guid? AdmittingPractitionerId { get; set; }
    public Guid? ConsultantPractitionerId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? WardId { get; set; }
    public Guid? BedId { get; set; }
    public string? ProvisionalDiagnosis { get; set; }
    public string? ReferredBy { get; set; }
    public PayerType PayerType { get; set; }
    public Guid? PayerId { get; set; }

    /// <summary>Insurance policy / TPA card number, AES-GCM encrypted: it is an identifier and is never logged in clear.</summary>
    [SensitiveData]
    public string? PolicyNumberCipher { get; set; }

    public string? PolicyNumberMasked { get; set; }

    /// <summary>Medico-legal case (police intimation required); MLC records are retained permanently.</summary>
    public bool IsMlc { get; set; }

    public string? MlcNumber { get; set; }
    public DateOnly? ExpectedDischargeDate { get; set; }
    public string? AttendantName { get; set; }
    public string? AttendantRelation { get; set; }
    public string? AttendantPhone { get; set; }
    public string? Notes { get; set; }

    public DateTimeOffset? DischargeInitiatedAt { get; set; }
    public Guid? DischargeInitiatedBy { get; set; }
    public DischargeType? DischargeType { get; set; }
    public DateTimeOffset? DischargedAt { get; set; }
    public Guid? DischargedBy { get; set; }
    public string? DischargeNotes { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }

    /// <summary>The patient still occupies a bed (admitted or discharge in progress).</summary>
    public bool IsActive => Status is AdmissionStatus.Admitted or AdmissionStatus.DischargeInitiated;

    /// <summary>Call once numbers are allocated and the bed is occupied: Billing opens the IP account from this event.</summary>
    public void MarkAdmitted(Guid? bedServiceItemId) =>
        Raise(new PatientAdmitted(Id, AdmissionNumber, PatientId, VisitId, FacilityId, WardId, BedId, bedServiceItemId,
            AdmittingPractitionerId, PayerType, AdmittedAt));

    public Result TransferTo(Guid wardId, Guid bedId, Guid? toBedServiceItemId, DateTimeOffset at)
    {
        if (Status != AdmissionStatus.Admitted)
        {
            return Error.Rule("admission.not_transferable",
                Status == AdmissionStatus.DischargeInitiated
                    ? "Discharge is in progress. Revoke the discharge before transferring the patient."
                    : "This admission is closed.");
        }

        if (BedId == bedId)
        {
            return Error.Rule("admission.same_bed", "The patient is already in this bed.");
        }

        if (at < AdmittedAt)
        {
            return Error.Rule("admission.transfer_before_admission", "The transfer time cannot be before the admission time.");
        }

        var fromBedId = BedId;
        WardId = wardId;
        BedId = bedId;
        Raise(new PatientTransferred(Id, PatientId, fromBedId, bedId, toBedServiceItemId, at, wardId));
        return Result.Success();
    }

    public Result InitiateDischarge(Guid? userId, DateOnly? expectedDate, DateTimeOffset now)
    {
        if (Status != AdmissionStatus.Admitted)
        {
            return Error.Rule("discharge.not_admitted",
                Status == AdmissionStatus.DischargeInitiated ? "Discharge has already been initiated." : "This admission is closed.");
        }

        Status = AdmissionStatus.DischargeInitiated;
        DischargeInitiatedAt = now;
        DischargeInitiatedBy = userId;
        ExpectedDischargeDate = expectedDate ?? ExpectedDischargeDate;
        Raise(new DischargeInitiated(Id, PatientId, now));
        return Result.Success();
    }

    /// <summary>Clinical condition changed after discharge was initiated: the stay continues.</summary>
    public Result RevokeDischarge()
    {
        if (Status != AdmissionStatus.DischargeInitiated)
        {
            return Error.Rule("discharge.not_initiated", "Discharge has not been initiated for this admission.");
        }

        Status = AdmissionStatus.Admitted;
        DischargeInitiatedAt = null;
        DischargeInitiatedBy = null;
        return Result.Success();
    }

    public Result CompleteDischarge(DischargeType type, Guid? userId, string? notes, DateTimeOffset at)
    {
        if (!IsActive)
        {
            return Error.Rule("admission.closed", "This admission is already closed.");
        }

        if (Status == AdmissionStatus.Admitted && DischargeRules.RequiresInitiation(type))
        {
            return Error.Rule("discharge.not_initiated", "Initiate the discharge first so billing can prepare the final bill.");
        }

        if (at < AdmittedAt)
        {
            return Error.Rule("discharge.before_admission", "The discharge time cannot be before the admission time.");
        }

        Status = DischargeRules.StatusFor(type);
        DischargeType = type;
        DischargedAt = at;
        DischargedBy = userId;
        DischargeNotes = string.IsNullOrWhiteSpace(notes) ? DischargeNotes : notes.Trim();
        DischargeInitiatedAt ??= at;
        DischargeInitiatedBy ??= userId;
        Raise(new PatientDischarged(Id, PatientId, VisitId, type.ToString(), at));
        return Result.Success();
    }

    /// <summary>Admission created in error or the patient never arrived. Discharge-in-progress admissions must be revoked first.</summary>
    public Result Cancel(string reason, DateTimeOffset now)
    {
        if (Status != AdmissionStatus.Admitted)
        {
            return Error.Rule("admission.not_cancellable", "Only an admission that is in progress (not in discharge or closed) can be cancelled.");
        }

        Status = AdmissionStatus.Cancelled;
        CancelledAt = now;
        CancelReason = reason.Trim();
        Raise(new AdmissionCancelled(Id, PatientId, VisitId, FacilityId, CancelReason, now));
        return Result.Success();
    }

    /// <summary>Nursing documentation stays open until 24 hours after discharge; later notes need the MRD late-entry process.</summary>
    public bool AcceptsCharting(DateTimeOffset now) => ChartingRules.IsOpen(Status, DischargedAt, now);
}

/// <summary>When nursing documentation may still be written against an admission.</summary>
public static class ChartingRules
{
    public static readonly TimeSpan LateEntryWindow = TimeSpan.FromHours(24);

    public static bool IsOpen(AdmissionStatus status, DateTimeOffset? dischargedAt, DateTimeOffset now) =>
        status is AdmissionStatus.Admitted or AdmissionStatus.DischargeInitiated
        || (status != AdmissionStatus.Cancelled && dischargedAt is { } at && now - at <= LateEntryWindow);
}
