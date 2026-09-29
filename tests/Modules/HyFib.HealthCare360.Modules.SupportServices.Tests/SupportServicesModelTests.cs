using HyFib.HealthCare360.Modules.HR;
using HyFib.HealthCare360.Modules.Inpatient;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Canteen;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace HyFib.HealthCare360.Modules.SupportServices.Tests;

/// <summary>Builds the EF Core model with the modules SupportServices depends on, so mapping errors surface before migrations.</summary>
public sealed class SupportServicesModelTests
{
    private static readonly Lazy<IModel> Model = new(() =>
    {
        var options = new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new HospitalDbContext(options, new ModuleCatalog([new PatientsModule(), new InpatientModule(), new HRModule(), new SupportServicesModule()]));
        return db.GetService<IDesignTimeModel>().Model;
    });

    [Theory]
    [InlineData(typeof(MenuItem), "menu_items")]
    [InlineData(typeof(CanteenSaleLine), "canteen_sale_lines")]
    public void Hsn_sac_codes_are_optional_columns_of_up_to_8_digits(Type entity, string table)
    {
        var entityType = Model.Value.FindEntityType(entity)!;
        var hsnSac = entityType.FindProperty(nameof(MenuItem.HsnSac))!;

        entityType.GetSchema().ShouldBe("supportservices");
        entityType.GetTableName().ShouldBe(table);
        hsnSac.GetColumnName().ShouldBe("hsn_sac");
        hsnSac.GetMaxLength().ShouldBe(8);
        hsnSac.IsNullable.ShouldBeTrue();
    }
}
