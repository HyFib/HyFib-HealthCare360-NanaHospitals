using FluentValidation;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.Platform.Identity;

/// <summary>
/// Something a second member of staff witnesses at the screen of the one who does it (a controlled drug given, a unit of
/// blood checked at the bedside), registered by the module that asks for it (<see cref="WitnessPurposes.AddWitnessPurpose"/>).
/// Only someone who holds <paramref name="Permission"/> witnesses it.
/// </summary>
/// <param name="Key">The purpose's name, as the screen and the module give it (<c>mar.dose</c>).</param>
/// <param name="Description">What is witnessed, for the audit trail ("a controlled drug given").</param>
public sealed record WitnessPurpose(string Key, string Permission, string Description);

public static class WitnessPurposes
{
    /// <summary>Lets <paramref name="purpose"/> be witnessed (<c>POST /api/v1/auth/witness</c>).</summary>
    public static IServiceCollection AddWitnessPurpose(this IServiceCollection services, WitnessPurpose purpose) => services.AddSingleton(purpose);
}

/// <summary>
/// A witness's confirmation, made at the screen of the signed-in user it was made for (<see cref="ForUserId"/>): they gave
/// their user name and password there, and their authenticator code when their account has one (<c>POST
/// /api/v1/auth/witness</c>). It is used once, within minutes, for one record of that user's, of that purpose and that
/// patient (<see cref="IWitnessConfirmations"/>), and then names the witness on the record. Kept afterwards as the record of
/// who witnessed what.
/// </summary>
public sealed class WitnessConfirmation : Entity, IVersioned
{
    /// <summary>How long a confirmation may wait to be used: the witness is there when the thing is done, not before.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public Guid WitnessUserId { get; set; }
    public string WitnessName { get; set; } = string.Empty;

    /// <summary>The signed-in user the witness confirmed for: no one else uses it.</summary>
    public Guid ForUserId { get; set; }

    public Guid? FacilityId { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public Guid? PatientId { get; set; }
    public DateTimeOffset ConfirmedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>The record it was used for (its type and id).</summary>
    public string? UsedForType { get; set; }

    public Guid? UsedForId { get; set; }
    public uint Version { get; set; }
}

/// <summary>
/// A witness gives their own user name and password at another user's screen to witness <paramref name="Purpose"/> for
/// <paramref name="PatientId"/>; their authenticator code too when their account has one.
/// </summary>
public sealed record ConfirmWitnessRequest(string UserName, string Password, string? Code, string Purpose, Guid? PatientId);

public sealed class ConfirmWitnessRequestValidator : AbstractValidator<ConfirmWitnessRequest>
{
    public ConfirmWitnessRequestValidator()
    {
        RuleFor(r => r.UserName).NotEmpty().WithMessage("Enter the witness's user name.").MaximumLength(64);
        RuleFor(r => r.Password).GivenPassword("Enter the witness's password.");
        RuleFor(r => r.Code).SixDigitCode(IdentityRules.AuthenticatorCodeMessage).When(r => !string.IsNullOrEmpty(r.Code));
        RuleFor(r => r.Purpose).NotEmpty().MaximumLength(64);
        RuleFor(r => r.PatientId).OptionalId();
    }
}

/// <summary>The witness confirmed: the confirmation to send with the record, and who the witness is.</summary>
public sealed record WitnessConfirmed(Guid ConfirmationId, Guid UserId, string Name, DateTimeOffset ExpiresAt);

/// <summary>Who witnessed a record: a member of staff who proved it at the screen (<see cref="WitnessConfirmation"/>).</summary>
public sealed record VerifiedWitness(Guid UserId, string Name);

/// <summary>Uses a witness's confirmation for a record of the signed-in user's (<see cref="WitnessConfirmation"/>).</summary>
public interface IWitnessConfirmations
{
    /// <summary>
    /// Uses confirmation <paramref name="confirmationId"/> for the record <paramref name="usedForType"/>
    /// <paramref name="usedForId"/>: only one the signed-in user was given, here, for <paramref name="purpose"/> and
    /// <paramref name="patientId"/>, not used and not expired. It is marked used in the caller's unit of work, so it is
    /// spent only if the caller saves; two saves using the same one do not both succeed.
    /// </summary>
    Task<Result<VerifiedWitness>> UseAsync(
        Guid confirmationId, string purpose, Guid? patientId, string usedForType, Guid usedForId, CancellationToken ct);
}

internal sealed class WitnessConfirmations(HospitalDbContext db, ICurrentUser currentUser, TimeProvider time) : IWitnessConfirmations
{
    internal static readonly Error NotUsable = Error.Rule("witness.confirmation_unusable",
        "The witness's confirmation has run out or was given for something else. Ask the witness to confirm again.");

    public async Task<Result<VerifiedWitness>> UseAsync(
        Guid confirmationId, string purpose, Guid? patientId, string usedForType, Guid usedForId, CancellationToken ct)
    {
        var confirmation = await db.Set<WitnessConfirmation>().FirstOrDefaultAsync(c => c.Id == confirmationId, ct);
        var now = time.GetUtcNow();
        if (confirmation is null
            || confirmation.ForUserId != currentUser.UserId
            || confirmation.FacilityId != currentUser.FacilityId
            || confirmation.Purpose != purpose
            || confirmation.PatientId != patientId
            || confirmation.UsedAt is not null
            || confirmation.ExpiresAt <= now)
        {
            return NotUsable;
        }

        (confirmation.UsedAt, confirmation.UsedForType, confirmation.UsedForId) = (now, usedForType, usedForId);
        return new VerifiedWitness(confirmation.WitnessUserId, confirmation.WitnessName);
    }
}
