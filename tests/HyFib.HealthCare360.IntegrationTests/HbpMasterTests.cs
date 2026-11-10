using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Packages;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// AB PM-JAY's HBP master through the API (INS-PMJAY): the tariff keeper imports NHA's file, a second person who may
/// approve prices puts it in force (maker-checker), the insurance desk looks procedures up, a package is mapped to one
/// (and only to one the master lists), and a later master retires what it no longer lists without breaking the packages
/// mapped to it. The codes are this test's own. Every import of this class is here, one after another: one import waits
/// for approval at a time, and each retires the procedures it does not list.
/// </summary>
public sealed class HbpMasterTests(HospitalServer server)
{
    private const string Imports = "/api/v1/billing/hbp/imports";
    private const string Procedures = "/api/v1/billing/hbp/procedures";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_master_is_imported_looked_up_mapped_to_a_package_and_superseded()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var specialty = Specialty();
        var (kept, dropped) = ($"{specialty}101A", $"{specialty}101B");
        var master = $"""
            Specialty,Specialty Code,Package Name,Procedure Code,Procedure Name,Procedure Price,Reservation for Public Hospitals,Length of Stay
            Test surgery,{specialty},Test package,{kept},Test procedure (band A),"12,500",N,2
            Test surgery,{specialty},Test package,{dropped},Test procedure (band B),18000,Y,4
            Test surgery,{specialty},Test package,XX1,Not a code,1,N,1
            """;

        // Only the tariff keeper imports; front office neither imports nor looks up.
        using (var forbidden = await ImportAsync(frontOffice, master, "HBP 2022"))
        {
            forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var notAllowed = await frontOffice.SendRawAsync(HttpMethod.Get, $"{Procedures}?search={kept}", null))
        {
            notAllowed.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var first = await ImportAndApproveAsync(admin, master, "HBP 2022");
        (first.MasterVersion, first.Read, first.Added, first.Status).ShouldBe(("HBP 2022", 2, 2, HbpImportStatus.Applied));
        first.Problems.ShouldHaveSingleItem().ShouldContain("\"XX1\" is not an HBP procedure code");

        var found = await admin.GetAsync<List<HbpProcedureDto>>($"{Procedures}?search={specialty}101");
        found.Select(p => p.Code).ShouldBe([kept, dropped]);
        var band = found[0];
        (band.Rate, band.LengthOfStayDays, band.PackageCode, band.ReservedForPublic).ShouldBe((12500m, 2, $"{specialty}101", false));
        found[1].ReservedForPublic.ShouldBeTrue();
        (await admin.GetAsync<List<HbpProcedureDto>>($"{Procedures}?search=test%20procedure%20(band%20b)")).Single(p => p.Code.StartsWith(specialty, StringComparison.Ordinal)).Code.ShouldBe(dropped);

        // A package maps to a listed procedure (as typed, in any case), and not to one the master does not list.
        var package = await PackageAsync(admin, kept.ToLowerInvariant());
        (package.HbpCode, package.HbpProcedureName).ShouldBe((kept, "Test procedure (band A)"));
        await PackageRefusedAsync(admin, $"{specialty}999A", "not in the imported HBP master");

        // The next master drops band B: it is retired, still found when asked for, and no longer mapped to anew.
        var second = await ImportAndApproveAsync(admin, master.Replace(dropped, "--", StringComparison.Ordinal), "HBP 2022 (revised)");
        second.Retired.ShouldBeGreaterThanOrEqualTo(1);
        (await admin.GetAsync<List<HbpProcedureDto>>($"{Procedures}?search={specialty}101")).Select(p => p.Code).ShouldBe([kept]);
        var retired = (await admin.GetAsync<List<HbpProcedureDto>>($"{Procedures}?search={specialty}101&includeRetired=true")).Single(p => p.Code == dropped);
        retired.IsActive.ShouldBeFalse();
        await PackageRefusedAsync(admin, dropped, "was retired by HBP master HBP 2022");
        (await admin.GetAsync<HbpProcedureDto>($"{Procedures}/{kept}")).MasterVersion.ShouldBe("HBP 2022 (revised)");
    }

    [Fact]
    public async Task An_import_changes_nothing_until_a_second_person_approves_the_file_it_staged()
    {
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var specialty = Specialty();
        var version = $"HBP 2023 ({specialty})";
        var master = $"""
            Procedure Code,Procedure Name,Procedure Price
            {specialty}201A,Staged procedure A,"40,000"
            {specialty}201B,Staged procedure B,52000
            """;

        // The billing desk stages NHA's file; nothing is in the master yet.
        var asked = await StageAsync(billing, master, version, "NHA circular on HBP 2023");
        var staged = asked.Import;
        (staged.Status, staged.Read, staged.Added, staged.MasterVersion).ShouldBe((HbpImportStatus.Waiting, 2, 2, version));
        staged.FileSha256.ShouldBe(Sha256(master), "the approver matches it to the copy NHA or the State Health Agency sent");
        (asked.Approval.Kind, asked.Approval.Status, asked.Approval.Reason).ShouldBe((HbpService.ApprovalKind, ApprovalStatus.Pending, "NHA circular on HBP 2023"));
        asked.Approval.Details.ShouldContain(d => d.Label == "File SHA-256" && d.Value == Sha256(master));
        asked.Approval.Details.ShouldContain(d => d.Label == "Procedures" && d.Value.StartsWith("2 procedures read: 2 new, ", StringComparison.Ordinal));
        (await billing.GetAsync<List<HbpProcedureDto>>($"{Procedures}?search={specialty}201")).ShouldBeEmpty();
        (await billing.GetAsync<List<HbpImportDto>>(Imports))[0].ShouldSatisfyAllConditions(
            i => i.Id.ShouldBe(staged.Id),
            i => i.Status.ShouldBe(HbpImportStatus.Waiting));

        // One import waits at a time.
        using (var another = await ImportAsync(billing, master.Replace("Staged procedure B", "Another", StringComparison.Ordinal), version))
        {
            another.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await another.Content.ReadAsStringAsync(Ct)).ShouldContain("already waiting for approval");
        }

        // The billing desk may not approve it; the accountant, who approves prices, puts it in force.
        var request = await accountant.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Approval.Id}");
        request.CanDecide.ShouldBeTrue();
        using (var refused = await billing.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{asked.Approval.Id}/approve", new ApproveRequest(null, request.Version)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("approval.not_permitted");
        }

        var approved = await accountant.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Approval.Id}/approve", new ApproveRequest("Matches the circular.", request.Version));
        approved.Status.ShouldBe(ApprovalStatus.Approved);
        approved.Outcome.ShouldNotBeNull().ShouldStartWith($"{version} in force: 2 procedures read: 2 new, 0 changed, ");
        var procedures = await billing.GetAsync<List<HbpProcedureDto>>($"{Procedures}?search={specialty}201");
        procedures.Select(p => (p.Code, p.Rate, p.MasterVersion)).ShouldBe([($"{specialty}201A", 40000m, version), ($"{specialty}201B", 52000m, version)]);

        var history = (await billing.GetAsync<List<HbpImportDto>>(Imports)).Single(i => i.Id == staged.Id);
        (history.Status, history.AskedBy, history.DecidedBy).ShouldBe((HbpImportStatus.Applied, request.RequestedByName, approved.DecidedByName));
        history.DecidedAt.ShouldNotBeNull();
        var audit = await AuditAsync(version);
        audit.ShouldHaveSingleItem().Summary.ShouldNotBeNull().ShouldContain(Sha256(master));

        // The same file again would change nothing: there is nothing to approve.
        using var again = await ImportAsync(billing, master, version);
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await again.Content.ReadAsStringAsync(Ct)).ShouldContain(HbpService.UnchangedCode);
    }

    [Fact]
    public async Task The_approver_is_told_which_packages_are_on_procedures_a_master_retires_reserves_or_rates_otherwise_and_a_refusal_changes_nothing()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var specialty = Specialty();
        var (rated, dropped, reserved) = ($"{specialty}301A", $"{specialty}301B", $"{specialty}301C");
        var master = $"""
            Procedure Code,Procedure Name,Procedure Price,Reservation for Public Hospitals
            {rated},Rated procedure,"12,500",N
            {dropped},Dropped procedure,9000,N
            {reserved},Reserved procedure,15000,N
            """;
        await ImportAndApproveAsync(admin, master, "HBP 2022");
        var onRated = await PackageAsync(admin, rated);
        var onDropped = await PackageAsync(admin, dropped);
        var onReserved = await PackageAsync(admin, reserved);

        var next = $"""
            Procedure Code,Procedure Name,Procedure Price,Reservation for Public Hospitals
            {rated},Rated procedure,"13,000",N
            {reserved},Reserved procedure,15000,Y
            """;
        var asked = await StageAsync(billing, next, "HBP 2022 (revised)", null);
        var details = asked.Approval.Details;
        details.ShouldContain(d => d.Label == "Packages on procedures it retires" && d.Value.Contains($"{onDropped.Code} PM-JAY test package ({dropped})", StringComparison.Ordinal));
        details.ShouldContain(d => d.Label == "Packages on procedures it reserves for public hospitals" && d.Value.Contains($"{onReserved.Code} PM-JAY test package ({reserved})", StringComparison.Ordinal));
        details.ShouldContain(d => d.Label == "Packages whose scheme rate changes" && d.Value.Contains($"{onRated.Code} PM-JAY test package ({rated} ₹12,500.00 → ₹13,000.00)", StringComparison.Ordinal));
        details.ShouldContain(d => d.Label == "Reserved for public hospitals" && d.Value == "1 procedure(s) newly reserved");
        asked.Approval.Reason.ShouldBe("No reason given.");

        // Refused: the master is as it was, and the import shows why.
        var request = await accountant.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Approval.Id}");
        (await accountant.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Approval.Id}/reject", new RejectApprovalRequest("Wait for the state's tier rates.", request.Version)))
            .Status.ShouldBe(ApprovalStatus.Rejected);
        var procedures = await admin.GetAsync<List<HbpProcedureDto>>($"{Procedures}?search={specialty}301");
        procedures.Select(p => (p.Code, p.Rate, p.ReservedForPublic)).ShouldBe([(rated, 12500m, false), (dropped, 9000m, false), (reserved, 15000m, false)]);
        var history = (await admin.GetAsync<List<HbpImportDto>>(Imports)).Single(i => i.Id == asked.Import.Id);
        (history.Status, history.Note).ShouldBe((HbpImportStatus.Rejected, "Wait for the state's tier rates."));
    }

    [Fact]
    public async Task An_import_is_applied_only_over_the_master_it_was_read_against_and_only_as_it_was_staged()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var specialty = Specialty();
        var (inUse, staged) = ($"{specialty}401A", $"{specialty}402A");
        await ImportAndApproveAsync(admin, $"Procedure Code,Procedure Name,Procedure Price\n{inUse},In use,1000\n", "HBP 2022");
        var master = $"Procedure Code,Procedure Name,Procedure Price\n{inUse},In use,1000\n{staged},Staged,2000\n";

        // The master changes after the import is staged (another way than an import): the approval does not apply it.
        var asked = await StageAsync(billing, master, "HBP 2023", null);
        await ChangeAsync(db => db.Set<HbpProcedure>().Where(p => p.Code == inUse).ExecuteUpdateAsync(p => p.SetProperty(x => x.Rate, 1100m), Ct));
        using (var changed = await ApproveAsync(accountant, asked.Approval.Id))
        {
            changed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await changed.Content.ReadAsStringAsync(Ct)).ShouldContain(HbpService.MasterChangedCode);
        }

        (await admin.GetAsync<List<HbpProcedureDto>>($"{Procedures}?search={staged}")).ShouldBeEmpty();
        var notApplied = (await admin.GetAsync<List<HbpImportDto>>(Imports)).Single(i => i.Id == asked.Import.Id);
        notApplied.Status.ShouldBe(HbpImportStatus.NotApplied);
        notApplied.Note.ShouldNotBeNull().ShouldContain("changed after this import was asked for");

        // The rows staged are changed after they were asked for: the approval does not apply them either.
        var again = await StageAsync(billing, master, "HBP 2023", null);
        await ChangeAsync(db => db.Set<HbpMasterImport>().Where(i => i.Id == again.Import.Id)
            .ExecuteUpdateAsync(i => i.SetProperty(x => x.Rows, x => x.Rows.Replace("2000", "9000")), Ct));
        using (var altered = await ApproveAsync(accountant, again.Approval.Id))
        {
            altered.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await altered.Content.ReadAsStringAsync(Ct)).ShouldContain(HbpService.AlteredCode);
        }

        (await admin.GetAsync<List<HbpProcedureDto>>($"{Procedures}?search={staged}")).ShouldBeEmpty();

        // Withdrawn by the person who asked: nothing changes, and the history says so.
        var third = await StageAsync(billing, master, "HBP 2023", null);
        var mine = await billing.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{third.Approval.Id}");
        (await billing.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{third.Approval.Id}/withdraw", new WithdrawApprovalRequest(mine.Version)))
            .Status.ShouldBe(ApprovalStatus.Withdrawn);
        (await admin.GetAsync<List<HbpImportDto>>(Imports)).Single(i => i.Id == third.Import.Id).Status.ShouldBe(HbpImportStatus.Withdrawn);
        (await admin.GetAsync<List<HbpProcedureDto>>($"{Procedures}?search={staged}")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_file_that_is_not_a_master_is_refused()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);

        using var notMaster = await ImportAsync(admin, "Name,Amount\nCataract,5000\n", "HBP 2022");
        notMaster.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await notMaster.Content.ReadAsStringAsync(Ct)).ShouldContain("Procedure Code");

        using var noVersion = await ImportAsync(admin, "Procedure Code,Procedure Name\nBM001A,Burns\n", " ");
        noVersion.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await noVersion.Content.ReadAsStringAsync(Ct)).ShouldContain("version");
    }

    /// <summary>Stages a master and has the other administrator approve it: the import as it is then.</summary>
    private async Task<HbpImportDto> ImportAndApproveAsync(ApiClient maker, string csv, string version)
    {
        var asked = await StageAsync(maker, csv, version, null);
        await server.ApprovePriceAsync(asked.Approval.Id);
        return (await maker.GetAsync<List<HbpImportDto>>(Imports)).Single(i => i.Id == asked.Import.Id);
    }

    private static async Task<HbpImportAsked> StageAsync(ApiClient maker, string csv, string version, string? reason)
    {
        using var response = await ImportAsync(maker, csv, version, reason);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<HbpImportAsked>(ApiClient.Json, Ct))!;
    }

    private static Task<HttpResponseMessage> ImportAsync(ApiClient client, string csv, string version, string? reason = null)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(content, "file", "hbp.csv");
        form.Add(new StringContent(version), "version");
        if (reason is not null)
        {
            form.Add(new StringContent(reason), "reason");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, Imports) { Content = form };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.Http.SendAsync(request, Ct);
    }

    private static async Task<HttpResponseMessage> ApproveAsync(ApiClient checker, Guid approvalId)
    {
        var request = await checker.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}");
        return await checker.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{approvalId}/approve", new ApproveRequest(null, request.Version));
    }

    private async Task ChangeAsync(Func<HospitalDbContext, Task<int>> change)
    {
        await using var scope = server.Services.CreateAsyncScope();
        (await change(scope.ServiceProvider.GetRequiredService<HospitalDbContext>())).ShouldBe(1);
    }

    private async Task<List<AuditEvent>> AuditAsync(string version)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>()
            .Where(e => e.Action == HbpService.Imported && e.EntityId == version).ToListAsync(Ct);
    }

    private async Task<PackageDetail> PackageAsync(ApiClient admin, string? hbpCode) =>
        await server.SavePackageAsync(admin, await PackageRequestAsync(admin, hbpCode));

    private async Task PackageRefusedAsync(ApiClient admin, string hbpCode, string reason)
    {
        using var response = await admin.SendRawAsync(HttpMethod.Post, "/api/v1/billing/packages", await PackageRequestAsync(admin, hbpCode));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadAsStringAsync(Ct);
        problem.ShouldContain("hbpCode");
        problem.ShouldContain(reason);
    }

    private async Task<SavePackageRequest> PackageRequestAsync(ApiClient admin, string? hbpCode)
    {
        var code = $"SV-{Letters(8)}";
        var item = await server.CreateServiceAsync(admin, new SaveServiceItemRequest(
            code, "PM-JAY test package", null, ServiceCategory.Package, null, 12500m, 0m, null, true, false, null, true, null));
        return new SavePackageRequest($"PKG-{Letters(8)}", "PM-JAY test package", item.Id, 12500m, 2, null, PackageUse.InPatient, null, true, [], null, hbpCode);
    }

    private static string Sha256(string csv) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(csv)));

    /// <summary>Two letters no other test's codes start with (the seed's and NHA's specialties are A to P).</summary>
    private static string Specialty() => new([.. Enumerable.Range(0, 2).Select(_ => (char)('Q' + Random.Shared.Next(10)))]);

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('A' + Random.Shared.Next(26)))]);
}
