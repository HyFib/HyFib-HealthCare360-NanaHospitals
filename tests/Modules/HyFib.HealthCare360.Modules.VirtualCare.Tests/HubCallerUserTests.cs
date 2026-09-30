using System.Security.Claims;
using HyFib.HealthCare360.Modules.VirtualCare.Hubs;
using HyFib.HealthCare360.Platform.Security;

namespace HyFib.HealthCare360.Modules.VirtualCare.Tests;

public sealed class HubCallerUserTests
{
    private static ClaimsPrincipal Staff(params Claim[] extra) => new(new ClaimsIdentity(
        [new(HcClaims.UserId, Guid.NewGuid().ToString()), new(HcClaims.UserType, HcClaims.StaffType), .. extra], "test"));

    [Fact]
    public void A_token_that_only_allows_finishing_sign_in_is_restricted() =>
        new HubCallerUser(Staff(new Claim(HcClaims.Restriction, "password-change"))).IsRestricted.ShouldBeTrue();

    [Fact]
    public void A_full_session_is_not_restricted() => new HubCallerUser(Staff()).IsRestricted.ShouldBeFalse();
}
