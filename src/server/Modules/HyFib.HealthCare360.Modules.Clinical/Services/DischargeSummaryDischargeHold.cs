using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// The discharge summary's word on a discharge (<see cref="IDischargeHoldSource"/>): a planned discharge (home, or referred
/// to another hospital) waits until the stay's discharge summary is signed, so the patient never leaves without it (NABH
/// COP, MOM; Clinical Establishments Rules). A death, a patient who left against advice or absconded keeps its own rules
/// and is not held (<see cref="DischargeHold.PlannedDischargeOnly"/>). On by default; the whole hospital or a facility
/// turns it off through <see cref="Setting"/>, which a second administrator approves (SettingGuard).
/// </summary>
public sealed class DischargeSummaryDischargeHold(HospitalDbContext db, ISettingsService settings, ICurrentUser user) : IDischargeHoldSource
{
    public const string Code = "discharge.summary_unsigned";

    /// <summary>On (the default): a planned discharge waits for the signed discharge summary.</summary>
    public const string Setting = "clinical.discharge.signedSummaryRequired";

    public async Task<IReadOnlyList<DischargeHold>> HoldsAsync(Guid admissionId, CancellationToken cancellationToken)
    {
        var summary = await db.Set<DischargeSummary>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(s => s.AdmissionId == admissionId)
            .Select(s => new { s.FacilityId, s.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (summary is { Status: not DischargeSummaryStatus.Draft })
        {
            return [];
        }

        if (await settings.GetAsync<bool?>(Setting, summary?.FacilityId ?? user.FacilityId, cancellationToken) == false)
        {
            return [];
        }

        return
        [
            new DischargeHold(Code, summary is null
                ? "The discharge summary is not written yet: the doctor writes and signs it before the patient goes home or is referred."
                : "The discharge summary is a draft: the doctor signs it before the patient goes home or is referred.")
            {
                PlannedDischargeOnly = true,
            },
        ];
    }
}
