namespace HyFib.HealthCare360.Modules.Billing;

/// <summary>
/// Billing, insurance and doctor-fee permissions (<c>&lt;area&gt;.&lt;resource&gt;.&lt;action&gt;</c>). The defaults keep
/// duties separate: counters create bills and collect money, while cancellations, discounts above a user's limit,
/// refunds, credit notes and doctor payouts are approved by accounts/administration.
/// </summary>
public static class BillingPermissions
{
    public const string ChargesRead = "billing.charges.read";
    public const string ChargesManage = "billing.charges.manage";
    public const string OpCreate = "billing.op.create";
    public const string IpManage = "billing.ip.manage";
    public const string BillsFinalize = "billing.bills.finalize";
    public const string BillsCancel = "billing.bills.cancel";
    public const string DiscountsApprove = "billing.discounts.approve";
    public const string PaymentsCollect = "billing.payments.collect";
    public const string RefundsRequest = "billing.refunds.request";
    public const string RefundsApprove = "billing.refunds.approve";
    public const string Deposits = "billing.deposits";
    public const string Shifts = "billing.shifts";
    public const string TariffsManage = "billing.tariffs.manage";
    public const string TallyExport = "billing.tally.export";
    public const string PayersManage = "insurance.payers.manage";
    public const string PoliciesManage = "insurance.policies.manage";
    public const string PreAuthManage = "insurance.preauth.manage";
    public const string ClaimsManage = "insurance.claims.manage";
    public const string ShareRulesManage = "doctorfees.rules.manage";
    public const string PayoutsManage = "doctorfees.payouts.manage";

    private static readonly string[] Approvers = [SystemRoles.HospitalAdmin, SystemRoles.Accountant];

    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(ChargesRead, "Billing", "View charges, bills, receipts, IP accounts and billing reports",
            [SystemRoles.Billing, SystemRoles.Cashier, SystemRoles.InsuranceDesk, SystemRoles.Accountant, SystemRoles.HospitalAdmin]),
        new(ChargesManage, "Billing", "Post manual charges and cancel unbilled charges", [SystemRoles.Billing, SystemRoles.HospitalAdmin]),
        new(OpCreate, "Billing", "Create and edit draft OP bills (services, discounts within the user's limit)",
            [SystemRoles.Billing, SystemRoles.Cashier, SystemRoles.FrontOffice, SystemRoles.HospitalAdmin]),
        new(IpManage, "Billing", "Manage IP accounts: packages, interim and final bills, settlement",
            [SystemRoles.Billing, SystemRoles.InsuranceDesk, SystemRoles.HospitalAdmin]),
        new(BillsFinalize, "Billing", "Finalize bills (allocates the bill number)", [SystemRoles.Billing, SystemRoles.Cashier, SystemRoles.HospitalAdmin]),
        new(BillsCancel, "Billing", "Approve and perform the cancellation of finalized bills", Approvers),
        new(DiscountsApprove, "Billing", "Approve discounts above a user's limit and discharges with dues; set discount limits", Approvers),
        new(PaymentsCollect, "Billing", "Collect payments, POS and payment-link payments, pay out approved refunds",
            [SystemRoles.Cashier, SystemRoles.Billing, SystemRoles.FrontOffice, SystemRoles.HospitalAdmin]),
        new(RefundsRequest, "Billing", "Request refunds", [SystemRoles.Cashier, SystemRoles.Billing, SystemRoles.HospitalAdmin]),
        new(RefundsApprove, "Billing", "Approve refunds and issue credit notes and write-offs", Approvers),
        new(Deposits, "Billing", "Collect in-patient advances (deposits)", [SystemRoles.Cashier, SystemRoles.Billing, SystemRoles.HospitalAdmin]),
        new(Shifts, "Billing", "Open and close cashier shifts; day-end collection report",
            [SystemRoles.Cashier, SystemRoles.Billing, SystemRoles.FrontOffice, SystemRoles.Accountant, SystemRoles.HospitalAdmin]),
        new(TariffsManage, "Billing", "Maintain tariff plans, prices and packages", [SystemRoles.Billing, SystemRoles.HospitalAdmin]),
        new(TallyExport, "Billing", "Post and export the Tally day book", Approvers),
        new(PayersManage, "Insurance", "Maintain insurers, TPAs, corporates, schemes and non-payable items", [SystemRoles.InsuranceDesk, SystemRoles.HospitalAdmin]),
        new(PoliciesManage, "Insurance", "Record and verify patient insurance policies",
            [SystemRoles.InsuranceDesk, SystemRoles.Billing, SystemRoles.HospitalAdmin]),
        new(PreAuthManage, "Insurance", "Manage cashless pre-authorisations and enhancements", [SystemRoles.InsuranceDesk, SystemRoles.HospitalAdmin]),
        new(ClaimsManage, "Insurance", "Submit claims, record queries and settlements, view receivable ageing",
            [SystemRoles.InsuranceDesk, SystemRoles.Accountant, SystemRoles.HospitalAdmin]),
        new(ShareRulesManage, "Doctor fees", "Maintain doctor revenue-sharing rules", Approvers),
        new(PayoutsManage, "Doctor fees", "View accruals and statements; prepare, approve and pay doctor payouts", Approvers),
    ];
}
