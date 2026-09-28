using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

public sealed class DischargeSummaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);

    private static DischargeSummary NewSummary() => new() { AdmissionId = Guid.CreateVersion7(), PatientId = Guid.CreateVersion7() };

    private static DischargeSummaryContent Content(string? condition = "Stable, ambulant", IReadOnlyList<string>? codes = null) => new(
        "Acute subendocardial myocardial infarction; type 2 diabetes mellitus", codes ?? [], "Chest pain for 2 hours",
        "Admitted to CCU, managed medically; PTCA to LAD on day 2; uneventful recovery.", "PTCA with drug-eluting stent to LAD",
        "Troponin raised; ECHO EF 50%", condition, "Aspirin, clopidogrel, atorvastatin, metoprolol", "Low-salt diabetic diet; cardiac rehabilitation",
        new DateOnly(2026, 10, 3), "Cardiology OPD with reports");

    [Fact]
    public void Summary_needs_diagnoses_course_and_condition_to_be_signed()
    {
        var summary = NewSummary();
        summary.Update(Content(condition: null));

        summary.Sign(null, null, Now).Error!.Code.ShouldBe("discharge_summary.incomplete");
        summary.Status.ShouldBe(DischargeSummaryStatus.Draft);
    }

    [Fact]
    public void Diagnosis_codes_are_normalised()
    {
        var summary = NewSummary();

        summary.Update(Content(codes: [" i21.4 ", "I21.4", "e11.9", ""]));

        summary.DiagnosisCodes.ShouldBe(["I21.4", "E11.9"]);
    }

    [Fact]
    public void Signing_locks_the_summary_and_raises_an_event()
    {
        var summary = NewSummary();
        summary.Update(Content());

        summary.Sign(Guid.CreateVersion7(), Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();

        summary.Status.ShouldBe(DischargeSummaryStatus.Signed);
        summary.DomainEvents.OfType<DischargeSummarySigned>().ShouldHaveSingleItem().AdmissionId.ShouldBe(summary.AdmissionId);
        summary.Update(Content()).Error!.Code.ShouldBe("discharge_summary.signed");
        summary.Sign(null, null, Now).Error!.Code.ShouldBe("discharge_summary.already_signed");
    }

    [Fact]
    public void Amendments_need_a_reason_and_keep_the_previous_text()
    {
        var summary = NewSummary();
        summary.Amend(Content(), "Correction of medicines", null, Now).Error!.Code.ShouldBe("discharge_summary.not_signed");
        summary.Update(Content());
        summary.Sign(null, null, Now);

        summary.Amend(Content(), " ", null, Now).Error!.Type.ShouldBe(ErrorType.Validation);
        summary.Amend(Content(condition: "Stable; walking unaided"), "Condition wording clarified", null, Now.AddHours(2)).IsSuccess.ShouldBeTrue();

        summary.Status.ShouldBe(DischargeSummaryStatus.Amended);
        summary.ConditionAtDischarge.ShouldBe("Stable; walking unaided");
        var amendment = summary.Amendments.ShouldHaveSingleItem();
        JsonDocument.Parse(amendment.PreviousContent).RootElement.GetProperty("conditionAtDischarge").GetString().ShouldBe("Stable, ambulant");
    }
}
