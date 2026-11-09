# Statutory returns, TDS and separation dues (HR and Billing)

Code: `src/server/Modules/HyFib.HealthCare360.Modules.HR/Domain/Statutory` (rules and file layouts, pure and unit-tested),
`Services/StatutoryService.cs`, `Services/PayrollTaxContext.cs`, `Features/Statutory/StatutoryEndpoints.cs`; for doctors'
fees, `Modules.Billing/Services/DoctorTaxService.cs`. Billing references HR (layer 7 on layer 2) to share the deductor's
details, the TDS challans, the return file writer and the rates.

Everything here comes from HC360's own approved payslips and doctor payouts. The files that hold UANs, PANs or bank
accounts are made when asked for, never stored, and each export is written to the audit trail (`statutory.ecr_exported`,
`statutory.esic_exported`, `statutory.24q_exported`, `statutory.form16_issued`, `doctorfees.26q_exported`,
`payroll.bank_file_exported`).

## What a payroll officer gets

| Output | Endpoint (permission) | Made from |
|---|---|---|
| EPFO ECR 2.0 (text) | `GET /api/v1/hr/statutory/runs/{runId}/ecr.txt`, check `…/ecr` (payroll.approve) | the approved month's payslips |
| ESIC monthly contribution (CSV, save as .xls) | `GET …/runs/{runId}/esic.csv`, check `…/esic` (payroll.approve) | payslips with ESI wages |
| Salary upload: SBI, HDFC, ICICI or CSV | `GET …/runs/{runId}/bank-file/{Sbi\|Hdfc\|Icici\|Csv}` (payroll.approve) | net pay of bank-transfer payees |
| Form 24Q (FVU input) | `GET …/24q/{fy}/{quarter}/file`, check `…/24q/{fy}/{quarter}` (payroll.approve) | the quarter's payslip TDS and the 24Q challans |
| Form 16 Part B (print or PDF) | `GET …/employees/{id}/form16/{fy}[/pdf]` (payroll.approve) | the year's payslips and the declaration |
| Tax computation, both regimes | `GET …/employees/{id}/tax-computation/{fy}` (process or approve); `GET /api/v1/hr/me/tax-computation/{fy}` (hr.self) | payslips so far, the salary structure to March |
| Tax declaration (Form 12BB) | `GET/PUT …/employees/{id}/tax-declarations/{fy}` (PUT: payroll.process); `GET/PUT /api/v1/hr/me/tax-declarations/{fy}` (hr.self) | entered |
| Gratuity | `GET …/employees/{id}/gratuity?asOf` (process or approve) | service and the current structure |
| Bonus register | `GET …/bonus/{fy}?ratePercent&minimumWage` (process or approve) | the year's payslips |
| Full and final statement | `POST …/employees/{id}/full-and-final` (payroll.process) | structure, leave balance, payslips, the recoveries given |
| Employer and deductor details | `GET/PUT …/profile` (PUT: payroll.approve) | entered once per facility |
| TDS challans (ITNS 281) | `GET/POST …/challans`, `DELETE …/challans/{id}` (POST/DELETE: payroll.approve) | entered after each deposit |
| Form 26Q (FVU input) | `GET /api/v1/doctor-fees/tds/26q/{fy}/{quarter}/file`, check without `/file` (doctorfees.payouts.manage) | approved doctor payouts and the 26Q challans |
| Form 16A statement per doctor | `GET /api/v1/doctor-fees/tds/16a/{practitionerId}/{fy}/{quarter}[/pdf]` | the doctor's payouts in the quarter |
| Doctor's PAN and s.197 certificate | `GET/PUT /api/v1/doctor-fees/tax-profiles/{practitionerId}` | entered |

Files are made only from an approved (or paid, or locked) payroll. The checks list every problem (a member without a
UAN or IP number, a month with TDS and no challan, a challan paying less than is allocated to it, a TAN not in form).
The 24Q and 26Q files are refused (422 `tds_return.problems`) until there are none. The ECR and ESIC files leave the
member out and say so.

## TDS on salary (s.192), computed each month

Payroll used to take the TDS typed into the salary structure. It now computes it (`PayrollTaxContext.MonthlyTds`,
`TdsProjector`):

1. **The year's salary:** the year's earlier payslips (gross, basic, house rent, employee PF, professional tax, TDS
   deducted), plus this month, plus the structure's full month for each month left to March or to the last working
   day.
2. **The regime:** the employee's declaration for the year (new regime if none, s.115BAC(1A)). The old regime takes
   the house rent exemption (s.10(13A), least of three), professional tax (s.16(iii), up to ₹2,500), the housing loan
   interest loss (s.24(b), ₹2 lakh) and Chapter VI-A: 80C with employee PF (₹1.5 lakh), 80CCD(1B) (₹50,000), 80D
   (₹25,000 or ₹50,000 for seniors, the same again for parents), 80E, 80G and 80TTA (₹10,000). Senior (60) and super
   senior (80) slabs come from the date of birth. Salary and TDS from a previous employer (Form 12B) are added in.
3. **The tax:** slabs, the s.87A rebate (with marginal relief in the new regime), surcharge with marginal relief at
   each band (the new regime stops at 25 %), 4 % cess, and rounding of income and tax to ₹10 (s.288A, s.288B).
4. **This month:** the year's tax, less what was deducted, divided by this month and the months left. TDS never
   goes negative: over-deduction is not refunded through payroll.
5. **No PAN (s.206AA):** at least 20 % of the income, with no cess on it. Nothing is deducted when no tax is due. The
   payslip warns.

A `TdsOverride` input for the month still wins. A year missing from the rate table falls back to the structure's
`monthlyTds` with a warning. Changing a declaration affects payrolls processed afterwards; an approved month keeps
its TDS.

### The rate table

`IncomeTaxRates.Years` has one row per financial year, each naming its source. **Add the new year's row, from the
Finance Act, before April's payroll.**

| FY | New regime | Std deduction | 87A | Source |
|---|---|---|---|---|
| 2024-25 | 0 to 3 L nil, 5/10/15/20/30 % at 3/7/10/12/15 L | ₹75,000 (old ₹50,000) | ₹25,000 to ₹7 L, marginal relief | Finance (No. 2) Act 2024 |
| 2025-26 | 0 to 4 L nil, 5/10/15/20/25/30 % at 4/8/12/16/20/24 L | ₹75,000 (old ₹50,000) | ₹60,000 to ₹12 L, marginal relief | Finance Act 2025 |
| 2026-27 | as 2025-26 | as 2025-26 | as 2025-26 | Income-tax Act 2025 as enacted; **check against the Finance Act 2026** |

Old regime, every year: 2.5 L nil, 5 % to 5 L, 20 % to 10 L, 30 % above (3 L nil at 60, 5 L at 80); 87A ₹12,500 to ₹5 L;
surcharge 10/15/25/37 % above 50 L/1 Cr/2 Cr/5 Cr.

From 1 April 2026 the Income-tax Act 2025 replaces the 1961 Act and renumbers the sections (s.192 becomes s.392, s.194J
becomes s.393, Form 16 and 16A become Forms 130 and 131 under the new rules). The code and the documents keep the 1961
section numbers that the 2025-26 returns use. **Update the labels and the FVU layouts when Protean publishes the formats
for tax year 2026-27.**

## TDS on doctors' fees (s.194J)

When a payout is created (`DoctorFeeService.CreatePayoutAsync` → `DoctorTaxService.PayoutTdsAsync` →
`ProfessionalFeeTds.Compute`):

* **Threshold:** fees credited to the doctor in the financial year, by this facility, up to the year's threshold bear
  no TDS: ₹30,000 to FY 2024-25, ₹50,000 from FY 2025-26 (Finance Act 2025).
* **Catch-up:** the payout that crosses the threshold is taxed on all of the year's fees not yet taxed, not only on
  itself. Payouts keep the amount they were taxed on (`TdsBasis`). Payouts from before this change count as taxed on
  their gross when they had TDS.
* **Rate:** 10 %, or the facility's `doctorfees.tds.percent` setting when set (a guarded setting).
* **No valid PAN** (five letters, four digits, a letter): 20 % (s.206AA), remark `C` in the return.
* **A s.197 certificate** on the doctor's tax profile, while valid, or a rate typed on the payout, can only lower the
  rate, and only for a doctor with a PAN (remark `A`).

Form 26Q lists approved and paid payouts by approval date (the date of credit), section `94J-B`, deductee code `02`,
under the month's 26Q challan.

**Form 16A and Form 16 Part A are valid only as downloaded from TRACES** (CBDT Circular 04/2013). HC360 prints Form 16
Part B, which the employer prepares, and, for doctors, a *statement* with the Form 16A details, headed as such, to check
the TRACES certificate against.

## ECR (EPFO)

One line per member: `UAN#~#Name#~#Gross#~#EPF wages#~#EPS wages#~#EDLI wages#~#EE share#~#EPS#~#ER−EPS#~#NCP days#~#Refund`.

* **EPF wages** are the payslip's PF wage: Code wages since November 2025, capped at ₹15,000 unless on actual wages.
* **EPS** is 8.33 % of EPF wages up to ₹15,000. A member who turned 58 before the wage month gets none, and the whole
  employer share goes to EPF. EDLI wages are capped at ₹15,000.
* **NCP days** are the loss-of-pay days, rounded to whole days.

Not handled:

* The "excluded employee" case: someone who joined after 1 September 2014 on more than ₹15,000 is not an EPS member.
  `EcrMemberMonth.EpsMember` carries the flag, but HC360 does not record it yet, so every member under 58 pays EPS.
* Arrear ECRs.
* The higher-pension option.

## ESIC

Each insured person's days paid (whole days) and ESI wages. Zero days gives reason 1 (on leave). Leaving in the month
gives reason 2 with the last working day (3 on retirement, 5 on death). Payroll now keeps a person covered to the end
of the contribution period (April–September, October–March) once covered in it, even after a raise above ₹21,000
(s.2(9) proviso), from earlier payslips in the period.

## Gratuity, bonus, leave encashment, full and final

* **Gratuity:** wages × 15/26 × years, where a part year over six months counts as a year.
  * Five years' continuous service is needed, or four years and 240 days worked in the fifth.
  * The five years are not needed on death, or under the Code at the end of a fixed term (`EmploymentType.Contract`
    with reason `EndOfContract`).
  * The amount is capped at ₹20 lakh.
  * Wages are basic pay for anyone who left before 21 November 2025 (Payment of Gratuity Act), and Code wages after
    (Code on Social Security, s.53). Code wages include the allowances above half of pay, as for PF.
  * Days worked in the final year default to calendar days. Pass the attendance figure where it matters.
* **Bonus:**
  * Eligibility: 30 days worked in the year, on months with wages of ₹21,000 or less.
  * Each month counts up to the higher of ₹7,000 and the minimum wage passed in.
  * The rate is 8.33 % to 20 %, chosen by the hospital from the allocable surplus (HC360 does not compute the
    surplus).
  * Wages are Code wages from the payslip's pay heads.
* **Leave encashment:** days × wages ÷ 30 (or 26). On leaving, it is exempt up to the least of ₹25 lakh, ten months'
  average salary and the amount (s.10(10AA)).
* **Full and final:** unpaid salary, leave encashment (the encashable balance unless given), gratuity, bonus due, and
  pay in lieu of notice when the employer withholds notice. Less notice not served (gross ÷ 30 a day, unless waived)
  and the recoveries given. It is a statement. Payment goes through the final payroll month (pay heads with
  `PaidOnLeaving` treatment) and its maker-checker approval.

## Bank files

SBI (pipe-separated, DCR within SBI, else NEFT), HDFC ENet (no header row, `I` within HDFC, `N` NEFT) and ICICI CIB
(`I` within ICICI, `N` NEFT). SBI and ICICI need the hospital's salary account from the statutory profile. Text is
stripped of separators and formula characters. Each pays the account and IFSC fixed on the payslip when the payroll
was approved (maker-checker slice 11), never the employee's account at export; a run approved before payment details
were fixed has no bank file until it is reopened and approved again (`payroll.bank_details_not_fixed`). **Banks set
these layouts per corporate client: confirm each against the bank's sample file before the first upload.**

## Field layouts and their sources

Every layout is in `StatutoryLayouts.cs`, with its source and the date it was last checked (8 November 2026). The
builders refer to fields by name, so a layout change is a change to that file only.

| Layout | Source | Checked |
|---|---|---|
| ECR 2.0 | EPFO Unified Portal, ECR file format v2.0 help and sample | 2026-11-08 |
| ESIC monthly contribution | ESIC portal, MC_Template.xls and reason codes | 2026-11-08 |
| e-TDS FH, BH, CD, DD (24Q, 26Q) | Protean, File Format for e-TDS/TCS regular statements | 2026-11-08 |
| HDFC ENet | HDFC Bank ENet bulk upload standard format | 2026-11-08 |
| ICICI CIB bulk | ICICI Bank CIB bulk payments template | 2026-11-08 |
| SBI CMP bulk | SBI CINB/CMP bulk upload as set up for salary | 2026-11-08 |

**Before go-live, an outsider must check each of these, not HC360's own tests:**

* every 24Q and 26Q file passes the current Protean FVU;
* an ECR uploads on the EPFO portal (it validates UANs and totals);
* an ESIC file uploads;
* each bank accepts a one-rupee test file;
* the hospital's auditor reconciles Form 16 Part B with Form 24Q, and both with Form 26AS and AIS.

## Not covered

* **Form 24Q Annexure II.** The salary detail (SD) records of the fourth quarter are not written; prepare them from
  Form 16 Part B in the RPU. Correction statements are not written either.
* **Staff-app screens.** There are none for statutory details, challans, declarations or the files yet. They are
  reached through the API.
* **Approvals.** Declarations and the statutory profile are saved without a second person.
* **Records not kept by HC360:** the EPS "excluded employee" flag, the higher-pension option, arrear ECRs, PF and ESI
  challan reconciliation, and the landlord's PAN for rent over ₹1 lakh.
