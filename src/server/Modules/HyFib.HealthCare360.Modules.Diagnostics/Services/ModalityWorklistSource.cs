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
    public const int MaxItems = 1000;

    public async Task<IReadOnlyList<ModalityWorklistItem>> FindAsync(ModalityWorklistQuery query, CancellationToken cancellationToken)
    {
        var (first, last) = query.From <= query.To ? (query.From, query.To) : (query.To, query.From);
        var start = DiagnosticsTime.StartOfDayUtc(first);
        var end = DiagnosticsTime.StartOfDayUtc(last.AddDays(1));
        var modalities = query.Modalities.SelectMany(DicomConventions.ModalitiesFor).Distinct().ToList();
        if (query.Modalities.Count > 0 && modalities.Count == 0)
        {
            return [];
        }

        var studies = db.Set<ImagingStudy>().AsNoTracking()
            .Where(s => s.FacilityId == query.FacilityId)
            .Where(s => s.Status == ImagingStudyStatus.Ordered || s.Status == ImagingStudyStatus.Scheduled
                        || s.Status == ImagingStudyStatus.Arrived || s.Status == ImagingStudyStatus.InProgress)
            .Where(s => (s.ArrivedAt ?? s.ScheduledAt ?? s.OrderedAt) >= start && (s.ArrivedAt ?? s.ScheduledAt ?? s.OrderedAt) < end);
        if (modalities.Count > 0)
        {
            studies = studies.Where(s => modalities.Contains(s.Modality));
        }

        if (Clean(query.StationAeTitle)?.ToUpperInvariant() is { } station)
        {
            studies = studies.Where(s => s.StationAeTitle == null || s.StationAeTitle == station);
        }

        if (Clean(query.AccessionNumber)?.ToUpperInvariant() is { } accession)
        {
            studies = studies.Where(s => s.AccessionNumber == accession);
        }

        if (Clean(query.PatientId)?.ToUpperInvariant() is { } uhid)
        {
            var patients = db.Set<Patient>().Where(p => p.Uhid == uhid).Select(p => p.Id);
            studies = studies.Where(s => patients.Contains(s.PatientId));
        }

        var rows = await studies
            .OrderBy(s => s.ArrivedAt ?? s.ScheduledAt ?? s.OrderedAt)
            .ThenBy(s => s.Id)
            .Take(Math.Clamp(query.Limit, 1, MaxItems))
            .ToListAsync(cancellationToken);

        var snapshots = await lookups.PatientsAsync(rows.Select(s => s.PatientId).Distinct().ToList(), cancellationToken);
        var doctors = await lookups.PractitionerNamesAsync(rows.Select(s => s.OrderingPractitionerId), cancellationToken);
        return rows
            .Where(s => snapshots.ContainsKey(s.PatientId))
            .Select(s => ModalityWorklistMapping.ToItem(
                s, snapshots[s.PatientId], s.OrderingPractitionerId is { } doctor ? doctors.GetValueOrDefault(doctor) : null))
            .ToList();
    }

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
