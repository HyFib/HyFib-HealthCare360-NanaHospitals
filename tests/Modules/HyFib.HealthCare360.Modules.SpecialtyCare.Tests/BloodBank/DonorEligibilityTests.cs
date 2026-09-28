using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.BloodBank;

/// <summary>Donor selection rules (BB-02) per NBTC 2017 guidelines.</summary>
public sealed class DonorEligibilityTests
{
    private static readonly DonorVitals Healthy = new(13.5m, 62m, 120, 80, 76, 36.8m);

    private static IReadOnlyList<ScreeningAnswer> AllNo(Gender gender) =>
        DonorEligibility.QuestionsFor(gender).Select(q => new ScreeningAnswer(q.Key, false)).ToList();

    private static EligibilityResult Evaluate(
        DonorVitals? vitals = null, Gender gender = Gender.Male, int age = 30, bool firstDonation = false, DateOnly? lastDonation = null,
        IReadOnlyList<ScreeningAnswer>? answers = null) =>
        DonorEligibility.Evaluate(new EligibilityInput(
            gender, TestData.Today.AddYears(-age).AddDays(-10), firstDonation, lastDonation, vitals ?? Healthy, answers ?? AllNo(gender), TestData.Today));

    [Fact]
    public void Healthy_donor_is_eligible_for_a_450_ml_bag()
    {
        var result = Evaluate();

        result.Outcome.ShouldBe(DonorEligibilityOutcome.Eligible);
        result.MaxBagVolumeMl.ShouldBe(450);
        result.Reasons.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("12.5", true)]
    [InlineData("12.4", false)]
    public void Haemoglobin_must_be_at_least_12_5(string haemoglobin, bool eligible)
    {
        var result = Evaluate(Healthy with { HaemoglobinGdl = decimal.Parse(haemoglobin, System.Globalization.CultureInfo.InvariantCulture) });

        result.IsEligible.ShouldBe(eligible);
        if (!eligible)
        {
            result.Outcome.ShouldBe(DonorEligibilityOutcome.TemporarilyDeferred);
            result.DeferredUntil.ShouldBe(TestData.Today.AddDays(1));
        }
    }

    [Theory]
    [InlineData(44.9, null)]
    [InlineData(45, 350)]
    [InlineData(54.9, 350)]
    [InlineData(55, 450)]
    public void Weight_decides_eligibility_and_bag_size(double weight, int? maxBag)
    {
        var result = Evaluate(Healthy with { WeightKg = (decimal)weight });

        result.IsEligible.ShouldBe(maxBag is not null);
        result.MaxBagVolumeMl.ShouldBe(maxBag);
    }

    [Fact]
    public void Men_may_donate_again_after_90_days()
    {
        Evaluate(lastDonation: TestData.Today.AddDays(-89)).DeferredUntil.ShouldBe(TestData.Today.AddDays(1));
        Evaluate(lastDonation: TestData.Today.AddDays(-90)).IsEligible.ShouldBeTrue();
    }

    [Fact]
    public void Women_may_donate_again_after_120_days()
    {
        var early = Evaluate(gender: Gender.Female, lastDonation: TestData.Today.AddDays(-100));

        early.Outcome.ShouldBe(DonorEligibilityOutcome.TemporarilyDeferred);
        early.DeferredUntil.ShouldBe(TestData.Today.AddDays(20));
        Evaluate(gender: Gender.Female, lastDonation: TestData.Today.AddDays(-120)).IsEligible.ShouldBeTrue();
    }

    [Fact]
    public void Donors_under_18_are_deferred_until_their_18th_birthday()
    {
        var dob = TestData.Today.AddYears(-17).AddDays(-100);
        var result = DonorEligibility.Evaluate(new EligibilityInput(Gender.Male, dob, true, null, Healthy, AllNo(Gender.Male), TestData.Today));

        result.Outcome.ShouldBe(DonorEligibilityOutcome.TemporarilyDeferred);
        result.DeferredUntil.ShouldBe(dob.AddYears(18));
    }

    [Fact]
    public void First_time_donors_above_60_and_regular_donors_above_65_are_permanently_deferred()
    {
        Evaluate(age: 61, firstDonation: true).Outcome.ShouldBe(DonorEligibilityOutcome.PermanentlyDeferred);
        Evaluate(age: 61, firstDonation: false).IsEligible.ShouldBeTrue();
        Evaluate(age: 66, firstDonation: false).Outcome.ShouldBe(DonorEligibilityOutcome.PermanentlyDeferred);
    }

    [Theory]
    [InlineData(99, 80, 76, "36.8")]
    [InlineData(141, 80, 76, "36.8")]
    [InlineData(120, 59, 76, "36.8")]
    [InlineData(120, 91, 76, "36.8")]
    [InlineData(120, 80, 59, "36.8")]
    [InlineData(120, 80, 101, "36.8")]
    [InlineData(120, 80, 76, "37.6")]
    public void Vital_signs_outside_limits_defer_for_the_day(int systolic, int diastolic, int pulse, string temperature)
    {
        var result = Evaluate(Healthy with
        {
            Systolic = systolic, Diastolic = diastolic, Pulse = pulse,
            TemperatureC = decimal.Parse(temperature, System.Globalization.CultureInfo.InvariantCulture),
        });

        result.Outcome.ShouldBe(DonorEligibilityOutcome.TemporarilyDeferred);
        result.DeferredUntil.ShouldBe(TestData.Today.AddDays(1));
    }

    [Theory]
    [InlineData(100, 60, 60, "37.5")]
    [InlineData(140, 90, 100, "36.0")]
    public void Boundary_vitals_are_acceptable(int systolic, int diastolic, int pulse, string temperature) =>
        Evaluate(Healthy with
        {
            Systolic = systolic, Diastolic = diastolic, Pulse = pulse,
            TemperatureC = decimal.Parse(temperature, System.Globalization.CultureInfo.InvariantCulture),
        }).IsEligible.ShouldBeTrue();

    [Fact]
    public void Tattoo_in_the_last_year_defers_for_twelve_months()
    {
        var answers = AllNo(Gender.Male).Select(a => a.Key == "tattoo_12m" ? a with { Yes = true } : a).ToList();

        var result = Evaluate(answers: answers);

        result.Outcome.ShouldBe(DonorEligibilityOutcome.TemporarilyDeferred);
        result.DeferredUntil.ShouldBe(TestData.Today.AddDays(365));
    }

    [Fact]
    public void Hepatitis_or_hiv_history_defers_permanently_even_with_other_temporary_reasons()
    {
        var answers = AllNo(Gender.Male).Select(a => a.Key is "hepatitis_hiv" or "alcohol_24h" ? a with { Yes = true } : a).ToList();

        var result = Evaluate(answers: answers);

        result.Outcome.ShouldBe(DonorEligibilityOutcome.PermanentlyDeferred);
        result.DeferredUntil.ShouldBeNull();
        result.Reasons.Count.ShouldBe(2);
    }

    [Fact]
    public void The_longest_temporary_deferral_wins()
    {
        var answers = AllNo(Gender.Male).Select(a => a.Key is "malaria_3m" or "alcohol_24h" ? a with { Yes = true } : a).ToList();

        Evaluate(answers: answers).DeferredUntil.ShouldBe(TestData.Today.AddDays(90));
    }

    [Fact]
    public void Pregnancy_question_applies_to_women_only()
    {
        DonorEligibility.QuestionsFor(Gender.Male).ShouldNotContain(q => q.Key == "pregnancy_12m");
        DonorEligibility.QuestionsFor(Gender.Female).ShouldContain(q => q.Key == "pregnancy_12m");
        DonorEligibility.MissingAnswers(Gender.Female, AllNo(Gender.Male)).ShouldBe(["pregnancy_12m"]);
        DonorEligibility.MissingAnswers(Gender.Male, AllNo(Gender.Male)).ShouldBeEmpty();
    }

    [Fact]
    public void Reactive_hiv_hbv_or_hcv_screen_defers_the_donor_permanently()
    {
        var deferral = DonorEligibility.DeferralForReactiveTti(new TtiPanel { Hiv = TtiResult.NonReactive, HbsAg = TtiResult.Reactive });

        deferral.ShouldNotBeNull().Permanent.ShouldBeTrue();
        deferral.Value.Reason.ShouldContain("HBsAg");
    }

    [Fact]
    public void Reactive_syphilis_and_malaria_screens_defer_temporarily()
    {
        DonorEligibility.DeferralForReactiveTti(new TtiPanel { Syphilis = TtiResult.Reactive })!.Value.Days.ShouldBe(365);
        DonorEligibility.DeferralForReactiveTti(new TtiPanel { Malaria = TtiResult.Reactive })!.Value.Days.ShouldBe(90);
        DonorEligibility.DeferralForReactiveTti(new TtiPanel()).ShouldBeNull();
    }

    [Fact]
    public void Deferrals_only_get_stricter()
    {
        var donor = new Donor();
        donor.Defer(TestData.Today.AddDays(90), false, "Malaria");
        donor.Defer(TestData.Today.AddDays(1), false, "Low haemoglobin");

        donor.DeferredUntil.ShouldBe(TestData.Today.AddDays(90));
        donor.IsDeferredOn(TestData.Today.AddDays(89)).ShouldBeTrue();
        donor.IsDeferredOn(TestData.Today.AddDays(90)).ShouldBeFalse();

        donor.Defer(null, true, "Reactive HIV screen");
        donor.IsDeferredOn(TestData.Today.AddYears(5)).ShouldBeTrue();
    }

    [Fact]
    public void Donor_blood_group_discrepancy_is_refused()
    {
        var donor = new Donor();
        donor.ConfirmBloodGroup(BloodGroup.OPositive).IsSuccess.ShouldBeTrue();

        donor.ConfirmBloodGroup(BloodGroup.OPositive).IsSuccess.ShouldBeTrue();
        donor.ConfirmBloodGroup(BloodGroup.APositive).Error!.Code.ShouldBe("bloodbank.donor_group_discrepancy");
    }

    [Fact]
    public void Screening_can_be_used_once_the_same_day_for_an_allowed_volume()
    {
        var screening = new DonorScreening { Outcome = DonorEligibilityOutcome.Eligible, ScreenedAt = TestData.Now, MaxBagVolumeMl = 350 };

        screening.EnsureUsableForDonation(450, TestData.Now).Error!.Code.ShouldBe("bloodbank.volume_not_allowed");
        screening.EnsureUsableForDonation(300, TestData.Now).IsFailure.ShouldBeTrue();
        screening.EnsureUsableForDonation(350, TestData.Now.AddHours(13)).Error!.Code.ShouldBe("bloodbank.screening_stale");
        screening.EnsureUsableForDonation(350, TestData.Now.AddHours(1)).IsSuccess.ShouldBeTrue();

        screening.DonationId = Guid.CreateVersion7();
        screening.EnsureUsableForDonation(350, TestData.Now).Error!.Code.ShouldBe("bloodbank.screening_used");
    }

    [Fact]
    public void Deferred_screening_cannot_be_used_for_a_donation() =>
        new DonorScreening { Outcome = DonorEligibilityOutcome.TemporarilyDeferred, ScreenedAt = TestData.Now }
            .EnsureUsableForDonation(350, TestData.Now).Error!.Code.ShouldBe("bloodbank.donor_not_eligible");
}
