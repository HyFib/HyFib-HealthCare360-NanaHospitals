using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Dietary;
using HyFib.HealthCare360.Modules.SupportServices.Features.Dietary;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The kitchen production list (DIET-03) through the API: for a day, how many meals of each diet to cook for each round,
/// split by food preference, with the beds whose meal carries a restriction or an allergy. It follows the diet orders: a
/// nil-by-mouth patient gets nothing, a changed diet moves the patient's meals to the new diet, and a stopped order drops
/// out. Other patients of the hospital are on the list too, so it is read before and after, and the difference checked.
/// </summary>
public sealed class DietProductionTests(HospitalServer server)
{
    private const string Orders = "/api/v1/dietary/orders";
    private const string Generate = "/api/v1/dietary/meal-plans/generate";

    private static readonly MealSlot[] StandardRounds = [MealSlot.EarlyMorning, MealSlot.Breakfast, MealSlot.Lunch, MealSlot.Evening, MealSlot.Dinner];
    private static readonly MealSlot[] AllRounds = [.. StandardRounds, MealSlot.MidMorning, MealSlot.Bedtime];

    [Fact]
    public async Task Each_round_counts_the_meals_of_each_diet_by_preference_and_follows_the_diet_orders()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var tomorrow = (await HospitalDayAsync()).AddDays(1);
        var types = await doctor.GetAsync<List<DietTypeDto>>("/api/v1/dietary/diet-types");
        DietTypeDto Type(string code) => types.Single(t => t.Code == code);

        // Tomorrow's list for the patients already in the wards, before ours have diets.
        var before = await ProductionAsync(nurse, tomorrow);

        // Four patients: a normal vegetarian diet, a normal non-vegetarian one with less salt and a peanut allergy, a Jain
        // diabetic diet, and one kept nil by mouth before surgery.
        var allergy = $"Peanuts ({Letters(4)})";
        var (vegetarian, _) = await AdmitAsync();
        var (lowSalt, lowSaltBed) = await AdmitAsync();
        var (diabetic, _) = await AdmitAsync();
        var (fasting, _) = await AdmitAsync();
        await doctor.PostAsync<DietOrderDto>(Orders, Order(vegetarian, Type("NORMAL"), FoodPreference.Vegetarian));
        await doctor.PostAsync<DietOrderDto>(Orders, Order(lowSalt, Type("NORMAL"), FoodPreference.NonVegetarian, ["Low salt"], allergy));
        var diabeticOrder = await doctor.PostAsync<DietOrderDto>(Orders, Order(diabetic, Type("DIABETIC"), FoodPreference.Jain));
        await doctor.PostAsync<DietOrderDto>(Orders, Order(fasting, Type("NPO"), FoodPreference.Vegetarian));

        var planned = await ProductionAsync(nurse, tomorrow);
        var added = Difference(before, planned);
        foreach (var round in StandardRounds)
        {
            added[(round, "NORMAL")].ShouldBe(new Counts(Vegetarian: 1, NonVegetarian: 1, Eggetarian: 0, Jain: 0, Total: 2), $"normal diets at {round}");
        }

        foreach (var round in AllRounds)
        {
            added[(round, "DIABETIC")].ShouldBe(new Counts(0, 0, 0, Jain: 1, Total: 1), $"the diabetic diet at {round}, every round");
        }

        added.Keys.Select(k => k.Diet).Distinct().ShouldBe(["NORMAL", "DIABETIC"], ignoreOrder: true, "Nothing is cooked for a patient nil by mouth.");
        (planned.TotalMeals - before.TotalMeals).ShouldBe(2 * StandardRounds.Length + AllRounds.Length);
        foreach (var round in StandardRounds)
        {
            Line(planned, round, "NORMAL").SpecialNotes.ShouldContain($"{lowSaltBed}: Low salt (allergy: {allergy})");
        }

        // The vegetarian patient moves to a soft diet and the diabetic patient's order is stopped: the list follows.
        await doctor.PostAsync<DietOrderDto>(Orders, Order(vegetarian, Type("SOFT"), FoodPreference.Vegetarian));
        await doctor.PostAsync<DietOrderDto>($"{Orders}/{diabeticOrder.Id}/stop", new StopDietOrderRequest("Discharge planned for tonight"));
        var changed = Difference(before, await ProductionAsync(nurse, tomorrow));
        foreach (var round in StandardRounds)
        {
            changed[(round, "NORMAL")].ShouldBe(new Counts(0, NonVegetarian: 1, 0, 0, Total: 1), $"only the low-salt patient's normal diet at {round}");
            changed[(round, "SOFT")].ShouldBe(new Counts(Vegetarian: 1, 0, 0, 0, Total: 1), $"the soft diet at {round}");
        }

        changed.Keys.ShouldNotContain(k => k.Diet == "DIABETIC", "A stopped order is not cooked for.");
    }

    private sealed record Counts(int Vegetarian, int NonVegetarian, int Eggetarian, int Jain, int Total);

    /// <summary>The day's plan brought up to date with the diet orders, then the kitchen's list for it.</summary>
    private static async Task<ProductionSummary> ProductionAsync(ApiClient nurse, DateOnly date)
    {
        await nurse.PostAsync<MealPlanResult>(Generate, new GenerateMealPlanRequest(date));
        return await nurse.GetAsync<ProductionSummary>($"/api/v1/dietary/meal-plans/production?date={date:yyyy-MM-dd}");
    }

    /// <summary>What <paramref name="after"/> has more of than <paramref name="before"/>, per round and diet; rounds and diets without a change left out.</summary>
    private static Dictionary<(MealSlot Round, string Diet), Counts> Difference(ProductionSummary before, ProductionSummary after)
    {
        Counts Of(ProductionSummary summary, MealSlot round, string diet) =>
            summary.Lines.SingleOrDefault(l => l.Slot == round && l.DietCode == diet) is { } line
                ? new Counts(line.Vegetarian, line.NonVegetarian, line.Eggetarian, line.Jain, line.Total)
                : new Counts(0, 0, 0, 0, 0);

        var keys = before.Lines.Concat(after.Lines).Select(l => (l.Slot, l.DietCode)).Distinct();
        return keys
            .Select(k => (Key: k, A: Of(after, k.Slot, k.DietCode), B: Of(before, k.Slot, k.DietCode)))
            .Select(x => (x.Key, Change: new Counts(x.A.Vegetarian - x.B.Vegetarian, x.A.NonVegetarian - x.B.NonVegetarian,
                x.A.Eggetarian - x.B.Eggetarian, x.A.Jain - x.B.Jain, x.A.Total - x.B.Total)))
            .Where(x => x.Change != new Counts(0, 0, 0, 0, 0))
            .ToDictionary(x => x.Key, x => x.Change);
    }

    private static ProductionLine Line(ProductionSummary summary, MealSlot round, string diet) =>
        summary.Lines.Single(l => l.Slot == round && l.DietCode == diet);

    private static CreateDietOrderRequest Order(
        Guid admissionId, DietTypeDto diet, FoodPreference preference, IReadOnlyList<string>? restrictions = null, string? allergy = null) =>
        new(admissionId, diet.Id, preference, restrictions, allergy, Instructions: null, FluidRestrictionMl: null, CalorieTargetKcal: null,
            MealSlots: null, StartDate: null, OrderedByPractitionerId: null);

    /// <summary>Front office: registers a new patient and admits him, self-paying, to any bed he may have, under the doctor.</summary>
    private async Task<(Guid AdmissionId, string BedLabel)> AdmitAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var consultantId = (await doctor.GetAsync<AuthUser>("/api/v1/auth/me")).PractitionerId
                           ?? throw new InvalidOperationException("The doctor's account is not linked to a practitioner.");
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Ramesh"), MiddleName: null, LastName: "Kitchen" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));
        var bed = (await frontOffice.GetAsync<List<AvailableBedDto>>($"/api/v1/beds/available?patientId={patient.Id}"))
            .First(b => b.IsEligible && b.Status == BedStatus.Vacant && b.WardType != WardType.Icu);
        var admission = await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patient.Id, BedId: bed.BedId, Type: AdmissionType.Elective, Source: AdmissionSource.OutPatient, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Elective surgery work-up", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
        return (admission.Id, bed.Label);
    }

    /// <summary>
    /// Today in the hospital's calendar (IST). The list is read for tomorrow, so the test must not straddle midnight:
    /// started in the last two minutes of a day, it waits for the next.
    /// </summary>
    private async Task<DateOnly> HospitalDayAsync()
    {
        var clock = server.Services.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();
        var untilMidnight = IndiaClock.StartOfDayUtc(IndiaClock.LocalDate(now).AddDays(1)) - now;
        if (untilMidnight < TimeSpan.FromMinutes(2))
        {
            await Task.Delay(untilMidnight + TimeSpan.FromSeconds(1), clock, TestContext.Current.CancellationToken);
        }

        return IndiaClock.LocalDate(clock.GetUtcNow());
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
