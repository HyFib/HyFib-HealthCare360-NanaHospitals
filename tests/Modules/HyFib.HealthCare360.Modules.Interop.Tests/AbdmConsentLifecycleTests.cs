using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Services;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

public sealed class AbdmConsentLifecycleTests
{
    [Theory]
    [InlineData("REVOKED")]
    [InlineData("expired")]
    [InlineData("Denied")]
    public void An_ended_consent_stays_ended_when_a_grant_arrives_late(string current) =>
        AbdmConsentLifecycle.MayChange(current, "GRANTED").ShouldBeFalse();

    [Theory]
    [InlineData("GRANTED", "GRANTED")]
    [InlineData("GRANTED", "REVOKED")]
    [InlineData("GRANTED", "EXPIRED")]
    [InlineData("REQUESTED", "GRANTED")]
    [InlineData(null, "GRANTED")]
    public void A_live_consent_may_change(string? current, string next) =>
        AbdmConsentLifecycle.MayChange(current, next).ShouldBeTrue();

    [Fact]
    public void An_ended_consent_may_be_ended_again_the_same_way() => AbdmConsentLifecycle.MayChange("REVOKED", "REVOKED").ShouldBeTrue();

    [Theory]
    [InlineData(AbdmConsentRequestStatus.Revoked)]
    [InlineData(AbdmConsentRequestStatus.Expired)]
    [InlineData(AbdmConsentRequestStatus.Denied)]
    public void An_ended_consent_request_is_not_reopened_by_a_grant(AbdmConsentRequestStatus current) =>
        AbdmConsentLifecycle.MayChange(current, AbdmConsentRequestStatus.Granted).ShouldBeFalse();

    [Theory]
    [InlineData(AbdmConsentRequestStatus.Sent)]
    [InlineData(AbdmConsentRequestStatus.Requested)]
    [InlineData(AbdmConsentRequestStatus.Granted)]
    public void A_live_consent_request_may_be_granted(AbdmConsentRequestStatus current) =>
        AbdmConsentLifecycle.MayChange(current, AbdmConsentRequestStatus.Granted).ShouldBeTrue();
}
