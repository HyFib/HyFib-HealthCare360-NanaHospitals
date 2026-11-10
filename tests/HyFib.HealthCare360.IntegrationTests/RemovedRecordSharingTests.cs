using System.Net;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Procedures;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static HyFib.HealthCare360.IntegrationTests.ScopedRecordSharingTests;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// What is removed from a patient's record is no longer shared: a FHIR search lists only what a read by id would
/// return. A search once listed a document front office had removed (its title, type, date and file size), as its query
/// ignored the soft-delete filter for the stored file it joined, and so for the document too.
/// </summary>
public sealed class RemovedRecordSharingTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_document_front_office_removes_is_no_longer_listed_to_a_provider()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (partner, partnerId, _) = await PartnerAsync(server);
        var patient = await RegisterAsync(frontOffice);
        var documents = $"/api/v1/patients/{patient}/documents";
        var kept = await frontOffice.PostAsync<Created>(documents, PatientDocumentTests.OutsideReportFrom(await PatientDocumentTests.UploadAsync(frontOffice, patient)));
        var removed = await frontOffice.PostAsync<Created>(documents,
            PatientDocumentTests.OutsideReportFrom(await PatientDocumentTests.UploadAsync(frontOffice, patient)) with { Title = "Uploaded to the wrong patient" });
        await frontOffice.PostAsync<Created>($"/api/v1/patients/{patient}/consents", new CaptureConsentRequest(ConsentType.DataSharing, null, true, null, null, null, null, null,
            new RecordSharingScope([partnerId], null, [SharedRecordKind.Documents], null, null, null)));
        var before = await FhirAsync(partner, $"DocumentReference?patient={patient}", null);
        Matches(before.Body).ShouldBe([$"doc-{kept.Id:D}", $"doc-{removed.Id:D}"], ignoreOrder: true);

        // The stored file's type and size, read apart from the documents, are on each.
        var attachment = Resource(before.Body, $"doc-{kept.Id:D}").GetProperty("content")[0].GetProperty("attachment");
        (attachment.GetProperty("contentType").GetString(), attachment.GetProperty("size").GetInt64())
            .ShouldBe(("application/pdf", (long)PatientDocumentTests.OutsideReport.Length));

        using (var response = await frontOffice.Http.DeleteAsync($"/api/v1/patients/documents/{removed.Id}", Ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        Matches((await FhirAsync(partner, $"DocumentReference?patient={patient}", null)).Body).ShouldBe([$"doc-{kept.Id:D}"]);
        (await FhirAsync(partner, $"DocumentReference/doc-{removed.Id:D}", null)).Refusal.ShouldBe((410, "deleted"));
    }

    [Fact]
    public async Task A_procedure_removed_from_the_record_reads_as_gone_to_a_provider()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (partner, partnerId, _) = await PartnerAsync(server);
        var patient = await RegisterAsync(frontOffice);
        var procedures = $"/api/v1/emr/patients/{patient}/procedures";
        var kept = await doctor.PostAsync<ProcedureDto>(procedures, new RecordProcedureRequest(
            null, DateTimeOffset.UtcNow.AddHours(-2), null, "Wound suturing", null, null, ProcedureSide.NotApplicable, "Forearm", null, null));
        var removed = await doctor.PostAsync<ProcedureDto>(procedures, new RecordProcedureRequest(
            null, DateTimeOffset.UtcNow.AddHours(-1), null, "Ear syringing", null, null, ProcedureSide.Left, null, null, null));
        await frontOffice.PostAsync<Created>($"/api/v1/patients/{patient}/consents", new CaptureConsentRequest(ConsentType.DataSharing, null, true, null, null, null, null, null,
            new RecordSharingScope([partnerId], null, [SharedRecordKind.Procedures], null, null, null)));
        Matches((await FhirAsync(partner, $"Procedure?patient={patient}", null)).Body).ShouldBe([kept.Id.ToString("D"), removed.Id.ToString("D")], ignoreOrder: true);

        // A procedure removed from the record. (No screen removes one; the rule holds for any procedure a module ever removes.)
        await using (var removing = server.Services.CreateAsyncScope())
        {
            var context = removing.ServiceProvider.GetRequiredService<HospitalDbContext>();
            context.Remove(await context.Set<ProcedureRecord>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).SingleAsync(p => p.Id == removed.Id, Ct));
            await context.SaveChangesAsync(Ct);
        }

        // It is no longer listed, and read by id it is gone (410), not a record that never was (404).
        Matches((await FhirAsync(partner, $"Procedure?patient={patient}", null)).Body).ShouldBe([kept.Id.ToString("D")]);
        (await FhirAsync(partner, $"Procedure/{removed.Id}", null)).Refusal.ShouldBe((410, "deleted"), "a provider that read it learns it was removed");
    }

    private static System.Text.Json.JsonElement Resource(System.Text.Json.JsonElement bundle, string id) =>
        bundle.GetProperty("entry").EnumerateArray().Select(e => e.GetProperty("resource"))
            .Single(r => r.TryGetProperty("id", out var value) && value.GetString() == id);
}
