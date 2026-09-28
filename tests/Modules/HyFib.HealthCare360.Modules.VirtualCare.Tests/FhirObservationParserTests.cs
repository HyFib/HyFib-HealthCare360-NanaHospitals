using System.Text.Json;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;

namespace HyFib.HealthCare360.Modules.VirtualCare.Tests;

public sealed class FhirObservationParserTests
{
    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void A_heart_rate_observation_becomes_one_reading()
    {
        var result = FhirObservationParser.Parse(Json("""
            {
              "resourceType": "Observation",
              "status": "final",
              "code": { "coding": [ { "system": "http://loinc.org", "code": "8867-4", "display": "Heart rate" } ] },
              "effectiveDateTime": "2026-09-28T08:15:00+05:30",
              "valueQuantity": { "value": 72, "unit": "/min", "system": "http://unitsofmeasure.org", "code": "/min" }
            }
            """));

        var reading = result.Readings.ShouldHaveSingleItem();
        reading.Type.ShouldBe(RpmReadingType.HeartRate);
        reading.Value.ShouldBe(72m);
        reading.Unit.ShouldBe("/min");
        reading.MeasuredAt.ShouldBe(new DateTimeOffset(2026, 9, 28, 2, 45, 0, TimeSpan.Zero));
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void A_blood_pressure_panel_yields_systolic_and_diastolic()
    {
        var result = FhirObservationParser.Parse(Json("""
            {
              "resourceType": "Observation",
              "code": { "coding": [ { "system": "http://loinc.org", "code": "85354-9" } ] },
              "effectiveDateTime": "2026-09-28T07:30:00Z",
              "component": [
                { "code": { "coding": [ { "code": "8480-6" } ] }, "valueQuantity": { "value": 150, "unit": "mmHg" } },
                { "code": { "coding": [ { "code": "8462-4" } ] }, "valueQuantity": { "value": 95, "unit": "mmHg" } }
              ]
            }
            """));

        result.Readings.Select(r => (r.Type, r.Value)).ShouldBe([(RpmReadingType.SystolicBp, 150m), (RpmReadingType.DiastolicBp, 95m)]);
    }

    [Fact]
    public void A_bundle_of_observations_is_read_entry_by_entry()
    {
        var result = FhirObservationParser.Parse(Json("""
            {
              "resourceType": "Bundle",
              "type": "collection",
              "entry": [
                { "resource": { "resourceType": "Observation", "code": { "coding": [ { "code": "59408-5" } ] }, "valueQuantity": { "value": 95, "unit": "%" } } },
                { "resource": { "resourceType": "Observation", "code": { "coding": [ { "code": "2339-0" } ] }, "valueQuantity": { "value": 6.1, "unit": "mmol/L" } } },
                { "resource": { "resourceType": "Patient" } }
              ]
            }
            """));

        result.Readings.Count.ShouldBe(2);
        result.Readings[1].Type.ShouldBe(RpmReadingType.Glucose);
        result.Readings[1].Unit.ShouldBe("mmol/L");
        result.Errors.ShouldHaveSingleItem().ShouldContain("entry[2]");
    }

    [Fact]
    public void An_ecg_interpretation_is_classified()
    {
        var result = FhirObservationParser.Parse(Json("""
            {
              "resourceType": "Observation",
              "code": { "coding": [ { "code": "8601-7" } ] },
              "valueCodeableConcept": { "text": "Atrial fibrillation" }
            }
            """));

        var reading = result.Readings.ShouldHaveSingleItem();
        reading.Type.ShouldBe(RpmReadingType.EcgRhythm);
        reading.Value.ShouldBe(2m);
        reading.TextValue.ShouldBe("Atrial fibrillation");
    }

    [Fact]
    public void Unsupported_codes_are_reported()
    {
        var result = FhirObservationParser.Parse(Json("""
            { "resourceType": "Observation", "code": { "coding": [ { "code": "1234-5" } ] }, "valueQuantity": { "value": 1 } }
            """));

        result.Readings.ShouldBeEmpty();
        result.Errors.ShouldHaveSingleItem().ShouldContain("1234-5");
    }

    [Fact]
    public void Observations_entered_in_error_are_skipped_silently()
    {
        var result = FhirObservationParser.Parse(Json("""
            { "resourceType": "Observation", "status": "entered-in-error", "code": { "coding": [ { "code": "8867-4" } ] }, "valueQuantity": { "value": 72 } }
            """));

        result.Readings.ShouldBeEmpty();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Other_resources_are_rejected()
    {
        FhirObservationParser.LooksLikeFhir(Json("""{ "readings": [] }""")).ShouldBeFalse();
        FhirObservationParser.Parse(Json("""{ "resourceType": "Patient" }""")).Errors.ShouldHaveSingleItem();
    }
}
