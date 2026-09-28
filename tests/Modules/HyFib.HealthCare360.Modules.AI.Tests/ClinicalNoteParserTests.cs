using System.Text.Json;
using HyFib.HealthCare360.Modules.AI.Features.Clinical;
using HyFib.HealthCare360.Modules.AI.Services.Clinical;

namespace HyFib.HealthCare360.Modules.AI.Tests;

public sealed class ClinicalNoteParserTests
{
    private const string Draft = """
        {
          "subjective": "  Fever x 3 days, dry cough.\r\nNo breathlessness.  ",
          "objective": "T 101.2 F, PR 96/min, BP 124/78 mmHg. Chest clear.",
          "assessment": "Acute viral fever. R/o dengue.",
          "plan": "Tab paracetamol 650 mg PO TDS x 3 days\nCBC, NS1 antigen\nReview after reports",
          "suggestedIcd": [
            { "code": "b34.9", "title": "Viral infection, unspecified", "confidence": 0.824 },
            { "code": "A90", "title": "Dengue fever [classical dengue]", "confidence": 1.7 },
            { "code": "1D2Z", "title": "ICD-11 code, not ICD-10", "confidence": 0.5 },
            { "code": "B34.9", "title": "Duplicate", "confidence": 0.3 },
            { "code": "R50.9", "title": "", "confidence": 0.4 },
            { "code": "R05", "title": "Cough", "confidence": -0.2 }
          ],
          "warnings": ["Paracetamol frequency was unclear.", "  ", "Paracetamol frequency was unclear."]
        }
        """;

    [Fact]
    public void Reads_the_soap_sections_with_normalised_line_endings()
    {
        var note = ClinicalNoteParser.TryParse(Draft).ShouldNotBeNull();

        note.Subjective.ShouldBe("Fever x 3 days, dry cough.\nNo breathlessness.");
        note.Objective.ShouldStartWith("T 101.2 F");
        note.Assessment.ShouldBe("Acute viral fever. R/o dengue.");
        note.Plan.Split('\n').Length.ShouldBe(3);
    }

    [Fact]
    public void Keeps_only_well_formed_distinct_icd10_suggestions_with_clamped_confidence()
    {
        var note = ClinicalNoteParser.TryParse(Draft).ShouldNotBeNull();

        note.SuggestedIcd.ShouldBe(
        [
            new SuggestedIcdCode("B34.9", "Viral infection, unspecified", 0.82),
            new SuggestedIcdCode("A90", "Dengue fever [classical dengue]", 1),
            new SuggestedIcdCode("R05", "Cough", 0),
        ]);
    }

    [Fact]
    public void Keeps_distinct_non_empty_warnings() =>
        ClinicalNoteParser.TryParse(Draft).ShouldNotBeNull().Warnings.ShouldBe(["Paracetamol frequency was unclear."]);

    [Fact]
    public void Bounds_what_the_model_can_put_in_front_of_the_clinician()
    {
        var suggestions = Enumerable.Range(0, 8).Select(i => new { code = $"J0{i}", title = $"Code {i}", confidence = 0.5 });
        var json = JsonSerializer.Serialize(new
        {
            subjective = new string('s', 5000),
            objective = "",
            assessment = "",
            plan = "",
            suggestedIcd = suggestions,
            warnings = Enumerable.Range(0, 15).Select(i => $"Warning {i} " + new string('w', 400)),
        });

        var note = ClinicalNoteParser.TryParse(json).ShouldNotBeNull();

        note.Subjective.Length.ShouldBe(ClinicalNoteParser.MaxSectionLength);
        note.SuggestedIcd.Count.ShouldBe(ClinicalNoteParser.MaxSuggestions);
        note.Warnings.Count.ShouldBe(ClinicalNoteParser.MaxWarnings);
        note.Warnings.ShouldAllBe(w => w.Length <= 300);
    }

    [Fact]
    public void Missing_sections_are_empty()
    {
        var note = ClinicalNoteParser.TryParse("""{"assessment":"Hypertension"}""").ShouldNotBeNull();

        note.Subjective.ShouldBeEmpty();
        note.SuggestedIcd.ShouldBeEmpty();
        note.Warnings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Here is your SOAP note: ...")]
    [InlineData("""["subjective"]""")]
    public void Rejects_answers_that_are_not_a_json_object(string json) => ClinicalNoteParser.TryParse(json).ShouldBeNull();
}
