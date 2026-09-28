using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

public sealed class ConditionMapperTests
{
    private static readonly Guid DiagnosisId = Guid.Parse("01923a4b-0000-7000-8000-00000000d001");
    private static readonly Guid EncounterId = Guid.Parse("01923a4b-0000-7000-8000-00000000d002");

    private static DiagnosisRow Diabetes() => new()
    {
        Id = DiagnosisId,
        PatientId = TestData.PatientId,
        EncounterId = EncounterId,
        IcdCode = "e11.9",
        Title = "Type 2 diabetes mellitus without complications",
        Type = DiagnosisType.Final,
        Status = DiagnosisStatus.Chronic,
        OnsetDate = new DateOnly(2019, 6, 1),
        RecordedAt = TestData.Now,
        Notes = "On metformin",
        CreatedAt = TestData.Now,
    };

    [Fact]
    public void Codes_the_diagnosis_with_icd10()
    {
        var condition = ConditionMapper.Map(Diabetes());

        condition.Id.ShouldBe(DiagnosisId.ToString("D"));
        var coding = condition.Code.Coding.ShouldHaveSingleItem();
        coding.System.ShouldBe("http://hl7.org/fhir/sid/icd-10");
        coding.Code.ShouldBe("E11.9");
        coding.Display.ShouldBe("Type 2 diabetes mellitus without complications");
        condition.Subject.Reference.ShouldBe($"Patient/{TestData.PatientId:D}");
        condition.Encounter.Reference.ShouldBe($"Encounter/enc-{EncounterId:D}");
        condition.Onset.ShouldBeOfType<R4.FhirDateTime>().Value.ShouldBe("2019-06-01");
        condition.RecordedDate.ShouldBe("2026-09-25T10:30:00+05:30");
        condition.Note.ShouldHaveSingleItem().Text.ShouldBe("On metformin");
    }

    [Fact]
    public void A_free_text_diagnosis_has_text_and_no_coding()
    {
        var code = ConditionMapper.Map(Diabetes() with { IcdCode = null, Title = "Viral fever" }).Code;

        code.Coding.ShouldBeEmpty();
        code.Text.ShouldBe("Viral fever");
    }

    [Theory]
    [InlineData(DiagnosisStatus.Active, "active", null)]
    [InlineData(DiagnosisStatus.Chronic, "active", "Chronic")]
    [InlineData(DiagnosisStatus.Resolved, "resolved", null)]
    public void Maps_the_clinical_status(DiagnosisStatus status, string code, string? text)
    {
        var clinical = ConditionMapper.ClinicalStatus(status);

        clinical.Coding.ShouldHaveSingleItem().Code.ShouldBe(code);
        clinical.Coding[0].System.ShouldBe("http://terminology.hl7.org/CodeSystem/condition-clinical");
        clinical.Text.ShouldBe(text);
    }

    [Theory]
    [InlineData(DiagnosisType.Provisional, "provisional")]
    [InlineData(DiagnosisType.Final, "confirmed")]
    [InlineData(DiagnosisType.Differential, "differential")]
    public void Maps_the_verification_status(DiagnosisType type, string code) =>
        ConditionMapper.VerificationStatus(type).Coding.ShouldHaveSingleItem().Code.ShouldBe(code);

    [Theory]
    [InlineData(DiagnosisType.Final, DiagnosisStatus.Chronic, true, "problem-list-item,encounter-diagnosis")]
    [InlineData(DiagnosisType.Final, DiagnosisStatus.Resolved, true, "encounter-diagnosis")]
    [InlineData(DiagnosisType.Differential, DiagnosisStatus.Active, true, "encounter-diagnosis")]
    [InlineData(DiagnosisType.Provisional, DiagnosisStatus.Active, false, "problem-list-item")]
    [InlineData(DiagnosisType.Differential, DiagnosisStatus.Active, false, "encounter-diagnosis")]
    public void Puts_active_problems_on_the_problem_list(DiagnosisType type, DiagnosisStatus status, bool inEncounter, string categories) =>
        string.Join(',', ConditionMapper.Categories(type, status, inEncounter).Select(c => c.Coding[0].Code)).ShouldBe(categories);

    [Fact]
    public void A_resolved_condition_states_when_it_resolved()
    {
        var condition = ConditionMapper.Map(Diabetes() with { Status = DiagnosisStatus.Resolved, ResolvedOn = new DateOnly(2026, 9, 1) });

        condition.Abatement.ShouldBeOfType<R4.FhirDateTime>().Value.ShouldBe("2026-09-01");
        ConditionMapper.Map(Diabetes()).Abatement.ShouldBeNull();
    }
}

public sealed class AllergyIntoleranceMapperTests
{
    private static AllergyRow Penicillin() => new()
    {
        Id = Guid.Parse("01923a4b-0000-7000-8000-00000000e001"),
        PatientId = TestData.PatientId,
        Allergen = "Penicillin",
        Type = AllergyType.Drug,
        Reaction = "Urticaria",
        Severity = AllergySeverity.Moderate,
        Status = AllergyStatus.Active,
        OnsetDate = new DateOnly(2010, 1, 1),
        RecordedAt = TestData.Now,
        CreatedAt = TestData.Now,
    };

    [Fact]
    public void Maps_a_drug_allergy()
    {
        var allergy = AllergyIntoleranceMapper.Map(Penicillin());

        allergy.Code.Text.ShouldBe("Penicillin");
        allergy.Category.ShouldBe([R4.AllergyIntolerance.AllergyIntoleranceCategory.Medication]);
        allergy.Criticality.ShouldBe(R4.AllergyIntolerance.AllergyIntoleranceCriticality.Low);
        allergy.ClinicalStatus.Coding.ShouldHaveSingleItem().Code.ShouldBe("active");
        allergy.VerificationStatus.ShouldBeNull();
        allergy.Patient.Reference.ShouldBe($"Patient/{TestData.PatientId:D}");
        allergy.RecordedDate.ShouldBe("2026-09-25T10:30:00+05:30");
        var reaction = allergy.Reaction.ShouldHaveSingleItem();
        reaction.Manifestation.ShouldHaveSingleItem().Text.ShouldBe("Urticaria");
        reaction.Severity.ShouldBe(R4.AllergyIntolerance.AllergyIntoleranceSeverity.Moderate);
    }

    [Fact]
    public void Anaphylaxis_is_high_criticality_with_a_snomed_manifestation()
    {
        var allergy = AllergyIntoleranceMapper.Map(Penicillin() with { Severity = AllergySeverity.Anaphylaxis, Reaction = null });

        allergy.Criticality.ShouldBe(R4.AllergyIntolerance.AllergyIntoleranceCriticality.High);
        var reaction = allergy.Reaction.ShouldHaveSingleItem();
        reaction.Severity.ShouldBe(R4.AllergyIntolerance.AllergyIntoleranceSeverity.Severe);
        var coding = reaction.Manifestation.ShouldHaveSingleItem().Coding.ShouldHaveSingleItem();
        coding.System.ShouldBe("http://snomed.info/sct");
        coding.Code.ShouldBe("39579001");
    }

    [Fact]
    public void Without_a_reaction_there_is_no_reaction_element() =>
        AllergyIntoleranceMapper.Map(Penicillin() with { Reaction = null }).Reaction.ShouldBeEmpty();

    [Fact]
    public void An_allergy_entered_in_error_has_no_clinical_status()
    {
        var allergy = AllergyIntoleranceMapper.Map(Penicillin() with { Status = AllergyStatus.EnteredInError, StatusReason = "Wrong patient" });

        allergy.ClinicalStatus.ShouldBeNull();
        allergy.VerificationStatus.Coding.ShouldHaveSingleItem().Code.ShouldBe("entered-in-error");
        allergy.Note.ShouldHaveSingleItem().Text.ShouldBe("Status reason: Wrong patient");
    }

    [Theory]
    [InlineData(AllergyStatus.Active, "active")]
    [InlineData(AllergyStatus.Inactive, "inactive")]
    [InlineData(AllergyStatus.Resolved, "resolved")]
    public void Maps_the_clinical_status(AllergyStatus status, string code) =>
        AllergyIntoleranceMapper.ClinicalStatus(status).Coding.ShouldHaveSingleItem().Code.ShouldBe(code);

    [Theory]
    [InlineData(AllergyType.Drug, R4.AllergyIntolerance.AllergyIntoleranceCategory.Medication)]
    [InlineData(AllergyType.Food, R4.AllergyIntolerance.AllergyIntoleranceCategory.Food)]
    [InlineData(AllergyType.Environmental, R4.AllergyIntolerance.AllergyIntoleranceCategory.Environment)]
    [InlineData(AllergyType.Other, null)]
    public void Maps_the_category(AllergyType type, R4.AllergyIntolerance.AllergyIntoleranceCategory? category) =>
        AllergyIntoleranceMapper.Category(type).ShouldBe(category);

    [Fact]
    public void An_uncategorised_allergy_has_no_category() =>
        AllergyIntoleranceMapper.Map(Penicillin() with { Type = AllergyType.Other }).Category.ShouldBeEmpty();
}
