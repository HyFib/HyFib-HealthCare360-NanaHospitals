namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>
/// How a teleconsultation was held, which the Telemedicine Practice Guidelines 2020 (section 3.7.4) make a condition of
/// what a doctor prescribes in it: a List A medicine in a first teleconsultation only by video. The module that holds the
/// video calls answers it, so the prescription can ask without referencing that module.
/// </summary>
public interface ITeleconsultModes
{
    /// <summary>
    /// Whether the doctor saw the patient by video on this telehealth visit: a call on it started and never fell back to
    /// audio only. False when no call on it has started, or when it ran by audio alone at any point.
    /// </summary>
    Task<bool> HeldByVideoAsync(Guid visitId, CancellationToken cancellationToken);
}
