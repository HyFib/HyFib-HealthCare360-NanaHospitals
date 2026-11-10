using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Persistence;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>Normalised patient fields shared by registration and update.</summary>
internal sealed record PatientInput(
    string? Title, string FirstName, string? MiddleName, string? LastName, Gender Gender, DateOnly? DateOfBirth, int? AgeYears, int? AgeMonths,
    string? Phone, string? AlternatePhone, string? Email, Address? Address, BloodGroup? BloodGroup, MaritalStatus? MaritalStatus,
    string? GuardianName, GuardianRelation? GuardianRelation, string? EmergencyContactName, string? EmergencyContactPhone,
    string? EmergencyContactRelation, string? Occupation, string? Religion, string? Nationality, string? PreferredLanguage,
    PatientCategory? Category, string? AbhaNumber, string? AbhaAddress, string? AadhaarNumber, Guid? PhotoFileId, bool RemovePhoto, bool IsVip, string? Notes)
{
    public static PatientInput From(RegisterPatientRequest r) => new(
        r.Title, r.FirstName, r.MiddleName, r.LastName, r.Gender, r.DateOfBirth, r.AgeYears, r.AgeMonths, r.Phone, r.AlternatePhone, r.Email,
        r.Address, r.BloodGroup, r.MaritalStatus, r.GuardianName, r.GuardianRelation, r.EmergencyContactName, r.EmergencyContactPhone,
        r.EmergencyContactRelation, r.Occupation, r.Religion, r.Nationality, r.PreferredLanguage, r.Category, r.AbhaNumber, r.AbhaAddress,
        r.AadhaarNumber, r.PhotoFileId, RemovePhoto: false, r.IsVip, r.Notes);

    public static PatientInput From(UpdatePatientRequest r) => new(
        r.Title, r.FirstName, r.MiddleName, r.LastName, r.Gender, r.DateOfBirth, r.AgeYears, r.AgeMonths, r.Phone, r.AlternatePhone, r.Email,
        r.Address, r.BloodGroup, r.MaritalStatus, r.GuardianName, r.GuardianRelation, r.EmergencyContactName, r.EmergencyContactPhone,
        r.EmergencyContactRelation, r.Occupation, r.Religion, r.Nationality, r.PreferredLanguage, r.Category, r.AbhaNumber, r.AbhaAddress,
        r.AadhaarNumber, r.PhotoFileId, r.RemovePhoto, r.IsVip, r.Notes);
}

/// <summary>
/// Registration (FO-01): duplicate detection, UHID allocation (gap-free, inside the insert transaction),
/// encryption of national identifiers, updates with optimistic concurrency, and duplicate merging, which one person asks
/// for and another approves (<see cref="PatientMergeApproval"/>).
/// </summary>
public sealed class PatientRegistrationService(
    HospitalDbContext db,
    INumberSeries numbers,
    DowntimeUhidService downtime,
    IFieldProtector protector,
    ICurrentUser currentUser,
    IAuditLogger audit,
    WhatsAppConsent whatsApp,
    ISettingsService settings,
    PatientAccessGuard access,
    IApprovals approvals,
    PatientRecordApprovals records,
    TimeProvider time)
{
    /// <summary>Audit action of a change to the patient's ABHA number or address.</summary>
    public const string AbhaChangedAction = "patient.abha_changed";

    private const int StrongDuplicateScore = 75;

    public async Task<IReadOnlyList<DuplicateCandidate>> FindDuplicatesAsync(DuplicateCheckRequest check, Guid? excludeId, CancellationToken ct)
    {
        var phone = Phone.Normalize(check.Phone);
        var abhaIndexes = string.IsNullOrWhiteSpace(check.AbhaNumber) ? null : protector.BlindIndexes(Masking.Normalize(check.AbhaNumber)).ToArray();
        var aadhaarIndex = string.IsNullOrWhiteSpace(check.AadhaarNumber) ? null : protector.FixedBlindIndex(Masking.Normalize(check.AadhaarNumber));
        var firstName = string.IsNullOrWhiteSpace(check.FirstName) ? null : check.FirstName.Trim();
        var dob = check.DateOfBirth;
        // The same first name, whatever its case: escaped, so a name's own % or _ is not a wildcard.
        var sameFirstName = firstName?.ToLikeLiteral();

        if (phone is null && abhaIndexes is null && aadhaarIndex is null && (firstName is null || dob is null))
        {
            return [];
        }

        var rows = await db.Set<Patient>().AsNoTracking()
            .Where(p => p.MergedIntoPatientId == null && (excludeId == null || p.Id != excludeId))
            .Where(p => (abhaIndexes != null && p.AbhaNumberIndex != null && abhaIndexes.Contains(p.AbhaNumberIndex))
                        || (aadhaarIndex != null && p.AadhaarIndex == aadhaarIndex)
                        || (phone != null && p.Phone == phone)
                        || (sameFirstName != null && dob != null && p.DateOfBirth == dob && EF.Functions.ILike(p.FirstName, sameFirstName, QueryExtensions.LikeEscape)))
            .OrderByDescending(p => p.RegisteredAt)
            .Take(25)
            .Select(p => new { p.Id, p.Uhid, p.FullName, p.FirstName, p.Gender, p.DateOfBirth, p.Phone, p.AbhaNumberIndex, p.AadhaarIndex, p.IsVip })
            .ToListAsync(ct);

        // A restricted record shows as initials to those who may not open it, as in search (R-3).
        var opened = await access.OpenableAsync([.. rows.Where(p => p.IsVip).Select(p => p.Id)], ct);

        var today = time.Today();
        return rows
            .Select(p =>
            {
                var sameFirst = firstName is not null && string.Equals(p.FirstName, firstName, StringComparison.OrdinalIgnoreCase);
                var samePhone = phone is not null && p.Phone == phone;
                var similarAge = dob is not null && p.DateOfBirth is not null && Math.Abs(dob.Value.DayNumber - p.DateOfBirth.Value.DayNumber) <= 365;
                var (score, reason) =
                    abhaIndexes is not null && p.AbhaNumberIndex is not null && abhaIndexes.Contains(p.AbhaNumberIndex) ? (100, "Same ABHA number")
                    : aadhaarIndex is not null && p.AadhaarIndex == aadhaarIndex ? (100, "Same Aadhaar number")
                    : samePhone && sameFirst ? (90, "Same mobile number and first name")
                    : sameFirst && dob is not null && p.DateOfBirth == dob ? (85, "Same name and date of birth")
                    : samePhone && similarAge ? (75, "Same mobile number and similar age")
                    : samePhone ? (50, "Same mobile number (possibly a family member)")
                    : (0, string.Empty);
                return new DuplicateCandidate(p.Id, p.Uhid, p.IsVip && !opened.Contains(p.Id) ? PatientNames.Restricted(p.FullName) : p.FullName, p.Gender, p.DateOfBirth is { } d ? Age.Format(d, today) : null,
                    Phone.Mask(p.Phone), score, reason);
            })
            .Where(c => c.Score > 0)
            .OrderByDescending(c => c.Score)
            .Take(5)
            .ToList();
    }

    public async Task<Result<PatientCreated>> RegisterAsync(RegisterPatientRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility before registering patients.");
        }

        var input = PatientInput.From(request);
        var today = time.Today();
        var dob = PatientRules.ResolveDateOfBirth(input.DateOfBirth, input.AgeYears, input.AgeMonths, today, out _);

        var identifierConflict = await CheckIdentifierConflictsAsync(input, null, ct);
        if (identifierConflict is not null)
        {
            return identifierConflict;
        }

        var photoProblem = await CheckPhotoAsync(input.PhotoFileId, null, ct);
        if (photoProblem is not null)
        {
            return photoProblem;
        }

        if (!request.ConfirmedNotDuplicate)
        {
            var candidates = await FindDuplicatesAsync(
                new DuplicateCheckRequest(input.FirstName, input.LastName, dob, input.Phone, null, null), null, ct);
            var strong = candidates.Where(c => c.Score >= StrongDuplicateScore).ToList();
            if (strong.Count > 0)
            {
                return Error.Conflict("patient.possible_duplicate", "This patient may already be registered. Review the matches before continuing.", strong);
            }
        }

        var patient = new Patient { RegisteredFacilityId = facilityId, RegisteredAt = request.Paper?.RegisteredAt ?? time.GetUtcNow() };
        Apply(patient, input, today, isNew: true);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (request.Paper is { } paper)
        {
            // Entered from the form filled in while HC360 was down: the UHID the patient already carries.
            var claimed = await downtime.ClaimAsync(paper, patient, ct);
            if (claimed.IsFailure)
            {
                return claimed.Error!;
            }

            patient.Uhid = claimed.Value.Uhid;
        }
        else
        {
            patient.Uhid = await numbers.NextAsync(PatientsNumberSeries.Uhid, null, ct);
        }

        patient.MarkRegistered();
        db.Add(patient);
        db.Set<PatientConsent>().Add(new PatientConsent
        {
            PatientId = patient.Id,
            Type = ConsentType.SmsWhatsAppUpdates,
            Granted = !string.IsNullOrWhiteSpace(patient.Phone),
            CapturedAt = patient.RegisteredAt,
            CapturedByUserId = currentUser.UserId,
            Notes = "Transactional SMS/WhatsApp updates accepted at registration.",
        });
        if (request.PrivacyNoticeGiven)
        {
            db.Set<PatientConsent>().Add(new PatientConsent
            {
                PatientId = patient.Id,
                Type = ConsentType.PrivacyNotice,
                ConsentVersion = await PrivacyNotice.CurrentVersionAsync(settings, facilityId, ct),
                Granted = true,
                CapturedAt = patient.RegisteredAt,
                CapturedByUserId = currentUser.UserId,
                Notes = "Privacy notice given at registration.",
            });
        }

        if (request.WhatsAppOptIn)
        {
            // Recorded for the number, as WhatsApp's own opt-in is; a STOP the number sent earlier stays in force.
            await whatsApp.OptInAsync(patient.Phone, patient.Id, WhatsAppOptInSource.RegistrationDesk, facilityId, byNumberHolder: false, ct);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PatientConfiguration.AadhaarNumberTaken(ex))
        {
            await transaction.RollbackAsync(ct);
            return await AadhaarNumberTakenAsync(input, null, ct);
        }

        await transaction.CommitAsync(ct);

        return new PatientCreated(patient.Id, patient.Uhid);
    }

    /// <summary>
    /// Saves a patient's details. Most are saved at once. What needs a second person (maker-checker, slice 7) is kept as
    /// it is and asked for, in the same transaction: the identity of a patient past the correction window
    /// (<see cref="IdentityCorrection"/>), a VIP restriction lifted, and the deceased status set or taken back here. The
    /// answer names the request (the first, when several are asked in one save; the record's read shows them all).
    /// </summary>
    public async Task<Result<PatientSaved>> UpdateAsync(Guid id, UpdatePatientRequest request, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), id);
        }

        if (patient.MergedIntoPatientId is not null)
        {
            return Error.Rule("patient.merged", "This record was merged into another patient and can no longer be edited.");
        }

        // The portal signs in with the registered number, so a new one is made only with a code sent to it
        // (PatientPhoneChangeService). Sending the number on file back, or no number to remove it, is still an edit. A form
        // loaded before the number changed is out of date as a whole: a conflict, so it reloads, not a refusal of its number.
        if (Phone.Normalize(request.Phone) is { } phone && phone != patient.Phone)
        {
            return request.Version != patient.Version
                ? Error.ConcurrencyConflict
                : Error.Validation("patient.phone_needs_code", PatientPhoneChangeService.NeedsCodeMessage,
                    new Dictionary<string, string[]> { ["phone"] = [PatientPhoneChangeService.NeedsCodeMessage] });
        }

        // A new or changed ABHA address must be in ABDM's format; one already on file (an old @ndhm handle, say) is kept.
        if (Abha.NormalizeAddress(request.AbhaAddress) is { } abhaAddress
            && abhaAddress != Abha.NormalizeAddress(patient.AbhaAddress)
            && !Abha.IsValidAddress(abhaAddress))
        {
            return Error.Field("abhaAddress", CommonRules.AbhaAddressMessage);
        }

        var input = PatientInput.From(request);
        var identifierConflict = await CheckIdentifierConflictsAsync(input, id, ct);
        if (identifierConflict is not null)
        {
            return identifierConflict;
        }

        // Only a new photo is checked; keeping the photo the patient already has needs no check.
        var photoProblem = input.PhotoFileId == patient.PhotoFileId ? null : await CheckPhotoAsync(input.PhotoFileId, id, ct);
        if (photoProblem is not null)
        {
            return photoProblem;
        }

        var today = time.Today();
        var asks = await DecideAsksAsync(patient, request, input, today, ct);
        if (asks.IsFailure)
        {
            return asks.Error!;
        }

        var (keepIdentity, identity, liftRestriction, deceased) = asks.Value;
        db.SetExpectedVersion(patient, request.Version);
        if (identity is not null || liftRestriction || deceased is not null)
        {
            // Asked for over the record as the form saw it, even when nothing else on it changes.
            db.GuardVersion(patient, request.Version);
        }

        var previousPhone = patient.Phone;
        ApplyDetails(patient, input);
        if (!keepIdentity)
        {
            ApplyIdentity(patient, input, today, isNew: false);
        }

        // A restriction is added at once; lifted, it waits, and the record stays restricted meanwhile. The deceased status is
        // never set here: it waits (or comes from the death certificate or an expired discharge).
        patient.IsVip = input.IsVip || patient.IsVip;
        patient.IsActive = request.IsActive;

        // Portal links sent to the number stop when it is removed, and the record's list of links says why. (Opening a link
        // checks that the record still has the number it went to, so one is never used after that either way.)
        if (patient.Phone != previousPhone)
        {
            await PortalLinkService.RevokeLiveLinksAsync(db, patient.Id, PortalLink.NumberChangedReason, time.GetUtcNow(), ct);
        }

        var reason = request.ChangeReason?.Trim() ?? string.Empty;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PatientConfiguration.AadhaarNumberTaken(ex))
        {
            await transaction.RollbackAsync(ct);
            return await AadhaarNumberTakenAsync(input, id, ct);
        }

        ApprovalRequestDto? first = null;
        if (identity is not null)
        {
            var asked = await records.AskIdentityAsync(patient, identity, reason, ct);
            if (asked.IsFailure)
            {
                return asked.Error!;
            }

            first ??= asked.Value;
        }

        if (liftRestriction)
        {
            var asked = await records.AskRestrictionLiftAsync(patient, reason, ct);
            if (asked.IsFailure)
            {
                return asked.Error!;
            }

            first ??= asked.Value;
        }

        if (deceased is not null)
        {
            var asked = await records.AskDeceasedAsync(patient, deceased, reason, ct);
            if (asked.IsFailure)
            {
                return asked.Error!;
            }

            first ??= asked.Value;
        }

        await transaction.CommitAsync(ct);
        return new PatientSaved(first);
    }

    /// <summary>
    /// What this save asks a second person for: the identity (unless it may still be corrected at once), the restriction
    /// lifted, the deceased status. What already waits as asked is not asked again; something else of a kind that waits
    /// is refused until that is decided. A request needs a reason.
    /// </summary>
    private async Task<Result<(bool KeepIdentity, PatientIdentityPayload? Identity, bool LiftRestriction, PatientDeceasedPayload? Deceased)>> DecideAsksAsync(
        Patient patient, UpdatePatientRequest request, PatientInput input, DateOnly today, CancellationToken ct)
    {
        PatientIdentityPayload? identity = null;
        var keepIdentity = false;
        var before = PatientIdentity.Of(patient);
        var scratch = new Patient
        {
            AbhaNumberCipher = patient.AbhaNumberCipher,
            AbhaNumberIndex = patient.AbhaNumberIndex,
            AbhaNumberMasked = patient.AbhaNumberMasked,
            AbhaAddress = patient.AbhaAddress,
            AadhaarLast4 = patient.AadhaarLast4,
            AadhaarIndex = patient.AadhaarIndex,
            DateOfBirth = patient.DateOfBirth,
            IsDateOfBirthEstimated = patient.IsDateOfBirthEstimated,
        };
        ApplyIdentity(scratch, input, today, isNew: false, auditAbha: false);
        var asked = PatientIdentity.Of(scratch);

        // A number is the same only when its index is (the same ABHA number entered again keeps its stored form): another
        // number ending in the same four digits is a change too.
        var abhaNumberChanged = scratch.AbhaNumberIndex != patient.AbhaNumberIndex;
        var aadhaarChanged = scratch.AadhaarIndex != patient.AadhaarIndex;
        if ((asked != before || abhaNumberChanged || aadhaarChanged) && await records.CorrectableUntilAsync(patient, ct) is null)
        {
            // Past the correction window the record keeps its identity: asked for now, or already asked for as it is.
            keepIdentity = true;
            var waiting = await records.WaitingAsync<PatientIdentityPayload>(PatientRecordApprovals.IdentityKind, patient.Id, ct);
            if (waiting is { } w && (w.Payload.Asked != asked || w.Payload.Identifiers.AbhaNumberIndex != scratch.AbhaNumberIndex || w.Payload.Identifiers.AadhaarIndex != scratch.AadhaarIndex))
            {
                return Error.Conflict(PatientRecordApprovals.WaitingCode,
                    $"A change to this patient's identity, asked for by {w.Request.RequestedBy}, is waiting for approval. Withdraw it in Approvals first, or wait for its decision.");
            }

            identity = waiting is not null
                ? null
                : new PatientIdentityPayload(patient.Id, before, asked,
                    new PatientIdentifiers(scratch.AbhaNumberCipher, scratch.AbhaNumberIndex, scratch.AadhaarIndex),
                    abhaNumberChanged, aadhaarChanged);
        }

        var liftRestriction = false;
        if (patient.IsVip && !input.IsVip)
        {
            liftRestriction = await records.WaitingAsync<PatientRestrictionPayload>(PatientRecordApprovals.RestrictionKind, patient.Id, ct) is null;
        }

        PatientDeceasedPayload? deceased = null;
        var deceasedAt = request.IsDeceased ? request.DeceasedAt ?? patient.DeceasedAt ?? time.GetUtcNow() : (DateTimeOffset?)null;
        if (request.IsDeceased != patient.IsDeceased || (request.IsDeceased && request.DeceasedAt is not null && request.DeceasedAt != patient.DeceasedAt))
        {
            if (!request.IsDeceased && await records.CertifiedDeathAsync(patient.Id, ct) is { } serial)
            {
                return PatientRecordApprovals.DeathCertified(serial);
            }

            var waiting = await records.WaitingAsync<PatientDeceasedPayload>(PatientRecordApprovals.DeceasedKind, patient.Id, ct);
            if (waiting is { } w && (w.Payload.Deceased != request.IsDeceased || (request.DeceasedAt is not null && w.Payload.DeceasedAt != deceasedAt)))
            {
                return Error.Conflict(PatientRecordApprovals.WaitingCode,
                    $"A change to this patient's deceased status, asked for by {w.Request.RequestedBy}, is waiting for approval. Withdraw it in Approvals first, or wait for its decision.");
            }

            deceased = waiting is null ? new PatientDeceasedPayload(patient.Id, patient.IsDeceased, patient.DeceasedAt, request.IsDeceased, deceasedAt) : null;
        }

        if ((identity is not null || liftRestriction || deceased is not null) && string.IsNullOrWhiteSpace(request.ChangeReason))
        {
            return PatientRecordApprovals.ReasonNeeded("changeReason");
        }

        return (keepIdentity, identity, liftRestriction, deceased);
    }

    /// <summary>
    /// Asks for a duplicate record to be merged into another. It cannot be undone, so it is made only when someone else
    /// who may merge records approves it, and only on the two records as they are now (<see cref="MergeApprovedAsync"/>).
    /// </summary>
    public async Task<Result<ApprovalRequestDto>> RequestMergeAsync(Guid sourceId, MergePatientRequest request, CancellationToken ct)
    {
        var records = await MergeRecordsAsync(sourceId, request.IntoPatientId, track: false, ct);
        if (records.IsFailure)
        {
            return records.Error!;
        }

        var (source, target) = records.Value;
        var today = time.Today();
        return await approvals.RequestAsync(
            PatientMergeApproval.Key,
            new ApprovalSubject(nameof(Patient), source.Id, source.Version, currentUser.FacilityId ?? source.RegisteredFacilityId, target.Id),
            new PatientMergePayload(source.Id, target.Id, target.Version, request.Reason.Trim()),
            $"Merge {source.Uhid} into {target.Uhid}",
            [new("Duplicate, merged away", Describe(source, today)), new("Record kept", Describe(target, today))],
            request.Reason,
            ct);
    }

    /// <summary>
    /// Merges an approved request's duplicate into the record it names, in the approval's transaction (the approval saves
    /// and commits, and other modules move their records on PatientsMerged in it). It is made only on the two records as
    /// they were when it was asked for: one corrected since may no longer be the same person.
    /// </summary>
    internal async Task<Result<string>> MergeApprovedAsync(ApprovalRequest approval, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<PatientMergePayload>(approval.Payload);
        var records = await MergeRecordsAsync(asked.DuplicateId, asked.IntoPatientId, track: true, ct);
        if (records.IsFailure)
        {
            return records.Error!;
        }

        var (source, target) = records.Value;
        if (source.Version != approval.SubjectVersion || target.Version != asked.IntoVersion)
        {
            return Error.Rule("approval.stale",
                $"{(source.Version != approval.SubjectVersion ? source.Uhid : target.Uhid)} was changed after this merge was asked for. Check that the two records are the same person, and ask again.");
        }

        await MergeAsync(source, target, $"{asked.Reason} (asked for by {approval.RequestedByName}, approved by {currentUser.DisplayName ?? currentUser.UserName})", ct);
        return $"{source.Uhid} merged into {target.Uhid}";
    }

    /// <summary>The duplicate and the record it would merge into, if neither is merged already.</summary>
    private async Task<Result<(Patient Source, Patient Target)>> MergeRecordsAsync(Guid sourceId, Guid targetId, bool track, CancellationToken ct)
    {
        if (sourceId == targetId)
        {
            return Error.Rule("patient.merge_self", "Choose a different record to merge into.");
        }

        var patients = track ? db.Set<Patient>() : db.Set<Patient>().AsNoTracking();
        var source = await patients.FirstOrDefaultAsync(p => p.Id == sourceId, ct);
        var target = await patients.FirstOrDefaultAsync(p => p.Id == targetId, ct);
        if (source is null || target is null)
        {
            return Error.NotFound(nameof(Patient), source is null ? sourceId : targetId);
        }

        if (source.MergedIntoPatientId is not null || target.MergedIntoPatientId is not null)
        {
            return Error.Rule("patient.already_merged", "One of these records has already been merged.");
        }

        return (source, target);
    }

    /// <summary>A record as the approver's list shows it; a VIP's name by its initials, as search shows it (they open the record to see more).</summary>
    private static string Describe(Patient patient, DateOnly today) =>
        string.Join(" · ", new[] { patient.Uhid, patient.IsVip ? PatientNames.Restricted(patient.FullName) : patient.FullName, patient.Gender.ToString(), patient.AgeDisplay(today) }
            .Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>Moves the duplicate's records to the surviving one and marks the duplicate merged; the caller saves.</summary>
    private async Task MergeAsync(Patient source, Patient target, string reason, CancellationToken ct)
    {
        var sourceId = source.Id;

        // Re-point this module's own records; other modules handle PatientsMerged in the same transaction.
        foreach (var visit in await db.Set<Visit>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(v => v.PatientId == sourceId).ToListAsync(ct))
        {
            visit.PatientId = target.Id;
        }

        foreach (var token in await db.Set<QueueToken>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(t => t.PatientId == sourceId).ToListAsync(ct))
        {
            token.PatientId = target.Id;
        }

        foreach (var consent in await db.Set<PatientConsent>().Where(c => c.PatientId == sourceId).ToListAsync(ct))
        {
            consent.PatientId = target.Id;
        }

        foreach (var document in await db.Set<PatientDocument>().Where(d => d.PatientId == sourceId).ToListAsync(ct))
        {
            document.PatientId = target.Id;
        }

        foreach (var check in await db.Set<PatientIdentityCheck>().Where(c => c.PatientId == sourceId).ToListAsync(ct))
        {
            check.PatientId = target.Id;
        }

        // The duplicate's number changes go with its record; one still waiting for its code ends (it was for the duplicate's number).
        foreach (var change in await db.Set<PatientPhoneChange>().Where(c => c.PatientId == sourceId).ToListAsync(ct))
        {
            change.Supersede();
            change.PatientId = target.Id;
        }

        await MovePortalFamilyLinksAsync(source, target, ct);

        // The duplicate's clinical record moves to the surviving one, and so do the categories that keep it confidential (R-4).
        target.SensitiveCategories |= source.SensitiveCategories;

        // The surviving record takes the duplicate's ABHA number when it has none: a link made by the merge, audited as
        // every link of a national health ID is.
        if (target.AbhaNumberCipher is null && source.AbhaNumberCipher is not null)
        {
            (target.AbhaNumberCipher, target.AbhaNumberIndex, target.AbhaNumberMasked) = (source.AbhaNumberCipher, source.AbhaNumberIndex, source.AbhaNumberMasked);
            audit.Add(new AuditEntry(AuditCategories.Data, AbhaChangedAction, $"Merged from {source.Uhid}: ABHA number {source.AbhaNumberMasked} linked")
            {
                PatientId = target.Id,
                EntityType = nameof(Patient),
                EntityId = target.Id.ToString(),
                Details = new { number = "linked", numberMasked = source.AbhaNumberMasked, previousNumberMasked = (string?)null, address = (string?)null, mergedFrom = source.Uhid },
            });
        }

        target.AadhaarLast4 ??= source.AadhaarLast4;
        target.AadhaarIndex ??= source.AadhaarIndex;
        source.MarkMergedInto(target.Id);

        audit.Add(new AuditEntry(AuditCategories.Clinical, "patient.merged", $"Merged {source.Uhid} into {target.Uhid}: {reason}")
        {
            PatientId = target.Id,
            EntityType = nameof(Patient),
            EntityId = source.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
    }

    /// <summary>
    /// Family members who saw the duplicate in the portal see the surviving record instead. A link that the surviving
    /// record already has for the same number, or one to the number the surviving record is registered with, is ended.
    /// Open portal codes for the duplicate are left to expire.
    /// </summary>
    private async Task MovePortalFamilyLinksAsync(Patient source, Patient target, CancellationToken ct)
    {
        var links = await db.Set<PortalFamilyLink>().Where(l => l.PatientId == source.Id).ToListAsync(ct);
        if (links.Count == 0)
        {
            return;
        }

        var targetNumbers = await db.Set<PortalFamilyLink>()
            .Where(l => l.PatientId == target.Id && l.RevokedAt == null)
            .Select(l => l.AccountPhone)
            .ToListAsync(ct);
        var now = time.GetUtcNow();
        foreach (var link in links)
        {
            if (link.IsActive && (targetNumbers.Contains(link.AccountPhone) || link.AccountPhone == target.Phone))
            {
                link.Revoke($"Merged into {target.Uhid}, which this number already sees.", currentUser.UserId, now);
            }

            link.PatientId = target.Id;
        }
    }

    private async Task<Error?> CheckIdentifierConflictsAsync(PatientInput input, Guid? excludeId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(input.AbhaNumber))
        {
            var indexes = protector.BlindIndexes(Masking.Normalize(input.AbhaNumber)).ToArray();
            var holder = await db.Set<Patient>().Where(p => p.AbhaNumberIndex != null && indexes.Contains(p.AbhaNumberIndex) && p.Id != excludeId)
                .Select(p => p.Uhid).FirstOrDefaultAsync(ct);
            if (holder is not null)
            {
                return Error.Conflict("patient.abha_in_use", $"This ABHA number is already linked to UHID {holder}.");
            }
        }

        if (!string.IsNullOrWhiteSpace(input.AadhaarNumber))
        {
            var index = protector.FixedBlindIndex(Masking.Normalize(input.AadhaarNumber));
            var holder = await db.Set<Patient>().Where(p => p.AadhaarIndex == index && p.Id != excludeId).Select(p => p.Uhid).FirstOrDefaultAsync(ct);
            if (holder is not null)
            {
                return Error.Conflict("patient.aadhaar_in_use", $"This Aadhaar number is already linked to UHID {holder}.");
            }
        }

        return null;
    }

    /// <summary>
    /// The answer when the save found the Aadhaar number on another record: another desk linked it after this request
    /// looked (<see cref="PatientConfiguration.OneRecordPerAadhaarNumber"/>). It names that record, as the first look does.
    /// </summary>
    private async Task<Error> AadhaarNumberTakenAsync(PatientInput input, Guid? excludeId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        return await CheckIdentifierConflictsAsync(input, excludeId, ct)
            ?? Error.Conflict("patient.aadhaar_in_use", "This Aadhaar number is already linked to another patient.");
    }

    /// <summary>
    /// A new photo must be an image in the photo category, small enough for an avatar, and not already another patient's
    /// photo. It is not linked to the patient as a document, because a document view is written to the audit trail and a
    /// photo in the banner is fetched on every screen.
    /// </summary>
    private async Task<Error?> CheckPhotoAsync(Guid? photoFileId, Guid? patientId, CancellationToken ct)
    {
        if (photoFileId is not { } id)
        {
            return null;
        }

        var file = await db.Set<StoredFile>().AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file is null)
        {
            return Error.Field("photoFileId", "The photo could not be found. Take or upload it again.");
        }

        if (PatientPhoto.Problem(file) is { } problem)
        {
            return Error.Field("photoFileId", problem);
        }

        var usedByAnother = await PatientPhoto.UsedByOthers(db, id, patientId).AnyAsync(ct);
        return usedByAnother ? Error.Field("photoFileId", "This photo already belongs to another patient. Take or upload a new one.") : null;
    }

    private void Apply(Patient patient, PatientInput input, DateOnly today, bool isNew)
    {
        ApplyIdentity(patient, input, today, isNew);
        ApplyDetails(patient, input);
        patient.IsVip = input.IsVip;
    }

    /// <summary>Who the patient is: name, sex, date of birth, ABHA number and address, Aadhaar (<see cref="PatientIdentity"/>).</summary>
    private void ApplyIdentity(Patient patient, PatientInput input, DateOnly today, bool isNew, bool auditAbha = true)
    {
        patient.SetName(input.Title, input.FirstName, input.MiddleName, input.LastName);
        patient.Gender = input.Gender;
        var dob = PatientRules.ResolveDateOfBirth(input.DateOfBirth, input.AgeYears, input.AgeMonths, today, out var estimated);
        if (dob is not null || isNew)
        {
            patient.DateOfBirth = dob;
            patient.IsDateOfBirthEstimated = estimated;
        }

        if (auditAbha)
        {
            AuditAbhaChange(patient, input, isNew);
        }

        patient.AbhaAddress = Abha.NormalizeAddress(input.AbhaAddress);

        // null = leave unchanged, "" = remove, value = replace.
        if (input.AbhaNumber is not null)
        {
            if (string.IsNullOrWhiteSpace(input.AbhaNumber))
            {
                patient.AbhaNumberCipher = patient.AbhaNumberIndex = patient.AbhaNumberMasked = null;
            }
            else
            {
                var abha = Abha.Digits(input.AbhaNumber);

                // The same number entered again is no change: it keeps its stored form (and its key).
                if (patient.AbhaNumberIndex is not { } index || !protector.BlindIndexes(abha).Contains(index))
                {
                    patient.AbhaNumberCipher = protector.Protect(abha);
                    patient.AbhaNumberIndex = protector.BlindIndex(abha);
                    patient.AbhaNumberMasked = Abha.Mask(abha);
                }
            }
        }

        if (input.AadhaarNumber is not null)
        {
            if (string.IsNullOrWhiteSpace(input.AadhaarNumber))
            {
                patient.AadhaarLast4 = patient.AadhaarIndex = null;
            }
            else
            {
                var aadhaar = Masking.Normalize(input.AadhaarNumber);
                patient.AadhaarLast4 = aadhaar[^4..];
                // Only the last four digits are kept, so the index cannot be computed again: it stays on the original key.
                patient.AadhaarIndex = protector.FixedBlindIndex(aadhaar);
            }
        }
    }

    /// <summary>Everything else on the record: contacts, address, emergency contact, social details, photo and notes.</summary>
    private void ApplyDetails(Patient patient, PatientInput input)
    {
        patient.Phone = Phone.Normalize(input.Phone);
        patient.AlternatePhone = Phone.Normalize(input.AlternatePhone);
        patient.Email = string.IsNullOrWhiteSpace(input.Email) ? null : input.Email.Trim().ToLowerInvariant();
        patient.Address = input.Address ?? new Address();
        patient.BloodGroup = input.BloodGroup ?? patient.BloodGroup;
        patient.MaritalStatus = input.MaritalStatus ?? patient.MaritalStatus;
        patient.GuardianName = Clean(input.GuardianName);
        patient.GuardianRelation = input.GuardianRelation;
        patient.EmergencyContactName = Clean(input.EmergencyContactName);
        patient.EmergencyContactPhone = Phone.Normalize(input.EmergencyContactPhone);
        patient.EmergencyContactRelation = Clean(input.EmergencyContactRelation);
        patient.Occupation = Clean(input.Occupation);
        patient.Religion = Clean(input.Religion);
        patient.Nationality = Clean(input.Nationality) ?? "Indian";
        patient.PreferredLanguage = Clean(input.PreferredLanguage);
        patient.Category = input.Category ?? patient.Category;
        patient.PhotoFileId = PatientPhoto.Resolve(patient.PhotoFileId, input.PhotoFileId, input.RemovePhoto);
        patient.Notes = Clean(input.Notes);
    }

    /// <summary>
    /// Records a change to the patient's ABHA link (the number linked, replaced or removed; the address added, changed or
    /// removed) as an audit entry of its own, so linking and unlinking a national health ID is easy to find in the trail.
    /// The number appears masked only; entering again the number already on file is not a change.
    /// </summary>
    private void AuditAbhaChange(Patient patient, PatientInput input, bool isNew)
    {
        var digits = string.IsNullOrWhiteSpace(input.AbhaNumber) ? null : Abha.Digits(input.AbhaNumber);
        var number = input.AbhaNumber switch
        {
            null => null,
            _ when digits is null => patient.AbhaNumberCipher is null ? null : "removed",
            _ when patient.AbhaNumberIndex is { } index && protector.BlindIndexes(digits).Contains(index) => null,
            _ => patient.AbhaNumberCipher is null ? "linked" : "replaced",
        };
        var newAddress = Abha.NormalizeAddress(input.AbhaAddress);
        var address = newAddress == patient.AbhaAddress ? null
            : newAddress is null ? "removed"
            : patient.AbhaAddress is null ? "added"
            : "changed";
        if (number is null && address is null)
        {
            return;
        }

        var after = digits is null ? null : Abha.Mask(digits);
        string?[] parts =
        [
            number switch
            {
                "linked" => $"ABHA number {after} linked",
                "replaced" => $"ABHA number {patient.AbhaNumberMasked} replaced by {after}",
                "removed" => $"ABHA number {patient.AbhaNumberMasked} removed",
                _ => null,
            },
            address is null ? null : $"ABHA address {address}",
        ];
        var summary = string.Join("; ", parts.OfType<string>());
        audit.Add(new AuditEntry(AuditCategories.Data, AbhaChangedAction, isNew ? $"At registration: {summary}" : summary)
        {
            PatientId = patient.Id,
            EntityType = nameof(Patient),
            EntityId = patient.Id.ToString(),
            Details = new { number, numberMasked = after, previousNumberMasked = number is null ? null : patient.AbhaNumberMasked, address },
        });
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
