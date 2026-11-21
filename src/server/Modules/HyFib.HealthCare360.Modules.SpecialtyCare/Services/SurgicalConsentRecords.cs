using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// The patient's consents an operation reads (<see cref="SurgicalConsent"/>), as the front office recorded them under the
/// patient's consents: to a high-risk procedure and to anaesthesia, given or refused, with when each was withdrawn, who
/// recorded it and whether the signed form is attached. Read afresh at every gate (the pre-op consent item, the WHO Sign-in
/// and Time-out, the incision), so a consent withdrawn since the last one counts; the patient journeys read them the same
/// way to say whether an operation goes ahead on an emergency waiver.
/// </summary>
public static class SurgicalConsentRecords
{
    /// <summary>
    /// The patient's consents as <paramref name="booking"/> reads them. A consent another operation's incision relied on
    /// (that booking's <see cref="SurgeryBooking.ProcedureConsentId"/> or <see cref="SurgeryBooking.AnaesthesiaConsentId"/>,
    /// in whichever branch) carries that booking's number, or <see cref="SurgicalConsent.AnotherBranch"/> when it is another
    /// branch's (the patient's operations are listed by branch): a consent is for one operation, so it does not open this
    /// one's gates. The signed form counts as attached only when the file is in the document store and filed to this
    /// patient: an id alone, or another patient's file, is not a form.
    /// </summary>
    public static async Task<IReadOnlyCollection<ConsentOnRecord>> ForBookingAsync(HospitalDbContext db, SurgeryBooking booking, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(booking);
        var consents = await db.Set<PatientConsent>().AsNoTracking()
            .Where(c => c.PatientId == booking.PatientId && (c.Type == ConsentType.HighRiskProcedure || c.Type == ConsentType.Anaesthesia))
            .Select(c => new
            {
                c.Id,
                c.Type,
                c.Granted,
                c.CapturedAt,
                c.RevokedAt,
                c.CapturedByUserId,
                Signed = c.DocumentFileId != null
                         && db.Set<StoredFile>().Any(f => f.Id == c.DocumentFileId && f.PatientId == c.PatientId),
            })
            .ToListAsync(ct);
        if (consents.Count == 0)
        {
            return [];
        }

        var reliedOn = new Dictionary<Guid, string>();
        var others = await db.Set<SurgeryBooking>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(b => b.PatientId == booking.PatientId && b.Id != booking.Id && (b.ProcedureConsentId != null || b.AnaesthesiaConsentId != null))
            .Select(b => new { b.BookingNumber, b.FacilityId, b.ProcedureConsentId, b.AnaesthesiaConsentId })
            .ToListAsync(ct);

        // This branch's operations first: one of them is named when it relied on the consent too.
        foreach (var other in others.OrderBy(o => o.FacilityId == booking.FacilityId ? 0 : 1))
        {
            var label = other.FacilityId == booking.FacilityId ? other.BookingNumber : SurgicalConsent.AnotherBranch;
            foreach (var consentId in new[] { other.ProcedureConsentId, other.AnaesthesiaConsentId }.OfType<Guid>())
            {
                reliedOn.TryAdd(consentId, label);
            }
        }

        var recorders = consents.Where(c => c.CapturedByUserId is not null).Select(c => c.CapturedByUserId!.Value).Distinct().ToList();
        var names = recorders.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Set<User>().AsNoTracking().Where(u => recorders.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        return
        [
            .. consents.Select(c => new ConsentOnRecord(
                c.Id, c.Type == ConsentType.Anaesthesia ? SurgicalConsentKind.Anaesthesia : SurgicalConsentKind.Procedure, c.Granted, c.CapturedAt, c.RevokedAt,
                c.CapturedByUserId is { } by ? names.GetValueOrDefault(by) : null, c.Signed, reliedOn.GetValueOrDefault(c.Id))),
        ];
    }
}
