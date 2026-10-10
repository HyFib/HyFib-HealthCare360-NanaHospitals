using System.Net;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Packages;
using HyFib.HealthCare360.Modules.Billing.Features.Tariffs;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Packages on OP bills (MB-02, OPB) through the API, as the billing desk uses them: a draft is billed at an
/// out-patient package, the package absorbs the services it includes up to its caps, and whatever is beyond the package
/// is billed as usual. The package line belongs to its bill: removing the package, discarding the draft or cancelling
/// the bill cancels it, so a later bill of the same services never carries it unasked.
/// </summary>
public sealed class OpBillPackageTests(HospitalServer server)
{
    private const string Bills = "/api/v1/billing/bills";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_bill_at_a_package_charges_the_package_price_and_only_what_is_beyond_it()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var procedure = await ServiceAsync(admin, "Cataract surgery (phaco)", ServiceCategory.Procedure, 4000m);
        var lens = await ServiceAsync(admin, "Foldable intraocular lens", ServiceCategory.Consumable, 1500m);
        var drops = await ServiceAsync(admin, "Post-operative eye drops", ServiceCategory.Consumable, 400m);
        var scan = await ServiceAsync(admin, "Orbit X-ray", ServiceCategory.Radiology, 700m);
        var package = await PackageAsync(admin, "Cataract surgery (one eye)", PackageUse.OutPatient, 5000m, null,
            new PackageInclusionInput(procedure.Id, null, 1m, null, "One procedure"),
            new PackageInclusionInput(null, ServiceCategory.Consumable, null, 1800m, "Lens and consumables"));

        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var patientId = await PatientAsync();
        var draft = await billing.PostAsync<BillDetail>(Bills, new CreateBillRequest(null, patientId, null));
        foreach (var service in new[] { procedure, lens, drops, scan })
        {
            draft = await AddAsync(billing, draft, service.Id);
        }

        draft.TariffDiscountAmount.ShouldBe(0m, "The default tariff gives no concession, so the amounts below are the services' prices.");
        draft.PatientAmount.ShouldBe(6600m);
        draft.Package.ShouldBeNull();

        // Billed at the package: one line at its price; the procedure and the consumables up to ₹1,800 are absorbed.
        var priced = await billing.PutAsync<BillDetail>($"{Bills}/{draft.Id}/package", new SetBillPackageRequest(package.Id, draft.Version));
        var packageLine = priced.Lines.Where(l => l.Category == ServiceCategory.Package).ShouldHaveSingleItem();
        packageLine.GrossAmount.ShouldBe(5000m);
        packageLine.Description.ShouldBe("Cataract surgery (one eye)");
        Covered(priced, procedure).ShouldBe(4000m);
        Covered(priced, lens).ShouldBe(1500m);
        Covered(priced, drops).ShouldBe(300m, "The consumables cap of ₹1,800 leaves ₹300 for the drops.");
        Covered(priced, scan).ShouldBe(0m, "The package does not include radiology.");
        priced.PackageCoveredAmount.ShouldBe(5800m);
        priced.PatientAmount.ShouldBe(5800m, "The package price, the ₹100 of drops beyond the cap and the X-ray.");
        var summary = priced.Package.ShouldNotBeNull();
        summary.PackageId.ShouldBe(package.Id);
        summary.Name.ShouldBe("Cataract surgery (one eye)");
        summary.Price.ShouldBe(5000m);
        summary.Actuals.ShouldBe(6600m);
        summary.Covered.ShouldBe(5800m);
        summary.BeyondPackage.ShouldBe(800m);

        // A second procedure is beyond the package's one, and is billed in full.
        var more = await AddAsync(billing, priced, procedure.Id);
        more.Lines.Where(l => l.ServiceItemId == procedure.Id).Select(l => l.PackageCoveredAmount).ShouldBe(new[] { 4000m, 0m }, ignoreOrder: true);
        more.PatientAmount.ShouldBe(9800m);

        var bill = await billing.PostAsync<BillDetail>($"{Bills}/{more.Id}/finalize", new BillVersionRequest(more.Version));
        bill.Status.ShouldBe(BillStatus.Finalized);
        bill.DueAmount.ShouldBe(9800m);
        bill.Package.ShouldNotBeNull().Covered.ShouldBe(5800m);

        // Cancelling the bill cancels its package line; the services wait for the next bill, which has no package.
        var accounts = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        await accounts.PostAsync<BillDetail>($"{Bills}/{bill.Id}/cancel", new CancelBillRequest("Billed at the wrong package", bill.Version));
        var charges = await ChargesAsync(billing, patientId);
        charges.Where(c => c.SourceModule == "billing.package").ShouldHaveSingleItem().Status.ShouldBe(ChargeStatus.Cancelled);

        var next = await billing.PostAsync<BillDetail>(Bills, new CreateBillRequest(null, patientId, null));
        next.Lines.Count.ShouldBe(5);
        next.Lines.ShouldNotContain(l => l.Category == ServiceCategory.Package);
        next.Package.ShouldBeNull();
        next.PackageCoveredAmount.ShouldBe(0m);
        next.PatientAmount.ShouldBe(10600m);
    }

    [Fact]
    public async Task Only_an_out_patient_package_for_the_bills_tariff_can_price_it_and_taking_it_off_bills_every_service_again()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var dressing = await ServiceAsync(admin, "Wound dressing", ServiceCategory.Procedure, 600m);
        var forAdmissions = await PackageAsync(admin, "Dressing stay", PackageUse.InPatient, 500m, null,
            new PackageInclusionInput(dressing.Id, null, null, null, null));
        var forBoth = await PackageAsync(admin, "Dressing course", PackageUse.Both, 500m, null,
            new PackageInclusionInput(dressing.Id, null, null, null, null));

        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var draft = await billing.PostAsync<BillDetail>(Bills, new CreateBillRequest(null, await PatientAsync(), null));
        draft = await AddAsync(billing, draft, dressing.Id);

        // A package for admissions, or one priced for another tariff plan, cannot price an OP bill.
        await RefusedAsync(billing, draft, forAdmissions.Id, "is a package for in-patient stays");
        var plans = await billing.GetAsync<List<TariffPlanListItem>>("/api/v1/billing/tariffs");
        var otherPlan = plans.First(p => p.Id != draft.TariffPlanId);
        var forOtherPlan = await PackageAsync(admin, "Dressing (other plan)", PackageUse.OutPatient, 500m, otherPlan.Id,
            new PackageInclusionInput(dressing.Id, null, null, null, null));
        await RefusedAsync(billing, draft, forOtherPlan.Id, "is priced for another tariff plan");

        // A package for both is accepted.
        var priced = await billing.PutAsync<BillDetail>($"{Bills}/{draft.Id}/package", new SetBillPackageRequest(forBoth.Id, draft.Version));
        priced.PatientAmount.ShouldBe(500m);
        var firstLine = priced.Lines.Where(l => l.Category == ServiceCategory.Package).ShouldHaveSingleItem();

        // Removing the package's line takes the package off: every service is billed in full again.
        using (var removed = await billing.SendRawAsync(HttpMethod.Delete, $"{Bills}/{priced.Id}/lines/{firstLine.Id}?version={priced.Version}"))
        {
            removed.EnsureSuccessStatusCode();
        }

        var unpriced = await billing.GetAsync<BillDetail>($"{Bills}/{priced.Id}");
        unpriced.Package.ShouldBeNull();
        unpriced.Lines.ShouldNotContain(l => l.Category == ServiceCategory.Package);
        unpriced.PackageCoveredAmount.ShouldBe(0m);
        unpriced.PatientAmount.ShouldBe(600m);

        // Choosing it again adds a new line; choosing no package removes it the same way.
        var again = await billing.PutAsync<BillDetail>($"{Bills}/{unpriced.Id}/package", new SetBillPackageRequest(forBoth.Id, unpriced.Version));
        again.Lines.Where(l => l.Category == ServiceCategory.Package).ShouldHaveSingleItem().ChargeId.ShouldNotBe(firstLine.ChargeId);
        var none = await billing.PutAsync<BillDetail>($"{Bills}/{again.Id}/package", new SetBillPackageRequest(null, again.Version));
        none.Package.ShouldBeNull();
        none.PatientAmount.ShouldBe(600m);

        // Discarding a draft at a package cancels the package line with the draft's own services.
        var last = await billing.PutAsync<BillDetail>($"{Bills}/{none.Id}/package", new SetBillPackageRequest(forBoth.Id, none.Version));
        using (var discarded = await billing.SendRawAsync(HttpMethod.Delete, $"{Bills}/{last.Id}?version={last.Version}"))
        {
            discarded.EnsureSuccessStatusCode();
        }

        var charges = await ChargesAsync(billing, last.PatientId);
        charges.Where(c => c.SourceModule == "billing.package").ShouldAllBe(c => c.Status == ChargeStatus.Cancelled);
        charges.Count(c => c.SourceModule == "billing.package").ShouldBe(3);
    }

    private static decimal Covered(BillDetail bill, ServiceItemDto service) =>
        bill.Lines.Where(l => l.ServiceItemId == service.Id).Sum(l => l.PackageCoveredAmount);

    private static Task<BillDetail> AddAsync(ApiClient billing, BillDetail draft, Guid serviceItemId) =>
        billing.PostAsync<BillDetail>($"{Bills}/{draft.Id}/lines", new AddBillLineRequest(serviceItemId, 1m, null, null, null, null, draft.Version));

    private static async Task RefusedAsync(ApiClient billing, BillDetail draft, Guid packageId, string reason)
    {
        using var response = await billing.SendRawAsync(HttpMethod.Put, $"{Bills}/{draft.Id}/package", new SetBillPackageRequest(packageId, draft.Version));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadAsStringAsync(Ct);
        problem.ShouldContain("packageId");
        problem.ShouldContain(reason);
    }

    private static async Task<List<ChargeDto>> ChargesAsync(ApiClient billing, Guid patientId) =>
        (await billing.GetAsync<PagedResult<ChargeDto>>($"/api/v1/billing/charges?patientId={patientId}&pageSize=200")).Items.ToList();

    private static Task<ServiceItemDto> ServiceAsync(ApiClient admin, string name, ServiceCategory category, decimal price) =>
        admin.PostAsync<ServiceItemDto>("/api/v1/masters/services", new SaveServiceItemRequest(
            $"SV-{Letters(8)}", name, null, category, null, price, 0m, null, true, false, null, true, null));

    private static async Task<PackageDetail> PackageAsync(
        ApiClient admin, string name, PackageUse usedFor, decimal price, Guid? tariffPlanId, params PackageInclusionInput[] inclusions)
    {
        // The package itself is billed under a catalogue service of category Package.
        var item = await ServiceAsync(admin, name, ServiceCategory.Package, price);
        return await admin.PostAsync<PackageDetail>("/api/v1/billing/packages", new SavePackageRequest(
            $"PKG-{Letters(8)}", name, item.Id, price, null, tariffPlanId, usedFor, null, true, inclusions, null));
    }

    private async Task<Guid> PatientAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);

        // A patient of this test alone: the unique surname and mobile number keep the duplicate check quiet.
        var surname = "Package" + Letters(8);
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: "Lata", MiddleName: null, LastName: surname, Gender: Gender.Female,
            DateOfBirth: new DateOnly(1958, 1, 1).AddDays(Random.Shared.Next(365 * 10)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));
        return created.Id;
    }

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('A' + Random.Shared.Next(26)))]);

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);
}
