using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Inpatient;
using HyFib.HealthCare360.Modules.Billing.Features.Packages;
using HyFib.HealthCare360.Modules.Billing.Features.Tariffs;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A billing package's price and what it covers change only when two people agree (maker-checker), over the real API and
/// database: the billing desk or the hospital administrator asks, the accountant decides. A new package is out of use, at
/// ₹0 and covering nothing, until its terms are approved; a change to a package in use waits, and the approver is told the
/// stays it reaches, as a stay's package is priced at its final bill from the package as it is then; taking a package out
/// of use, and its covers' notes, are saved at once; a stay takes only a package for its plan or any plan; an approval
/// never overwrites a change made after it was asked for. Billing's own package-against-actuals for a stay on the package
/// is the witness.
/// </summary>
public sealed class PackageApprovalTests(HospitalServer server)
{
    private const string PackagesPath = "/api/v1/billing/packages";
    private const string AccountsPath = "/api/v1/billing/ip-accounts";
    private const string BedNotes = "Three bed days";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_package_is_out_of_use_at_nothing_covering_nothing_until_a_second_person_approves_its_terms()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var (item, code) = await PackageServiceAsync(admin);
        var created = await admin.PostAsync<PackageDetail>(PackagesPath, Package(item, 9_000m, null) with { Reason = "Agreed with the surgeons." });
        (created.IsActive, created.Price, created.Inclusions.Count).ShouldBe((false, 0m, 0), "made out of use, at ₹0 covering nothing, until approved");
        var pending = created.PendingTerms.ShouldNotBeNull();
        (pending.Asked.IsActive, pending.Asked.Price, pending.Asked.Covers.Count, pending.ServiceCode).ShouldBe((true, 9_000m, 2, code));
        (await admin.GetAsync<List<PackageListItem>>(PackagesPath)).ShouldNotContain(p => p.Id == created.Id, "no stay or bill may choose it yet");

        // The hospital administrator may approve prices, but not a change they asked for; the billing desk, who manages
        // packages, may not approve prices at all; the accountant decides.
        var request = await admin.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{pending.ApprovalId}");
        (request.Kind, request.CanDecide).ShouldBe((PackageApprovals.Kind, false));
        request.Details.ShouldContain(d => d.Label == "Price" && d.Value == "₹0.00 → ₹9,000.00");
        request.Details.ShouldContain(d => d.Label == "Covers" && d.Value == "Added: every bed charge service, up to 3");
        request.Details.ShouldContain(d => d.Label == "Covers" && d.Value == "Added: every consumable service, up to ₹2,000.00");
        using (var own = await admin.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{pending.ApprovalId}/approve", new ApproveRequest(null, request.Version)))
        {
            own.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await own.Content.ReadAsStringAsync(Ct)).ShouldContain("approval.self");
        }

        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        using (var desk = await billing.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{pending.ApprovalId}/approve", new ApproveRequest(null, request.Version)))
        {
            desk.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await desk.Content.ReadAsStringAsync(Ct)).ShouldContain("approval.not_permitted");
        }

        await ApproveAsAccountantAsync(pending.ApprovalId);
        var live = await admin.GetAsync<PackageDetail>($"{PackagesPath}/{created.Id}");
        (live.IsActive, live.Price, live.Inclusions.Count, live.PendingTerms).ShouldBe((true, 9_000m, 2, (PendingPackageTerms?)null));
        (await admin.GetAsync<List<PackageListItem>>(PackagesPath)).ShouldContain(p => p.Id == created.Id);
        (await AuditCountAsync("package.terms_changed", created.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task A_packages_price_waits_and_a_stay_on_it_is_priced_at_the_package_in_use_until_it_is_approved()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var (item, _) = await PackageServiceAsync(admin);
        var package = await server.SavePackageAsync(admin, Package(item, 9_000m, null));
        await using var stay = await AdmitAsync();
        await AssignAsync(billing, stay.Account, package.Id);
        (await PackagePriceAsync(billing, stay.Account)).ShouldBe(9_000m);

        // The billing desk asks for a dearer package: the approver is told it reaches the stay already on it.
        var asked = await billing.PutAsync<ApprovalRequestDto>($"{PackagesPath}/{package.Id}", Package(item, 9_900m, null, package) with { Reason = "Dressings cost more." });
        asked.Kind.ShouldBe(PackageApprovals.Kind);
        asked.Details.ShouldContain(d => d.Label == "Price" && d.Value == "₹9,000.00 → ₹9,900.00 (+10%)");
        asked.Details.ShouldContain(d => d.Label == "Stays on it" && d.Value.StartsWith("1 stay(s) in progress", StringComparison.Ordinal));
        (await PackagePriceAsync(billing, stay.Account)).ShouldBe(9_000m, "the package in use until the change is approved");

        // While it waits: other terms are refused; saved again as asked, nothing is asked twice and the name saves at once.
        var waiting = await billing.GetAsync<PackageDetail>($"{PackagesPath}/{package.Id}");
        (waiting.Price, waiting.PendingTerms.ShouldNotBeNull().Asked.Price).ShouldBe((9_000m, 9_900m));
        using (var other = await billing.SendRawAsync(HttpMethod.Put, $"{PackagesPath}/{package.Id}", Package(item, 10_500m, null, waiting)))
        {
            other.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        var renamed = await billing.PutAsync<PackageDetail>($"{PackagesPath}/{package.Id}", Package(item, 9_900m, null, waiting) with { Name = waiting.Name + " (revised)" });
        (renamed.Name, renamed.Price, renamed.PendingTerms.ShouldNotBeNull().ApprovalId).ShouldBe((waiting.Name + " (revised)", 9_000m, asked.Id));

        // Approved by the accountant, the stay is priced at the package approved.
        await ApproveAsAccountantAsync(asked.Id);
        (await PackagePriceAsync(billing, stay.Account)).ShouldBe(9_900m, "a stay's package is priced from the package as it is");
        (await AuditCountAsync("package.terms_changed", package.Id)).ShouldBe(2, "its first terms, and this price");
    }

    [Fact]
    public async Task A_stay_takes_only_a_package_priced_for_its_plan_or_any_plan()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var (item, _) = await PackageServiceAsync(admin);
        var staff = await server.SaveTariffPlanAsync(admin, new SaveTariffPlanRequest(
            "PK" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), $"Staff plan {Guid.NewGuid().ToString("N")[..4]}", TariffType.Staff, null, null, 0m, false, true, null, null, null));
        var forStaff = await server.SavePackageAsync(admin, Package(item, 7_500m, staff.Id));
        var forAny = await server.SavePackageAsync(admin, Package(item, 9_000m, null));
        await using var stay = await AdmitAsync();

        // The stay is billed on the General plan: a package approved for the staff plan does not price it.
        var account = await billing.GetAsync<IpAccountDetail>($"{AccountsPath}/{stay.Account.Id}");
        account.TariffPlanId.ShouldNotBe(staff.Id);
        using (var refused = await billing.SendRawAsync(HttpMethod.Put, $"{AccountsPath}/{account.Id}/package", new AssignPackageRequest(forStaff.Id, account.Version)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("is priced for another tariff plan than this stay's");
        }

        await AssignAsync(billing, account, forAny.Id);
        (await PackagePriceAsync(billing, account)).ShouldBe(9_000m);
    }

    [Fact]
    public async Task Taking_a_package_out_of_use_and_its_covers_notes_are_saved_at_once_and_putting_it_back_waits()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var (item, _) = await PackageServiceAsync(admin);
        var package = await server.SavePackageAsync(admin, Package(item, 9_000m, null));

        // The covers' notes say why, and decide nothing: saved at once.
        const string notes = "Three bed days, any ward";
        var noted = await admin.PutAsync<PackageDetail>($"{PackagesPath}/{package.Id}", Package(item, 9_000m, null, package, notes));
        (noted.PendingTerms, noted.Inclusions.Single(i => i.Category == ServiceCategory.BedCharge).Notes).ShouldBe(((PendingPackageTerms?)null, notes));

        // Out of use at once: no stay or bill may choose it again.
        var stopped = await admin.PutAsync<PackageDetail>($"{PackagesPath}/{package.Id}", Package(item, 9_000m, null, noted, notes) with { IsActive = false });
        (stopped.IsActive, stopped.Price, stopped.PendingTerms).ShouldBe((false, 9_000m, (PendingPackageTerms?)null));
        (await admin.GetAsync<List<PackageListItem>>(PackagesPath)).ShouldNotContain(p => p.Id == package.Id);

        // Back in use waits for a second person.
        using (var back = await admin.SendRawAsync(HttpMethod.Put, $"{PackagesPath}/{package.Id}", Package(item, 9_000m, null, stopped, notes)))
        {
            back.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        (await admin.GetAsync<PackageDetail>($"{PackagesPath}/{package.Id}")).IsActive.ShouldBeFalse();
        (await AuditCountAsync("package.terms_changed", package.Id)).ShouldBe(1, "only its first terms were approved");
    }

    [Fact]
    public async Task An_approval_never_overwrites_a_package_changed_after_it_was_asked_for()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var (item, _) = await PackageServiceAsync(admin);
        var package = await server.SavePackageAsync(admin, Package(item, 9_000m, null));
        var asked = await admin.PutAsync<ApprovalRequestDto>($"{PackagesPath}/{package.Id}", Package(item, 9_900m, null, package));

        // Its price changed by another way in after the change was asked for: approving is refused, and it stays as it is.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.Set<Package>().SingleAsync(p => p.Id == package.Id, Ct)).Price = 9_500m;
            await db.SaveChangesAsync(Ct);
        }

        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var request = await accountant.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}");
        using (var refused = await accountant.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, request.Version)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain(TariffApprovals.ChangedCode);
        }

        (await admin.GetAsync<PackageDetail>($"{PackagesPath}/{package.Id}")).Price.ShouldBe(9_500m);
    }

    // ---------------------------------------------------------------------- helpers

    /// <summary>A package for stays of three days covering the bed days and up to ₹2,000 of consumables.</summary>
    private static SavePackageRequest Package(Guid item, decimal price, Guid? planId, PackageDetail? package = null, string bedNotes = BedNotes) => new(
        package?.Code ?? "PKG-" + Letters(8).ToUpperInvariant(), package?.Name ?? "Cellulitis care (3 days)", item, price, 3, planId, PackageUse.InPatient, null,
        IsActive: true,
        [
            new PackageInclusionInput(null, ServiceCategory.BedCharge, 3m, null, bedNotes),
            new PackageInclusionInput(null, ServiceCategory.Consumable, null, 2_000m, "Dressings and drugs"),
        ],
        package?.Version);

    /// <summary>The catalogue service a package is billed as (category Package), in use: its id and code.</summary>
    private async Task<(Guid Id, string Code)> PackageServiceAsync(ApiClient admin)
    {
        var service = await server.CreateServiceAsync(admin, new SaveServiceItemRequest(
            "SV-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), "Cellulitis care package", null, ServiceCategory.Package, null, 9_000m, 0m, null,
            IsDiscountAllowed: false, IsPriceEditable: false, Description: null, IsActive: true, Version: null));
        return (service.Id, service.Code);
    }

    /// <summary>The billing desk puts the stay on the package.</summary>
    private static async Task AssignAsync(ApiClient billing, IpAccountDetail account, Guid packageId)
    {
        var current = await billing.GetAsync<IpAccountDetail>($"{AccountsPath}/{account.Id}");
        using var response = await billing.SendRawAsync(HttpMethod.Put, $"{AccountsPath}/{account.Id}/package", new AssignPackageRequest(packageId, current.Version));
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>The package price billing weighs the stay's services against (its package against actuals).</summary>
    private static async Task<decimal> PackagePriceAsync(ApiClient billing, IpAccountDetail account) =>
        (await billing.GetAsync<PackageStatus>($"{AccountsPath}/{account.Id}/package-status")).PackagePrice;

    private async Task ApproveAsAccountantAsync(Guid approvalId)
    {
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var request = await accountant.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}");
        request.CanDecide.ShouldBeTrue();
        (await accountant.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}/approve", new ApproveRequest("Agreed.", request.Version)))
            .Status.ShouldBe(ApprovalStatus.Approved);
    }

    private async Task<int> AuditCountAsync(string action, Guid entityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var id = entityId.ToString();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().CountAsync(e => e.Action == action && e.EntityId == id, Ct);
    }

    private static async Task<Guid> RegisterAsync(ApiClient frontOffice) =>
        (await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Ravi"), MiddleName: null, LastName: "Package" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10))), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null,
            AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false))).Id;

    /// <summary>A new patient admitted, paying for himself, to a bed of the male general ward of the test's own.</summary>
    private async Task<Stay> AdmitAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var patient = await RegisterAsync(frontOffice);
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "GWM");
        var bed = await TestBeds.AddAsync(server, ward.Id);
        var admission = await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patient, BedId: bed.BedId, Type: AdmissionType.Elective, Source: AdmissionSource.OutPatient, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Cellulitis of the right leg", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
        return new Stay(server, frontOffice, await billing.GetAsync<IpAccountDetail>($"{AccountsPath}/by-admission/{admission.Id}"), ward.Id);
    }

    /// <summary>
    /// The patient's stay, with his IP account. When the test ends the stay is cancelled and the nurse cleans the bed it
    /// left, as the shared ward needs.
    /// </summary>
    private sealed class Stay(HospitalServer server, ApiClient frontOffice, IpAccountDetail account, Guid wardId) : IAsyncDisposable
    {
        public IpAccountDetail Account => account;

        public async ValueTask DisposeAsync()
        {
            (await frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/inpatient/admissions/{account.AdmissionId}/cancel",
                new CancelAdmissionRequest("Admitted for a test of package approvals"))).Dispose();
            var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
            foreach (var task in (await nurse.GetAsync<List<HousekeepingTaskDto>>($"/api/v1/beds/housekeeping?wardId={wardId}"))
                     .Where(t => t.AdmissionId == account.AdmissionId && t.Status == HousekeepingStatus.Pending))
            {
                await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/start");
                await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/complete", new BedNoteRequest("Cleaned after the stay"));
            }
        }
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
