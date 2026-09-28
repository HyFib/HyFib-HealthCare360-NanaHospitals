namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

/// <summary>
/// A request for blood components for a patient (BB-06). The patient's ABO/Rh group used for compatibility is the
/// one determined by the blood bank on the request sample — never the self-reported group from registration.
/// </summary>
public sealed class BloodRequest : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string RequestNumber { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public Guid? AdmissionId { get; set; }
    public Guid? VisitId { get; set; }

    /// <summary>EMR order line that created the request (unique; makes the order handler idempotent).</summary>
    public Guid? OrderItemId { get; set; }

    public BloodComponentType ComponentType { get; set; }
    public int UnitsRequested { get; set; }
    public BloodRequestUrgency Urgency { get; set; }
    public string Indication { get; set; } = string.Empty;
    public DateTimeOffset? RequiredBy { get; set; }
    public Guid? RequestedByPractitionerId { get; set; }
    public string? RequestedByName { get; set; }

    /// <summary>Pre-transfusion haemoglobin / platelet count, kept for transfusion-appropriateness audits.</summary>
    public decimal? HaemoglobinGdl { get; set; }

    public int? PlateletCount { get; set; }

    public BloodGroup PatientBloodGroup { get; set; }
    public DateTimeOffset? SampleReceivedAt { get; set; }
    public DateTimeOffset? GroupedAt { get; set; }
    public string? GroupedByName { get; set; }

    public string? EmergencyReleaseAuthorisedBy { get; set; }
    public string? EmergencyReleaseReason { get; set; }
    public DateTimeOffset? EmergencyReleaseAt { get; set; }

    public BloodRequestStatus Status { get; set; }
    public int UnitsReserved { get; set; }
    public int UnitsIssued { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    public bool IsOpen => Status is not (BloodRequestStatus.Issued or BloodRequestStatus.Cancelled);

    public bool IsGrouped => GroupedAt is not null && BloodCompatibility.IsKnown(PatientBloodGroup);

    public bool EmergencyReleaseAuthorised => EmergencyReleaseAt is not null;

    public Result EnsureOpen() =>
        IsOpen ? Result.Success() : Error.Rule("bloodbank.request_closed", "This blood request is closed.");

    /// <summary>
    /// Records the blood bank's grouping of the patient sample. A second grouping that disagrees with the first
    /// signals a wrong-blood-in-tube error and is refused so it can be investigated.
    /// </summary>
    public Result RecordGrouping(BloodGroup group, string groupedBy, DateTimeOffset now)
    {
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open;
        }

        if (!BloodCompatibility.IsKnown(group))
        {
            return Error.Field("bloodGroup", "Enter the patient's ABO and Rh(D) group.");
        }

        if (IsGrouped && PatientBloodGroup != group)
        {
            return Error.Rule("bloodbank.grouping_discrepancy",
                $"This sample groups as {group} but the patient was grouped as {PatientBloodGroup}. Collect a fresh sample and investigate (possible wrong blood in tube).");
        }

        PatientBloodGroup = group;
        SampleReceivedAt ??= now;
        GroupedAt ??= now;
        GroupedByName ??= groupedBy;
        Refresh();
        return Result.Success();
    }

    /// <summary>Uncross-matched emergency release is a doctor's documented decision, only for emergency requests.</summary>
    public Result AuthoriseEmergencyRelease(string doctorName, string reason, DateTimeOffset now)
    {
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open;
        }

        if (Urgency != BloodRequestUrgency.Emergency)
        {
            return Error.Rule("bloodbank.not_emergency", "Uncross-matched release is only possible for emergency requests.");
        }

        EmergencyReleaseAuthorisedBy = doctorName.Trim();
        EmergencyReleaseReason = reason.Trim();
        EmergencyReleaseAt = now;
        return Result.Success();
    }

    public void OnUnitReserved()
    {
        UnitsReserved++;
        Refresh();
    }

    public void OnReservationReleased()
    {
        UnitsReserved = Math.Max(0, UnitsReserved - 1);
        Refresh();
    }

    public void OnUnitIssued(bool wasReserved)
    {
        UnitsIssued++;
        if (wasReserved)
        {
            UnitsReserved = Math.Max(0, UnitsReserved - 1);
        }

        Refresh();
    }

    public void OnUnitReturned()
    {
        UnitsIssued = Math.Max(0, UnitsIssued - 1);
        ClosedAt = null;
        Refresh();
    }

    public Result Cancel(string reason, DateTimeOffset now)
    {
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open;
        }

        if (UnitsIssued > 0)
        {
            return Error.Rule("bloodbank.request_partly_issued", "Units have already been issued against this request; it cannot be cancelled.");
        }

        Status = BloodRequestStatus.Cancelled;
        CancelReason = reason.Trim();
        ClosedAt = now;
        UnitsReserved = 0;
        return Result.Success();
    }

    private void Refresh()
    {
        if (Status == BloodRequestStatus.Cancelled)
        {
            return;
        }

        Status = UnitsIssued >= UnitsRequested ? BloodRequestStatus.Issued
            : UnitsIssued > 0 ? BloodRequestStatus.PartiallyIssued
            : UnitsReserved >= UnitsRequested ? BloodRequestStatus.Ready
            : IsGrouped ? BloodRequestStatus.InProcess
            : BloodRequestStatus.Pending;
    }
}
