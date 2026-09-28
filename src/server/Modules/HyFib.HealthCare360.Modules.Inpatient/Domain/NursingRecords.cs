namespace HyFib.HealthCare360.Modules.Inpatient.Domain;

public enum NursingShift
{
    Morning = 0,
    Evening,
    Night,
}

/// <summary>
/// Nursing shifts of the hospital day: Morning 08:00–14:00, Evening 14:00–20:00, Night 20:00–08:00 (IST). The duty date
/// of a night shift is the date it started, so a note written at 02:00 lands on the same handover sheet as the rest of
/// that night.
/// </summary>
public static class NursingShifts
{
    public static TimeOnly MorningStart { get; } = new(8, 0);
    public static TimeOnly EveningStart { get; } = new(14, 0);
    public static TimeOnly NightStart { get; } = new(20, 0);

    public static NursingShift At(TimeOnly localTime) =>
        localTime >= MorningStart && localTime < EveningStart ? NursingShift.Morning
        : localTime >= EveningStart && localTime < NightStart ? NursingShift.Evening
        : NursingShift.Night;

    public static NursingShift At(DateTimeOffset instant) => At(IndiaClock.LocalTime(instant));

    public static DateOnly DutyDate(DateTimeOffset instant)
    {
        var date = IndiaClock.LocalDate(instant);
        return IndiaClock.LocalTime(instant) < MorningStart ? date.AddDays(-1) : date;
    }

    /// <summary>Start of a shift on a duty date (UTC).</summary>
    public static DateTimeOffset StartOf(DateOnly dutyDate, NursingShift shift) => shift switch
    {
        NursingShift.Morning => IndiaClock.AtLocal(dutyDate, MorningStart),
        NursingShift.Evening => IndiaClock.AtLocal(dutyDate, EveningStart),
        _ => IndiaClock.AtLocal(dutyDate, NightStart),
    };
}

public enum NursingNoteCategory
{
    General = 0,
    Observation,
    Procedure,
    Incident,
    Education,
}

/// <summary>
/// A nursing progress note, signed by the nurse who wrote it. Notes are never edited (medico-legal record): corrections
/// are new notes. A note about an earlier time than its writing is flagged as a late entry.
/// </summary>
public sealed class NursingNote : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid AdmissionId { get; set; }
    public Guid PatientId { get; set; }
    public DateOnly DutyDate { get; set; }
    public NursingShift Shift { get; set; }
    public NursingNoteCategory Category { get; set; }
    public string Note { get; set; } = string.Empty;

    /// <summary>The time the note is about.</summary>
    public DateTimeOffset NoteFor { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
    public Guid? RecordedBy { get; set; }
    public string? RecordedByName { get; set; }
    public bool IsLateEntry { get; set; }

    /// <summary>A note is late when written in a later shift than the one it describes.</summary>
    public static bool IsLate(DateTimeOffset noteFor, DateTimeOffset recordedAt) =>
        NursingShifts.DutyDate(noteFor) != NursingShifts.DutyDate(recordedAt) || NursingShifts.At(noteFor) != NursingShifts.At(recordedAt);
}

/// <summary>
/// Patient handover between shifts in SBAR form (Situation, Background, Assessment, Recommendation), the structure NABH
/// recommends for safe transfer of responsibility. The receiving nurse acknowledges it, which closes the loop.
/// </summary>
public sealed class ShiftHandover : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid AdmissionId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? WardId { get; set; }
    public DateOnly DutyDate { get; set; }
    public NursingShift FromShift { get; set; }
    public string Situation { get; set; } = string.Empty;
    public string? Background { get; set; }
    public string? Assessment { get; set; }
    public string? Recommendation { get; set; }
    public string? PendingTasks { get; set; }
    public DateTimeOffset HandedOverAt { get; set; }
    public Guid? HandedOverBy { get; set; }
    public string? HandedOverByName { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public Guid? AcknowledgedBy { get; set; }
    public string? AcknowledgedByName { get; set; }

    public Result Acknowledge(Guid? userId, string? userName, DateTimeOffset now)
    {
        if (AcknowledgedAt is not null)
        {
            return Error.Rule("handover.acknowledged", "This handover has already been acknowledged.");
        }

        if (userId is not null && userId == HandedOverBy)
        {
            return Error.Rule("handover.self", "The receiving nurse must acknowledge the handover, not the nurse handing over.");
        }

        AcknowledgedAt = now;
        AcknowledgedBy = userId;
        AcknowledgedByName = userName;
        return Result.Success();
    }
}
