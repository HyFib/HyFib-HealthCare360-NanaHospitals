namespace HyFib.HealthCare360.Modules.Clinical.Domain;

/// <summary>
/// A vaccine dose given here or transcribed from the patient's immunisation card (<see cref="IsExternal"/>),
/// so the EHR shows the complete schedule (Universal Immunisation Programme, travel and adult vaccines).
/// </summary>
public sealed class Immunization : AuditableEntity
{
    public Guid PatientId { get; set; }
    public Guid? EncounterId { get; set; }
    public string VaccineName { get; set; } = string.Empty;

    /// <summary>Short code used on Indian immunisation cards (BCG, OPV, PENTA, MR, HPV…).</summary>
    public string? VaccineCode { get; set; }

    public int? DoseNumber { get; set; }
    public DateOnly AdministeredOn { get; set; }
    public string? Route { get; set; }
    public string? Site { get; set; }
    public string? BatchNumber { get; set; }
    public string? Manufacturer { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public Guid? AdministeredByUserId { get; set; }

    /// <summary>Name of the vaccinator or facility for doses given elsewhere.</summary>
    public string? AdministeredBy { get; set; }

    public bool IsExternal { get; set; }
    public DateOnly? NextDueOn { get; set; }
    public string? Notes { get; set; }
    public bool IsEnteredInError { get; private set; }
    public string? EnteredInErrorReason { get; private set; }

    public Result MarkEnteredInError(string reason)
    {
        if (IsEnteredInError)
        {
            return Error.Rule("immunization.already_voided", "This immunisation is already marked as entered in error.");
        }

        IsEnteredInError = true;
        EnteredInErrorReason = reason.Trim();
        return Result.Success();
    }
}
