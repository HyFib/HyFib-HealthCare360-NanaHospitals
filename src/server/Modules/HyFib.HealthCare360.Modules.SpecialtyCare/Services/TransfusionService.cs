using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.BloodBank;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Realtime;

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
/// Patient side of the blood bank (BB-06/07): requests, blood-bank grouping of the patient sample, cross-match and
/// reservation, issue through the <see cref="IssueCompatibility"/> gate, returns under the cold-chain rule, and
/// transfusion reaction reporting. Charges: cross-match when performed, component when issued (withdrawn if the
/// unit comes back in time and is restocked).
/// </summary>
public sealed class TransfusionService(
    HospitalDbContext db,
    INumberSeries numbers,
    SpecialtyChargeService charges,
    INotificationService notifications,
    IAuditLogger audit,
    IRealtimePublisher realtime,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public const string RequestSeries = "blood-request";
    public const string RequestFormat = "{FAC}BR{YY}{SEQ:6}";

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
        bloodRequest.RequestNumber = await numbers.NextAsync(RequestSeries, facilityId, RequestFormat, NumberResetPolicy.Yearly, ct);
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

    public Task<Result> GroupPatientAsync(Guid requestId, BloodGroup group, CancellationToken ct) =>
        ChangeRequestAsync(requestId, r => Task.FromResult(r.RecordGrouping(group, currentUser.Stamp(time.GetUtcNow()).Name, time.GetUtcNow())), ct);

    public Task<Result> AuthoriseEmergencyReleaseAsync(Guid requestId, EmergencyReleaseRequest request, CancellationToken ct) =>
        ChangeRequestAsync(requestId, r =>
        {
            var result = r.AuthoriseEmergencyRelease(request.AuthorisedBy, request.Reason, time.GetUtcNow());
            if (result.IsSuccess)
            {
                audit.Add(new AuditEntry(AuditCategories.Clinical, "bloodbank.emergency_release_authorised",
                    $"Uncross-matched release authorised by {request.AuthorisedBy} for {r.RequestNumber}: {request.Reason}")
                {
                    PatientId = r.PatientId,
                    EntityType = nameof(BloodRequest),
                    EntityId = r.Id.ToString(),
                    Severity = AuditSeverity.Warning,
                });
            }

            return Task.FromResult(result);
        }, ct);

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

        var now = time.GetUtcNow();
        var crossMatches = await db.Set<CrossMatch>().AsNoTracking()
            .Where(c => c.RequestId == requestId && c.ComponentId == unit.Id)
            .OrderByDescending(c => c.PerformedAt)
            .ToListAsync(ct);
        var latest = crossMatches.FirstOrDefault();
        var validCrossMatch = latest is not null && latest.IsValidCompatibleAt(now) ? latest : null;

        var compatibility = IssueCompatibility.Check(new IssueContext(
            unit.ComponentType, unit.BloodGroup, bloodRequest.PatientBloodGroup, validCrossMatch is not null, bloodRequest.EmergencyReleaseAuthorised));
        if (compatibility.IsFailure)
        {
            return compatibility.Error!;
        }

        var wasReserved = unit.Status == BloodUnitStatus.Reserved && unit.ReservedForRequestId == requestId;
        var issued = unit.Issue(requestId, now);
        if (issued.IsFailure)
        {
            return issued.Error!;
        }

        var stamp = currentUser.Stamp(now);
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
            IssuedToName = request.IssuedToName.Trim(),
        };
        db.Add(issue);
        bloodRequest.OnUnitIssued(wasReserved);

        if (issue.IsEmergencyRelease)
        {
            audit.Add(new AuditEntry(AuditCategories.Clinical, "bloodbank.emergency_release", $"Uncross-matched {unit.UnitNumber} issued for {bloodRequest.RequestNumber}")
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
        await db.SaveChangesAsync(ct);

        await notifications.NotifyPermissionHoldersAsync(SpecialtyCarePermissions.BloodBankReactionsInvestigate, issue.FacilityId,
            reaction.IsSerious ? "Serious transfusion reaction" : "Transfusion reaction reported",
            $"Unit {issue.UnitNumber}: {reaction.Severity} reaction reported. Investigate and send the bag and post-transfusion sample to the blood bank.",
            $"/blood-bank/reactions/{reaction.Id}", "bloodbank", reaction.IsSerious ? "critical" : "warning", ct);
        return reaction;
    }

    public Task<Result> InvestigateReactionAsync(Guid reactionId, CancellationToken ct) =>
        ChangeReactionAsync(reactionId, r => r.StartInvestigation(), ct);

    public Task<Result> CloseReactionAsync(Guid reactionId, CloseReactionRequest request, CancellationToken ct) =>
        ChangeReactionAsync(reactionId, r => r.Close(request.Type, request.Severity, request.Findings, request.ReportedToHaemovigilance,
            currentUser.Stamp(time.GetUtcNow()).Name, time.GetUtcNow()), ct);

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

    private Task PublishAsync(Guid facilityId, CancellationToken ct) =>
        realtime.FacilityChangedAsync(facilityId, SpecialtyRealtime.BloodBankChanged, new { area = "requests" }, ct);
}
