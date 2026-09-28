using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Features.Incidents;

namespace HyFib.HealthCare360.Modules.Quality.Tests;

/// <summary>Enums are stored as text, so the severity sort must rank by harm explicitly (never alphabetically).</summary>
public sealed class IncidentSortTests
{
    [Fact]
    public void Severity_sort_ranks_by_harm_not_by_name()
    {
        var rank = IncidentEndpoints.SortKeys["severity"].Compile();
        var incidents = Enum.GetValues<IncidentSeverity>().Reverse().Select(s => new Incident { Severity = s }).ToList();

        incidents.OrderBy(rank).Select(i => i.Severity).ShouldBe(
        [
            IncidentSeverity.NearMiss, IncidentSeverity.NoHarm, IncidentSeverity.Mild, IncidentSeverity.Moderate, IncidentSeverity.Severe,
            IncidentSeverity.Death,
        ]);
    }

    [Fact]
    public void Every_severity_has_its_own_rank()
    {
        var rank = IncidentEndpoints.SortKeys["severity"].Compile();

        Enum.GetValues<IncidentSeverity>().Select(s => rank(new Incident { Severity = s })).Distinct().Count()
            .ShouldBe(Enum.GetValues<IncidentSeverity>().Length);
    }
}
