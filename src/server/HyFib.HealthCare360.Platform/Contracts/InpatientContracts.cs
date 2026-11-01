namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>Whether a stay goes on, as the modules that act on it need to know.</summary>
public enum StayStatus
{
    /// <summary>The patient is in a bed: admitted, or with the discharge begun.</summary>
    Open = 0,

    /// <summary>The stay has ended, however it ended: discharged, referred, against advice, absconded or died.</summary>
    Discharged,

    /// <summary>The admission was made in error, or the patient never came.</summary>
    Cancelled,
}

/// <summary>An admission as the modules below Inpatient see it: its number, its patient and whether the stay goes on.</summary>
public sealed record StaySummary(Guid AdmissionId, string AdmissionNumber, Guid PatientId, StayStatus Status);

/// <summary>
/// Implemented by the Inpatient module, which keeps the admissions, for the modules below it that act on a stay: Clinical
/// takes no order for a stay that has ended.
/// </summary>
public interface IAdmissionDirectory
{
    /// <summary>The admission with this id, in any facility; null when there is none.</summary>
    Task<StaySummary?> FindAsync(Guid admissionId, CancellationToken cancellationToken);
}

/// <summary>Fallback used until the Inpatient module registers its directory: nothing is known of any admission.</summary>
internal sealed class NullAdmissionDirectory : IAdmissionDirectory
{
    public Task<StaySummary?> FindAsync(Guid admissionId, CancellationToken cancellationToken) => Task.FromResult<StaySummary?>(null);
}
