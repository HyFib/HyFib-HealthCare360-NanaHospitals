using HyFib.HealthCare360.Modules.Interop.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Search;

/// <summary>
/// Builds <c>searchset</c> bundles: <c>total</c>, a <c>self</c> link with the page size actually used, <c>previous</c> and
/// <c>next</c> links (offset paging with <c>_count</c> and <c>_offset</c>), one <c>match</c> entry per resource and an
/// optional <c>outcome</c> entry. Links repeat the search parameters, so search by identifier with POST
/// <c>_search</c> when identifiers should stay out of request lines; the links still carry them.
/// </summary>
internal static class SearchBundles
{
    public static R4.Bundle Build(string baseUrl, SearchRequest request, SearchPage page, DateTimeOffset now, R4.OperationOutcome? outcome = null)
    {
        var bundle = new R4.Bundle
        {
            Id = FhirIds.Of(Guid.CreateVersion7()),
            Type = R4.Bundle.BundleType.Searchset,
            Timestamp = FhirTime.ToMilliseconds(now),
            Total = page.Total,
        };

        bundle.Link.Add(new R4.Bundle.LinkComponent { Relation = "self", Url = Link(baseUrl, request, request.Offset) });
        if (request.Count > 0 && request.Offset > 0)
        {
            bundle.Link.Add(new R4.Bundle.LinkComponent { Relation = "previous", Url = Link(baseUrl, request, Math.Max(0, request.Offset - request.Count)) });
        }

        if (request.Count > 0 && (long)request.Offset + request.Count < page.Total)
        {
            bundle.Link.Add(new R4.Bundle.LinkComponent { Relation = "next", Url = Link(baseUrl, request, request.Offset + request.Count) });
        }

        foreach (var resource in page.Resources)
        {
            bundle.Entry.Add(new R4.Bundle.EntryComponent
            {
                FullUrl = $"{baseUrl}/{resource.TypeName}/{resource.Id}",
                Resource = resource,
                Search = new R4.Bundle.SearchComponent { Mode = R4.Bundle.SearchEntryMode.Match },
            });
        }

        if (outcome is not null)
        {
            bundle.Entry.Add(new R4.Bundle.EntryComponent
            {
                Resource = outcome,
                Search = new R4.Bundle.SearchComponent { Mode = R4.Bundle.SearchEntryMode.Outcome },
            });
        }

        return bundle;
    }

    /// <summary>The search URL for a page: the accepted parameters as sent, then <c>_count</c> and <c>_offset</c>.</summary>
    public static string Link(string baseUrl, SearchRequest request, int offset)
    {
        var query = request.LinkParameters
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}")
            .Append($"{SearchDefinitions.Count}={request.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (offset > 0)
        {
            query = query.Append($"{SearchDefinitions.Offset}={offset.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        return $"{baseUrl}/{request.Definition.Type}?{string.Join('&', query)}";
    }
}
