using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Localization;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The rate list a hospital displays (Clinical Establishments Rules 2012, r.9(i): the rates of each service, in the local
/// language as well as in English), over the real API: what it lists for a service, each doctor's consultation and the
/// registration fee is what a self-paying patient's bill charges for them, each service's name prints in the facility's own
/// local language, and anyone may read the same list on the patient portal without signing in, unless the hospital keeps a
/// facility's off it.
/// </summary>
public sealed class RateListTests(HospitalServer server)
{
    private const string RateList = "/api/v1/billing/rate-list";

    private const string PublicRateList = "/api/v1/public/rate-list";

    private const string Tamil = "பெரிய காயக் கட்டு";

    private const string Hindi = "घाव की बड़ी पट्टी";

    private const string MinorTamil = "சிறிய காயக் கட்டு";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task What_the_rate_list_says_is_what_a_cash_bill_charges()
    {
        // A dressing with GST and names in Tamil and Hindi, and a doctor with a fee of their own and five free follow-up days.
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var created = await admin.PostAsync<ServiceItemDto>("/api/v1/masters/services", new SaveServiceItemRequest(
            $"SV-{Letters(8)}", "Wound dressing — major", null, ServiceCategory.Procedure, null, 1_250m, 18m, "999312", true, false, null, true, null,
            LocalNames: new Dictionary<string, string> { ["ta"] = Tamil, ["hi"] = " " + Hindi + " " }));
        var dressing = await admin.GetAsync<ServiceItemDto>($"/api/v1/masters/services/{created.Id}");
        dressing.LocalNames.ShouldBe(new Dictionary<string, string> { ["ta"] = Tamil, ["hi"] = Hindi }, ignoreOrder: true);
        var surgery = (await admin.GetAsync<List<LookupItem>>("/api/v1/masters/lookups/departments")).Single(d => d.Code == "GS");
        var doctor = await DoctorAsync(admin, surgery.Id, PractitionerKind.Consultant, fee: 777m, freeFollowUpDays: 5);

        // The facility is in Tamil Nadu, whatever other tests have made of its address.
        await using var scope = server.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        await settings.SetAsync(LocalLanguages.Setting, "ta", null, false, Ct);
        try
        {
            await CashBillAsync(dressing, surgery, doctor);
        }
        finally
        {
            await settings.RemoveAsync(LocalLanguages.Setting, null, Ct);
        }
    }

    [Fact]
    public async Task Each_facility_prints_a_services_name_in_its_own_local_language()
    {
        // One service named in two languages: a Tamil Nadu facility prints the Tamil name, a Delhi facility the Hindi one.
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var code = $"SV-{Letters(8)}";
        await admin.PostAsync<ServiceItemDto>("/api/v1/masters/services", new SaveServiceItemRequest(
            code, "Wound dressing — major", null, ServiceCategory.Procedure, null, 1_250m, 0m, "999312", true, false, null, true, null,
            LocalNames: new Dictionary<string, string> { ["ta"] = Tamil, ["hi"] = Hindi }));
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        await using var scope = server.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        try
        {
            foreach (var (language, name) in new[] { ("ta", Tamil), ("hi", Hindi) })
            {
                await settings.SetAsync(LocalLanguages.Setting, language, null, false, Ct);

                var view = await billing.GetAsync<RateListView>(RateList);
                (view.Language, view.LocalLanguage?.Code).ShouldBe((language, language));
                view.Rates.Sections.SelectMany(s => s.Lines).Single(l => l.Code == code).LocalName.ShouldBe(name);
                (await billing.GetAsync<PrintableDocumentDto>($"{RateList}/document")).Html.ShouldContain($"<span class=\"local\">{name}</span>");
                (await admin.GetAsync<List<LocalLanguageItem>>("/api/v1/masters/lookups/local-languages"))
                    .Where(l => l.AtFacility).Select(l => l.Code).ShouldBe([language], "the service editor offers a name in it");
            }

            // A choice that names no language is told, for the screen to say so.
            await settings.SetAsync(LocalLanguages.Setting, "Tamizh", null, false, Ct);
            (await billing.GetAsync<RateListView>(RateList)).UnknownSetting.ShouldBe("Tamizh");
        }
        finally
        {
            await settings.RemoveAsync(LocalLanguages.Setting, null, Ct);
        }
    }

    [Theory]
    [InlineData(PractitionerKind.Resident)]
    [InlineData(PractitionerKind.Visiting)]
    public async Task Every_doctor_whose_visit_is_charged_is_listed_at_what_the_bill_charges(PractitionerKind kind)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var surgery = (await admin.GetAsync<List<LookupItem>>("/api/v1/masters/lookups/departments")).Single(d => d.Code == "GS");
        var doctor = await DoctorAsync(admin, surgery.Id, kind, fee: 350m, freeFollowUpDays: 3);

        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var listed = (await billing.GetAsync<RateListView>(RateList)).Rates.Doctors.Single(d => d.Name == doctor.DisplayName);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patientId = await PatientAsync(frontOffice);
        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patientId, VisitType.OutPatient, surgery.Id, doctor.Id, null, PayerType.Self, null, ReferralSource.Self, null, null, false, null, null,
            TokenPriority.Normal, IssueToken: false, NotifyPatient: false));
        var bill = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(visit.Id, null, null));

        bill.Lines.Single(l => l.ServiceCode == "CONS-NEW").NetAmount.ShouldBe(listed.Fee.Payable);
        (listed.Fee.Payable, listed.FreeFollowUpDays).ShouldBe((350m, 3));
    }

    private async Task CashBillAsync(ServiceItemDto dressing, LookupItem surgery, PractitionerDto doctor)
    {
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var rates = (await billing.GetAsync<RateListView>(RateList)).Rates;
        var listedDressing = rates.Sections.SelectMany(s => s.Lines).Single(l => l.Code == dressing.Code);
        listedDressing.LocalName.ShouldBe(Tamil);
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

        // The printed list carries the dressing under its English and Tamil names.
        var document = await billing.GetAsync<PrintableDocumentDto>($"{RateList}/document");
        document.Html.ShouldContain($"Wound dressing — major<span class=\"local\">{Tamil}</span>");
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
    public async Task Anyone_reads_on_the_portal_the_list_the_counter_prints_and_a_cash_bill_charges_it()
    {
        // A minor dressing with GST and a Tamil name, and a doctor with a fee of their own.
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var created = await admin.PostAsync<ServiceItemDto>("/api/v1/masters/services", new SaveServiceItemRequest(
            $"SV-{Letters(8)}", "Wound dressing — minor", null, ServiceCategory.Procedure, null, 450m, 18m, "999312", true, false, null, true, null,
            LocalNames: new Dictionary<string, string> { ["ta"] = MinorTamil }));
        var dressing = await admin.GetAsync<ServiceItemDto>($"/api/v1/masters/services/{created.Id}");
        var surgery = (await admin.GetAsync<List<LookupItem>>("/api/v1/masters/lookups/departments")).Single(d => d.Code == "GS");
        var doctor = await DoctorAsync(admin, surgery.Id, PractitionerKind.Consultant, fee: 650m, freeFollowUpDays: 7);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var facilityId = (await billing.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();
        var visitor = server.AnonymousOnPortal(ownAddress: true);

        // The facility's language is Tamil, whatever other tests have made of its address.
        await using var scope = server.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        await settings.SetAsync(LocalLanguages.Setting, "ta", facilityId, false, Ct);
        try
        {
            (await visitor.GetAsync<List<PublicRateListFacility>>($"{PublicRateList}/")).ShouldContain(f => f.Id == facilityId);

            // The published list is the counter's, to the line, in the same languages; what staff are to correct is not in it.
            var (counter, published) = await CounterAndPublishedAsync(billing, visitor, facilityId);
            Json(published.Rates).ShouldBe(Json(counter.Rates));
            (published.Language, published.LocalLanguage).ShouldBe((counter.Language, counter.LocalLanguage));
            (published.LocalLanguage?.Code).ShouldBe("ta");
            published.Facility.Id.ShouldBe(facilityId);
            (await visitor.GetAsync<JsonElement>($"{PublicRateList}/{facilityId}")).EnumerateObject().Select(p => p.Name)
                .ShouldBe(["facility", "rates", "language", "localLanguage"]);

            // A new patient pays for themselves: each line of their bill is what the published list says.
            var listed = published.Rates.Sections.SelectMany(s => s.Lines).Single(l => l.Code == dressing.Code);
            listed.LocalName.ShouldBe(MinorTamil);
            listed.Payable.ShouldBe(531m, "₹450 and 18% GST");
            var fee = published.Rates.Doctors.Single(d => d.Name == doctor.DisplayName).Fee;
            var registration = published.Rates.Registration.ShouldNotBeNull("the hospital charges a new patient's registration");
            var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
            var patientId = await PatientAsync(frontOffice);
            var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
                patientId, VisitType.OutPatient, surgery.Id, doctor.Id, null, PayerType.Self, null, ReferralSource.Self, null, null, false, null, null,
                TokenPriority.Normal, IssueToken: false, NotifyPatient: false));
            await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(
                patientId, visit.Id, null, dressing.Id, 1m, null, null, null, null, null));
            var bill = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(visit.Id, null, null));
            bill.Lines.Single(l => l.ServiceItemId == dressing.Id).NetAmount.ShouldBe(listed.Payable);
            bill.Lines.Single(l => l.ServiceCode == "CONS-NEW").NetAmount.ShouldBe(fee.Payable);
            bill.Lines.Single(l => l.ServiceCode == "REG-NEW").NetAmount.ShouldBe(registration.Payable);

            // Its PDF downloads without signing in; a server with no browser to make it says so, and nothing of why.
            using var pdf = await visitor.SendRawAsync(HttpMethod.Get, $"{PublicRateList}/{facilityId}/pdf");
            if (server.Services.GetRequiredService<IPdfRenderer>().Status.Available)
            {
                pdf.StatusCode.ShouldBe(HttpStatusCode.OK);
                pdf.Content.Headers.ContentType?.MediaType.ShouldBe("application/pdf");
                Encoding.ASCII.GetString(await pdf.Content.ReadAsByteArrayAsync(Ct), 0, 5).ShouldBe("%PDF-");
                (pdf.Content.Headers.ContentDisposition?.FileNameStar ?? pdf.Content.Headers.ContentDisposition?.FileName?.Trim('"'))
                    .ShouldStartWith("rate-list-");
            }
            else
            {
                pdf.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
                var problem = await pdf.Content.ReadFromJsonAsync<JsonElement>(Ct);
                problem.GetProperty("code").GetString().ShouldBe("pdf.unavailable");
                problem.GetProperty("detail").GetString().ShouldBe("The PDF cannot be made just now. The rates on this page are the same.");
            }
        }
        finally
        {
            await settings.RemoveAsync(LocalLanguages.Setting, facilityId, Ct);
        }
    }

    [Fact]
    public async Task A_facility_the_hospital_keeps_off_the_portal_is_not_there_though_its_counter_prints_the_list()
    {
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var facilityId = (await billing.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();
        var visitor = server.AnonymousOnPortal(ownAddress: true);
        await using var scope = server.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        await settings.SetAsync(RateListService.PublishSetting, false, facilityId, false, Ct);
        try
        {
            (await visitor.GetAsync<List<PublicRateListFacility>>($"{PublicRateList}/")).ShouldNotContain(f => f.Id == facilityId);
            foreach (var path in new[] { $"{PublicRateList}/{facilityId}", $"{PublicRateList}/{facilityId}/pdf" })
            {
                using var response = await visitor.SendRawAsync(HttpMethod.Get, path);
                response.StatusCode.ShouldBe(HttpStatusCode.NotFound, path);
            }

            (await billing.GetAsync<RateListView>(RateList)).Rates.AsOn.ShouldNotBe(default);
        }
        finally
        {
            await settings.RemoveAsync(RateListService.PublishSetting, facilityId, Ct);
        }

        // Published again once the setting is gone; a facility that does not exist has no list.
        (await visitor.GetAsync<List<PublicRateListFacility>>($"{PublicRateList}/")).ShouldContain(f => f.Id == facilityId);
        using var unknown = await visitor.SendRawAsync(HttpMethod.Get, $"{PublicRateList}/{Guid.NewGuid()}");
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Staff_who_neither_bill_nor_keep_the_tariffs_cannot_read_it()
    {
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);

        using var response = await nurse.SendRawAsync(HttpMethod.Get, RateList);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The counter's list and the published one, read while nothing changed the counter's: other tests add services and
    /// doctors as they run.
    /// </summary>
    private async Task<(RateListView Counter, PublicRateList Published)> CounterAndPublishedAsync(
        ApiClient billing, ApiClient visitor, Guid facilityId)
    {
        for (var attempt = 1; ; attempt++)
        {
            var before = await billing.GetAsync<RateListView>(RateList);
            var published = await visitor.GetAsync<PublicRateList>($"{PublicRateList}/{facilityId}");
            var after = await billing.GetAsync<RateListView>(RateList);
            if (Json(before.Rates) == Json(after.Rates) || attempt == 5)
            {
                return (after, published);
            }
        }
    }

    private static string Json(RateList rates) => JsonSerializer.Serialize(rates);

    private static async Task<PractitionerDto> DoctorAsync(ApiClient admin, Guid departmentId, PractitionerKind kind, decimal fee, int freeFollowUpDays)
    {
        var added = await admin.PostAsync<PractitionerDto>("/api/v1/masters/practitioners", new SavePractitionerRequest(
            Code: $"DR{Letters(6)}", Title: "Dr.", FirstName: "Meena", LastName: "Rates" + Letters(6), Gender: Gender.Female, Qualification: "MBBS, MS (General Surgery)",
            RegistrationCouncil: "Tamil Nadu Medical Council", RegistrationNumber: $"TNMC {Digits(6)}", HprId: null, SpecialtyId: null, DepartmentId: departmentId,
            Kind: kind, Phone: null, Email: null, ConsultationFeeNew: fee, ConsultationFeeFollowUp: 0m, FollowUpValidityDays: freeFollowUpDays,
            Languages: null, Bio: null, PhotoFileId: null, SignatureFileId: null, AvailableForOnlineBooking: false, AvailableForTelehealth: false,
            FacilityIds: null, IsActive: true, Version: null));
        return await admin.GetAsync<PractitionerDto>($"/api/v1/masters/practitioners/{added.Id}");
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
