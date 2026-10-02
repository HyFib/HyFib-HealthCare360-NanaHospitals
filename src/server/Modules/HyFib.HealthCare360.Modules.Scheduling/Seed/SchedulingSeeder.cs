using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Services;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Scheduling.Seed;

/// <summary>
/// Notification templates the module needs beyond the platform defaults (always, idempotent), and — only when
/// <c>HealthCare360:SeedDemoData</c> is true — OPD sessions for every active consultant (Monday–Saturday 09:00–13:00
/// and 17:00–19:00, 15-minute slots) plus a handful of appointments for today and tomorrow.
/// </summary>
internal sealed class SchedulingSeeder(IOptions<PlatformOptions> options, INumberSeries numbers, TimeProvider time) : ISeeder
{
    public int Order => 110;

    private static readonly (string Key, NotificationChannel Channel, string Body, string Description)[] Templates =
    [
        (AppointmentNotifier.CancelledTemplate, NotificationChannel.WhatsApp,
            "Dear {{name}}, your appointment with {{doctor}} on {{date}} at {{time}} (ref {{reference}}) has been cancelled. Reply or call us to book another time.",
            "Appointment cancelled (WhatsApp)"),
        (AppointmentNotifier.WaitlistTemplate, NotificationChannel.Sms,
            "Dear {{name}}, a slot with {{doctor}} is now free on {{date}} at {{time}}. Book it at {{link}} before it is taken. - {{hospital}}",
            "Waitlist: a slot opened"),
        (AppointmentNotifier.WaitlistTemplate, NotificationChannel.WhatsApp,
            "Dear {{name}}, good news: a slot with {{doctor}} is now free on {{date}} at {{time}}. Book it at {{link}} before it is taken.",
            "Waitlist: a slot opened (WhatsApp)"),
    ];

    private static readonly (TimeOnly Start, TimeOnly End)[] DemoSessions =
    [
        (new TimeOnly(9, 0), new TimeOnly(13, 0)),
        (new TimeOnly(17, 0), new TimeOnly(19, 0)),
    ];

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        await SeedTemplatesAsync(db, cancellationToken);
        if (!options.Value.SeedDemoData)
        {
            return;
        }

        await SeedSchedulesAsync(db, cancellationToken);
        await SeedAppointmentsAsync(db, cancellationToken);
    }

    private static async Task SeedTemplatesAsync(HospitalDbContext db, CancellationToken ct)
    {
        var existing = await db.Set<NotificationTemplate>()
            .Where(t => t.FacilityId == null && t.Language == "en")
            .Select(t => new { t.Key, t.Channel })
            .ToListAsync(ct);

        foreach (var template in Templates.Where(t => !existing.Any(e => e.Key == t.Key && e.Channel == t.Channel)))
        {
            db.Add(new NotificationTemplate { Key = template.Key, Channel = template.Channel, Body = template.Body, Description = template.Description });
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task SeedSchedulesAsync(HospitalDbContext db, CancellationToken ct)
    {
        if (await db.Set<DoctorSchedule>().IgnoreQueryFilters().AnyAsync(ct))
        {
            return;
        }

        var facilities = await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Id).ToListAsync(ct);
        if (facilities.Count == 0)
        {
            return;
        }

        var consultants = await db.Set<Practitioner>()
            .Where(p => p.IsActive && p.Kind == PractitionerKind.Consultant && p.DepartmentId != null)
            .OrderBy(p => p.Code)
            .ToListAsync(ct);

        var effectiveFrom = time.Today().AddDays(-30);
        var room = 0;
        foreach (var doctor in consultants)
        {
            room++;
            var facilityId = doctor.FacilityIds.FirstOrDefault(facilities.Contains);
            if (facilityId == Guid.Empty)
            {
                facilityId = facilities[0];
            }

            foreach (var day in new[] { Weekday.Monday, Weekday.Tuesday, Weekday.Wednesday, Weekday.Thursday, Weekday.Friday, Weekday.Saturday })
            {
                foreach (var (start, end) in DemoSessions)
                {
                    db.Add(new DoctorSchedule
                    {
                        FacilityId = facilityId,
                        PractitionerId = doctor.Id,
                        DepartmentId = doctor.DepartmentId!.Value,
                        Weekday = day,
                        StartTime = start,
                        EndTime = end,
                        SlotMinutes = 15,
                        CapacityPerSlot = 1,
                        Mode = doctor.AvailableForTelehealth ? ConsultationMode.Both : ConsultationMode.InPerson,
                        Room = $"OPD-{room:00}",
                        EffectiveFrom = effectiveFrom,
                        Notes = "Demo OPD session",
                    });
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task SeedAppointmentsAsync(HospitalDbContext db, CancellationToken ct)
    {
        if (await db.Set<Appointment>().IgnoreQueryFilters().AnyAsync(ct))
        {
            return;
        }

        var patients = await db.Set<Patient>()
            .Where(p => p.IsActive && p.MergedIntoPatientId == null && !p.IsDeceased)
            .OrderBy(p => p.RegisteredAt)
            .Take(8)
            .ToListAsync(ct);
        var doctors = await db.Set<Practitioner>()
            .Where(p => p.IsActive && p.AvailableForOnlineBooking)
            .OrderBy(p => p.Code)
            .Take(3)
            .ToListAsync(ct);
        if (patients.Count < 8 || doctors.Count == 0)
        {
            return;
        }

        var doctorIds = doctors.Select(d => d.Id).ToList();
        var schedules = await db.Set<DoctorSchedule>().Where(s => doctorIds.Contains(s.PractitionerId) && s.IsActive).ToListAsync(ct);
        var calendar = HospitalCalendar.India;
        var now = time.GetUtcNow();
        var today = time.Today();
        var busy = doctors.ToDictionary(d => d.Id, _ => new List<BusyInterval>());

        // (doctor, patient, day offset, preferred slot index, channel, visit type, video, reason, outcome)
        var plan = new (int Doctor, int Patient, int Day, int Slot, AppointmentChannel Channel, AppointmentVisitType Type, bool Video, string Reason, AppointmentStatus Outcome)[]
        {
            (0, 0, 0, 0, AppointmentChannel.Phone, AppointmentVisitType.New, false, "Fever and body ache for two days", AppointmentStatus.Booked),
            (0, 1, 0, 1, AppointmentChannel.Portal, AppointmentVisitType.FollowUp, false, "Blood pressure review", AppointmentStatus.Confirmed),
            (1, 2, 0, 0, AppointmentChannel.Portal, AppointmentVisitType.New, true, "Child with cough and cold", AppointmentStatus.Booked),
            (0, 3, 1, 0, AppointmentChannel.Phone, AppointmentVisitType.New, false, "Headache and giddiness", AppointmentStatus.Booked),
            (0, 4, 1, 1, AppointmentChannel.Online, AppointmentVisitType.New, false, "Diabetes check-up", AppointmentStatus.Booked),
            (Math.Min(2, doctors.Count - 1), 5, 1, 2, AppointmentChannel.WalkIn, AppointmentVisitType.FollowUp, false, "Knee pain review", AppointmentStatus.Booked),
            (0, 6, 1, 3, AppointmentChannel.Phone, AppointmentVisitType.New, false, "Acidity and stomach pain", AppointmentStatus.Cancelled),
            (1, 7, 1, 4, AppointmentChannel.Portal, AppointmentVisitType.FollowUp, true, "Vaccination follow-up", AppointmentStatus.Booked),
        };

        foreach (var item in plan)
        {
            var doctor = doctors[item.Doctor];
            if (item.Video && !doctor.AvailableForTelehealth)
            {
                continue;
            }

            var date = today.AddDays(item.Day);
            var slots = SlotEngine.Compute(
                schedules.Where(s => s.PractitionerId == doctor.Id).ToList(),
                [],
                busy[doctor.Id],
                new SlotQuery(date, date, now.AddMinutes(15)) { Mode = item.Video ? ConsultationMode.Telehealth : ConsultationMode.InPerson },
                calendar);
            if (slots.Count == 0)
            {
                continue;
            }

            var slot = slots[Math.Min(item.Slot, slots.Count - 1)];
            var number = await numbers.NextAsync(SchedulingNumberSeries.Appointment, slot.FacilityId, ct);
            var appointment = Appointment.Book(
                new AppointmentBooking(
                    slot.FacilityId, patients[item.Patient].Id, doctor.Id, slot.DepartmentId, slot.ScheduleId, slot.StartsAt, slot.EndsAt, 1,
                    item.Channel, item.Type, item.Video, slot.Room,
                    BookingRules.FeeFor(item.Type, doctor.ConsultationFeeNew, doctor.ConsultationFeeFollowUp), item.Reason, null, null),
                number,
                now.AddDays(-1));

            switch (item.Outcome)
            {
                case AppointmentStatus.Confirmed:
                    appointment.Confirm(now);
                    break;
                case AppointmentStatus.Cancelled:
                    appointment.Cancel(CancellationReason.PatientRequest, "Patient is travelling", CancellationSource.Patient, null, now);
                    break;
            }

            if (item.Channel == AppointmentChannel.Portal)
            {
                appointment.ChoosePayAtHospital();
            }

            // Demo rows must not send SMS/WhatsApp: VirtualCare's seeder creates the demo video sessions itself.
            appointment.ClearDomainEvents();
            db.Add(appointment);
            if (appointment.HoldsSeat)
            {
                busy[doctor.Id].Add(new BusyInterval(slot.StartsAt, slot.EndsAt));
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
