using System.Text.Json;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The data audit trail records complex properties part by part. An edited address (of a facility, patient, employee or
/// donor), a cardiology finding or a TTI result used to leave no trace of what changed: EF lists complex properties apart
/// from the entity's own, and only the latter were read.
/// </summary>
public sealed class AuditChangesTests
{
    [Fact]
    public void An_edited_address_is_recorded_part_by_part_with_what_it_was()
    {
        using var db = ModelOnlyContext();
        var facility = new Facility { Code = "NH", Name = "Nana Hospitals", Address = new Address { City = "Chennai", PinCode = "600001" } };
        db.Attach(facility);

        facility.Address = facility.Address with { City = "Madurai" };
        facility.Name = "Nana Hospitals Madurai";
        db.ChangeTracker.DetectChanges();

        var changes = UnitOfWorkInterceptor.AuditChanges(db.Entry(facility), "update");

        changes.Keys.ShouldBe(["name", "address.city"], ignoreOrder: true);
        JsonSerializer.Serialize(changes["address.city"]).ShouldBe("""{"from":"Chennai","to":"Madurai"}""");
    }

    [Fact]
    public void An_untouched_address_is_left_out_of_an_update()
    {
        using var db = ModelOnlyContext();
        var facility = new Facility { Code = "NH", Name = "Nana Hospitals", Address = new Address { City = "Chennai" } };
        db.Attach(facility);

        facility.Name = "Nana Hospitals, Chennai";
        db.ChangeTracker.DetectChanges();

        UnitOfWorkInterceptor.AuditChanges(db.Entry(facility), "update").Keys.ShouldBe(["name"]);
    }

    [Fact]
    public void A_new_record_lists_its_address_parts_with_its_other_values()
    {
        using var db = ModelOnlyContext();
        var facility = new Facility { Code = "NH", Name = "Nana Hospitals", Address = new Address { City = "Chennai" } };
        db.Add(facility);

        var changes = UnitOfWorkInterceptor.AuditChanges(db.Entry(facility), "create");

        changes["code"].ShouldBe("NH");
        changes["address.city"].ShouldBe("Chennai");
        changes["address.country"].ShouldBe("India");
        changes.ShouldNotContainKey("version");
    }

    /// <summary>The platform's EF model with the Npgsql provider; entities are tracked, never saved.</summary>
    private static HospitalDbContext ModelOnlyContext() => new(
        new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=localhost;Database=hc360_model_check")
            .UseSnakeCaseNamingConvention()
            .Options,
        new ModuleCatalog([]));
}
