using HyFib.HealthCare360.Modules.SupportServices.Domain.Ambulance;

namespace HyFib.HealthCare360.Modules.SupportServices.Tests;

/// <summary>An ambulance trip: the right vehicle with its papers in force, each step in order, the kilometres from the odometer.</summary>
public sealed class AmbulanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 25);

    private static AmbulanceVehicle Vehicle(AmbulanceKind kind = AmbulanceKind.Bls, DateOnly? fitness = null) => new()
    {
        Id = Guid.NewGuid(),
        RegistrationNumber = "TN 49 AX 2071",
        Kind = kind,
        FitnessValidUntil = fitness ?? Today.AddMonths(6),
        InsuranceValidUntil = Today.AddMonths(3),
        PollutionValidUntil = Today.AddMonths(1),
        OdometerKm = 45_200,
    };

    private static AmbulanceTrip Trip(TripPurpose purpose = TripPurpose.EmergencyPickup, bool patient = true) => new()
    {
        Number = "NHAT26000001",
        Purpose = purpose,
        PatientId = patient ? Guid.NewGuid() : null,
        PatientName = "Senthil Kumar",
        RequestedAt = Now.AddMinutes(-20),
    };

    [Fact]
    public void A_vehicle_whose_papers_have_lapsed_or_are_missing_stays_in()
    {
        Vehicle(fitness: Today.AddDays(-1)).Lapsed(Today).ShouldBe(["fitness certificate"]);
        new AmbulanceVehicle { InsuranceValidUntil = Today, PollutionValidUntil = Today }.Lapsed(Today).ShouldBe(["fitness certificate"]);
        Trip().Dispatch(Vehicle(fitness: Today.AddDays(-1)), "Raju", "EMT Priya", Today, Now).Error!.Code.ShouldBe("ambulance.papers_lapsed");
    }

    [Theory]
    [InlineData(TripPurpose.EmergencyPickup, AmbulanceKind.PatientTransport, false)]
    [InlineData(TripPurpose.EmergencyPickup, AmbulanceKind.Als, true)]
    [InlineData(TripPurpose.BodyTransport, AmbulanceKind.Bls, false)]
    [InlineData(TripPurpose.BodyTransport, AmbulanceKind.Hearse, true)]
    [InlineData(TripPurpose.ScheduledTransport, AmbulanceKind.Hearse, false)]
    [InlineData(TripPurpose.InterFacilityTransfer, AmbulanceKind.PatientTransport, true)]
    public void Each_trip_goes_in_a_vehicle_suited_to_it(TripPurpose purpose, AmbulanceKind kind, bool suits) =>
        AmbulanceVehicle.Suits(kind, purpose).ShouldBe(suits);

    [Fact]
    public void An_emergency_goes_with_a_technician_or_paramedic()
    {
        Trip().Dispatch(Vehicle(), "Raju", null, Today, Now).Error!.Fields!.Keys.ShouldContain("crewName");
        Trip(TripPurpose.BodyTransport).Dispatch(Vehicle(AmbulanceKind.Hearse), "Raju", null, Today, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_trip_runs_in_order_and_its_kilometres_come_from_the_odometer()
    {
        var vehicle = Vehicle();
        var trip = Trip();
        trip.LeftScene(Now).Error!.Code.ShouldBe("ambulance.out_of_order");
        trip.Dispatch(vehicle, "Raju", "EMT Priya", Today, Now.AddMinutes(-18)).IsSuccess.ShouldBeTrue();
        trip.StartOdometerKm.ShouldBe(45_200);
        trip.Complete(vehicle, 45_230, null, Now).Error!.Code.ShouldBe("ambulance.not_transporting");
        trip.ReachedScene(Now.AddMinutes(-8)).IsSuccess.ShouldBeTrue();
        trip.LeftScene(Now.AddMinutes(-5)).IsSuccess.ShouldBeTrue();
        trip.Cancel("Not needed", Now).Error!.Code.ShouldBe("ambulance.transporting");

        trip.Complete(vehicle, 45_100, null, Now).Error!.Fields!.Keys.ShouldContain("endOdometerKm");
        trip.Complete(vehicle, 45_224, null, Now).IsSuccess.ShouldBeTrue();
        (trip.Status, trip.Kilometres, vehicle.OdometerKm).ShouldBe((TripStatus.Completed, 24, 45_224));
        trip.Chargeable(10).ShouldBe((24, 14));
    }

    [Fact]
    public void A_waived_trip_or_one_without_a_registered_patient_is_not_charged()
    {
        var vehicle = Vehicle();
        var trip = Trip(patient: false);
        trip.Dispatch(vehicle, "Raju", "EMT Priya", Today, Now).IsSuccess.ShouldBeTrue();
        trip.ReachedScene(Now).IsSuccess.ShouldBeTrue();
        trip.LeftScene(Now).IsSuccess.ShouldBeTrue();
        trip.Complete(vehicle, 45_206, null, Now).IsSuccess.ShouldBeTrue();
        trip.Chargeable(10).ShouldBeNull();

        var waived = Trip();
        waived.Dispatch(vehicle, "Raju", "EMT Priya", Today, Now).IsSuccess.ShouldBeTrue();
        waived.ReachedScene(Now).IsSuccess.ShouldBeTrue();
        waived.LeftScene(Now).IsSuccess.ShouldBeTrue();
        waived.Complete(vehicle, 45_210, "108 scheme transfer", Now).IsSuccess.ShouldBeTrue();
        waived.Chargeable(10).ShouldBeNull();
    }

    [Fact]
    public void A_trip_is_cancelled_with_a_reason_before_the_patient_is_on_board()
    {
        var trip = Trip();
        trip.Cancel(" ", Now).Error!.Fields!.Keys.ShouldContain("reason");
        trip.Cancel("Caller went by taxi", Now).IsSuccess.ShouldBeTrue();
        trip.Cancel("Again", Now).Error!.Code.ShouldBe("ambulance.closed");
    }
}
