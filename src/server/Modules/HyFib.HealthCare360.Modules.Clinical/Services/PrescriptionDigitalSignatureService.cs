using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.WindowsInterop.DigitalSignatures;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// The facility setting <c>clinical.prescriptions.signature</c>: <c>"off"</c> (the default, so nothing changes on an
/// upgrade), <c>"optional"</c> or <c>"required"</c>. Anything else reads as off.
/// </summary>
public sealed class PrescriptionSignatureSettings(ISettingsService settings)
{
    public const string Key = "clinical.prescriptions.signature";

    public async Task<PrescriptionSignaturePolicy> PolicyAsync(Guid facilityId, CancellationToken ct)
    {
        try
        {
            return Parse(await settings.GetAsync<JsonElement?>(Key, facilityId, ct));
        }
        catch (JsonException)
        {
            return PrescriptionSignaturePolicy.Off;
        }
    }

    public static PrescriptionSignaturePolicy Parse(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } text
            ? text.GetString()?.Trim().ToUpperInvariant() switch
            {
                "OPTIONAL" => PrescriptionSignaturePolicy.Optional,
                "REQUIRED" => PrescriptionSignaturePolicy.Required,
                _ => PrescriptionSignaturePolicy.Off,
            }
            : PrescriptionSignaturePolicy.Off;

    /// <summary>
    /// Why the prescription cannot be printed, downloaded or shared as the final copy: under <c>"required"</c>, until
    /// the doctor has signed it with their DSC. Null when it can.
    /// </summary>
    public async Task<Error?> FinalCopyProblemAsync(Prescription prescription, CancellationToken ct) =>
        !prescription.IsDigitallySigned && await PolicyAsync(prescription.FacilityId, ct) == PrescriptionSignaturePolicy.Required
            ? Error.Rule("prescription.digital_signature_required",
                "This hospital issues prescriptions only once the doctor has signed them with their DSC. Sign it with your DSC in HC360 Desktop.")
            : null;
}

/// <summary>
/// Signing a prescription with the doctor's Digital Signature Certificate (ERX-04, IT Act 2000). The doctor signs a
/// prescription they have already signed in HC360: HC360 Desktop fetches its canonical bytes
/// (<see cref="PrescriptionCanonicalForm"/>), signs them with a certificate the doctor picks from the Windows store
/// (the DSC token's CSP/KSP asks for its PIN), and sends back a detached CMS/PKCS#7 signature. The server rebuilds the
/// bytes itself, checks the signature against them (<see cref="DetachedCms.Verify"/>) and keeps the signature, the bytes
/// and the certificate. A signed prescription never changes, so the signature stays valid; every later check verifies
/// the signature again and rebuilds the bytes from the stored prescription to catch a record changed outside HC360.
/// </summary>
public sealed class PrescriptionDigitalSignatureService(
    HospitalDbContext db,
    PrescriptionService prescriptions,
    PrescriptionSignatureSettings settings,
    ICurrentUser currentUser,
    IAuditLogger audit,
    TimeProvider time)
{
    /// <summary>How long before the server receives it a signature may have been made (choosing the certificate, the PIN).</summary>
    public static readonly TimeSpan MaxSigningAge = TimeSpan.FromMinutes(15);

    /// <summary>How far ahead of the server's clock a workstation's clock may be.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    public async Task<Result<PrescriptionSigningContent>> ContentAsync(Guid id, CancellationToken ct)
    {
        var ready = await ReadyToSignAsync(id, ct);
        if (ready.IsFailure)
        {
            return ready.Error!;
        }

        var (p, content, parties, _) = ready.Value;
        return new PrescriptionSigningContent(p.Id, p.PrescriptionNumber, PrescriptionCanonicalForm.Format, Convert.ToBase64String(content),
            PrescriptionCanonicalForm.Sha256(content), parties.PatientName, parties.PatientUhid, p.Items.Count);
    }

    public async Task<Result<PrescriptionDigitalSignatureDto>> AttachAsync(Guid id, AttachDigitalSignatureRequest request, CancellationToken ct)
    {
        var ready = await ReadyToSignAsync(id, ct);
        if (ready.IsFailure)
        {
            return ready.Error!;
        }

        var (p, content, _, policy) = ready.Value;
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(request.Signature);
        }
        catch (FormatException)
        {
            return Error.Field("signature", "The signature is not base64.");
        }

        var check = DetachedCms.Verify(content, signature);
        if (!check.IsValid)
        {
            return Error.Rule("prescription.signature_invalid", check.Message);
        }

        var now = time.GetUtcNow();
        if (check.SignedAt is not { } signedAt || signedAt < now - MaxSigningAge || signedAt > now + MaxClockSkew)
        {
            return Error.Rule("prescription.signature_time",
                "The signature's time is not close to the server's. Check this computer's date and time, then sign again.");
        }

        var signer = check.Signer!;
        var attached = p.AttachDigitalSignature(new PrescriptionDigitalSignature(signature, content, PrescriptionCanonicalForm.Sha256(content), signedAt,
            currentUser.UserId, signer.SignerName, signer.Subject, signer.SerialNumber, signer.Issuer, signer.Thumbprint,
            check.Chain?.Summary ?? "Not checked"));
        if (attached.IsFailure)
        {
            return attached.Error!;
        }

        audit.Add(new AuditEntry(AuditCategories.Clinical, "prescription.digitally_signed",
            $"{p.PrescriptionNumber}: signed with DSC {signer.SerialNumber} ({signer.SignerName})")
        {
            PatientId = p.PatientId,
            EntityType = nameof(Prescription),
            EntityId = p.Id.ToString(),
            Details = new { signer.Subject, signer.Issuer, signer.SerialNumber, signer.Thumbprint, ContentHash = p.SignedContentHash, Chain = p.SignerChainStatus },
        });

        // Where the DSC is required the patient is told only now, with the signed copy.
        if (policy == PrescriptionSignaturePolicy.Required)
        {
            await prescriptions.QueuePatientNoticeAsync(p, ct);
        }

        await db.SaveChangesAsync(ct);
        return Check(p, policy);
    }

    /// <summary>The signature re-checked now; the caller has loaded the prescription through the access guard.</summary>
    public async Task<PrescriptionDigitalSignatureDto> StatusAsync(Prescription prescription, CancellationToken ct) =>
        Check(prescription, await settings.PolicyAsync(prescription.FacilityId, ct));

    /// <summary>
    /// Verifies the stored signature over the stored bytes, the stored hash, and that the stored prescription still gives
    /// those bytes. Valid only when all three hold.
    /// </summary>
    public static PrescriptionDigitalSignatureDto Check(Prescription p, PrescriptionSignaturePolicy policy)
    {
        if (p.DigitalSignature is not { } signature || p.SignedContent is not { } content)
        {
            return new PrescriptionDigitalSignatureDto(DigitalSignatureState.NotSigned, policy, null, null, null, null, null, null, null, false, null, null);
        }

        var cms = DetachedCms.Verify(content, signature);
        var hashMatches = string.Equals(PrescriptionCanonicalForm.Sha256(content), p.SignedContentHash, StringComparison.Ordinal);
        var recordMatches = RecordMatches(p, content);
        var valid = cms.IsValid && hashMatches && recordMatches;
        var message = !cms.IsValid ? cms.Message
            : !hashMatches || !recordMatches ? "The prescription was changed after it was signed. Do not dispense it."
            : "The signature is valid.";
        return new PrescriptionDigitalSignatureDto(valid ? DigitalSignatureState.Valid : DigitalSignatureState.Invalid, policy, p.DigitallySignedAt,
            p.SignerName, p.SignerSubject, p.SignerSerialNumber, p.SignerIssuer, p.SignerThumbprint, p.SignedContentHash, recordMatches,
            cms.Chain?.Summary ?? p.SignerChainStatus, message);
    }

    /// <summary>The stored prescription, with the parties recorded in the signed bytes, rebuilds those bytes exactly.</summary>
    public static bool RecordMatches(Prescription p, byte[] content)
    {
        try
        {
            return PrescriptionCanonicalForm.Build(p, PrescriptionCanonicalForm.ReadParties(content)).AsSpan().SequenceEqual(content);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The parties as they are now: the facility, the patient and the prescriber.</summary>
    public async Task<PrescriptionSigningParties> PartiesAsync(Prescription p, CancellationToken ct)
    {
        var facility = await db.Set<Facility>().AsNoTracking().IgnoreQueryFilters().Where(f => f.Id == p.FacilityId).Select(f => f.Name).FirstOrDefaultAsync(ct);
        var patient = await db.Set<Patient>().AsNoTracking().Where(x => x.Id == p.PatientId).Select(x => new { x.Uhid, x.FullName }).FirstAsync(ct);
        var prescriber = await PrintQueries.PrescriberAsync(db, p.PractitionerId, ct);
        return new PrescriptionSigningParties(facility ?? string.Empty, patient.Uhid, patient.FullName, prescriber?.DisplayName ?? string.Empty,
            prescriber?.Qualification, prescriber?.RegistrationNumber, prescriber?.RegistrationCouncil);
    }

    // The prescribing doctor signs, once, a prescription signed in HC360, where the facility offers DSC signing.
    private async Task<Result<(Prescription Prescription, byte[] Content, PrescriptionSigningParties Parties, PrescriptionSignaturePolicy Policy)>>
        ReadyToSignAsync(Guid id, CancellationToken ct)
    {
        var loaded = await prescriptions.LoadAsync(id, ct);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var p = loaded.Value;
        var policy = await settings.PolicyAsync(p.FacilityId, ct);
        if (policy == PrescriptionSignaturePolicy.Off)
        {
            return Error.Rule("prescription.signature_off", "Signing prescriptions with a DSC is switched off at this hospital.");
        }

        if (currentUser.PractitionerId != p.PractitionerId)
        {
            return Error.Forbidden("prescription.not_prescriber", "Only the prescribing doctor can sign this prescription with their DSC.");
        }

        if (p.Status != PrescriptionStatus.Signed)
        {
            return Error.Rule("prescription.not_signed", p.Status == PrescriptionStatus.Draft
                ? "Sign the prescription before signing it with your DSC."
                : "This prescription is cancelled.");
        }

        if (p.IsDigitallySigned)
        {
            return Error.Conflict("prescription.already_digitally_signed",
                "This prescription is already signed with a DSC and cannot change. Repeat it to issue a new prescription.");
        }

        var parties = await PartiesAsync(p, ct);
        return (p, PrescriptionCanonicalForm.Build(p, parties), parties, policy);
    }
}
