using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The data audit trail records complex properties part by part, and binary values by their length and SHA-256. An
/// edited address (of a facility, patient, employee or donor), a cardiology finding or a TTI result used to leave no trace
/// of what changed: EF lists complex properties apart from the entity's own, and only the latter were read. A binary value
/// (a prescription's DSC signature and the bytes it signed, which name the patient) used to be copied in full, as base64.
/// A text over 500 characters is cut to keep the row small; it used to be cut alone, so an edit past the cut showed the
/// same value before and after, and now carries its length and SHA-256 too.
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

    [Fact]
    public void A_long_text_edited_past_its_first_500_characters_shows_as_changed_without_being_copied()
    {
        // A letterhead's HTML, edited near its end: the trail used to show the first 500 characters on both sides, the same.
        using var db = ModelOnlyContext();
        var opening = new string('a', 500);
        var (was, now) = (opening + new string('b', 2_000), opening + new string('c', 2_001));
        var setting = new SystemSetting { Key = "print.letterhead", Value = was };
        db.Attach(setting);
        setting.Value = now;
        db.ChangeTracker.DetectChanges();

        var json = JsonSerializer.Serialize(UnitOfWorkInterceptor.AuditChanges(db.Entry(setting), "update")["value"], Web);

        var change = JsonNode.Parse(json)!;
        change["from"]!["start"]!.GetValue<string>().ShouldBe(opening + "…");
        change["to"]!["start"]!.GetValue<string>().ShouldBe(opening + "…", "The edit is past what is kept of the text.");
        (change["from"]!["length"]!.GetValue<int>(), change["to"]!["length"]!.GetValue<int>()).ShouldBe((2_500, 2_501));
        change["from"]!["sha256"]!.GetValue<string>().ShouldBe(Sha256(was));
        change["to"]!["sha256"]!.GetValue<string>().ShouldBe(Sha256(now));
        json.Length.ShouldBeLessThan(1_300, "Each side keeps 500 characters and a hash, not the whole text.");
    }

    [Fact]
    public void A_text_of_500_characters_is_kept_whole_and_a_longer_one_is_never_cut_inside_a_character()
    {
        using var db = ModelOnlyContext();
        var whole = new SystemSetting { Key = "ui.notice", Value = new string('n', 500) };
        var emoji = new SystemSetting { Key = "ui.banner", Value = new string('e', 499) + "\U0001F3E5" + new string('e', 100) };
        db.AddRange(whole, emoji);

        UnitOfWorkInterceptor.AuditChanges(db.Entry(whole), "create")["value"].ShouldBe(new string('n', 500));
        var cut = JsonNode.Parse(JsonSerializer.Serialize(UnitOfWorkInterceptor.AuditChanges(db.Entry(emoji), "create")["value"], Web))!;
        cut["start"]!.GetValue<string>().ShouldBe(new string('e', 499) + "…", "The hospital sign's two halves stay together, out of the part kept.");
        cut["length"]!.GetValue<int>().ShouldBe(601);
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    /// <summary>The platform's EF model with the Npgsql provider; entities are tracked, never saved.</summary>
    private static HospitalDbContext ModelOnlyContext() => new(
        new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=localhost;Database=hc360_model_check")
            .UseSnakeCaseNamingConvention()
            .Options,
        new ModuleCatalog([]));
}
