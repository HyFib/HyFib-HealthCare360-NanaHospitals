using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

/// <summary>
/// ABHA at registration and when the record is edited (SRS EHR-08): the number must have a valid Verhoeff check digit
/// and the address must be an ABHA address, on both requests. Blank keeps nothing (registration) or leaves the number as
/// it is (update).
/// </summary>
public sealed class AbhaValidationTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("91-1234-5678-9010", "meera.k@abdm")]
    [InlineData("91123456789010", "meera.k@sbx")]
    public void Valid_abha_details_are_accepted(string? number, string? address)
    {
        new RegisterPatientRequestValidator(TimeProvider.System).Validate(Register(number, address)).IsValid.ShouldBeTrue();
        new UpdatePatientRequestValidator(TimeProvider.System).Validate(Update(number, address)).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("91-1234-5678-9012", null, "AbhaNumber")]
    [InlineData("9112345678901", null, "AbhaNumber")]
    [InlineData(null, "meera.k@gmail.com", "AbhaAddress")]
    [InlineData(null, "meera.k", "AbhaAddress")]
    public void A_mistyped_abha_number_or_an_address_that_is_not_an_abha_address_is_refused(string? number, string? address, string field)
    {
        new RegisterPatientRequestValidator(TimeProvider.System).Validate(Register(number, address)).Errors
            .ShouldHaveSingleItem().PropertyName.ShouldBe(field);
        new UpdatePatientRequestValidator(TimeProvider.System).Validate(Update(number, address)).Errors
            .ShouldHaveSingleItem().PropertyName.ShouldBe(field);
    }

    [Fact]
    public void The_message_tells_the_desk_to_check_the_digits() =>
        new RegisterPatientRequestValidator(TimeProvider.System).Validate(Register("91-1234-5678-9012", null)).Errors[0].ErrorMessage
            .ShouldContain("check digit");

    private static RegisterPatientRequest Register(string? number, string? address) => new(
        Title: null, FirstName: "Meera", MiddleName: null, LastName: "Kulkarni", Gender: Gender.Female, DateOfBirth: new DateOnly(1984, 4, 12),
        AgeYears: null, AgeMonths: null, Phone: null, AlternatePhone: null, Email: null, Address: new Address(), BloodGroup: null, MaritalStatus: null,
        GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelation: null,
        Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: number, AbhaAddress: address,
        AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false);

    private static UpdatePatientRequest Update(string? number, string? address) => new(
        Title: null, FirstName: "Meera", MiddleName: null, LastName: "Kulkarni", Gender: Gender.Female, DateOfBirth: new DateOnly(1984, 4, 12),
        AgeYears: null, AgeMonths: null, Phone: null, AlternatePhone: null, Email: null, Address: new Address(), BloodGroup: null, MaritalStatus: null,
        GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelation: null,
        Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: number, AbhaAddress: address,
        AadhaarNumber: null, PhotoFileId: null, IsVip: false, IsDeceased: false, DeceasedAt: null, Notes: null, IsActive: true, Version: 3,
        RemovePhoto: false);
}
