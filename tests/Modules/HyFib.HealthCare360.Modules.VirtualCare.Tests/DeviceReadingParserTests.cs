using System.Text.Json;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;

namespace HyFib.HealthCare360.Modules.VirtualCare.Tests;

public sealed class DeviceReadingParserTests
{
    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void The_simple_format_is_read_with_units_and_times()
    {
        var payload = DeviceReadingParser.Parse(Json("""
            { "readings": [ { "type": "SystolicBp", "value": 128, "unit": "mmHg", "measuredAt": "2026-09-28T07:30:00+05:30" } ] }
            """));

        payload.Source.ShouldBe(ReadingSource.Device);
        var reading = payload.Readings.ShouldHaveSingleItem();
        reading.Type.ShouldBe(RpmReadingType.SystolicBp);
        reading.Value.ShouldBe(128m);
        reading.Unit.ShouldBe("mmHg");
        reading.MeasuredAt.ShouldBe(new DateTimeOffset(2026, 9, 28, 2, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("pulse", RpmReadingType.HeartRate)]
    [InlineData("heart_rate", RpmReadingType.HeartRate)]
    [InlineData("SpO2", RpmReadingType.SpO2)]
    [InlineData("blood-sugar", RpmReadingType.Glucose)]
    [InlineData("Temp", RpmReadingType.Temperature)]
    [InlineData("sbp", RpmReadingType.SystolicBp)]
    public void Reading_types_are_matched_leniently(string name, RpmReadingType expected) =>
        DeviceReadingParser.TypeOf(name).ShouldBe(expected);

    [Fact]
    public void A_blood_pressure_item_expands_into_three_readings()
    {
        var payload = DeviceReadingParser.Parse(Json("""
            [ { "type": "BloodPressure", "systolic": 142, "diastolic": 91, "pulse": 78, "unit": "mmHg" } ]
            """));

        payload.Readings.Select(r => r.Type).ShouldBe([RpmReadingType.SystolicBp, RpmReadingType.DiastolicBp, RpmReadingType.HeartRate]);
        payload.Readings[2].Unit.ShouldBeNull();
    }

    [Fact]
    public void An_ecg_item_may_carry_only_the_interpretation()
    {
        var reading = DeviceReadingParser.Parse(Json("""
            { "readings": [ { "type": "ecg", "text": "Normal sinus rhythm" } ] }
            """)).Readings.ShouldHaveSingleItem();

        reading.Value.ShouldBe(0m);
        reading.TextValue.ShouldBe("Normal sinus rhythm");
    }

    [Fact]
    public void Problems_are_reported_per_line_and_good_lines_are_kept()
    {
        var payload = DeviceReadingParser.Parse(Json("""
            { "readings": [
                { "type": "weight", "value": "72.4", "unit": "kg" },
                { "type": "cholesterol", "value": 180 },
                { "type": "glucose" },
                "not an object"
            ] }
            """));

        payload.Readings.ShouldHaveSingleItem().Value.ShouldBe(72.4m);
        payload.Errors.Count.ShouldBe(3);
        payload.Errors[0].ShouldStartWith("#2");
    }

    [Fact]
    public void Fhir_payloads_are_delegated_to_the_fhir_parser()
    {
        var payload = DeviceReadingParser.Parse(Json("""
            { "resourceType": "Observation", "code": { "coding": [ { "code": "8310-5" } ] }, "valueQuantity": { "value": 99.1, "unit": "[degF]" } }
            """));

        payload.Source.ShouldBe(ReadingSource.Fhir);
        payload.Readings.ShouldHaveSingleItem().Type.ShouldBe(RpmReadingType.Temperature);
    }

    [Fact]
    public void Anything_else_is_explained()
    {
        var payload = DeviceReadingParser.Parse(Json("""{ "value": 5 }"""));

        payload.Readings.ShouldBeEmpty();
        payload.Errors.ShouldHaveSingleItem();
    }
}
