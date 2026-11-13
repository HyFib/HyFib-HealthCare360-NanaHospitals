using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Features.Schedules;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Scheduling.Services;

/// <summary>
/// A doctor's leave or block that strands bookings: the staff tell the patients, in one action, by SMS and WhatsApp that
/// the doctor is not available, with a link to choose another time on the portal (<c>appointment.doctor_unavailable</c>,
/// through the outbox and the message templates). Nothing is moved or cancelled: the patient rebooks, or the desk does
/// it on the bookings list. A patient already told about this leave is not told again; the list says who was told, on
/// which channel and how the message fared, and who could not be told and why.
/// SMS in India goes only on a template registered on the DLT portal (TRAI's TCCCPR 2018): when the SMS provider needs
/// the DLT template id and the template has none, the SMS is not queued (it would only fail) and the patient is told on
/// WhatsApp alone; the list says so.
/// </summary>
public sealed class StrandedPatientNotifier(
    HospitalDbContext db,
    ScheduleService schedules,
    AppointmentNotifier notifier,
    ISettingsService settings,
    IAuditLogger audit)
{
    public const string Template = "appointment.doctor_unavailable";

    /// <summary>SMS providers that send without a DLT template id: the training simulator, and Twilio, which sends from abroad.</summary>
    private static readonly string[] NoDltNeeded = ["simulator", "twilio"];

    /// <summary>The bookings the leave or block strands, and whether each patient was told about it.</summary>
    public async Task<Result<StrandedPatients>> ListAsync(Guid exceptionId, CancellationToken ct)
    {
        var exception = await FindAsync(exceptionId, ct);
        if (exception.IsFailure)
        {
            return exception.Error!;
        }

        var stranded = await schedules.StrandedByAsync(exception.Value, ct);
        var sms = await SmsAsync(stranded, ct);
        return await DescribeAsync(exception.Value, stranded, sms, ct);
    }

    /// <summary>
    /// Tells every patient the leave or block strands, not told yet, that the doctor is not available, with the link to
    /// rebook; the list comes back with what was queued.
    /// </summary>
    public async Task<Result<StrandedPatients>> NotifyAsync(Guid exceptionId, CancellationToken ct)
    {
        var found = await FindAsync(exceptionId, ct);
        if (found.IsFailure)
        {
            return found.Error!;
        }

        var exception = found.Value;
        var stranded = await schedules.StrandedByAsync(exception, ct);
        var sms = await SmsAsync(stranded, ct);
        var before = await DescribeAsync(exception, stranded, sms, ct);
        var toTell = before.Patients.Where(p => p.ToldAt is null && p.NotToldReason is null).Select(p => p.AppointmentId).ToHashSet();
        if (toTell.Count == 0)
        {
            return before;
        }

        var appointments = await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(a => toTell.Contains(a.Id))
            .ToListAsync(ct);
        var told = new List<string>();
        foreach (var appointment in appointments.OrderBy(a => a.StartsAt))
        {
            NotificationChannel[] channels = sms.GetValueOrDefault(appointment.FacilityId, SmsRoute.Ready).Sends
                ? [NotificationChannel.Sms, NotificationChannel.WhatsApp]
                : [NotificationChannel.WhatsApp];
            if (await notifier.DoctorUnavailableAsync(appointment, channels, ct))
            {
                told.Add(appointment.Number);
            }
        }

        audit.Add(new AuditEntry(AuditCategories.Data, "scheduling.leave.patients_notified",
            Fit($"Told {told.Count} patient(s) that the doctor is not available ({exception.Type} {exception.FromDate:dd MMM yyyy} to {exception.ToDate:dd MMM yyyy}): {string.Join(", ", told)}", 1000))
        {
            EntityType = nameof(ScheduleException),
            EntityId = exception.Id.ToString(),
            Details = new { appointments = told, exception.PractitionerId },
        });
        await db.SaveChangesAsync(ct);
        return await DescribeAsync(exception, stranded, sms, ct);
    }

    private async Task<Result<ScheduleException>> FindAsync(Guid exceptionId, CancellationToken ct)
    {
        var exception = await db.Set<ScheduleException>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == exceptionId, ct);
        if (exception is null)
        {
            return Error.NotFound("Leave or block", exceptionId);
        }

        return exception.RemovesSlots
            ? exception
            : Error.Rule("schedule.not_leave", "Only a leave or a block takes bookings away; an extra session strands none.");
    }

    private async Task<StrandedPatients> DescribeAsync(
        ScheduleException exception, IReadOnlyList<AffectedAppointment> stranded, Dictionary<Guid, SmsRoute> sms, CancellationToken ct)
    {
        // A message is about this leave when it was queued after the leave was last saved; read by the patients (the index).
        var since = exception.UpdatedAt ?? exception.CreatedAt;
        var patientIds = stranded.Select(s => (Guid?)s.PatientId).Distinct().ToList();
        var logs = (await db.Set<NotificationLog>().AsNoTracking()
                .Where(l => l.TemplateKey == Template && patientIds.Contains(l.PatientId) && l.CreatedAt >= since)
                .Select(l => new { l.PatientId, l.CreatedAt, l.Channel, l.Status, l.Error })
                .ToListAsync(ct))
            .ToLookup(l => l.PatientId!.Value);
        var appointmentIds = stranded.Select(s => s.Id).ToList();
        var contacts = await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(a => appointmentIds.Contains(a.Id))
            .Join(db.Set<Patient>(), a => a.PatientId, p => p.Id, (a, p) => new { a.Id, a.FacilityId, HasPhone = p.Phone != null, HasEmail = p.Email != null })
            .ToDictionaryAsync(x => x.Id, ct);

        // The message names the doctor, whose speciality can reveal the care (a psychiatrist, say), so a record in a
        // protected category on a number another patient shares is not messaged (SharedNumbers): the desk calls instead.
        var hidden = new HashSet<Guid>();
        foreach (var patientId in stranded.Select(s => s.PatientId).Distinct())
        {
            if (await SharedNumbers.HiddenFromNumberAsync(db, patientId, ct))
            {
                hidden.Add(patientId);
            }
        }

        var patients = stranded.Select(s =>
        {
            var sent = logs[s.PatientId].ToList();
            var contact = contacts.GetValueOrDefault(s.Id);
            var notTold = NotToldReason(sent.Count > 0, contact is not null, contact?.HasPhone ?? false, hidden.Contains(s.PatientId));
            return new StrandedPatient(
                s.Id, s.Number, s.PatientId, s.PatientName, s.PhoneMasked, s.StartsAt, s.Status,
                sent.Count == 0 ? null : sent.Min(l => l.CreatedAt),
                [.. sent.OrderBy(l => l.Channel).Select(l => new StrandedNotice(l.Channel, l.Status, l.Error))],
                notTold);
        }).ToList();

        var blocked = sms.Values.FirstOrDefault(r => !r.Sends);
        return new StrandedPatients(exception.Id, blocked is null, blocked?.Note, patients);
    }

    /// <summary>
    /// Why the patient cannot be told by message, for the staff who then call them; null when they were told or can be.
    /// A hidden record gets no reason that names the category.
    /// </summary>
    public static string? NotToldReason(bool alreadyTold, bool hasContact, bool hasPhone, bool hiddenFromNumber)
    {
        if (alreadyTold || !hasContact)
        {
            return null;
        }

        if (!hasPhone)
        {
            return "No mobile number on record: call or write to the patient";
        }

        return hiddenFromNumber ? "Not sent by message: call the patient" : null;
    }

    private sealed record SmsRoute(bool Sends, string? Note)
    {
        public static readonly SmsRoute Ready = new(true, null);
    }

    /// <summary>Per facility of the stranded bookings: whether the SMS goes, by the provider and the template's DLT id.</summary>
    private async Task<Dictionary<Guid, SmsRoute>> SmsAsync(IReadOnlyList<AffectedAppointment> stranded, CancellationToken ct)
    {
        var ids = stranded.Select(s => s.Id).ToList();
        var facilities = await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(a => ids.Contains(a.Id)).Select(a => a.FacilityId).Distinct().ToListAsync(ct);
        var provider = await SmsProviderAsync(ct);
        var templates = await db.Set<NotificationTemplate>().AsNoTracking()
            .Where(t => t.Key == Template && t.Channel == NotificationChannel.Sms && t.IsActive && t.Language == "en")
            .Select(t => new { t.FacilityId, t.DltTemplateId })
            .ToListAsync(ct);
        return facilities.ToDictionary(f => f, f =>
        {
            // The facility's own template comes first, as the renderer picks it.
            var template = templates.FirstOrDefault(t => t.FacilityId == f) ?? templates.FirstOrDefault(t => t.FacilityId == null);
            if (template is null)
            {
                return new SmsRoute(false, "There is no active SMS template for this message (Administration › Message templates), so patients are told on WhatsApp only.");
            }

            return provider is null || NoDltNeeded.Contains(provider) || !string.IsNullOrWhiteSpace(template.DltTemplateId)
                ? SmsRoute.Ready
                : new SmsRoute(false,
                    $"The SMS template {Template} has no DLT template id, which SMS in India needs, so patients are told on WhatsApp only. "
                    + "Register its text on the DLT portal and enter the id (Administration › Message templates).");
        });
    }

    private async Task<string?> SmsProviderAsync(CancellationToken ct)
    {
        if (await settings.GetAsync<JsonElement?>("integrations.sms", cancellationToken: ct) is not { ValueKind: JsonValueKind.Object } value)
        {
            return null;
        }

        var enabled = value.TryGetProperty("enabled", out var on) && on.ValueKind == JsonValueKind.True;
        return enabled && value.TryGetProperty("provider", out var provider) && provider.ValueKind == JsonValueKind.String
            ? provider.GetString()?.Trim().ToLowerInvariant()
            : null;
    }

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
