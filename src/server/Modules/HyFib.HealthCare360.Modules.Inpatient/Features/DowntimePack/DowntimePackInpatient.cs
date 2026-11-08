using System.Globalization;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.DowntimePack;

/// <summary>
/// Inpatient's part of the pre-downtime pack: the census of each ward (every patient in a bed with the diagnosis,
/// allergies and isolation), and each in-patient's medication sheet: the active orders with the doses due in the next
/// 24 hours and a box to sign each one given on paper.
/// <para>
/// When the in-patient ward sheet (the IP workstream's downtime print) reaches main, its per-ward sheet is the source of
/// the census and medication rows here, so the two prints agree; until then these are read directly.
/// </para>
/// </summary>
internal sealed class DowntimePackInpatient(HospitalDbContext db) : IDowntimePackSource
{
    public async Task<IReadOnlyList<DowntimePackSection>> BuildAsync(DowntimePackContext context, CancellationToken ct)
    {
        var facilityId = context.FacilityId;
        var wards = await db.Set<Ward>().AsNoTracking().IgnoreQueryFilters()
            .Where(w => w.FacilityId == facilityId && w.IsActive && !w.IsDeleted)
            .OrderBy(w => w.DisplayOrder).ThenBy(w => w.Name)
            .Select(w => new { w.Id, w.Name, w.Code })
            .ToListAsync(ct);
        var stays = await (
                from a in db.Set<Admission>().AsNoTracking().IgnoreQueryFilters()
                join p in db.Set<Patient>().IgnoreQueryFilters() on a.PatientId equals p.Id
                join b in db.Set<Bed>().IgnoreQueryFilters() on a.BedId equals b.Id
                join r in db.Set<Room>().IgnoreQueryFilters() on b.RoomId equals r.Id into rooms
                from r in rooms.DefaultIfEmpty()
                join c in db.Set<Practitioner>().IgnoreQueryFilters() on a.ConsultantPractitionerId equals c.Id into consultants
                from c in consultants.DefaultIfEmpty()
                where a.FacilityId == facilityId && !a.IsDeleted
                      && (a.Status == AdmissionStatus.Admitted || a.Status == AdmissionStatus.DischargeInitiated)
                orderby b.Label
                select new
                {
                    a.Id,
                    a.PatientId,
                    a.WardId,
                    a.AdmissionNumber,
                    a.AdmittedAt,
                    a.ProvisionalDiagnosis,
                    a.IsMlc,
                    p.Uhid,
                    p.FullName,
                    p.Gender,
                    p.DateOfBirth,
                    p.IsVip,
                    Bed = b.Label,
                    IsolationRoom = r != null && r.IsIsolation,
                    Consultant = c != null ? c.DisplayName : null,
                })
            .ToListAsync(ct);
        var admissionIds = stays.Select(s => s.Id).ToList();
        var patientIds = stays.Select(s => s.PatientId).Distinct().ToList();
        var today = DateOnly.FromDateTime(Platform.Numbering.NumberFormat.ToHospitalTime(context.Now).Date);

        var allergies = (await db.Set<Allergy>().AsNoTracking().IgnoreQueryFilters()
                .Where(x => patientIds.Contains(x.PatientId) && x.Status == AllergyStatus.Active && !x.IsDeleted)
                .OrderByDescending(x => x.Severity).ThenBy(x => x.Allergen)
                .Select(x => new { x.PatientId, x.Allergen, x.Severity, x.Reaction })
                .ToListAsync(ct))
            .ToLookup(x => x.PatientId);
        var noKnown = (await db.Set<NoKnownAllergiesStatement>().AsNoTracking().IgnoreQueryFilters()
                .Where(x => patientIds.Contains(x.PatientId) && !x.IsDeleted).Select(x => x.PatientId).ToListAsync(ct))
            .ToHashSet();
        var isolation = (await db.Set<PatientAlert>().AsNoTracking().IgnoreQueryFilters()
                .Where(x => patientIds.Contains(x.PatientId) && x.IsActive && !x.IsDeleted
                            && (x.Type == PatientAlertType.Isolation || x.Type == PatientAlertType.InfectionControl)
                            && (x.ExpiresOn == null || x.ExpiresOn >= today))
                .Select(x => new { x.PatientId, x.Title })
                .ToListAsync(ct))
            .ToLookup(x => x.PatientId, x => x.Title);
        var orders = (await db.Set<MedicationOrder>().AsNoTracking().IgnoreQueryFilters()
                .Where(o => admissionIds.Contains(o.AdmissionId) && o.Status == MedicationOrderStatus.Active && !o.IsDeleted)
                .OrderBy(o => o.StartAt)
                .ToListAsync(ct))
            .ToLookup(o => o.AdmissionId);
        var doses = (await db.Set<MedicationAdministration>().AsNoTracking().IgnoreQueryFilters()
                .Where(m => admissionIds.Contains(m.AdmissionId) && m.Status == MarStatus.Due && m.ScheduledAt < context.WindowEnd && !m.IsDeleted)
                .OrderBy(m => m.ScheduledAt)
                .Select(m => new { m.MedicationOrderId, m.ScheduledAt })
                .ToListAsync(ct))
            .ToLookup(m => m.MedicationOrderId);

        string AllergyText(Guid patientId) => allergies[patientId].Any()
            ? string.Join("; ", allergies[patientId].Select(a => $"{a.Allergen} ({a.Severity.ToString().ToLowerInvariant()}{(a.Reaction is null ? "" : ", " + a.Reaction)})"))
            : noKnown.Contains(patientId) ? "No known allergies" : "ALLERGIES NOT RECORDED";
        string? IsolationText(Guid patientId, bool room) => isolation[patientId].Any()
            ? string.Join("; ", isolation[patientId])
            : room ? "Isolation room" : null;

        var census = wards.Select(w =>
        {
            var inWard = stays.Where(s => s.WardId == w.Id).ToList();
            var rows = inWard.Select(s => (IReadOnlyList<string>)[
                s.Bed,
                context.Patient(s.FullName, s.Uhid, s.IsVip),
                $"{Age(s.DateOfBirth, today)} / {s.Gender.ToString()[..1]}",
                s.IsVip && !context.CanSeeRestricted ? "" : s.ProvisionalDiagnosis ?? "",
                AllergyText(s.PatientId),
                IsolationText(s.PatientId, s.IsolationRoom) ?? "",
                s.Consultant ?? "",
                $"{s.AdmissionNumber}, day {today.DayNumber - DateOnly.FromDateTime(Platform.Numbering.NumberFormat.ToHospitalTime(s.AdmittedAt).Date).DayNumber + 1}{(s.IsMlc ? ", MLC" : "")}",
            ]).ToList();
            var isolated = inWard.Count(s => IsolationText(s.PatientId, s.IsolationRoom) is not null);
            return new DowntimePackTable($"{w.Name} ({w.Code})", $"{inWard.Count} {(inWard.Count == 1 ? "patient" : "patients")}{(isolated > 0 ? $", {isolated} in isolation" : "")}",
                inWard.Any(s => AllergyText(s.PatientId) == "ALLERGIES NOT RECORDED") ? "Allergies not recorded for some patients: ask before giving any medicine." : null,
                ["Bed", "Patient (UHID)", "Age / sex", "Diagnosis", "Allergies", "Isolation", "Consultant", "Admission"], rows);
        }).Where(t => t.Rows.Count > 0).ToList();

        var sheets = stays.OrderBy(s => wards.FindIndex(w => w.Id == s.WardId)).ThenBy(s => s.Bed).Select(s =>
        {
            var rows = orders[s.Id].Select(o =>
            {
                var due = doses[o.Id].Select(d => Platform.Numbering.NumberFormat.ToHospitalTime(d.ScheduledAt).ToString("dd/MM HH:mm", CultureInfo.InvariantCulture)).ToList();
                var flags = new List<string>();
                if (o.IsHighAlert)
                {
                    flags.Add("high-alert");
                }

                if (o.IsControlledDrug)
                {
                    flags.Add("controlled");
                }

                if (o.CountersignDueAt is not null && o.CountersignedAt is null || o.StopRequestedAt is not null)
                {
                    flags.Add("held: awaiting doctor");
                }

                return (IReadOnlyList<string>)[
                    o.DrugName + (o.Strength is null ? "" : " " + o.Strength),
                    o.Dose,
                    o.Route ?? "",
                    o.Frequency,
                    due.Count == 0 ? "As ordered" : string.Join(", ", due),
                    string.Join(", ", flags),
                    "",
                ];
            }).ToList();
            var ward = wards.FirstOrDefault(w => w.Id == s.WardId)?.Name ?? "";
            return new DowntimePackTable($"{ward}, bed {s.Bed}: {context.Patient(s.FullName, s.Uhid, s.IsVip)}",
                $"Allergies: {AllergyText(s.PatientId)}{(IsolationText(s.PatientId, s.IsolationRoom) is { } iso ? $". Isolation: {iso}" : "")}",
                AllergyText(s.PatientId) == "ALLERGIES NOT RECORDED" ? "ALLERGIES NOT RECORDED" : null,
                ["Medicine", "Dose", "Route", "Frequency", "Due (next 24 h)", "Flags", "Given: time, by (sign)"], rows);
        }).ToList();

        var window = $"{Local(context.Now)} to {Local(context.WindowEnd)}";
        return
        [
            new DowntimePackSection("census", "Census by ward",
                $"Every patient in a bed at {Local(context.Now)}: diagnosis (provisional, from the admission), allergies and isolation.", false, census),
            new DowntimePackSection("mar", "Medication sheets",
                $"Each in-patient's active medication orders and the doses due {window}. Sign each dose given on the sheet; it is entered on the MAR, with the time given, when HC360 is back. A medicine ordered after this print is on paper only.",
                true, sheets),
        ];
    }

    private static string Age(DateOnly? dob, DateOnly today)
    {
        if (dob is not { } born)
        {
            return "";
        }

        var years = today.Year - born.Year - (today < born.AddYears(today.Year - born.Year) ? 1 : 0);
        return years >= 1 ? $"{years} y" : $"{Math.Max(0, (today.DayNumber - born.DayNumber) / 30)} m";
    }

    private static string Local(DateTimeOffset at) => Platform.Numbering.NumberFormat.ToHospitalTime(at).ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
}
