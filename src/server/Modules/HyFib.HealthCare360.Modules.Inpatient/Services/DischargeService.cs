using HyFib.HealthCare360.Modules.Clinical;
using HyFib.HealthCare360.Modules.Clinical.Documents;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>What closing the stay was asked for: the type, the time, the notes and the abscondence report, as they stood.</summary>
public sealed record DischargeAskPayload(Guid AdmissionId, DischargeType Type, DateTimeOffset DischargedAt, string? Notes, AbscondedReport? Absconded, bool CertificatePendingPostMortem);

/// <summary>
/// Discharge workflow: initiate (Billing prepares the final bill), check billing clearance, complete (the visit closes,
/// the bed goes for cleaning, the family is informed) or revoke. Every bed day up to discharge is charged before the
/// clearance check, so the patient never leaves with charges missing from the final bill. No stay ends while another
/// department still has the patient (<see cref="IDischargeHoldSource"/>: a patient in pre-op or on the table).
/// </summary>
/// <remarks>
/// How a stay ends other than routinely is not on one person's word (maker-checker, slice 8):
/// <list type="bullet">
/// <item>LAMA and DAMA record who signed the form (or that nobody would), the doctor who explained the risks and a witness,
/// each from their own sign-in or confirmation at this screen (<see cref="AgainstAdviceDeclarations"/>, ADR-014).</item>
/// <item>Expired needs the patient's death certificate confirmed by a second doctor (<see cref="ExpiredDischarge"/>); a
/// medico-legal death whose certificate waits for the post-mortem is closed only with a second doctor's approval
/// (<see cref="ExpiredPendingPostMortemApproval"/>).</item>
/// <item>Absconded waits for the nurse in charge (<see cref="AbscondedDischargeApproval"/>), with the report of who
/// searched and who was told; for a medico-legal stay, the police.</item>
/// </list>
/// Expired and Absconded still skip billing clearance (a body is not held for dues; the absconded patient is gone): what was
/// due is recorded on the stay's <see cref="DischargeDeclaration"/>.
/// </remarks>
public sealed class DischargeService(
    HospitalDbContext db,
    IBillingStatusProvider billing,
    IEnumerable<IDischargeHoldSource> holdSources,
    InpatientChargeService charges,
    DeathCertificateStandings certificates,
    AgainstAdviceDeclarations againstAdvice,
    IApprovals approvals,
    INotificationService notifications,
    NotificationTemplateRenderer templates,
    IRealtimePublisher realtime,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public const string AbscondedKind = "inpatient.absconded";

    /// <summary>The discharge's SMS (<c>discharge.completed</c>): it only says the summary is on the portal.</summary>
    internal static readonly IReadOnlyList<NotificationChannel> DischargedSmsChannels = [NotificationChannel.Sms];

    /// <summary>
    /// The discharge's WhatsApp message: <c>discharge.completed</c> (text only) for every discharge, or
    /// <c>discharge.summary</c> (a document header) instead when the stay has a signed summary and the hospital has switched
    /// that template on (it is seeded "Not in use" until Meta approves it).
    /// </summary>
    internal static readonly IReadOnlyList<NotificationChannel> DischargedWhatsAppChannels = [NotificationChannel.WhatsApp];

    internal const string DischargedTemplate = "discharge.completed";
    internal const string DischargeSummaryTemplate = "discharge.summary";
    public const string PendingPostMortemKind = "inpatient.expired_pending_postmortem";

    /// <summary>A witness to a patient leaving the ward against medical advice: a nurse or doctor of the ward team.</summary>
    public static readonly WitnessPurpose AgainstAdviceWitness = new("discharge.against_advice", InpatientPermissions.NursingChart,
        "a patient leaving the ward against medical advice (LAMA or DAMA)");

    public async Task<Result<DischargeResult>> InitiateAsync(Guid admissionId, InitiateDischargeRequest request, CancellationToken ct)
    {
        var admission = await db.Set<Admission>().FirstOrDefaultAsync(a => a.Id == admissionId, ct);
        if (admission is null)
        {
            return Error.NotFound(nameof(Admission), admissionId);
        }

        var now = time.GetUtcNow();
        var initiated = admission.InitiateDischarge(currentUser.UserId, request.ExpectedDischargeDate, now);
        if (initiated.IsFailure)
        {
            return initiated.Error!;
        }

        if (!string.IsNullOrWhiteSpace(request.Notes))
        {
            admission.DischargeNotes = request.Notes.Trim();
        }

        // Everything billable up to now goes on the bill before Billing prepares the final bill: the due days are staged
        // in this unit of work, so Billing's DischargeInitiated handler, which runs inside this save, drafts the final
        // bill with them.
        var posted = await charges.StageDueBedDaysAsync(admission.Id, now, ct);
        await db.SaveChangesAsync(ct);

        await realtime.ToFacilityAsync(admission.FacilityId, InpatientRealtime.DischargeInitiated, new { admissionId = admission.Id }, ct);
        await InpatientRealtime.AdmissionChangedAsync(realtime, admission, ct);
        return ToResult(admission, posted);
    }

    public async Task<Result<DischargeResult>> RevokeAsync(Guid admissionId, RevokeDischargeRequest request, CancellationToken ct)
    {
        var admission = await db.Set<Admission>().FirstOrDefaultAsync(a => a.Id == admissionId, ct);
        if (admission is null)
        {
            return Error.NotFound(nameof(Admission), admissionId);
        }

        var revoked = admission.RevokeDischarge(time.GetUtcNow());
        if (revoked.IsFailure)
        {
            return revoked.Error!;
        }

        admission.DischargeNotes = $"Discharge revoked: {request.Reason.Trim()}";
        await db.SaveChangesAsync(ct);
        await InpatientRealtime.AdmissionChangedAsync(realtime, admission, ct);
        return ToResult(admission, 0);
    }

    public async Task<Result<DischargeClearanceDto>> ClearanceAsync(Guid admissionId, CancellationToken ct)
    {
        if (!await db.Set<Admission>().AnyAsync(a => a.Id == admissionId, ct))
        {
            return Error.NotFound(nameof(Admission), admissionId);
        }

        var clearance = await billing.GetDischargeClearanceAsync(admissionId, ct);
        return new DischargeClearanceDto(admissionId, clearance.Cleared, clearance.Outstanding, clearance.Reason);
    }

    /// <summary>The patient's death certificate as it stands, for the ward about to close the stay as expired: why it waits, if it does.</summary>
    public async Task<Result<DischargeDeathCertificateDto>> DeathCertificateAsync(Guid admissionId, CancellationToken ct)
    {
        var admission = await db.Set<Admission>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == admissionId, ct);
        if (admission is null)
        {
            return Error.NotFound(nameof(Admission), admissionId);
        }

        var certificate = await certificates.ForPatientAsync(admission.PatientId, ct);
        var check = ExpiredDischarge.Check(certificate, admission.Id, admission.IsMlc, pendingPostMortem: false);
        var status = check.IsSuccess
            ? $"Death certificate {certificate!.Words}: the stay can be closed as expired."
            : certificate is null && admission.IsMlc
                ? $"{check.Error!.Message} A medico-legal death whose certificate waits for the post-mortem can be closed with a second doctor's approval."
                : check.Error!.Message;
        return new DischargeDeathCertificateDto(
            admission.Id, certificate?.Id, certificate?.SerialNumber, check.IsSuccess, certificate is not null && (certificate.AdmissionId is null || certificate.AdmissionId == admission.Id),
            certificate?.ConfirmedBy, certificate?.ConfirmationWaitingSince, admission.IsMlc, status);
    }

    /// <summary>The closing of this stay waiting for a second person (Absconded, Expired pending post-mortem), if any.</summary>
    public async Task<PendingDischarge?> PendingAsync(Guid admissionId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var request = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => (r.Kind == AbscondedKind || r.Kind == PendingPostMortemKind) && r.SubjectType == nameof(Admission) && r.SubjectId == admissionId
                        && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderByDescending(r => r.RequestedAt)
            .Select(r => new { r.Id, r.Kind, r.Summary, r.RequestedByName, r.RequestedAt })
            .FirstOrDefaultAsync(ct);
        return request is null
            ? null
            : new PendingDischarge(request.Id, request.Kind == AbscondedKind ? DischargeType.Absconded : DischargeType.Expired, request.Summary, request.RequestedByName,
                request.RequestedAt);
    }

    /// <summary>How the stay ended, as declared (LAMA, DAMA, Absconded, Expired); null for a routine discharge.</summary>
    public async Task<DischargeDeclarationDto?> DeclarationAsync(Guid admissionId, CancellationToken ct)
    {
        var d = await db.Set<DischargeDeclaration>().AsNoTracking().FirstOrDefaultAsync(x => x.AdmissionId == admissionId, ct);
        return d is null
            ? null
            : new DischargeDeclarationDto(
                d.Type, d.SignedBy is { } signedBy ? AgainstAdviceRules.Signed(signedBy, d.SignatoryName, d.SignatoryRelationship) : null, d.RisksExplainedByName,
                d.WitnessName, d.SignedFormFileId, d.LastSeenAt, d.SearchedBy, d.PoliceInformed, d.PoliceStation, d.PoliceReference, d.RelativesInformed,
                d.RelativeInformed, d.DeathCertificateNumber, d.CertificatePendingPostMortem, d.DuesOutstanding, d.RecordedByName, d.ApprovedByName);
    }

    /// <summary>
    /// Closes the stay, or, for an absconded patient or a medico-legal death whose certificate waits for the post-mortem,
    /// asks a second person to approve closing it (<see cref="DischargeOutcome.Asked"/>).
    /// </summary>
    public async Task<Result<DischargeOutcome>> CompleteAsync(Guid admissionId, CompleteDischargeRequest request, CancellationToken ct)
    {
        var admission = await db.Set<Admission>().FirstOrDefaultAsync(a => a.Id == admissionId, ct);
        if (admission is null)
        {
            return Error.NotFound(nameof(Admission), admissionId);
        }

        if (!admission.IsActive)
        {
            return Error.Rule("admission.closed", "This admission is already closed.");
        }

        var type = request.DischargeType;
        if (admission.Status == AdmissionStatus.Admitted && DischargeRules.RequiresInitiation(type))
        {
            return Error.Rule("discharge.not_initiated", "Initiate the discharge first so billing can prepare the final bill.");
        }

        // However the stay is to end, a patient the theatre still has (in pre-op, on the table) is not discharged: the
        // case is finished, postponed or cancelled there first. Asked before anything is charged, changed or asked for, and
        // again below under the stay's lock, where it holds.
        if (await HoldAsync(admission.Id, type, ct) is { } held)
        {
            return held;
        }

        var now = time.GetUtcNow();
        var dischargedAt = request.DischargedAt ?? now;
        DeathCertificateStanding? certificate = null;
        if (type == DischargeType.Expired)
        {
            certificate = await certificates.ForPatientAsync(admission.PatientId, ct);
            var expired = ExpiredDischarge.Check(certificate, admission.Id, admission.IsMlc, request.CertificatePendingPostMortem);
            if (expired.IsFailure)
            {
                return expired.Error!;
            }

            if (expired.Value == ExpiredDischargeOutcome.AskPendingPostMortem)
            {
                return await AskAsync(admission, request with { DischargedAt = dischargedAt }, ct);
            }
        }

        if (type == DischargeType.Absconded)
        {
            if (request.Absconded!.Check(admission.IsMlc, now) is { } incomplete)
            {
                return incomplete;
            }

            return await AskAsync(admission, request with { DischargedAt = dischargedAt }, ct);
        }

        var posted = 0;
        decimal? dues = null;
        if (DischargeRules.RequiresBillingClearance(type))
        {
            posted = await charges.PostDueBedDaysAsync(admission.Id, dischargedAt, ct);
            var clearance = await billing.GetDischargeClearanceAsync(admission.Id, ct);
            if (!clearance.Cleared)
            {
                return new Error("discharge.billing_pending",
                    clearance.Reason ?? $"Billing clearance is pending: Rs. {Money.FormatInr(clearance.Outstanding)} outstanding.",
                    ErrorType.BusinessRule)
                {
                    Data = new { clearance.Outstanding },
                };
            }
        }
        else
        {
            // A body is not held for dues; what was due is recorded.
            dues = (await billing.GetDischargeClearanceAsync(admission.Id, ct)).Outstanding;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // LAMA and DAMA: who signed, the doctor who explained the risks and a witness, each confirmed at this screen; the
        // confirmations are spent with this save.
        AgainstAdviceRecord? declared = null;
        if (DischargeRules.IsAgainstAdvice(type))
        {
            var recorded = await againstAdvice.RecordAsync(request.AgainstAdvice!, AgainstAdviceWitness.Key, admission.PatientId, nameof(Admission), admission.Id, ct);
            if (recorded.IsFailure)
            {
                return recorded.Error!;
            }

            declared = recorded.Value;
        }

        var closed = await CloseAsync(admission, type, currentUser.UserId, request.Notes, dischargedAt, request.NotifyPatient, now, ct);
        if (closed.IsFailure)
        {
            return closed.Error!;
        }

        if (declared is not null || type == DischargeType.Expired)
        {
            var declaration = Declaration(admission, type, dues);
            if (declared is not null)
            {
                declaration.SetAgainstAdvice(declared);
            }

            (declaration.DeathCertificateId, declaration.DeathCertificateNumber) = (certificate?.Id, certificate?.SerialNumber);
            db.Add(declaration);
            Audit(admission, type == DischargeType.Expired ? "discharge.expired" : "discharge.against_advice",
                type == DischargeType.Expired
                    ? $"{admission.AdmissionNumber} closed as expired with death certificate {certificate!.Words}; dues left: Rs. {Money.FormatInr(dues ?? 0)}"
                    : $"{admission.AdmissionNumber} closed as {type.ToString().ToUpperInvariant()}: {declared!.Words}",
                new { type = type.ToString(), certificate = certificate?.SerialNumber, dues, againstAdvice = declared?.Words });
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        if (!DischargeRules.RequiresBillingClearance(type))
        {
            // Death or abscondence is recorded first; the stay's remaining days are billed afterwards (retried hourly).
            posted = await charges.PostDueBedDaysAsync(admission.Id, null, ct);
        }

        if (closed.Value is { } bed)
        {
            await InpatientRealtime.BedChangedAsync(realtime, bed, ct);
        }

        await InpatientRealtime.AdmissionChangedAsync(realtime, admission, ct);
        return new DischargeOutcome(ToResult(admission, posted), null);
    }

    /// <summary>
    /// Asks a second person to approve closing the stay: the nurse in charge for an absconded patient, a second doctor for a
    /// medico-legal death whose certificate waits for the post-mortem. Until then the patient stays admitted.
    /// </summary>
    private async Task<Result<DischargeOutcome>> AskAsync(Admission admission, CompleteDischargeRequest request, CancellationToken ct)
    {
        var absconded = request.DischargeType == DischargeType.Absconded;
        var kind = absconded ? AbscondedKind : PendingPostMortemKind;
        if (await PendingAsync(admission.Id, ct) is { } waiting)
        {
            return Error.Conflict("approval.already_requested",
                $"Closing this stay is already waiting for approval ({waiting.Summary}, asked by {waiting.RequestedBy}). Withdraw it first, or wait for its decision.");
        }

        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == admission.PatientId).Select(p => new { p.Uhid, p.FullName, p.IsVip }).FirstAsync(ct);
        var dues = (await billing.GetDischargeClearanceAsync(admission.Id, ct)).Outstanding;
        var dischargedAt = request.DischargedAt!.Value;
        List<ApprovalDetail> details =
        [
            new("Stay", $"{admission.AdmissionNumber} · {patient.Uhid} · {(patient.IsVip ? PatientNames.Restricted(patient.FullName) : patient.FullName)}"),
            new("Status", $"{(admission.Status == AdmissionStatus.DischargeInitiated ? "discharge initiated" : "admitted")} → {(absconded ? "absconded" : "expired")}"),
            new("Time", DocumentFormat.DateTime(dischargedAt)),
            new("Medico-legal", admission.IsMlc ? admission.MlcNumber is { Length: > 0 } number ? $"Yes, MLC no. {number}" : "Yes" : "No"),
        ];
        if (request.Absconded is { } report)
        {
            details.Add(new("Last seen", DocumentFormat.DateTime(report.LastSeenAt)));
            details.Add(new("Searched by", report.SearchedBy.Trim()));
            details.Add(new("Police told",
                report.PoliceInformed ? $"Yes, {report.PoliceStation?.Trim()}{(report.PoliceReference is { Length: > 0 } reference ? $", ref. {reference.Trim()}" : string.Empty)}" : "No"));
            details.Add(new("Relatives told", report.RelativesInformed ? $"Yes, {report.RelativeInformed?.Trim()}" : "No"));
        }
        else
        {
            details.Add(new("Death certificate", "Not yet issued: the cause of death waits for the post-mortem"));
        }

        details.Add(new("Dues", $"Billing clearance is skipped; Rs. {Money.FormatInr(dues)} due so far is recorded, and the remaining bed days are billed."));
        details.Add(new("What it reaches", absconded
            ? "The stay closes as absconded and the bed goes for cleaning. Until this is approved the patient stays admitted: if they come back, withdraw the request."
            : "The stay closes as expired and the patient is recorded as deceased, so the mortuary can receive the body. Until this is approved the patient stays admitted."));

        var asked = await approvals.RequestAsync(
            kind,
            new ApprovalSubject(nameof(Admission), admission.Id, admission.Version, admission.FacilityId, admission.PatientId),
            new DischargeAskPayload(admission.Id, request.DischargeType, dischargedAt, request.Notes?.Trim(), request.Absconded, request.CertificatePendingPostMortem),
            $"{admission.AdmissionNumber}: {(absconded ? "close the stay as absconded" : "close the stay as expired, certificate pending post-mortem")}",
            details,
            request.Notes!.Trim(),
            ct);
        return asked.IsFailure ? asked.Error! : new DischargeOutcome(null, asked.Value);
    }

    /// <summary>
    /// Closes the stay a second person approved, in the approval's transaction (the approval saves): only a stay still open,
    /// that the theatre does not hold, and, pending post-mortem, whose patient still has no certificate (with one, the stay is
    /// closed with it). What was due is recorded, and the closing audited with both names.
    /// </summary>
    internal async Task<Result<string>> ApplyApprovedAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<DischargeAskPayload>(request.Payload);
        var admission = await db.Set<Admission>().FirstOrDefaultAsync(a => a.Id == asked.AdmissionId, ct);
        if (admission is null)
        {
            return Error.Rule("admission.gone", "The admission no longer exists, so nothing was changed.");
        }

        if (!admission.IsActive)
        {
            return Error.Rule("admission.closed", $"{admission.AdmissionNumber} is already closed, so nothing was changed.");
        }

        if (asked.Type == DischargeType.Expired && await certificates.ForPatientAsync(admission.PatientId, ct) is { } certificate)
        {
            return Error.Rule("approval.stale",
                $"Death certificate {certificate.SerialNumber} has been certified since this was asked for, so nothing was changed. Close the stay as expired with the certificate.");
        }

        var dues = (await billing.GetDischargeClearanceAsync(admission.Id, ct)).Outstanding;
        var closed = await CloseAsync(admission, asked.Type, request.RequestedBy, asked.Notes, asked.DischargedAt, notify: false, time.GetUtcNow(), ct);
        if (closed.IsFailure)
        {
            return closed.Error!;
        }

        // The stay's remaining days are billed with the approval's save (the hourly run retries any billing refuses).
        await charges.StageDueBedDaysAsync(admission.Id, null, ct);

        var declaration = Declaration(admission, asked.Type, dues);
        (declaration.RecordedByUserId, declaration.RecordedByName) = (request.RequestedBy, request.RequestedByName);
        (declaration.ApprovalId, declaration.ApprovedByUserId, declaration.ApprovedByName) = (request.Id, currentUser.UserId, Approver);
        asked.Absconded?.Apply(declaration);
        declaration.CertificatePendingPostMortem = asked.CertificatePendingPostMortem;
        db.Add(declaration);

        var words = asked.Type == DischargeType.Absconded ? "absconded" : "expired, certificate pending post-mortem";
        Audit(admission, asked.Type == DischargeType.Absconded ? "discharge.absconded" : "discharge.expired_pending_postmortem",
            $"{admission.AdmissionNumber}: admitted → {words}; dues left: Rs. {Money.FormatInr(dues)}: {request.Reason} (asked for by {request.RequestedByName}, approved by {Approver})",
            new { type = asked.Type.ToString(), dues, approval = request.Id, requestedBy = request.RequestedBy, approvedBy = currentUser.UserId });
        return $"{admission.AdmissionNumber} closed as {words}";
    }

    /// <summary>
    /// Closes the stay in the caller's transaction, under the stay's lock: the theatre's hold checked again, the visit
    /// completed, the bed released for cleaning, the patient told (routine discharges). Returns the bed released, if any.
    /// </summary>
    private async Task<Result<Bed?>> CloseAsync(
        Admission admission, DischargeType type, Guid? by, string? notes, DateTimeOffset dischargedAt, bool notify, DateTimeOffset now, CancellationToken ct)
    {
        // The stay's lock, held to this commit, is the one the theatre takes to receive the patient in pre-op or start
        // the operation (EncounterLocks): a case taken into the theatre meanwhile is committed first and found here.
        await EncounterLocks.LockAsync(db, [admission.Id, admission.VisitId], ct);
        if (await HoldAsync(admission.Id, type, ct) is { } stillHeld)
        {
            return stillHeld;
        }

        var completed = admission.CompleteDischarge(type, by, notes, dischargedAt);
        if (completed.IsFailure)
        {
            return completed.Error!;
        }

        var visit = await db.Set<Visit>().FirstOrDefaultAsync(v => v.Id == admission.VisitId, ct);
        visit?.Complete(dischargedAt);

        var openStays = await db.Set<BedAssignment>().Where(a => a.AdmissionId == admission.Id && a.ToAt == null).ToListAsync(ct);
        openStays.ForEach(a => a.Close(dischargedAt));

        var bed = admission.BedId is { } bedId ? await db.Set<Bed>().FirstOrDefaultAsync(b => b.Id == bedId, ct) : null;
        if (bed is not null && bed.CurrentAdmissionId == admission.Id)
        {
            var released = BedOperations.Release(db, bed, HousekeepingTrigger.Discharge, admission.Id, "Cleaning after discharge", now);
            if (released.IsFailure)
            {
                return released.Error!;
            }
        }

        if (notify && DischargeRules.NotifiesPatient(type))
        {
            var patient = await db.Set<Patient>().AsNoTracking()
                .Where(p => p.Id == admission.PatientId)
                .Select(p => new { p.Id, p.FullName, p.Phone, p.Email, p.PreferredLanguage })
                .FirstOrDefaultAsync(ct);
            if (patient?.Phone is not null && !await SharedNumbers.HiddenFromNumberAsync(db, patient.Id, ct))
            {
                var values = new Dictionary<string, string> { ["name"] = patient.FullName };
                var recipient = NotificationRecipient.Patient(patient.Phone, patient.Email, patient.Id, patient.FullName, patient.PreferredLanguage);
                notifications.Queue(DischargedTemplate, recipient, values, DischargedSmsChannels, TimeSpan.FromHours(12));

                // The signed discharge summary goes in the document header of the WhatsApp template made for it, where the
                // hospital has switched that template on. Every other discharge gets the text-only message, because a
                // template with a document header can't go without its document. A summary amended later is sent as it
                // stands then.
                var summaryId = await db.Set<DischargeSummary>().AsNoTracking()
                    .Where(s => s.AdmissionId == admission.Id && s.Status != DischargeSummaryStatus.Draft && s.SignedAt != null)
                    .OrderByDescending(s => s.SignedAt)
                    .Select(s => (Guid?)s.Id)
                    .FirstOrDefaultAsync(ct);
                if (summaryId is { } id
                    && await templates.InUseAsync(DischargeSummaryTemplate, NotificationChannel.WhatsApp, admission.FacilityId, recipient.Language, ct))
                {
                    notifications.Queue(DischargeSummaryTemplate, recipient, values, DischargedWhatsAppChannels, TimeSpan.FromHours(12),
                        document: new NotificationDocument(ClinicalDocumentKinds.DischargeSummary, id));
                }
                else
                {
                    notifications.Queue(DischargedTemplate, recipient, values, DischargedWhatsAppChannels, TimeSpan.FromHours(12));
                }
            }
        }

        return bed;
    }

    private DischargeDeclaration Declaration(Admission admission, DischargeType type, decimal? dues) => new()
    {
        FacilityId = admission.FacilityId,
        AdmissionId = admission.Id,
        Type = type,
        DuesOutstanding = DischargeRules.RequiresBillingClearance(type) ? null : dues,
        RecordedByUserId = currentUser.UserId,
        RecordedByName = currentUser.DisplayName ?? currentUser.UserName ?? "Staff",
    };

    private string Approver => currentUser.DisplayName ?? currentUser.UserName ?? "Staff";

    private void Audit(Admission admission, string action, string summary, object details) =>
        audit.Add(new AuditEntry(AuditCategories.Clinical, action, summary.Length <= 1000 ? summary : summary[..999] + "…")
        {
            PatientId = admission.PatientId,
            EntityType = nameof(Admission),
            EntityId = admission.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = details,
        });

    /// <summary>
    /// Why the stay cannot end now as <paramref name="type"/>, as the modules that hold it say (the theatre; for a planned
    /// discharge, an unsigned discharge summary), or null when nothing holds it.
    /// </summary>
    private async Task<Error?> HoldAsync(Guid admissionId, DischargeType type, CancellationToken ct)
    {
        var planned = DischargeRules.IsPlanned(type);
        var holds = new List<DischargeHold>();
        foreach (var source in holdSources)
        {
            holds.AddRange((await source.HoldsAsync(admissionId, ct)).Where(h => planned || !h.PlannedDischargeOnly));
        }

        return holds.Count > 0 ? Error.Rule(holds[0].Code, string.Join(" ", holds.Select(h => h.Message))) : null;
    }

    private static DischargeResult ToResult(Admission a, int chargesPosted) =>
        new(a.Id, a.Status, a.DischargeInitiatedAt, a.DischargedAt, chargesPosted);
}

/// <summary>An absconded patient's stay closed: decided by the nurse in charge (<see cref="InpatientPermissions.NursingInCharge"/>), never the one who asked.</summary>
internal sealed class AbscondedDischargeApproval(DischargeService discharges) : IApprovalAction
{
    public string Kind => DischargeService.AbscondedKind;

    public string Title => "Stay closed as absconded";

    public string ApprovePermission => InpatientPermissions.NursingInCharge;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => discharges.ApplyApprovedAsync(request, ct);
}

/// <summary>
/// A medico-legal death's stay closed before its certificate, which waits for the post-mortem: decided by a second doctor
/// who confirms death certificates (<see cref="ClinicalPermissions.DeathsConfirm"/>), never the one who asked.
/// </summary>
internal sealed class ExpiredPendingPostMortemApproval(DischargeService discharges) : IApprovalAction
{
    public string Kind => DischargeService.PendingPostMortemKind;

    public string Title => "Death closed pending post-mortem";

    public string ApprovePermission => ClinicalPermissions.DeathsConfirm;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => discharges.ApplyApprovedAsync(request, ct);
}
