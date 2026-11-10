using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// Access is given by two people: the rule, row by row. A grant waits for anyone else who may approve it; with nobody
/// else, access given to oneself is refused, a hospital that has never had two people who may approve grants (setting up)
/// gives it at once, and once it has had two only a Super Administrator gives it alone (break-glass). The integration tests
/// drive the same rule over the API, where the seeded hospital always has two approvers; the rows they cannot reach are here.
/// </summary>
public sealed class GrantDecisionTests
{
    private static readonly Guid Maker = Guid.Parse("0f000000-0000-0000-0000-00000000000a");
    private static readonly Guid Other = Guid.Parse("0f000000-0000-0000-0000-00000000000b");

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void Anyone_else_who_may_approve_it_is_asked(bool self, bool latched, bool makerHoldsAll)
    {
        GrantApprovals.Decide([Other], Maker, self, latched, makerHoldsAll).Value.ShouldBe(GrantDecision.Ask);
        GrantApprovals.Decide([Maker, Other], Maker, self, latched, makerHoldsAll).Value.ShouldBe(GrantDecision.Ask, "the maker is one of them, and another");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Nobody_gives_themselves_access_that_nobody_else_may_approve(bool latched, bool makerHoldsAll)
    {
        foreach (Guid[] mayApprove in new[] { new[] { Maker }, Array.Empty<Guid>() })
        {
            var refused = GrantApprovals.Decide(mayApprove, Maker, self: true, latched, makerHoldsAll);
            (refused.Error!.Code, refused.Error.Type).ShouldBe((GrantApprovals.SelfGrantCode, ErrorType.BusinessRule),
                "not while setting up, and not a Super Administrator either");
        }
    }

    [Fact]
    public void A_hospital_that_never_had_two_people_to_approve_grants_gives_them_at_once()
    {
        GrantApprovals.Decide([Maker], Maker, self: false, latched: false, makerHoldsAll: false).Value.ShouldBe(GrantDecision.GiveBeforeSecondApprover);
        GrantApprovals.Decide([], Maker, self: false, latched: false, makerHoldsAll: true).Value.ShouldBe(GrantDecision.GiveBeforeSecondApprover);
    }

    [Fact]
    public void Once_it_has_had_two_only_a_Super_Administrator_gives_access_alone()
    {
        GrantApprovals.Decide([Maker], Maker, self: false, latched: true, makerHoldsAll: true).Value.ShouldBe(GrantDecision.GiveBreakGlass);

        var refused = GrantApprovals.Decide([Maker], Maker, self: false, latched: true, makerHoldsAll: false);
        (refused.Error!.Code, refused.Error.Type).ShouldBe((GrantApprovals.NoSecondApproverCode, ErrorType.BusinessRule));
        GrantApprovals.Decide([], Maker, self: false, latched: true, makerHoldsAll: false).Error!.Code.ShouldBe(GrantApprovals.NoSecondApproverCode);
    }

    [Fact]
    public void Approvers_hold_the_permission_to_approve_grants_and_every_administrative_permission_given()
    {
        var superAdmin = Guid.Parse("0f000000-0000-0000-0000-000000000001");
        var hospitalAdmin = Guid.Parse("0f000000-0000-0000-0000-000000000002");
        var auditor = Guid.Parse("0f000000-0000-0000-0000-000000000003");
        (Guid, string)[] holdings =
        [
            (superAdmin, PermissionRegistry.Wildcard),
            (hospitalAdmin, PlatformPermissions.GrantsApprove),
            (hospitalAdmin, PlatformPermissions.AuditRead),
            (hospitalAdmin, PlatformPermissions.UsersManage),
            (auditor, PlatformPermissions.AuditRead),
        ];

        GrantApprovals.Approvers(holdings, []).ShouldBe([superAdmin, hospitalAdmin], ignoreOrder: true,
            "nothing administrative given: whoever may approve grants");
        GrantApprovals.Approvers(holdings, [PlatformPermissions.AuditRead]).ShouldBe([superAdmin, hospitalAdmin], ignoreOrder: true,
            "the auditor holds it but may not approve grants");
        GrantApprovals.Approvers(holdings, [PlatformPermissions.AuditRead, PlatformPermissions.BackupsManage]).ShouldBe([superAdmin],
            "only all-access holds every one of them");
        GrantApprovals.Approvers([], [PlatformPermissions.AuditRead]).ShouldBeEmpty();
    }

    [Fact]
    public void System_health_tells_of_a_break_glass_and_of_one_approver_left_once_there_were_two()
    {
        GrantApprovals.Warnings(breakGlass: 0, latched: false, approvers: 1).ShouldBeEmpty("a hospital setting up has one");
        GrantApprovals.Warnings(breakGlass: 0, latched: true, approvers: 2).ShouldBeEmpty();

        var alone = GrantApprovals.Warnings(breakGlass: 0, latched: true, approvers: 1).ShouldHaveSingleItem();
        (alone.Code, alone.Severity).ShouldBe((GrantApprovals.OneApproverCode, "warning"));
        alone.Message.ShouldContain(PlatformPermissions.GrantsApprove);
        GrantApprovals.Warnings(breakGlass: 0, latched: true, approvers: 0).ShouldHaveSingleItem().Code.ShouldBe(GrantApprovals.OneApproverCode);

        // A single break-glass grant is enough to tell.
        var breakGlass = GrantApprovals.Warnings(breakGlass: 1, latched: false, approvers: 0).ShouldHaveSingleItem();
        (breakGlass.Code, breakGlass.Severity).ShouldBe((GrantApprovals.AloneAction, "critical"), "critical, so IT on-call is told");
        breakGlass.Message.ShouldContain("1 time(s)");
        GrantApprovals.Warnings(breakGlass: 2, latched: false, approvers: 0).ShouldHaveSingleItem().Message.ShouldContain("2 time(s)");
    }
}
