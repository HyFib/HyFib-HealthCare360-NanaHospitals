using System.Net;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Print templates (SRS HMS-04, UX-09): an administrator sets how each kind of document prints, the server's documents
/// follow it, and the staff app reads it with the letterhead for its own prints. The server is shared by the whole run, so these tests change
/// only this facility's bills template, which no other test prints, and put it back.
/// </summary>
public sealed class PrintTemplateTests(HospitalServer server)
{
    private const string Base = "/api/v1/admin/print-templates";

    [Fact]
    public async Task Only_administrators_set_up_print_templates_and_values_outside_the_rules_are_refused()
    {
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        using (var refused = await billing.SendRawAsync(HttpMethod.Get, Base))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var list = await admin.GetAsync<PrintTemplateList>(Base);
        list.Templates.Select(t => t.Key).ShouldBe(PrintTemplateCatalog.All.Select(d => d.Key));
        list.Templates.Single(t => t.Key == PrintTemplateCatalog.Prescription).Papers.ShouldBe([DocumentPageSize.A5, DocumentPageSize.A4]);
        list.Templates.Single(t => t.Key == PrintTemplateCatalog.SalesInvoice).AllowsPreprinted.ShouldBeFalse();
        list.FacilityName.ShouldNotBeNullOrEmpty();
        list.FacilityCount.ShouldBeGreaterThan(0);

        await RefusedAsync(admin, PrintTemplateCatalog.Report, PrintTemplate.Default with { Letterhead = PrintLetterhead.Preprinted, TopSpaceMm = 200 }, "topSpaceMm");
        await RefusedAsync(admin, PrintTemplateCatalog.SalesInvoice, PrintTemplate.Default with { Letterhead = PrintLetterhead.Preprinted }, "letterhead");
        await RefusedAsync(admin, PrintTemplateCatalog.Bill, PrintTemplate.Default with { Paper = DocumentPageSize.A5 }, "paper");
        using var unknown = await admin.SendRawAsync(HttpMethod.Put, $"{Base}/letters", new SavePrintTemplateRequest(PrintTemplate.Default, ForThisFacility: false));
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task This_facility_s_bills_template_reaches_the_staff_app_and_the_printed_bill_until_it_is_removed()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var bill = await DraftBillAsync(billing);
        var facility = (await billing.GetAsync<FacilityDto>($"/api/v1/masters/facilities/{bill.FacilityId}")).Name;
        await using var restore = new Restore(() => admin.SendRawAsync(HttpMethod.Delete, $"{Base}/{PrintTemplateCatalog.Bill}/facility"));

        var stationery = new PrintTemplate
        {
            Letterhead = PrintLetterhead.Preprinted,
            TopSpaceMm = 38,
            ShowLogo = false,
            ClosingNote = "  Subject to Pune jurisdiction.\nThank you.  ",
        };
        await admin.PutAsync($"{Base}/{PrintTemplateCatalog.Bill}", new SavePrintTemplateRequest(stationery, ForThisFacility: true));

        var saved = (await admin.GetAsync<PrintTemplateList>(Base)).Templates.Single(t => t.Key == PrintTemplateCatalog.Bill);
        saved.Facility.ShouldNotBeNull().ClosingNote.ShouldBe("Subject to Pune jurisdiction.\nThank you.", "kept trimmed");
        saved.Facility.TopSpaceMm.ShouldBe(38);

        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var letterhead = await pharmacist.GetAsync<PrintLetterheadDto>("/api/v1/print/letterhead");
        letterhead.Name.ShouldBe(facility, "every member of staff prints with the letterhead");
        letterhead.Templates.Keys.ShouldBe(PrintTemplateCatalog.All.Select(d => d.Key), ignoreOrder: true);
        letterhead.Templates[PrintTemplateCatalog.Bill].ShouldBe(stationery with { ClosingNote = "Subject to Pune jurisdiction.\nThank you." },
            "the staff app's running bill follows it too");
        letterhead.Templates[PrintTemplateCatalog.Receipt].ShouldBe(PrintTemplate.Default);

        var printed = (await billing.GetAsync<PrintableDocumentDto>($"/api/v1/billing/bills/{bill.Id}/document")).Html;
        printed.ShouldNotContain($"<div class=\"h-name\">", Case.Sensitive, "the stationery has the hospital's name");
        printed.ShouldContain("margin: 50mm 12mm 16mm;");
        printed.ShouldContain("<div class=\"closing-note\">Subject to Pune jurisdiction.<br>Thank you.</div>");

        using (var removed = await admin.SendRawAsync(HttpMethod.Delete, $"{Base}/{PrintTemplateCatalog.Bill}/facility"))
        {
            removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await admin.GetAsync<PrintTemplateList>(Base)).Templates.Single(t => t.Key == PrintTemplateCatalog.Bill).Facility.ShouldBeNull();
        var again = (await billing.GetAsync<PrintableDocumentDto>($"/api/v1/billing/bills/{bill.Id}/document")).Html;
        again.ShouldContain("<div class=\"h-name\">", Case.Sensitive, "back to the hospital-wide template");
        again.ShouldContain(facility);
        again.ShouldNotContain("class=\"closing-note\"");
    }

    private static async Task RefusedAsync(ApiClient admin, string key, PrintTemplate template, string field)
    {
        using var response = await admin.SendRawAsync(HttpMethod.Put, $"{Base}/{key}", new SavePrintTemplateRequest(template, ForThisFacility: false));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain($"\"{field}\"");
    }

    /// <summary>A new patient's draft bill for a registration fee: an estimate, which prints with the bills template.</summary>
    private async Task<BillDetail> DraftBillAsync(ApiClient billing)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var surname = "Print" + new string(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());
        var registered = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Ms.", FirstName: TestPatients.FirstName("Asha"), MiddleName: null, LastName: surname, Gender: Gender.Female,
            DateOfBirth: new DateOnly(1985, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: "8" + string.Concat(Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10))), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null,
            AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        var service = (await billing.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?search=REG-NEW")).Single(s => s.Code == "REG-NEW");
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(
            registered.Id, null, null, service.Id, 1m, null, null, null, null, null));
        return await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(null, registered.Id, null));
    }

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
