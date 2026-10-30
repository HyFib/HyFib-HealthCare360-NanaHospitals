using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// Free treatment of the victim of an offence the law names. Every hospital, public or private, gives the victim of rape
/// or another sexual offence, or of an acid attack, first aid and medical treatment free of cost (Bharatiya Nagarik
/// Suraksha Sanhita 2023, s.397, before it CrPC s.357C); the person in charge of a hospital that does not is punishable
/// (BNS 2023, s.200). The MLC register says who is such a victim. Its entry for a sexual assault or an acid attack marks
/// the case's visit (<see cref="Visit.FreeTreatmentReason"/>), the stay the patient is admitted to from it, and a later
/// visit the desk links to the entry by its MLC number. Billing and the pharmacy charge a marked visit nothing.
/// </summary>
public static class FreeTreatment
{
    /// <summary>Whether the victim of a case of this cause is treated free.</summary>
    public static bool Covers(MlcCause cause) => cause is MlcCause.SexualAssault or MlcCause.AcidAttack;

    /// <summary>
    /// What a marked visit's bills print. It names the law and the MLC entry, never the offence: a bill is seen at the
    /// counter and may be sent to a phone others share, and nothing may make a victim's identity known (BNS 2023, s.72).
    /// </summary>
    public static string Reason(string mlcNumber) => $"BNSS 2023 s.397, MLC no. {mlcNumber}";

    /// <summary>
    /// Marks the visits of an MLC entry as it now stands, saved or not, or no longer marks them: the case's own visit, the
    /// stays the patient was admitted to from it, and the visits linked to it by its number. Does not save.
    /// </summary>
    public static async Task MarkAsync(HospitalDbContext db, MlcRecord mlc, CancellationToken ct)
    {
        var reason = Reason(mlc.Number);
        var number = mlc.Number.ToUpperInvariant();
        var visits = await db.Set<Visit>()
            .Where(v => v.PatientId == mlc.PatientId
                        && (v.Id == mlc.VisitId
                            || (v.Type == VisitType.InPatient && v.PreviousVisitId == mlc.VisitId)
                            || (v.IsMlc && v.MlcNumber != null && v.MlcNumber.ToUpper() == number)))
            .ToListAsync(ct);
        foreach (var visit in visits)
        {
            if (Covers(mlc.Cause))
            {
                visit.FreeTreatmentReason ??= reason;
            }
            else if (visit.FreeTreatmentReason == reason)
            {
                visit.FreeTreatmentReason = null;
            }
        }
    }

    /// <summary>
    /// Why a visit other than an MLC case's own is treated free, from the register as saved: a stay goes on as the visit
    /// the patient was admitted from, and a visit linked to an entry by its MLC number is free when that entry's case is.
    /// <c>null</c> when it is billed as usual.
    /// </summary>
    public static async Task<string?> ForVisitAsync(HospitalDbContext db, Visit visit, CancellationToken ct)
    {
        if (visit.Type == VisitType.InPatient && visit.PreviousVisitId is { } sourceId
            && await db.Set<Visit>().Where(v => v.Id == sourceId && v.PatientId == visit.PatientId).Select(v => v.FreeTreatmentReason).FirstOrDefaultAsync(ct)
                is { } carried)
        {
            return carried;
        }

        var number = visit.IsMlc ? visit.MlcNumber?.Trim().ToUpperInvariant() : null;
        if (string.IsNullOrEmpty(number))
        {
            return null;
        }

        var entry = await db.Set<MlcRecord>().AsNoTracking()
            .Where(m => m.PatientId == visit.PatientId && m.Number.ToUpper() == number)
            .Select(m => new { m.Number, m.Cause })
            .FirstOrDefaultAsync(ct);
        return entry is not null && Covers(entry.Cause) ? Reason(entry.Number) : null;
    }
}

/// <summary>
/// A new visit of a victim treated free goes on free (BNSS 2023, s.397), in the visit's own transaction: the stay the
/// patient is admitted to from a marked visit, and a visit the desk links to the victim's MLC entry by its number.
/// </summary>
internal sealed class FreeTreatmentCarrier(HospitalDbContext db) : IDomainEventHandler<VisitCreated>
{
    public async Task HandleAsync(VisitCreated created, CancellationToken cancellationToken)
    {
        if (await db.Set<Visit>().FindAsync([created.VisitId], cancellationToken) is { FreeTreatmentReason: null } visit)
        {
            visit.FreeTreatmentReason = await FreeTreatment.ForVisitAsync(db, visit, cancellationToken);
        }
    }
}
