namespace HyFib.HealthCare360.SharedKernel;

/// <summary>Normalised paging/sorting/search request. Page size is capped to protect the server and WiFi airtime.</summary>
public sealed record PageRequest
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 200;

    /// <summary>Nobody browses further than this (they search instead), and it keeps page × size far from overflowing.</summary>
    public const int MaxPage = 10_000;

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = DefaultPageSize;
    public string? Search { get; init; }
    public string? Sort { get; init; }
    public bool Desc { get; init; }

    public int Take => Math.Clamp(PageSize, 1, MaxPageSize);

    public int Skip => (Math.Clamp(Page, 1, MaxPage) - 1) * Take;

    public string? SearchTerm => string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();

    public static PageRequest Create(int? page, int? pageSize, string? search = null, string? sort = null, bool? desc = null) => new()
    {
        Page = Math.Clamp(page ?? 1, 1, MaxPage),
        PageSize = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize),
        Search = search,
        Sort = sort,
        Desc = desc ?? false,
    };
}

/// <summary>One page of results: <c>{ items, page, pageSize, totalCount }</c>.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public PagedResult<TOut> Map<TOut>(Func<T, TOut> map) => new(Items.Select(map).ToList(), Page, PageSize, TotalCount);

    public static PagedResult<T> Empty(PageRequest request) => new([], request.Page, request.Take, 0);
}
