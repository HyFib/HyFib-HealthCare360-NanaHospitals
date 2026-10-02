using HyFib.HealthCare360.Platform.Numbering;
using Microsoft.Extensions.Caching.Memory;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// Every number series is declared by its module, so administrators can set its format before its first number: one
/// that is not declared cannot issue numbers at all.
/// </summary>
public sealed class NumberSeriesCatalogTests
{
    private static readonly NumberSeriesDefinition Receipt =
        new("billing.receipt", "Receipt", "Billing", "{FAC}RC{YY}{SEQ:7}", NumberResetPolicy.Yearly, MaxLength: 24);

    [Fact]
    public void A_key_is_declared_once()
    {
        var copy = Receipt with { Name = "Payment receipt" };

        Should.Throw<InvalidOperationException>(() => new NumberSeriesCatalog([Receipt, copy]))
            .Message.ShouldContain("billing.receipt");
    }

    [Fact]
    public void Only_the_declared_definition_of_a_key_counts_as_declared()
    {
        var catalog = new NumberSeriesCatalog([Receipt]);

        catalog.IsDeclared(Receipt).ShouldBeTrue();
        catalog.IsDeclared(Receipt with { DefaultFormat = "RC{SEQ}" }).ShouldBeFalse("a definition made up at the call is not the declared one");
        catalog.Find("billing.receipt").ShouldBe(Receipt);
        catalog.Find("billing.unknown").ShouldBeNull();
    }

    [Fact]
    public async Task A_series_no_module_declares_cannot_issue_numbers()
    {
        var numbers = Service(new NumberSeriesCatalog([]));

        var refused = await Should.ThrowAsync<InvalidOperationException>(() => numbers.NextAsync(Receipt, Guid.NewGuid(), TestContext.Current.CancellationToken));

        refused.Message.ShouldContain("not declared");
    }

    [Fact]
    public async Task A_per_facility_series_needs_the_facility_it_counts_for()
    {
        var numbers = Service(new NumberSeriesCatalog([Receipt]));

        var refused = await Should.ThrowAsync<InvalidOperationException>(() => numbers.NextAsync(Receipt, null, TestContext.Current.CancellationToken));

        refused.Message.ShouldContain("per facility");
    }

    // The checks come before the database is touched, so the service needs none here.
    private static NumberSeriesService Service(NumberSeriesCatalog catalog) =>
        new(null!, catalog, new MemoryCache(new MemoryCacheOptions()), TimeProvider.System);
}
