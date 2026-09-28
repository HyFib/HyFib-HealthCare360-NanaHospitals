namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;

/// <summary>
/// Where the billing hand-off of a clinical event stands. Clinical work (completing surgery, a dialysis session…)
/// never waits for Billing: the charge outcome is recorded here and can be re-posted, because posting is idempotent
/// on <c>SourceModule</c> + <c>SourceReference</c>.
/// </summary>
public enum ChargeStatus
{
    /// <summary>Nothing billable has happened yet.</summary>
    NotDue = 0,
    Posted,

    /// <summary>Billing rejected or was unavailable; <c>ChargeError</c> explains why and staff can retry.</summary>
    Failed,

    /// <summary>The charge was withdrawn (for example a blood unit returned in time).</summary>
    Cancelled,
}

/// <summary>Stable <c>SourceModule</c> keys used on every charge this module posts.</summary>
public static class ChargeSources
{
    public const string Ot = "specialtycare.ot";
    public const string Dialysis = "specialtycare.dialysis";
    public const string Physio = "specialtycare.physio";
    public const string BloodBank = "specialtycare.bloodbank";
}
