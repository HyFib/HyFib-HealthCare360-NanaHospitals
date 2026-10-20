using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

/// <summary>
/// A date of birth is checked against the hospital's date (IST), which is a day ahead of UTC's between midnight and
/// 05:30: a baby born just after midnight is registered that night, not refused until morning.
/// </summary>
public sealed class DateOfBirthTests
{
    // 00:30 on 21 October in India, still 20 October in UTC.
    private static readonly TimeProvider JustAfterMidnight = new FixedTime(new DateTimeOffset(2026, 10, 20, 19, 0, 0, TimeSpan.Zero));
    private static readonly DateOnly IndiaToday = new(2026, 10, 21);

    [Fact]
    public void A_baby_born_just_after_midnight_is_registered_that_night()
    {
        new RegisterPatientRequestValidator(JustAfterMidnight).Validate(Register(IndiaToday)).IsValid.ShouldBeTrue();
        new UpdatePatientRequestValidator(JustAfterMidnight).Validate(Update(IndiaToday)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_date_of_birth_after_the_hospitals_today_is_refused()
    {
        var tomorrow = IndiaToday.AddDays(1);

        new RegisterPatientRequestValidator(JustAfterMidnight).Validate(Register(tomorrow)).Errors
            .ShouldContain(e => e.PropertyName == nameof(RegisterPatientRequest.DateOfBirth));
        new UpdatePatientRequestValidator(JustAfterMidnight).Validate(Update(tomorrow)).Errors
            .ShouldContain(e => e.PropertyName == nameof(UpdatePatientRequest.DateOfBirth));
    }

    private static RegisterPatientRequest Register(DateOnly dateOfBirth) => new(
        Title: null, FirstName: "Baby", MiddleName: null, LastName: "Rao", Gender: Gender.Female, DateOfBirth: dateOfBirth, AgeYears: null,
        AgeMonths: null, Phone: null, AlternatePhone: null, Email: null, Address: new Address(), BloodGroup: null, MaritalStatus: null, GuardianName: null,
        GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null,
        Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false,
        Notes: null, ConfirmedNotDuplicate: false);

    private static UpdatePatientRequest Update(DateOnly dateOfBirth) => new(
        Title: null, FirstName: "Baby", MiddleName: null, LastName: "Rao", Gender: Gender.Female, DateOfBirth: dateOfBirth, AgeYears: null,
        AgeMonths: null, Phone: null, AlternatePhone: null, Email: null, Address: new Address(), BloodGroup: null, MaritalStatus: null, GuardianName: null,
        GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null,
        Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false,
        IsDeceased: false, DeceasedAt: null, Notes: null, IsActive: true, Version: 3, RemovePhoto: false);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
