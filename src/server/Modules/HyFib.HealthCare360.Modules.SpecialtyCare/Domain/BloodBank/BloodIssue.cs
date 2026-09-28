using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

/// <summary>Cold-chain rule for units coming back from the ward (BB-07).</summary>
public static class ColdChain
{
    /// <summary>
    /// Red cells out of controlled storage for more than 30 minutes may have warmed above 10 °C (bacterial growth
    /// risk), so they can be restocked only if returned within 30 minutes with the cold chain maintained.
    /// </summary>
    public static readonly TimeSpan ReturnWindow = TimeSpan.FromMinutes(30);

    public static Result CanRestock(BloodComponentType component, DateTimeOffset issuedAt, DateTimeOffset returnedAt, bool temperatureMaintained)
    {
        if (component is BloodComponentType.FFP or BloodComponentType.Cryoprecipitate)
        {
            return Error.Rule("bloodbank.thawed_plasma", "Thawed plasma products cannot be refrozen; the unit is discarded.");
        }

        if (!temperatureMaintained)
        {
            return Error.Rule("bloodbank.cold_chain_broken", "Storage temperature was not maintained; the unit is discarded.");
        }

        return returnedAt < issuedAt || returnedAt - issuedAt > ReturnWindow
            ? Error.Rule("bloodbank.return_too_late", "Returned more than 30 minutes after issue; the unit is discarded.")
            : Result.Success();
    }
}

/// <summary>A unit handed over to the ward for one patient (BB-07), and its return if it was not transfused.</summary>
public sealed class BloodIssue : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid RequestId { get; set; }
    public Guid ComponentId { get; set; }
    public Guid PatientId { get; set; }
    public string UnitNumber { get; set; } = string.Empty;
    public BloodComponentType ComponentType { get; set; }
    public BloodGroup UnitBloodGroup { get; set; }
    public Guid? CrossMatchId { get; set; }

    /// <summary>Issued without a cross-match under a documented emergency release.</summary>
    public bool IsEmergencyRelease { get; set; }

    public DateTimeOffset IssuedAt { get; set; }
    public Guid? IssuedByUserId { get; set; }
    public string? IssuedByName { get; set; }

    /// <summary>Ward staff who collected the unit (identity check at the blood bank counter).</summary>
    public string IssuedToName { get; set; } = string.Empty;

    public DateTimeOffset? ReturnedAt { get; set; }
    public bool? Restocked { get; set; }
    public string? ReturnNotes { get; set; }

    public ChargeStatus ChargeStatus { get; set; }
    public string? ChargeError { get; set; }

    public bool IsReturned => ReturnedAt is not null;

    public Result MarkReturned(DateTimeOffset returnedAt, bool restocked, string? notes)
    {
        if (IsReturned)
        {
            return Error.Rule("bloodbank.already_returned", "This unit has already been returned.");
        }

        ReturnedAt = returnedAt;
        Restocked = restocked;
        ReturnNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        return Result.Success();
    }

    public void RecordCharges(ChargeStatus status, string? error)
    {
        ChargeStatus = status;
        ChargeError = error;
    }
}
