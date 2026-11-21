using System.Globalization;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.BloodBank;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Realtime;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

public static class BloodBankServiceCodes
{
    public const string CrossMatch = "BB-CROSSMATCH";

    public static string For(BloodComponentType component) => component switch
    {
        BloodComponentType.WholeBlood => "BB-WB",
        BloodComponentType.PRBC => "BB-PRBC",
        BloodComponentType.FFP => "BB-FFP",
        BloodComponentType.PlateletConcentrate => "BB-PLT",
        _ => "BB-CRYO",
    };

    public static BloodComponentType? ComponentFor(string serviceCode) => serviceCode.ToUpperInvariant() switch
    {
        "BB-WB" => BloodComponentType.WholeBlood,
        "BB-PRBC" => BloodComponentType.PRBC,
        "BB-FFP" => BloodComponentType.FFP,
        "BB-PLT" => BloodComponentType.PlateletConcentrate,
        "BB-CRYO" => BloodComponentType.Cryoprecipitate,
        _ => null,
    };
}

/// <summary>
/// Patient side of the blood bank (BB-06/07): requests, blood-bank grouping of the patient sample (compared with the
/// patient's earlier requests), cross-match and reservation, issue through the <see cref="IssueCompatibility"/> gate and
/// the second-officer rule (<see cref="IssueSeparation"/>), the doctor's own authorisation of uncross-matched release,
/// returns under the cold-chain rule, and transfusion reaction reporting. Charges: cross-match when performed, component
/// when issued (withdrawn if the unit comes back in time and is restocked).
/// </summary>
public sealed class TransfusionService(
    HospitalDbContext db,
    INumberSeries numbers,
    SpecialtyChargeService charges,
    INotificationService notifications,
    IAuditLogger audit,
    IRealtimePublisher realtime,
    ICurrentUser currentUser,
    ISettingsService settings,
    TimeProvider time,
    IIncidentRegister incidents)
{
    private static readonly PractitionerKind[] Doctors = [.. PractitionerKinds.Doctors];

    public async Task<Result<BloodRequest>> CreateRequestAsync(CreateBloodRequestRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var patient = await SpecialtyLookups.EnsurePatientAsync(db, request.PatientId, ct);
        if (patient.IsFailure)
        {
            return patient.Error!;
        }

        var (admissionId, visitId) = await SpecialtyLookups.EncounterAsync(db, request.PatientId, request.AdmissionId, request.VisitId, ct);
        var bloodRequest = new BloodRequest
        {
            FacilityId = facilityId,
            PatientId = request.PatientId,
            AdmissionId = admissionId,
            VisitId = visitId,
            ComponentType = request.ComponentType,
            UnitsRequested = request.Units,
            Urgency = request.Urgency,
            Indication = request.Indication.Trim(),
            RequiredBy = request.RequiredBy,
            RequestedByPractitionerId = currentUser.PractitionerId,
            RequestedByName = currentUser.Stamp(time.GetUtcNow()).Name,
            HaemoglobinGdl = request.HaemoglobinGdl,
            PlateletCount = request.PlateletCount,
            Status = BloodRequestStatus.Pending,
        };

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        bloodRequest.RequestNumber = await numbers.NextAsync(SpecialtyCareNumberSeries.BloodRequest, facilityId, ct);
        db.Add(bloodRequest);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        if (bloodRequest.Urgency == BloodRequestUrgency.Emergency)
        {
            await notifications.NotifyPermissionHoldersAsync(SpecialtyCarePermissions.BloodBankIssue, facilityId, "Emergency blood request",
                $"{bloodRequest.RequestNumber}: {bloodRequest.UnitsRequested} unit(s) of {bloodRequest.ComponentType}.",
                $"/blood-bank/requests/{bloodRequest.Id}", "bloodbank", "critical", ct);
        }

        await PublishAsync(facilityId, ct);
        return bloodRequest;
    }

    /// <summary>
    /// Records the blood bank's grouping of the request sample, with when it was drawn and who drew it, compared with this
    /// request's first sample (a disagreement is refused) and with the patient's earlier confirmed groupings on other
    /// requests, in any facility (a disagreement is recorded and holds the request: audited at Warning, and the officers are
    /// told). With none on another sample and the two-sample rule in force (<see cref="BloodBankSettings.TwoSampleRule"/>),
    /// a grouping for red cells or plasma waits for a second sample, and the requesting doctor is told. A new sample
    /// (<see cref="GroupPatientRequest.NewSample"/>), drawn separately, is grouped by another officer and replaces the first
    /// of a held request, or confirms the first of one waiting for a second sample. A later sample that disagrees with the
    /// first of one waiting for a second sample is refused, and recorded: the request is held even on a doctor's
    /// authorisation of blood on one sample (<see cref="HoldAsync"/>).
    /// <para>
    /// Every sample grouped, whether its grouping is taken or refused as a discrepancy, is also compared with the patient's
    /// other requests resting on one sample (<see cref="HoldOthersAsync"/>), and a first grouping resting on one sample with
    /// the samples grouped on the patient's other requests: a disagreement either way is recorded on the request resting on
    /// one sample and holds it. The patient's groupings are saved one at a time (<see cref="BloodGroupingLocks"/>), so two
    /// first groupings saved at the same moment see each other. That holds for a sample refused as a new one on a request that
    /// waits for nothing (<c>bloodbank.regroup_not_needed</c>) too, and a refused sample that disagrees with the request's
    /// group is audited at Warning (<c>bloodbank.grouping_discrepancy_refused</c>) when nothing else records it. While the
    /// facility has a second officer, a group the same officer found on another request confirms nothing
    /// (<see cref="GroupingHistory.NeedsSecondSample"/>).
    /// </para>
    /// </summary>
    public async Task<Result> GroupPatientAsync(Guid requestId, GroupPatientRequest request, CancellationToken ct)
    {
        var patientId = await db.Set<BloodRequest>().AsNoTracking().Where(r => r.Id == requestId).Select(r => (Guid?)r.PatientId).FirstOrDefaultAsync(ct);
        if (patientId is not { } patient)
        {
            return Error.NotFound(nameof(BloodRequest), requestId);
        }

        if (request.SampleDrawnAt is not { } drawnAt)
        {
            return Error.Field("sampleDrawnAt", "Enter when the sample was drawn, as the tube or the form says.");
        }

        // The request and the patient's others are read under the patient's lock, held to the commit.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await BloodGroupingLocks.LockAsync(db, patient, ct);
        var bloodRequest = await db.Set<BloodRequest>().FirstAsync(r => r.Id == requestId, ct);
        var by = currentUser.Stamp(time.GetUtcNow());
        var draw = new SampleDraw(drawnAt, request.SampleDrawnBy ?? string.Empty);
        var wasHeld = bloodRequest.HistoryHeld;
        var wasAwaiting = bloodRequest.AwaitingSecondSample;
        var disagreedBefore = bloodRequest.DisagreeingSampleGroup is not null;
        Result result;
        if (request.NewSample)
        {
            var first = (bloodRequest.PatientBloodGroup, bloodRequest.SampleDrawnAt, bloodRequest.SampleDrawnBy, bloodRequest.GroupedByUserId, bloodRequest.GroupedByName);
            result = bloodRequest.Regroup(request.BloodGroup, by, draw, await settings.SeparateVerifierAsync(bloodRequest.FacilityId, ct));
            if (result.IsSuccess && wasAwaiting)
            {
                var alone = first.GroupedByUserId is { } firstOfficer && firstOfficer == by.UserId;
                var disagreeing = bloodRequest.DisagreeingSampleGroup;
                audit.Add(RequestAudit(bloodRequest, "bloodbank.group_confirmed",
                    $"{(disagreeing is null ? "Second" : "Further")} sample for {bloodRequest.RequestNumber} grouped {bloodRequest.PatientBloodGroup} by {by.Name}, drawn {Instant(draw.DrawnAt)} by {bloodRequest.SampleDrawnBy}, "
                    + $"as the first, drawn {Instant(first.SampleDrawnAt)} by {first.SampleDrawnBy ?? "an unrecorded collector"} and grouped by {first.GroupedByName ?? "an unrecorded officer"}: the group is confirmed"
                    + (disagreeing is { } other
                        ? $"; a sample that grouped {other}{(bloodRequest.DisagreeingSampleRequestNumber is { } number ? $" on {number}" : string.Empty)}, drawn {Instant(bloodRequest.DisagreeingSampleDrawnAt)} by {bloodRequest.DisagreeingSampleDrawnBy ?? "an unrecorded collector"}, disagreed before it (possible wrong blood in tube): investigate"
                        : string.Empty)
                    + (alone ? "; one officer grouped both (no second officer at this facility): for the medical officer's review" : string.Empty),
                    alone || disagreeing is not null ? AuditSeverity.Warning : AuditSeverity.Info));
            }
            else if (result.IsSuccess)
            {
                audit.Add(RequestAudit(bloodRequest, "bloodbank.regrouped", bloodRequest.PatientBloodGroup == bloodRequest.EarlierGroup
                    ? $"New sample for {bloodRequest.RequestNumber} grouped {bloodRequest.PatientBloodGroup} by {by.Name}, as on the patient's earlier requests; the first sample ({first.PatientBloodGroup}) is set aside"
                    : $"New sample for {bloodRequest.RequestNumber} grouped {bloodRequest.PatientBloodGroup} by {by.Name}, as the first sample, against {bloodRequest.EarlierGroup} on the patient's earlier requests: investigate the earlier grouping",
                    AuditSeverity.Warning));
            }
        }
        else
        {
            var earlier = await EarlierGroupingsAsync(bloodRequest, ct);
            var needsSecondSample = !bloodRequest.IsGrouped
                                    && GroupingHistory.NeedsSecondSample(await settings.TwoSampleRuleAsync(bloodRequest.FacilityId, ct), draw, earlier,
                                        by.UserId, await settings.SeparateVerifierAsync(bloodRequest.FacilityId, ct));
            result = bloodRequest.RecordGrouping(request.BloodGroup, by, GroupingHistory.Disagreement(request.BloodGroup, earlier.Select(e => e.Group)),
                draw, needsSecondSample);
        }

        // Any other refusal grouped no sample: nothing is saved. A sample sent as a new one to a request that waits for nothing
        // is still compared with the patient's other requests resting on one sample.
        var refusedCode = result.IsFailure ? result.Error!.Code : null;
        if (refusedCode is not (null or "bloodbank.grouping_discrepancy" or "bloodbank.regroup_not_needed"))
        {
            return result;
        }

        var holds = new List<HoldNotice>();
        var held = false;
        var awaiting = false;
        var refusedAudited = false;
        if (result.IsFailure)
        {
            // Refused as a discrepancy, but a sample that disagrees with the one a request waiting for its second rests on
            // is recorded on it, and holds it.
            if (!disagreedBefore && bloodRequest.DisagreeingSampleGroup is not null)
            {
                holds.Add(await HoldAsync(bloodRequest, by, ct));
            }
            else if (bloodRequest.IsGrouped && request.BloodGroup != bloodRequest.PatientBloodGroup)
            {
                // A refused sample that disagrees leaves a trace even when nothing else changes: a third group on a request
                // whose samples already disagree, a new sample agreeing with neither group of a held one, or one sent to a
                // request that waits for nothing.
                audit.Add(RequestAudit(bloodRequest, "bloodbank.grouping_discrepancy_refused",
                    $"Sample for {bloodRequest.RequestNumber} grouped {request.BloodGroup} by {by.Name}, drawn {Instant(draw.DrawnAt)} by {draw.DrawnBy.Trim()}, was refused: "
                    + $"the request is grouped {bloodRequest.PatientBloodGroup} (possible wrong blood in tube): investigate",
                    AuditSeverity.Warning));
                refusedAudited = true;
            }
        }
        else
        {
            held = !wasHeld && bloodRequest.HistoryHeld;
            if (held)
            {
                audit.Add(RequestAudit(bloodRequest, "bloodbank.grouping_history_discrepancy",
                    $"Sample for {bloodRequest.RequestNumber} grouped {bloodRequest.PatientBloodGroup} by {by.Name}; the patient's earlier requests were grouped {bloodRequest.EarlierGroup}. Held for a new sample",
                    AuditSeverity.Warning));
            }

            awaiting = !wasAwaiting && bloodRequest.AwaitingSecondSample;
            if (awaiting)
            {
                audit.Add(RequestAudit(bloodRequest, "bloodbank.second_sample_required",
                    $"Sample for {bloodRequest.RequestNumber} grouped {bloodRequest.PatientBloodGroup} by {by.Name}, drawn {Instant(bloodRequest.SampleDrawnAt)} by {bloodRequest.SampleDrawnBy}; "
                    + "the patient has no group on record: a second, separately drawn sample confirms it before group-specific blood is cross-matched or issued",
                    AuditSeverity.Info));

                // A sample grouped on another request of the patient that disagrees (one the patient's record leaves out: a
                // request whose samples disagree, a grouping held against earlier requests, a sample set aside) holds this
                // one at once.
                if (GroupingHistory.Contrary(bloodRequest.PatientBloodGroup, await PatientGroupings.OtherThanAsync(db, bloodRequest, ct)) is { } contrary
                    && bloodRequest.RecordContrarySample(contrary, by.At))
                {
                    holds.Add(await HoldAsync(bloodRequest, by, ct));
                }
            }
        }

        holds.AddRange(await HoldOthersAsync(bloodRequest, new FoundGrouping(request.BloodGroup, draw.DrawnAt, draw.DrawnBy.Trim(), bloodRequest.RequestNumber), by, ct));
        if (result.IsFailure && holds.Count == 0 && !refusedAudited)
        {
            return result;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (held)
        {
            await notifications.NotifyPermissionHoldersAsync(SpecialtyCarePermissions.BloodBankCrossmatch, bloodRequest.FacilityId,
                "Blood group differs from earlier requests",
                $"{bloodRequest.RequestNumber}: the sample groups differently from the patient's earlier requests. Collect a new sample; another officer groups it.",
                $"/blood-bank/requests/{bloodRequest.Id}", "bloodbank", "warning", ct);
        }

        if (awaiting && !bloodRequest.SamplesDisagree && bloodRequest.RequestedByPractitionerId is { } requestedBy)
        {
            await NotifyDoctorAsync(requestedBy, "Second blood sample needed",
                $"{bloodRequest.RequestNumber}: the patient has no blood group on record. Send a second sample, drawn separately (another time, the collector named on the tube and the form), before group-specific blood is cross-matched or issued.",
                $"/blood-bank/requests/{bloodRequest.Id}", "warning", ct);
        }

        foreach (var hold in holds)
        {
            await NotifyHoldAsync(hold, ct);
        }

        foreach (var facility in holds.Select(h => h.FacilityId).Prepend(bloodRequest.FacilityId).Distinct())
        {
            await PublishAsync(facility, ct);
        }

        return result;
    }

    /// <summary>
    /// The disagreeing sample now recorded on <paramref name="held"/>, a request waiting for its second sample (a later
    /// sample of its own, or one grouped on another request of the patient), holds it: nothing group-specific is
    /// cross-matched or issued on it, whatever a doctor authorised on its one sample, until a sample drawn separately agrees
    /// with its first. The units reserved for it go back to stock, cross-matched as they were on a sample that may be another
    /// patient's. Audited, at Critical where a doctor had authorised blood on the one sample (units issued on it may already
    /// be on the ward). Returns what the blood bank and the requesting doctor are told once it is saved.
    /// </summary>
    private async Task<HoldNotice> HoldAsync(BloodRequest held, StaffStamp by, CancellationToken ct)
    {
        var overridden = held.SingleSampleOverridden;
        var released = await ReleaseReservationsAsync(held.Id, ct);
        if (released > 0 || held.UnitsReserved > 0)
        {
            held.OnReservationsReleased();
        }

        var sample = held.DisagreeingSampleRequestNumber is { } other
            ? $"A sample grouped on {other} came out {held.DisagreeingSampleGroup}"
            : $"A later sample for {held.RequestNumber} grouped {held.DisagreeingSampleGroup} by {by.Name}";
        // Filed under the held request's facility: it may be another branch's than the officer who grouped the sample.
        audit.Add(RequestAudit(held, "bloodbank.second_sample_discrepancy",
            $"{sample}, drawn {Instant(held.DisagreeingSampleDrawnAt)} by {held.DisagreeingSampleDrawnBy ?? "an unrecorded collector"}, "
            + $"but the one sample {held.RequestNumber} rests on grouped {held.PatientBloodGroup}, drawn {Instant(held.SampleDrawnAt)} by {held.SampleDrawnBy ?? "an unrecorded collector"}: possible wrong blood in tube. "
            + "Held: nothing group-specific is cross-matched or issued until a sample drawn separately agrees with the first"
            + (released > 0 ? $"; {released} unit(s) reserved for it went back to stock" : string.Empty)
            + (overridden ? $"; the blood on one sample {held.SingleSampleOverrideBy} authorised no longer goes, and units already issued on it may be incompatible" : string.Empty),
            overridden ? AuditSeverity.Critical : AuditSeverity.Warning) with
        { FacilityId = held.FacilityId });
        return new HoldNotice(held.Id, held.FacilityId, held.RequestNumber, held.PatientBloodGroup, held.DisagreeingSampleGroup!.Value,
            held.DisagreeingSampleRequestNumber, overridden, released, held.RequestedByPractitionerId);
    }

    /// <summary>
    /// The patient's other requests resting on one sample (waiting for their second, on a doctor's override or not; in any
    /// facility; open or fully issued, not cancelled) that <paramref name="sample"/>, just grouped for
    /// <paramref name="grouped"/>, disagrees with: it is recorded on each as its disagreeing sample, and holds it
    /// (<see cref="HoldAsync"/>). A request that already has a disagreement recorded is held already.
    /// </summary>
    private async Task<List<HoldNotice>> HoldOthersAsync(BloodRequest grouped, FoundGrouping sample, StaffStamp by, CancellationToken ct)
    {
        var others = await db.Set<BloodRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(r => r.PatientId == grouped.PatientId && r.Id != grouped.Id && r.SecondSampleRequired && r.RegroupedAt == null
                        && r.DisagreeingSampleGroup == null && r.GroupedAt != null && r.Status != BloodRequestStatus.Cancelled
                        && r.PatientBloodGroup != sample.Group)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);
        var holds = new List<HoldNotice>();
        foreach (var other in others)
        {
            if (other.RecordContrarySample(sample, by.At))
            {
                holds.Add(await HoldAsync(other, by, ct));
            }
        }

        return holds;
    }

    /// <summary>
    /// Tells the blood bank of the held request's facility, and its requesting doctor, that samples of the patient disagree:
    /// critical where blood had been authorised on the one sample, since units issued on it may be on the ward.
    /// </summary>
    private async Task NotifyHoldAsync(HoldNotice hold, CancellationToken ct)
    {
        var severity = hold.Overridden ? "critical" : "warning";
        var link = $"/blood-bank/requests/{hold.RequestId}";
        var samples = hold.FromRequest is { } other
            ? $"its one sample grouped {hold.First}, and a sample grouped on {other} {hold.Disagreeing}"
            : $"two samples grouped {hold.First} and {hold.Disagreeing}";
        await notifications.NotifyPermissionHoldersAsync(SpecialtyCarePermissions.BloodBankIssue, hold.FacilityId, "Blood samples disagree",
            $"{hold.RequestNumber}: {samples} (possible wrong blood in tube). Nothing group-specific is issued until a fresh sample agrees with the first."
            + (hold.Released > 0 ? $" {hold.Released} unit(s) reserved for it went back to stock." : string.Empty),
            link, "bloodbank", severity, ct);
        if (hold.RequestedBy is { } requestedBy)
        {
            await NotifyDoctorAsync(requestedBy, "Blood samples disagree",
                $"{hold.RequestNumber}: {samples} (possible wrong blood in tube). Send a fresh sample, drawn separately, and investigate."
                + (hold.Overridden ? " Blood issued on the first sample may be incompatible: check the patient." : string.Empty),
                link, severity, ct);
        }
    }

    /// <summary>
    /// The treating doctor authorises uncross-matched release from their own sign-in: a holder of
    /// <c>bloodbank.requests.create</c> whose account is linked to a doctor in the practitioner list, never a name typed by
    /// the blood bank (403 <c>bloodbank.doctor_required</c> otherwise). Their account, practitioner record and the time are
    /// recorded, audited at Warning, and the blood bank is told it may issue.
    /// </summary>
    public async Task<Result> AuthoriseEmergencyReleaseAsync(Guid requestId, EmergencyReleaseRequest request, CancellationToken ct)
    {
        var doctor = await SignedInDoctorAsync(ct);
        if (doctor is null)
        {
            return Error.Forbidden("bloodbank.doctor_required",
                "Uncross-matched release is authorised by the treating doctor, signed in as themselves. Your account is not linked to a doctor in the practitioner list.");
        }

        var bloodRequest = await db.Set<BloodRequest>().FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (bloodRequest is null)
        {
            return Error.NotFound(nameof(BloodRequest), requestId);
        }

        var stamp = currentUser.Stamp(time.GetUtcNow()) with { Name = doctor.DisplayName };
        var result = bloodRequest.AuthoriseEmergencyRelease(stamp, doctor.Id, request.Reason);
        if (result.IsFailure)
        {
            return result;
        }

        audit.Add(RequestAudit(bloodRequest, "bloodbank.emergency_release_authorised",
            $"Uncross-matched release for {bloodRequest.RequestNumber} authorised by {doctor.DisplayName}{doctor.Registration}, signed in as themselves: {bloodRequest.EmergencyReleaseReason}",
            AuditSeverity.Warning));
        await db.SaveChangesAsync(ct);
        await notifications.NotifyPermissionHoldersAsync(SpecialtyCarePermissions.BloodBankIssue, bloodRequest.FacilityId,
            "Uncross-matched release authorised", $"{bloodRequest.RequestNumber}: {doctor.DisplayName} authorised uncross-matched release.",
            $"/blood-bank/requests/{bloodRequest.Id}", "bloodbank", "critical", ct);
        await PublishAsync(bloodRequest.FacilityId, ct);
        return Result.Success();
    }

    /// <summary>
    /// The emergency override of the two-sample rule: the treating doctor authorises group-specific blood on the one sample an
    /// emergency request waiting for a second sample was grouped on, from their own sign-in, with the clinical reason (403
    /// <c>bloodbank.doctor_required</c> for anyone else, as for the emergency release; 422 <c>bloodbank.not_emergency</c>
    /// for a routine or urgent request). Refused while a sample grouped on another request of the patient disagrees with it,
    /// read under the patient's lock (<see cref="BloodGroupingLocks"/>) so that a grouping saved at the same moment is seen,
    /// or sees the override. Their account, practitioner record, the reason and the time are recorded, audited at Critical,
    /// and the blood bank is told it may cross-match and issue.
    /// </summary>
    public async Task<Result> AuthoriseSingleSampleOverrideAsync(Guid requestId, SingleSampleOverrideRequest request, CancellationToken ct)
    {
        var doctor = await SignedInDoctorAsync(ct);
        if (doctor is null)
        {
            return Error.Forbidden("bloodbank.doctor_required",
                "Group-specific blood on one sample is authorised by the treating doctor, signed in as themselves. Your account is not linked to a doctor in the practitioner list.");
        }

        var patientId = await db.Set<BloodRequest>().AsNoTracking().Where(r => r.Id == requestId).Select(r => (Guid?)r.PatientId).FirstOrDefaultAsync(ct);
        if (patientId is not { } patient)
        {
            return Error.NotFound(nameof(BloodRequest), requestId);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await BloodGroupingLocks.LockAsync(db, patient, ct);
        var bloodRequest = await db.Set<BloodRequest>().FirstAsync(r => r.Id == requestId, ct);
        var stamp = currentUser.Stamp(time.GetUtcNow()) with { Name = doctor.DisplayName };
        var result = bloodRequest.AuthoriseSingleSampleOverride(stamp, doctor.Id, request.Reason, await PatientGroupings.ContraryAsync(db, bloodRequest, ct));
        if (result.IsFailure)
        {
            return result;
        }

        audit.Add(RequestAudit(bloodRequest, "bloodbank.single_sample_override",
            $"Group-specific blood for {bloodRequest.RequestNumber} on one sample's grouping ({bloodRequest.PatientBloodGroup}, drawn {Instant(bloodRequest.SampleDrawnAt)} by {bloodRequest.SampleDrawnBy}), "
            + $"before a second sample confirms it, authorised by {doctor.DisplayName}{doctor.Registration}, signed in as themselves: {bloodRequest.SingleSampleOverrideReason}",
            AuditSeverity.Critical));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await notifications.NotifyPermissionHoldersAsync(SpecialtyCarePermissions.BloodBankIssue, bloodRequest.FacilityId,
            "Blood on one sample authorised",
            $"{bloodRequest.RequestNumber}: {doctor.DisplayName} authorised group-specific blood on one sample's grouping. Group the second sample when it comes.",
            $"/blood-bank/requests/{bloodRequest.Id}", "bloodbank", "critical", ct);
        await PublishAsync(bloodRequest.FacilityId, ct);
        return Result.Success();
    }

    /// <summary>Cancels the request and returns its reserved units to general stock.</summary>
    public Task<Result> CancelRequestAsync(Guid requestId, string reason, CancellationToken ct) =>
        ChangeRequestAsync(requestId, async r =>
        {
            var result = r.Cancel(reason, time.GetUtcNow());
            if (result.IsSuccess)
            {
                await ReleaseReservationsAsync(r.Id, ct);
            }

            return result;
        }, ct);

    public async Task<Result<CrossMatch>> CrossMatchAsync(Guid requestId, RecordCrossMatchRequest request, CancellationToken ct)
    {
        var bloodRequest = await db.Set<BloodRequest>().FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (bloodRequest is null)
        {
            return Error.NotFound(nameof(BloodRequest), requestId);
        }

        // Under the patient's grouping lock, held to the commit (BloodGroupingLocks), with the request read again under it, as
        // the issue does: a disagreeing sample grouped at the same moment either holds this cross-match or is grouped after the
        // unit was reserved, and neither save undoes the other.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await BloodGroupingLocks.LockAsync(db, bloodRequest.PatientId, ct);
        await db.Entry(bloodRequest).ReloadAsync(ct);

        var open = bloodRequest.EnsureOpen();
        var unit = await db.Set<BloodComponent>().FirstOrDefaultAsync(c => c.Id == request.ComponentId, ct);
        if (open.IsFailure)
        {
            return open.Error!;
        }

        var contrary = await PatientGroupings.ContraryAsync(db, bloodRequest, ct);
        if (bloodRequest.HeldGiven(contrary))
        {
            return GroupingHeldError(bloodRequest, contrary);
        }

        if (unit is null || unit.ComponentType != bloodRequest.ComponentType)
        {
            return Error.Field("componentId", $"Choose a {bloodRequest.ComponentType} unit.");
        }

        var consistent = CrossMatch.EnsureConsistent(request.Result, unit.ComponentType, unit.BloodGroup, bloodRequest.PatientBloodGroup);
        if (consistent.IsFailure)
        {
            return consistent.Error!;
        }

        var now = time.GetUtcNow();
        var alreadyReserved = unit.Status == BloodUnitStatus.Reserved && unit.ReservedForRequestId == requestId;
        if (request.Result == CrossmatchResult.Compatible)
        {
            var reserved = unit.Reserve(requestId, now);
            if (reserved.IsFailure)
            {
                return reserved.Error!;
            }

            if (!alreadyReserved)
            {
                bloodRequest.OnUnitReserved();
            }
        }
        else if (alreadyReserved)
        {
            unit.ReleaseReservation(now);
            bloodRequest.OnReservationReleased();
        }

        var stamp = currentUser.Stamp(now);
        var crossMatch = new CrossMatch
        {
            FacilityId = bloodRequest.FacilityId,
            RequestId = requestId,
            ComponentId = unit.Id,
            PatientId = bloodRequest.PatientId,
            UnitNumber = unit.UnitNumber,
            Method = request.Method,
            Result = request.Result,
            PerformedAt = now,
            PerformedByUserId = stamp.UserId,
            PerformedByName = stamp.Name,
            Remarks = string.IsNullOrWhiteSpace(request.Remarks) ? null : request.Remarks.Trim(),
        };
        db.Add(crossMatch);

        var outcome = await charges.PostAsync(
        [
            new ChargeLine(ChargeSources.BloodBank, $"crossmatch:{crossMatch.Id}", bloodRequest.PatientId, bloodRequest.VisitId, bloodRequest.AdmissionId,
                BloodBankServiceCodes.CrossMatch, Description: $"Cross-match {unit.UnitNumber}", ServiceDate: now),
        ], ct);
        crossMatch.RecordCharges(outcome.Status, outcome.Error);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await PublishAsync(bloodRequest.FacilityId, ct);
        return crossMatch;
    }

    /// <summary>Hands a unit over for the patient after the final server-side compatibility and safety checks.</summary>
    public async Task<Result<BloodIssue>> IssueAsync(Guid requestId, IssueBloodRequest request, CancellationToken ct)
    {
        var bloodRequest = await db.Set<BloodRequest>().FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (bloodRequest is null)
        {
            return Error.NotFound(nameof(BloodRequest), requestId);
        }

        // Under the patient's grouping lock, held to the commit (BloodGroupingLocks), and with the request read again under it:
        // a disagreeing sample grouped at the same moment is either read here, holding the issue, or grouped after the unit
        // went out, and neither save undoes the other.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await BloodGroupingLocks.LockAsync(db, bloodRequest.PatientId, ct);
        await db.Entry(bloodRequest).ReloadAsync(ct);

        var open = bloodRequest.EnsureOpen();
        if (open.IsFailure)
        {
            return open.Error!;
        }

        var unit = await db.Set<BloodComponent>().FirstOrDefaultAsync(c => c.Id == request.ComponentId, ct);
        if (unit is null || unit.ComponentType != bloodRequest.ComponentType)
        {
            return Error.Field("componentId", $"Scan a {bloodRequest.ComponentType} unit for this request.");
        }

        // A grouping held against the patient's earlier groups, one that waits for a second sample with no doctor's override, or
        // one resting on one sample that another sample of the patient disagrees with, is not relied on: only what an
        // ungrouped patient may have under the doctor's emergency release (O Rh(D)-negative red cells, AB plasma) goes before
        // the new sample is grouped.
        var contrary = await PatientGroupings.ContraryAsync(db, bloodRequest, ct);
        var held = bloodRequest.HeldGiven(contrary);
        if (held && !bloodRequest.EmergencyReleaseAuthorised)
        {
            return GroupingHeldError(bloodRequest, contrary);
        }

        var now = time.GetUtcNow();
        var crossMatches = await db.Set<CrossMatch>().AsNoTracking()
            .Where(c => c.RequestId == requestId && c.ComponentId == unit.Id)
            .OrderByDescending(c => c.PerformedAt)
            .ToListAsync(ct);
        var latest = crossMatches.FirstOrDefault();
        var validCrossMatch = !held && latest is not null && latest.IsValidCompatibleAt(now) ? latest : null;

        var compatibility = IssueCompatibility.Check(new IssueContext(
            unit.ComponentType, unit.BloodGroup, bloodRequest.GroupForIssueGiven(contrary), validCrossMatch is not null, bloodRequest.EmergencyReleaseAuthorised));
        if (compatibility.IsFailure)
        {
            return compatibility.Error!;
        }

        // Grouping, cross-match and issue are not all one person: refused, or where the facility has one officer, issued and
        // listed for the medical officer's review.
        var stamp = currentUser.Stamp(now);
        var ownChecks = stamp.UserId is { } issuer
            ? IssueSeparation.OwnChecksReason(issuer, new IssueChecks(
                bloodRequest.GroupedByUserId, validCrossMatch?.PerformedByUserId, validCrossMatch is not null, compatibility.Value,
                bloodRequest.EmergencyReleaseByUserId,
                bloodRequest.SingleSampleOverridden && bloodRequest.AwaitingSecondSample ? bloodRequest.SingleSampleOverrideByUserId : null))
            : null;
        if (ownChecks is not null && await settings.SeparateVerifierAsync(bloodRequest.FacilityId, ct))
        {
            return Error.Forbidden(IssueSeparation.OwnChecks, ownChecks);
        }

        var wasReserved = unit.Status == BloodUnitStatus.Reserved && unit.ReservedForRequestId == requestId;
        var issued = unit.Issue(requestId, now);
        if (issued.IsFailure)
        {
            return issued.Error!;
        }

        var issue = new BloodIssue
        {
            FacilityId = bloodRequest.FacilityId,
            RequestId = requestId,
            ComponentId = unit.Id,
            PatientId = bloodRequest.PatientId,
            UnitNumber = unit.UnitNumber,
            ComponentType = unit.ComponentType,
            UnitBloodGroup = unit.BloodGroup,
            CrossMatchId = validCrossMatch?.Id,
            IsEmergencyRelease = compatibility.Value,
            IssuedAt = now,
            IssuedByUserId = stamp.UserId,
            IssuedByName = stamp.Name,
            IssuedOnOwnChecks = ownChecks is not null,
            IssuedToName = request.IssuedToName.Trim(),
        };
        db.Add(issue);
        bloodRequest.OnUnitIssued(wasReserved);

        if (issue.IsEmergencyRelease)
        {
            audit.Add(new AuditEntry(AuditCategories.Clinical, "bloodbank.emergency_release",
                $"Uncross-matched {unit.UnitNumber} issued for {bloodRequest.RequestNumber} by {stamp.Name}, on the release {bloodRequest.EmergencyReleaseAuthorisedBy} authorised")
            {
                PatientId = bloodRequest.PatientId,
                EntityType = nameof(BloodIssue),
                EntityId = issue.Id.ToString(),
                Severity = AuditSeverity.Warning,
            });
        }

        // Group-specific blood before a second sample confirmed the group goes only on the doctor's override: audited at
        // Critical for each unit, as the override itself is.
        if (bloodRequest.AwaitingSecondSample && !issue.IsEmergencyRelease)
        {
            audit.Add(new AuditEntry(AuditCategories.Clinical, "bloodbank.single_sample_issue", Clip(
                $"{unit.ComponentType} {unit.UnitNumber} ({unit.BloodGroup}) issued for {bloodRequest.RequestNumber} by {stamp.Name} on one sample's grouping ({bloodRequest.PatientBloodGroup}), "
                + $"before a second sample confirmed it, on the override {bloodRequest.SingleSampleOverrideBy} authorised: {bloodRequest.SingleSampleOverrideReason}"))
            {
                PatientId = bloodRequest.PatientId,
                EntityType = nameof(BloodIssue),
                EntityId = issue.Id.ToString(),
                Severity = AuditSeverity.Critical,
            });
        }

        if (issue.IssuedOnOwnChecks)
        {
            var checks = issue.IsEmergencyRelease ? "authorised its uncross-matched release"
                : validCrossMatch is not null ? "grouped the patient and cross-matched the unit" : "grouped the patient";
            audit.Add(new AuditEntry(AuditCategories.Clinical, "bloodbank.issued_on_own_checks",
                $"{unit.UnitNumber} issued for {bloodRequest.RequestNumber} by {stamp.Name}, who also {checks} (no second officer at this facility): for the medical officer's review")
            {
                PatientId = bloodRequest.PatientId,
                EntityType = nameof(BloodIssue),
                EntityId = issue.Id.ToString(),
                Severity = AuditSeverity.Warning,
            });
        }

        var outcome = await charges.PostAsync(
        [
            new ChargeLine(ChargeSources.BloodBank, IssueReference(issue), bloodRequest.PatientId, bloodRequest.VisitId, bloodRequest.AdmissionId,
                BloodBankServiceCodes.For(unit.ComponentType), Description: $"{unit.ComponentType} {unit.UnitNumber} ({unit.BloodGroup})", ServiceDate: now),
        ], ct);
        issue.RecordCharges(outcome.Status, outcome.Error);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await PublishAsync(bloodRequest.FacilityId, ct);
        return issue;
    }

    /// <summary>
    /// Unit sent back unused: restocked only within the cold-chain window (charge withdrawn), otherwise discarded as
    /// wastage (the charge stands, since the unit can no longer be used for anyone).
    /// </summary>
    public async Task<Result<BloodIssue>> ReturnAsync(Guid issueId, ReturnBloodRequest request, CancellationToken ct)
    {
        var issue = await db.Set<BloodIssue>().FirstOrDefaultAsync(i => i.Id == issueId, ct);
        if (issue is null)
        {
            return Error.NotFound(nameof(BloodIssue), issueId);
        }

        // A unit started on the ward has been given, in full or in part: it is not stock again.
        if (await db.Set<Transfusion>().AnyAsync(t => t.IssueId == issueId, ct))
        {
            return Error.Rule("bloodbank.transfusion_started",
                $"Unit {issue.UnitNumber} was started on the ward and cannot be returned to stock. End the transfusion there, and report a reaction if there was one.");
        }

        var unit = await db.Set<BloodComponent>().FirstAsync(c => c.Id == issue.ComponentId, ct);
        var bloodRequest = await db.Set<BloodRequest>().FirstAsync(r => r.Id == issue.RequestId, ct);
        var now = time.GetUtcNow();
        var returned = unit.Return(issue.IssuedAt, now, request.TemperatureMaintained);
        if (returned.IsFailure)
        {
            return returned.Error!;
        }

        var restocked = unit.Status == BloodUnitStatus.Available;
        var notes = restocked ? request.Notes : string.Join(" ", new[] { unit.DiscardReason, request.Notes }.Where(n => !string.IsNullOrWhiteSpace(n)));
        var marked = issue.MarkReturned(now, restocked, notes);
        if (marked.IsFailure)
        {
            return marked.Error!;
        }

        bloodRequest.OnUnitReturned();
        if (restocked)
        {
            var cancelled = await charges.CancelAsync(ChargeSources.BloodBank, IssueReference(issue), "Unit returned unused within 30 minutes", ct);
            issue.RecordCharges(cancelled.Status, cancelled.Error);
        }

        await db.SaveChangesAsync(ct);
        await PublishAsync(issue.FacilityId, ct);
        return issue;
    }

    public async Task<Result<TransfusionReaction>> ReportReactionAsync(Guid issueId, ReportReactionRequest request, CancellationToken ct)
    {
        var issue = await db.Set<BloodIssue>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == issueId, ct);
        if (issue is null)
        {
            return Error.NotFound(nameof(BloodIssue), issueId);
        }

        if (request.OnsetAt < issue.IssuedAt)
        {
            return Error.Field("onsetAt", "The reaction cannot start before the unit was issued.");
        }

        var stamp = currentUser.Stamp(time.GetUtcNow());
        var reaction = new TransfusionReaction
        {
            FacilityId = issue.FacilityId,
            IssueId = issue.Id,
            RequestId = issue.RequestId,
            ComponentId = issue.ComponentId,
            PatientId = issue.PatientId,
            UnitNumber = issue.UnitNumber,
            ReportedAt = stamp.At,
            ReportedByName = stamp.Name,
            OnsetAt = request.OnsetAt,
            Symptoms = request.Symptoms.Distinct().ToList(),
            Vitals = string.IsNullOrWhiteSpace(request.Vitals) ? null : request.Vitals.Trim(),
            TransfusionStopped = request.TransfusionStopped,
            VolumeTransfusedMl = request.VolumeTransfusedMl,
            ActionsTaken = string.IsNullOrWhiteSpace(request.ActionsTaken) ? null : request.ActionsTaken.Trim(),
            Severity = request.Severity,
            Type = ReactionType.Unclassified,
            Status = ReactionStatus.Reported,
        };
        db.Add(reaction);

        // The reaction is also an incident in the Quality register (NABH COP: transfusion reactions reported and analysed), raised
        // in the same save so one is never kept without the other.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        var raised = await incidents.ReportAsync(new ReportedSafetyEvent(
            reaction.FacilityId, reaction.PatientId, SafetyEventKind.TransfusionReaction, ReactionHarm.Of(reaction.Severity), reaction.OnsetAt,
            "Where the unit was transfused",
            $"Transfusion reaction to unit {reaction.UnitNumber} ({reaction.Severity}): {string.Join(", ", reaction.Symptoms)}."
            + (reaction.TransfusionStopped ? " Transfusion stopped." : string.Empty),
            reaction.ActionsTaken, "SpecialtyCare", nameof(TransfusionReaction), reaction.Id, $"Unit {reaction.UnitNumber}"), ct);
        if (raised.IsFailure)
        {
            return raised.Error!;
        }

        await transaction.CommitAsync(ct);

        await notifications.NotifyPermissionHoldersAsync(SpecialtyCarePermissions.BloodBankReactionsInvestigate, issue.FacilityId,
            reaction.IsSerious ? "Serious transfusion reaction" : "Transfusion reaction reported",
            $"Unit {issue.UnitNumber}: {reaction.Severity} reaction reported. Investigate and send the bag and post-transfusion sample to the blood bank.",
            "/blood-bank/reactions?status=Reported", "bloodbank", reaction.IsSerious ? "critical" : "warning", ct);
        return reaction;
    }

    public Task<Result> InvestigateReactionAsync(Guid reactionId, CancellationToken ct) =>
        ChangeReactionAsync(reactionId, r => r.StartInvestigation(), ct);

    /// <summary>
    /// Closes the investigation. A close that would lower the severity or imputability in force is refused while the
    /// facility asks for the medical officer: the lowering is asked for first (<see cref="ReactionClassificationService"/>).
    /// </summary>
    public async Task<Result> CloseReactionAsync(Guid reactionId, CloseReactionRequest request, CancellationToken ct)
    {
        var reaction = await db.Set<TransfusionReaction>().FirstOrDefaultAsync(r => r.Id == reactionId, ct);
        if (reaction is null)
        {
            return Error.NotFound(nameof(TransfusionReaction), reactionId);
        }

        var lowered = ReactionDowngrade.Lowers(reaction.Severity, reaction.Imputability, request.Severity, request.Imputability);
        var name = currentUser.Stamp(time.GetUtcNow()).Name;
        if (lowered.Count > 0)
        {
            if (await settings.GetAsync<bool?>(ReactionClassificationService.DowngradeApprovalSetting, reaction.FacilityId, ct) != false)
            {
                return Error.Rule("bloodbank.reaction_lowered",
                    $"Closing this way lowers the reaction ({string.Join("; ", lowered)}), which waits for the blood bank medical officer. Record the classification with your reason first, and close once it is approved.");
            }

            reaction.MarkLowered(request.Findings, name ?? "Staff", null);
            audit.Add(new AuditEntry(AuditCategories.Data, "bloodbank.reaction_lowered",
                $"Transfusion reaction on unit {reaction.UnitNumber} lowered at closing by {name} alone (the medical officer is not required here): {string.Join("; ", lowered)}")
            {
                PatientId = reaction.PatientId,
                EntityType = nameof(TransfusionReaction),
                EntityId = reaction.Id.ToString(),
                Severity = AuditSeverity.Warning,
            });
        }

        var closed = reaction.Close(request.Type, request.Severity, request.Imputability, request.Findings, request.ReportedToHaemovigilance, name ?? "Staff", time.GetUtcNow());
        if (closed.IsFailure)
        {
            return closed;
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>Returns every unit reserved for the request to general stock, and says how many there were.</summary>
    internal async Task<int> ReleaseReservationsAsync(Guid requestId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var reserved = await db.Set<BloodComponent>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(c => c.ReservedForRequestId == requestId && c.Status == BloodUnitStatus.Reserved)
            .ToListAsync(ct);
        reserved.ForEach(c => c.ReleaseReservation(now));
        return reserved.Count;
    }

    private async Task<Result> ChangeRequestAsync(Guid requestId, Func<BloodRequest, Task<Result>> change, CancellationToken ct)
    {
        var bloodRequest = await db.Set<BloodRequest>().FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (bloodRequest is null)
        {
            return Error.NotFound(nameof(BloodRequest), requestId);
        }

        var result = await change(bloodRequest);
        if (result.IsFailure)
        {
            return result;
        }

        await db.SaveChangesAsync(ct);
        await PublishAsync(bloodRequest.FacilityId, ct);
        return Result.Success();
    }

    private async Task<Result> ChangeReactionAsync(Guid reactionId, Func<TransfusionReaction, Result> change, CancellationToken ct)
    {
        var reaction = await db.Set<TransfusionReaction>().FirstOrDefaultAsync(r => r.Id == reactionId, ct);
        if (reaction is null)
        {
            return Error.NotFound(nameof(TransfusionReaction), reactionId);
        }

        var result = change(reaction);
        if (result.IsFailure)
        {
            return result;
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    private static string IssueReference(BloodIssue issue) => $"issue:{issue.Id}";

    /// <summary>
    /// The groups the blood bank confirmed on the patient's other requests, every one of them (a patient transfused for years
    /// has many, and an old one that disagrees must not drop out of the comparison), most recent first, in every facility (a
    /// patient's group does not change with the branch), with when each sample was drawn (<see cref="GroupingHistory.OnRecord"/>:
    /// both samples of a group a second sample confirmed): a request held for a disagreement and not regrouped confirms
    /// nothing, nor does one whose samples disagree.
    /// </summary>
    private async Task<List<EarlierGrouping>> EarlierGroupingsAsync(BloodRequest bloodRequest, CancellationToken ct)
    {
        var requests = await db.Set<BloodRequest>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(r => r.PatientId == bloodRequest.PatientId && r.Id != bloodRequest.Id && r.GroupedAt != null && r.PatientBloodGroup != BloodGroup.Unknown
                        && (r.EarlierGroup == null || r.RegroupedAt != null))
            .OrderByDescending(r => r.GroupedAt)
            .Select(r => new { r.PatientBloodGroup, r.SampleDrawnAt, r.FirstSampleGroup, r.FirstSampleDrawnAt, r.RegroupedAt, r.DisagreeingSampleGroup, r.GroupedByUserId })
            .ToListAsync(ct);
        return
        [
            .. requests.SelectMany(r => GroupingHistory.OnRecord(r.PatientBloodGroup, r.SampleDrawnAt, r.FirstSampleGroup, r.FirstSampleDrawnAt,
                regrouped: r.RegroupedAt is not null, disputed: r.DisagreeingSampleGroup is not null && r.RegroupedAt is null, groupedBy: r.GroupedByUserId)),
        ];
    }

    /// <summary>The doctor signed in, by their own account linked to an active doctor in the practitioner list; null for anyone else.</summary>
    private async Task<SignedInDoctor?> SignedInDoctorAsync(CancellationToken ct)
    {
        if (currentUser.UserId is null || currentUser.PractitionerId is not { } practitionerId)
        {
            return null;
        }

        var doctor = await db.Set<Practitioner>().AsNoTracking()
            .Where(p => p.Id == practitionerId && p.IsActive && Doctors.Contains(p.Kind))
            .Select(p => new { p.Id, p.DisplayName, p.RegistrationNumber })
            .FirstOrDefaultAsync(ct);
        return doctor is null
            ? null
            : new SignedInDoctor(doctor.Id, doctor.DisplayName,
                string.IsNullOrWhiteSpace(doctor.RegistrationNumber) ? string.Empty : $" (registration {doctor.RegistrationNumber})");
    }

    /// <summary>Tells the doctor, on every staff account linked to their practitioner record.</summary>
    private async Task NotifyDoctorAsync(Guid practitionerId, string title, string body, string link, string severity, CancellationToken ct)
    {
        var users = await db.Set<User>().AsNoTracking()
            .Where(u => u.PractitionerId == practitionerId && u.IsActive && u.Type == UserType.Staff)
            .Select(u => u.Id)
            .ToListAsync(ct);
        await notifications.NotifyUsersAsync(users, title, body, link, "bloodbank", severity, ct);
    }

    /// <summary>
    /// Why nothing group-specific is cross-matched or issued on the request's grouping yet (<see cref="BloodRequest.HeldGiven"/>):
    /// it disagrees with the patient's earlier requests, a sample disagreed with the one it rests on (a later sample of its
    /// own, or one grouped on another request: recorded, or <paramref name="contrary"/>), or it waits for a second sample to
    /// confirm it.
    /// </summary>
    internal static Error GroupingHeldError(BloodRequest bloodRequest, FoundGrouping? contrary) =>
        bloodRequest.HistoryHeld
            ? Error.Rule("bloodbank.grouping_history_discrepancy",
                $"This request's sample grouped {bloodRequest.PatientBloodGroup}, but the patient's earlier requests were grouped {bloodRequest.EarlierGroup}. Collect a new sample: another officer groups it before anything is cross-matched or issued.")
            : bloodRequest.DisputedGroups(contrary).Count > 0
                ? Error.Rule("bloodbank.grouping_discrepancy",
                    $"{bloodRequest.DisagreementSummary(contrary)}: possible wrong blood in tube. Nothing group-specific is cross-matched or issued, whatever was authorised on one sample, until a fresh sample drawn separately agrees with the first. In an emergency the doctor can release O Rh(D)-negative red cells or AB plasma uncross-matched.")
                : Error.Rule("bloodbank.second_sample_required",
                    $"The patient has no blood group on record, and this request's one sample grouped {bloodRequest.PatientBloodGroup}. A second sample, drawn separately, confirms the group before group-specific blood is cross-matched or issued: another officer groups it as a new sample. In an emergency the doctor can release O Rh(D)-negative red cells or AB plasma uncross-matched, or authorise group-specific blood on this one sample.");

    /// <summary>An instant in hospital time for an audit summary.</summary>
    private static string Instant(DateTimeOffset? at) =>
        at is { } instant ? SpecialtyClock.ToLocal(instant).ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture) + " IST" : "at an unrecorded time";

    /// <summary>An audit summary within the audit trail's 1000 characters (reasons and names are long text).</summary>
    private static string Clip(string summary) => summary.Length <= 1000 ? summary : summary[..999] + "…";

    private static AuditEntry RequestAudit(BloodRequest bloodRequest, string action, string summary, AuditSeverity severity) =>
        new(AuditCategories.Clinical, action, Clip(summary))
        {
            PatientId = bloodRequest.PatientId,
            EntityType = nameof(BloodRequest),
            EntityId = bloodRequest.Id.ToString(),
            Severity = severity,
        };

    private Task PublishAsync(Guid facilityId, CancellationToken ct) =>
        realtime.FacilityChangedAsync(facilityId, SpecialtyRealtime.BloodBankChanged, new { area = "requests" }, ct);

    /// <param name="Registration">" (registration …)" for the audit trail, or empty when the practitioner list has none.</param>
    private sealed record SignedInDoctor(Guid Id, string DisplayName, string Registration);

    /// <summary>What the blood bank and the requesting doctor are told once a request held for samples that disagree is saved.</summary>
    /// <param name="First">The group of the one sample the request rests on.</param>
    /// <param name="FromRequest">The patient's other request the disagreeing sample was grouped on; none for a later sample of the request's own.</param>
    /// <param name="Released">How many units reserved for the request went back to stock.</param>
    private sealed record HoldNotice(
        Guid RequestId, Guid FacilityId, string RequestNumber, BloodGroup First, BloodGroup Disagreeing, string? FromRequest, bool Overridden, int Released,
        Guid? RequestedBy);
}

/// <summary>The harm grade (NABH incident reporting) a transfusion reaction is reported with.</summary>
internal static class ReactionHarm
{
    public static IncidentHarm Of(ReactionSeverity severity) => severity switch
    {
        ReactionSeverity.Mild => IncidentHarm.Mild,
        ReactionSeverity.Moderate => IncidentHarm.Moderate,
        ReactionSeverity.Death => IncidentHarm.Death,
        _ => IncidentHarm.Severe,
    };
}
