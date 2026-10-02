using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Notifications;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.VirtualCare.Seed;

/// <summary>
/// Reference data (always, idempotent): the four standard RPM programmes and the notification templates this module
/// sends beyond the platform defaults. Demo data (only with <c>HealthCare360:SeedDemoData</c>): video sessions for the
/// demo telehealth appointments, and two monitored patients with devices, two weeks of readings and open alerts.
/// Demo device keys are generated and discarded — rotate a key in the UI to connect a real device.
/// </summary>
internal sealed class VirtualCareSeeder(IOptions<PlatformOptions> options, TimeProvider time) : ISeeder
{
    public int Order => 120;

    private static readonly (string Code, string Name, RpmProgramKind Kind, string Description, int PerDay)[] Programs =
    [
        ("HTN", "Hypertension control", RpmProgramKind.Hypertension, "Home blood pressure twice a day; the physician titrates medicines remotely.", 2),
        ("DM", "Diabetes care", RpmProgramKind.Diabetes, "Fasting and post-meal glucose with blood pressure for patients on insulin or oral drugs.", 2),
        ("CARD", "Cardiac care", RpmProgramKind.Cardiac, "Heart failure and post-MI follow-up: pulse, SpO2, blood pressure, weight and ECG rhythm.", 1),
        ("POSTDC", "Post-discharge recovery", RpmProgramKind.PostDischarge, "Temperature, SpO2, pulse and blood pressure for the first weeks at home after discharge.", 2),
    ];

    internal static readonly (string Key, NotificationChannel Channel, string Body, string Description)[] Templates =
    [
        ("rpm.alert", NotificationChannel.WhatsApp,
            "HC360 RPM alert: {{patient}} reported {{reading}} at {{time}}. Please review it in HC360.",
            "Remote monitoring alert to the care team (WhatsApp)"),
        ("rpm.device_silent", NotificationChannel.Sms,
            "Dear {{name}}, we have not received readings from your {{device}} since {{since}}. Please take a reading today or call us if you need help. - {{hospital}}",
            "RPM device silent: reminder to the patient"),
        ("rpm.device_silent", NotificationChannel.WhatsApp,
            "Dear {{name}}, we have not received readings from your {{device}} since {{since}}. Please take a reading today, or reply if you need help.",
            "RPM device silent: reminder to the patient (WhatsApp)"),
        ("prescription.shared", NotificationChannel.Sms,
            "Dear {{name}}, your prescription from {{doctor}} dated {{date}} is available on the patient portal. - {{hospital}}",
            "Prescription shared after a video consultation"),
    ];

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        await SeedProgramsAsync(db, cancellationToken);
        await SeedTemplatesAsync(db, cancellationToken);
        if (!options.Value.SeedDemoData)
        {
            return;
        }

        await SeedTelehealthSessionsAsync(db, cancellationToken);
        await SeedMonitoringAsync(db, cancellationToken);
    }

    private static async Task SeedProgramsAsync(HospitalDbContext db, CancellationToken ct)
    {
        var codes = await db.Set<RpmProgram>().Select(p => p.Code).ToListAsync(ct);
        foreach (var program in Programs.Where(p => !codes.Contains(p.Code)))
        {
            db.Add(new RpmProgram
            {
                Code = program.Code,
                Name = program.Name,
                Kind = program.Kind,
                Description = program.Description,
                ExpectedReadingsPerDay = program.PerDay,
            });
        }

        await db.SaveChangesAsync(ct);
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

    /// <summary>Demo telehealth appointments are seeded without events (no SMS), so their sessions are created here.</summary>
    private static async Task SeedTelehealthSessionsAsync(HospitalDbContext db, CancellationToken ct)
    {
        var withSession = db.Set<TelehealthSession>().IgnoreQueryFilters().Where(s => s.AppointmentId != null).Select(s => s.AppointmentId);
        var appointments = await db.Set<Appointment>()
            .Where(a => a.IsTelehealth && (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.Confirmed) && !withSession.Contains(a.Id))
            .ToListAsync(ct);
        foreach (var appointment in appointments)
        {
            var session = TelehealthSession.Schedule(
                appointment.FacilityId, appointment.PatientId, appointment.PractitionerId, appointment.DepartmentId, appointment.StartsAt, appointment.EndsAt,
                appointment.Id, appointment.Number);
            _ = session.IssueJoinToken();
            db.Add(session);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task SeedMonitoringAsync(HospitalDbContext db, CancellationToken ct)
    {
        if (await db.Set<RpmEnrollment>().IgnoreQueryFilters().AnyAsync(ct))
        {
            return;
        }

        var facilityId = await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => (Guid?)f.Id).FirstOrDefaultAsync(ct);
        var patients = await db.Set<Patient>().Where(p => p.IsActive && !p.IsDeceased).OrderByDescending(p => p.DateOfBirth == null ? 0 : 1).ThenBy(p => p.DateOfBirth).Take(2).ToListAsync(ct);
        var doctors = await db.Set<Practitioner>().Where(p => p.IsActive && p.Kind == PractitionerKind.Consultant).OrderBy(p => p.Code).ToListAsync(ct);
        var programs = await db.Set<RpmProgram>().ToDictionaryAsync(p => p.Code, ct);
        if (facilityId is null || patients.Count < 2 || doctors.Count == 0)
        {
            return;
        }

        var physician = doctors.FirstOrDefault(d => d.Code == "DR001") ?? doctors[0];
        var cardiologist = doctors.FirstOrDefault(d => d.Code == "DR005") ?? physician;
        var today = time.Today();
        var now = time.GetUtcNow();

        // Elderly hypertensive patient with a BP monitor (twice daily), one critical reading last evening.
        var hypertension = Enroll(db, facilityId.Value, patients[0], programs["HTN"], [physician.Id], today.AddDays(-21), "Keep home BP below 140/90");
        var htnLimits = DefaultThresholds.For(RpmProgramKind.Hypertension);
        var cuff = Device(db, facilityId.Value, "OMRON-HEM7156-00231", RpmDeviceType.BpMonitor, "Omron", "HEM-7156", hypertension, now);
        var bp = new List<RpmReading>();
        for (var day = 13; day >= 0; day--)
        {
            foreach (var (hour, bump) in new[] { (7, 0), (19, 6) })
            {
                var at = HospitalCalendar.India.ToUtc(today.AddDays(-day), new TimeOnly(hour, 30));
                if (at > now)
                {
                    continue;
                }

                var wave = Math.Sin(day * 0.9 + hour);
                var critical = day == 1 && hour == 19;
                bp.Add(Reading(hypertension, htnLimits, cuff, RpmReadingType.SystolicBp, critical ? 184m : Round(134 + (10 * wave) + bump), at));
                bp.Add(Reading(hypertension, htnLimits, cuff, RpmReadingType.DiastolicBp, critical ? 112m : Round(84 + (6 * wave) + (bump / 2.0)), at));
                bp.Add(Reading(hypertension, htnLimits, cuff, RpmReadingType.HeartRate, Round(76 + (7 * Math.Cos(day))), at));
            }
        }

        Store(db, hypertension, bp, now);

        // Heart-failure patient with a pulse oximeter and a weighing scale (daily), SpO2 dipping to 91 % today.
        var cardiac = Enroll(db, facilityId.Value, patients[1], programs["CARD"], [cardiologist.Id, physician.Id], today.AddDays(-10), "Daily weight; report SpO2 below 94 %");
        var cardiacLimits = DefaultThresholds.For(RpmProgramKind.Cardiac);
        var oximeter = Device(db, facilityId.Value, "MASIMO-MIGHTYSAT-0482", RpmDeviceType.PulseOximeter, "Masimo", "MightySat", cardiac, now);
        var scale = Device(db, facilityId.Value, "DRTRUST-WS-1197", RpmDeviceType.WeightScale, "Dr Trust", "Smart Scale", cardiac, now);
        var vitals = new List<RpmReading>();
        for (var day = 9; day >= 0; day--)
        {
            var at = HospitalCalendar.India.ToUtc(today.AddDays(-day), new TimeOnly(8, 0));
            if (at > now)
            {
                continue;
            }

            vitals.Add(Reading(cardiac, cardiacLimits, oximeter, RpmReadingType.SpO2, day == 0 ? 91m : Round(96 + Math.Sin(day)), at));
            vitals.Add(Reading(cardiac, cardiacLimits, oximeter, RpmReadingType.HeartRate, Round(82 + (8 * Math.Sin(day * 1.3))), at));
            vitals.Add(Reading(cardiac, cardiacLimits, scale, RpmReadingType.Weight, Math.Round(71.4m + (0.12m * (9 - day)), 1), at.AddMinutes(5)));
        }

        Store(db, cardiac, vitals, now);
        await db.SaveChangesAsync(ct);
    }

    private static RpmEnrollment Enroll(HospitalDbContext db, Guid facilityId, Patient patient, RpmProgram program, List<Guid> careTeam, DateOnly start, string goals)
    {
        var enrollment = new RpmEnrollment
        {
            FacilityId = facilityId,
            PatientId = patient.Id,
            ProgramId = program.Id,
            CareTeamPractitionerIds = careTeam,
            PrimaryPractitionerId = careTeam[0],
            Status = RpmEnrollmentStatus.Active,
            StartDate = start,
            Goals = goals,
        };
        db.Add(enrollment);
        foreach (var (type, range) in DefaultThresholds.For(program.Kind))
        {
            var threshold = new RpmThreshold { EnrollmentId = enrollment.Id, Type = type };
            threshold.Apply(range);
            db.Add(threshold);
        }

        return enrollment;
    }

    private static RpmDevice Device(HospitalDbContext db, Guid facilityId, string serial, RpmDeviceType type, string maker, string model, RpmEnrollment enrollment, DateTimeOffset now)
    {
        var device = new RpmDevice { FacilityId = facilityId, SerialNumber = serial, Type = type, Manufacturer = maker, Model = model };
        _ = device.IssueApiKey(now);
        device.AssignTo(enrollment, now.AddDays(-14));
        db.Add(device);
        return device;
    }

    private static RpmReading Reading(
        RpmEnrollment enrollment, IReadOnlyDictionary<RpmReadingType, ThresholdRange> limits, RpmDevice device, RpmReadingType type, decimal value, DateTimeOffset at)
    {
        var severity = ThresholdEvaluator.Evaluate(value, limits.TryGetValue(type, out var range) ? range : null);
        return new RpmReading
        {
            FacilityId = enrollment.FacilityId,
            EnrollmentId = enrollment.Id,
            PatientId = enrollment.PatientId,
            DeviceId = device.Id,
            Type = type,
            Value = value,
            Unit = ReadingUnits.CanonicalUnit(type),
            MeasuredAt = at,
            ReceivedAt = at.AddMinutes(1),
            Severity = severity,
            IsOutOfRange = severity != ReadingSeverity.Normal,
            Source = ReadingSource.Device,
        };
    }

    /// <summary>Adds the readings and the alerts the ingestion service would have raised for them.</summary>
    private static void Store(HospitalDbContext db, RpmEnrollment enrollment, List<RpmReading> readings, DateTimeOffset now)
    {
        var active = new Dictionary<RpmReadingType, RpmAlert>();
        foreach (var reading in readings.OrderBy(r => r.MeasuredAt))
        {
            db.Add(reading);
            enrollment.RecordReading(reading.MeasuredAt);
            if (!reading.IsOutOfRange)
            {
                continue;
            }

            if (active.TryGetValue(reading.Type, out var alert))
            {
                alert.Recur(reading, reading.ReceivedAt);
            }
            else
            {
                alert = RpmAlert.ForReading(enrollment, reading, reading.ReceivedAt);
                active[reading.Type] = alert;
                db.Add(alert);
            }
        }

        // Older warnings have been seen by the nurse; the latest critical ones are still open.
        foreach (var alert in active.Values.Where(a => a.Severity == RpmAlertSeverity.Warning && a.LastOccurredAt < now.AddDays(-2)))
        {
            alert.Acknowledge(null, "Called the patient; advised to repeat the reading after rest.", alert.LastOccurredAt.AddHours(2));
        }
    }

    private static decimal Round(double value) => Math.Round((decimal)value, 0, MidpointRounding.AwayFromZero);
}
