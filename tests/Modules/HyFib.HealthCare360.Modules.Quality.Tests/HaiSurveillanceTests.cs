using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Domain.Hai;

namespace HyFib.HealthCare360.Modules.Quality.Tests;

/// <summary>Device days, device eligibility and the NHSN case-review checklists.</summary>
public sealed class HaiSurveillanceTests
{
    private static readonly Guid PatientA = Guid.NewGuid();
    private static readonly Guid PatientB = Guid.NewGuid();

    [Fact]
    public void Device_days_count_each_calendar_day_a_patient_had_the_device_once()
    {
        // Patient A: a line from day 1 to day 5, replaced by another from day 5 to day 8 (day 5 counts once): 8 days.
        // Patient B: a line from day 7, still in on day 10 (today): days 7..10 = 4. In days 1..10: 12 central-line days.
        var spans = new[]
        {
            new DeviceSpan(PatientA, 1, 5),
            new DeviceSpan(PatientA, 5, 8),
            new DeviceSpan(PatientB, 7, null),
        };

        DeviceDays.Count(spans, 1, 10, today: 10).ShouldBe(12);
        DeviceDays.Count(spans, 6, 7, today: 10).ShouldBe(3); // A on 6 and 7, B on 7
    }

    [Fact]
    public void Clabsi_rate_worked_example()
    {
        // 2 CLABSIs over 1,250 central-line days → 1.6 per 1,000 central-line days.
        IndicatorMath.Compute(2, 1250, 1000).ShouldBe(1.6m);
    }

    [Theory]
    [InlineData(1, null, 3, true)] // device day 3, still in
    [InlineData(1, null, 2, false)] // device day 2: not more than two days
    [InlineData(1, 3, 4, true)] // in for 3 days, removed the day before the event
    [InlineData(1, 3, 5, false)] // removed two days before the event
    [InlineData(1, 2, 3, false)] // in for only 2 days, removed the day before
    [InlineData(5, null, 4, false)] // the event before insertion
    public void A_device_associated_infection_needs_the_device_more_than_two_days_and_on_the_day_or_the_day_before(
        int inserted, int? removed, int eventDay, bool expected) =>
        DeviceDays.Eligible(inserted, removed, eventDay).ShouldBe(expected);

    private static HaiEvaluation Evaluate(HaiType type, bool eligible, params string[] met) =>
        HaiCriteria.Evaluate(type, new HaiEvidence(met.ToHashSet(), eligible));

    [Fact]
    public void Clabsi_by_a_recognised_pathogen()
    {
        Evaluate(HaiType.Clabsi, true, "lcbi1-pathogen", "not-secondary").Meets.ShouldBeTrue();
    }

    [Fact]
    public void Clabsi_by_a_common_commensal_needs_two_blood_specimens_and_a_symptom()
    {
        Evaluate(HaiType.Clabsi, true, "lcbi2-commensal", "not-secondary").Meets.ShouldBeFalse();
        Evaluate(HaiType.Clabsi, true, "lcbi2-commensal", "lcbi2-symptom", "not-secondary").Meets.ShouldBeTrue();
    }

    [Fact]
    public void Clabsi_is_not_met_without_the_central_line_or_when_secondary()
    {
        var noLine = Evaluate(HaiType.Clabsi, false, "lcbi1-pathogen", "not-secondary");
        noLine.Meets.ShouldBeFalse();
        noLine.Missing.ShouldContain(m => m.Contains("central line", StringComparison.Ordinal));

        Evaluate(HaiType.Clabsi, true, "lcbi1-pathogen").Meets.ShouldBeFalse();
    }

    [Fact]
    public void Cauti_needs_a_culture_and_a_sign_and_dysuria_counts_only_once_the_catheter_is_out()
    {
        Evaluate(HaiType.Cauti, true, "culture", "fever").Meets.ShouldBeTrue();
        Evaluate(HaiType.Cauti, true, "fever").Meets.ShouldBeFalse();

        HaiCriteria.Evaluate(HaiType.Cauti, new HaiEvidence(new HashSet<string> { "culture", "dysuria" }, true, CatheterInPlaceOnEventDay: true)).Meets.ShouldBeFalse();
        HaiCriteria.Evaluate(HaiType.Cauti, new HaiEvidence(new HashSet<string> { "culture", "dysuria" }, true, CatheterInPlaceOnEventDay: false)).Meets.ShouldBeTrue();
    }

    [Fact]
    public void Vap_needs_imaging_a_systemic_sign_and_two_pulmonary_signs()
    {
        Evaluate(HaiType.Vap, true, "imaging", "fever", "sputum", "gas").Meets.ShouldBeTrue();
        Evaluate(HaiType.Vap, true, "imaging", "fever", "sputum").Meets.ShouldBeFalse();
        Evaluate(HaiType.Vap, true, "fever", "wbc", "sputum", "gas").Meets.ShouldBeFalse();
        Evaluate(HaiType.Vap, true, "imaging", "sputum", "gas", "rales").Meets.ShouldBeFalse();
    }

    [Theory]
    [InlineData(12, false, true)] // superficial, day 12
    [InlineData(31, false, false)] // superficial, day 31: outside 30 days
    [InlineData(31, true, false)] // an implant does not lengthen the superficial window
    public void Superficial_ssi_is_surveyed_for_30_days(int day, bool implant, bool expected)
    {
        var met = new HashSet<string> { "superficial", "purulent" };
        if (implant)
        {
            met.Add("implant");
        }

        HaiCriteria.Evaluate(HaiType.Ssi, new HaiEvidence(met, false, DaysAfterOperation: day)).Meets.ShouldBe(expected);
    }

    [Fact]
    public void Deep_ssi_after_an_implant_is_surveyed_for_90_days()
    {
        HaiCriteria.Evaluate(HaiType.Ssi, new HaiEvidence(new HashSet<string> { "deep", "abscess", "implant" }, false, DaysAfterOperation: 75)).Meets.ShouldBeTrue();
        HaiCriteria.Evaluate(HaiType.Ssi, new HaiEvidence(new HashSet<string> { "deep", "abscess" }, false, DaysAfterOperation: 75)).Meets.ShouldBeFalse();
    }

    [Fact]
    public void Organ_space_ssi_needs_a_site_criterion_and_exactly_one_depth_is_chosen()
    {
        HaiCriteria.Evaluate(HaiType.Ssi, new HaiEvidence(new HashSet<string> { "organ-space", "organism" }, false, DaysAfterOperation: 10)).Meets.ShouldBeFalse();
        HaiCriteria.Evaluate(HaiType.Ssi, new HaiEvidence(new HashSet<string> { "organ-space", "organism", "site-criterion" }, false, DaysAfterOperation: 10)).Meets.ShouldBeTrue();
        HaiCriteria.Evaluate(HaiType.Ssi, new HaiEvidence(new HashSet<string> { "superficial", "deep", "purulent" }, false, DaysAfterOperation: 10)).Meets.ShouldBeFalse();
    }

    [Fact]
    public void A_case_is_confirmed_only_when_the_checklist_meets_the_definition_and_ruled_out_only_with_a_reason()
    {
        var c = new HaiCase { Type = HaiType.Clabsi, Status = HaiCaseStatus.Suspected };
        var user = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        c.Confirm(new HaiEvaluation(false, ["x"]), user, "ICN", null, now).Error!.Code.ShouldBe("hai.definition_not_met");
        c.RuleOut(user, "ICN", " ", now).IsFailure.ShouldBeTrue();
        c.Confirm(new HaiEvaluation(true, []), user, "ICN", null, now).IsSuccess.ShouldBeTrue();
        c.Status.ShouldBe(HaiCaseStatus.Confirmed);
        c.RuleOut(user, "ICN", "contaminant", now).Error!.Code.ShouldBe("hai.reviewed");
        c.Reopen("new culture").IsSuccess.ShouldBeTrue();
        c.Status.ShouldBe(HaiCaseStatus.Suspected);
    }

    [Fact]
    public void A_device_is_removed_once_after_its_insertion_and_not_in_the_future()
    {
        var now = new DateTimeOffset(2026, 9, 10, 6, 0, 0, TimeSpan.Zero);
        var e = new DeviceEpisode { Device = InvasiveDevice.Ventilator, InsertedAt = now.AddDays(-3) };

        e.Remove(now.AddDays(-4), DeviceRemovalReason.Planned, "Nurse", now).IsFailure.ShouldBeTrue();
        e.Remove(now.AddHours(1), DeviceRemovalReason.Planned, "Nurse", now).IsFailure.ShouldBeTrue();
        e.Remove(now, DeviceRemovalReason.Planned, "Nurse", now).IsSuccess.ShouldBeTrue();
        e.IsExtubation.ShouldBeTrue();
        e.Remove(now, DeviceRemovalReason.Planned, "Nurse", now).Error!.Code.ShouldBe("hai.device_removed");
    }

    [Theory]
    [InlineData(DeviceRemovalReason.Planned, true)]
    [InlineData(DeviceRemovalReason.Accidental, true)]
    [InlineData(DeviceRemovalReason.PatientDied, false)]
    [InlineData(DeviceRemovalReason.Replaced, false)]
    public void Only_a_planned_or_accidental_end_of_ventilation_is_an_extubation(DeviceRemovalReason reason, bool expected) =>
        new DeviceEpisode { Device = InvasiveDevice.Ventilator, RemovalReason = reason }.IsExtubation.ShouldBe(expected);
}
