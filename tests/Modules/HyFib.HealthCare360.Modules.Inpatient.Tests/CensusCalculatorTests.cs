using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Tests;

public sealed class CensusCalculatorTests
{
    private static readonly Guid WardA = Guid.NewGuid();
    private static readonly Guid WardB = Guid.NewGuid();

    private static DateTimeOffset At(int day, int hour, int minute = 0) => IndiaClock.AtLocal(new DateOnly(2026, 9, day), new TimeOnly(hour, minute));

    private static DateOnly Day(int day) => new(2026, 9, day);

    private static StayRecord Stay(DateTimeOffset admitted, DateTimeOffset? discharged, AdmissionStatus status, params BedStay[] stays) =>
        new(Guid.NewGuid(), admitted, discharged, status, stays);

    [Fact]
    public void Occupancy_is_occupied_over_beds_in_service()
    {
        var counts = BedCounts.From([BedStatus.Occupied, BedStatus.Occupied, BedStatus.Vacant, BedStatus.Blocked, BedStatus.Maintenance]);

        counts.Total.ShouldBe(5);
        counts.Operational.ShouldBe(3);
        counts.OccupancyPercent.ShouldBe(66.7m);
    }

    [Fact]
    public void A_ward_with_no_beds_in_service_has_zero_occupancy() =>
        BedCounts.From([BedStatus.Blocked, BedStatus.Maintenance]).OccupancyPercent.ShouldBe(0m);

    [Fact]
    public void Counts_are_summed_across_wards()
    {
        var sum = BedCounts.Sum([BedCounts.From([BedStatus.Occupied]), BedCounts.From([BedStatus.Vacant, BedStatus.Cleaning])]);

        sum.ShouldBe(new BedCounts(3, 1, 1, 0, 1, 0, 0));
    }

    [Fact]
    public void Hospital_census_counts_movements_midnight_census_alos_and_occupancy()
    {
        StayRecord[] stays =
        [
            Stay(At(20, 10), At(22, 11), AdmissionStatus.Discharged, new BedStay(At(20, 10), At(22, 11), null, WardA)),
            Stay(At(21, 9), null, AdmissionStatus.Admitted, new BedStay(At(21, 9), null, null, WardA)),
            Stay(At(21, 12), At(21, 20), AdmissionStatus.Expired, new BedStay(At(21, 12), At(21, 20), null, WardB)),
        ];

        var census = CensusCalculator.Summarize(stays, Day(20), Day(22), bedsInService: 10);

        census.Days.Select(d => (d.Admissions, d.Discharges, d.Deaths, d.MidnightCensus)).ShouldBe([(1, 0, 0, 1), (2, 1, 1, 2), (0, 1, 0, 1)]);
        census.Admissions.ShouldBe(3);
        census.Discharges.ShouldBe(2);
        census.Deaths.ShouldBe(1);
        census.InpatientDays.ShouldBe(4);
        census.AverageLengthOfStay.ShouldBe(1.5m);
        census.BedOccupancyRate.ShouldBe(13.3m);
    }

    [Fact]
    public void Ward_census_follows_the_patient_through_transfers()
    {
        StayRecord[] stays =
        [
            Stay(At(20, 10), null, AdmissionStatus.Admitted,
                new BedStay(At(20, 10), At(21, 15), null, WardA),
                new BedStay(At(21, 15), null, null, WardB)),
        ];

        var wardA = CensusCalculator.Summarize(stays, Day(20), Day(21), bedsInService: 4, wardId: WardA);
        var wardB = CensusCalculator.Summarize(stays, Day(20), Day(21), bedsInService: 4, wardId: WardB);

        wardA.Days.Select(d => (d.Admissions, d.TransfersOut, d.MidnightCensus)).ShouldBe([(1, 0, 1), (0, 1, 0)]);
        wardB.Days.Select(d => (d.Admissions, d.TransfersIn, d.MidnightCensus)).ShouldBe([(0, 0, 0), (0, 1, 1)]);
    }

    [Fact]
    public void An_empty_period_has_no_alos_and_no_occupancy()
    {
        var census = CensusCalculator.Summarize([], Day(20), Day(26), bedsInService: 50);

        census.Days.Count.ShouldBe(7);
        census.AverageLengthOfStay.ShouldBe(0m);
        census.BedOccupancyRate.ShouldBe(0m);
    }
}
