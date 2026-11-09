using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// The implants and consumables of a recalled batch recorded against an operation (<see cref="SurgeryMaterial"/>), for the
/// pharmacy's recall trace: the patient, the surgery, its date, the theatre and the surgeon, and an implant's serial number.
/// A line is matched by its OT store item and its lot, which is the batch the store issued when none was written. A line
/// a correction replaced is left out, the corrected one standing for it; a consignment implant, not in the item master, is
/// not found by an item's batch.
/// </summary>
internal sealed class OtRecallTrace(HospitalDbContext db) : IRecallTraceSource
{
    public async Task<IReadOnlyList<RecalledUse>> UsesAsync(Guid itemId, string batchNumber, CancellationToken cancellationToken)
    {
        var rows = await (
                from material in db.Set<SurgeryMaterial>().AsNoTracking()
                join booking in db.Set<SurgeryBooking>().AsNoTracking() on material.SurgeryBookingId equals booking.Id
                where material.SupplyItemId == itemId && material.LotNumber != null && material.LotNumber.ToUpper() == batchNumber
                      && material.SupersededAt == null
                select new
                {
                    material.Kind,
                    At = booking.StartedAt ?? booking.ScheduledStart ?? material.CreatedAt,
                    booking.BookingNumber,
                    Theatre = db.Set<OperationTheatre>().Where(t => t.Id == booking.TheatreId).Select(t => t.Name).FirstOrDefault(),
                    material.PatientId,
                    material.Quantity,
                    material.SerialNumber,
                    booking.ProcedureName,
                    Surgeon = db.Set<Practitioner>().Where(p => p.Id == booking.SurgeonId).Select(p => p.DisplayName).FirstOrDefault(),
                })
            .ToListAsync(cancellationToken);
        return
        [
            .. rows.Select(r => new RecalledUse(
                r.Kind == MaterialKind.Implant ? RecalledUseKind.Implant : RecalledUseKind.Consumable,
                r.At,
                r.BookingNumber,
                r.Theatre ?? "Operation theatre",
                r.PatientId,
                r.Quantity,
                r.SerialNumber,
                r.ProcedureName,
                r.Surgeon)),
        ];
    }
}
