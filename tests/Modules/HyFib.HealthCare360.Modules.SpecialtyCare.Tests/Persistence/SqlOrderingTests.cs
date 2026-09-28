using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Common;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Persistence;

/// <summary>Enums are stored as text; ranks must order by the enum's declared order, not alphabetically.</summary>
public sealed class SqlOrderingTests
{
    [Fact]
    public void Rank_follows_the_declared_enum_order()
    {
        var rank = SqlOrdering.Rank<DialysisMachine, SeroStatus>(m => m.SeroDedication).Compile();

        Enum.GetValues<SeroStatus>().Select(s => rank(new DialysisMachine { SeroDedication = s })).ShouldBe([0, 1, 2, 3]);
    }

    [Fact]
    public void Ranked_sort_puts_values_in_clinical_not_alphabetical_order()
    {
        var shift = SqlOrdering.Rank<DialysisSession, DialysisShift>(s => s.Shift).Compile();
        var sessions = new[] { DialysisShift.Night, DialysisShift.Afternoon, DialysisShift.Morning, DialysisShift.Evening }
            .Select(s => new DialysisSession { Shift = s });

        sessions.OrderBy(shift).Select(s => s.Shift)
            .ShouldBe([DialysisShift.Morning, DialysisShift.Afternoon, DialysisShift.Evening, DialysisShift.Night]);
    }

    [Fact]
    public void Rank_translates_to_a_sql_case_over_the_stored_text_values()
    {
        var options = new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=localhost;Database=hc360_model_check")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new HospitalDbContext(options, new ModuleCatalog([new PatientsModule(), new SpecialtyCareModule()]));

        var sql = db.Set<BloodComponent>().OrderBy(SqlOrdering.Rank<BloodComponent, BloodComponentType>(c => c.ComponentType)).ToQueryString();

        sql.ShouldContain("CASE");
        sql.ShouldContain("'WholeBlood'");
        sql.ShouldContain("'Cryoprecipitate'");
    }
}
