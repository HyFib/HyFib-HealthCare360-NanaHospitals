using System.Globalization;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// Who the patient is, as the record says: what identifies them on every wristband, label, report, bill, ABDM record and
/// FHIR resource. The national identifiers are kept as the record keeps them: the ABHA number masked (and, in
/// <see cref="PatientIdentifiers"/>, encrypted with its blind index), Aadhaar by its last four digits.
/// </summary>
public sealed record PatientIdentity(
    string? Title, string FirstName, string? MiddleName, string? LastName, Gender Gender, DateOnly? DateOfBirth, bool IsDateOfBirthEstimated,
    string? AbhaNumberMasked, string? AbhaAddress, string? AadhaarLast4)
{
    public static PatientIdentity Of(Patient patient)
    {
        ArgumentNullException.ThrowIfNull(patient);
        return new(patient.Title, patient.FirstName, patient.MiddleName, patient.LastName, patient.Gender, patient.DateOfBirth, patient.IsDateOfBirthEstimated,
            patient.AbhaNumberMasked, patient.AbhaAddress, patient.AadhaarLast4);
    }

    public string FullName => Patient.ComposeFullName(FirstName, MiddleName, LastName);

    /// <summary>
    /// What changes from <paramref name="from"/> to <paramref name="to"/>, one phrase each ("name Ravi Kumar → Ravi
    /// Kumaar"); a restricted (VIP) record's names by their initials, as search shows them.
    /// </summary>
    public static IReadOnlyList<(string Label, string Change)> Changes(PatientIdentity from, PatientIdentity to, bool restricted)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        List<(string, string)> changes = [];
        if (from.FullName != to.FullName || from.Title != to.Title)
        {
            changes.Add(("Name", $"{Name(from, restricted)} → {Name(to, restricted)}"));
        }

        if (from.Gender != to.Gender)
        {
            changes.Add(("Sex", $"{from.Gender} → {to.Gender}"));
        }

        if (from.DateOfBirth != to.DateOfBirth || from.IsDateOfBirthEstimated != to.IsDateOfBirthEstimated)
        {
            changes.Add(("Date of birth", $"{Birth(from)} → {Birth(to)}"));
        }

        if (from.AbhaNumberMasked != to.AbhaNumberMasked)
        {
            changes.Add(("ABHA number", $"{from.AbhaNumberMasked ?? "none"} → {to.AbhaNumberMasked ?? "none"}"));
        }

        if (from.AbhaAddress != to.AbhaAddress)
        {
            changes.Add(("ABHA address", $"{from.AbhaAddress ?? "none"} → {to.AbhaAddress ?? "none"}"));
        }

        if (from.AadhaarLast4 != to.AadhaarLast4)
        {
            changes.Add(("Aadhaar", $"{Aadhaar(from.AadhaarLast4)} → {Aadhaar(to.AadhaarLast4)}"));
        }

        return changes;
    }

    /// <summary>The changes in one line, for the request's summary and the audit trail; "unchanged" when there are none.</summary>
    public static string Describe(PatientIdentity from, PatientIdentity to, bool restricted)
    {
        var changes = Changes(from, to, restricted);
        return changes.Count == 0 ? "unchanged" : string.Join("; ", changes.Select(c => $"{c.Label.ToLowerInvariant()} {c.Change}"));
    }

    private static string Name(PatientIdentity identity, bool restricted)
    {
        var full = identity.FullName;
        if (restricted)
        {
            return PatientNames.Restricted(full);
        }

        return string.IsNullOrWhiteSpace(identity.Title) ? full : $"{identity.Title} {full}";
    }

    private static string Birth(PatientIdentity identity) => identity.DateOfBirth is { } dob
        ? dob.ToString("d MMM yyyy", CultureInfo.InvariantCulture) + (identity.IsDateOfBirthEstimated ? " (estimated from the age)" : string.Empty)
        : "not recorded";

    private static string Aadhaar(string? last4) => last4 is null ? "none" : $"ending {last4}";
}

/// <summary>The stored (encrypted or indexed) form of the national identifiers asked for; never the numbers themselves.</summary>
public sealed record PatientIdentifiers(string? AbhaNumberCipher, string? AbhaNumberIndex, string? AadhaarIndex);

/// <summary>An identity change as asked, the identity it replaces, and the identifiers it sets (when they change).</summary>
public sealed record PatientIdentityPayload(
    Guid PatientId, PatientIdentity Before, PatientIdentity Asked, PatientIdentifiers Identifiers, bool AbhaNumberChanged, bool AadhaarChanged);

/// <summary>A patient's deceased status as asked, and the status it replaces.</summary>
public sealed record PatientDeceasedPayload(Guid PatientId, bool WasDeceased, DateTimeOffset? WasDeceasedAt, bool Deceased, DateTimeOffset? DeceasedAt);

/// <summary>A restricted (VIP) record to be opened like any other.</summary>
public sealed record PatientRestrictionPayload(Guid PatientId);

/// <summary>Sensitive categories to take a record out of.</summary>
public sealed record SensitiveRemovalPayload(Guid PatientId, SensitiveCategory Remove);

/// <summary>A change to a patient's record waiting for a second person: the request, its kind, what it does in words, who asked and when.</summary>
public sealed record PendingPatientChange(Guid ApprovalId, string Kind, string Title, string Summary, string RequestedBy, DateTimeOffset RequestedAt);

/// <summary>
/// Whether a desk may still correct a patient's identity without a second person: the one who registered the patient,
/// within <see cref="Window"/> of entering the record, before the patient has been seen (no visit begun, nobody
/// admitted). A name misspelt or a date of birth mistyped at the counter is then corrected at once, before anything
/// clinical, any report or any claim carries it. After that, a changed identity could put one person's record under
/// another's name, so it waits (MAKER-CHECKER.md, slice 7).
/// </summary>
public static class IdentityCorrection
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>Until when the record's identity may be corrected at once by <paramref name="userId"/>; null when it may not.</summary>
    public static DateTimeOffset? OpenUntil(DateTimeOffset enteredAt, Guid? registeredBy, Guid? userId, bool seen, DateTimeOffset now)
    {
        if (seen || userId is null || registeredBy != userId)
        {
            return null;
        }

        var until = enteredAt + Window;
        return now < until ? until : null;
    }
}

/// <summary>
/// The patient's record: the changes that need a second person (maker-checker, slice 7). Changing an existing patient's
/// identity (name, sex, date of birth, ABHA number or address, Aadhaar) after the correction window
/// (<see cref="IdentityCorrection"/>), lifting a VIP restriction, marking a patient deceased from the registration screen
/// or taking the mark back, and taking a record out of a sensitive category each wait: the one who asks gives a reason,
/// the record keeps its old values everywhere meanwhile (banners, wristbands, ABDM and FHIR, the break-the-glass rules),
/// and someone else who holds the kind's permission approves, seeing the change in words. Each is made only over the
/// record as it was asked (<c>approval.stale</c>), audited with both names. The safer direction (a restriction or a
/// category added, a death recorded by its certificate or an expired discharge) is made at once, as before.
/// </summary>
public sealed class PatientRecordApprovals(
    HospitalDbContext db,
    ICurrentUser current,
    IAuditLogger audit,
    IApprovals approvals,
    IEnumerable<IDeathCertificateLookup> deathCertificates,
    TimeProvider time)
{
    public const string IdentityKind = "patients.identity";
    public const string RestrictionKind = "patients.restriction_lift";
    public const string DeceasedKind = "patients.deceased";
    public const string SensitiveKind = "patients.sensitive_remove";

    /// <summary>Refused: the patient's death is certified, so the certificate, not the registration screen, decides it.</summary>
    public const string DeathCertifiedCode = "patient.death_certified";

    /// <summary>Refused: another change of this kind is waiting (the approvals' own code).</summary>
    public const string WaitingCode = "approval.already_requested";

    /// <summary>
    /// What a sensitive category's removal is asked about. Not the patient: the record's history and its list of what waits
    /// open to everyone who serves the patient, and the categories, and each change of them, are kept from all but those who
    /// manage them (<see cref="PatientRecordHistory"/>).
    /// </summary>
    public const string SensitiveSubject = "PatientSensitiveCategories";

    /// <summary>The kinds asked about the patient's record itself, which its read shows waiting.</summary>
    public static readonly IReadOnlyList<string> Kinds = [IdentityKind, RestrictionKind, DeceasedKind];

    /// <summary>The changes waiting for approval on this patient's record, oldest first (a sensitive category's removal is not among them).</summary>
    public async Task<IReadOnlyList<PendingPatientChange>> PendingAsync(Guid patientId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.SubjectType == nameof(Patient) && r.SubjectId == patientId && Kinds.Contains(r.Kind) && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderBy(r => r.RequestedAt)
            .Select(r => new PendingPatientChange(r.Id, r.Kind, r.Title, r.Summary, r.RequestedByName, r.RequestedAt))
            .ToListAsync(ct);
    }

    /// <summary>The change of <paramref name="kind"/> waiting on this patient, if any: the request and its payload.</summary>
    public async Task<(PendingPatientChange Request, T Payload)?> WaitingAsync<T>(string kind, Guid patientId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var subject = kind == SensitiveKind ? SensitiveSubject : nameof(Patient);
        var request = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == kind && r.SubjectType == subject && r.SubjectId == patientId && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderByDescending(r => r.RequestedAt)
            .Select(r => new { Pending = new PendingPatientChange(r.Id, r.Kind, r.Title, r.Summary, r.RequestedByName, r.RequestedAt), r.Payload })
            .FirstOrDefaultAsync(ct);
        return request is null ? null : (request.Pending, ApprovalPayload.Read<T>(request.Payload));
    }

    /// <summary>
    /// Until when <see cref="ICurrentUser"/> may correct this patient's identity at once (<see cref="IdentityCorrection"/>):
    /// they registered the patient within the window and no visit has begun.
    /// </summary>
    public async Task<DateTimeOffset?> CorrectableUntilAsync(Patient patient, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(patient);
        var now = time.GetUtcNow();
        if (IdentityCorrection.OpenUntil(patient.CreatedAt, patient.CreatedBy, current.UserId, seen: false, now) is null)
        {
            return null;
        }

        var seen = await db.Set<Visit>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .AnyAsync(v => v.PatientId == patient.Id && v.Status != VisitStatus.Open && v.Status != VisitStatus.Cancelled, ct);
        return IdentityCorrection.OpenUntil(patient.CreatedAt, patient.CreatedBy, current.UserId, seen, now);
    }

    /// <summary>The serial number of a death certificate (not entered in error) on this patient, if there is one.</summary>
    public async Task<string?> CertifiedDeathAsync(Guid patientId, CancellationToken ct)
    {
        foreach (var lookup in deathCertificates)
        {
            if (await lookup.CertifiedDeathAsync(patientId, ct) is { } serial)
            {
                return serial;
            }
        }

        return null;
    }

    public static Error DeathCertified(string serial) => Error.Rule(DeathCertifiedCode,
        $"This patient's death is certified (certificate {serial}). The certificate decides it: if it was made on the wrong patient, mark it entered in error in Medical Records.");

    public static Error ReasonNeeded(string field) => Error.Field(field, "Say why: this change goes to someone else for approval, and they read your reason.");

    // ------------------------------------------------------------------ asking (in the caller's transaction, once the rest is saved)

    public Task<Result<ApprovalRequestDto>> AskIdentityAsync(Patient patient, PatientIdentityPayload payload, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(patient);
        ArgumentNullException.ThrowIfNull(payload);
        var restricted = patient.IsVip;
        List<ApprovalDetail> details = [new("Patient", Label(patient))];
        details.AddRange(PatientIdentity.Changes(payload.Before, payload.Asked, restricted).Select(c => new ApprovalDetail(c.Label, c.Change)));
        if (payload.AbhaNumberChanged && payload.Before.AbhaNumberMasked == payload.Asked.AbhaNumberMasked)
        {
            details.Add(new("ABHA number", $"{payload.Before.AbhaNumberMasked} → another number ending in the same digits"));
        }

        if (payload.AadhaarChanged && payload.Before.AadhaarLast4 == payload.Asked.AadhaarLast4)
        {
            details.Add(new("Aadhaar", $"ending {payload.Before.AadhaarLast4} → another number ending in the same digits"));
        }

        details.Add(new("What it reaches",
            "The name, sex and age on every wristband, label, report, prescription and bill printed from now on; what ABDM and FHIR are told; how the desk finds the patient. Until it is approved, the record keeps its present identity everywhere."));
        return AskAsync(IdentityKind, nameof(Patient), patient, payload, $"Identity of {patient.Uhid}: {PatientIdentity.Describe(payload.Before, payload.Asked, restricted)}", details, reason, ct);
    }

    public Task<Result<ApprovalRequestDto>> AskRestrictionLiftAsync(Patient patient, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(patient);
        return AskAsync(RestrictionKind, nameof(Patient), patient, new PatientRestrictionPayload(patient.Id), $"Lift the VIP restriction on {patient.Uhid}",
        [
            new("Patient", Label(patient)),
            new("Restriction", "Restricted (VIP) → open like any other record"),
            new("What it reaches",
                "Anyone who serves patients may then open the record and see the name in search without the break-the-glass reason and its alert. Until it is approved, the record stays restricted."),
        ], reason, ct);
    }

    public Task<Result<ApprovalRequestDto>> AskDeceasedAsync(Patient patient, PatientDeceasedPayload payload, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(patient);
        ArgumentNullException.ThrowIfNull(payload);
        var words = Deceased(payload.WasDeceased, payload.WasDeceasedAt) + " → " + Deceased(payload.Deceased, payload.DeceasedAt);
        return AskAsync(DeceasedKind, nameof(Patient), patient, payload, payload.Deceased && !payload.WasDeceased ? $"Mark {patient.Uhid} deceased" : $"Deceased status of {patient.Uhid}: {words}",
        [
            new("Patient", Label(patient)),
            new("Status", words),
            new("What it reaches", payload.Deceased
                ? "The desk stops booking, admitting and messaging the patient, and the record shows them deceased. No death certificate is made: a doctor certifies the death in Medical Records."
                : "The record shows the patient alive again: the desk books, admits and messages them. Only a death with no certificate is taken back this way."),
        ], reason, ct);
    }

    public Task<Result<ApprovalRequestDto>> AskSensitiveRemovalAsync(Patient patient, SensitiveCategory remove, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(patient);
        return AskAsync(SensitiveKind, SensitiveSubject, patient, new SensitiveRemovalPayload(patient.Id, remove), $"Take {patient.Uhid} out of: {SensitiveCategories.Describe(remove)}",
        [
            new("Patient", Label(patient)),
            new("Categories taken out", SensitiveCategories.Describe(remove)),
            new("What it reaches",
                "The clinical record then opens to everyone who may open charts, without that category's permission, and is shared as any other (portal, ABDM, scoped sharing). Until it is approved, the record keeps the category."),
        ], reason, ct);
    }

    private async Task<Result<ApprovalRequestDto>> AskAsync<T>(
        string kind, string subject, Patient patient, T payload, string summary, IReadOnlyList<ApprovalDetail> details, string reason, CancellationToken ct) =>
        await approvals.RequestAsync(
            kind,
            new ApprovalSubject(subject, patient.Id, patient.Version, current.FacilityId ?? patient.RegisteredFacilityId, patient.Id),
            payload,
            summary,
            details,
            reason,
            ct);

    // ------------------------------------------------------------------ applying (in the approval's transaction)

    internal async Task<Result<string>> ApplyIdentityAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<PatientIdentityPayload>(request.Payload);
        var patient = await PatientAsync(asked.PatientId, ct);
        if (patient.IsFailure)
        {
            return patient.Error!;
        }

        var p = patient.Value;
        var now = PatientIdentity.Of(p);
        if (now != asked.Before)
        {
            return Error.Rule("approval.stale",
                $"{p.Uhid}'s identity changed after this was asked for ({PatientIdentity.Describe(asked.Before, now, p.IsVip)}), so nothing was changed. Ask again.");
        }

        // Asked for when nobody else had the number; checked again now, as the desk checks it.
        if (asked.AbhaNumberChanged && asked.Identifiers.AbhaNumberIndex is { } abha
            && await db.Set<Patient>().Where(x => x.Id != p.Id && x.AbhaNumberIndex == abha).Select(x => x.Uhid).FirstOrDefaultAsync(ct) is { } abhaHolder)
        {
            return Error.Rule("patient.abha_in_use", $"This ABHA number has since been linked to UHID {abhaHolder}, so nothing was changed.");
        }

        if (asked.AadhaarChanged && asked.Identifiers.AadhaarIndex is { } aadhaar
            && await db.Set<Patient>().Where(x => x.Id != p.Id && x.AadhaarIndex == aadhaar).Select(x => x.Uhid).FirstOrDefaultAsync(ct) is { } aadhaarHolder)
        {
            return Error.Rule("patient.aadhaar_in_use", $"This Aadhaar number has since been linked to UHID {aadhaarHolder}, so nothing was changed.");
        }

        var a = asked.Asked;
        p.SetName(a.Title, a.FirstName, a.MiddleName, a.LastName);
        p.Gender = a.Gender;
        (p.DateOfBirth, p.IsDateOfBirthEstimated) = (a.DateOfBirth, a.IsDateOfBirthEstimated);
        p.AbhaAddress = a.AbhaAddress;
        if (asked.AbhaNumberChanged)
        {
            (p.AbhaNumberCipher, p.AbhaNumberIndex, p.AbhaNumberMasked) = (asked.Identifiers.AbhaNumberCipher, asked.Identifiers.AbhaNumberIndex, a.AbhaNumberMasked);
        }

        if (asked.AadhaarChanged)
        {
            (p.AadhaarLast4, p.AadhaarIndex) = (a.AadhaarLast4, asked.Identifiers.AadhaarIndex);
        }

        var words = PatientIdentity.Describe(asked.Before, a, p.IsVip);
        if (asked.Before.AbhaNumberMasked != a.AbhaNumberMasked || asked.Before.AbhaAddress != a.AbhaAddress)
        {
            // A national health ID linked, replaced or removed is found in the trail as every such change is.
            audit.Add(new AuditEntry(AuditCategories.Data, PatientRegistrationService.AbhaChangedAction,
                $"ABHA changed with approval: {string.Join("; ", PatientIdentity.Changes(asked.Before, a, p.IsVip).Where(c => c.Label.StartsWith("ABHA", StringComparison.Ordinal)).Select(c => $"{c.Label} {c.Change}"))}")
            {
                PatientId = p.Id,
                EntityType = nameof(Patient),
                EntityId = p.Id.ToString(),
                Details = new { numberMasked = a.AbhaNumberMasked, previousNumberMasked = asked.Before.AbhaNumberMasked, address = a.AbhaAddress, approval = request.Id },
            });
        }

        Audited(request, p, "patient.identity_changed", $"Identity of {p.Uhid}: {words}", new { before = asked.Before, after = a });
        return words;
    }

    internal async Task<Result<string>> ApplyRestrictionLiftAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<PatientRestrictionPayload>(request.Payload);
        var patient = await PatientAsync(asked.PatientId, ct);
        if (patient.IsFailure)
        {
            return patient.Error!;
        }

        var p = patient.Value;
        if (!p.IsVip)
        {
            return Error.Rule("approval.stale", $"{p.Uhid} is no longer restricted, so nothing was changed.");
        }

        p.IsVip = false;
        Audited(request, p, "patient.restriction_lifted", $"VIP restriction lifted from {p.Uhid}", new { restricted = false });
        return "the record is no longer restricted";
    }

    internal async Task<Result<string>> ApplyDeceasedAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<PatientDeceasedPayload>(request.Payload);
        var patient = await PatientAsync(asked.PatientId, ct);
        if (patient.IsFailure)
        {
            return patient.Error!;
        }

        var p = patient.Value;
        if (p.IsDeceased != asked.WasDeceased || p.DeceasedAt != asked.WasDeceasedAt)
        {
            return Error.Rule("approval.stale",
                $"{p.Uhid}'s deceased status changed after this was asked for (now: {Deceased(p.IsDeceased, p.DeceasedAt)}), so nothing was changed. Ask again.");
        }

        // A death certified since it was asked: the certificate decides, not this request.
        if (!asked.Deceased && await CertifiedDeathAsync(p.Id, ct) is { } serial)
        {
            return DeathCertified(serial);
        }

        (p.IsDeceased, p.DeceasedAt) = (asked.Deceased, asked.Deceased ? asked.DeceasedAt : null);
        var words = $"{Deceased(asked.WasDeceased, asked.WasDeceasedAt)} → {Deceased(p.IsDeceased, p.DeceasedAt)}";
        Audited(request, p, asked.Deceased ? "patient.deceased" : "patient.deceased_cleared", $"Deceased status of {p.Uhid}: {words}",
            new { deceased = p.IsDeceased, deceasedAt = p.DeceasedAt, wasDeceased = asked.WasDeceased, wasDeceasedAt = asked.WasDeceasedAt });
        return words;
    }

    internal async Task<Result<string>> ApplySensitiveRemovalAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<SensitiveRemovalPayload>(request.Payload);
        var patient = await PatientAsync(asked.PatientId, ct);
        if (patient.IsFailure)
        {
            return patient.Error!;
        }

        var p = patient.Value;
        if ((p.SensitiveCategories & asked.Remove) != asked.Remove)
        {
            return Error.Rule("approval.stale", $"{p.Uhid} is no longer in every category this asked to take it out of, so nothing was changed.");
        }

        var before = p.SensitiveCategories;
        p.SensitiveCategories &= ~asked.Remove;
        audit.Add(new AuditEntry(AuditCategories.Security, "patient.sensitive_changed",
            Fit($"Sensitive categories changed from \"{Categories(before)}\" to \"{Categories(p.SensitiveCategories)}\": {request.Reason} (asked for by {request.RequestedByName}, approved by {Approver})", 1000))
        {
            PatientId = p.Id,
            EntityType = nameof(Patient),
            EntityId = p.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { removed = SensitiveCategories.Describe(asked.Remove), approval = request.Id, requestedBy = request.RequestedBy, approvedBy = current.UserId },
        });
        return $"taken out of {SensitiveCategories.Describe(asked.Remove)}";
    }

    private async Task<Result<Patient>> PatientAsync(Guid id, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (patient is null)
        {
            return Error.Rule("patient.gone", "The patient no longer exists, so nothing was changed.");
        }

        return patient.MergedIntoPatientId is null
            ? patient
            : Error.Rule("patient.merged", $"{patient.Uhid} was merged into another record after this was asked for, so nothing was changed.");
    }

    private void Audited(ApprovalRequest request, Patient patient, string action, string summary, object details) =>
        audit.Add(new AuditEntry(AuditCategories.Data, action, Fit($"{summary}: {request.Reason} (asked for by {request.RequestedByName}, approved by {Approver})", 1000))
        {
            PatientId = patient.Id,
            EntityType = nameof(Patient),
            EntityId = patient.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { change = details, approval = request.Id, requestedBy = request.RequestedBy, approvedBy = current.UserId },
        });

    private string Approver => current.DisplayName ?? current.UserName ?? "a second person";

    /// <summary>A record as the approver's list shows it; a VIP's name by its initials, as search shows it.</summary>
    private static string Label(Patient patient) => $"{patient.Uhid} · {(patient.IsVip ? PatientNames.Restricted(patient.FullName) : patient.FullName)}";

    internal static string Deceased(bool deceased, DateTimeOffset? at) =>
        !deceased ? "alive" : at is { } when ? $"deceased ({when.ToOffset(TimeSpan.FromHours(5.5)).ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture)})" : "deceased";

    private static string Categories(SensitiveCategory categories) => categories == SensitiveCategory.None ? "none" : SensitiveCategories.Describe(categories);

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>Whether a patient's death is certified: the Clinical module's death register answers.</summary>
public interface IDeathCertificateLookup
{
    /// <summary>The serial number of a death certificate on this patient that is not entered in error, or null.</summary>
    Task<string?> CertifiedDeathAsync(Guid patientId, CancellationToken ct);
}

/// <summary>A patient's identity changed after the correction window: one person asks, another who may approve identity changes decides.</summary>
internal sealed class PatientIdentityApproval(PatientRecordApprovals records) : IApprovalAction
{
    public string Kind => PatientRecordApprovals.IdentityKind;

    public string Title => "Patient identity change";

    public string ApprovePermission => PatientsPermissions.IdentityApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => records.ApplyIdentityAsync(request, ct);
}

/// <summary>A VIP restriction lifted: decided by someone who may open restricted records.</summary>
internal sealed class PatientRestrictionLiftApproval(PatientRecordApprovals records) : IApprovalAction
{
    public string Kind => PatientRecordApprovals.RestrictionKind;

    public string Title => "VIP restriction lifted";

    public string ApprovePermission => PatientsPermissions.ReadRestricted;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => records.ApplyRestrictionLiftAsync(request, ct);
}

/// <summary>A patient marked deceased from the registration screen, or the mark taken back: decided by someone who may approve identity changes.</summary>
internal sealed class PatientDeceasedApproval(PatientRecordApprovals records) : IApprovalAction
{
    public string Kind => PatientRecordApprovals.DeceasedKind;

    public string Title => "Patient deceased status";

    public string ApprovePermission => PatientsPermissions.IdentityApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => records.ApplyDeceasedAsync(request, ct);
}

/// <summary>A record taken out of a sensitive category: decided by another doctor who manages the categories.</summary>
internal sealed class SensitiveCategoryRemovalApproval(PatientRecordApprovals records) : IApprovalAction
{
    public string Kind => PatientRecordApprovals.SensitiveKind;

    public string Title => "Sensitive category removed";

    public string ApprovePermission => PatientsPermissions.SensitiveManage;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => records.ApplySensitiveRemovalAsync(request, ct);
}
