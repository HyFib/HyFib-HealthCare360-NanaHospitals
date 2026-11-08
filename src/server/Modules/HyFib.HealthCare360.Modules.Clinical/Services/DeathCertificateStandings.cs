using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// A patient's death certificate (MCCD) as it stands, for the ward closing the stay as expired (maker-checker, slice 8):
/// its register number, the stay it names, whether a second doctor has confirmed it (or the facility does not ask for
/// that), when a confirmation was asked for, and whether it is reported to the Registrar.
/// </summary>
public sealed record DeathCertificateStanding(
    Guid Id,
    string SerialNumber,
    Guid? AdmissionId,
    DateTimeOffset DiedAt,
    bool MedicoLegal,
    string CertifiedBy,
    bool ConfirmationRequired,
    string? ConfirmedBy,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? ConfirmationWaitingSince,
    bool Reported)
{
    /// <summary>
    /// Whether the certificate is enough to close the stay as expired: confirmed by a second doctor, reported, or at a
    /// facility that does not ask for a second doctor's confirmation.
    /// </summary>
    public bool Settled => DeathCertificateRule.Settled(ConfirmationRequired, ConfirmedAt is not null, Reported);

    /// <summary>"D-0042, confirmed by Dr Rao", "D-0042, waiting for a second doctor's confirmation", "D-0042, not yet confirmed".</summary>
    public string Words => Settled
        ? ConfirmedBy is { Length: > 0 } by ? $"{SerialNumber}, confirmed by {by}" : Reported ? $"{SerialNumber}, reported to the Registrar" : $"{SerialNumber}, certified by {CertifiedBy}"
        : ConfirmationWaitingSince is not null ? $"{SerialNumber}, waiting for a second doctor's confirmation" : $"{SerialNumber}, not yet confirmed by a second doctor";
}

/// <summary>When a death certificate is enough to close a stay as expired.</summary>
public static class DeathCertificateRule
{
    public static bool Settled(bool confirmationRequired, bool confirmed, bool reported) => confirmed || reported || !confirmationRequired;
}

/// <summary>The death certificate standing for a patient (not entered in error), across facilities, for the Inpatient module.</summary>
public sealed class DeathCertificateStandings(HospitalDbContext db, ISettingsService settings, IApprovals approvals)
{
    public async Task<DeathCertificateStanding?> ForPatientAsync(Guid patientId, CancellationToken ct)
    {
        var death = await db.Set<DeathRecord>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(d => d.PatientId == patientId && d.Status != VitalReportStatus.EnteredInError)
            .OrderByDescending(d => d.CertifiedAt)
            .FirstOrDefaultAsync(ct);
        if (death is null)
        {
            return null;
        }

        var required = await settings.GetAsync<bool?>(VitalRegistrationService.ConfirmationSetting, death.FacilityId, ct) != false;
        var waiting = (await approvals.WaitingSinceAsync(DeathCertificateApproval.Key, nameof(DeathRecord), [death.Id], ct)).TryGetValue(death.Id, out var since)
            ? since
            : (DateTimeOffset?)null;
        return new DeathCertificateStanding(
            death.Id, death.SerialNumber, death.AdmissionId, death.DiedAt, death.MedicoLegal, death.CertifiedBy, required, death.ConfirmedBy, death.ConfirmedAt,
            waiting, death.Status == VitalReportStatus.Reported);
    }
}
