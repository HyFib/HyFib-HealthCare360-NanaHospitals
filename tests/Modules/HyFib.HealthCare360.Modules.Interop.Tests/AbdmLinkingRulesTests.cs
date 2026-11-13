using HyFib.HealthCare360.Modules.Interop.Services;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// The rules of HIP-initiated linking that need no database: how long a kept link token is sent (six months, as NHA's
/// wrapper keeps it).
/// </summary>
public sealed class AbdmLinkingRulesTests
{
    private static readonly DateTimeOffset Issued = new(2026, 3, 31, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_link_token_is_sent_for_six_months_after_it_was_issued_and_not_after()
    {
        AbdmLinkingService.IsLinkTokenUsable(Issued, Issued).ShouldBeTrue();
        AbdmLinkingService.IsLinkTokenUsable(Issued, Issued.AddMonths(6).AddTicks(-1)).ShouldBeTrue();
        AbdmLinkingService.IsLinkTokenUsable(Issued, Issued.AddMonths(6)).ShouldBeFalse();
        AbdmLinkingService.IsLinkTokenUsable(Issued, Issued.AddYears(1)).ShouldBeFalse();
    }

    [Fact]
    public void Six_months_are_calendar_months_so_a_token_from_the_end_of_a_month_runs_to_the_end_of_a_short_one()
    {
        // 31 March + 6 months is 30 September: the token is not used on 30 September at the time it was issued.
        AbdmLinkingService.IsLinkTokenUsable(Issued, new DateTimeOffset(2026, 9, 30, 9, 59, 59, TimeSpan.Zero)).ShouldBeTrue();
        AbdmLinkingService.IsLinkTokenUsable(Issued, new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero)).ShouldBeFalse();
    }
}
