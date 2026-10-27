using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Packages;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Organization;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// AB PM-JAY's HBP master through the API (INS-PMJAY): the tariff keeper imports NHA's file, the insurance desk looks
/// procedures up, a package is mapped to one (and only to one the master lists), and a later master retires what it no
/// longer lists without breaking the packages mapped to it. The codes are this test's own, so other tests' masters do
/// not meet them.
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
        var specialty = new string([.. Enumerable.Range(0, 2).Select(_ => (char)('Q' + Random.Shared.Next(10)))]);
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

        var first = await ReadAsync<HbpImportResult>(await ImportAsync(admin, master, "HBP 2022"));
        (first.MasterVersion, first.Read, first.Added).ShouldBe(("HBP 2022", 2, 2));
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
        var second = await ReadAsync<HbpImportResult>(await ImportAsync(admin, master.Replace(dropped, "--", StringComparison.Ordinal), "HBP 2022 (revised)"));
        second.Retired.ShouldBeGreaterThanOrEqualTo(1);
        (await admin.GetAsync<List<HbpProcedureDto>>($"{Procedures}?search={specialty}101")).Select(p => p.Code).ShouldBe([kept]);
        var retired = (await admin.GetAsync<List<HbpProcedureDto>>($"{Procedures}?search={specialty}101&includeRetired=true")).Single(p => p.Code == dropped);
        retired.IsActive.ShouldBeFalse();
        await PackageRefusedAsync(admin, dropped, "was retired by HBP master HBP 2022");
        (await admin.GetAsync<HbpProcedureDto>($"{Procedures}/{kept}")).MasterVersion.ShouldBe("HBP 2022 (revised)");
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

    private static Task<HttpResponseMessage> ImportAsync(ApiClient client, string csv, string version)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(content, "file", "hbp.csv");
        form.Add(new StringContent(version), "version");
        var request = new HttpRequestMessage(HttpMethod.Post, Imports) { Content = form };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.Http.SendAsync(request, Ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        using (response)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            return (await response.Content.ReadFromJsonAsync<T>(ApiClient.Json, Ct))!;
        }
    }

    private static async Task<PackageDetail> PackageAsync(ApiClient admin, string? hbpCode) =>
        await admin.PostAsync<PackageDetail>("/api/v1/billing/packages", await PackageRequestAsync(admin, hbpCode));

    private static async Task PackageRefusedAsync(ApiClient admin, string hbpCode, string reason)
    {
        using var response = await admin.SendRawAsync(HttpMethod.Post, "/api/v1/billing/packages", await PackageRequestAsync(admin, hbpCode));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadAsStringAsync(Ct);
        problem.ShouldContain("hbpCode");
        problem.ShouldContain(reason);
    }

    private static async Task<SavePackageRequest> PackageRequestAsync(ApiClient admin, string? hbpCode)
    {
        var code = $"SV-{Letters(8)}";
        var item = await admin.PostAsync<ServiceItemDto>("/api/v1/masters/services", new SaveServiceItemRequest(
            code, "PM-JAY test package", null, ServiceCategory.Package, null, 12500m, 0m, null, true, false, null, true, null));
        return new SavePackageRequest($"PKG-{Letters(8)}", "PM-JAY test package", item.Id, 12500m, 2, null, PackageUse.InPatient, null, true, [], null, hbpCode);
    }

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('A' + Random.Shared.Next(26)))]);
}
