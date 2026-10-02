using System.Text.RegularExpressions;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Safety;
using HyFib.HealthCare360.Modules.Clinical.Persistence;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Scheduling;
using HyFib.HealthCare360.Modules.SupplyChain;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>
/// Builds the real EF Core model (PostgreSQL provider, snake_case, platform conventions) without a database, so a
/// mapping mistake — a payload record turned into a table, an unbounded column, a patient delete that cascades into
/// the clinical record — fails here instead of when the migration is generated or on the first request.
/// </summary>
public sealed class ClinicalModelTests
{
    private static readonly Lazy<IModel> Model = new(() =>
    {
        using var db = CreateContext();

        // The design-time model keeps the provider annotations (index method, operator classes) the runtime model drops.
        return db.GetService<IDesignTimeModel>().Model;
    });

    /// <summary>
    /// A model-only context with Clinical and the modules it references. EF caches one model per context type, so every
    /// test in this assembly that needs a context must use this one module set.
    /// </summary>
    internal static HospitalDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=localhost;Database=hc360_model_check")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new HospitalDbContext(options, new ModuleCatalog([new PatientsModule(), new SchedulingModule(), new SupplyChainModule(), new ClinicalModule()]));
    }

    private static List<IEntityType> ClinicalEntities =>
    [
        .. Model.Value.GetEntityTypes()
            .Where(t => t.ClrType.Namespace?.StartsWith("HyFib.HealthCare360.Modules.Clinical", StringComparison.Ordinal) == true),
    ];

    [Fact]
    public void Only_domain_entities_become_tables_in_the_clinical_schema()
    {
        var entities = ClinicalEntities;

        entities.Count.ShouldBeGreaterThanOrEqualTo(34);
        entities.ShouldAllBe(t => typeof(Entity).IsAssignableFrom(t.ClrType) && !t.IsOwned() && t.FindPrimaryKey() != null);
        entities.ShouldAllBe(t => t.GetSchema() == ClinicalSchema.Name && Regex.IsMatch(t.GetTableName() ?? string.Empty, "^[a-z][a-z_]*[a-z]$"));
    }

    [Fact]
    public void Json_payloads_are_jsonb_columns_not_tables()
    {
        Model.Value.FindEntityType(typeof(Prescription))!.FindProperty(nameof(Prescription.SafetyAlerts))!.GetColumnType().ShouldBe("jsonb");
        Model.Value.FindEntityType(typeof(EncounterAmendment))!.FindProperty(nameof(EncounterAmendment.PreviousContent))!.GetColumnType().ShouldBe("jsonb");
        Model.Value.FindEntityType(typeof(DischargeSummaryAmendment))!.FindProperty(nameof(DischargeSummaryAmendment.PreviousContent))!
            .GetColumnType().ShouldBe("jsonb");

        Model.Value.FindEntityType(typeof(SafetyAlert)).ShouldBeNull();
        Model.Value.FindEntityType(typeof(EncounterContent)).ShouldBeNull();
        Model.Value.FindEntityType(typeof(DischargeSummaryContent)).ShouldBeNull();
    }

    [Fact]
    public void Text_columns_are_bounded_unless_deliberately_json_or_text()
    {
        var unbounded = ClinicalEntities
            .SelectMany(t => t.GetProperties())
            .Where(p => p.ClrType == typeof(string) && p.GetMaxLength() is null
                        && p.FindAnnotation(RelationalAnnotationNames.ColumnType)?.Value is not ("jsonb" or "text"))
            .Select(p => $"{p.DeclaringType.DisplayName()}.{p.Name}")
            .ToList();

        unbounded.ShouldBeEmpty();
    }

    [Fact]
    public void Clinical_rows_never_cascade_from_a_patient()
    {
        // Removing a patient row must fail loudly rather than silently take the clinical record with it.
        var patientKeys = ClinicalEntities
            .SelectMany(t => t.GetForeignKeys())
            .Where(fk => fk.PrincipalEntityType.ClrType == typeof(Patient))
            .ToList();

        patientKeys.Count.ShouldBeGreaterThanOrEqualTo(15);
        patientKeys.ShouldAllBe(fk => fk.DeleteBehavior == DeleteBehavior.Restrict);
    }

    [Fact]
    public void Platform_conventions_reach_every_clinical_entity()
    {
        foreach (var entity in ClinicalEntities)
        {
            var name = entity.DisplayName();

            if (typeof(IVersioned).IsAssignableFrom(entity.ClrType))
            {
                var version = entity.FindProperty(nameof(IVersioned.Version))!;
                version.GetColumnName().ShouldBe("xmin", name);
                version.IsConcurrencyToken.ShouldBeTrue(name);
            }

            if (typeof(ISoftDeletable).IsAssignableFrom(entity.ClrType))
            {
                entity.GetIndexes().ShouldContain(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(ISoftDeletable.IsDeleted), name);
            }

            if (typeof(IFacilityScoped).IsAssignableFrom(entity.ClrType))
            {
                entity.GetIndexes().ShouldContain(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(IFacilityScoped.FacilityId), name);
            }
        }
    }

    [Fact]
    public void Code_search_is_backed_by_trigram_indexes()
    {
        foreach (var type in new[] { typeof(IcdCode), typeof(ProcedureCode) })
        {
            var index = Model.Value.FindEntityType(type)!.GetIndexes()
                .Single(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(IcdCode.SearchText));

            index.GetMethod().ShouldBe("gin");
            index.GetOperators().ShouldNotBeNull().ShouldBe(["gin_trgm_ops"]);
        }
    }

    [Fact]
    public void Order_lines_always_request_at_least_one_unit()
    {
        var constraint = Model.Value.FindEntityType(typeof(ClinicalOrderItem))!.GetCheckConstraints().ShouldHaveSingleItem();

        constraint.ModelName.ShouldBe("ck_clinical_order_items_quantity");
        constraint.Sql.ShouldBe("quantity >= 1");
    }

    [Fact]
    public void A_patient_has_at_most_one_live_no_known_allergies_statement()
    {
        var entity = Model.Value.FindEntityType(typeof(NoKnownAllergiesStatement))!;
        var index = entity.GetIndexes().Single(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(NoKnownAllergiesStatement.PatientId));

        entity.GetTableName().ShouldBe("no_known_allergies");
        index.IsUnique.ShouldBeTrue();
        index.GetFilter().ShouldBe("is_deleted = false"); // removed statements stay as soft-deleted history
        index.GetDatabaseName().ShouldBe(NoKnownAllergiesStatementConfiguration.OnePerPatientIndex);
        entity.GetForeignKeys().Single().DeleteBehavior.ShouldBe(DeleteBehavior.Restrict);
        entity.FindProperty(nameof(NoKnownAllergiesStatement.RecordedByName))!.GetMaxLength().ShouldBe(200);
    }

    [Fact]
    public void Names_follow_the_snake_case_convention()
    {
        var prescription = Model.Value.FindEntityType(typeof(Prescription))!;

        prescription.GetTableName().ShouldBe("prescriptions");
        prescription.FindProperty(nameof(Prescription.PrescriptionNumber))!.GetColumnName().ShouldBe("prescription_number");
        Model.Value.FindEntityType(typeof(VitalSigns))!.GetTableName().ShouldBe("vital_signs");
    }
}
