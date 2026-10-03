using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// The DICOM modality worklist (IMG-01, IMG-02): imaging studies still to be done, as worklist entries. The worklist
/// server in Integrations calls it for every modality query, outside any request, so the facility is always filtered
/// here explicitly; the staff app's JSON worklist calls it for the signed-in facility.
/// </summary>
internal sealed class ModalityWorklistSource(HospitalDbContext db, DiagnosticsLookups lookups) : IModalityWorklistSource
{
    /// <summary>Upper bound for one answer, whatever the caller asks for.</summary>
    public const int MaxItems = ModalityWorklistQuery.MaxLimit;

    /// <summary>When the procedure step starts: on arrival, else at the appointment, else when ordered.</summary>
    private static readonly Expression<Func<ImagingStudy, DateTimeOffset>> StartOf =
        s => s.ArrivedAt ?? s.ScheduledAt ?? s.OrderedAt;

    public async Task<IReadOnlyList<ModalityWorklistItem>> FindAsync(
        ModalityWorklistQuery query, CancellationToken cancellationToken)
    {
        var (first, last) = query.From <= query.To ? (query.From, query.To) : (query.To, query.From);
        var modalities = query.Modalities.SelectMany(DicomConventions.ModalitiesFor).Distinct().ToList();
        if (query.Modalities.Count > 0 && modalities.Count == 0)
        {
            return [];
        }

        var studies = db.Set<ImagingStudy>().AsNoTracking()
            .Where(s => s.FacilityId == query.FacilityId)
            .Where(s => s.Status == ImagingStudyStatus.Ordered || s.Status == ImagingStudyStatus.Scheduled
                        || s.Status == ImagingStudyStatus.Arrived || s.Status == ImagingStudyStatus.InProgress)
            .Where(StartsWithin(first, last, query.FromTime, query.ToTime));
        if (modalities.Count > 0)
        {
            studies = studies.Where(s => modalities.Contains(s.Modality));
        }

        if (Clean(query.StationAeTitle)?.ToUpperInvariant() is { } station)
        {
            studies = studies.Where(s => s.StationAeTitle == null || s.StationAeTitle == station);
        }

        if (Clean(query.AccessionNumber) is { } accession)
        {
            studies = Like(accession) is { } pattern
                ? studies.Where(s => EF.Functions.ILike(s.AccessionNumber, pattern))
                : studies.Where(s => s.AccessionNumber == accession.ToUpperInvariant());
        }

        var patients = db.Set<Patient>().AsQueryable();
        var byPatient = false;
        if (Clean(query.PatientId) is { } uhid)
        {
            patients = Like(uhid) is { } pattern
                ? patients.Where(p => EF.Functions.ILike(p.Uhid, pattern))
                : patients.Where(p => p.Uhid == uhid.ToUpperInvariant());
            byPatient = true;
        }

        if (Clean(query.PatientNamePrefix) is { } prefix)
        {
            // The family name component as DicomConventions.PersonName makes it: the surname, or for a patient
            // registered without one, the whole name.
            var pattern = Escape(prefix) + "%";
            patients = patients.Where(p => p.LastName != null && p.LastName != ""
                ? EF.Functions.ILike(p.LastName, pattern)
                : EF.Functions.ILike(p.FirstName + " " + (p.MiddleName ?? ""), pattern));
            byPatient = true;
        }

        if (byPatient)
        {
            var ids = patients.Select(p => p.Id);
            studies = studies.Where(s => ids.Contains(s.PatientId));
        }

        var ordered = query.Around is { } around
            ? studies.OrderBy(s => (s.ArrivedAt ?? s.ScheduledAt ?? s.OrderedAt) < around
                ? around - (s.ArrivedAt ?? s.ScheduledAt ?? s.OrderedAt)
                : (s.ArrivedAt ?? s.ScheduledAt ?? s.OrderedAt) - around)
            : studies.OrderBy(StartOf);
        var rows = await ordered
            .ThenBy(s => s.Id)
            .Skip(Math.Max(0, query.Offset))
            .Take(Math.Clamp(query.Limit, 1, MaxItems))
            .ToListAsync(cancellationToken);

        var snapshots = await lookups.PatientsAsync(rows.Select(s => s.PatientId).Distinct().ToList(),
            cancellationToken);
        var doctors = await lookups.PractitionerNamesAsync(rows.Select(s => s.OrderingPractitionerId),
            cancellationToken);
        return rows
            .Where(s => snapshots.ContainsKey(s.PatientId))
            .Select(s => ModalityWorklistMapping.ToItem(s, snapshots[s.PatientId],
                s.OrderingPractitionerId is { } doctor ? doctors.GetValueOrDefault(doctor) : null))
            .ToList();
    }

    /// <summary>
    /// Studies whose step starts on a day from <paramref name="first"/> to <paramref name="last"/> (hospital time),
    /// and, when a time range is given, within it on that day: one UTC window per day, so the database applies it.
    /// </summary>
    internal static Expression<Func<ImagingStudy, bool>> StartsWithin(
        DateOnly first, DateOnly last, TimeOnly? from, TimeOnly? to)
    {
        if (from is null && to is null)
        {
            var start = DiagnosticsTime.StartOfDayUtc(first);
            var end = DiagnosticsTime.StartOfDayUtc(last.AddDays(1));
            return s => (s.ArrivedAt ?? s.ScheduledAt ?? s.OrderedAt) >= start
                        && (s.ArrivedAt ?? s.ScheduledAt ?? s.OrderedAt) < end;
        }

        var startOf = StartOf.Body;
        Expression? any = null;
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            var (low, high) = Window(day, from, to);
            if (low >= high)
            {
                continue;
            }

            var within = Expression.AndAlso(
                Expression.GreaterThanOrEqual(startOf, Expression.Constant(low)),
                Expression.LessThan(startOf, Expression.Constant(high)));
            any = any is null ? within : Expression.OrElse(any, within);
        }

        return Expression.Lambda<Func<ImagingStudy, bool>>(any ?? Expression.Constant(false), StartOf.Parameters);
    }

    /// <summary>
    /// The UTC window [low, high) of one day's time range; the upper bound takes in the whole last second.
    /// </summary>
    internal static (DateTimeOffset Low, DateTimeOffset High) Window(DateOnly day, TimeOnly? from, TimeOnly? to)
    {
        var dayStart = DiagnosticsTime.StartOfDayUtc(day);
        var low = dayStart + (from ?? TimeOnly.MinValue).ToTimeSpan();
        var high = to is { } until
            ? dayStart + new TimeSpan(until.Hour, until.Minute, until.Second) + TimeSpan.FromSeconds(1)
            : DiagnosticsTime.StartOfDayUtc(day.AddDays(1));
        return (low, high);
    }

    /// <summary>A DICOM wildcard pattern as a LIKE pattern, or null when it has no wildcard (an exact match).</summary>
    internal static string? Like(string pattern) =>
        pattern.Contains('*', StringComparison.Ordinal) || pattern.Contains('?', StringComparison.Ordinal)
            ? Escape(pattern).Replace('*', '%').Replace('?', '_')
            : null;

    /// <summary>Escapes LIKE's own wildcards (PostgreSQL's escape character is the backslash).</summary>
    private static string Escape(string text) =>
        text.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>A study as a worklist entry, in DICOM form (pure; unit-tested).</summary>
internal static class ModalityWorklistMapping
{
    /// <summary>
    /// The procedure step starts when the patient arrived; before that, at the appointment; for an unscheduled order, when
    /// it was ordered. The accession number goes as it is: <see cref="ModalityWorklistItem.AccessionFitsDicom"/> tells the
    /// worklist server whether DICOM can carry it, and the procedure ids are made to fit (<see cref="DicomConventions.ProcedureId"/>).
    /// </summary>
    public static ModalityWorklistItem ToItem(ImagingStudy study, PatientSnapshot patient, string? orderingDoctor)
    {
        var start = DiagnosticsTime.LocalTime(study.ArrivedAt ?? study.ScheduledAt ?? study.OrderedAt);
        var procedureId = DicomConventions.ProcedureId(study.AccessionNumber, study.Id);
        return new ModalityWorklistItem(
            study.Id,
            study.FacilityId,
            study.AccessionNumber,
            procedureId,
            procedureId,
            study.StudyInstanceUid,
            patient.Uhid,
            DicomConventions.PersonName(patient.FirstName, patient.MiddleName, patient.LastName),
            patient.DateOfBirth is { } dob ? DicomConventions.Date(dob) : null,
            DicomConventions.Sex(patient.Gender),
            DicomConventions.ModalityCode(study.Modality),
            study.StationAeTitle,
            DicomConventions.Date(DateOnly.FromDateTime(start.DateTime)),
            DicomConventions.Time(TimeOnly.FromDateTime(start.DateTime)),
            StepStatus(study.Status),
            ShortString(study.Room),
            TextLimits.Clip(study.StudyName, 64),
            BodyPart(study.BodyPart),
            orderingDoctor is null ? null : DicomConventions.PersonName(orderingDoctor, null, null),
            study.Priority switch { OrderPriority.Stat => "STAT", OrderPriority.Urgent => "HIGH", _ => "ROUTINE" },
            study.SourceOrderNumber);
    }

    public static string StepStatus(ImagingStudyStatus status) => status switch
    {
        ImagingStudyStatus.Arrived => "ARRIVED",
        ImagingStudyStatus.InProgress => "STARTED",
        _ => "SCHEDULED",
    };

    /// <summary>Body Part Examined is a DICOM code string (capital letters and digits, 16 at most); one that does not fit is left out.</summary>
    private static string? BodyPart(string? bodyPart)
    {
        var code = new string((bodyPart ?? string.Empty).ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        return code.Length is > 0 and <= DicomConventions.MaxShortStringLength ? code : null;
    }

    /// <summary>A room name as a DICOM short string; one longer than 16 characters, or with a backslash, is left out rather than cut.</summary>
    private static string? ShortString(string? value)
    {
        var text = value?.Trim();
        var fits = text is { Length: > 0 and <= DicomConventions.MaxShortStringLength };
        return fits && !text!.Contains('\\', StringComparison.Ordinal) && !text.Any(char.IsControl) ? text : null;
    }
}
