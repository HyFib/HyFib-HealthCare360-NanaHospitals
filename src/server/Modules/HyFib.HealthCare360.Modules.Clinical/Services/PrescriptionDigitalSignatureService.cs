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
/// bytes itself, checks the signature against them (<see cref="DetachedCms.Verify"/>), accepts it only from a
/// certificate that chains to a CCA India root in its DSC trust folder (<see cref="DscTrustStore"/>), and keeps the
/// signature, the bytes and the certificate. A signed prescription never changes, so the signature stays valid; every
/// later check verifies the signature and the chain again and rebuilds the bytes from the stored prescription to catch
/// a record changed outside HC360. A merge of the patient's duplicate record, which moves the prescription, is not such
/// a change (<see cref="SignedForCurrentPatientAsync"/>).
/// </summary>
public sealed class PrescriptionDigitalSignatureService(
    HospitalDbContext db,
    PrescriptionService prescriptions,
    PrescriptionSignatureSettings settings,
    DscTrustStore trust,
    ICurrentUser currentUser,
    IAuditLogger audit,
    TimeProvider time)
{
    /// <summary>How long before the server receives it a signature may have been made (choosing the certificate, the PIN).</summary>
    public static readonly TimeSpan MaxSigningAge = TimeSpan.FromMinutes(15);

    /// <summary>How far ahead of the server's clock a workstation's clock may be.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>More merges than this between the record signed for and the prescription's record now is not a merge history.</summary>
    public const int MaxMergeHops = 16;

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

        var anchors = trust.Current;
        var check = DetachedCms.Verify(content, signature, anchors);
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

        // Whatever the policy: any certificate on the computer that may sign (a self-signed one, the domain's user
        // certificate) is not a DSC, and the printed copy and the QR page would present it as one.
        if (check.Chain is not { Trusted: true })
        {
            return Error.Rule("prescription.signature_untrusted",
                "This certificate is not a DSC from a licensed Certifying Authority: it does not chain to a CCA India root installed "
                + $"on the server ({check.Chain?.Summary}). Sign with the DSC on your token.");
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
        return Check(p, policy, anchors, signedForCurrentPatient: true);
    }

    /// <summary>The signature re-checked now; the caller has loaded the prescription through the access guard.</summary>
    public async Task<PrescriptionDigitalSignatureDto> StatusAsync(Prescription prescription, CancellationToken ct) =>
        await CheckAsync(db, trust.Current, prescription, await settings.PolicyAsync(prescription.FacilityId, ct), ct);

    /// <summary><see cref="Check"/>, with the patient the bytes were signed for looked up (<see cref="SignedForCurrentPatientAsync"/>).</summary>
    public static async Task<PrescriptionDigitalSignatureDto> CheckAsync(HospitalDbContext db, DscTrustAnchors anchors, Prescription p,
        PrescriptionSignaturePolicy policy, CancellationToken ct) =>
        Check(p, policy, anchors, p.IsDigitallySigned && await SignedForCurrentPatientAsync(db, p, ct));

    /// <summary>
    /// Verifies the stored signature over the stored bytes, the stored hash, that the stored prescription still gives
    /// those bytes and is still on the record they were signed for (<paramref name="signedForCurrentPatient"/>), and
    /// that the certificate chains to a root in <paramref name="anchors"/>. <see cref="DigitalSignatureState.Valid"/>
    /// when all hold; <see cref="DigitalSignatureState.Untrusted"/> when only the chain does not (the roots were changed
    /// since); <see cref="DigitalSignatureState.Invalid"/> otherwise.
    /// </summary>
    public static PrescriptionDigitalSignatureDto Check(
        Prescription p,
        PrescriptionSignaturePolicy policy,
        DscTrustAnchors anchors,
        bool signedForCurrentPatient)
    {
        if (p.DigitalSignature is not { } signature || p.SignedContent is not { } content)
        {
            return new PrescriptionDigitalSignatureDto(DigitalSignatureState.NotSigned, policy, null, null, null, null, null, null, null, false, null, false,
                null);
        }

        var cms = DetachedCms.Verify(content, signature, anchors);
        var hashMatches = string.Equals(PrescriptionCanonicalForm.Sha256(content), p.SignedContentHash, StringComparison.Ordinal);
        var recordMatches = signedForCurrentPatient && RecordMatches(p, content);
        var chainTrusted = cms.Chain is { Trusted: true };
        var (state, message) = !cms.IsValid ? (DigitalSignatureState.Invalid, cms.Message)
            : !hashMatches || !recordMatches ? (DigitalSignatureState.Invalid, "The prescription was changed after it was signed. Do not dispense it.")
            : !chainTrusted ? (DigitalSignatureState.Untrusted, "The prescription is as the doctor signed it, but HC360 could not confirm the "
                + $"certificate as a DSC from a licensed Certifying Authority ({cms.Chain?.Summary}).")
            : (DigitalSignatureState.Valid, "The signature is valid.");
        return new PrescriptionDigitalSignatureDto(state, policy, p.DigitallySignedAt, p.SignerName, p.SignerSubject, p.SignerSerialNumber, p.SignerIssuer,
            p.SignerThumbprint, p.SignedContentHash, recordMatches, cms.Chain?.Summary ?? p.SignerChainStatus, chainTrusted, message);
    }

    /// <summary>
    /// The stored prescription, with the parties recorded in the signed bytes (the patient's record as signed for,
    /// names as printed), rebuilds those bytes exactly.
    /// </summary>
    public static bool RecordMatches(Prescription p, byte[] content) =>
        PrescriptionCanonicalForm.TryReadParties(content) is { } parties && PrescriptionCanonicalForm.Build(p, parties).AsSpan().SequenceEqual(content);

    /// <summary>
    /// The patient record the bytes were signed for is the prescription's record now: the same one, or a duplicate front
    /// office merged into it, directly or through later merges. <c>PatientsMerged</c> moves the prescription to the
    /// surviving record, so its patient changes without the prescription changing; any other patient means the record
    /// was changed outside HC360.
    /// </summary>
    public static async Task<bool> SignedForCurrentPatientAsync(HospitalDbContext db, Prescription p, CancellationToken ct)
    {
        if (p.SignedContent is not { } content || PrescriptionCanonicalForm.TryReadParties(content) is not { } parties)
        {
            return false;
        }

        var record = parties.PatientId;
        for (var hops = 0; record != p.PatientId; hops++)
        {
            var mergedInto = hops < MaxMergeHops
                ? await db.Set<Patient>().AsNoTracking().IgnoreQueryFilters().Where(x => x.Id == record).Select(x => x.MergedIntoPatientId)
                    .FirstOrDefaultAsync(ct)
                : null;
            if (mergedInto is not { } next)
            {
                return false;
            }

            record = next;
        }

        return true;
    }

    /// <summary>The parties as they are now: the facility, the patient and the prescriber.</summary>
    public async Task<PrescriptionSigningParties> PartiesAsync(Prescription p, CancellationToken ct)
    {
        var facility = await db.Set<Facility>().AsNoTracking().IgnoreQueryFilters().Where(f => f.Id == p.FacilityId).Select(f => f.Name).FirstOrDefaultAsync(ct);
        var patient = await db.Set<Patient>().AsNoTracking().Where(x => x.Id == p.PatientId).Select(x => new { x.Uhid, x.FullName }).FirstAsync(ct);
        var prescriber = await PrintQueries.PrescriberAsync(db, p.PractitionerId, ct);
        return new PrescriptionSigningParties(facility ?? string.Empty, p.PatientId, patient.Uhid, patient.FullName, prescriber?.DisplayName ?? string.Empty,
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

        // Before the doctor enters a PIN for a signature the server would refuse.
        if (trust.Current.IsEmpty)
        {
            return Error.Rule("prescription.signature_not_set_up",
                "HC360 cannot accept DSC signatures yet: IT has not installed the CCA India root certificates on the server.");
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
