using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.SharedKernel;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>The package charge of a health checkup: posted at check-in, cancelled with the booking, never half-applied.</summary>
public sealed class PackageBillingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 3, 30, 0, TimeSpan.Zero);
    private static readonly Guid PackageServiceItemId = Guid.CreateVersion7();

    private readonly IChargePoster poster = Substitute.For<IChargePoster>();
    private readonly PackageBilling billing;

    public PackageBillingTests()
    {
        poster.PostAsync(Arg.Any<ChargeRequest>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result<Guid>.Ok(Guid.CreateVersion7())));
        poster.CancelAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Success()));
        billing = new PackageBilling(poster);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PackageBooking Booking() => new()
    {
        FacilityId = Guid.CreateVersion7(),
        BookingNumber = "NHHC26000042",
        PatientId = Guid.CreateVersion7(),
        PackageId = Guid.CreateVersion7(),
        PackageCode = "HC-EXEC",
        PackageName = "Executive Health Checkup",
        ScheduledDate = new DateOnly(2026, 9, 26),
    };

    private void BillingRefusesToCancel() =>
        poster.CancelAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Result>(Error.Rule(PackageBilling.ChargeOnFinalisedBill, "This service is on a finalized bill.")));

    [Fact]
    public async Task Check_in_posts_one_package_charge_keyed_by_the_booking()
    {
        var booking = Booking();
        var visitId = Guid.CreateVersion7();

        (await billing.CheckInAsync(booking, PackageServiceItemId, visitId, Now, Ct)).IsSuccess.ShouldBeTrue();

        booking.Status.ShouldBe(BookingStatus.CheckedIn);
        booking.VisitId.ShouldBe(visitId);
        await poster.Received(1).PostAsync(
            Arg.Is<ChargeRequest>(r => r.SourceModule == "healthcheck.package" && r.SourceReference == booking.Id.ToString()
                                       && r.ServiceItemId == PackageServiceItemId && r.Quantity == 1 && r.PatientId == booking.PatientId
                                       && r.VisitId == visitId && r.AdmissionId == null && r.PractitionerId == null && r.ServiceDate == Now
                                       && r.Description == "Executive Health Checkup (NHHC26000042)"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refused_charge_blocks_the_check_in()
    {
        poster.PostAsync(Arg.Any<ChargeRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<Guid>.Fail(Error.Rule("billing.service_inactive", "Executive Health Checkup is no longer offered."))));
        var booking = Booking();

        var result = await billing.CheckInAsync(booking, PackageServiceItemId, null, Now, Ct);

        result.Error!.Code.ShouldBe("billing.service_inactive");
        booking.Status.ShouldBe(BookingStatus.Booked);
        booking.CheckedInAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_package_without_a_billable_item_is_not_checked_in()
    {
        var booking = Booking();

        (await billing.CheckInAsync(booking, null, null, Now, Ct)).Error!.Code.ShouldBe("package.not_billable");

        booking.Status.ShouldBe(BookingStatus.Booked);
        await poster.DidNotReceive().PostAsync(Arg.Any<ChargeRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Only_a_booked_checkup_is_charged_at_check_in()
    {
        var booking = Booking();
        await billing.CheckInAsync(booking, PackageServiceItemId, null, Now, Ct);

        (await billing.CheckInAsync(booking, PackageServiceItemId, null, Now, Ct)).Error!.Code.ShouldBe("booking.not_booked");

        await poster.Received(1).PostAsync(Arg.Any<ChargeRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancelling_cancels_the_package_charge_then_the_booking()
    {
        var booking = Booking();
        await billing.CheckInAsync(booking, PackageServiceItemId, null, Now, Ct);

        (await billing.CancelAsync(booking, "  Patient unwell  ", Now, Ct)).IsSuccess.ShouldBeTrue();

        booking.Status.ShouldBe(BookingStatus.Cancelled);
        booking.CancelReason.ShouldBe("Patient unwell");
        await poster.Received(1).CancelAsync("healthcheck.package", booking.Id.ToString(), "Health checkup cancelled: Patient unwell", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_charge_on_a_finalised_bill_keeps_the_booking_unchanged()
    {
        var booking = Booking();
        await billing.CheckInAsync(booking, PackageServiceItemId, null, Now, Ct);
        BillingRefusesToCancel();

        var result = await billing.CancelAsync(booking, "Patient unwell", Now, Ct);

        result.Error!.Code.ShouldBe("booking.package_billed");
        result.Error.Type.ShouldBe(ErrorType.BusinessRule);
        result.Error.Message.ShouldContain("NHHC26000042");
        booking.Status.ShouldBe(BookingStatus.CheckedIn);
        booking.CancelledAt.ShouldBeNull();
        booking.CancelReason.ShouldBeNull();
    }

    [Fact]
    public async Task A_no_show_releases_any_charge()
    {
        var booking = Booking();

        (await billing.NoShowAsync(booking, Now, Ct)).IsSuccess.ShouldBeTrue();

        booking.Status.ShouldBe(BookingStatus.NoShow);
        await poster.Received(1).CancelAsync("healthcheck.package", booking.Id.ToString(), "Health checkup no-show", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refused_no_show_keeps_the_booking_unchanged()
    {
        var booking = Booking();
        BillingRefusesToCancel();

        (await billing.NoShowAsync(booking, Now, Ct)).Error!.Code.ShouldBe("booking.package_billed");

        booking.Status.ShouldBe(BookingStatus.Booked);
        booking.CancelledAt.ShouldBeNull();
    }

    [Fact]
    public async Task Billing_is_not_asked_when_the_booking_cannot_change()
    {
        var checkedIn = Booking();
        await billing.CheckInAsync(checkedIn, PackageServiceItemId, null, Now, Ct);
        var cancelled = Booking();
        cancelled.Cancel("Duplicate booking", Now);

        (await billing.NoShowAsync(checkedIn, Now, Ct)).Error!.Code.ShouldBe("booking.not_booked");
        (await billing.CancelAsync(cancelled, "Again", Now, Ct)).Error!.Code.ShouldBe("booking.closed");

        await poster.DidNotReceive().CancelAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Long_reasons_are_clipped_to_what_billing_stores()
    {
        var booking = Booking();

        (await billing.CancelAsync(booking, new string('x', 300), Now, Ct)).IsSuccess.ShouldBeTrue();

        await poster.Received(1).CancelAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Is<string>(r => r.Length == 300), Arg.Any<CancellationToken>());
        booking.CancelReason!.Length.ShouldBe(300);
    }
}
