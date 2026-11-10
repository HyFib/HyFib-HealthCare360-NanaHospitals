using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Search;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping;

/// <summary>
/// Reading types measured within a span of instants (From ≤ measured &lt; Before, either open; UTC, as a query takes them).
/// </summary>
internal sealed record ReadingSpan(IReadOnlyList<RpmReadingType> Types, DateTimeOffset? From, DateTimeOffset? Before);

/// <summary>
/// Remote patient monitoring readings → FHIR <c>Observation</c>s (<c>rpm-{readingId}</c>), coded as the clinic's vital
/// signs are (<see cref="VitalSignObservations"/>), so a receiver reads a reading taken at home as it reads one taken here:
/// <list type="bullet">
/// <item>a systolic and a diastolic reading taken together are one blood pressure panel (85354-9, components 8480-6 and
/// 8462-4) under the systolic reading's id; heart rate (8867-4), SpO₂ (59408-5 with 2708-6), temperature (8310-5) and
/// weight (29463-7) carry the FHIR R4 vital-signs profiles;</item>
/// <item>a home glucometer reading is capillary blood glucose by glucometer (41653-7) in the laboratory category, as a
/// bedside glucose is;</item>
/// <item>an ECG patch's rhythm is 8884-9, the code the readings' own intake reads as a rhythm, in the procedure category,
/// with the device's words for it.</item>
/// </list>
/// Each says when it was measured (effective) and when HC360 received it (issued), that the patient measured it
/// (performer), with which device, and how it stood against the limits the care team set for the patient when it arrived
/// (interpretation N, A or AA). The limits themselves are not sent as reference ranges: the care team can change them,
/// and each reading was judged against those of its day.
/// </summary>
internal static class DeviceReadingObservations
{
    public const string RhythmLoinc = "8884-9";

    /// <summary>
    /// The most readings one search loads: about a year and a half of a patient's readings at a few a day. A search that
    /// would load more is refused (<see cref="FhirOutcomes.TooCostly"/>), not cut short.
    /// </summary>
    public const int MaxPerSearch = 5000;

    /// <summary>An ECG patch's rhythm classification: not a vital sign; an electrodiagnostic result.</summary>
    public static readonly VitalSignKind HeartRhythm = new("rhythm", [(RhythmLoinc, "Heart rate rhythm")], "Heart rhythm", VitalSignObservations.Procedure, null);

    public static IReadOnlyList<R4.Observation> Map(IReadOnlyList<DeviceReadingRow> rows)
    {
        var observations = new List<R4.Observation>();

        // At most one systolic and one diastolic reading per enrolment and instant (the readings' unique key).
        foreach (var pair in rows.Where(IsBloodPressure).GroupBy(r => (r.EnrollmentId, r.MeasuredAt)))
        {
            observations.Add(BloodPressure(pair.FirstOrDefault(r => r.Type == RpmReadingType.SystolicBp), pair.FirstOrDefault(r => r.Type == RpmReadingType.DiastolicBp)));
        }

        observations.AddRange(rows.Where(r => !IsBloodPressure(r)).Select(Single));
        return observations;
    }

    /// <summary>The kind of record a reading of this type is, as a record-sharing consent reads it.</summary>
    public static SharedRecordKind KindOf(RpmReadingType type) => SharedRecords.KindOf(Prototypes[type]);

    /// <summary>
    /// The reading types a search could match, judged by its <c>category</c> and <c>code</c> on a reading of each type,
    /// as the search itself will judge them: the types left out cannot match, whatever their values.
    /// </summary>
    public static IReadOnlyList<RpmReadingType> TypesMatching(SearchRequest request)
    {
        var tokens = request.Criteria.OfType<TokenCriterion>().Where(c => c.Parameter is "category" or "code").ToList();
        return [.. Prototypes.Where(p => tokens.All(c => ResourceSearch.Matches(p.Value, c))).Select(p => p.Key)];
    }

    /// <summary>
    /// The span a search's <c>date</c> limits a reading's measurement time to, as bounds a query can use (UTC): From ≤
    /// measured &lt; Before, either open. A reading's time goes to the second, which the search reads as a one-second
    /// range, so a second is added either side: the query keeps every reading the search could match, and the search does
    /// the rest. A <c>ne</c> date is left to the search.
    /// </summary>
    public static (DateTimeOffset? From, DateTimeOffset? Before) MeasuredWithin(SearchRequest request)
    {
        DateTimeOffset? from = null;
        DateTimeOffset? before = null;
        foreach (var date in request.Criteria.OfType<DateCriterion>().Where(c => c.Parameter == "date"))
        {
            var (lower, upper, negate) = date.Value.InstantBounds();
            if (negate)
            {
                continue;
            }

            if (lower is { } low && (from is null || low > from))
            {
                from = low;
            }

            if (upper is { } high && (before is null || high < before))
            {
                before = high;
            }
        }

        return ((from is { } f && f > DateTimeOffset.MinValue.AddSeconds(1) ? f.AddSeconds(-1) : from)?.ToUniversalTime(),
                (before is { } b && b < DateTimeOffset.MaxValue.AddSeconds(-1) ? b.AddSeconds(1) : before)?.ToUniversalTime());
    }

    /// <summary>
    /// What the consents admitting a reader cover of a patient's readings of these types, one span for each consent: the
    /// types whose kind of record (<see cref="KindOf"/>) it covers, measured on the days it covers. A day in hospital time
    /// is a range of instants (India Standard Time keeps no daylight saving), so this is the rule
    /// <see cref="SharingScope.Covers"/> applies to a reading's day, put as a query can apply it to the reading's instant.
    /// </summary>
    public static IReadOnlyList<ReadingSpan> CoveredSpans(IEnumerable<SharingScope> scopes, IReadOnlyCollection<RpmReadingType> types)
    {
        var spans = new List<ReadingSpan>();
        foreach (var scope in scopes)
        {
            List<RpmReadingType> covered = [.. types.Where(type => scope.CoversKind(KindOf(type)))];
            if (covered.Count == 0)
            {
                continue;
            }

            // Every day is on or after the first day there is, and on or before the last.
            spans.Add(new ReadingSpan(
                covered,
                scope.RecordsFrom is { } first && first > DateOnly.MinValue ? FhirTime.StartOfDay(first).ToUniversalTime() : null,
                scope.RecordsTo is { } last && last < DateOnly.MaxValue ? FhirTime.StartOfDay(last.AddDays(1)).ToUniversalTime() : null));
        }

        return spans;
    }

    public static R4.CodeableConcept Interpretation(ReadingSeverity severity) => severity switch
    {
        ReadingSeverity.Normal => FhirElements.Concept(FhirSystems.ObservationInterpretation, "N", "Normal", "Within the limits set for the patient"),
        ReadingSeverity.Warning => FhirElements.Concept(FhirSystems.ObservationInterpretation, "A", "Abnormal", "Outside the limits set for the patient"),
        ReadingSeverity.Critical => FhirElements.Concept(FhirSystems.ObservationInterpretation, "AA", "Critical abnormal", "Beyond the critical limits set for the patient"),
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "Not a reading severity."),
    };

    /// <summary>The device's class of an ECG rhythm (0, 1 or 2), when it sent no words for it.</summary>
    public static string RhythmClass(decimal value) => value switch
    {
        0m => "Normal sinus rhythm",
        1m => "Other or inconclusive rhythm",
        2m => "Atrial fibrillation or worse",
        _ => $"Rhythm class {value}",
    };

    private static bool IsBloodPressure(DeviceReadingRow row) => row.Type is RpmReadingType.SystolicBp or RpmReadingType.DiastolicBp;

    /// <summary>A panel of the readings taken together; one taken alone is a panel with the other stated as absent.</summary>
    private static R4.Observation BloodPressure(DeviceReadingRow? systolic, DeviceReadingRow? diastolic)
    {
        var first = systolic ?? diastolic!;
        var worst = (ReadingSeverity)Math.Max((int)(systolic?.Severity ?? ReadingSeverity.Normal), (int)(diastolic?.Severity ?? ReadingSeverity.Normal));
        var received = systolic is not null && diastolic is not null && diastolic.ReceivedAt > systolic.ReceivedAt ? diastolic.ReceivedAt : first.ReceivedAt;
        var observation = Create(first, VitalSignObservations.BloodPressure, worst, received);
        observation.Component.Add(Component(VitalSignObservations.SystolicCode, "Systolic blood pressure", systolic));
        observation.Component.Add(Component(VitalSignObservations.DiastolicCode, "Diastolic blood pressure", diastolic));
        return observation;
    }

    private static R4.Observation.ComponentComponent Component(string code, string display, DeviceReadingRow? reading)
    {
        var component = new R4.Observation.ComponentComponent
        {
            Code = FhirElements.Concept(FhirSystems.Loinc, code, display, display),
            Value = reading is null ? null : FhirElements.Ucum(FhirElements.Normalize(reading.Value), "mm[Hg]", "mmHg"),
            DataAbsentReason = reading is null ? FhirElements.Concept(FhirSystems.DataAbsentReason, "unknown", "Unknown") : null,
        };
        if (reading is not null)
        {
            component.Interpretation.Add(Interpretation(reading.Severity));
        }

        return component;
    }

    private static R4.Observation Single(DeviceReadingRow row)
    {
        var (kind, value) = row.Type switch
        {
            RpmReadingType.HeartRate => (VitalSignObservations.HeartRate, (R4.DataType)Quantity(row, "/min", "beats/minute")),
            RpmReadingType.SpO2 => (VitalSignObservations.OxygenSaturation, Quantity(row, "%", "%")),
            RpmReadingType.Temperature => (VitalSignObservations.BodyTemperature, Quantity(row, "Cel", "°C")),
            RpmReadingType.Weight => (VitalSignObservations.BodyWeight, Quantity(row, "kg", "kg")),
            RpmReadingType.Glucose => (VitalSignObservations.BloodGlucose, Quantity(row, "mg/dL", "mg/dL")),
            RpmReadingType.EcgRhythm => (HeartRhythm, new R4.CodeableConcept { Text = FhirElements.Clean(row.TextValue) ?? RhythmClass(row.Value) }),
            _ => throw new ArgumentOutOfRangeException(nameof(row), row.Type, "Not a single reading."),
        };
        var observation = Create(row, kind, row.Severity, row.ReceivedAt);
        observation.Value = value;
        return observation;
    }

    private static R4.Quantity Quantity(DeviceReadingRow row, string code, string unit) => FhirElements.Ucum(FhirElements.Normalize(row.Value), code, unit);

    private static R4.Observation Create(DeviceReadingRow row, VitalSignKind kind, ReadingSeverity severity, DateTimeOffset received)
    {
        string[] profiles = kind.Profile is null ? [] : [FhirSystems.VitalSignsProfile + kind.Profile];
        var observation = new R4.Observation
        {
            Id = FhirIds.Of(FhirIds.DeviceReading, row.Id),
            Meta = FhirElements.Meta(received, null, profiles),
            Status = R4.ObservationStatus.Final,
            Category = [VitalSignObservations.Category(kind.Category)],
            Code = new R4.CodeableConcept
            {
                Coding = [.. kind.Codes.Select(c => FhirElements.Coding(FhirSystems.Loinc, c.Code, c.Display))],
                Text = kind.Text,
            },
            Subject = FhirReferences.Patient(row.PatientId),
            Effective = new R4.FhirDateTime(FhirTime.DateTime(row.MeasuredAt)),
            Issued = FhirTime.ToMilliseconds(received),
            Performer = [FhirReferences.Patient(row.PatientId)],
            Device = Device(row),
        };
        observation.Interpretation.Add(Interpretation(severity));
        if (row.Source == ReadingSource.Manual)
        {
            observation.Note.Add(new R4.Annotation
            {
                Text = FhirElements.Clean(row.RecordedByUserName) is { } who
                    ? $"Read out by the patient and entered by {who}."
                    : "Read out by the patient and entered by hand.",
            });
        }

        return observation;
    }

    /// <summary>The device, by name (no Device resource is served), with its serial number for tracing.</summary>
    private static R4.ResourceReference? Device(DeviceReadingRow row)
    {
        if (row.DeviceType is not { } type)
        {
            return null;
        }

        var name = string.Join(' ', new[] { FhirElements.Clean(row.DeviceManufacturer), FhirElements.Clean(row.DeviceModel), DeviceName(type) }.OfType<string>());
        return new R4.ResourceReference
        {
            Type = "Device",
            Display = FhirElements.Clean(row.DeviceSerial) is { } serial ? $"{name} (serial {serial})" : name,
        };
    }

    private static string DeviceName(RpmDeviceType type) => type switch
    {
        RpmDeviceType.BpMonitor => "blood pressure monitor",
        RpmDeviceType.Glucometer => "glucometer",
        RpmDeviceType.PulseOximeter => "pulse oximeter",
        RpmDeviceType.WeightScale => "weighing scale",
        RpmDeviceType.Thermometer => "thermometer",
        RpmDeviceType.EcgPatch => "ECG patch",
        _ => "monitoring device",
    };

    /// <summary>An observation of each reading type, to judge what a search or a consent makes of the type.</summary>
    private static readonly IReadOnlyDictionary<RpmReadingType, R4.Observation> Prototypes = Enum.GetValues<RpmReadingType>().ToDictionary(
        type => type,
        type => Map([new DeviceReadingRow { Id = Guid.Empty, PatientId = Guid.Empty, Type = type, MeasuredAt = DateTimeOffset.UnixEpoch, ReceivedAt = DateTimeOffset.UnixEpoch }])[0]);
}
