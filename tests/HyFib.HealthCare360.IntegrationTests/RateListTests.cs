using System.Net;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Packages;
using HyFib.HealthCare360.Modules.Billing.Features.Tariffs;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The rate list a hospital displays (Clinical Establishments Rules 2012, r.9(i): the rates of each service, in the local
/// language as well as in English), over the real API: what it lists for a service, a doctor's consultation and the
/// registration fee is what a self-paying patient's bill charges for them, and each service's local name prints on it.
/// </summary>
public sealed class RateListTests(HospitalServer server)
{
    private const string RateList = "/api/v1/billing/rate-list";

    [Fact]
    public async Task What_the_rate_list_says_is_what_a_cash_bill_charges()
    {
        // A dressing with GST and a name in Tamil, and a doctor with a fee of their own and five free follow-up days.
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var created = await admin.PostAsync<ServiceItemDto>("/api/v1/masters/services", new SaveServiceItemRequest(
            $"SV-{Letters(8)}", "Wound dressing — major", null, ServiceCategory.Procedure, null, 1_250m, 18m, "999312", true, false, null, true, null,
            LocalName: "பெரிய காயக் கட்டு"));
        var dressing = await admin.GetAsync<ServiceItemDto>($"/api/v1/masters/services/{created.Id}");
        dressing.LocalName.ShouldBe("பெரிய காயக் கட்டு");
        var surgery = (await admin.GetAsync<List<LookupItem>>("/api/v1/masters/lookups/departments")).Single(d => d.Code == "GS");
        var added = await admin.PostAsync<PractitionerDto>("/api/v1/masters/practitioners", new SavePractitionerRequest(
            Code: $"DR{Letters(6)}", Title: "Dr.", FirstName: "Meena", LastName: "Rates" + Letters(6), Gender: Gender.Female, Qualification: "MBBS, MS (General Surgery)",
            RegistrationCouncil: "Tamil Nadu Medical Council", RegistrationNumber: $"TNMC {Digits(6)}", HprId: null, SpecialtyId: null, DepartmentId: surgery.Id,
            Kind: PractitionerKind.Consultant, Phone: null, Email: null, ConsultationFeeNew: 777m, ConsultationFeeFollowUp: 0m, FollowUpValidityDays: 5,
            Languages: null, Bio: null, PhotoFileId: null, SignatureFileId: null, AvailableForOnlineBooking: false, AvailableForTelehealth: false,
            FacilityIds: null, IsActive: true, Version: null));
        var doctor = await admin.GetAsync<PractitionerDto>($"/api/v1/masters/practitioners/{added.Id}");

        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var rates = (await billing.GetAsync<RateListView>(RateList)).Rates;
        var listedDressing = rates.Sections.SelectMany(s => s.Lines).Single(l => l.Code == dressing.Code);
        listedDressing.LocalName.ShouldBe("பெரிய காயக் கட்டு");
        listedDressing.GstRate.ShouldBe(18m);
        var listedDoctor = rates.Doctors.Single(d => d.Name == doctor.DisplayName);
        (listedDoctor.Department, listedDoctor.FreeFollowUpDays).ShouldBe((surgery.Name, 5));
        var registration = rates.Registration.ShouldNotBeNull("the hospital charges a new patient's registration");

        // A new patient pays for themselves: a visit with the doctor, and the dressing.
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patientId = await PatientAsync(frontOffice);
        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patientId, VisitType.OutPatient, surgery.Id, doctor.Id, null, PayerType.Self, null, ReferralSource.Self, null, null, false, null, null,
            TokenPriority.Normal, IssueToken: false, NotifyPatient: false));
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(
            patientId, visit.Id, null, dressing.Id, 1m, null, null, null, null, null));
        var bill = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(visit.Id, null, null));

        // Each line of the bill comes to what the list says it costs.
        bill.Lines.Single(l => l.ServiceItemId == dressing.Id).NetAmount.ShouldBe(listedDressing.Payable);
        bill.Lines.Single(l => l.ServiceCode == "CONS-NEW").NetAmount.ShouldBe(listedDoctor.Fee.Payable);
        bill.Lines.Single(l => l.ServiceCode == "REG-NEW").NetAmount.ShouldBe(registration.Payable);
        listedDoctor.Fee.Payable.ShouldBe(777m, "the doctor's own fee, not the consultation's price");
        listedDressing.Payable.ShouldBe(1_475m, "₹1,250 and 18% GST, with the default tariff's concession of nothing");

        // The printed list carries the dressing under both its names.
        var document = await billing.GetAsync<PrintableDocumentDto>($"{RateList}/document");
        document.Html.ShouldContain("Wound dressing — major<span class=\"local\">பெரிய காயக் கட்டு</span>");
        document.FileName.ShouldStartWith("rate-list-");
    }

    [Fact]
    public async Task A_package_is_listed_when_a_self_paying_patients_bill_may_use_it()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var plans = await billing.GetAsync<List<TariffPlanListItem>>("/api/v1/billing/tariffs");
        var otherPlan = plans.First(p => p is { Type: not TariffType.General, IsActive: true });
        var anyPlan = await PackageAsync(admin, "Day-care chemotherapy cycle", null);
        var forOtherPlan = await PackageAsync(admin, "Day-care chemotherapy cycle (scheme)", otherPlan.Id);

        var packages = (await billing.GetAsync<RateListView>(RateList)).Rates.Packages;

        var listed = packages.Single(p => p.Price.Code == anyPlan.Code);
        (listed.Price.Payable, listed.LengthOfStayDays, listed.UsedFor).ShouldBe((12_500m, 1, PackageUse.OutPatient));
        packages.ShouldNotContain(p => p.Price.Code == forOtherPlan.Code, "a bill at the self-paying patient's plan refuses it");
    }

    [Fact]
    public async Task The_bed_classes_of_the_wards_are_listed_with_intensive_care_free_of_gst()
    {
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);

        var rooms = (await billing.GetAsync<RateListView>(RateList)).Rates.Rooms;

        var icu = rooms.Single(r => r.Wards.Contains("Intensive Care Unit"));
        (icu.IntensiveCare, icu.Day.GstRate, icu.Visit?.Code).ShouldBe((true, 0m, "VISIT-DR-ICU"));
        var general = rooms.Single(r => r.Wards.Contains("General Ward — Male"));
        (general.IntensiveCare, general.Day.Code, general.Nursing?.Code, general.Visit?.Code).ShouldBe((false, "BED-GEN", "NURS-DAY", "VISIT-DR"));
        general.Wards.ShouldContain("General Ward — Female");
    }

    [Fact]
    public async Task Staff_who_neither_bill_nor_keep_the_tariffs_cannot_read_it()
    {
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);

        using var response = await nurse.SendRawAsync(HttpMethod.Get, RateList);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static async Task<PackageDetail> PackageAsync(ApiClient admin, string name, Guid? tariffPlanId)
    {
        // The package itself is billed under a catalogue service of category Package.
        var item = await admin.PostAsync<ServiceItemDto>("/api/v1/masters/services", new SaveServiceItemRequest(
            $"SV-{Letters(8)}", name, null, ServiceCategory.Package, null, 12_500m, 0m, null, true, false, null, true, null));
        return await admin.PostAsync<PackageDetail>("/api/v1/billing/packages", new SavePackageRequest(
            $"PKG-{Letters(8)}", name, item.Id, 12_500m, 1, tariffPlanId, PackageUse.OutPatient, null, true,
            [new PackageInclusionInput(null, ServiceCategory.Consumable, null, 4_000m, "Drugs and consumables")], null));
    }

    private static async Task<Guid> PatientAsync(ApiClient frontOffice)
    {
        // A patient of this test alone: the unique surname and mobile number keep the duplicate check quiet.
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Kannan"), MiddleName: null, LastName: "Rates" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));
        return created.Id;
    }

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('A' + Random.Shared.Next(26)))]);

    private static string Digits(int length) => string.Concat(Enumerable.Range(0, length).Select(_ => Random.Shared.Next(10)));
}
