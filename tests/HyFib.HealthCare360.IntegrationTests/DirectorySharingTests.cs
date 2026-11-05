using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Procedures;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static HyFib.HealthCare360.IntegrationTests.ScopedRecordSharingTests;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The hospital's directory over the FHIR API (gap #23). A provider reading a patient's records follows them to the doctor
/// who did the work and to the hospital, with no consent: they are no patient's. A doctor is looked up, never listed, and
/// the reads are integration events, not PHI access.
/// </summary>
public sealed class DirectorySharingTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_provider_follows_a_record_to_the_doctor_who_did_it_and_to_the_hospital_without_a_consent()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (partner, partnerId, _) = await PartnerAsync(server);
        var patient = await RegisterAsync(frontOffice);
        var drainage = await doctor.PostAsync<ProcedureDto>($"/api/v1/emr/patients/{patient}/procedures", new RecordProcedureRequest(
            null, DateTimeOffset.UtcNow.AddHours(-1), null, "Incision and drainage of abscess", null, null, ProcedureSide.Right, "Axilla",
            "Pus drained", null));
        await frontOffice.PostAsync<Created>($"/api/v1/patients/{patient}/consents", new CaptureConsentRequest(
            ConsentType.DataSharing, null, true, null, null, null, null, null,
            new RecordSharingScope([partnerId], [SharingPurpose.Treatment], [SharedRecordKind.Procedures], null, null, null)));

        // The procedure names the doctor who did it by a reference that resolves, with no consent asked: their name and
        // whether they are in use, never how to reach them.
        var procedure = (await FhirAsync(partner, $"Procedure/{drainage.Id}", "TREAT")).Body;
        var doctorReference = procedure.GetProperty("performer")[0].GetProperty("actor").GetProperty("reference").GetString().ShouldNotBeNull();
        doctorReference.ShouldStartWith("Practitioner/");
        var practitioner = await FhirAsync(partner, doctorReference, null);
        practitioner.Status.ShouldBe(200);
        practitioner.Body.GetProperty("name")[0].GetProperty("text").GetString().ShouldBe(drainage.PerformerName);
        practitioner.Body.GetProperty("active").GetBoolean().ShouldBeTrue();
        practitioner.Body.TryGetProperty("telecom", out _).ShouldBeFalse("who a doctor is, never how to reach them");

        // A doctor is looked up by name, never listed.
        var doctorId = doctorReference["Practitioner/".Length..];
        var surname = drainage.PerformerName.ShouldNotBeNull().Split(' ')[^1];
        Matches((await FhirAsync(partner, $"Practitioner?name={Uri.EscapeDataString(surname)}&_count=100", null)).Body).ShouldContain(doctorId);
        (await FhirAsync(partner, "Practitioner?active=true", null)).Status.ShouldBe(400);

        // The patient's record names the hospital that keeps it: read by its reference, and among the facilities listed.
        var organizationReference = (await FhirAsync(partner, $"Patient/{patient}", "TREAT")).Body
            .GetProperty("managingOrganization").GetProperty("reference").GetString().ShouldNotBeNull();
        organizationReference.ShouldStartWith("Organization/");
        var organization = await FhirAsync(partner, organizationReference, null);
        organization.Status.ShouldBe(200);
        var facilityId = organizationReference["Organization/".Length..];
        Matches((await FhirAsync(partner, "Organization", null)).Body).ShouldContain(facilityId);

        // The reads are integration events, not PHI access: no patient's access history shows them.
        foreach (var (type, id) in new[] { ("Practitioner", doctorId), ("Organization", facilityId) })
        {
            var entries = await DirectoryAuditAsync(type, id);
            entries.ShouldNotBeEmpty();
            entries.ShouldAllBe(e => e.Category == AuditCategories.Integration && e.PatientId == null);
        }
    }

    [Fact]
    public async Task A_doctor_taken_out_of_use_is_read_as_not_active_and_one_deleted_is_gone()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var (partner, _, _) = await PartnerAsync(server);
        var (retiredId, _) = await DoctorFeeStatementTests.DoctorAsync(admin);
        var (deletedId, _) = await DoctorFeeStatementTests.DoctorAsync(admin);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.Set<Practitioner>().SingleAsync(p => p.Id == retiredId, Ct)).IsActive = false;
            var deleted = await db.Set<Practitioner>().SingleAsync(p => p.Id == deletedId, Ct);
            (deleted.IsDeleted, deleted.DeletedAt) = (true, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
        }

        var retired = await FhirAsync(partner, $"Practitioner/{retiredId}", null);
        (retired.Status, retired.Body.GetProperty("active").GetBoolean()).ShouldBe((200, false));
        Matches((await FhirAsync(partner, $"Practitioner?_id={retiredId}&active=false", null)).Body).ShouldBe([retiredId.ToString("D")]);

        // Records still name the deleted doctor: read by id, they are gone; a search never finds them.
        (await FhirAsync(partner, $"Practitioner/{deletedId}", null)).Status.ShouldBe(410);
        Matches((await FhirAsync(partner, $"Practitioner?_id={deletedId}", null)).Body).ShouldBeEmpty();
        var unknown = Guid.CreateVersion7();
        (await FhirAsync(partner, $"Practitioner/{unknown}", null)).Status.ShouldBe(404);

        // A lookup that finds a deleted doctor, or none, is on the audit trail too: a probe for ids is seen.
        (await DirectoryAuditAsync("Practitioner", deletedId.ToString("D"))).ShouldHaveSingleItem().Summary.ShouldEndWith("(gone)");
        (await DirectoryAuditAsync("Practitioner", unknown.ToString("D"))).ShouldHaveSingleItem().Summary.ShouldEndWith("(not-found)");
        (await FhirAsync(partner, $"Practitioner/{new string('x', 200)}", null)).Status.ShouldBe(404, "an id too long for the trail is still answered");
    }

    private async Task<List<AuditEvent>> DirectoryAuditAsync(string type, string id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(a => a.EntityType == "fhir." + type && a.EntityId == id)
            .ToListAsync(Ct);
    }
}
