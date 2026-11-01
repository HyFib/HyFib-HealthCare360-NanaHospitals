using System.Globalization;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Packages;
using HyFib.HealthCare360.Modules.Billing.Features.Tariffs;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;
using HyFib.HealthCare360.Modules.Clinical.Features.Templates;
using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Features.Employees;
using HyFib.HealthCare360.Modules.HR.Features.Payroll;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// What the audit trail keeps of a change, read back as an auditor reads it (Administration › Audit log): who changed what,
/// with the values before and after, in the transaction that made the change. Each of these changes left no such trail:
/// the roles and facilities an account was given, the permissions an upgrade gave a role, the codes of a coding done
/// again, and the bed multipliers, package inclusions, template medicines and pay heads saved with their record.
/// </summary>
public sealed class AuditedChangeTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_roles_and_facilities_an_account_is_given_and_loses_are_in_the_trail_with_the_change()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var roles = await admin.GetAsync<List<RoleListItem>>("/api/v1/admin/roles");
        var (frontOffice, nurse) = (roles.Single(r => r.Name == SystemRoles.FrontOffice), roles.Single(r => r.Name == SystemRoles.Nurse));
        var main = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId ?? throw new InvalidOperationException("No facility.");
        var annexeName = "Nana Hospitals Annexe " + Letters(4);
        var annexe = (await admin.PostAsync<Created>("/api/v1/masters/facilities", new SaveFacilityRequest(
            Letters(6).ToUpperInvariant(), annexeName, null, new Address { City = "Chennai", PinCode = "600002" },
            null, null, null, null, null, null, null, null, null, null, IsActive: false, null))).Id;

        // A front office clerk moves to the wards, and covers the annexe too.
        var account = await admin.PostAsync<TemporaryPasswordResponse>("/api/v1/admin/users", new CreateUserRequest(
            "it-ward-" + Letters(6), "Saranya", Email: null, Phone: null, [frontOffice.Id], [main], main, PractitionerId: null, TemporaryPassword: null));
        var user = await admin.GetAsync<UserDetail>($"/api/v1/admin/users/{account.UserId}");
        await admin.PutAsync($"/api/v1/admin/users/{user.Id}", new UpdateUserRequest(
            user.DisplayName, user.Email, user.Phone, [nurse.Id], [main, annexe], main, null, IsActive: true, user.Version));

        var trail = await TrailAsync(nameof(User), user.Id);

        var created = Details(trail, "user.created");
        Names(created["roles"]!).ShouldBe([SystemRoles.FrontOffice]);
        Ids(created["facilities"]!).ShouldBe([main]);
        var updated = Details(trail, "user.updated");
        Names(updated["roles"]!["granted"]!).ShouldBe([SystemRoles.Nurse]);
        Names(updated["roles"]!["withdrawn"]!).ShouldBe([SystemRoles.FrontOffice]);
        Names(updated["facilities"]!["granted"]!).ShouldBe([annexeName]);
        updated["facilities"]!["withdrawn"]!.AsArray().ShouldBeEmpty();

        // The account's own data events hold the grants too, whichever way they are made.
        Rows(Details(trail, "User.create")["roles"]!, "roleId").ShouldBe([frontOffice.Id]);
        var change = Details(trail, "User.update");
        Rows(change["roles"]!["from"]!, "roleId").ShouldBe([frontOffice.Id]);
        Rows(change["roles"]!["to"]!, "roleId").ShouldBe([nurse.Id]);
        Rows(change["facilities"]!["from"]!, "facilityId").ShouldBe([main]);
        Rows(change["facilities"]!["to"]!, "facilityId").ShouldBe([main, annexe], ignoreOrder: true);
    }

    [Fact]
    public async Task The_default_permissions_an_upgrade_gives_a_role_or_takes_back_are_in_the_trail()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<PermissionRegistry>();
        var cssd = await db.Set<Role>().Include(r => r.Permissions).SingleAsync(r => r.Name == SystemRoles.CssdTechnician, Ct);

        // As a database the version before seeded: one of the CSSD technician's defaults is new in this version, and a
        // default the version before gave the role (patient search) is no longer one.
        var newDefault = registry.All.Where(d => d.DefaultRoles?.Contains(SystemRoles.CssdTechnician) == true).Select(d => d.Name).Order(StringComparer.Ordinal).First();
        db.RemoveRange(cssd.Permissions.Where(p => p.Permission == newDefault).ToList());
        db.RemoveRange(await db.Set<SeededGrant>().Where(g => g.RoleName == cssd.Name && g.Permission == newDefault).ToListAsync(Ct));
        cssd.Permissions.Add(new RolePermission { RoleId = cssd.Id, Permission = PatientsRead });
        db.Add(new SeededGrant { RoleName = cssd.Name, Permission = PatientsRead, SeededAt = DateTimeOffset.UtcNow.AddYears(-1) });
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();

        // The upgrade's start.
        await scope.ServiceProvider.GetServices<ISeeder>().OfType<IdentitySeeder>().Single().SeedAsync(db, Ct);

        var entry = (await TrailAsync(nameof(Role), cssd.Id)).First(e => e.Action == "role.permissions_changed");
        entry.UserId.ShouldBeNull("Start-up made the change, not a person.");
        entry.Severity.ShouldBe(AuditSeverity.Warning);
        var details = JsonNode.Parse(entry.Details!)!;
        Strings(details["added"]!).ShouldBe([newDefault]);
        Strings(details["removed"]!).ShouldBe([PatientsRead]);
    }

    [Fact]
    public async Task Coding_an_episode_again_keeps_the_codes_it_had_in_the_trail()
    {
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var patient = await RegisterAsync("Coding");
        var coding = await mrd.PostAsync<CodingDto>("/api/v1/mrd/codings",
            new CreateCodingRequest(patient, EncounterId: null, AdmissionId: Guid.CreateVersion7(), VisitId: null, null, Notes: null));
        coding = await mrd.PutAsync<CodingDto>($"/api/v1/mrd/codings/{coding.Id}", new SaveCodingRequest(
            [new CodingLineRequest(CodingEntryKind.Diagnosis, "A90", IsPrimary: true)], null, coding.Version));

        // The discharge summary said malaria, not dengue: MRD codes the episode again.
        await mrd.PutAsync<CodingDto>($"/api/v1/mrd/codings/{coding.Id}", new SaveCodingRequest(
            [new CodingLineRequest(CodingEntryKind.Diagnosis, "B50.9", IsPrimary: true), new CodingLineRequest(CodingEntryKind.Diagnosis, "I10", IsPrimary: false)],
            null, coding.Version));

        var recoded = (await TrailAsync(nameof(EncounterCoding), coding.Id)).First(e => e.Action == "EncounterCoding.update");
        recoded.PatientId.ShouldBe(patient);
        var entries = JsonNode.Parse(recoded.Details!)!["entries"]!;
        Strings(entries["from"]!, "code").ShouldBe(["A90"]);
        Strings(entries["to"]!, "code").ShouldBe(["B50.9", "I10"], ignoreOrder: true);
        entries["to"]!.AsArray().Single(e => e!["code"]!.GetValue<string>() == "B50.9")!["isPrimary"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task A_tariff_plans_bed_multipliers_are_in_the_trail_as_they_were_and_as_they_are()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var privateRoom = await ServiceAsync(admin, "Private room", ServiceCategory.BedCharge, 3_000m);
        var icu = await ServiceAsync(admin, "ICU bed", ServiceCategory.BedCharge, 8_000m);
        var code = "AT" + Letters(6).ToUpperInvariant();
        var plan = await admin.PostAsync<TariffPlanDetail>("/api/v1/billing/tariffs", Plan(code, null, new BedMultiplierInput(privateRoom, 1.5m)));

        // The insurer's new contract: private rooms at twice the rate, and the ICU at two and a half.
        plan = await admin.PutAsync<TariffPlanDetail>($"/api/v1/billing/tariffs/{plan.Id}",
            Plan(code, plan.Version, new BedMultiplierInput(privateRoom, 2m), new BedMultiplierInput(icu, 2.5m)));

        // Saved again from the screen, multipliers untouched.
        await admin.PutAsync<TariffPlanDetail>($"/api/v1/billing/tariffs/{plan.Id}",
            Plan(code, plan.Version, new BedMultiplierInput(icu, 2.5m), new BedMultiplierInput(privateRoom, 2m)) with { Description = "2027 contract" });

        var trail = await TrailAsync(nameof(TariffPlan), plan.Id);
        Multipliers(Details(trail, "TariffPlan.create")["bedMultipliers"]!).ShouldBe([(privateRoom, 1.5m)]);
        var updates = trail.Where(e => e.Action == "TariffPlan.update").Select(e => JsonNode.Parse(e.Details!)!).ToList();
        updates.Count.ShouldBe(2);
        var changed = updates[1]["bedMultipliers"]!;
        Multipliers(changed["from"]!).ShouldBe([(privateRoom, 1.5m)]);
        Multipliers(changed["to"]!).ShouldBe([(privateRoom, 2m), (icu, 2.5m)], ignoreOrder: true);
        updates[0].AsObject().ContainsKey("bedMultipliers").ShouldBeFalse("The same multipliers saved again are no change.");
        updates[0]["description"]!["to"]!.GetValue<string>().ShouldBe("2027 contract");
    }

    [Fact]
    public async Task A_packages_inclusions_are_in_the_trail_as_they_were_and_as_they_are()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var item = await ServiceAsync(admin, "Cataract package", ServiceCategory.Package, 18_000m);
        var lens = await ServiceAsync(admin, "Foldable IOL", ServiceCategory.Consumable, 6_000m);
        var package = await admin.PostAsync<PackageDetail>("/api/v1/billing/packages", new SavePackageRequest(
            $"PKG-{Letters(8)}", "Cataract (per eye)", item, 18_000m, 1, null, PackageUse.Both, null, true,
            [new PackageInclusionInput(null, ServiceCategory.Consumable, null, 4_000m, "Drugs and consumables")], null));

        // The lens is covered by name now, with the consumables cap raised.
        await admin.PutAsync<PackageDetail>($"/api/v1/billing/packages/{package.Id}", new SavePackageRequest(
            package.Code, package.Name, item, 18_000m, 1, null, PackageUse.Both, null, true,
            [new PackageInclusionInput(lens, null, 1m, null, "One lens"), new PackageInclusionInput(null, ServiceCategory.Consumable, null, 4_500m, "Drugs and consumables")],
            package.Version));

        var inclusions = Details(await TrailAsync(nameof(Package), package.Id), "Package.update")["inclusions"]!;
        Caps(inclusions["from"]!).ShouldBe([(null, 4_000m)]);
        Caps(inclusions["to"]!).ShouldBe([(lens, null), (null, 4_500m)], ignoreOrder: true);
    }

    [Fact]
    public async Task A_templates_medicines_are_in_the_trail_as_they_were_and_as_they_are()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var code = "FEVER-" + Letters(6).ToUpperInvariant();
        var template = await admin.PostAsync<TemplateDto>("/api/v1/emr/templates",
            Template(code, null, Medicine("Paracetamol", "650 mg", DoseFrequency.TDS, 3)));

        // Paediatrics asks for the lower dose, with a cough syrup added.
        await admin.PutAsync<TemplateDto>($"/api/v1/emr/templates/{template.Id}",
            Template(code, template.Version, Medicine("Paracetamol", "500 mg", DoseFrequency.TDS, 3), Medicine("Ambroxol syrup", "5 ml", DoseFrequency.BD, 5)));

        var medications = Details(await TrailAsync(nameof(ClinicalTemplate), template.Id), "ClinicalTemplate.update")["medications"]!;
        Doses(medications["from"]!).ShouldBe([("Paracetamol", "650 mg")]);
        Doses(medications["to"]!).ShouldBe([("Paracetamol", "500 mg"), ("Ambroxol syrup", "5 ml")], ignoreOrder: true);
    }

    [Fact]
    public async Task A_salary_structures_pay_heads_are_in_the_trail()
    {
        var hr = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var components = (await hr.GetAsync<List<SalaryComponentDto>>("/api/v1/hr/payroll/components")).ToDictionary(c => c.Code);
        var employee = await hr.PostAsync<EmployeeCreated>("/api/v1/hr/employees/", Employee());
        var structure = await hr.PostAsync<Created>($"/api/v1/hr/employees/{employee.Id}/salary-structures", new SaveSalaryStructureRequest(
            new DateOnly(2026, 4, 1), null, 0, PfApplicable: true, PfOnActualBasic: false, EsiApplicable: false, null, null,
            [new SalaryStructureLineInput(components["BASIC"].Id, 18_000), new SalaryStructureLineInput(components["HRA"].Id, 40)]));

        var lines = Details(await TrailAsync(nameof(EmployeeSalaryStructure), structure.Id), "EmployeeSalaryStructure.create")["lines"]!;

        lines.AsArray().Select(l => (l!["salaryComponentId"]!.GetValue<Guid>(), l["value"]!.GetValue<decimal>()))
            .ShouldBe([(components["BASIC"].Id, 18_000m), (components["HRA"].Id, 40m)], ignoreOrder: true);
    }

    private const string PatientsRead = "patients.read";

    /// <summary>The audit trail of one record, newest first, as Administration › Audit log shows it.</summary>
    private async Task<List<AuditEventDto>> TrailAsync(string entityType, Guid entityId, string? action = null)
    {
        var auditor = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var path = $"/api/v1/admin/audit?entityType={entityType}&entityId={entityId}&pageSize=200" + (action is null ? "" : $"&action={action}");
        return [.. (await auditor.GetAsync<PagedResult<AuditEventDto>>(path)).Items];
    }

    /// <summary>The details of the newest entry with exactly this action (the filter matches part of an action).</summary>
    private static JsonNode Details(IEnumerable<AuditEventDto> trail, string action) =>
        JsonNode.Parse(trail.First(e => e.Action == action).Details!)!;

    private static List<Guid> Ids(JsonNode list) => [.. list.AsArray().Select(n => n!.GetValueKind() == System.Text.Json.JsonValueKind.Object ? n["id"]!.GetValue<Guid>() : n.GetValue<Guid>())];

    private static List<string?> Names(JsonNode list) => [.. list.AsArray().Select(n => n!["name"]?.GetValue<string>())];

    private static List<Guid> Rows(JsonNode list, string property) => [.. list.AsArray().Select(n => n![property]!.GetValue<Guid>())];

    private static List<string> Strings(JsonNode list, string? property = null) =>
        [.. list.AsArray().Select(n => property is null ? n!.GetValue<string>() : n![property]!.GetValue<string>())];

    private static List<(Guid, decimal)> Multipliers(JsonNode list) =>
        [.. list.AsArray().Select(n => (n!["bedServiceItemId"]!.GetValue<Guid>(), n["multiplier"]!.GetValue<decimal>()))];

    private static List<(Guid?, decimal?)> Caps(JsonNode list) =>
        [.. list.AsArray().Select(n => (n!["serviceItemId"]?.GetValue<Guid>(), n["maxAmount"]?.GetValue<decimal>()))];

    private static List<(string, string)> Doses(JsonNode list) =>
        [.. list.AsArray().Select(n => (n!["drugName"]!.GetValue<string>(), n["dose"]!.GetValue<string>()))];

    private static SaveTariffPlanRequest Plan(string code, uint? version, params BedMultiplierInput[] multipliers) => new(
        code, "Star Health corporate", TariffType.Insurance, null, null, 0m, false, true, null, multipliers, version);

    private static SaveTemplateRequest Template(string code, uint? version, params TemplateMedicationRequest[] medicines) => new(
        code, "Fever, adult", "GM", Personal: false, "Fever", null, null, null, null, "Plenty of fluids", 3, ["Fever"], null, medicines, 0, true, version);

    private static TemplateMedicationRequest Medicine(string name, string dose, DoseFrequency frequency, int days) =>
        new(null, name, null, null, null, "Oral", dose, frequency, null, days, null);

    private static async Task<Guid> ServiceAsync(ApiClient admin, string name, ServiceCategory category, decimal price) =>
        (await admin.PostAsync<ServiceItemDto>("/api/v1/masters/services", new SaveServiceItemRequest(
            $"SV-{Letters(8).ToUpperInvariant()}", name, null, category, null, price, 0m, null, true, false, null, true, null))).Id;

    private async Task<Guid> RegisterAsync(string firstName)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: TestPatients.FirstName(firstName), MiddleName: null, LastName: "Audit" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(9000)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Random.Shared.Next(100_000_000, 999_999_999).ToString(CultureInfo.InvariantCulture), AlternatePhone: null, Email: null,
            Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null,
            EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
        return patient.Id;
    }

    private static SaveEmployeeRequest Employee() => new(
        null, "Revathi", null, "Audit" + Letters(6), Gender.Female, new DateOnly(1994, 3, 2), null, null, null, null, null, null, null,
        EmploymentType.Permanent, EmployeeStatus.Active, new DateOnly(2025, 12, 1), null, null, null, null, null, null, null,
        IsAttendanceExempt: true, SalaryPaymentMode.Cash, null, null, null, null, null, null, null, null);

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private sealed record Created(Guid Id);
}
