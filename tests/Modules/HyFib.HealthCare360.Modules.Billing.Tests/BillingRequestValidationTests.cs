using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.DoctorFees;
using HyFib.HealthCare360.Modules.Billing.Features.Inpatient;
using HyFib.HealthCare360.Platform.Http;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>SRS SEC-06 on the billing actions that carried no validator: a version, a package, and a day already begun.</summary>
public sealed class BillingRequestValidationTests
{
    [Theory]
    [InlineData("2026-10-23T19:00:00Z", "2026-10-24", true)] // 00:30 on the 24th in India: the 24th has begun
    [InlineData("2026-10-23T18:00:00Z", "2026-10-24", false)] // 23:30 on the 23rd in India
    [InlineData("2026-10-23T18:00:00Z", "2026-10-23", true)]
    [InlineData("2026-10-23T18:00:00Z", "2026-10-01", true)]
    public void An_interim_bill_covers_days_up_to_the_hospitals_today(string utcNow, string upTo, bool valid)
    {
        var validator = new CreateInterimBillRequestValidator(new FixedTimeProvider(DateTimeOffset.Parse(utcNow, System.Globalization.CultureInfo.InvariantCulture)));

        validator.Validate(new CreateInterimBillRequest(DateOnly.Parse(upTo, System.Globalization.CultureInfo.InvariantCulture))).IsValid.ShouldBe(valid);
        validator.Validate(new CreateInterimBillRequest(null)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_bill_action_says_which_version_it_saw()
    {
        new BillVersionRequestValidator().Validate(new BillVersionRequest(0)).Errors.ShouldHaveSingleItem().ErrorMessage
            .ShouldBe(ConcurrencyExtensions.VersionMissing);
        new BillVersionRequestValidator().Validate(new BillVersionRequest(4711)).IsValid.ShouldBeTrue();
        new PayoutVersionRequestValidator().Validate(new PayoutVersionRequest(0)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_package_is_one_from_the_list_or_none()
    {
        new SetBillPackageRequestValidator().Validate(new SetBillPackageRequest(null, 12)).IsValid.ShouldBeTrue();
        new SetBillPackageRequestValidator().Validate(new SetBillPackageRequest(Guid.Empty, 12)).IsValid.ShouldBeFalse();
        new AssignPackageRequestValidator().Validate(new AssignPackageRequest(Guid.NewGuid(), 12)).IsValid.ShouldBeTrue();
        new AssignPackageRequestValidator().Validate(new AssignPackageRequest(Guid.Empty, 0)).Errors.Select(e => e.PropertyName)
            .ShouldBe(["PackageId", "Version"], ignoreOrder: true);
    }
}
