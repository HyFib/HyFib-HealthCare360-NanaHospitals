using System.Globalization;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping;

/// <summary>
/// The kind (<see cref="SharedRecordKind"/>) and the day of each clinical resource the FHIR API serves, as a
/// record-sharing consent's scope reads them. Read from the resource as it is sent, so what is checked is what would leave.
/// </summary>
internal static class SharedRecords
{
    /// <summary>The kind of record a resource is.</summary>
    public static SharedRecordKind KindOf(R4.Resource resource) => resource switch
    {
        R4.Encounter or R4.Condition => SharedRecordKind.Visits,
        R4.AllergyIntolerance => SharedRecordKind.Allergies,
        R4.MedicationRequest => SharedRecordKind.Medicines,

        // A lab result, or a measurement of the vital-signs set (vital signs, the pain score, the coma scale); a bedside
        // glucose by glucometer is coded as a laboratory observation and goes with the lab results.
        R4.Observation observation => HasCategory(observation.Category, VitalSignObservations.Laboratory)
            ? SharedRecordKind.LabResults
            : SharedRecordKind.VitalSigns,
        R4.DiagnosticReport report => HasCategory(report.Category, "LAB") ? SharedRecordKind.LabResults : SharedRecordKind.ScansAndHeartTests,
        R4.DocumentReference document => KindOf(document),
        R4.Immunization => SharedRecordKind.Immunisations,
        _ => throw new ArgumentOutOfRangeException(nameof(resource), resource.TypeName, "Not a clinical resource of the FHIR API."),
    };

    /// <summary>
    /// The day (hospital time) the record is of: when the visit began, the diagnosis or allergy was recorded, the
    /// medicine prescribed, the sample collected or the scan taken, the document written (a note signed) or the vaccine
    /// given. Null when it has none.
    /// </summary>
    public static DateOnly? DateOf(R4.Resource resource) => resource switch
    {
        R4.Encounter encounter => Day(encounter.Period?.Start),
        R4.Condition condition => Day(condition.RecordedDate) ?? Day((condition.Onset as R4.FhirDateTime)?.Value),
        R4.AllergyIntolerance allergy => Day(allergy.RecordedDate),
        R4.MedicationRequest request => Day(request.AuthoredOn),
        R4.Observation observation => Effective(observation.Effective) ?? Day(observation.Issued),
        R4.DiagnosticReport report => Effective(report.Effective) ?? Day(report.Issued),
        R4.DocumentReference document => Day(document.Date),
        R4.Immunization immunization => Effective(immunization.Occurrence),
        _ => null,
    };

    /// <summary>
    /// A counselling note (whatever its id: the type decides, so it is never shared as something milder), a clinical note,
    /// or a document of the record (a discharge summary or a document filed with it).
    /// </summary>
    private static SharedRecordKind KindOf(R4.DocumentReference document) =>
        DocumentReferenceMapper.IsCounsellingNote(document) ? SharedRecordKind.CounsellingNotes
        : FhirIds.TryParse(document.Id, FhirIds.ClinicalNote, out _) ? SharedRecordKind.ClinicalNotes
        : SharedRecordKind.Documents;

    private static bool HasCategory(List<R4.CodeableConcept> categories, string code) =>
        categories.Any(category => category.Coding.Any(coding => string.Equals(coding.Code, code, StringComparison.Ordinal)));

    private static DateOnly? Effective(R4.DataType? effective) => effective switch
    {
        R4.FhirDateTime dateTime => Day(dateTime.Value),
        R4.Period period => Day(period.Start),
        _ => null,
    };

    private static DateOnly? Day(DateTimeOffset? instant) => instant is { } value ? FhirTime.IstDate(value) : null;

    /// <summary>A FHIR date or dateTime as text: a date stays the date it names; a time is read in IST.</summary>
    private static DateOnly? Day(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.Length == 10 && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant)
            ? FhirTime.IstDate(instant)
            : null;
    }
}
