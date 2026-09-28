using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Persistence;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Persistence;

/// <summary>
/// Builds the real EF Core model (PostgreSQL provider, snake_case) without a database, so mapping mistakes such as
/// unbounded strings or unsupported column types fail here rather than at migration time.
/// </summary>
public sealed class ModelConfigurationTests
{
    private static readonly Lazy<IModel> Model = new(() =>
    {
        var options = new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=localhost;Database=hc360_model_check")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new HospitalDbContext(options, new ModuleCatalog([new PatientsModule(), new SpecialtyCareModule()]));
        return db.Model;
    });

    private static IReadOnlyList<IEntityType> ModuleEntities =>
        Model.Value.GetEntityTypes().Where(t => t.ClrType.Namespace?.StartsWith("HyFib.HealthCare360.Modules.SpecialtyCare", StringComparison.Ordinal) == true).ToList();

    [Fact]
    public void Every_entity_lives_in_the_specialtycare_schema()
    {
        ModuleEntities.Count.ShouldBeGreaterThanOrEqualTo(20);
        ModuleEntities.ShouldAllBe(t => t.GetSchema() == SpecialtyCareSchema.Name);
    }

    [Fact]
    public void Every_text_column_is_bounded()
    {
        var unbounded = ModuleEntities
            .SelectMany(t => t.GetProperties().Concat(t.GetComplexProperties().SelectMany(c => c.ComplexType.GetProperties())))
            .Where(p => p.ClrType == typeof(string) && p.GetMaxLength() is null)
            .Select(p => $"{p.DeclaringType.DisplayName()}.{p.Name}")
            .ToList();

        unbounded.ShouldBeEmpty();
    }

    [Fact]
    public void Row_collections_are_stored_as_jsonb()
    {
        var booking = Model.Value.FindEntityType(typeof(SurgeryBooking))!;

        booking.FindProperty(nameof(SurgeryBooking.SafetyChecks))!.GetColumnType().ShouldBe("jsonb");
        booking.FindProperty(nameof(SurgeryBooking.PreOpChecks))!.GetColumnType().ShouldBe("jsonb");
        booking.FindProperty(nameof(SurgeryBooking.AssistantIds))!.ShouldNotBeNull();
    }

    [Fact]
    public void Tables_use_snake_case_names()
    {
        Model.Value.FindEntityType(typeof(SurgeryBooking))!.GetTableName().ShouldBe("surgery_bookings");
        Model.Value.FindEntityType(typeof(SurgeryBooking))!.FindProperty(nameof(SurgeryBooking.SignInCompletedAt))!
            .GetColumnName().ShouldBe("sign_in_completed_at");
    }

    [Fact]
    public void Json_rows_round_trip_with_enum_names()
    {
        List<SafetyChecklistEntry> rows = [new(SafetyPhase.TimeOut, "team_introduced", ChecklistItemState.Done, null, Guid.Empty, "Sr. Anita", DateTimeOffset.UnixEpoch)];

        var json = JsonColumn.Serialize(rows);

        json.ShouldContain("\"TimeOut\"");
        JsonColumn.Deserialize<SafetyChecklistEntry>(json).ShouldBe(rows);
        JsonColumn.Deserialize<SafetyChecklistEntry>(null).ShouldBeEmpty();
    }
}
