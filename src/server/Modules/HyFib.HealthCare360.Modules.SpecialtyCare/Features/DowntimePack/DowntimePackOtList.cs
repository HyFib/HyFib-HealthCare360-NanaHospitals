using System.Globalization;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.DowntimePack;

/// <summary>
/// SpecialtyCare's part of the pre-downtime pack: the OT list, theatre by theatre, of the operations booked in the next
/// 24 hours and those under way, with the side to be operated on, so the theatre runs from paper and the WHO checklist is
/// done on the paper form.
/// </summary>
internal sealed class DowntimePackOtList(HospitalDbContext db) : IDowntimePackSource
{
    public async Task<IReadOnlyList<DowntimePackSection>> BuildAsync(DowntimePackContext context, CancellationToken ct)
    {
        var bookings = await (
                from s in db.Set<SurgeryBooking>().AsNoTracking().IgnoreQueryFilters()
                join p in db.Set<Patient>().IgnoreQueryFilters() on s.PatientId equals p.Id
                join t in db.Set<OperationTheatre>().IgnoreQueryFilters() on s.TheatreId equals t.Id into theatres
                from t in theatres.DefaultIfEmpty()
                join surgeon in db.Set<Practitioner>().IgnoreQueryFilters() on s.SurgeonId equals surgeon.Id into surgeons
                from surgeon in surgeons.DefaultIfEmpty()
                join anaesthetist in db.Set<Practitioner>().IgnoreQueryFilters() on s.AnaesthetistId equals anaesthetist.Id into anaesthetists
                from anaesthetist in anaesthetists.DefaultIfEmpty()
                where s.FacilityId == context.FacilityId && !s.IsDeleted
                      && ((s.Status == SurgeryStatus.Scheduled && s.ScheduledStart < context.WindowEnd)
                          || s.Status == SurgeryStatus.InPreOp || s.Status == SurgeryStatus.InProgress)
                orderby s.ScheduledStart
                select new
                {
                    Theatre = t != null ? t.Name : "No theatre yet",
                    s.ScheduledStart,
                    s.BookingNumber,
                    p.FullName,
                    p.Uhid,
                    p.IsVip,
                    s.ProcedureName,
                    s.Laterality,
                    s.SiteLevel,
                    s.Priority,
                    s.AnaesthesiaType,
                    s.Status,
                    Surgeon = surgeon != null ? surgeon.DisplayName : null,
                    Anaesthetist = anaesthetist != null ? anaesthetist.DisplayName : null,
                })
            .ToListAsync(ct);

        var tables = bookings.GroupBy(b => b.Theatre).Select(g => new DowntimePackTable(g.Key, $"{g.Count()} {(g.Count() == 1 ? "case" : "cases")}",
            g.Any(b => b.Laterality != Laterality.NotApplicable) ? "Check the side and site against the consent and the mark on the patient before the WHO Sign-in." : null,
            ["Time", "Patient (UHID)", "Procedure", "Side / level", "Surgeon", "Anaesthetist", "Anaesthesia", "Priority", "Status", "Booking"],
            [.. g.Select(b => (IReadOnlyList<string>)[
                b.ScheduledStart is { } at ? NumberFormat.ToHospitalTime(at).ToString("dd/MM HH:mm", CultureInfo.InvariantCulture) : "",
                context.Patient(b.FullName, b.Uhid, b.IsVip),
                b.ProcedureName,
                b.Laterality == Laterality.NotApplicable ? b.SiteLevel ?? "" : b.Laterality.ToString().ToUpperInvariant() + (b.SiteLevel is null ? "" : ", " + b.SiteLevel),
                b.Surgeon ?? "",
                b.Anaesthetist ?? "",
                b.AnaesthesiaType.ToString(),
                b.Priority.ToString(),
                b.Status.ToString(),
                b.BookingNumber,
            ])])).ToList();
        return
        [
            new DowntimePackSection("ot", "OT list",
                $"Operations booked until {NumberFormat.ToHospitalTime(context.WindowEnd).ToString("d MMM HH:mm", CultureInfo.InvariantCulture)}, and those under way. The WHO checklist and the counts are done on the paper forms and entered when HC360 is back.",
                false, tables),
        ];
    }
}
