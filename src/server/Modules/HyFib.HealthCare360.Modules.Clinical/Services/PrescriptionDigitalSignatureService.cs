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
}

/// <summary>
/// The final copy of a signed prescription (ERX-04): printed, downloaded as a PDF by staff or on the portal, and sent
/// with the "prescription shared" message. Every copy re-checks the doctor's DSC signature
/// (<see cref="PrescriptionDigitalSignatureService.Check"/>) and states it ("Digitally signed by …") only while it is
/// <see cref="DigitalSignatureState.Valid"/>; an Untrusted or Invalid one prints as "Signed electronically". Where the
/// hospital requires the DSC, no copy is issued without a valid one (<see cref="Problem"/>).
/// </summary>
public sealed class PrescriptionFinalCopies(HospitalDbContext db, PrescriptionSignatureSettings settings, DscTrustStore trust)
{
    public const string RequiredCode = "prescription.digital_signature_required";

    public const string UntrustedCode = "prescription.digital_signature_untrusted";

    public const string InvalidCode = "prescription.digital_signature_invalid";

    /// <summary>The DSC signature re-checked now, under the facility's policy.</summary>
    public async Task<PrescriptionDigitalSignatureDto> CheckAsync(Prescription prescription, CancellationToken ct) =>
        await PrescriptionDigitalSignatureService.CheckAsync(db, trust.Current, prescription, await settings.PolicyAsync(prescription.FacilityId, ct), ct);

    /// <summary>
    /// Why the final copy cannot be issued (null when <see cref="PrescriptionDigitalSignatureDto.FinalCopyAvailable"/>):
    /// where the hospital requires the doctor's DSC, until the prescribing doctor signs it with theirs
    /// (<see cref="RequiredCode"/>), while its certificate does not chain to a CCA India root in the server's DSC trust
    /// folder (<see cref="UntrustedCode"/>: a DSC is then treated as missing), or when the signature does not check out
    /// (<see cref="InvalidCode"/>). The caller checks that the prescription is signed.
    /// </summary>
    public static Error? Problem(PrescriptionDigitalSignatureDto signature) => signature.FinalCopyAvailable
        ? null
        : signature.State switch
        {
            DigitalSignatureState.Untrusted => Error.Rule(UntrustedCode,
                "HC360 cannot confirm the doctor's DSC on this prescription: its certificate does not chain to a CCA India root installed on the "
                + "server. It is issued only once that is fixed: ask IT to check the server's DSC trust folder."),
            DigitalSignatureState.Invalid => Error.Rule(InvalidCode,
                "The doctor's DSC signature on this prescription does not check out: the prescription or its signature was changed after signing. "
                + "Do not issue it; repeat it to issue a new prescription."),
            _ => Error.Rule(RequiredCode,
                "This hospital issues prescriptions only once the prescribing doctor has signed them with their DSC, in HC360 Desktop."),
        };

    /// <summary>
    /// <see cref="Problem"/> for the patient portal: the same code, in a few words for the patient (the portal words it
    /// by the code, in the patient's language).
    /// </summary>
    public static Error? PatientProblem(PrescriptionDigitalSignatureDto signature) => Problem(signature) is not { } problem
        ? null
        : Error.Rule(problem.Code, problem.Code == RequiredCode
            ? "Your doctor has not digitally signed this prescription yet."
            : "This prescription cannot be issued right now. Please contact the hospital.");

    /// <summary>
    /// What the printed copy says of the DSC signature: null ("Signed electronically") unless it is
    /// <see cref="DigitalSignatureState.Valid"/>, and the registration number as the doctor signed it, not as the
    /// practitioner master has it today.
    /// </summary>
    public static PrescriptionPrintSignature? PrintSignature(Prescription p, PrescriptionDigitalSignatureDto signature) =>
        signature.State == DigitalSignatureState.Valid && p.SignedContent is { } content
            ? new PrescriptionPrintSignature(p.SignerName!, PrescriptionCanonicalForm.TryReadParties(content)?.RegistrationNumber, p.SignerSerialNumber!,
                p.DigitallySignedAt!.Value, p.SignedContentHash!)
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
    PrescriptionFinalCopies finalCopies,
    DscTrustStore trust,
    ClinicalAccess access,
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

        // The bytes name the patient and the medicines: fetching them is reading the record (the guard and the PHI access
        // audit, as for the signed content).
        var (p, content, parties, _) = ready.Value;
        var opened = await access.OpenAsync(p.PatientId, $"Fetched prescription {p.PrescriptionNumber} to sign with a DSC", ct);
        if (opened.IsFailure)
        {
            return opened.Error!;
        }

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

        var (p, content, parties, policy) = ready.Value;
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

        // The printed copy pairs the certificate's name with the doctor's registration number: a colleague's DSC, or an
        // organisation's on a shared workstation, would lend the prescription a signature that is not the doctor's.
        var signer = check.Signer!;
        if (!DscSignerName.Matches(signer.SignerName, parties.PrescriberName))
        {
            return Error.Rule("prescription.signer_mismatch",
                $"This DSC is issued to {signer.SignerName}, not to {parties.PrescriberName}. Sign with your own DSC. If it is yours, ask the "
                + "administrator to write your name in HC360 as it is on your DSC (Hospital setup › Doctors and clinical staff).");
        }

        if (Unstorable(signer) is { } unstorable)
        {
            return Error.Rule("prescription.certificate_unsupported",
                $"HC360 cannot keep this certificate's details: its {unstorable} is longer than HC360 stores. Sign with the DSC on your token, or "
                + "ask IT to contact HyFib support.");
        }

        var attached = p.AttachDigitalSignature(new PrescriptionDigitalSignature(signature, content, PrescriptionCanonicalForm.Sha256(content), signedAt,
            currentUser.UserId, signer.SignerName, signer.Subject, signer.SerialNumber, signer.Issuer, signer.Thumbprint,
            Truncate(check.Chain.Summary, Prescription.SignerChainStatusMaxLength)));
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

        // Where the DSC was required when the doctor signed, the patient was not told then: they are now, with the signed
        // copy. Once only, whatever the setting is now or was then.
        await prescriptions.QueuePatientNoticeAsync(p, ct);

        await db.SaveChangesAsync(ct);
        return Check(p, policy, anchors, signedForCurrentPatient: true);
    }

    /// <summary>The signature re-checked now; the caller has loaded the prescription through the access guard.</summary>
    public Task<PrescriptionDigitalSignatureDto> StatusAsync(Prescription prescription, CancellationToken ct) => finalCopies.CheckAsync(prescription, ct);

    /// <summary><see cref="Check"/>, with the patient the bytes were signed for looked up (<see cref="SignedForCurrentPatientAsync"/>).</summary>
    public static async Task<PrescriptionDigitalSignatureDto> CheckAsync(HospitalDbContext db, DscTrustAnchors anchors, Prescription p,
        PrescriptionSignaturePolicy policy, CancellationToken ct) =>
        Check(p, policy, anchors, p.IsDigitallySigned && await SignedForCurrentPatientAsync(db, p, ct));

    /// <summary>
    /// Verifies the stored signature over the stored bytes, the stored hash, that the stored prescription still gives
    /// those bytes and is still on the record they were signed for (<paramref name="signedForCurrentPatient"/>), and
    /// that the certificate chains to a root in <paramref name="anchors"/>. <see cref="DigitalSignatureState.Valid"/>
    /// when all hold; <see cref="DigitalSignatureState.Untrusted"/> when only the chain does not (the roots were changed
    /// since); <see cref="DigitalSignatureState.Invalid"/> otherwise. A signature that does not check out has no chain
    /// to report: the one stored when it was attached is not repeated as if it still held.
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
                null, FinalCopyAvailable(policy, DigitalSignatureState.NotSigned));
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
            p.SignerThumbprint, p.SignedContentHash, recordMatches, cms.Chain?.Summary ?? NotChecked, chainTrusted, message,
            FinalCopyAvailable(policy, state));
    }

    /// <summary>What the chain reads when the signature does not check out, so it was not built.</summary>
    public const string NotChecked = "Not checked: the signature does not check out";

    /// <summary>The DSC rules let the prescription be printed, downloaded and shared (<see cref="PrescriptionFinalCopies"/>).</summary>
    public static bool FinalCopyAvailable(PrescriptionSignaturePolicy policy, DigitalSignatureState state) =>
        policy != PrescriptionSignaturePolicy.Required || state == DigitalSignatureState.Valid;

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

    /// <summary>Which of the certificate's details does not fit its column, or null when all do.</summary>
    internal static string? Unstorable(SignerCertificate signer) =>
        signer.SignerName.Length > Prescription.SignerNameMaxLength ? "name"
        : signer.Subject.Length > Prescription.SignerDistinguishedNameMaxLength ? "subject"
        : signer.Issuer.Length > Prescription.SignerDistinguishedNameMaxLength ? "issuer"
        : signer.SerialNumber.Length > Prescription.SignerSerialNumberMaxLength ? "serial number"
        : signer.Thumbprint.Length > Prescription.SignerThumbprintMaxLength ? "thumbprint"
        : null;

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

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
