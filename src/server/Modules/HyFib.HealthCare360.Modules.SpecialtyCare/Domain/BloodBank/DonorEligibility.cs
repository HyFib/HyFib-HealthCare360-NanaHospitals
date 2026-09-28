namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

/// <summary>A screening question; answering "yes" defers the donor for <see cref="DeferralDays"/> days (null = permanently).</summary>
public sealed record DonorQuestion(string Key, string Text, int? DeferralDays, Gender? OnlyFor = null);

/// <summary>A donor's answer: <c>true</c> means "yes" (the risk applies).</summary>
public sealed record ScreeningAnswer(string Key, bool Yes);

public sealed record DonorVitals(decimal HaemoglobinGdl, decimal WeightKg, int Systolic, int Diastolic, int Pulse, decimal TemperatureC);

public sealed record EligibilityInput(
    Gender Gender,
    DateOnly DateOfBirth,
    bool IsFirstDonation,
    DateOnly? LastDonationDate,
    DonorVitals Vitals,
    IReadOnlyList<ScreeningAnswer> Answers,
    DateOnly ScreeningDate);

public sealed record EligibilityResult(DonorEligibilityOutcome Outcome, IReadOnlyList<string> Reasons, DateOnly? DeferredUntil, int? MaxBagVolumeMl)
{
    public bool IsEligible => Outcome == DonorEligibilityOutcome.Eligible;
}

/// <summary>
/// Donor selection criteria from the NBTC "Guidelines for Blood Donor Selection &amp; Blood Donor Referral" (2017)
/// and the Drugs &amp; Cosmetics Rules (BB-02). Pure rules: the screening endpoint records the inputs and the outcome.
/// </summary>
public static class DonorEligibility
{
    public const decimal MinHaemoglobinGdl = 12.5m;
    public const decimal MinWeightKg = 45m;

    /// <summary>A 450 ml collection needs at least 55 kg; 45–55 kg donors give 350 ml.</summary>
    public const decimal MinWeightFor450Kg = 55m;

    public const int MinAge = 18;
    public const int MaxAge = 65;
    public const int MaxAgeFirstDonation = 60;
    public const int MinPulse = 60;
    public const int MaxPulse = 100;
    public const int MinSystolic = 100;
    public const int MaxSystolic = 140;
    public const int MinDiastolic = 60;
    public const int MaxDiastolic = 90;
    public const decimal MaxTemperatureC = 37.5m;

    /// <summary>Minimum interval between whole-blood donations: 90 days for men, 120 days for women.</summary>
    public const int WholeBloodIntervalDaysMale = 90;

    public const int WholeBloodIntervalDaysFemale = 120;

    /// <summary>Default deferral periods; the list is data so the blood bank medical officer can review it in one place.</summary>
    public static IReadOnlyList<DonorQuestion> Questions { get; } =
    [
        new("unwell_today", "Feeling unwell today (fever, cold, cough, diarrhoea)?", 7),
        new("alcohol_24h", "Consumed alcohol in the last 24 hours?", 1),
        new("medication_7d", "Taken antibiotics or other medicines in the last 7 days?", 7),
        new("vaccination_4w", "Received a vaccination in the last 4 weeks?", 28),
        new("malaria_3m", "Had malaria or treatment for malaria in the last 3 months?", 90),
        new("minor_surgery_6m", "Had minor surgery or a dental extraction in the last 6 months?", 182),
        new("tattoo_12m", "Had a tattoo, body piercing or acupuncture in the last 12 months?", 365),
        new("major_surgery_12m", "Had major surgery in the last 12 months?", 365),
        new("transfusion_12m", "Received blood or blood products in the last 12 months?", 365),
        new("pregnancy_12m", "Pregnant, breast-feeding, delivered or had an abortion in the last 12 months?", 365, Gender.Female),
        new("hepatitis_hiv", "Ever tested positive for hepatitis B, hepatitis C or HIV?", null),
        new("high_risk_behaviour", "Ever had high-risk behaviour (multiple partners, sex work, injecting drugs)?", null),
        new("chronic_disease", "Cancer, heart disease, severe lung disease, epilepsy or a bleeding disorder?", null),
    ];

    public static IReadOnlyList<DonorQuestion> QuestionsFor(Gender gender) =>
        Questions.Where(q => q.OnlyFor is null || q.OnlyFor == gender).ToList();

    /// <summary>Questions that still need an answer (the screening is refused rather than guessed).</summary>
    public static IReadOnlyList<string> MissingAnswers(Gender gender, IReadOnlyCollection<ScreeningAnswer> answers) =>
        QuestionsFor(gender).Where(q => answers.All(a => a.Key != q.Key)).Select(q => q.Key).ToList();

    public static int IntervalDays(Gender gender) => gender == Gender.Female ? WholeBloodIntervalDaysFemale : WholeBloodIntervalDaysMale;

    public static EligibilityResult Evaluate(EligibilityInput input)
    {
        var reasons = new List<string>();
        var permanent = false;
        var today = input.ScreeningDate;

        // Measurement failures defer only for today: the donor may come back once the value is normal.
        var tomorrow = today.AddDays(1);
        DateOnly? until = null;
        void DeferUntil(DateOnly date, string reason)
        {
            reasons.Add(reason);
            until = until is null || date > until ? date : until;
        }

        var age = Age.InYears(input.DateOfBirth, today);
        var maxAge = input.IsFirstDonation ? MaxAgeFirstDonation : MaxAge;
        if (age < MinAge)
        {
            DeferUntil(input.DateOfBirth.AddYears(MinAge), $"Donors must be at least {MinAge} years old.");
        }
        else if (age > maxAge)
        {
            permanent = true;
            reasons.Add(input.IsFirstDonation
                ? $"First-time donors must be {MaxAgeFirstDonation} years or younger."
                : $"Donors must be {MaxAge} years or younger.");
        }

        if (input.LastDonationDate is { } last)
        {
            var next = last.AddDays(IntervalDays(input.Gender));
            if (today < next)
            {
                DeferUntil(next, $"Minimum interval between whole-blood donations is {IntervalDays(input.Gender)} days.");
            }
        }

        var v = input.Vitals;
        if (v.HaemoglobinGdl < MinHaemoglobinGdl)
        {
            DeferUntil(tomorrow, $"Haemoglobin {v.HaemoglobinGdl} g/dL is below {MinHaemoglobinGdl} g/dL.");
        }

        if (v.WeightKg < MinWeightKg)
        {
            DeferUntil(tomorrow, $"Weight {v.WeightKg} kg is below {MinWeightKg} kg.");
        }

        if (v.Pulse is < MinPulse or > MaxPulse)
        {
            DeferUntil(tomorrow, $"Pulse {v.Pulse}/min is outside {MinPulse}–{MaxPulse}/min.");
        }

        if (v.Systolic is < MinSystolic or > MaxSystolic || v.Diastolic is < MinDiastolic or > MaxDiastolic)
        {
            DeferUntil(tomorrow, $"Blood pressure {v.Systolic}/{v.Diastolic} mmHg is outside {MinSystolic}–{MaxSystolic}/{MinDiastolic}–{MaxDiastolic} mmHg.");
        }

        if (v.TemperatureC > MaxTemperatureC)
        {
            DeferUntil(tomorrow, $"Temperature {v.TemperatureC} °C is above {MaxTemperatureC} °C.");
        }

        foreach (var question in QuestionsFor(input.Gender))
        {
            if (!input.Answers.Any(a => a.Key == question.Key && a.Yes))
            {
                continue;
            }

            if (question.DeferralDays is { } days)
            {
                DeferUntil(today.AddDays(days), question.Text);
            }
            else
            {
                permanent = true;
                reasons.Add(question.Text);
            }
        }

        if (permanent)
        {
            return new EligibilityResult(DonorEligibilityOutcome.PermanentlyDeferred, reasons, null, null);
        }

        return reasons.Count > 0
            ? new EligibilityResult(DonorEligibilityOutcome.TemporarilyDeferred, reasons, until, null)
            : new EligibilityResult(DonorEligibilityOutcome.Eligible, reasons, null, v.WeightKg >= MinWeightFor450Kg ? 450 : 350);
    }

    /// <summary>
    /// Deferral after a reactive TTI screen: HIV/HBV/HCV reactive donors are deferred permanently and referred for
    /// confirmatory testing and counselling; syphilis defers for 12 months and malaria for 3 months.
    /// </summary>
    public static (bool Permanent, int? Days, string Reason)? DeferralForReactiveTti(TtiPanel tti)
    {
        if (tti.Hiv == TtiResult.Reactive || tti.HbsAg == TtiResult.Reactive || tti.Hcv == TtiResult.Reactive)
        {
            return (true, null, $"Reactive screening test ({string.Join(", ", tti.ReactiveMarkers)}): refer for confirmatory testing and counselling.");
        }

        if (tti.Syphilis == TtiResult.Reactive)
        {
            return (false, 365, "Reactive syphilis screening test: refer for treatment.");
        }

        return tti.Malaria == TtiResult.Reactive ? (false, 90, "Reactive malaria screening test: refer for treatment.") : null;
    }
}
