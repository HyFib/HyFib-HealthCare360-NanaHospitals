using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Procedures;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static HyFib.HealthCare360.IntegrationTests.ScopedRecordSharingTests;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The hospital's directory over the FHIR API (gaps #23 and #24). A provider reading a patient's records follows them to
/// the doctor who did the work, to the hospital, and to the wards and beds of a stay, with no consent: they are no
/// patient's. A doctor is looked up, never listed, and the reads are integration events, not PHI access.
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

    [Fact]
    public async Task A_stay_names_the_ward_and_bed_of_each_part_of_it_and_they_lead_to_the_hospital()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var (partner, partnerId, _) = await PartnerAsync(server);
        var patient = await RegisterAsync(frontOffice);

        // Admitted to a bed of the open female ward, then moved to a bed in a room of the semi-private ward.
        var wards = await admin.GetAsync<List<WardDto>>("/api/v1/inpatient/wards");
        var (open, semiPrivate, icu) = (wards.Single(w => w.Code == "GWF"), wards.Single(w => w.Code == "SPW"), wards.Single(w => w.Code == "ICU"));
        var room = (await admin.GetAsync<List<RoomDto>>($"/api/v1/inpatient/wards/{semiPrivate.Id}/rooms")).First(r => r.IsActive);
        var first = await TestBeds.AddAsync(admin, open.Id);
        var second = await admin.PostAsync<BedDto>("/api/v1/beds", new SaveBedRequest(
            semiPrivate.Id, room.Id, "T-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(), false, false, false, 999, true, null));
        var admission = await AdmitAsync(frontOffice, patient, first.BedId);
        await frontOffice.PostAsync($"/api/v1/inpatient/admissions/{admission.Id}/transfer",
            new TransferPatientRequest(second.Id, "Moved to a room at the family's request", null, null, null));
        await frontOffice.PostAsync<Created>($"/api/v1/patients/{patient}/consents", new CaptureConsentRequest(
            ConsentType.DataSharing, null, true, null, null, null, null, null,
            new RecordSharingScope([partnerId], [SharingPurpose.Treatment], [SharedRecordKind.Visits], null, null, null)));

        // The stay names each ward and bed with when: the first ward and bed left, the second where the patient is now.
        var stay = (await FhirAsync(partner, $"Encounter/visit-{admission.VisitId}", "TREAT")).Body;
        var places = stay.GetProperty("location").EnumerateArray().ToList();
        places.Select(l => l.GetProperty("location").GetProperty("reference").GetString()).ShouldBe(
            [$"Location/ward-{open.Id}", $"Location/bed-{first.BedId}", $"Location/ward-{semiPrivate.Id}", $"Location/bed-{second.Id}"]);
        places.Select(l => l.GetProperty("location").GetProperty("display").GetString()).ShouldBe(
            [open.Name, first.Label, semiPrivate.Name, second.Label], "a ward by its name and a bed by its label, as the bed board shows them");
        places.Select(l => (l.GetProperty("status").GetString(), l.GetProperty("physicalType").GetProperty("coding")[0].GetProperty("code").GetString()))
            .ShouldBe([("completed", "wa"), ("completed", "bd"), ("active", "wa"), ("active", "bd")]);
        places[1].GetProperty("period").GetProperty("end").GetString().ShouldBe(places[3].GetProperty("period").GetProperty("start").GetString());
        places[3].GetProperty("period").TryGetProperty("end", out _).ShouldBeFalse("the patient is still in that bed");

        // The bed is in its room, the room in its ward, and the ward is the hospital's: each reference resolves.
        var bed = await FhirAsync(partner, $"Location/bed-{second.Id}", null);
        bed.Status.ShouldBe(200);
        (bed.Body.GetProperty("name").GetString(), bed.Body.GetProperty("type")[0].GetProperty("coding")[0].GetProperty("code").GetString())
            .ShouldBe((second.Label, "HU"));
        bed.Body.TryGetProperty("description", out _).ShouldBeFalse("what a place is, never what happens in it");
        var inRoom = await FhirAsync(partner, Reference(bed.Body, "partOf"), null);
        (inRoom.Status, inRoom.Body.GetProperty("id").GetString()).ShouldBe((200, $"room-{room.Id}"));
        var ward = await FhirAsync(partner, Reference(inRoom.Body, "partOf"), null);
        (ward.Status, ward.Body.GetProperty("name").GetString()).ShouldBe((200, semiPrivate.Name));
        (await FhirAsync(partner, Reference(ward.Body, "managingOrganization"), null)).Status.ShouldBe(200);

        // A place is found from the ward or room it is in, and a ward by its kind. (The open ward gathers the beds every
        // test adds to it, so the searches name the place too, whatever page it would fall on.)
        Matches((await FhirAsync(partner, $"Location?partof=Location/room-{room.Id}&_count=100", null)).Body).ShouldContain($"bed-{second.Id}");
        Matches((await FhirAsync(partner, $"Location?_id=bed-{first.BedId}&partof=ward-{open.Id}", null)).Body).ShouldBe([$"bed-{first.BedId}"]);
        Matches((await FhirAsync(partner, $"Location?_id=bed-{first.BedId}&partof=ward-{semiPrivate.Id}", null)).Body).ShouldBeEmpty();
        Matches((await FhirAsync(partner, $"Location?_id=ward-{icu.Id},ward-{open.Id}&type=ICU", null)).Body).ShouldBe([$"ward-{icu.Id}"]);

        // Reading a place is an integration event, in no patient's access history.
        var entries = await DirectoryAuditAsync("Location", $"bed-{second.Id}");
        entries.ShouldNotBeEmpty();
        entries.ShouldAllBe(e => e.Category == AuditCategories.Integration && e.PatientId == null);
    }

    [Fact]
    public async Task A_stay_still_names_the_ward_and_bed_it_was_in_after_they_are_deleted_by_mistake()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var (partner, partnerId, _) = await PartnerAsync(server);
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();
        var patient = await RegisterAsync(frontOffice);

        // A ward of the test's own, with a bed added on the bed board: the patient is admitted to it, then moved to a bed of
        // the open female ward.
        var ward = NewWard(facilityId);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            db.Add(ward);
            await db.SaveChangesAsync(Ct);
        }

        var open = (await admin.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "GWF");
        var (first, second) = (await TestBeds.AddAsync(admin, ward.Id), await TestBeds.AddAsync(admin, open.Id));
        var admission = await AdmitAsync(frontOffice, patient, first.BedId);
        try
        {
            await frontOffice.PostAsync($"/api/v1/inpatient/admissions/{admission.Id}/transfer",
                new TransferPatientRequest(second.BedId, "Moved to the female ward", null, null, null));
            await frontOffice.PostAsync<Created>($"/api/v1/patients/{patient}/consents", new CaptureConsentRequest(
                ConsentType.DataSharing, null, true, null, null, null, null, null,
                new RecordSharingScope([partnerId], [SharingPurpose.Treatment], [SharedRecordKind.Visits], null, null, null)));

            // Then the first ward and its bed are deleted by mistake (the app takes places out of use; it never deletes one).
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
                var deletedWard = await db.Set<Ward>().SingleAsync(w => w.Id == ward.Id, Ct);
                var deletedBed = await db.Set<Bed>().SingleAsync(b => b.Id == first.BedId, Ct);
                (deletedWard.IsDeleted, deletedWard.DeletedAt) = (true, DateTimeOffset.UtcNow);
                (deletedBed.IsDeleted, deletedBed.DeletedAt) = (true, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(Ct);
            }

            // The stay still says where the patient was: that ward and that bed, by the name and label the bed board showed,
            // and then where the patient is now.
            var stay = (await FhirAsync(partner, $"Encounter/visit-{admission.VisitId}", "TREAT")).Body;
            stay.GetProperty("location").EnumerateArray()
                .Select(l => (l.GetProperty("location").GetProperty("reference").GetString(), l.GetProperty("location").GetProperty("display").GetString(),
                    l.GetProperty("status").GetString()))
                .ShouldBe([
                    ($"Location/ward-{ward.Id}", ward.Name, "completed"),
                    ($"Location/bed-{first.BedId}", first.Label, "completed"),
                    ($"Location/ward-{open.Id}", open.Name, "active"),
                    ($"Location/bed-{second.BedId}", second.Label, "active"),
                ], "a past stay keeps the ward and bed it was in, deleted since or not");
        }
        finally
        {
            await admin.PostAsync($"/api/v1/inpatient/admissions/{admission.Id}/cancel", new CancelAdmissionRequest("Admitted for a test of the FHIR directory"));
        }
    }

    [Fact]
    public async Task A_facility_taken_out_of_use_reads_as_not_active_one_deleted_is_gone_and_every_search_is_on_the_trail()
    {
        var (partner, partnerId, _) = await PartnerAsync(server);
        var closed = await FacilityAsync(isActive: false);
        var deleted = await FacilityAsync(isActive: true);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var facility = await db.Set<Facility>().SingleAsync(f => f.Id == deleted.Id, Ct);
            (facility.IsDeleted, facility.DeletedAt) = (true, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
        }

        // A facility closed in the app reads as not active, and is found so.
        var read = await FhirAsync(partner, $"Organization/{closed.Id}", null);
        (read.Status, read.Body.GetProperty("active").GetBoolean()).ShouldBe((200, false));
        Matches((await FhirAsync(partner, $"Organization?_id={closed.Id}&active=false", null)).Body).ShouldBe([closed.Id.ToString("D")]);

        // Records may still name a facility deleted by mistake: read by id, it is gone; a search never finds it.
        (await FhirAsync(partner, $"Organization/{deleted.Id}", null)).Status.ShouldBe(410);
        Matches((await FhirAsync(partner, $"Organization?_id={deleted.Id}", null)).Body).ShouldBeEmpty();
        Matches((await FhirAsync(partner, $"Organization?identifier={deleted.Code}", null)).Body).ShouldBeEmpty();
        (await DirectoryAuditAsync("Organization", deleted.Id.ToString("D"))).ShouldHaveSingleItem().Summary.ShouldEndWith("(gone)");

        // For treatment, the first page of one of every facility changed since 2000: the hospital's own and the one closed
        // here match at least, so fewer are returned than matched.
        (await FhirAsync(partner, "Organization?_lastUpdated=ge2000-01-01&_count=1", "TREAT")).Status.ShouldBe(200);

        // Each search is on the trail, by its parameter names, as an integration event in no patient's access history.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var searches = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
                .Where(a => a.Action == "fhir.directory" && a.UserId == partnerId && a.EntityId == null)
                .OrderBy(a => a.Id)
                .ToListAsync(Ct);
            searches.Count.ShouldBe(4);
            searches.ShouldAllBe(e => e.Category == AuditCategories.Integration && e.PatientId == null && e.EntityType == "fhir.Organization");
            searches.ShouldAllBe(e => e.Summary!.StartsWith("FHIR R4 search Organization: ", StringComparison.Ordinal));

            // The page's entry says how many it returned of how many matched, which parameters were used (never their
            // values) and for what purpose.
            var page = searches[^1];
            var json = page.Details.ShouldNotBeNull();
            using var document = JsonDocument.Parse(json);
            var details = document.RootElement;
            var total = details.GetProperty("total").GetInt32();
            total.ShouldBeGreaterThan(1, "every live facility matched, the hospital's own and the one closed here at least");
            (details.GetProperty("returned").GetInt32(), page.Summary).ShouldBe((1, $"FHIR R4 search Organization: 1 of {total} returned"));
            details.GetProperty("parameters").EnumerateArray().Select(p => p.GetString()).ShouldBe(["_lastUpdated", "_count"]);
            details.GetProperty("purpose").GetString().ShouldBe("TREAT", "the purpose the partner stated");
            json.ShouldNotContain("2000-01-01", Case.Sensitive, "what was searched for is not recorded, only by which parameters");
        }
    }

    [Fact]
    public async Task A_place_deleted_by_mistake_is_gone_and_another_facilitys_places_are_not_the_readers()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var (partner, _, _) = await PartnerAsync(server);
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();
        var wards = await admin.GetAsync<List<WardDto>>("/api/v1/inpatient/wards");
        var (open, semiPrivate) = (wards.Single(w => w.Code == "GWF"), wards.Single(w => w.Code == "SPW"));
        var bed = await TestBeds.AddAsync(admin, open.Id);
        var annexe = await FacilityAsync(isActive: true);
        Ward deletedWard, annexeWard;
        Room deletedRoom;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var deletedBed = await db.Set<Bed>().SingleAsync(b => b.Id == bed.BedId, Ct);
            (deletedBed.IsDeleted, deletedBed.DeletedAt) = (true, DateTimeOffset.UtcNow);
            deletedWard = NewWard(facilityId);
            (deletedWard.IsDeleted, deletedWard.DeletedAt) = (true, DateTimeOffset.UtcNow);
            annexeWard = NewWard(annexe.Id);

            // A room of the semi-private ward, which is in use, recorded and then deleted by mistake.
            var number = "D" + string.Concat(Enumerable.Range(0, 6).Select(_ => (char)('0' + Random.Shared.Next(10))));
            deletedRoom = new Room { FacilityId = facilityId, WardId = semiPrivate.Id, Number = number, IsDeleted = true, DeletedAt = DateTimeOffset.UtcNow };
            db.AddRange(deletedWard, annexeWard, deletedRoom);
            await db.SaveChangesAsync(Ct);
        }

        // Places are taken out of use, not deleted; one deleted by mistake is gone by id, and no search finds it.
        (await FhirAsync(partner, $"Location/bed-{bed.BedId}", null)).Status.ShouldBe(410);
        Matches((await FhirAsync(partner, $"Location?_id=bed-{bed.BedId}", null)).Body).ShouldBeEmpty();
        (await FhirAsync(partner, $"Location/ward-{deletedWard.Id}", null)).Status.ShouldBe(410);
        Matches((await FhirAsync(partner, $"Location?_id=ward-{deletedWard.Id}", null)).Body).ShouldBeEmpty();

        // So is a room, though its ward is in use: gone by id, in no search, not even among the places of its ward.
        (await FhirAsync(partner, $"Location/room-{deletedRoom.Id}", null)).Refusal.ShouldBe((410, "deleted"), "records may still name the room");
        Matches((await FhirAsync(partner, $"Location?_id=room-{deletedRoom.Id}", null)).Body).ShouldBeEmpty();
        Matches((await FhirAsync(partner, $"Location?partof=Location/ward-{semiPrivate.Id}&_count=100", null)).Body)
            .ShouldNotContain($"room-{deletedRoom.Id}", "a room deleted by mistake is no place of its ward");

        // Another facility's wards are that facility's: a reader of this one neither reads nor finds them.
        (await FhirAsync(partner, $"Location/ward-{annexeWard.Id}", null)).Status.ShouldBe(404);
        Matches((await FhirAsync(partner, $"Location?_id=ward-{annexeWard.Id}", null)).Body).ShouldBeEmpty();
        Matches((await FhirAsync(partner, $"Location?organization=Organization/{annexe.Id}", null)).Body).ShouldBeEmpty();
    }

    private static string Reference(JsonElement resource, string element) =>
        resource.GetProperty(element).GetProperty("reference").GetString().ShouldNotBeNull();

    /// <summary>The front office admits the patient under the doctor to the bed.</summary>
    private async Task<AdmissionCreated> AdmitAsync(ApiClient frontOffice, Guid patientId, Guid bedId)
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        return await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bedId, Type: AdmissionType.Emergency, Source: AdmissionSource.Emergency, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Fever with a rash", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null, NotifyPatient: false));
    }

    /// <summary>A new facility of the hospital, saved as its masters screen saves one. It stays: the server is shared by the run's tests.</summary>
    private async Task<(Guid Id, string Code)> FacilityAsync(bool isActive)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var code = string.Concat(Enumerable.Range(0, 6).Select(_ => (char)('A' + Random.Shared.Next(26))));
        var id = (await admin.PostAsync<Created>("/api/v1/masters/facilities", new SaveFacilityRequest(
            code, "Nana Hospitals Annexe " + code, null, new Address { City = "Chennai", PinCode = "600002" },
            null, null, null, null, null, null, null, null, null, null, isActive, null))).Id;
        return (id, code);
    }

    /// <summary>A general ward of the facility, as the ward master saves one.</summary>
    private static Ward NewWard(Guid facilityId)
    {
        var code = "Z" + string.Concat(Enumerable.Range(0, 5).Select(_ => (char)('A' + Random.Shared.Next(26))));
        return new Ward { FacilityId = facilityId, Code = code, Name = "Ward " + code, Type = WardType.General };
    }

    private async Task<List<AuditEvent>> DirectoryAuditAsync(string type, string id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(a => a.EntityType == "fhir." + type && a.EntityId == id)
            .ToListAsync(Ct);
    }
}
