using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// A patient leaving against medical advice, as the screen sends it: who signed the form (a relative named, with their
/// relationship), or that nobody would; the doctor who explained the risks, confirming at this screen with their own user
/// name and password when they are not the one signed in (<see cref="AgainstAdviceDeclarations.DoctorPurpose"/>); the
/// witness's confirmation, given at this screen with their own user name and password; and a scanned copy of the signed
/// form, uploaded to the document store, when there is one.
/// </summary>
public sealed record AgainstAdviceRequest(
    AgainstAdviceSignatory SignedBy,
    string? SignatoryName,
    string? Relationship,
    Guid? DoctorConfirmationId,
    Guid WitnessConfirmationId,
    Guid? SignedFormFileId);

public sealed class AgainstAdviceRequestValidator : AbstractValidator<AgainstAdviceRequest>
{
    public AgainstAdviceRequestValidator()
    {
        RuleFor(x => x.SignedBy).IsInEnum();
        RuleFor(x => x.SignatoryName).SafeText(200);
        RuleFor(x => x.Relationship).SafeText(64);
        RuleFor(x => x.SignatoryName).NotEmpty().When(x => x.SignedBy == AgainstAdviceSignatory.Relative)
            .WithMessage("Name the relative who signed.");
        RuleFor(x => x.Relationship).NotEmpty().When(x => x.SignedBy == AgainstAdviceSignatory.Relative)
            .WithMessage("Say how the relative who signed is related to the patient.");
        RuleFor(x => x.DoctorConfirmationId).OptionalId();
        RuleFor(x => x.WitnessConfirmationId).NotEmpty().WithMessage("A witness confirms with their own user name and password.");
        RuleFor(x => x.SignedFormFileId).OptionalId();
    }
}

/// <summary>
/// Records a patient leaving against medical advice (LAMA or DAMA on the ward, LAMA from the emergency department):
/// maker-checker, slice 8. The form's signatory, or "refused to sign"; the doctor who explained the risks, who is the user
/// signed in when they are a doctor (they sign encounters and have a practitioner record), or else confirms at this
/// screen with their own credentials; and a witness, a different member of staff, confirming with theirs
/// (<see cref="IWitnessConfirmations"/>, ADR-014), never the doctor. Each confirmation is spent in the caller's unit of
/// work, once, for this patient and record.
/// </summary>
public sealed class AgainstAdviceDeclarations(HospitalDbContext db, ICurrentUser currentUser, IPermissionService permissions, IWitnessConfirmations witnesses)
{
    /// <summary>The doctor who explained the risks, confirming at the screen of the nurse who records the patient leaving.</summary>
    public static readonly WitnessPurpose DoctorPurpose = new("against_advice.doctor", ClinicalPermissions.EncountersSign,
        "the risks of leaving against medical advice explained to the patient");

    /// <summary>A witness to a patient leaving the emergency department against medical advice: a nurse or doctor of the department (who triage).</summary>
    public static readonly WitnessPurpose ErWitness = new("er.lama", ClinicalPermissions.ErTriage, "a patient leaving the emergency department against medical advice");

    public async Task<Result<AgainstAdviceRecord>> RecordAsync(
        AgainstAdviceRequest request, string witnessPurpose, Guid patientId, string usedForType, Guid usedForId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (AgainstAdviceRules.Check(request.SignedBy, request.SignatoryName, request.Relationship) is { } incomplete)
        {
            return incomplete;
        }

        if (currentUser.UserId is not { } me)
        {
            return Error.Forbidden("against_advice.staff_only", "Only a signed-in member of staff records a patient leaving against medical advice.");
        }

        VerifiedWitness doctor;
        if (request.DoctorConfirmationId is { } confirmation)
        {
            var confirmed = await witnesses.UseAsync(confirmation, DoctorPurpose.Key, patientId, usedForType, usedForId, ct);
            if (confirmed.IsFailure)
            {
                return confirmed.Error!;
            }

            doctor = confirmed.Value;
        }
        else if (currentUser.PractitionerId is not null && await permissions.HasPermissionAsync(me, ClinicalPermissions.EncountersSign, ct))
        {
            doctor = new VerifiedWitness(me, currentUser.DisplayName ?? currentUser.UserName ?? "Doctor");
        }
        else
        {
            return Error.Rule("against_advice.doctor_required",
                "The doctor who explained the risks confirms it: they record it signed in as themselves, or confirm here with their own user name and password.");
        }

        var witness = await witnesses.UseAsync(request.WitnessConfirmationId, witnessPurpose, patientId, usedForType, usedForId, ct);
        if (witness.IsFailure)
        {
            return witness.Error!;
        }

        if (witness.Value.UserId == doctor.UserId)
        {
            return Error.Rule("against_advice.witness_is_doctor", "The witness is someone other than the doctor who explained the risks.");
        }

        if (request.SignedFormFileId is { } file
            && !await db.Set<StoredFile>().AnyAsync(f => f.Id == file && (f.PatientId == null || f.PatientId == patientId), ct))
        {
            return Error.Field("againstAdvice.signedFormFileId", "The scanned form was not found for this patient. Upload it again.");
        }

        return new AgainstAdviceRecord(
            request.SignedBy,
            request.SignedBy == AgainstAdviceSignatory.Relative ? request.SignatoryName!.Trim() : null,
            request.SignedBy == AgainstAdviceSignatory.Relative ? request.Relationship!.Trim() : null,
            doctor.UserId,
            doctor.Name,
            witness.Value.UserId,
            witness.Value.Name,
            request.SignedFormFileId);
    }
}
