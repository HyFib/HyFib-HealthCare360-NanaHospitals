using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using HyFib.HealthCare360.Testing.Abdm;
using Hl7.Fhir.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A finalised bill as an ABDM invoice record over the real API (SRS EHR-08): downloaded by the medical records department
/// and the hospital administrator, with amounts that are the bill's; a draft and a cancelled bill refused, saying why;
/// refused to staff without <c>abdm.export</c>; every export audited; and the finalised bill offered for linking to the
/// patient's ABHA, named by its number, and not the draft or the cancelled one. The document is validated against base
/// FHIR R4 and the guide's other profiles: the guide's InvoiceRecord and Invoice profiles are not in the package the
/// validator has, and it reports them unchecked and nothing else. In the feature-switch collection, as the ABDM switch is
/// hospital-wide.
/// </summary>
[Collection(FeatureSwitchCollection.Name)]
public sealed class AbdmInvoiceExportTests(HospitalServer server)
{
    private static readonly JsonSerializerOptions Fhir = new JsonSerializerOptions().ForFhir(R4.ModelInfo.ModelInspector);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_finalised_bill_is_exported_as_an_invoice_record_and_a_draft_or_cancelled_bill_is_not()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var abha = AbdmExportTests.NewAbha();
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", AbdmExportTests.Registration(abha, null));

        var bill = await FinalizedBillAsync(frontOffice, billing, patient.Id, dressings: 2m);
        var draft = await DraftBillAsync(frontOffice, billing, patient.Id, dressings: 1m);
        var cancelled = await FinalizedBillAsync(frontOffice, billing, patient.Id, dressings: 3m);
        await admin.PostAsync<BillDetail>($"/api/v1/billing/bills/{cancelled.Id}/cancel", new CancelBillRequest("Billed to the wrong patient", cancelled.Version));

        var path = Path(bill.Id);
        try
        {
            await SwitchAsync(enabled: true);

            // The medical records department and the hospital administrator download it.
            var bundle = await ExportAsync(await server.SignInAsync(HospitalServer.Users.MrdOfficer), path, $"bill-{bill.BillNumber}-abdm.json");
            var again = await ExportAsync(admin, path, $"bill-{bill.BillNumber}-abdm.json");
            FhirJson(again).ShouldBe(FhirJson(bundle), "the same bill exported again is the same document");

            var composition = (R4.Composition)bundle.Entry[0].Resource!;
            composition.Meta!.Profile.ShouldBe([AbdmProfiles.InvoiceRecord]);
            composition.Type.Coding.ShouldHaveSingleItem().Code.ShouldBe("INVOICE");
            var patientResource = bundle.Entry.Select(e => e.Resource).OfType<R4.Patient>().Single();
            patientResource.Identifier.Single(i => i.System == "https://healthid.ndhm.gov.in").Value.ShouldBe(Abha.Format(abha));

            // The invoice is the bill: its number, its lines and its amounts.
            var invoice = bundle.Entry.Select(e => e.Resource).OfType<R4.Invoice>().Single();
            invoice.Identifier.ShouldHaveSingleItem().Value.ShouldBe(bill.BillNumber);
            invoice.Status.ShouldBe(R4.Invoice.InvoiceStatus.Issued);
            // A line for each of the bill's (the first visit's registration fee and the dressings), each adding up to its net.
            invoice.LineItem.Select(i => ((R4.CodeableConcept)i.ChargeItem!).Coding[0].Code).ShouldBe(bill.Lines.Select(l => l.ServiceCode));
            foreach (var (item, billed) in invoice.LineItem.Zip(bill.Lines))
            {
                var @base = item.PriceComponent.Single(c => c.Type == R4.InvoicePriceComponentType.Base);
                (@base.Factor, @base.Amount!.Value).ShouldBe((billed.Quantity, billed.GrossAmount));
                item.PriceComponent.Where(c => c.Type != R4.InvoicePriceComponentType.Informational).Sum(c => c.Amount!.Value).ShouldBe(billed.NetAmount);
            }

            invoice.LineItem.Single(i => ((R4.CodeableConcept)i.ChargeItem!).Coding[0].Code == "PROC-DRESS")
                .PriceComponent.Single(c => c.Type == R4.InvoicePriceComponentType.Base).Factor.ShouldBe(2m);
            invoice.TotalPriceComponent.Where(c => c.Type != R4.InvoicePriceComponentType.Informational).Sum(c => c.Amount!.Value).ShouldBe(bill.NetAmount);
            (invoice.TotalNet!.Value, invoice.TotalGross!.Value, invoice.TotalGross.Currency).ShouldBe((bill.TaxableAmount, bill.NetAmount, R4.Money.Currencies.INR));
            ShouldCarryThePrintedBill(bundle);

            var exported = await AuditAsync(patient.Id, AbdmBundleService.Exported);
            exported.ShouldNotBeNull().Summary.ShouldBe("ABDM health record exported: bill");
            (exported.EntityType, exported.EntityId).ShouldBe(("abdm.Invoice", bill.Id.ToString("D")));

            // A draft is an estimate, and a cancelled bill is not owed: neither is exported, and each says why.
            (await NotExportableAsync(admin, Path(draft.Id))).ShouldBe("Only a finalised bill can be exported: a draft is an estimate.");
            (await NotExportableAsync(admin, Path(cancelled.Id))).ShouldBe("A cancelled bill is not exported: the patient does not owe it.");

            // Staff without abdm.export (the billing desk that raised it, a doctor) may not export it.
            foreach (var user in new[] { HospitalServer.Users.Billing, HospitalServer.Users.Doctor })
            {
                using var refused = await (await server.SignInAsync(user)).SendRawAsync(HttpMethod.Get, path);
                refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, user);
            }

            // A bill that does not exist is not found.
            using (var missing = await admin.SendRawAsync(HttpMethod.Get, Path(Guid.NewGuid())))
            {
                missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            }

            // The patient's app is offered the finalised bill to link to their ABHA, named by its number: not the draft, nor
            // the cancelled one.
            await using var linking = server.Services.CreateAsyncScope();
            var offered = await linking.ServiceProvider.GetRequiredService<AbdmRecordLoader>().ListExportableAsync(patient.Id, 50, Ct);
            offered.Where(r => r.Type == AbdmRecordType.Invoice).Select(r => (r.Id, r.Label)).ShouldBe([(bill.Id, bill.BillNumber)]);
            AbdmCareContexts.Describe(AbdmRecordType.Invoice, bill.Id, bill.FinalizedAt!.Value, bill.BillNumber).Display.ShouldStartWith($"Bill: {bill.BillNumber}, ");
        }
        finally
        {
            await SwitchAsync(enabled: false);
        }

        // With ABDM off nothing is exported.
        using var off = await admin.SendRawAsync(HttpMethod.Get, path);
        off.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await off.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString().ShouldBe(FeatureProblems.Code);
    }

    [Fact]
    public async Task A_paid_bill_is_balanced()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", AbdmExportTests.Registration(AbdmExportTests.NewAbha(), null));
        var bill = await FinalizedBillAsync(frontOffice, billing, patient.Id, dressings: 1m);

        // Settled as the counter settles it once paid (the receipt flow is the billing tests'); the export reads the bill.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var row = await db.Set<Bill>().SingleAsync(b => b.Id == bill.Id, Ct);
            row.ApplyPayment(row.DueAmount, allowExcess: false).IsSuccess.ShouldBeTrue();
            row.ClearDomainEvents();
            await db.SaveChangesAsync(Ct);
        }

        try
        {
            await SwitchAsync(enabled: true);
            var bundle = await ExportAsync(admin, Path(bill.Id), $"bill-{bill.BillNumber}-abdm.json");
            bundle.Entry.Select(e => e.Resource).OfType<R4.Invoice>().Single().Status.ShouldBe(R4.Invoice.InvoiceStatus.Balanced);
        }
        finally
        {
            await SwitchAsync(enabled: false);
        }
    }

    internal static string Path(Guid billId) => $"/api/v1/abdm/bundles/invoices/{billId}";

    /// <summary>A health check-up visit charged <paramref name="dressings"/> dressings (₹250 each, no GST), billed as a draft.</summary>
    internal static async Task<BillDetail> DraftBillAsync(ApiClient frontOffice, ApiClient billing, Guid patientId, decimal dressings)
    {
        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patientId, VisitType.HealthCheckup, null, null, null, PayerType.Self, null, ReferralSource.Self, null, null, false, null, null,
            TokenPriority.Normal, IssueToken: false, NotifyPatient: false));
        var dressing = (await billing.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?search=PROC-DRESS")).Single(s => s.Code == "PROC-DRESS");
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(patientId, visit.Id, null, dressing.Id, dressings, null, null, null, null, null));
        return await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(visit.Id, null, null));
    }

    /// <summary>The same, finalised by the billing desk under its number.</summary>
    internal static async Task<BillDetail> FinalizedBillAsync(ApiClient frontOffice, ApiClient billing, Guid patientId, decimal dressings)
    {
        var draft = await DraftBillAsync(frontOffice, billing, patientId, dressings);
        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        bill.Status.ShouldBe(BillStatus.Finalized);
        bill.BillNumber.ShouldNotBeNullOrWhiteSpace();
        return bill;
    }

    /// <summary>
    /// The bill as <paramref name="client"/> downloads it: a FHIR document saved under the bill's number, never cached, which
    /// meets base FHIR R4 and every profile of the guide the validator has; the two it lacks it reports unchecked, and only
    /// those.
    /// </summary>
    private static async Task<R4.Bundle> ExportAsync(ApiClient client, string path, string fileName)
    {
        using var exported = await client.SendRawAsync(HttpMethod.Get, path);
        var body = await exported.Content.ReadAsStringAsync(Ct);
        exported.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        exported.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/fhir+json");
        exported.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        var disposition = exported.Content.Headers.ContentDisposition.ShouldNotBeNull();
        (disposition.FileNameStar ?? disposition.FileName).ShouldNotBeNull().Trim('"').ShouldBe(fileName);

        var bundle = JsonSerializer.Deserialize<R4.Bundle>(body, Fhir).ShouldNotBeNull();
        var validation = AbdmProfileValidator.Validate(bundle, AbdmProfileValidator.DocumentBundle);
        string[] notChecked = [$"Unable to resolve reference to profile '{AbdmProfiles.InvoiceRecord}'.", $"Unable to resolve reference to profile '{AbdmProfiles.Invoice}'."];
        validation.Errors.Select(e => e.Details?.Text ?? e.Diagnostics).Order(StringComparer.Ordinal).ShouldBe(notChecked.Order(StringComparer.Ordinal), validation.Describe());
        bundle.Type.ShouldBe(R4.Bundle.BundleType.Document);

        // It stands alone: every reference is to one of its entries.
        var addresses = bundle.Entry.Select(e => e.FullUrl).ToHashSet(StringComparer.Ordinal);
        References(JsonDocument.Parse(body).RootElement).ShouldAllBe(r => addresses.Contains(r));
        return bundle;
    }

    private static async Task<string?> NotExportableAsync(ApiClient client, string path)
    {
        using var refused = await client.SendRawAsync(HttpMethod.Get, path);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.Content.ReadAsStringAsync(Ct));
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        problem.GetProperty("code").GetString().ShouldBe("abdm.not_exportable");
        return problem.GetProperty("detail").GetString();
    }

    /// <summary>The printed bill wherever this server has a browser to make PDFs with, and none where it has none.</summary>
    private void ShouldCarryThePrintedBill(R4.Bundle bundle)
    {
        var printed = bundle.Entry.Select(e => e.Resource).OfType<R4.Binary>().ToList();
        if (server.Services.GetRequiredService<IPdfRenderer>().Status.Browser is null)
        {
            printed.ShouldBeEmpty("this server has no browser to make PDFs with");
            return;
        }

        var pdf = printed.ShouldHaveSingleItem();
        pdf.ContentType.ShouldBe("application/pdf");
        Encoding.ASCII.GetString(pdf.Data!, 0, 5).ShouldBe("%PDF-");
    }

    private static string FhirJson(R4.Bundle bundle) => JsonSerializer.Serialize(bundle, Fhir);

    private static IEnumerable<string> References(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p =>
            p is { Name: "reference", Value.ValueKind: JsonValueKind.String } ? [p.Value.GetString()!] : References(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(References),
        _ => [],
    };

    private async Task SwitchAsync(bool enabled)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        (await admin.SetFeatureAsync(FeatureKeys.Abdm, new SetFeatureRequest(enabled))).Enabled.ShouldBe(enabled);
    }

    private async Task<AuditEvent?> AuditAsync(Guid patientId, string action)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(a => a.PatientId == patientId && a.Action == action)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(Ct);
    }
}
