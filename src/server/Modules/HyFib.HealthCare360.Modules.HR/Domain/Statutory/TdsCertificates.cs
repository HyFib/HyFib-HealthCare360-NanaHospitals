using System.Globalization;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.HR.Domain.Statutory;

public sealed record CertificateParty(string Name, string? Pan, string? Tan, string? Address);

public sealed record QuarterTds(string Quarter, decimal AmountPaid, decimal TaxDeducted, decimal TaxDeposited);

public sealed record ChallanReference(string BsrCode, DateOnly DepositedOn, string ChallanSerial, decimal Amount);

public sealed record FeePayment(DateOnly PaidOn, string Section, decimal Amount, decimal Tds, string Reference);

/// <summary>
/// The printable tax certificates: Form 16 Part B (the salary computation, which the employer prepares, rule 31(1)(a)) and
/// a statement in the form of Form 16A for doctors' fees. Part A of Form 16 and Form 16A itself are valid only as
/// downloaded from TRACES (CBDT Circular 04/2013), so the Form 16A here is headed as HC360's statement for checking the
/// TRACES certificate against, not the certificate.
/// </summary>
public static class TdsCertificates
{
    private const string Styles =
        "table.c{width:100%;border-collapse:collapse;margin:6px 0;font-size:10pt}table.c td,table.c th{border:1px solid #999;padding:3px 6px;vertical-align:top}"
        + "table.c th{background:#f2f2f2;text-align:left}td.n,th.n{text-align:right;white-space:nowrap}tr.t td{font-weight:bold}.note{font-size:9pt;color:#333}";

    public static PrintableDocument Form16PartB(CertificateParty employer, CertificateParty employee, IncomeTaxYear year, DateOnly from, DateOnly to,
        IncomeTaxComputation c, IReadOnlyList<QuarterTds> quarters, string responsibleName, string responsibleDesignation, DateOnly issuedOn)
    {
        ArgumentNullException.ThrowIfNull(employer);
        ArgumentNullException.ThrowIfNull(employee);
        ArgumentNullException.ThrowIfNull(year);
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(quarters);
        var deducted = quarters.Sum(q => q.TaxDeducted);
        Html Row(string no, string label, decimal amount, bool total = false) =>
            Html.Of($"<tr class=\"{(total ? "t" : "")}\"><td>{no}</td><td>{label}</td><td class=\"n\">{Rupees(amount)}</td></tr>");

        var rows = new List<Html>
        {
            Row("1", "Gross salary: salary as per section 17(1)", c.GrossSalary),
            Row("2", "Less: allowances exempt under section 10 (house rent allowance, s.10(13A))", c.HouseRentExemption),
            Row("3", "Total salary (1 − 2)", c.GrossSalary - c.HouseRentExemption, true),
            Row("4(a)", "Standard deduction under section 16(ia)", c.StandardDeduction),
            Row("4(c)", "Tax on employment under section 16(iii)", c.ProfessionalTax),
            Row("6", "Income chargeable under the head \"Salaries\"", c.IncomeFromSalary, true),
            Row("7(a)", "Income (or admissible loss) from house property reported by the employee", -c.HousePropertyLoss),
            Row("7(b)", "Income under the head other sources reported by the employee", c.OtherIncome),
            Row("9", "Gross total income", c.GrossTotalIncome, true),
            Row("10(a)", "Deduction under section 80C (including provident fund)", c.Deduction80C),
            Row("10(e)", "Deduction under section 80CCD(1B)", c.Deduction80Ccd1B),
            Row("10(g)", "Deduction under section 80D", c.Deduction80D),
            Row("10(h)", "Deduction under section 80E", c.Deduction80E),
            Row("10(i)", "Deduction under section 80G", c.Deduction80G),
            Row("10(j)", "Deduction under section 80TTA", c.Deduction80Tta),
            Row("11", "Aggregate of deductions under Chapter VI-A", c.TotalDeductions, true),
            Row("12", "Total taxable income (9 − 11)", c.TaxableIncome, true),
            Row("13", "Tax on total income", c.TaxOnIncome),
            Row("14", "Rebate under section 87A", c.Rebate87A),
            Row("15", "Surcharge", c.Surcharge),
            Row("16", "Health and education cess", c.Cess),
            Row("17", "Tax payable (13 − 14 + 15 + 16)", c.TotalTax, true),
            Row("18", "Less: relief under section 89", 0),
            Row("19", "Net tax payable (17 − 18)", c.TotalTax, true),
        };

        var quarterRows = quarters.Select(q => Html.Of(
            $"<tr><td>{q.Quarter}</td><td class=\"n\">{Rupees(q.AmountPaid)}</td><td class=\"n\">{Rupees(q.TaxDeducted)}</td><td class=\"n\">{Rupees(q.TaxDeposited)}</td></tr>"));
        var regime = c.Regime == TaxRegime.New ? "Yes" : "No";
        var body = Html.Of($"""
            {DocumentParts.Title("FORM No. 16 — PART B")}
            <p class="note">[See rule 31(1)(a)] Certificate under section 203 of the Income-tax Act for tax deducted at source from income chargeable under the head "Salaries". Part A is downloaded from TRACES.</p>
            {DocumentParts.Fields([
                ("Employer", employer.Name), ("TAN of the deductor", employer.Tan), ("PAN of the deductor", employer.Pan), ("Address", employer.Address),
                ("Employee", employee.Name), ("PAN of the employee", employee.Pan ?? TdsReturnFile.PanNotAvailable),
                ("Financial year", year.Label), ("Assessment year", year.AssessmentYear),
                ("Period with the employer", $"{from:dd/MM/yyyy} to {to:dd/MM/yyyy}"),
                ("Opting out of the new regime (s.115BAC(1A))", c.Regime == TaxRegime.New ? "No" : "Yes")])}
            <h2>Details of salary paid and tax deducted</h2>
            <table class="c"><tr><th>No.</th><th>Particulars</th><th class="n">₹</th></tr>{rows}</table>
            <h2>Summary of tax deducted at source</h2>
            <table class="c"><tr><th>Quarter</th><th class="n">Amount paid or credited</th><th class="n">Tax deducted</th><th class="n">Tax deposited</th></tr>{quarterRows}
            <tr class="t"><td>Total</td><td class="n">{Rupees(quarters.Sum(q => q.AmountPaid))}</td><td class="n">{Rupees(deducted)}</td><td class="n">{Rupees(quarters.Sum(q => q.TaxDeposited))}</td></tr></table>
            <p class="note">Taxed under the new regime: {regime}. Rates: {year.Source}</p>
            <h2>Verification</h2>
            <p>I, {responsibleName}, working in the capacity of {responsibleDesignation}, certify that the information given above is true, complete and correct and is based on the books of account, documents, TDS statements, and other available records.</p>
            <p>Place: {employer.Address} &nbsp; Date: {issuedOn:dd/MM/yyyy}</p>
            <p>Signature of the person responsible for deduction of tax: ____________________ &nbsp; Full name: {responsibleName} &nbsp; Designation: {responsibleDesignation}</p>
            """);

        return new PrintableDocument
        {
            Title = $"Form 16 Part B {year.Label} — {employee.Name}",
            FileName = $"form16-partb-{year.Label}-{employee.Name}",
            Body = body,
            Styles = Styles,
            Footer = $"Form 16 Part B · {employee.Name} · FY {year.Label} · TAN {employer.Tan}",
        };
    }

    public static PrintableDocument Form16AStatement(CertificateParty deductor, CertificateParty deductee, int fyStartYear, int quarter,
        IReadOnlyList<FeePayment> payments, IReadOnlyList<ChallanReference> challans, string responsibleName, string responsibleDesignation, DateOnly issuedOn)
    {
        ArgumentNullException.ThrowIfNull(deductor);
        ArgumentNullException.ThrowIfNull(deductee);
        ArgumentNullException.ThrowIfNull(payments);
        ArgumentNullException.ThrowIfNull(challans);
        var (from, to) = TdsReturnFile.QuarterDates(fyStartYear, quarter);
        var label = $"{fyStartYear}-{(fyStartYear + 1) % 100:00}";
        var paymentRows = payments.Select(p => Html.Of(
            $"<tr><td>{p.PaidOn:dd/MM/yyyy}</td><td>{p.Reference}</td><td>{p.Section}</td><td class=\"n\">{Rupees(p.Amount)}</td><td class=\"n\">{Rupees(p.Tds)}</td></tr>"));
        var challanRows = challans.Select(ch => Html.Of(
            $"<tr><td>{ch.BsrCode}</td><td>{ch.DepositedOn:dd/MM/yyyy}</td><td>{ch.ChallanSerial}</td><td class=\"n\">{Rupees(ch.Amount)}</td></tr>"));
        var body = Html.Of($"""
            {DocumentParts.Title("STATEMENT OF TAX DEDUCTED AT SOURCE — FORM No. 16A DETAILS")}
            <p class="note">For checking the certificate in Form 16A downloaded from TRACES, which is the certificate under section 203 (rule 31(1)(b)). This statement is not the certificate.</p>
            {DocumentParts.Fields([
                ("Deductor", deductor.Name), ("TAN", deductor.Tan), ("PAN of the deductor", deductor.Pan), ("Address", deductor.Address),
                ("Deductee", deductee.Name), ("PAN of the deductee", deductee.Pan ?? TdsReturnFile.PanNotAvailable),
                ("Financial year", label), ("Quarter", $"{TdsReturnFile.Quarter(quarter)} ({from:dd/MM/yyyy} to {to:dd/MM/yyyy})")])}
            <h2>Amounts paid or credited</h2>
            <table class="c"><tr><th>Date</th><th>Reference</th><th>Section</th><th class="n">Amount</th><th class="n">Tax deducted</th></tr>{paymentRows}
            <tr class="t"><td colspan="3">Total</td><td class="n">{Rupees(payments.Sum(p => p.Amount))}</td><td class="n">{Rupees(payments.Sum(p => p.Tds))}</td></tr></table>
            <h2>Tax deposited (challans)</h2>
            <table class="c"><tr><th>BSR code</th><th>Date of deposit</th><th>Challan serial</th><th class="n">Tax deposited for the deductee</th></tr>{challanRows}</table>
            <p>Prepared by {responsibleName}, {responsibleDesignation}, on {issuedOn:dd/MM/yyyy}.</p>
            """);

        return new PrintableDocument
        {
            Title = $"TDS statement {label} {TdsReturnFile.Quarter(quarter)} — {deductee.Name}",
            FileName = $"tds-16a-statement-{label}-{TdsReturnFile.Quarter(quarter)}-{deductee.Name}",
            Body = body,
            Styles = Styles,
            Footer = $"TDS statement · {deductee.Name} · FY {label} {TdsReturnFile.Quarter(quarter)} · TAN {deductor.Tan}",
        };
    }

    private static string Rupees(decimal amount) => Money.FormatInr(amount);

    /// <summary>The quarter (1–4) of the financial year a date falls in.</summary>
    public static int QuarterOf(DateOnly date) => date.Month switch { >= 4 and <= 6 => 1, >= 7 and <= 9 => 2, >= 10 and <= 12 => 3, _ => 4 };

    public static string Format(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}
