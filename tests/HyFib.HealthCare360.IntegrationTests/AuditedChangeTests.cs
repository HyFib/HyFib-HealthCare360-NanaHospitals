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
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Physiotherapy;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// What the audit trail keeps of a change, read back as an auditor reads it (Administration › Audit log): who changed what,
/// with the values before and after, in the transaction that made the change. Each of these changes left no such trail:
/// the roles and facilities an account was given, the permissions an upgrade gave a role, the codes of a coding done
/// again, the records a merge moved, the bed multipliers, package inclusions, template medicines and pay heads saved with
/// their record, the batches a clean-up removed before it stopped, an ABDM erase, and the value a removed setting had.
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
    public async Task A_merge_names_the_specialty_care_records_it_moved_and_the_bookings_it_cancelled()
    {
        var facility = (await (await server.SignInAsync(HospitalServer.Users.FrontOffice)).GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId!.Value;
        var (duplicate, kept) = (await RegisterAsync("Duplicate"), await RegisterAsync("Kept"));
        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30 + Random.Shared.Next(300));
        Guid duplicateProfile, clash, other, referral;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            // Both records are enrolled in the dialysis unit and booked for the same morning; the duplicate has a second
            // booking and a physiotherapy referral of its own.
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var profiles = new[] { Profile(facility, duplicate, day), Profile(facility, kept, day) };
            var sessions = new[] { Session(facility, duplicate, day), Session(facility, kept, day), Session(facility, duplicate, day.AddDays(2)) };
            var physio = new PhysioReferral
            {
                FacilityId = facility,
                PatientId = duplicate,
                Reason = "Frozen shoulder",
                ReferredAt = DateTimeOffset.UtcNow,
                Status = PhysioReferralStatus.New,
                Priority = OrderPriority.Routine,
            };
            db.AddRange(profiles);
            db.AddRange(sessions);
            db.Add(physio);
            await db.SaveChangesAsync(Ct);
            (duplicateProfile, clash, other, referral) = (profiles[0].Id, sessions[0].Id, sessions[2].Id, physio.Id);
        }

        // A second records officer approves the merge (maker-checker): the rows move in the approval's transaction.
        await MakerChecker.MergeAsync(
            await server.SignInAsync(HospitalServer.Users.MrdOfficer), await server.SignInAsync(HospitalServer.Users.MrdVerifier), duplicate, kept, "Registered twice");

        var moved = (await TrailAsync("Patient", duplicate, PatientMergeHandler.RecordsMovedAction)).ShouldHaveSingleItem();
        moved.PatientId.ShouldBe(kept);
        moved.Category.ShouldBe(AuditCategories.Data);
        var details = JsonNode.Parse(moved.Details!)!;
        Ids(details["moved"]!["DialysisSession"]!).ShouldBe([clash, other], ignoreOrder: true);
        Ids(details["moved"]!["PhysioReferral"]!).ShouldBe([referral]);
        details["moved"]!.AsObject().ContainsKey("DialysisPatientProfile").ShouldBeFalse("The duplicate's profile stays with it, deactivated.");
        Ids(details["dialysisProfilesDeactivated"]!).ShouldBe([duplicateProfile]);
        Ids(details["dialysisSessionsCancelled"]!).ShouldBe([clash]);

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.Set<DialysisSession>().IgnoreQueryFilters().SingleAsync(s => s.Id == clash, Ct)).Status.ShouldBe(DialysisSessionStatus.Cancelled);
            (await db.Set<PhysioReferral>().IgnoreQueryFilters().SingleAsync(r => r.Id == referral, Ct)).PatientId.ShouldBe(kept);
        }
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

    [Fact]
    public async Task A_clean_up_stopped_part_way_leaves_each_batch_it_removed_in_the_trail_and_none_it_did_not()
    {
        // A thousand and one messages sent long ago: the clean-up removes them a thousand at a time, oldest first.
        var old = DateTimeOffset.UtcNow.AddDays(-400);
        var sent = Enumerable.Range(0, 1_001)
            .Select(_ => new OutboxMessage { Type = "audit.test", Status = OutboxStatus.Succeeded, CreatedAt = old, NextAttemptAt = old, ProcessedAt = old })
            .ToList();
        var ids = sent.Select(m => m.Id).ToList();
        RetentionRun run;
        Guid last;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            db.AddRange(sent);
            await db.SaveChangesAsync(Ct);

            // The second batch fails, as a lost connection or a full disk would stop it: the database refuses to delete the
            // message the clean-up comes to last.
            last = await db.Set<OutboxMessage>().Where(m => ids.Contains(m.Id)).OrderByDescending(m => m.Id).Select(m => m.Id).FirstAsync(Ct);
            var refuse = $"""
                CREATE FUNCTION platform.audit_test_refuse() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'kept by the test'; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER audit_test_refuse BEFORE DELETE ON platform.outbox_messages FOR EACH ROW WHEN (OLD.id = '{last}')
                    EXECUTE FUNCTION platform.audit_test_refuse();
                """;
            await db.Database.ExecuteSqlRawAsync(refuse, Ct);
            try
            {
                run = await scope.ServiceProvider.GetRequiredService<RetentionService>().RunAsync(RetentionRunTrigger.Manual, null, Ct);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync(
                    "DROP TRIGGER audit_test_refuse ON platform.outbox_messages; DROP FUNCTION platform.audit_test_refuse();", Ct);
            }
        }

        run.Error.ShouldNotBeNull().ShouldContain("kept by the test");
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var left = (await db.Set<OutboxMessage>().Where(m => ids.Contains(m.Id)).Select(m => m.Id).ToListAsync(Ct)).ToHashSet();
            var batches = await db.Set<AuditEvent>().AsNoTracking()
                .Where(e => e.Action == RetentionService.PurgedAction && e.EntityId == run.Id.ToString()).ToListAsync(Ct);
            var named = batches.SelectMany(b => Ids(JsonNode.Parse(b.Details!)!["ids"]!)).ToHashSet();

            var removed = ids.Where(id => !left.Contains(id)).ToList();
            removed.ShouldNotBeEmpty("The first batch was removed.");
            left.ShouldContain(last);
            removed.Where(id => !named.Contains(id)).ShouldBeEmpty("Every message removed is named in a batch's audit entry.");
            named.Where(left.Contains).ShouldBeEmpty("A batch that failed is in no audit entry: its deletes were rolled back with it.");
            batches.ShouldAllBe(b => b.Category == AuditCategories.Data);

            await db.Set<OutboxMessage>().Where(m => ids.Contains(m.Id)).ExecuteDeleteAsync(Ct);
        }
    }

    [Fact]
    public async Task A_clean_up_names_each_refresh_token_it_removes_with_its_session()
    {
        // A session revoked long ago that still holds a refresh token not yet expired: the token goes with its session.
        var user = (await (await server.SignInAsync(HospitalServer.Users.FrontOffice)).GetAsync<AuthUser>("/api/v1/auth/me")).Id;
        var old = DateTimeOffset.UtcNow.AddDays(-400);
        var session = new UserSession { UserId = user, CreatedAt = old, LastSeenAt = old, LastActiveAt = old, ExpiresAt = old.AddHours(8), RevokedAt = old.AddHours(1), RevokedReason = "test" };
        var token = new RefreshToken { SessionId = session.Id, TokenHash = "audit-test-" + Guid.NewGuid().ToString("N"), CreatedAt = old, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) };
        RetentionRun run;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            db.AddRange(session, token);
            await db.SaveChangesAsync(Ct);
            run = await scope.ServiceProvider.GetRequiredService<RetentionService>().RunAsync(RetentionRunTrigger.Manual, null, Ct);
        }

        run.Error.ShouldBeNull();
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.Set<UserSession>().IgnoreQueryFilters().AnyAsync(s => s.Id == session.Id, Ct)).ShouldBeFalse();
            (await db.Set<RefreshToken>().IgnoreQueryFilters().AnyAsync(t => t.Id == token.Id, Ct)).ShouldBeFalse();
            var batches = (await db.Set<AuditEvent>().AsNoTracking()
                    .Where(e => e.Action == RetentionService.PurgedAction && e.EntityId == run.Id.ToString()).ToListAsync(Ct))
                .Select(e => JsonNode.Parse(e.Details!)!).ToList();
            List<Guid> Named(string records) =>
                [.. batches.Where(b => b["records"]!.GetValue<string>() == records).SelectMany(b => Ids(b["ids"]!))];
            Named(nameof(RefreshToken)).ShouldContain(token.Id, "The token is named in the entry of the batch that removed it.");
            Named(nameof(UserSession)).ShouldContain(session.Id);
            batches.ShouldAllBe(b => b["removed"]!.GetValue<int>() == b["ids"]!.AsArray().Count, "Each entry counts only the records it names.");
        }
    }

    [Fact]
    public async Task An_abdm_erase_that_fails_part_way_erases_nothing_and_the_one_that_completes_names_the_records()
    {
        var (patient, _, transfer, record) = await ReceivedUnderAPassedConsentAsync();
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();

            // Dropping the request's key fails, after the record is deleted.
            var refuse = $"""
                CREATE FUNCTION interop.audit_test_refuse() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'kept by the test'; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER audit_test_refuse BEFORE UPDATE ON interop.abdm_health_information_requests FOR EACH ROW WHEN (OLD.id = '{transfer}')
                    EXECUTE FUNCTION interop.audit_test_refuse();
                """;
            await db.Database.ExecuteSqlRawAsync(refuse, Ct);
            try
            {
                await Should.ThrowAsync<Exception>(() => scope.ServiceProvider.GetRequiredService<AbdmHealthInformationService>().EraseExpiredAsync(Ct));
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync(
                    "DROP TRIGGER audit_test_refuse ON interop.abdm_health_information_requests; DROP FUNCTION interop.audit_test_refuse();", Ct);
            }
        }

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.Set<AbdmReceivedRecord>().IgnoreQueryFilters().AnyAsync(r => r.Id == record, Ct)).ShouldBeTrue("The failed erase was rolled back whole.");
            (await Erased(db)).ShouldBeEmpty();

            await scope.ServiceProvider.GetRequiredService<AbdmHealthInformationService>().EraseExpiredAsync(Ct);

            (await db.Set<AbdmReceivedRecord>().IgnoreQueryFilters().AnyAsync(r => r.Id == record, Ct)).ShouldBeFalse();
            (await db.Set<AbdmHealthInformationRequest>().AsNoTracking().SingleAsync(q => q.Id == transfer, Ct)).PrivateKeyCipher.ShouldBeNull();
            Ids(JsonNode.Parse((await Erased(db)).ShouldHaveSingleItem().Details!)!["records"]!).ShouldBe([record]);
        }

        Task<List<AuditEvent>> Erased(HospitalDbContext db) =>
            db.Set<AuditEvent>().AsNoTracking().Where(a => a.PatientId == patient && a.Action == AbdmHealthInformationService.Erased).ToListAsync(Ct);
    }

    [Fact]
    public async Task A_page_kept_while_an_erase_waits_for_it_is_erased_and_named_with_the_rest()
    {
        var (patient, artefact, transfer, first) = await ReceivedUnderAPassedConsentAsync();

        // A page being kept: the receipt holds the transfer's lock (AbdmHealthInformationService.ReceiveAsync) and has
        // written its record, not yet committed.
        await using var receipt = server.Services.CreateAsyncScope();
        var receiving = receipt.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await using var keeping = await receiving.Database.BeginTransactionAsync(Ct);
        await receiving.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({AbdmHealthInformationService.TransferLock(artefact)}, 0))", Ct);
        var late = new AbdmReceivedRecord { RequestEntityId = transfer, PatientId = patient, CareContextReference = "visit/2", PageNumber = 2, Content = "{}" };
        receiving.Add(late);
        await receiving.SaveChangesAsync(Ct);

        // The erase starts meanwhile: it waits for the page, then erases it with the record before it.
        await using var eraser = server.Services.CreateAsyncScope();
        var erase = eraser.ServiceProvider.GetRequiredService<AbdmHealthInformationService>().EraseExpiredAsync(Ct);
        (await Task.WhenAny(erase, Task.Delay(TimeSpan.FromSeconds(2), Ct))).ShouldNotBe(erase, "The erase waits for the page being kept.");
        await keeping.CommitAsync(Ct);
        await erase;

        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        (await db.Set<AbdmReceivedRecord>().IgnoreQueryFilters().AnyAsync(r => r.RequestEntityId == transfer, Ct)).ShouldBeFalse("No record of the transfer outlives the erase.");
        var erased = await db.Set<AuditEvent>().AsNoTracking().Where(a => a.PatientId == patient && a.Action == AbdmHealthInformationService.Erased).ToListAsync(Ct);
        Ids(JsonNode.Parse(erased.ShouldHaveSingleItem().Details!)!["records"]!).ShouldBe([first, late.Id], ignoreOrder: true);
    }

    /// <summary>A patient with a record received through ABDM under a consent whose erase date has passed.</summary>
    private async Task<(Guid Patient, Guid Artefact, Guid Transfer, Guid Record)> ReceivedUnderAPassedConsentAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = (await frontOffice.PostAsync<PatientCreated>("/api/v1/patients",
            AbdmExportTests.Registration(AbdmExportTests.NewAbha(), "erase." + AbdmExportTests.Letters(8) + "@sbx"))).Id;
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var eraseAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var request = new AbdmConsentRequest
        {
            PatientId = patient,
            AbhaAddress = "erase@sbx",
            GatewayRequestId = Guid.NewGuid(),
            Status = AbdmConsentRequestStatus.Granted,
            Purpose = "CAREMGT",
            HiTypes = "OPConsultation",
            RequesterName = "Dr. Test",
            DateFrom = DateTimeOffset.UtcNow.AddYears(-1),
            DateTo = DateTimeOffset.UtcNow,
            DataEraseAt = eraseAt,
        };
        var artefact = new AbdmConsentArtefact
        {
            ConsentRequestEntityId = request.Id,
            PatientId = patient,
            ArtefactId = Guid.NewGuid().ToString(),
            Status = "GRANTED",
            DataEraseAt = eraseAt,
        };
        var information = new AbdmHealthInformationRequest
        {
            ArtefactEntityId = artefact.Id,
            PatientId = patient,
            GatewayRequestId = Guid.NewGuid(),
            Status = AbdmHealthInformationStatus.Requested,
            PrivateKeyCipher = "v1.key",
            Nonce = "nonce",
            KeyExpiresAt = eraseAt,
        };
        var received = new AbdmReceivedRecord { RequestEntityId = information.Id, PatientId = patient, CareContextReference = "visit/1", PageNumber = 1, Content = "{}" };
        db.AddRange(request, artefact, information, received);
        await db.SaveChangesAsync(Ct);
        return (patient, artefact.Id, information.Id, received.Id);
    }

    [Fact]
    public async Task A_setting_removed_leaves_the_value_it_had_in_the_trail()
    {
        var key = "ui.notice_" + Letters(8);
        Guid id;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            await settings.SetAsync(key, "Ward 5 closed for deep cleaning", null, false, Ct);
            id = (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<SystemSetting>().SingleAsync(s => s.Key == key, Ct)).Id;

            (await settings.RemoveAsync(key, null, Ct)).ShouldBeTrue();
        }

        var removed = Details(await TrailAsync(nameof(SystemSetting), id), "SystemSetting.delete");
        removed["key"]!.GetValue<string>().ShouldBe(key);
        removed["value"]!.GetValue<string>().ShouldBe("\"Ward 5 closed for deep cleaning\"");
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

    private static DialysisPatientProfile Profile(Guid facility, Guid patient, DateOnly enrolled) => new()
    {
        FacilityId = facility,
        PatientId = patient,
        DryWeightKg = 58m,
        Shift = DialysisShift.Morning,
        EnrolledOn = enrolled.AddMonths(-6),
    };

    private static DialysisSession Session(Guid facility, Guid patient, DateOnly day) => new()
    {
        FacilityId = facility,
        SessionNumber = "DS" + Letters(10).ToUpperInvariant(),
        PatientId = patient,
        ScheduledDate = day,
        Shift = DialysisShift.Morning,
        Status = DialysisSessionStatus.Scheduled,
    };

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
