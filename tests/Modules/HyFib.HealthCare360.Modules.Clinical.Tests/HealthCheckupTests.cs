using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

public sealed class HealthCheckupTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 3, 30, 0, TimeSpan.Zero);

    private static HealthPackage WomensPackage() => new() { Code = "HC-WOMEN", Name = "Women's Wellness", Gender = PackageGender.Female, MinAgeYears = 18 };

    private static PackageBooking Booking()
    {
        var booking = new PackageBooking { BookingNumber = "NHHC26000001", PatientId = Guid.CreateVersion7(), PackageCode = "HC-BASIC", PackageName = "Basic" };
        booking.PlanItinerary(
        [
            new ItineraryStop(null, "Laboratory", "Sample collection: Complete blood count, Fasting blood sugar"),
            new ItineraryStop(null, "General Medicine", "Consultation — General Medicine"),
        ]);
        return booking;
    }

    [Fact]
    public void Package_rules_check_sex_and_age()
    {
        var package = WomensPackage();

        package.CheckEligibility(Gender.Male, 30).Error!.Code.ShouldBe("package.gender");
        package.CheckEligibility(Gender.Female, 16).Error!.Code.ShouldBe("package.age");
        package.CheckEligibility(Gender.Female, null).Error!.Code.ShouldBe("package.age_unknown");
        package.CheckEligibility(Gender.Female, 30).IsSuccess.ShouldBeTrue();

        package.IsActive = false;
        package.CheckEligibility(Gender.Female, 30).Error!.Code.ShouldBe("package.inactive");
    }

    [Fact]
    public void Open_packages_accept_anyone() =>
        new HealthPackage { Code = "HC-BASIC", Name = "Basic" }.CheckEligibility(Gender.Unknown, null).IsSuccess.ShouldBeTrue();

    [Fact]
    public void Item_rules_limit_mammography_to_women_from_forty()
    {
        var mammography = new HealthPackageItem { ServiceCode = "RAD-MAMMO", Gender = PackageGender.Female, MinAgeYears = 40 };

        mammography.AppliesTo(Gender.Female, 35).ShouldBeFalse();
        mammography.AppliesTo(Gender.Female, 45).ShouldBeTrue();
        mammography.AppliesTo(Gender.Male, 45).ShouldBeFalse();
        mammography.AppliesTo(Gender.Female, null).ShouldBeTrue();
    }

    [Fact]
    public void Itinerary_is_numbered_in_visiting_order()
    {
        var booking = Booking();

        booking.Itinerary.Select(i => i.Sequence).ShouldBe([1, 2]);
        booking.Itinerary.ShouldAllBe(i => i.Status == ItineraryStatus.Pending && i.BookingId == booking.Id);
    }

    [Fact]
    public void Checkup_day_runs_from_check_in_to_completion()
    {
        var booking = Booking();
        var lab = booking.Itinerary[0];
        var consultation = booking.Itinerary[1];

        booking.CompleteStop(lab.Id, null, null, Now).Error!.Code.ShouldBe("booking.not_started");
        booking.CheckIn(Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        booking.CheckIn(null, Now).Error!.Code.ShouldBe("booking.not_booked");

        booking.CompleteStop(lab.Id, null, "Fasting sample taken", Now).IsSuccess.ShouldBeTrue();
        booking.Status.ShouldBe(BookingStatus.InProgress);
        booking.CompleteStop(lab.Id, null, null, Now).Error!.Code.ShouldBe("itinerary.closed");
        booking.Complete(Now).Error!.Code.ShouldBe("booking.pending_stops");

        booking.SkipStop(consultation.Id, null, "Doctor on leave; tele-review booked", Now).IsSuccess.ShouldBeTrue();
        booking.Complete(Now).Error!.Code.ShouldBe("booking.summary_missing");

        booking.RecordSummary("All parameters within normal limits.", "Repeat annually.", Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        booking.Complete(Now).IsSuccess.ShouldBeTrue();

        booking.Status.ShouldBe(BookingStatus.Completed);
        booking.CompletedAt.ShouldBe(Now);
        booking.DomainEvents.OfType<HealthCheckupCompleted>().ShouldHaveSingleItem().BookingId.ShouldBe(booking.Id);
        booking.Cancel("Too late", Now).Error!.Code.ShouldBe("booking.closed");
        booking.RecordSummary("Changed", null, Guid.CreateVersion7(), Now).Error!.Code.ShouldBe("booking.closed");
    }

    [Fact]
    public void Only_untouched_bookings_become_no_shows()
    {
        var absent = Booking();
        absent.MarkNoShow(Now).IsSuccess.ShouldBeTrue();
        absent.Status.ShouldBe(BookingStatus.NoShow);
        absent.IsOpen.ShouldBeFalse();

        var arrived = Booking();
        arrived.CheckIn(null, Now);
        arrived.MarkNoShow(Now).Error!.Code.ShouldBe("booking.not_booked");
        arrived.Cancel("Patient unwell", Now).IsSuccess.ShouldBeTrue();
        arrived.CancelReason.ShouldBe("Patient unwell");
    }
}
