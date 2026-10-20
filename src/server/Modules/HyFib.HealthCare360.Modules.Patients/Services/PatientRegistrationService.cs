using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;

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
/// encryption of national identifiers, updates with optimistic concurrency, and duplicate merging.
/// </summary>
public sealed class PatientRegistrationService(
    HospitalDbContext db,
    INumberSeries numbers,
    IFieldProtector protector,
    ICurrentUser currentUser,
    IAuditLogger audit,
    WhatsAppConsent whatsApp,
    TimeProvider time)
{
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
            .Select(p => new { p.Id, p.Uhid, p.FullName, p.FirstName, p.Gender, p.DateOfBirth, p.Phone, p.AbhaNumberIndex, p.AadhaarIndex })
            .ToListAsync(ct);

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
                return new DuplicateCandidate(p.Id, p.Uhid, p.FullName, p.Gender, p.DateOfBirth is { } d ? Age.Format(d, today) : null,
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

        var patient = new Patient { RegisteredFacilityId = facilityId, RegisteredAt = time.GetUtcNow() };
        Apply(patient, input, today, isNew: true);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        patient.Uhid = await numbers.NextAsync(PatientsNumberSeries.Uhid, null, ct);
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
        if (request.WhatsAppOptIn)
        {
            // Recorded for the number, as WhatsApp's own opt-in is; a STOP the number sent earlier stays in force.
            await whatsApp.OptInAsync(patient.Phone, patient.Id, WhatsAppOptInSource.RegistrationDesk, facilityId, byNumberHolder: false, ct);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new PatientCreated(patient.Id, patient.Uhid);
    }

    public async Task<Result> UpdateAsync(Guid id, UpdatePatientRequest request, CancellationToken ct)
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

        db.SetExpectedVersion(patient, request.Version);
        Apply(patient, input, time.Today(), isNew: false);
        patient.IsDeceased = request.IsDeceased;
        patient.DeceasedAt = request.IsDeceased ? request.DeceasedAt ?? patient.DeceasedAt ?? time.GetUtcNow() : null;
        patient.IsActive = request.IsActive;

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> MergeAsync(Guid sourceId, MergePatientRequest request, CancellationToken ct)
    {
        if (sourceId == request.IntoPatientId)
        {
            return Error.Rule("patient.merge_self", "Choose a different record to merge into.");
        }

        var source = await db.Set<Patient>().FirstOrDefaultAsync(p => p.Id == sourceId, ct);
        var target = await db.Set<Patient>().FirstOrDefaultAsync(p => p.Id == request.IntoPatientId, ct);
        if (source is null || target is null)
        {
            return Error.NotFound(nameof(Patient), source is null ? sourceId : request.IntoPatientId);
        }

        if (source.MergedIntoPatientId is not null || target.MergedIntoPatientId is not null)
        {
            return Error.Rule("patient.already_merged", "One of these records has already been merged.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

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

        await MovePortalFamilyLinksAsync(source, target, ct);

        target.AbhaNumberCipher ??= source.AbhaNumberCipher;
        target.AbhaNumberIndex ??= source.AbhaNumberIndex;
        target.AbhaNumberMasked ??= source.AbhaNumberMasked;
        target.AadhaarLast4 ??= source.AadhaarLast4;
        target.AadhaarIndex ??= source.AadhaarIndex;
        source.MarkMergedInto(target.Id);

        audit.Add(new AuditEntry(AuditCategories.Clinical, "patient.merged", $"Merged {source.Uhid} into {target.Uhid}: {request.Reason}")
        {
            PatientId = target.Id,
            EntityType = nameof(Patient),
            EntityId = source.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Success();
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
        patient.SetName(input.Title, input.FirstName, input.MiddleName, input.LastName);
        patient.Gender = input.Gender;
        var dob = PatientRules.ResolveDateOfBirth(input.DateOfBirth, input.AgeYears, input.AgeMonths, today, out var estimated);
        if (dob is not null || isNew)
        {
            patient.DateOfBirth = dob;
            patient.IsDateOfBirthEstimated = estimated;
        }

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
        patient.AbhaAddress = Clean(input.AbhaAddress)?.ToLowerInvariant();
        patient.PhotoFileId = PatientPhoto.Resolve(patient.PhotoFileId, input.PhotoFileId, input.RemovePhoto);
        patient.IsVip = input.IsVip;
        patient.Notes = Clean(input.Notes);

        // null = leave unchanged, "" = remove, value = replace.
        if (input.AbhaNumber is not null)
        {
            if (string.IsNullOrWhiteSpace(input.AbhaNumber))
            {
                patient.AbhaNumberCipher = patient.AbhaNumberIndex = patient.AbhaNumberMasked = null;
            }
            else
            {
                var abha = Masking.Normalize(input.AbhaNumber);
                patient.AbhaNumberCipher = protector.Protect(abha);
                patient.AbhaNumberIndex = protector.BlindIndex(abha);
                patient.AbhaNumberMasked = "XX-XXXX-XXXX-" + abha[^4..];
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

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
