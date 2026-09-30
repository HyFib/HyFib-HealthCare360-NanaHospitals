using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Services;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// A patient's data summary (DPDP Act 2023 s11) is built from <see cref="PersonalDataCatalogue"/>. These tests keep it
/// in step with the database: every column that holds a patient id is catalogued, every catalogue entry exists, and
/// every counted table can be counted by patient without reading the whole table.
/// </summary>
public sealed class PersonalDataCatalogueTests
{
    private static readonly Lazy<IModel> Model = new(() =>
    {
        using var scope = EndpointAuthorizationTests.App.Value.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Model;
    });

    [Fact]
    public void Every_column_that_holds_a_patient_id_is_in_the_catalogue()
    {
        var catalogued = PersonalDataCatalogue.Sources.Select(s => $"{s.Table}.{s.Column}").ToHashSet(StringComparer.Ordinal);

        PatientColumns().Where(c => !catalogued.Contains(c)).ShouldBeEmpty(
            "Add these to PersonalDataCatalogue, so a patient's data summary includes them");
    }

    [Fact]
    public void Every_catalogue_entry_is_a_real_column() =>
        PersonalDataCatalogue.Sources.Where(s => PersonalDataInventory.Resolve(Model.Value, s) is null)
            .Select(s => $"{s.Table}.{s.Column}")
            .ShouldBeEmpty("These catalogue entries name no table or column in the model");

    [Fact]
    public void Each_column_is_catalogued_once() =>
        PersonalDataCatalogue.Sources.GroupBy(s => $"{s.Table}.{s.Column}").Where(g => g.Count() > 1).Select(g => g.Key).ShouldBeEmpty();

    [Fact]
    public void Counted_tables_are_indexed_by_patient()
    {
        var unindexed = PersonalDataCatalogue.Sources
            .Where(s => s.Counted)
            .Select(s => (Source: s, Found: PersonalDataInventory.Resolve(Model.Value, s)))
            .Where(x => x.Found is { } found && !found.Entity.GetIndexes().Any(i => i.Properties[0] == found.Patient))
            .Select(x => x.Source.Table);

        unindexed.ShouldBeEmpty("Counting these by patient would read the whole table: index the patient column, or mark the source not counted");
    }

    [Fact]
    public void Category_keys_are_unique() =>
        PersonalDataCatalogue.Categories.Select(c => c.Key).ShouldBeUnique();

    private static IEnumerable<string> PatientColumns() =>
        from entity in Model.Value.GetEntityTypes()
        where !entity.IsOwned() && entity.GetTableName() is not null
        from property in entity.GetProperties()
        where (property.ClrType == typeof(Guid) || property.ClrType == typeof(Guid?))
              && property.GetColumnName().EndsWith("patient_id", StringComparison.Ordinal)
        select $"{entity.GetSchema()}.{entity.GetTableName()}.{property.GetColumnName()}";
}
