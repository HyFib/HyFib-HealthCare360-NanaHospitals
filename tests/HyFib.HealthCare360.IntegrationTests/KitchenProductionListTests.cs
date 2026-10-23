using System.Globalization;
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
/// The kitchen production list (DIET-03) over the real API and database: the counts per meal round and per diet that the
/// kitchen cooks to, from the plan the diet orders make. A patient kept nil by mouth has nothing cooked, and when a diet
/// changes, the meals planned under the old one are cancelled and are not cooked; the new diet's are. The test plans
/// tomorrow, whose rounds are all still to come, with diets of its own, so the lines it reads are its patients' alone.
/// </summary>
public sealed class KitchenProductionListTests(HospitalServer server)
{
    private const string Orders = "/api/v1/dietary/orders";

    [Fact]
    public async Task Counts_each_rounds_meals_per_diet_and_leaves_out_nil_by_mouth_and_cancelled_meals()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var tomorrow = IndiaClock.LocalDate(server.Services.GetRequiredService<TimeProvider>().GetUtcNow()).AddDays(1);

        // The hospital's diets for this test: normal, diabetic (small frequent feeds) and nil by mouth.
        var suffix = Letters(6).ToUpperInvariant();
        var normal = await DietAsync(admin, $"NOR-{suffix}", DietCategory.Regular, MealSlots.Standard);
        var diabetic = await DietAsync(admin, $"DIA-{suffix}", DietCategory.Therapeutic, MealSlots.All);
        var npo = await DietAsync(admin, $"NPO-{suffix}", DietCategory.NilByMouth, MealSlots.Standard);

        var anand = await AdmitAsync(Gender.Male);
        var bina = await AdmitAsync(Gender.Female);
        var chitra = await AdmitAsync(Gender.Female);
        await doctor.PostAsync<DietOrderDto>(Orders, Order(anand.Id, normal.Id, FoodPreference.Vegetarian));
        await doctor.PostAsync<DietOrderDto>(Orders, Order(bina.Id, normal.Id, FoodPreference.Jain, ["No onion"], "Peanuts"));
        await doctor.PostAsync<DietOrderDto>(Orders, Order(chitra.Id, npo.Id, FoodPreference.NonVegetarian));

        // The kitchen plans tomorrow: Anand and Bina have every standard round, Chitra none.
        await nurse.PostAsync<MealPlanResult>("/api/v1/dietary/meal-plans/generate", new GenerateMealPlanRequest(tomorrow));
        var production = await ProductionAsync(nurse, tomorrow, normal, diabetic, npo);
        production.Select(l => (l.Slot, l.DietCode, l.Vegetarian, l.Jain, l.Total)).ShouldBe(
            MealSlots.Split(MealSlots.Standard).Select(slot => (slot, normal.Code, 1, 1, 2)));
        production.ShouldAllBe(l => l.ServiceTime == MealSlots.ServiceTime(l.Slot) && l.DietName == normal.Name);
        var binasNote = $"{bina.BedLabel}: No onion (allergy: Peanuts)";
        production.ShouldAllBe(l => l.SpecialNotes.Count == 1 && l.SpecialNotes[0] == binasNote);

        // The doctor puts Anand on the diabetic diet: his normal meals for tomorrow are cancelled and not cooked; the
        // diabetic diet's every round is.
        await doctor.PostAsync<DietOrderDto>(Orders, Order(anand.Id, diabetic.Id, FoodPreference.Vegetarian));
        var expected = new List<(MealSlot, string, int, int, int)>();
        foreach (var slot in MealSlots.Split(MealSlots.All))
        {
            expected.Add((slot, diabetic.Code, 1, 0, 1));
            if (MealSlots.Contains(MealSlots.Standard, slot))
            {
                expected.Add((slot, normal.Code, 0, 1, 1));
            }
        }

        production = await ProductionAsync(nurse, tomorrow, normal, diabetic, npo);
        production.Select(l => (l.Slot, l.DietCode, l.Vegetarian, l.Jain, l.Total)).ShouldBe(expected);
    }

    /// <summary>Tomorrow's production lines of the test's own diets, as the kitchen reads them.</summary>
    private static async Task<List<ProductionLine>> ProductionAsync(ApiClient nurse, DateOnly date, params DietTypeDto[] diets)
    {
        var summary = await nurse.GetAsync<ProductionSummary>(string.Create(CultureInfo.InvariantCulture,
            $"/api/v1/dietary/meal-plans/production?date={date:yyyy-MM-dd}"));
        summary.Date.ShouldBe(date);
        summary.TotalMeals.ShouldBe(summary.Lines.Sum(l => l.Total));
        var codes = diets.Select(d => d.Code).ToHashSet();
        return summary.Lines.Where(l => codes.Contains(l.DietCode)).ToList();
    }

    private static Task<DietTypeDto> DietAsync(ApiClient admin, string code, DietCategory category, MealSlot slots) =>
        admin.PostAsync<DietTypeDto>("/api/v1/dietary/diet-types",
            new SaveDietTypeRequest(code, $"Test diet {code}", null, category, null, slots, 900, IsActive: true, null));

    private static CreateDietOrderRequest Order(
        Guid admissionId, Guid dietTypeId, FoodPreference preference, List<string>? restrictions = null, string? allergy = null) =>
        new(admissionId, dietTypeId, preference, restrictions, allergy, null, null, null, null, null, null);

    /// <summary>Front office registers a new patient and admits him or her, self-paying, to a vacant bed under the doctor.</summary>
    private async Task<AdmissionCreated> AdmitAsync(Gender gender)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var consultantId = (await doctor.GetAsync<AuthUser>("/api/v1/auth/me")).PractitionerId
                           ?? throw new InvalidOperationException("The doctor's account is not linked to a practitioner.");
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: "Kitchen", MiddleName: null, LastName: "List" + Letters(8), Gender: gender,
            DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(10_000)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Random.Shared.Next(100_000_000, 999_999_999).ToString(CultureInfo.InvariantCulture),
            AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));

        var bed = (await frontOffice.GetAsync<List<AvailableBedDto>>($"/api/v1/beds/available?patientId={patient.Id}"))
            .First(b => b.IsEligible && b.Status == BedStatus.Vacant && b.WardType is WardType.General or WardType.SemiPrivate or WardType.Private);
        return await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patient.Id, BedId: bed.BedId, Type: AdmissionType.Elective, Source: AdmissionSource.OutPatient, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Observation", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
    }

    private static string Letters(int count) =>
        string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
