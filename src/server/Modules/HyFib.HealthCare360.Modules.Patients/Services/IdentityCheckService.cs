using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Identity;
using HyFib.HealthCare360.Modules.Patients.Persistence;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// Checks a patient's identity with their Aadhaar fingerprint (SRS INT-BIO, optional patient identity verification).
/// The number the patient gives must be the one on their record, when the record has one; with a Virtual ID, which
/// cannot be compared, the record shows that one was used. A check UIDAI answered is kept, without the number, and a
/// verified one links the number to a record that had none, as registration would. Off until an administrator sets up
/// Aadhaar authentication; a training simulator stands in for UIDAI.
/// </summary>
public sealed class IdentityCheckService(
    HospitalDbContext db,
    IAadhaarAuthenticator authenticator,
    PatientAccessGuard guard,
    IFieldProtector protector,
    ICurrentUser currentUser,
    IAuditLogger audit)
{
    /// <summary>What the workstation should capture, or why identity cannot be checked here.</summary>
    public Task<AadhaarCaptureSetup> SetupAsync(CancellationToken ct) => authenticator.SetupAsync(currentUser.FacilityId, ct);

    public async Task<Result<IReadOnlyList<IdentityCheckDto>>> ListAsync(Guid patientId, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!;
        }

        await audit.PatientRecordAccessedAsync(patientId, "Viewed identity checks", ct);
        var checks = await db.Set<PatientIdentityCheck>().AsNoTracking()
            .Where(c => c.PatientId == patientId)
            .OrderByDescending(c => c.CreatedAt)
            .Take(50)
            .ToListAsync(ct);
        return checks.Select(Dto).ToList();
    }

    public async Task<Result<IdentityCheckDto>> VerifyAsync(Guid patientId, VerifyIdentityRequest request, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!;
        }

        var patient = await db.Set<Patient>().FirstAsync(p => p.Id == patientId, ct);
        if (patient.MergedIntoPatientId is not null)
        {
            return Error.Rule("patient.merged", "This record was merged into another one. Check the identity on that record.");
        }

        var number = AadhaarNumber.Normalize(request.AadhaarNumber)!;
        var virtualId = AadhaarNumber.IsVirtualId(number);
        var index = virtualId ? null : protector.FixedBlindIndex(Masking.Normalize(number));
        if (index is not null && patient.AadhaarIndex is not null && patient.AadhaarIndex != index)
        {
            return Error.Rule("patient.aadhaar_mismatch",
                $"This is not the Aadhaar number on {patient.FullName}'s record (it ends in {patient.AadhaarLast4}). Check the number with the patient.");
        }

        if (index is not null && patient.AadhaarIndex is null && await HolderAsync(index, patientId, ct) is { } holder)
        {
            return Error.Conflict("patient.aadhaar_in_use", $"This Aadhaar number is already linked to UHID {holder}.");
        }

        var result = await authenticator.AuthenticateAsync(new AadhaarFingerprintCheck(number, request.PidData, request.Consent, currentUser.FacilityId), ct);
        if (result.TransactionId is null)
        {
            // Nothing was asked of UIDAI (not set up, or the capture could not be used): there is nothing to keep.
            return Error.Rule("patient.identity_check_failed", result.Message);
        }

        var check = new PatientIdentityCheck
        {
            PatientId = patient.Id,
            FacilityId = currentUser.FacilityId,
            Method = IdentityCheckMethod.AadhaarFingerprint,
            Outcome = result.Outcome switch
            {
                AadhaarCheckOutcome.Verified => IdentityCheckOutcome.Verified,
                AadhaarCheckOutcome.NotVerified => IdentityCheckOutcome.NotVerified,
                _ => IdentityCheckOutcome.Failed,
            },
            Message = result.Message.Length <= 400 ? result.Message : result.Message[..400],
            AadhaarLast4 = AadhaarNumber.Last4(number),
            UsedVirtualId = virtualId,
            Provider = result.Provider,
            ErrorCode = result.ErrorCode,
            TransactionId = result.TransactionId,
            ResponseCode = result.ResponseCode,
            DeviceProviderId = result.DeviceProviderId,
            DeviceModelId = result.DeviceModelId,
            CheckedByName = currentUser.DisplayName ?? currentUser.UserName,
        };
        db.Add(check);
        audit.Add(new AuditEntry(AuditCategories.Clinical, "patient.identity_checked",
            $"Checked {patient.Uhid}'s identity with an Aadhaar fingerprint ({(virtualId ? "Virtual ID" : "Aadhaar number")} ending {check.AadhaarLast4}): {check.Outcome}")
        {
            PatientId = patient.Id,
            EntityType = nameof(PatientIdentityCheck),
            EntityId = check.Id.ToString(),
            Severity = check.Outcome == IdentityCheckOutcome.NotVerified ? AuditSeverity.Warning : AuditSeverity.Info,
        });
        await db.SaveChangesAsync(ct);

        // UIDAI's answer is kept whatever happens next. The number goes on the record unless another desk has linked it to
        // another record while UIDAI answered: the database keeps one record per Aadhaar number.
        if (check.Outcome == IdentityCheckOutcome.Verified && index is not null && patient.AadhaarIndex is null)
        {
            patient.AadhaarLast4 = check.AadhaarLast4;
            patient.AadhaarIndex = index;
            check.LinkedAadhaar = true;
            audit.Add(new AuditEntry(AuditCategories.Clinical, "patient.aadhaar_linked",
                $"Linked the Aadhaar number ending {check.AadhaarLast4} to {patient.Uhid}'s record after a verified fingerprint check")
            {
                PatientId = patient.Id,
                EntityType = nameof(Patient),
                EntityId = patient.Id.ToString(),
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (PatientConfiguration.AadhaarNumberTaken(ex))
            {
                db.ChangeTracker.Clear();
                var other = await HolderAsync(index, patientId, ct);
                return Error.Conflict("patient.aadhaar_in_use",
                    $"The check is kept, but the Aadhaar number was not linked: it was linked to {(other is null ? "another record" : $"UHID {other}")} a moment ago.");
            }
        }

        return Dto(check);
    }

    /// <summary>The UHID of another record that holds the Aadhaar number, if one does.</summary>
    private Task<string?> HolderAsync(string index, Guid patientId, CancellationToken ct) =>
        db.Set<Patient>().Where(p => p.AadhaarIndex == index && p.Id != patientId).Select(p => p.Uhid).FirstOrDefaultAsync(ct);

    private static IdentityCheckDto Dto(PatientIdentityCheck c) => new(
        c.Id, c.Method, c.Outcome, c.Message, c.AadhaarLast4, c.UsedVirtualId, c.LinkedAadhaar, c.Provider, c.ErrorCode, c.TransactionId,
        c.ResponseCode, c.DeviceProviderId, c.DeviceModelId, c.CreatedAt, c.CheckedByName);
}
