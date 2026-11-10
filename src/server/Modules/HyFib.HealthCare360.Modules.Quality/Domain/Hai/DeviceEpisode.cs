using HyFib.HealthCare360.Modules.Quality.Services;

namespace HyFib.HealthCare360.Modules.Quality.Domain.Hai;

/// <summary>The invasive devices whose days NHSN device-associated infection surveillance counts.</summary>
public enum InvasiveDevice
{
    /// <summary>A central venous catheter ending at or near the heart or in a great vessel (CLABSI).</summary>
    CentralLine = 0,

    /// <summary>An indwelling urinary catheter (CAUTI).</summary>
    UrinaryCatheter,

    /// <summary>Mechanical ventilation through a tracheostomy or endotracheal tube (VAP; the episode's end is the extubation).</summary>
    Ventilator,
}

/// <summary>Why a device came out.</summary>
public enum DeviceRemovalReason
{
    /// <summary>Taken out as planned (for a ventilator: a planned extubation).</summary>
    Planned = 0,

    /// <summary>Came out by accident or the patient pulled it out (for a ventilator: an unplanned extubation).</summary>
    Accidental,

    /// <summary>Replaced by a new device of the same kind (a line changed over a wire, a catheter changed).</summary>
    Replaced,
    PatientDied,

    /// <summary>The patient was discharged or transferred out with the device.</summary>
    PatientLeft,
}

/// <summary>
/// One spell of an invasive device in a patient, from insertion (or the start of ventilation) to removal (or
/// extubation): the source of device days and of the device eligibility of a CLABSI, CAUTI or VAP. Kept by the ward and
/// ICU nurses; nothing else in HC360 records lines, catheters or ventilation.
/// </summary>
public sealed class DeviceEpisode : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }
    public Guid AdmissionId { get; set; }
    public InvasiveDevice Device { get; set; }

    /// <summary>Where and what: "Right internal jugular, triple lumen", "Foley 14 Fr", "ETT 7.5".</summary>
    public string? Detail { get; set; }

    /// <summary>The ward the patient was in when the device went in (inserted in theatre: the ward they came back to).</summary>
    public Guid? WardId { get; set; }

    public DateTimeOffset InsertedAt { get; set; }
    public string? InsertedByName { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
    public DeviceRemovalReason? RemovalReason { get; set; }
    public string? RemovedByName { get; set; }
    public string? Notes { get; set; }

    public bool InPlace => RemovedAt is null;

    public Result Remove(DateTimeOffset removedAt, DeviceRemovalReason reason, string? by, DateTimeOffset now)
    {
        if (RemovedAt is not null)
        {
            return Error.Rule("hai.device_removed", "This device is already recorded as removed.");
        }

        if (removedAt > now)
        {
            return Error.Field("removedAt", "The removal cannot be in the future.");
        }

        if (removedAt < InsertedAt)
        {
            return Error.Field("removedAt", "The removal must be after the insertion.");
        }

        RemovedAt = removedAt;
        RemovalReason = reason;
        RemovedByName = by;
        return Result.Success();
    }

    /// <summary>
    /// A planned or accidental extubation: the end of ventilation that the re-intubation indicator watches. Dying,
    /// leaving and a tube change are not extubations.
    /// </summary>
    public bool IsExtubation => Device == InvasiveDevice.Ventilator && RemovalReason is DeviceRemovalReason.Planned or DeviceRemovalReason.Accidental;
}

/// <summary>A device's spell in local calendar days, for the device-day arithmetic.</summary>
/// <param name="FirstDay">Local (IST) day number of the insertion.</param>
/// <param name="LastDay">Local day number of the removal; null while in place.</param>
public readonly record struct DeviceSpan(Guid PatientId, int FirstDay, int? LastDay);

/// <summary>
/// Device days and device eligibility as NHSN counts them, on local (IST) calendar days. A device day is a calendar
/// day on which a patient had the device in place for any part of it, the day of insertion being device day 1; a patient
/// with two central lines on a day has one central-line day. A device-associated infection needs the device in place for
/// more than two consecutive calendar days on the date of event, and in place on that day or the day before.
/// </summary>
public static class DeviceDays
{
    /// <summary>Device days falling in <paramref name="fromDay"/>..<paramref name="toDay"/> (inclusive): each patient's spells merged, so a day counts once per patient.</summary>
    public static int Count(IEnumerable<DeviceSpan> spans, int fromDay, int toDay, int today)
    {
        var total = 0;
        foreach (var patient in spans.GroupBy(s => s.PatientId))
        {
            var days = new HashSet<int>();
            foreach (var span in patient)
            {
                var first = Math.Max(fromDay, span.FirstDay);
                var last = Math.Min(toDay, span.LastDay ?? today);
                for (var day = first; day <= last; day++)
                {
                    days.Add(day);
                }
            }

            total += days.Count;
        }

        return total;
    }

    /// <summary>The device day the date of event falls on (1 = the day of insertion); 0 when the device was not yet in.</summary>
    public static int DayOfDevice(int insertedDay, int eventDay) => eventDay < insertedDay ? 0 : eventDay - insertedDay + 1;

    /// <summary>
    /// Whether an infection on <paramref name="eventDay"/> can be device-associated: the device in for more than two
    /// consecutive calendar days by the date of event (so the event is on device day 3 or later), and in place on the date
    /// of event or removed the day before.
    /// </summary>
    public static bool Eligible(int insertedDay, int? removedDay, int eventDay)
    {
        if (eventDay < insertedDay)
        {
            return false;
        }

        var lastInPlace = removedDay is { } removed ? Math.Min(removed, eventDay) : eventDay;
        var consecutiveDays = lastInPlace - insertedDay + 1;
        return consecutiveDays > 2 && (removedDay is null || removedDay >= eventDay - 1);
    }

    public static DeviceSpan Span(DeviceEpisode episode) =>
        new(episode.PatientId, QualityClock.LocalDate(episode.InsertedAt).DayNumber, episode.RemovedAt is { } r ? QualityClock.LocalDate(r).DayNumber : null);
}
