using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Billing;

/// <summary>The numbers the Billing module issues (administrators set their formats under Hospital setup).</summary>
public static class BillingNumberSeries
{
    /// <summary>OP and IP bills share one table, so their formats must differ.</summary>
    public static NumberSeriesDefinition OpBill { get; } =
        new("billing.bill.op", "OP bill", "Billing", "{FAC}OB{YY}{SEQ:7}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition IpBill { get; } =
        new("billing.bill.ip", "IP bill", "Billing", "{FAC}IB{YY}{SEQ:7}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition Receipt { get; } =
        new("billing.receipt", "Receipt", "Billing", "{FAC}RC{YY}{SEQ:7}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition Refund { get; } =
        new("billing.refund", "Refund", "Billing", "{FAC}RF{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition CreditNote { get; } =
        new("billing.creditnote", "Credit note", "Billing", "{FAC}CN{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition InsuranceClaim { get; } =
        new("insurance.claim", "Insurance claim", "Insurance", "{FAC}CL{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition PreAuthorisation { get; } =
        new("insurance.preauth", "Pre-authorisation", "Insurance", "{FAC}PA{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition DoctorPayout { get; } =
        new("doctorfees.payout", "Doctor payout", "Doctor fees", "{FAC}DP{YY}{SEQ:5}", NumberResetPolicy.Yearly, MaxLength: 24);

    internal static IEnumerable<NumberSeriesDefinition> All { get; } =
        [OpBill, IpBill, Receipt, Refund, CreditNote, InsuranceClaim, PreAuthorisation, DoctorPayout];
}
