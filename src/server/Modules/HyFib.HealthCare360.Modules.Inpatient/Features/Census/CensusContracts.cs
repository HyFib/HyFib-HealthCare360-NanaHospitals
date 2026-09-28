using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.Census;

public sealed record WardCensus(Guid WardId, string Code, string Name, WardType Type, int BedsInService, CensusSummary Summary);

/// <summary>
/// Census and occupancy statistics for a period (WB-06, MIS bed occupancy / ALOS). Beds in service are today's active,
/// unblocked beds, which is the denominator hospitals use for a rolling occupancy rate.
/// </summary>
public sealed record CensusReport(
    DateOnly From,
    DateOnly To,
    int BedsInService,
    CensusSummary Hospital,
    IReadOnlyList<WardCensus> Wards,
    int HousekeepingTasksCompleted,
    double? AverageHousekeepingTurnaroundMinutes);
