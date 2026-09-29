using System.Text.Json;
using System.Text.Json.Serialization;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

public sealed class PatientPhotoTests
{
    private static readonly Guid Current = Guid.CreateVersion7();
    private static readonly Guid Replacement = Guid.CreateVersion7();

    [Fact]
    public void A_save_that_says_nothing_about_the_photo_keeps_it() =>
        PatientPhoto.Resolve(Current, requested: null, remove: false).ShouldBe(Current);

    [Fact]
    public void A_new_photo_replaces_the_current_one() =>
        PatientPhoto.Resolve(Current, Replacement, remove: false).ShouldBe(Replacement);

    [Fact]
    public void A_photo_is_removed_only_when_the_save_asks_for_it()
    {
        PatientPhoto.Resolve(Current, requested: null, remove: true).ShouldBeNull();
        PatientPhoto.Resolve(current: null, requested: null, remove: false).ShouldBeNull();
    }

    [Fact]
    public void A_patient_without_a_photo_gets_the_first_one() =>
        PatientPhoto.Resolve(current: null, Replacement, remove: false).ShouldBe(Replacement);

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    public void An_image_in_the_photo_category_is_accepted(string contentType) =>
        PatientPhoto.Problem(File(contentType: contentType)).ShouldBeNull();

    [Fact]
    public void A_photo_may_be_up_to_5_MB() =>
        PatientPhoto.Problem(File(size: PatientPhoto.MaxBytes)).ShouldBeNull();

    [Fact]
    public void A_file_uploaded_for_something_else_is_refused()
    {
        // A lab report's scanned page is an image too, but it must not become a patient's photo.
        PatientPhoto.Problem(File(category: "lab")).ShouldNotBeNull().ShouldContain("not uploaded as a photo");
        PatientPhoto.Problem(File(category: "document")).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("application/dicom")]
    [InlineData("text/html")]
    public void A_file_that_is_not_an_image_is_refused(string contentType) =>
        PatientPhoto.Problem(File(contentType: contentType)).ShouldNotBeNull().ShouldContain("JPEG, PNG or WebP");

    [Fact]
    public void A_photo_over_5_MB_is_refused() =>
        PatientPhoto.Problem(File(size: PatientPhoto.MaxBytes + 1)).ShouldNotBeNull().ShouldContain("too large");

    [Fact]
    public void Registering_with_or_without_a_photo_is_valid()
    {
        RegisterValidator().Validate(Register(photo: null)).IsValid.ShouldBeTrue();
        RegisterValidator().Validate(Register(photo: Replacement)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void An_empty_photo_id_is_not_a_photo() =>
        RegisterValidator().Validate(Register(photo: Guid.Empty)).Errors.ShouldContain(e => e.PropertyName == "PhotoFileId");

    [Fact]
    public void Updating_without_a_photo_field_keeps_the_photo_and_is_valid()
    {
        // Clients that know nothing about photos still send nothing: the request stays valid and keeps the photo.
        var request = Update(photo: null, remove: false);

        UpdateValidator().Validate(request).IsValid.ShouldBeTrue();
        PatientPhoto.Resolve(Current, request.PhotoFileId, request.RemovePhoto).ShouldBe(Current);
    }

    [Fact]
    public void Updating_can_replace_or_remove_the_photo()
    {
        UpdateValidator().Validate(Update(photo: Replacement, remove: false)).IsValid.ShouldBeTrue();
        UpdateValidator().Validate(Update(photo: null, remove: true)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Updating_cannot_both_replace_and_remove_the_photo()
    {
        var result = UpdateValidator().Validate(Update(photo: Replacement, remove: true));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "photoFileId" && e.ErrorMessage.Contains("not both", StringComparison.Ordinal));
    }

    [Fact]
    public void The_json_of_a_client_that_predates_the_field_binds_to_keeping_the_photo()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

        var request = JsonSerializer.Deserialize<UpdatePatientRequest>(
            """{"firstName":"Asha","gender":"Female","dateOfBirth":"1990-01-15","isVip":false,"isDeceased":false,"isActive":true,"version":3}""",
            options);

        request.ShouldNotBeNull().RemovePhoto.ShouldBeFalse();
        request.PhotoFileId.ShouldBeNull();
    }

    private static StoredFile File(string category = "photo", string contentType = "image/jpeg", long size = 150_000) =>
        new() { Category = category, ContentType = contentType, SizeBytes = size };

    private static RegisterPatientRequestValidator RegisterValidator() => new(TimeProvider.System);

    private static UpdatePatientRequestValidator UpdateValidator() => new(TimeProvider.System);

    private static RegisterPatientRequest Register(Guid? photo) => new(
        Title: null, FirstName: "Asha", MiddleName: null, LastName: "Rao", Gender: Gender.Female, DateOfBirth: new DateOnly(1990, 1, 15), AgeYears: null,
        AgeMonths: null, Phone: null, AlternatePhone: null, Email: null, Address: new Address(), BloodGroup: null, MaritalStatus: null, GuardianName: null,
        GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null,
        Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: photo, IsVip: false,
        Notes: null, ConfirmedNotDuplicate: false);

    private static UpdatePatientRequest Update(Guid? photo, bool remove) => new(
        Title: null, FirstName: "Asha", MiddleName: null, LastName: "Rao", Gender: Gender.Female, DateOfBirth: new DateOnly(1990, 1, 15), AgeYears: null,
        AgeMonths: null, Phone: null, AlternatePhone: null, Email: null, Address: new Address(), BloodGroup: null, MaritalStatus: null, GuardianName: null,
        GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null,
        Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: photo, IsVip: false,
        IsDeceased: false, DeceasedAt: null, Notes: null, IsActive: true, Version: 3, RemovePhoto: remove);
}
