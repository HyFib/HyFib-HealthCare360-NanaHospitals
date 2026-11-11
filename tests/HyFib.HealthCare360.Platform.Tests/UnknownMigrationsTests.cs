using HyFib.HealthCare360.Platform.Persistence;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// Which of a database's migrations this build does not know (<see cref="DatabaseInitializer.UnknownMigrations"/>): a newer
/// version applied them, and start-up stops on them (<see cref="DatabaseInitializer.NewerDatabaseMessage"/>).
/// </summary>
public sealed class UnknownMigrationsTests
{
    [Fact]
    public void A_database_with_only_this_builds_migrations_has_none_unknown() =>
        DatabaseInitializer.UnknownMigrations(["20260928_A", "20261001_B"], ["20260928_A", "20261001_B"]).ShouldBeEmpty();

    [Fact]
    public void Migrations_this_build_has_still_to_apply_are_not_unknown() =>
        DatabaseInitializer.UnknownMigrations(["20260928_A"], ["20260928_A", "20261001_B", "20261002_C"]).ShouldBeEmpty();

    [Fact]
    public void Migrations_a_newer_version_applied_are_unknown_in_the_order_they_were_applied() =>
        DatabaseInitializer.UnknownMigrations(["20260928_A", "20270101_X", "20270102_Y"], ["20260928_A"])
            .ShouldBe(["20270101_X", "20270102_Y"]);

    [Fact]
    public void Names_are_compared_exactly() =>
        DatabaseInitializer.UnknownMigrations(["20260928_a"], ["20260928_A"]).ShouldBe(["20260928_a"]);
}
