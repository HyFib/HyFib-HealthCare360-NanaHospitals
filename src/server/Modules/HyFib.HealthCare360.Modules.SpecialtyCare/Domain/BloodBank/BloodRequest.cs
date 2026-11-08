using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

/// <summary>
/// A request for blood components for a patient (BB-06). The patient's ABO/Rh group used for compatibility is the
/// one determined by the blood bank on the request sample — never the self-reported group from registration — and is
/// compared with the groups of the patient's earlier requests.
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

    /// <summary>The officer whose grouping the request uses: the first sample's, or the new sample's after a regroup.</summary>
    public Guid? GroupedByUserId { get; set; }

    public string? GroupedByName { get; set; }

    /// <summary>
    /// The group the patient's earlier requests confirmed, when this request's sample grouped otherwise: nothing is
    /// cross-matched or issued on this request's grouping until another officer groups a new sample (<see cref="Regroup"/>).
    /// </summary>
    public BloodGroup? EarlierGroup { get; set; }

    /// <summary>The group of the first sample, kept when a new sample's grouping replaced it.</summary>
    public BloodGroup? FirstSampleGroup { get; set; }

    public DateTimeOffset? RegroupedAt { get; set; }

    /// <summary>The doctor who authorised uncross-matched release, by name as the practitioner list has it.</summary>
    public string? EmergencyReleaseAuthorisedBy { get; set; }

    public string? EmergencyReleaseReason { get; set; }
    public DateTimeOffset? EmergencyReleaseAt { get; set; }

    /// <summary>The doctor's own sign-in that authorised it, and their practitioner record.</summary>
    public Guid? EmergencyReleaseByUserId { get; set; }

    public Guid? EmergencyReleaseByPractitionerId { get; set; }

    public BloodRequestStatus Status { get; set; }
    public int UnitsReserved { get; set; }
    public int UnitsIssued { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    public bool IsOpen => Status is not (BloodRequestStatus.Issued or BloodRequestStatus.Cancelled);

    public bool IsGrouped => GroupedAt is not null && BloodCompatibility.IsKnown(PatientBloodGroup);

    public bool EmergencyReleaseAuthorised => EmergencyReleaseAt is not null;

    /// <summary>The grouping disagrees with the patient's earlier groups and no new sample has been grouped yet.</summary>
    public bool GroupingHeld => EarlierGroup is not null && RegroupedAt is null;

    public Result EnsureOpen() =>
        IsOpen ? Result.Success() : Error.Rule("bloodbank.request_closed", "This blood request is closed.");

    /// <summary>
    /// Records the blood bank's grouping of the patient sample. A second grouping that disagrees with the first signals a
    /// wrong-blood-in-tube error and is refused so it can be investigated. A first grouping that disagrees with the group of
    /// the patient's earlier requests (<paramref name="earlierGroup"/>, from <see cref="GroupingHistory"/>) is recorded,
    /// and holds the request until another officer groups a new sample.
    /// </summary>
    public Result RecordGrouping(BloodGroup group, StaffStamp by, BloodGroup? earlierGroup)
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

        if (!IsGrouped)
        {
            PatientBloodGroup = group;
            SampleReceivedAt ??= by.At;
            GroupedAt = by.At;
            GroupedByUserId = by.UserId;
            GroupedByName = by.Name;
            EarlierGroup = earlierGroup is { } earlier && earlier != group ? earlier : null;
        }

        Refresh();
        return Result.Success();
    }

    /// <summary>
    /// Another officer groups a new sample of a request held because its grouping disagreed with the patient's earlier
    /// groups. The new sample decides between them: it must agree with the first sample or with the earlier group, and its
    /// grouping replaces the request's. A third group is refused as a discrepancy, and the request stays held.
    /// </summary>
    public Result Regroup(BloodGroup group, StaffStamp by)
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

        if (!GroupingHeld)
        {
            return Error.Rule("bloodbank.regroup_not_needed",
                "A new sample is grouped here only when the request's grouping disagreed with the patient's earlier requests.");
        }

        if (GroupedByUserId is { } firstOfficer && firstOfficer == by.UserId)
        {
            return Error.Forbidden("bloodbank.regroup_own_sample", "You grouped the first sample. Another blood bank officer groups the new one.");
        }

        if (group != PatientBloodGroup && group != EarlierGroup)
        {
            return Error.Rule("bloodbank.grouping_discrepancy",
                $"The new sample groups as {group}, the first as {PatientBloodGroup} and the patient's earlier requests as {EarlierGroup}. Collect another sample and investigate.");
        }

        FirstSampleGroup = PatientBloodGroup;
        PatientBloodGroup = group;
        SampleReceivedAt = by.At;
        GroupedAt = by.At;
        GroupedByUserId = by.UserId;
        GroupedByName = by.Name;
        RegroupedAt = by.At;
        Refresh();
        return Result.Success();
    }

    /// <summary>
    /// Uncross-matched emergency release is the treating doctor's decision, made from their own sign-in, only for emergency
    /// requests: <paramref name="doctor"/> is their account, <paramref name="practitionerId"/> their practitioner record.
    /// </summary>
    public Result AuthoriseEmergencyRelease(StaffStamp doctor, Guid practitionerId, string reason)
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

        if (EmergencyReleaseAuthorised)
        {
            return Error.Rule("bloodbank.release_already_authorised", $"{EmergencyReleaseAuthorisedBy ?? "A doctor"} has already authorised uncross-matched release for this request.");
        }

        EmergencyReleaseAuthorisedBy = doctor.Name;
        EmergencyReleaseByUserId = doctor.UserId;
        EmergencyReleaseByPractitionerId = practitionerId;
        EmergencyReleaseReason = reason.Trim();
        EmergencyReleaseAt = doctor.At;
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
