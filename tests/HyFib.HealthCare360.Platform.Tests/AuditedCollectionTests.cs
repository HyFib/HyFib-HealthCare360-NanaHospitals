using System.Text.Json.Nodes;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// Rows audited with their record (<see cref="AuditedWithParentAttribute"/>): replacing a collection (a coding's ICD codes,
/// a tariff's bed multipliers, a template's medicines) used to leave nothing in the trail but the record's own fields, so
/// the codes or amounts it had were gone. The record's data audit event now lists the collection as it was and as it is.
/// Checked on a small model of a CSSD tray and its instruments, without a database: the change tracker holds what a save
/// would write, and the interceptor says which audit events it would add.
/// </summary>
public sealed class AuditedCollectionTests
{
    [Fact]
    public void Replacing_a_trays_instruments_records_them_as_they_were_and_as_they_are()
    {
        using var db = new TrayContext();
        var tray = Loaded(db, NewTray("Major ortho", ("Bone cutter", 1), ("Artery forceps", 4)));

        tray.Instruments.Clear();
        tray.Instruments.Add(new TrayInstrument { TrayId = tray.Id, Name = "Artery forceps", Count = 6 });
        tray.Instruments.Add(new TrayInstrument { TrayId = tray.Id, Name = "Bone cutter", Count = 1 });

        var change = Interceptor().DataAuditEvents(db).ShouldHaveSingleItem("The rows are not audited apart from the tray.");

        (change.Action, change.EntityType, change.EntityId).ShouldBe(("Tray.update", "Tray", tray.Id.ToString()));
        Instruments(change)["from"].ShouldBe([("Artery forceps", 4), ("Bone cutter", 1)], ignoreOrder: true);
        Instruments(change)["to"].ShouldBe([("Artery forceps", 6), ("Bone cutter", 1)], ignoreOrder: true);
    }

    [Fact]
    public void Replacing_them_with_the_same_instruments_records_no_change()
    {
        using var db = new TrayContext();
        var tray = Loaded(db, NewTray("Major ortho", ("Bone cutter", 1), ("Artery forceps", 4)));

        // New rows (new ids) with the values the old ones had, as an edit screen that saves the whole list does.
        tray.Instruments.Clear();
        tray.Instruments.Add(new TrayInstrument { TrayId = tray.Id, Name = "Bone cutter", Count = 1 });
        tray.Instruments.Add(new TrayInstrument { TrayId = tray.Id, Name = "Artery forceps", Count = 4 });

        Interceptor().DataAuditEvents(db).ShouldBeEmpty();
    }

    [Fact]
    public void A_new_tray_lists_its_instruments_with_its_other_values()
    {
        using var db = new TrayContext();
        var tray = NewTray("Caesarean", ("Doyen retractor", 1));
        db.Add(tray);

        var change = Interceptor().DataAuditEvents(db).ShouldHaveSingleItem();

        change.Action.ShouldBe("Tray.create");
        var details = JsonNode.Parse(change.Details!)!;
        details["name"]!.GetValue<string>().ShouldBe("Caesarean");
        Rows(details["instruments"]!).ShouldBe([("Doyen retractor", 1)]);
    }

    [Fact]
    public void Instruments_added_to_a_tray_whose_instruments_were_not_loaded_are_recorded_as_added()
    {
        using var db = new TrayContext();
        var tray = NewTray("Minor set");
        db.Attach(tray);

        // What else the tray holds was never read, so the trail says only what certainly changed.
        db.Add(new TrayInstrument { TrayId = tray.Id, Name = "Needle holder", Count = 2 });

        var change = Interceptor().DataAuditEvents(db).ShouldHaveSingleItem();

        change.Action.ShouldBe("Tray.update");
        Instruments(change)["added"].ShouldBe([("Needle holder", 2)]);
        Instruments(change)["removed"].ShouldBeEmpty();
        Instruments(change).Keys.ShouldNotContain("from");
    }

    [Fact]
    public void An_instrument_removed_from_a_tray_that_was_not_read_names_the_tray()
    {
        using var db = new TrayContext();
        var trayId = Guid.CreateVersion7();
        var instrument = new TrayInstrument { TrayId = trayId, Name = "Sponge holder", Count = 2 };
        db.Attach(instrument);

        db.Remove(instrument);

        var change = Interceptor().DataAuditEvents(db).ShouldHaveSingleItem();

        (change.Action, change.EntityType, change.EntityId).ShouldBe(("Tray.update", "Tray", trayId.ToString()));
        Instruments(change)["removed"].ShouldBe([("Sponge holder", 2)]);
    }

    [Fact]
    public void A_changed_tray_records_its_own_values_and_its_instruments_in_one_event()
    {
        using var db = new TrayContext();
        var tray = Loaded(db, NewTray("Major ortho", ("Bone cutter", 1)));

        tray.Name = "Major orthopaedic";
        tray.Instruments.Single().Count = 2;

        var change = Interceptor().DataAuditEvents(db).ShouldHaveSingleItem();

        var details = JsonNode.Parse(change.Details!)!;
        details["name"]!["to"]!.GetValue<string>().ShouldBe("Major orthopaedic");
        Instruments(change)["from"].ShouldBe([("Bone cutter", 1)]);
        Instruments(change)["to"].ShouldBe([("Bone cutter", 2)]);
    }

    [Fact]
    public void A_facility_given_to_a_user_is_recorded_by_the_facility_the_grant_names()
    {
        // A grant's key is the user and the facility: the facility is what the trail must say.
        using var db = Hospital();
        var (main, annexe) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var user = new User { UserName = "anu", NormalizedUserName = "anu", DisplayName = "Anu" };
        user.Facilities.Add(new UserFacility { UserId = user.Id, FacilityId = main });
        db.Attach(user);
        db.Entry(user).Collection(u => u.Facilities).IsLoaded = true;

        user.Facilities.Add(new UserFacility { UserId = user.Id, FacilityId = annexe });

        var change = Interceptor().DataAuditEvents(db).ShouldHaveSingleItem();
        change.Action.ShouldBe("User.update");
        var facilities = JsonNode.Parse(change.Details!)!["facilities"]!;
        facilities["from"]!.AsArray().Select(f => f!["facilityId"]!.GetValue<Guid>()).ShouldBe([main]);
        facilities["to"]!.AsArray().Select(f => f!["facilityId"]!.GetValue<Guid>()).ShouldBe([main, annexe], ignoreOrder: true);
    }

    [Fact]
    public void Roles_and_facilities_changed_in_one_save_are_one_event_of_the_user()
    {
        using var db = Hospital();
        var (nurse, wardIncharge, main, annexe) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var user = new User { UserName = "anu", NormalizedUserName = "anu", DisplayName = "Anu" };
        user.Roles.Add(new UserRole { UserId = user.Id, RoleId = nurse });
        user.Facilities.Add(new UserFacility { UserId = user.Id, FacilityId = main });
        db.Attach(user);
        db.Entry(user).Collection(u => u.Roles).IsLoaded = true;
        db.Entry(user).Collection(u => u.Facilities).IsLoaded = true;

        user.Roles.Add(new UserRole { UserId = user.Id, RoleId = wardIncharge });
        user.Facilities.Add(new UserFacility { UserId = user.Id, FacilityId = annexe });

        var change = Interceptor().DataAuditEvents(db).ShouldHaveSingleItem("One event a user, whichever of its collections changed.");
        change.Action.ShouldBe("User.update");
        var details = JsonNode.Parse(change.Details!)!;
        details["roles"]!["to"]!.AsArray().Select(r => r!["roleId"]!.GetValue<Guid>()).ShouldBe([nurse, wardIncharge], ignoreOrder: true);
        details["facilities"]!["to"]!.AsArray().Select(f => f!["facilityId"]!.GetValue<Guid>()).ShouldBe([main, annexe], ignoreOrder: true);
    }

    [Fact]
    public void Roles_and_facilities_given_to_a_user_that_was_not_read_are_one_event_naming_the_user()
    {
        using var db = Hospital();
        var (userId, role, facility) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        db.Add(new UserRole { UserId = userId, RoleId = role });
        db.Add(new UserFacility { UserId = userId, FacilityId = facility });

        var change = Interceptor().DataAuditEvents(db).ShouldHaveSingleItem("One event a user, whichever of its collections changed.");

        (change.Action, change.EntityType, change.EntityId).ShouldBe(("User.update", "User", userId.ToString()));
        var details = JsonNode.Parse(change.Details!)!;
        details["roles"]!["added"]!.AsArray().Select(r => r!["roleId"]!.GetValue<Guid>()).ShouldBe([role]);
        details["facilities"]!["added"]!.AsArray().Select(f => f!["facilityId"]!.GetValue<Guid>()).ShouldBe([facility]);
    }

    /// <summary>The hospital's model, without a database: enough for the change tracker and the interceptor.</summary>
    private static HospitalDbContext Hospital() => new(
        new DbContextOptionsBuilder<HospitalDbContext>().UseNpgsql("Host=localhost;Database=hc360_model_check").UseSnakeCaseNamingConvention().Options,
        new ModuleCatalog([]));

    private static Tray NewTray(string name, params (string Name, int Count)[] instruments)
    {
        var tray = new Tray { Name = name };
        tray.Instruments.AddRange(instruments.Select(i => new TrayInstrument { TrayId = tray.Id, Name = i.Name, Count = i.Count }));
        return tray;
    }

    /// <summary>The tray as a query with its instruments included leaves it: tracked, and its collection loaded.</summary>
    private static Tray Loaded(TrayContext db, Tray tray)
    {
        db.Attach(tray);
        db.Entry(tray).Collection(t => t.Instruments).IsLoaded = true;
        return tray;
    }

    private static Dictionary<string, List<(string Name, int Count)>> Instruments(AuditEvent change) =>
        JsonNode.Parse(change.Details!)!["instruments"]!.AsObject().ToDictionary(p => p.Key, p => Rows(p.Value!));

    private static List<(string Name, int Count)> Rows(JsonNode rows) =>
        [.. rows.AsArray().Select(r => (r!["name"]!.GetValue<string>(), r["count"]!.GetValue<int>()))];

    private static UnitOfWorkInterceptor Interceptor() =>
        new(Substitute.For<ICurrentUser>(), TimeProvider.System, new DomainEventDispatcher(new ServiceCollection().BuildServiceProvider()));

    private sealed class Tray : AuditableEntity
    {
        public string Name { get; set; } = string.Empty;

        public List<TrayInstrument> Instruments { get; set; } = [];
    }

    [AuditedWithParent]
    private sealed class TrayInstrument : Entity
    {
        public Guid TrayId { get; set; }

        public string Name { get; set; } = string.Empty;

        public int Count { get; set; }
    }

    private sealed class TrayContext : DbContext
    {
        public TrayContext()
            : base(new DbContextOptionsBuilder<TrayContext>().UseNpgsql("Host=localhost;Database=hc360_model_check").UseSnakeCaseNamingConvention().Options)
        {
        }

        // As ModelConventions has it for the hospital's model: ids are made with the row, so a row added to a loaded
        // tray is inserted, not taken for one that exists.
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Tray>().Property(t => t.Id).ValueGeneratedNever();
            modelBuilder.Entity<Tray>().HasMany(t => t.Instruments).WithOne().HasForeignKey(i => i.TrayId);
            modelBuilder.Entity<TrayInstrument>().Property(i => i.Id).ValueGeneratedNever();
        }
    }
}
