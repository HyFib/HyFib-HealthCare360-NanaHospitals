using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.BloodBank;
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
    /// Records the blood bank's grouping of the request sample, compared with this request's first sample (a disagreement
    /// is refused) and with the patient's earlier confirmed groupings on other requests, in any facility (a disagreement is
    /// recorded and holds the request: audited at Warning, and the officers are told). A new sample
    /// (<see cref="GroupPatientRequest.NewSample"/>) for a held request is grouped by another officer and replaces the
    /// first.
    /// </summary>
    public async Task<Result> GroupPatientAsync(Guid requestId, GroupPatientRequest request, CancellationToken ct)
    {
        var bloodRequest = await db.Set<BloodRequest>().FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (bloodRequest is null)
        {
            return Error.NotFound(nameof(BloodRequest), requestId);
        }

        var by = currentUser.Stamp(time.GetUtcNow());
        var wasHeld = bloodRequest.GroupingHeld;
        Result result;
        if (request.NewSample)
        {
            var heldGroup = bloodRequest.PatientBloodGroup;
            result = bloodRequest.Regroup(request.BloodGroup, by);
            if (result.IsSuccess)
            {
                audit.Add(RequestAudit(bloodRequest, "bloodbank.regrouped", bloodRequest.PatientBloodGroup == bloodRequest.EarlierGroup
                    ? $"New sample for {bloodRequest.RequestNumber} grouped {bloodRequest.PatientBloodGroup} by {by.Name}, as on the patient's earlier requests; the first sample ({heldGroup}) is set aside"
                    : $"New sample for {bloodRequest.RequestNumber} grouped {bloodRequest.PatientBloodGroup} by {by.Name}, as the first sample, against {bloodRequest.EarlierGroup} on the patient's earlier requests: investigate the earlier grouping",
                    AuditSeverity.Warning));
            }
        }
        else
        {
            var earlier = await EarlierGroupsAsync(bloodRequest, ct);
            result = bloodRequest.RecordGrouping(request.BloodGroup, by, GroupingHistory.Disagreement(request.BloodGroup, earlier));
        }

        if (result.IsFailure)
        {
            return result;
        }

        var held = !wasHeld && bloodRequest.GroupingHeld;
        if (held)
        {
            audit.Add(RequestAudit(bloodRequest, "bloodbank.grouping_history_discrepancy",
                $"Sample for {bloodRequest.RequestNumber} grouped {bloodRequest.PatientBloodGroup} by {by.Name}; the patient's earlier requests were grouped {bloodRequest.EarlierGroup}. Held for a new sample",
                AuditSeverity.Warning));
        }

        await db.SaveChangesAsync(ct);
        if (held)
        {
            await notifications.NotifyPermissionHoldersAsync(SpecialtyCarePermissions.BloodBankCrossmatch, bloodRequest.FacilityId,
                "Blood group differs from earlier requests",
                $"{bloodRequest.RequestNumber}: the sample groups differently from the patient's earlier requests. Collect a new sample; another officer groups it.",
                $"/blood-bank/requests/{bloodRequest.Id}", "bloodbank", "warning", ct);
        }

        await PublishAsync(bloodRequest.FacilityId, ct);
        return Result.Success();
    }

    /// <summary>
    /// The treating doctor authorises uncross-matched release from their own sign-in: a holder of
    /// <c>bloodbank.requests.create</c> whose account is linked to a doctor in the practitioner list, never a name typed by
    /// the blood bank (403 <c>bloodbank.doctor_required</c> otherwise). Their account, practitioner record and the time are
    /// recorded, audited at Warning, and the blood bank is told it may issue.
    /// </summary>
    public async Task<Result> AuthoriseEmergencyReleaseAsync(Guid requestId, EmergencyReleaseRequest request, CancellationToken ct)
    {
        var doctor = currentUser.PractitionerId is { } practitionerId
            ? await db.Set<Practitioner>().AsNoTracking()
                .Where(p => p.Id == practitionerId && p.IsActive && Doctors.Contains(p.Kind))
                .Select(p => new { p.Id, p.DisplayName, p.RegistrationNumber })
                .FirstOrDefaultAsync(ct)
            : null;
        if (currentUser.UserId is null || doctor is null)
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

        var registration = string.IsNullOrWhiteSpace(doctor.RegistrationNumber) ? string.Empty : $" (registration {doctor.RegistrationNumber})";
        audit.Add(RequestAudit(bloodRequest, "bloodbank.emergency_release_authorised",
            $"Uncross-matched release for {bloodRequest.RequestNumber} authorised by {doctor.DisplayName}{registration}, signed in as themselves: {bloodRequest.EmergencyReleaseReason}",
            AuditSeverity.Warning));
        await db.SaveChangesAsync(ct);
        await notifications.NotifyPermissionHoldersAsync(SpecialtyCarePermissions.BloodBankIssue, bloodRequest.FacilityId,
            "Uncross-matched release authorised", $"{bloodRequest.RequestNumber}: {doctor.DisplayName} authorised uncross-matched release.",
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

        var open = bloodRequest.EnsureOpen();
        var unit = await db.Set<BloodComponent>().FirstOrDefaultAsync(c => c.Id == request.ComponentId, ct);
        if (open.IsFailure)
        {
            return open.Error!;
        }

        if (bloodRequest.GroupingHeld)
        {
            return GroupingHeldError(bloodRequest);
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

        // A grouping held against the patient's earlier groups is not relied on: only what an ungrouped patient may have
        // under the doctor's emergency release (O Rh(D)-negative red cells, AB plasma) goes before the new sample is grouped.
        var held = bloodRequest.GroupingHeld;
        if (held && !bloodRequest.EmergencyReleaseAuthorised)
        {
            return GroupingHeldError(bloodRequest);
        }

        var now = time.GetUtcNow();
        var crossMatches = await db.Set<CrossMatch>().AsNoTracking()
            .Where(c => c.RequestId == requestId && c.ComponentId == unit.Id)
            .OrderByDescending(c => c.PerformedAt)
            .ToListAsync(ct);
        var latest = crossMatches.FirstOrDefault();
        var validCrossMatch = !held && latest is not null && latest.IsValidCompatibleAt(now) ? latest : null;

        var compatibility = IssueCompatibility.Check(new IssueContext(
            unit.ComponentType, unit.BloodGroup, held ? BloodGroup.Unknown : bloodRequest.PatientBloodGroup, validCrossMatch is not null,
            bloodRequest.EmergencyReleaseAuthorised));
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
                bloodRequest.EmergencyReleaseByUserId))
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

    /// <summary>Returns every unit reserved for the request to general stock.</summary>
    internal async Task ReleaseReservationsAsync(Guid requestId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var reserved = await db.Set<BloodComponent>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(c => c.ReservedForRequestId == requestId && c.Status == BloodUnitStatus.Reserved)
            .ToListAsync(ct);
        reserved.ForEach(c => c.ReleaseReservation(now));
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
    /// The groups the blood bank confirmed on the patient's other requests, most recent first, in every facility (a
    /// patient's group does not change with the branch): a request held for a disagreement and not regrouped confirms
    /// nothing.
    /// </summary>
    private Task<List<BloodGroup>> EarlierGroupsAsync(BloodRequest bloodRequest, CancellationToken ct) =>
        db.Set<BloodRequest>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(r => r.PatientId == bloodRequest.PatientId && r.Id != bloodRequest.Id && r.GroupedAt != null && r.PatientBloodGroup != BloodGroup.Unknown
                        && (r.EarlierGroup == null || r.RegroupedAt != null))
            .OrderByDescending(r => r.GroupedAt)
            .Select(r => r.PatientBloodGroup)
            .Take(20)
            .ToListAsync(ct);

    internal static Error GroupingHeldError(BloodRequest bloodRequest) =>
        Error.Rule("bloodbank.grouping_history_discrepancy",
            $"This request's sample grouped {bloodRequest.PatientBloodGroup}, but the patient's earlier requests were grouped {bloodRequest.EarlierGroup}. Collect a new sample: another officer groups it before anything is cross-matched or issued.");

    private static AuditEntry RequestAudit(BloodRequest bloodRequest, string action, string summary, AuditSeverity severity) =>
        new(AuditCategories.Clinical, action, summary)
        {
            PatientId = bloodRequest.PatientId,
            EntityType = nameof(BloodRequest),
            EntityId = bloodRequest.Id.ToString(),
            Severity = severity,
        };

    private Task PublishAsync(Guid facilityId, CancellationToken ct) =>
        realtime.FacilityChangedAsync(facilityId, SpecialtyRealtime.BloodBankChanged, new { area = "requests" }, ct);
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
