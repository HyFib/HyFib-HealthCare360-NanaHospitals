using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Tests;

public sealed class PageRequestTests
{
    [Fact]
    public void A_page_number_far_beyond_any_list_neither_overflows_nor_fails()
    {
        var request = PageRequest.Create(page: 21_474_838, pageSize: 100);

        request.Page.ShouldBe(PageRequest.MaxPage);
        request.Skip.ShouldBe((PageRequest.MaxPage - 1) * 100);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(-5, 1)]
    [InlineData(3, 3)]
    public void Ordinary_page_numbers_are_kept(int? page, int expected) =>
        PageRequest.Create(page, pageSize: null).Page.ShouldBe(expected);
}
