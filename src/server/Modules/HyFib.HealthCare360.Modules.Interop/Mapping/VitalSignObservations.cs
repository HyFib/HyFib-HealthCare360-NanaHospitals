using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping;

/// <summary>One kind of measurement in a vital-signs set and how it is coded in FHIR.</summary>
/// <param name="Suffix">Id suffix: <c>Observation/vs-{vitalsId}-{Suffix}</c>.</param>
/// <param name="Codes">LOINC codes (the first is the primary code) with their LOINC display names.</param>
/// <param name="Text">Human-readable name.</param>
/// <param name="Category">observation-category code.</param>
/// <param name="Profile">FHIR R4 vital-signs profile name, when the measurement has one.</param>
internal sealed record VitalSignKind(string Suffix, IReadOnlyList<(string Code, string Display)> Codes, string Text, string Category, string? Profile);

/// <summary>
/// Clinical vitals → FHIR <c>Observation</c>s, one per recorded measurement, coded with the LOINC codes and UCUM units of
/// the FHIR R4 vital-signs profiles: blood pressure panel 85354-9 with systolic 8480-6 and diastolic 8462-4 components,
/// heart rate 8867-4, SpO₂ 59408-5 (+ 2708-6), body temperature 8310-5, respiratory rate 9279-1, body weight 29463-7,
/// body height 8302-2 and BMI 39156-5. Pain score, GCS and bedside glucose are exported with their LOINC codes too.
/// A set marked "entered in error" keeps its values with status <c>entered-in-error</c> so receivers can retract it.
/// </summary>
internal static class VitalSignObservations
{
    public const string VitalSigns = "vital-signs";
    public const string Laboratory = "laboratory";
    public const string Survey = "survey";
    public const string Exam = "exam";

    public static readonly VitalSignKind BloodPressure = new("bp", [("85354-9", "Blood pressure panel with all children optional")], "Blood pressure", VitalSigns, "bp");
    public static readonly VitalSignKind HeartRate = new("hr", [("8867-4", "Heart rate")], "Heart rate", VitalSigns, "heartrate");
    public static readonly VitalSignKind OxygenSaturation = new("spo2",
        [("59408-5", "Oxygen saturation in Arterial blood by Pulse oximetry"), ("2708-6", "Oxygen saturation in Arterial blood")],
        "Oxygen saturation (SpO2)", VitalSigns, "oxygensat");
    public static readonly VitalSignKind BodyTemperature = new("temp", [("8310-5", "Body temperature")], "Body temperature", VitalSigns, "bodytemp");
    public static readonly VitalSignKind RespiratoryRate = new("rr", [("9279-1", "Respiratory rate")], "Respiratory rate", VitalSigns, "resprate");
    public static readonly VitalSignKind BodyWeight = new("wt", [("29463-7", "Body weight")], "Body weight", VitalSigns, "bodyweight");
    public static readonly VitalSignKind BodyHeight = new("ht", [("8302-2", "Body height")], "Body height", VitalSigns, "bodyheight");
    public static readonly VitalSignKind BodyMassIndex = new("bmi", [("39156-5", "Body mass index (BMI) [Ratio]")], "Body mass index", VitalSigns, "bmi");
    public static readonly VitalSignKind PainScore = new("pain", [("72514-3", "Pain severity - 0-10 verbal numeric rating [Score] - Reported")], "Pain score (0-10)", Survey, null);
    public static readonly VitalSignKind GlasgowComaScore = new("gcs", [("9269-2", "Glasgow coma score total")], "Glasgow coma score", Exam, null);
    public static readonly VitalSignKind BloodGlucose = new("glucose", [("41653-7", "Glucose [Mass/volume] in Capillary blood by Glucometer")], "Blood glucose", Laboratory, null);

    public const string SystolicCode = "8480-6";
    public const string DiastolicCode = "8462-4";

    public static IReadOnlyList<VitalSignKind> All { get; } =
        [BloodPressure, HeartRate, OxygenSaturation, BodyTemperature, RespiratoryRate, BodyWeight, BodyHeight, BodyMassIndex, PainScore, GlasgowComaScore, BloodGlucose];

    /// <summary>Every measurement recorded in the set, in a fixed order.</summary>
    public static IReadOnlyList<R4.Observation> Map(VitalSignsRow v)
    {
        var observations = new List<R4.Observation>();
        if (v.SystolicBp is not null || v.DiastolicBp is not null)
        {
            observations.Add(BloodPressurePanel(v));
        }

        if (v.Pulse is { } pulse)
        {
            observations.Add(WithValue(v, HeartRate, FhirElements.Ucum(pulse, "/min", "beats/minute")));
        }

        if (v.SpO2 is { } spo2)
        {
            var observation = WithValue(v, OxygenSaturation, FhirElements.Ucum(spo2, "%", "%"));
            if (v.OnSupplementalOxygen)
            {
                observation.Note.Add(new R4.Annotation { Text = "Measured on supplemental oxygen." });
            }

            observations.Add(observation);
        }

        if (v.Temperature is { } temperature)
        {
            observations.Add(WithValue(v, BodyTemperature, v.TemperatureUnit == TemperatureUnit.Fahrenheit
                ? FhirElements.Ucum(temperature, "[degF]", "°F")
                : FhirElements.Ucum(temperature, "Cel", "°C")));
        }

        if (v.RespiratoryRate is { } respiratoryRate)
        {
            observations.Add(WithValue(v, RespiratoryRate, FhirElements.Ucum(respiratoryRate, "/min", "breaths/minute")));
        }

        if (v.WeightKg is { } weight)
        {
            observations.Add(WithValue(v, BodyWeight, FhirElements.Ucum(weight, "kg", "kg")));
        }

        if (v.HeightCm is { } height)
        {
            observations.Add(WithValue(v, BodyHeight, FhirElements.Ucum(height, "cm", "cm")));
        }

        if (v.Bmi is { } bmi)
        {
            var observation = WithValue(v, BodyMassIndex, FhirElements.Ucum(bmi, "kg/m2", "kg/m2"));
            if (v.WeightKg is not null)
            {
                observation.DerivedFrom.Add(FhirReferences.Observation(Id(v.Id, BodyWeight)));
            }

            if (v.HeightCm is not null)
            {
                observation.DerivedFrom.Add(FhirReferences.Observation(Id(v.Id, BodyHeight)));
            }

            observations.Add(observation);
        }

        if (v.PainScore is { } pain)
        {
            observations.Add(WithValue(v, PainScore, FhirElements.Ucum(pain, "{score}", "score")));
        }

        if (v.Gcs is { } gcs)
        {
            observations.Add(WithValue(v, GlasgowComaScore, FhirElements.Ucum(gcs, "{score}", "score")));
        }

        if (v.BloodGlucose is { } glucose)
        {
            var observation = WithValue(v, BloodGlucose, FhirElements.Ucum(glucose, "mg/dL", "mg/dL"));
            observation.Code.Text = GlucoseText(v.GlucoseTiming);
            observations.Add(observation);
        }

        return observations;
    }

    public static string Id(Guid vitalsId, VitalSignKind kind) => $"{FhirIds.Of(FhirIds.Vitals, vitalsId)}-{kind.Suffix}";

    public static VitalSignKind? KindOf(string suffix) => All.FirstOrDefault(k => k.Suffix == suffix);

    private static R4.Observation BloodPressurePanel(VitalSignsRow v)
    {
        var observation = Create(v, BloodPressure);
        observation.Component.Add(Component(SystolicCode, "Systolic blood pressure", v.SystolicBp));
        observation.Component.Add(Component(DiastolicCode, "Diastolic blood pressure", v.DiastolicBp));
        return observation;
    }

    /// <summary>A missing systolic or diastolic reading is stated with a data-absent reason, as the bp profile requires.</summary>
    private static R4.Observation.ComponentComponent Component(string code, string display, int? mmHg) => new()
    {
        Code = FhirElements.Concept(FhirSystems.Loinc, code, display, display),
        Value = mmHg is { } value ? FhirElements.Ucum(value, "mm[Hg]", "mmHg") : null,
        DataAbsentReason = mmHg is null ? FhirElements.Concept(FhirSystems.DataAbsentReason, "unknown", "Unknown") : null,
    };

    private static R4.Observation WithValue(VitalSignsRow v, VitalSignKind kind, R4.Quantity value)
    {
        var observation = Create(v, kind);
        observation.Value = value;
        return observation;
    }

    private static R4.Observation Create(VitalSignsRow v, VitalSignKind kind)
    {
        string[] profiles = kind.Profile is null ? [] : [FhirSystems.VitalSignsProfile + kind.Profile];
        return new R4.Observation
        {
            Id = Id(v.Id, kind),
            Meta = FhirElements.Meta(v.CreatedAt, v.UpdatedAt, profiles),
            Status = v.IsEnteredInError ? R4.ObservationStatus.EnteredInError : R4.ObservationStatus.Final,
            Category = [Category(kind.Category)],
            Code = new R4.CodeableConcept
            {
                Coding = kind.Codes.Select(c => FhirElements.Coding(FhirSystems.Loinc, c.Code, c.Display)).ToList(),
                Text = kind.Text,
            },
            Subject = FhirReferences.Patient(v.PatientId),
            Encounter = v.EncounterId is { } encounter ? FhirReferences.Encounter(FhirIds.ClinicalEncounter, encounter) : null,
            Effective = new R4.FhirDateTime(FhirTime.DateTime(v.RecordedAt)),
        };
    }

    public static R4.CodeableConcept Category(string code) => FhirElements.Concept(FhirSystems.ObservationCategory, code, code switch
    {
        VitalSigns => "Vital Signs",
        Laboratory => "Laboratory",
        Survey => "Survey",
        Exam => "Exam",
        _ => code,
    });

    private static string GlucoseText(GlucoseTiming timing) => timing switch
    {
        GlucoseTiming.Fasting => "Blood glucose (fasting)",
        GlucoseTiming.PostPrandial => "Blood glucose (post-prandial)",
        _ => "Blood glucose (random)",
    };
}
