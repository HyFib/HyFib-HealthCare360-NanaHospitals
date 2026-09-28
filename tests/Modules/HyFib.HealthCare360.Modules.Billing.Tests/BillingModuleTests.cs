using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Security;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

public sealed class BillingModuleTests
{
    [Fact]
    public void Module_uses_its_schema_name_as_key() => new BillingModule().Name.ShouldBe("billing");

    [Fact]
    public void Permissions_are_unique_documented_and_follow_the_area_resource_action_convention()
    {
        var permissions = BillingPermissions.All.ToList();

        permissions.Select(p => p.Name).ShouldBeUnique();
        permissions.ShouldAllBe(p => p.Name.StartsWith("billing.") || p.Name.StartsWith("insurance.") || p.Name.StartsWith("doctorfees."));
        permissions.ShouldAllBe(p => !string.IsNullOrWhiteSpace(p.Description) && p.DefaultRoles != null && p.DefaultRoles.Length > 0);
    }

    [Theory]
    [InlineData(BillingPermissions.DiscountsApprove)]
    [InlineData(BillingPermissions.RefundsApprove)]
    [InlineData(BillingPermissions.BillsCancel)]
    [InlineData(BillingPermissions.PayoutsManage)]
    public void Approvals_are_not_granted_to_counter_roles_by_default(string permission)
    {
        var roles = BillingPermissions.All.Single(p => p.Name == permission).DefaultRoles!;

        roles.ShouldNotContain(SystemRoles.Cashier);
        roles.ShouldNotContain(SystemRoles.FrontOffice);
        roles.ShouldNotContain(SystemRoles.Billing);
    }

    [Theory]
    [InlineData(BillingPermissions.OpCreate)]
    [InlineData(BillingPermissions.PaymentsCollect)]
    public void Front_office_can_bill_and_collect_at_the_desk(string permission)
    {
        BillingPermissions.All.Single(p => p.Name == permission).DefaultRoles!.ShouldContain(SystemRoles.FrontOffice);
    }

    [Fact]
    public void Webhook_payload_yields_the_ids_to_check_with_the_gateway()
    {
        const string payload = """
            {"event":"payment.captured","payload":{"payment":{"entity":{"id":"pay_Nx12AbC","order_id":"order_Q9zY81","amount":50000,"status":"captured","notes":{"merchantReference":"0192f3c4a5b6"}}}}}
            """;

        var candidates = PaymentGatewayService.ExtractCandidates(payload);

        candidates.ShouldContain("pay_Nx12AbC");
        candidates.ShouldContain("order_Q9zY81");
        candidates.ShouldContain("0192f3c4a5b6");
        candidates.ShouldNotContain("50000");
    }

    [Fact]
    public void An_unreadable_webhook_payload_yields_nothing()
    {
        PaymentGatewayService.ExtractCandidates("not json").ShouldBeEmpty();
    }
}
