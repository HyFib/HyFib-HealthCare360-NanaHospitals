using System.Security.Cryptography;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The data audit trail records complex properties part by part, and binary values by their length and SHA-256. An
/// edited address (of a facility, patient, employee or donor), a cardiology finding or a TTI result used to leave no trace
/// of what changed: EF lists complex properties apart from the entity's own, and only the latter were read. A binary value
/// (a prescription's DSC signature and the bytes it signed, which name the patient) used to be copied in full, as base64.
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

    [Fact]
    public void A_binary_value_is_recorded_as_its_length_and_hash_never_its_bytes()
    {
        using var db = ModelOnlyContext();
        var body = "{\"patient\":\"Asha Rao\"}"u8.ToArray();
        var record = new IdempotencyRecord { Scope = "test", Key = "k", ResponseBody = body };
        db.Add(record);

        // As the interceptor writes the event's details.
        var json = JsonSerializer.Serialize(UnitOfWorkInterceptor.AuditChanges(db.Entry(record), "create"), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.ShouldContain($"\"responseBody\":{{\"bytes\":{body.Length},\"sha256\":\"{Convert.ToHexStringLower(SHA256.HashData(body))}\"}}");
        json.ShouldNotContain(Convert.ToBase64String(body));
        json.ShouldNotContain("Asha Rao");
    }

    /// <summary>The platform's EF model with the Npgsql provider; entities are tracked, never saved.</summary>
    private static HospitalDbContext ModelOnlyContext() => new(
        new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=localhost;Database=hc360_model_check")
            .UseSnakeCaseNamingConvention()
            .Options,
        new ModuleCatalog([]));
}
