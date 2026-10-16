using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

/// <summary>
/// ABHA at registration and when the record is edited (SRS EHR-08): the number must be 14 digits (it has no published
/// check digit, so any 14 digits will do) and the address must be an ABHA address, on both requests. Blank keeps nothing
/// (registration) or leaves the number as it is (update).
/// </summary>
public sealed class AbhaValidationTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("91-1234-5678-9010", "meera.k@abdm")]
    [InlineData("91123456789010", "meera.k@sbx")]
    [InlineData("21-1521-3828-3227", null)] // an example patient's number in the ABDM implementation guide
    [InlineData("91-1234-5678-9012", null)] // fails a Verhoeff check, which ABHA numbers do not have
    public void Valid_abha_details_are_accepted(string? number, string? address)
    {
        new RegisterPatientRequestValidator(TimeProvider.System).Validate(Register(number, address)).IsValid.ShouldBeTrue();
        new UpdatePatientRequestValidator(TimeProvider.System).Validate(Update(number, address)).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("9112345678901", null, "AbhaNumber")]
    [InlineData("91-1234-5678-901O", null, "AbhaNumber")]
    [InlineData(null, "meera.k@gmail.com", "AbhaAddress")]
    [InlineData(null, "meera.k", "AbhaAddress")]
    public void An_abha_number_that_is_not_14_digits_or_an_address_that_is_not_an_abha_address_is_refused(string? number, string? address, string field)
    {
        new RegisterPatientRequestValidator(TimeProvider.System).Validate(Register(number, address)).Errors
            .ShouldHaveSingleItem().PropertyName.ShouldBe(field);
        new UpdatePatientRequestValidator(TimeProvider.System).Validate(Update(number, address)).Errors
            .ShouldHaveSingleItem().PropertyName.ShouldBe(field);
    }

    [Fact]
    public void The_message_asks_for_the_14_digits() =>
        new RegisterPatientRequestValidator(TimeProvider.System).Validate(Register("91-1234-5678-901", null)).Errors[0].ErrorMessage
            .ShouldBe("Enter a valid 14-digit ABHA number.");

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
