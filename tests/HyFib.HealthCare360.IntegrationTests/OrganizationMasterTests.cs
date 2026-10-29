using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The organisation masters over the real API: codes are permanent, references and images are checked when they are
/// chosen, the letterhead can be previewed before it is saved, and the facility staff are signed in to stays in use. The
/// records made here have codes of their own, and the extra facility is never in use, so other tests are not affected.
/// </summary>
public sealed class OrganizationMasterTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_code_is_permanent_and_a_new_reference_must_be_in_use()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var suffix = Letters(6);
        var specialty = await admin.PostAsync<Created>("/api/v1/masters/specialties", new SaveSpecialtyRequest($"SP{suffix}", "Sports medicine", null, true, null));
        var retired = await admin.PostAsync<Created>("/api/v1/masters/specialties", new SaveSpecialtyRequest($"RT{suffix}", "Retired speciality", null, false, null));
        var department = await admin.PostAsync<Created>("/api/v1/masters/departments", new SaveDepartmentRequest(
            $"D{suffix}", "Sports clinic", DepartmentKind.Clinical, specialty.Id, null, "Block B", null, null, 500, true, null));

        var saved = await admin.GetAsync<DepartmentDto>($"/api/v1/masters/departments/{department.Id}");
        saved.SpecialtyId.ShouldBe(specialty.Id);
        (await admin.GetAsync<SpecialtyDto>($"/api/v1/masters/specialties/{specialty.Id}")).Name.ShouldBe("Sports medicine");

        using (var renamed = await admin.SendRawAsync(HttpMethod.Put, $"/api/v1/masters/departments/{department.Id}", Edit(saved) with { Code = $"X{suffix}" }))
        {
            renamed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            // The problem JSON escapes the apostrophe, so the message is matched after it.
            (await renamed.Content.ReadAsStringAsync(Ct)).ShouldContain("be changed once it is saved");
        }

        using (var outOfUse = await admin.SendRawAsync(HttpMethod.Put, $"/api/v1/masters/departments/{department.Id}", Edit(saved) with { SpecialtyId = retired.Id }))
        {
            outOfUse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await outOfUse.Content.ReadAsStringAsync(Ct)).ShouldContain("specialtyId");
        }

        await admin.PutAsync($"/api/v1/masters/departments/{department.Id}", Edit(saved) with { Name = "Sports injury clinic" });
        (await admin.GetAsync<DepartmentDto>($"/api/v1/masters/departments/{department.Id}")).Name.ShouldBe("Sports injury clinic");
    }

    [Fact]
    public async Task A_logo_must_be_a_small_image_and_the_preview_shows_it_before_saving()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var name = "Nana Hospitals Annexe " + Letters(4);
        var facility = await admin.PostAsync<Created>("/api/v1/masters/facilities", new SaveFacilityRequest(
            Letters(6), name, null, new Address { City = "Chennai", PinCode = "600002" }, null, null, null, null, null, null, null, null,
            "NABH accredited", null, IsActive: false, null));
        var saved = await admin.GetAsync<FacilityDto>($"/api/v1/masters/facilities/{facility.Id}");

        var tooBig = await UploadAsync(admin, [.. PngSignature, .. new byte[310 * 1024]]);
        using (var refused = await admin.SendRawAsync(HttpMethod.Put, $"/api/v1/masters/facilities/{facility.Id}", Edit(saved) with { LogoFileId = tooBig }))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("300 KB");
        }

        // A patient's photo is stored without a patient id on the file; only an image uploaded as a logo is accepted.
        var patientStylePhoto = await UploadAsync(admin, TinyPng, category: "photo");
        using (var notALogo = await admin.SendRawAsync(HttpMethod.Put, $"/api/v1/masters/facilities/{facility.Id}", Edit(saved) with { LogoFileId = patientStylePhoto }))
        {
            notALogo.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await notALogo.Content.ReadAsStringAsync(Ct)).ShouldContain("only an image uploaded for it");
        }

        var logo = await UploadAsync(admin, TinyPng);
        var preview = await admin.PostAsync<PrintableDocumentDto>("/api/v1/masters/facilities/letterhead-preview", new LetterheadPreviewRequest(
            name, null, saved.Address, null, null, null, null, null, null, null, "NABH accredited", logo));
        preview.Html.ShouldContain(name);
        preview.Html.ShouldContain("data:image/png;base64,");
        preview.Html.ShouldContain("NABH accredited");

        await admin.PutAsync($"/api/v1/masters/facilities/{facility.Id}", Edit(saved) with { LogoFileId = logo });
        (await admin.GetAsync<FacilityDto>($"/api/v1/masters/facilities/{facility.Id}")).LogoFileId.ShouldBe(logo);
    }

    [Fact]
    public async Task The_facility_staff_are_signed_in_to_stays_in_use()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var me = await admin.GetAsync<Me>("/api/v1/auth/me");
        var current = await admin.GetAsync<FacilityDto>($"/api/v1/masters/facilities/{me.FacilityId}");

        using var response = await admin.SendRawAsync(HttpMethod.Put, $"/api/v1/masters/facilities/{current.Id}", Edit(current) with { IsActive = false });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("facility.current");
        (await admin.GetAsync<FacilityDto>($"/api/v1/masters/facilities/{current.Id}")).IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task A_facilitys_state_is_kept_as_gst_writes_it_and_one_of_no_state_is_refused()
    {
        // What the rate list's language is read from (Clinical Establishments Rules 2012, r.9(i)).
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var facility = await admin.PostAsync<Created>("/api/v1/masters/facilities", new SaveFacilityRequest(
            Letters(6), "Nana Hospitals Annexe " + Letters(4), null, new Address { City = "Chennai", State = "tamilnadu", PinCode = "600002" },
            null, null, null, null, null, null, null, null, null, null, IsActive: false, null));

        var saved = await admin.GetAsync<FacilityDto>($"/api/v1/masters/facilities/{facility.Id}");
        saved.Address.State.ShouldBe("Tamil Nadu");

        using var refused = await admin.SendRawAsync(
            HttpMethod.Put, $"/api/v1/masters/facilities/{facility.Id}", Edit(saved) with { Address = saved.Address with { State = "Tamizh" } });
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("state or union territory of India");
    }

    [Fact]
    public async Task A_value_too_long_for_its_column_is_a_field_error_not_a_server_error()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);

        using var response = await admin.SendRawAsync(HttpMethod.Post, "/api/v1/masters/services", new SaveServiceItemRequest(
            $"SV-{Letters(6)}", "Wound dressing", new string('s', 65), ServiceCategory.Procedure, null, 250m, 0m, "999312", true, false, null, true, null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("shortName");
    }

    private static SaveDepartmentRequest Edit(DepartmentDto d) => new(
        d.Code, d.Name, d.Kind, d.SpecialtyId, d.HeadPractitionerId, d.Location, d.Phone, d.TokenPrefix, d.DisplayOrder, d.IsActive, d.Version);

    private static SaveFacilityRequest Edit(FacilityDto f) => new(
        f.Code, f.Name, f.LegalName, f.Address, f.Phone, f.Email, f.Website, f.Gstin, f.Pan, f.RegistrationNumber, f.NabhNumber, f.HfrId,
        f.LetterheadNote, f.LogoFileId, f.IsActive, f.Version);

    private static async Task<Guid> UploadAsync(ApiClient client, byte[] png, string category = MasterImages.Logo)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "logo.png");
        form.Add(new StringContent(category), "category");
        using var response = await client.Http.PostAsync("/api/v1/files", form, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<Created>(ApiClient.Json, Ct))!.Id;
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('A' + Random.Shared.Next(26))));

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // A 1×1 transparent PNG.
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private sealed record Created(Guid Id);

    private sealed record Me(Guid? FacilityId);
}
