using HyFib.HealthCare360.Modules.VirtualCare.Domain;

namespace HyFib.HealthCare360.Modules.VirtualCare.Services;

/// <summary>
/// Whether a telehealth visit's consultation was by video, for what the doctor may prescribe in it (Telemedicine Practice
/// Guidelines 2020, List A): a call on the visit started, and none on it fell back to audio only, which the call records
/// (<see cref="TelehealthSession.AudioOnlyUsed"/>). A call that ran by audio alone at any point is not counted as video.
/// </summary>
internal sealed class TelehealthConsultModes(HospitalDbContext db) : ITeleconsultModes
{
    public async Task<bool> HeldByVideoAsync(Guid visitId, CancellationToken cancellationToken)
    {
        var calls = await db.Set<TelehealthSession>().AsNoTracking()
            .Where(s => s.VisitId == visitId && s.StartedAt != null)
            .Select(s => s.AudioOnlyUsed)
            .ToListAsync(cancellationToken);
        return calls.Count > 0 && !calls.Any(audioOnly => audioOnly);
    }
}
