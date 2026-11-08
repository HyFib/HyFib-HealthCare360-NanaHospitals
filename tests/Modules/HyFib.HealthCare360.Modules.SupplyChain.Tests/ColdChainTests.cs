namespace HyFib.HealthCare360.Modules.SupplyChain.Tests;

/// <summary>
/// The cold-chain log's rules: a reading out of range opens one excursion and later ones extend it until one is back in
/// range; a logger's late readings do not reopen anything; the twice-daily checks and their windows; logger keys.
/// </summary>
public sealed class ColdChainTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 8, 6, 30, 0, TimeSpan.Zero); // 12:00 IST

    private static ColdStorageUnit Fridge() => new() { Code = "F1", Name = "OP pharmacy fridge", MinCelsius = 2, MaxCelsius = 8 };

    private static TemperatureReading At(ColdStorageUnit unit, int minutes, decimal celsius, TemperatureSource source = TemperatureSource.Manual) =>
        TemperatureReading.For(unit, Noon.AddMinutes(minutes), celsius, source, Noon.AddMinutes(minutes));

    [Theory]
    [InlineData(2.0, false)]
    [InlineData(8.0, false)]
    [InlineData(8.04, false)] // read to one decimal: 8.0
    [InlineData(8.1, true)]
    [InlineData(1.9, true)]
    [InlineData(-0.5, true)]
    public void A_reading_is_out_of_range_beyond_the_units_limits_at_one_decimal(double celsius, bool outOfRange) =>
        At(Fridge(), 0, (decimal)celsius).OutOfRange.ShouldBe(outOfRange);

    [Fact]
    public void An_excursion_opens_once_widens_and_closes_at_the_first_reading_back_in_range()
    {
        var unit = Fridge();
        unit.Record(At(unit, 0, 5), null).ShouldBeNull();

        var excursion = unit.Record(At(unit, 10, 9.5m), null).ShouldNotBeNull();
        unit.OpenExcursionId.ShouldBe(excursion.Id);
        unit.Record(At(unit, 20, 11.2m), excursion).ShouldBeNull("the same excursion, not a second alert");
        (excursion.HighestCelsius, excursion.ReadingsOutOfRange, excursion.IsOpen).ShouldBe((11.2m, 2, true));
        excursion.ExtremeWords().ShouldBe("up to 11.2 °C");

        unit.Record(At(unit, 30, 6), excursion).ShouldBeNull();
        (excursion.IsOpen, excursion.EndedAt, unit.OpenExcursionId).ShouldBe((false, Noon.AddMinutes(30), (Guid?)null));
    }

    [Fact]
    public void A_late_logger_reading_is_kept_but_moves_nothing()
    {
        var unit = Fridge();
        unit.Record(At(unit, 60, 5, TemperatureSource.Logger), null);
        unit.Record(At(unit, 30, 12, TemperatureSource.Logger), null).ShouldBeNull("older than the last reading: no excursion opened now");
        (unit.LastCelsius, unit.LastReadingAt).ShouldBe((5m, Noon.AddMinutes(60)));
    }

    [Fact]
    public void Freezing_is_an_excursion_too()
    {
        var unit = Fridge();
        var excursion = unit.Record(At(unit, 0, -1), null).ShouldNotBeNull();
        excursion.ExtremeWords().ShouldBe("down to -1 °C");
    }

    [Fact]
    public void The_default_range_follows_the_kind_and_a_range_must_be_sensible()
    {
        ColdStorageUnit.DefaultRange(ColdUnitKind.Refrigerator).ShouldBe((2m, 8m));
        ColdStorageUnit.DefaultRange(ColdUnitKind.Freezer).ShouldBe((-25m, -15m));
        new ColdStorageUnit { MinCelsius = 8, MaxCelsius = 2 }.Validate().IsFailure.ShouldBeTrue();
    }

    [Theory]
    [InlineData(5, 59, null)]
    [InlineData(6, 0, ColdChainSlot.Morning)]
    [InlineData(10, 59, ColdChainSlot.Morning)]
    [InlineData(11, 0, null)]
    [InlineData(16, 0, ColdChainSlot.Evening)]
    [InlineData(20, 59, ColdChainSlot.Evening)]
    [InlineData(21, 0, null)]
    public void A_manual_reading_counts_for_the_check_whose_window_it_falls_in(int hour, int minute, ColdChainSlot? slot) =>
        ColdChainSchedule.SlotOf(new TimeOnly(hour, minute)).ShouldBe(slot);

    [Fact]
    public void The_checks_that_ended_between_two_times_are_listed_oldest_first()
    {
        // From 10:00 IST to 21:30 IST: the morning check ended at 11:00, the evening one at 21:00.
        var from = new DateTimeOffset(2026, 11, 8, 4, 30, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 11, 8, 16, 0, 0, TimeSpan.Zero);
        ColdChainSchedule.EndedBetween(from, to).Select(w => (w.Date, w.Slot)).ToList()
            .ShouldBe([(new DateOnly(2026, 11, 8), ColdChainSlot.Morning), (new DateOnly(2026, 11, 8), ColdChainSlot.Evening)]);
        ColdChainSchedule.EndedBetween(to, to.AddMinutes(10)).ShouldBeEmpty();
    }

    [Fact]
    public void A_logger_key_names_its_unit_and_is_checked_against_its_hash()
    {
        var unit = Fridge();
        var key = unit.IssueLoggerKey(Noon);
        ColdChainLoggerKeys.TryGetUnitId(key, out var unitId).ShouldBeTrue();
        unitId.ShouldBe(unit.Id);
        unit.AcceptsLogger(key).ShouldBeTrue();
        unit.AcceptsLogger(key + "x").ShouldBeFalse();
        unit.LoggerKeyHash.ShouldNotBe(key, "only the hash is kept");

        var replaced = unit.IssueLoggerKey(Noon);
        unit.AcceptsLogger(key).ShouldBeFalse("a new key stops the old one");
        unit.AcceptsLogger(replaced).ShouldBeTrue();
        unit.RevokeLoggerKey();
        unit.AcceptsLogger(replaced).ShouldBeFalse();
    }
}
