using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Http;

/// <summary>
/// Standard list query-string parameters bound with <c>[AsParameters]</c>:
/// <c>?page=1&amp;pageSize=25&amp;search=&amp;sort=name&amp;desc=true</c>.
/// </summary>
public sealed record PageQuery(int? Page, int? PageSize, string? Search, string? Sort, bool? Desc)
{
    public PageRequest ToRequest() => PageRequest.Create(Page, PageSize, Search, Sort, Desc);
}

/// <summary>A lightweight option for dropdowns and look-ups.</summary>
public sealed record LookupItem(Guid Id, string Code, string Name);
