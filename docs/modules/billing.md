# Billing module (`billing`)

Tariffs and pricing, charge capture from every clinical/support module, OP and IP billing, payments (counter, POS
terminal, payment gateway), cashier shifts and day-end, patient advances (online appointment prepayments), refunds and
credit notes, insurance/TPA (payers, policies, pre-authorisation, claims, receivable ageing), doctor fee accounting,
the Tally day book, and the reconciliation of the payment gateway and of card and UPI terminals. SRS **MB-01..06, OPB-01..07, IPB-01..08,
INS-01..07, DFA-01..05**, business side of **INT-PG / INT-POS / INT-TALLY**, and OA-03 (appointment prepayments).

Project: `src/server/Modules/HyFib.HealthCare360.Modules.Billing` (layer 7; references Patients and Scheduling).
Schema `billing`. Tests: `tests/Modules/HyFib.HealthCare360.Modules.Billing.Tests` (372 unit tests: pure domain, the settlement reports and the
printed bill's HTML, plus change-tracking checks on the real EF model without a database), and the end-to-end
`GatewayReconciliationTests` in `tests/HyFib.HealthCare360.IntegrationTests` (payment link to settlement journal, a
webhook delivered twice, who may reconcile), `PosSettlementTests` (a terminal sale and a typed card payment matched
from the acquirer's report, the review, the settlement journal, a card receipt never settled), `PosRecheckTests`
(the training terminal: a sale paid after the counter stopped polling is recorded once; one nobody paid is cancelled
and timed out), `InterimBillTests` (a self-paying stay billed in parts, and none for insured, scheme and package
stays; see [Interim bills](#interim-bills-ipb-05)) and `DoctorFeeStatementTests` (a doctor's statement for a period
from bills that accrued fees under his rules: the fee lines, a cancelled bill's reversal, and the payouts with their
deductions, TDS and net). The staff app's doctor-fee screens keep their logic in `modules/doctor-fees/logic.ts`,
tested with the statement and payout-advice prints by vitest.

The module implements the Platform contracts `IChargePoster`, `IBillingStatusProvider` and `IPayerDirectory`
(replacing the Null fallbacks) — see [Contracts for other modules](#contracts-for-other-modules). For insurance claims it reads the coding
medical records verified for a stay or visit through `IVerifiedCodingSource`, which the Clinical module implements.

## Money rules

* `decimal` only; every amount is rounded with `Money.Round` (half away from zero) per line.
* GST is charged on the discounted value of each line and split into CGST/SGST (`Money.SplitGst`, SGST takes the paisa).
* **Place of supply** (IGST Act s.12(2)(a)): the state of a payer registered for GST (the first two digits of its
  GSTIN), else the facility's own state (`BillPlaceOfSupply`, set while the bill is a draft). A payer registered in
  another state is billed **IGST** for the whole tax instead of CGST and SGST, on every line and in the totals; the bill
  stores `placeOfSupply` and `igstAmount`. Tally posts IGST to the `Output IGST` ledger. A facility without a GSTIN bills
  CGST and SGST as before.
* **Room rent** (`RoomRentGst`; notification 12/2017-CT(R) entry 74 as amended by 4/2022, and 11/2017-CT(R) entry 31A
  inserted by 3/2022, from 18 July 2022): health care is exempt, but a day of a room other than intensive or critical
  care charged above ₹5,000 is taxed at 5% on the whole charge (CGST and SGST of 2.5% each, or IGST), without input tax
  credit. At or below ₹5,000, or in critical care (ICU, CCU, ICCU, NICU: Inpatient's ICU, NICU, PICU and HDU wards), a
  day is exempt. The rule replaces the room's rate in the service master for the bed days Inpatient charges (each tells
  Billing its `RoomCharge`, kept on the charge as `room`), and again when a stay is re-priced on another plan. A day is
  over the line by its charge after the plan's concession; a discount given later on the bill lowers the tax, not the
  rate. A bed charge posted by hand at the desk keeps the service's own rate, and bed days charged before this rule keep
  the GST they were charged with.
* **Free treatment by law** (Bharatiya Nagarik Suraksha Sanhita 2023, s.397, formerly CrPC s.357C; the person in
  charge of a hospital that does not give it is punishable, BNS 2023 s.200). The victim of rape or another sexual
  offence, or of an acid attack, is treated free of cost. The MLC register marks the visit (Clinical's
  `FreeTreatment`; `visit.freeTreatmentReason`, e.g. "BNSS 2023 s.397, MLC no. …"). It names the law and the MLC entry,
  never the offence, as a bill may be seen by anyone. A bill of a marked visit (`BillFreeTreatment`, applied wherever a
  draft's terms are: the OP draft, its refresh and finalisation, and the IP interim, final and supplementary bills)
  charges nothing to anyone:
  * package cover and tariff concessions still say what each line was worth, kept as `freeTreatmentAmount`;
  * staff discounts are cleared and refused (422 `billing.free_treatment`), and a discount awaiting approval no longer
    holds the bill;
  * there is no payer share, no taxable value and no tax: nothing is supplied for a price;
  * finalised, it is settled at ₹0 with nothing paid.

  An IP stay that is free takes no advance, at the counter, on a terminal or by a payment link (422
  `billing.free_treatment`). A link or terminal sale made before the stay was marked, and paid after, is not recorded
  as an advance either: the capture is left to the accountants, as an unrecorded payment, to refund. Its running bill
  estimates nothing for the patient. The printed bill shows the services,
  then "Less: free treatment by law (…)", and a net of ₹0.
* Only the **patient share** is rounded to the rupee (it is collected in cash); the payer share stays at paise.
* **Cash limit** (Income-tax Act s.269ST; the penalty under s.271DA is the whole sum, and hospitals have no exemption):
  ₹2,00,000 or more may not be taken in cash from one patient in a day, for one admission (deposits and the bills of the
  stay, whatever the day), for one bill, or in one payment. A wallet counts as cash: rule 6ABBA's bank modes are cards,
  net banking, IMPS, UPI, RTGS, NEFT and BHIM Aadhaar Pay. The day counts the cash taken at the billing counters and at
  the pharmacy (each registers an `ICashReceiptSource`) at every facility under the same PAN (every facility when the
  PAN is not recorded), from the IST midnight; money paid back is not taken off. A payment that would reach the limit is
  refused (422 `payments.cash_limit`) with how much less to take in cash. `CashLimit` takes a per-patient advisory lock
  inside the transaction that records the money, so two counters taking cash from the same patient at once are counted
  one after the other. A payment taken online counts as cash when the gateway says it was made from a wallet, and is
  held to the limit when its capture is recorded, on the day the gateway took it: refused, it is not recorded, and the
  capture is left to the accountants (an unrecorded-payment exception) to refund.
* Every document number is allocated inside the transaction that saves the document, so series are gap-free.
* Charge posting (per source line), gateway payments, POS transactions and patient advances are idempotent — a WiFi
  retry or a replayed webhook can never charge or receipt twice (backed by unique indexes).
* Days are Indian Standard Time days (`BillingTime`: IST calendar day, half-open UTC ranges).
* A **return** is a negative-quantity charge (credit line). It mirrors its sale — same tariff concession, same GST, amounts
  negated — takes a proportional (negative) share of any bill discount, never receives a staff line discount, gives a
  package back what the package absorbed (after first refunding what the patient paid beyond the package), and can
  make a bill's patient share negative, in which case the difference is refundable.

## Permissions

| Permission | Purpose | Default roles |
|---|---|---|
| `billing.charges.read` | View charges, bills, receipts, IP accounts, reports | Billing, Cashier, InsuranceDesk, Accountant, HospitalAdmin |
| `billing.charges.manage` | Manual charges, cancel unbilled charges (an OT, procedure, implant or bed-day charge, or one above `billing.chargeCancel.approvalAbove`, is asked for) | Billing, HospitalAdmin |
| `billing.op.create` | Create/edit draft OP bills (discounts within own limit) | Billing, Cashier, FrontOffice, HospitalAdmin |
| `billing.ip.manage` | IP accounts: packages, interim/final bills, settlement | Billing, InsuranceDesk, HospitalAdmin |
| `billing.bills.finalize` | Finalize bills (allocates the number) | Billing, Cashier, HospitalAdmin |
| `billing.bills.cancel` | Cancel finalized bills | HospitalAdmin, Accountant |
| `billing.discounts.approve` | Approve discounts above a user's limit (never one's own), approve charges cancelled at the desk, discharge with dues, set limits (raising one waits for another holder) | HospitalAdmin, Accountant |
| `billing.payments.collect` | Collect payments, POS, payment links, advance adjustment, pay out approved refunds | Cashier, Billing, FrontOffice, HospitalAdmin |
| `billing.refunds.request` | Request refunds, credit notes and write-offs, and ask to reverse money that did not move (a receipt not received, a refund not paid out) | Cashier, Billing, HospitalAdmin |
| `billing.refunds.approve` | Approve refunds (the pharmacy's invoice cancellations and returns paying money out or writing stock off included, see [pharmacy.md](pharmacy.md)), credit notes, write-offs, reversals of money that did not move, and card and UPI settlement reports withdrawn as imported in error, someone else asked for | HospitalAdmin, Accountant |
| `billing.deposits` | Collect IP advances | Cashier, Billing, HospitalAdmin |
| `billing.shifts` | Cashier shifts, day-end report | Cashier, Billing, FrontOffice, Accountant, HospitalAdmin |
| `billing.tariffs.manage` | Tariff plans, prices, packages | Billing, HospitalAdmin |
| `billing.tally.export` | Post and export the Tally day book, mark it imported and reconcile it with Tally | HospitalAdmin, Accountant |
| `billing.tally.configure` | Set the Tally ledger names the day book posts to | HospitalAdmin, Accountant |
| `billing.einvoice` | Keep the GST e-invoice register: the e-invoice JSON of B2B invoices, their IRNs (asked of the portal or recorded), IRN cancellations and e-way bills | Billing, Accountant, Pharmacist, HospitalAdmin |
| `billing.gst.returns` | Read the GST returns of the facility's GSTIN: GSTR-1 with its GSTN offline-tool JSON, and GSTR-3B | Accountant, HospitalAdmin |
| `billing.gateway.reconcile` | Gateway reconciliation: settlements, exceptions, record a missed online receipt, send a failed gateway refund again. Card and UPI reconciliation: import the acquirer's settlement reports, review what does not match, ask to withdraw a report imported in error | HospitalAdmin, Accountant |
| `insurance.payers.manage` | Insurers, TPAs, corporates, schemes, non-payable items | InsuranceDesk, HospitalAdmin |
| `insurance.policies.manage` | Patient policies | InsuranceDesk, Billing, HospitalAdmin |
| `insurance.preauth.manage` | Cashless pre-authorisation, enhancements | InsuranceDesk, HospitalAdmin |
| `insurance.claims.manage` | Claims, settlements, ageing (a write-off above `billing.claimWriteOff.approvalAbove` is asked for) | InsuranceDesk, Accountant, HospitalAdmin |
| `insurance.claims.writeoff.approve` | Approve an insurance write-off another user asked for (the insurer's deductions, a rejected claim) | Accountant, HospitalAdmin |
| `doctorfees.rules.manage` | Doctor revenue-share rules | HospitalAdmin, Accountant |
| `doctorfees.payouts.manage` | Accruals, statements, payouts | HospitalAdmin, Accountant |

Segregation of duties is enforced in the domain, not only by roles: the requester of a refund or a discount cannot
approve it, and the preparer of a doctor payout cannot approve it. Holding `billing.discounts.approve` gives no more
discount than one's own limit (see [The billing desk's decisions wait for a second person](#the-billing-desks-decisions-wait-for-a-second-person)). A refund requested by the system (no user, for
example a cancelled prepaid appointment) can be approved by any holder of `billing.refunds.approve`.

## Endpoints

All routes return RFC 7807 problems on failure (`422` business rule, `404`, `409` concurrency/duplicate). Updates that
carry `version` use optimistic concurrency (row `xmin`).

### Tariffs — `/api/v1/billing/tariffs`
Readers = any of `billing.tariffs.manage`, `billing.charges.read`, `billing.op.create`, `billing.ip.manage`.

| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/` | readers | → `TariffPlanListItem[]` |
| GET | `/{id}` | readers | → `TariffPlanDetail` (bed-class multipliers; `pendingTerms` and `pendingPrices`, what waits for approval) |
| POST | `/` | `billing.tariffs.manage` | `SaveTariffPlanRequest` (+`reason`) → 201 `TariffPlanDetail`: made out of use, its terms in `pendingTerms` until approved |
| PUT | `/{id}` | `billing.tariffs.manage` | `SaveTariffPlanRequest` (+`version`, `reason`) → 200 `TariffPlanDetail`, or 202 with the `ApprovalRequestDto` when its terms wait; 409 `approval.already_requested` when other terms wait. Approving replaces the bed-class multipliers; the plan's audit event lists them as they were and as they are (`bedMultipliers`) |
| GET | `/{id}/prices` (paged, `sort=effectiveFrom\|price`) | readers | → `PagedResult<TariffPriceDto>` |
| PUT | `/{id}/prices` | `billing.tariffs.manage` | `SaveTariffPriceRequest` (effective from/to, `version` of the price it changes, `reason`) → 202 `ApprovalRequestDto`; 409 `concurrency` on a stale or missing version |
| POST | `/{id}/prices/bulk` | `billing.tariffs.manage` | `BulkTariffPricesRequest` (at most 5,000 prices, `reason`) → 202 `ApprovalRequestDto`: one request for the revision |
| DELETE | `/{id}/prices/{priceId}` | `billing.tariffs.manage` | → 202 `ApprovalRequestDto` |
| GET | `/quote?serviceItemId&planId&date&bedServiceItemId` | readers | → `PriceQuoteDto` (price source: base, plan, override, free follow-up) |

**A plan's prices and terms change only when two people agree** (maker-checker, `TariffApprovals`). Every change to a
plan's prices (one, a bulk revision as one request, one removed: `billing.tariff_prices`) and to its terms (type,
facility, concession, bed-class multipliers, default, in use: `billing.tariff_plan`) is asked for; someone else who holds
`masters.prices.approve` (accountants and hospital administrators by default) decides it in Approvals. For a plan nothing
only narrows: taken out of use, its patients are charged by their type's default plan instead. Name, payer and
description are saved at once. A new plan is made out of use, without a concession, multipliers or default, and goes
live with the terms approved. While prices wait, the plan's prices are not asked for again; while terms wait, a save
must ask for the same terms (409 `approval.already_requested`). The request lists each price (the first 20) against the
one it replaces, a bulk revision's summary (dearer, cheaper, new, the largest change) and a backdated start (charges
already posted keep their price). Approving is made only over the very rows and terms the request replaced: a change
made meanwhile fails it (422 `prices.changed`). Billing quotes and charges the prices and terms in use until then; each
approved change is audited (`tariff.prices_changed`, `tariff.plan_changed`: before and after, who asked, who approved).

### Rate list — `/api/v1/billing/rate-list`
Readers = any of `billing.tariffs.manage`, `billing.charges.read`, `billing.op.create`, `billing.ip.manage`,
`billing.payments.collect`. For the signed-in facility, as on today; see [The rate list](#the-rate-list).

| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/` | readers | → `RateListView` (`rates`: registration, doctors, rooms, sections by category, packages; `language`: the words' language, `hi`, `ta` or null; `localLanguage`: the facility's, `{code, name}` or null; `unknownSetting`, `unknownState`: what could not be read) |
| GET | `/document` · `/pdf` | readers | → `PrintableDocumentDto` · the PDF |

### Published rate list — `/api/v1/public/rate-list`
Anonymous and rate-limited (the "public" policy), and off while the patient portal is (503). For each active facility
that publishes its list (`billing.rateList.publish`, on unless set to false); see [On the portal](#on-the-portal).

| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/` | anyone | → `PublicRateListFacility[]` (`id`, `name`, `city`), by name |
| GET | `/{facilityId}` | anyone | → `PublicRateList` (`facility`, `rates`, `language`, `localLanguage`: the counter's list, without what is for staff to correct); 404 for a facility that does not publish |
| GET | `/{facilityId}/pdf` | anyone | → the PDF the counter prints; 503 `pdf.unavailable`, without the reason, when it cannot be made |

### Packages — `/api/v1/billing/packages`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/` · `/{id}` | `billing.tariffs.manage` / `billing.ip.manage` / `billing.charges.read` / `billing.op.create` | → `PackageListItem[]` / `PackageDetail` |
| POST | `/` | `billing.tariffs.manage` | `SavePackageRequest` (price, `usedFor`: `InPatient`, `OutPatient` or `Both`, LOS, inclusions: item or category, quantity/amount caps, `hbpCode`: the AB PM-JAY procedure, see [PM-JAY procedures](#pm-jay-procedures-hbp); `reason` for the approver) → 201 `PackageDetail`: a new package out of use, at ₹0 covering nothing, with `pendingTerms` when its terms wait |
| PUT | `/{id}` | `billing.tariffs.manage` | `SavePackageRequest` (+`version`) → 200 `PackageDetail` (saved at once) / 202 `ApprovalRequestDto` (`billing.package`, its price or cover asked for) / 409 `approval.already_requested` (other terms while some wait). Approved terms replace the inclusions; the package's audit event lists them as they were and as they are (`inclusions`) |

**A package's price and cover change only when two people agree.** What a package's patients pay is its price, the
catalogue service it is billed as (whose GST rate the package line carries), what it covers (each service or category
with its caps), the tariff plan it is priced for (none: any plan), the bills it may price and whether it is in use. These
are asked for (`billing.package`), and someone else who holds `masters.prices.approve` decides. An admission's package is
priced at its final bill from the package as it is then, so a change reaches every stay already on it: the request says
how many stays in progress are on the package, and reads each change in words ("price ₹9,000.00 → ₹9,900.00 (+10%)",
"covers every bed charge service, up to 3", "no longer covers …"). A cheaper package waits too: it is a concession to
every stay on it. Taking a package out of use is made at once: it only stops it being chosen again, and stays and bills
on it keep it. A new package is made out of use, at ₹0 and covering nothing, and goes live with the terms approved. Its
name, length of stay, description and PM-JAY procedure, and the covers' notes (which decide nothing), are saved at once.
Approving re-checks the terms it replaces (`prices.changed`); audited `package.terms_changed` with before and after, who
asked and who approved. `PackageDetail.pendingTerms` holds what waits, with the service code and the covers named; while
it waits, a save must ask for the same terms (409).

### PM-JAY procedures — `/api/v1/billing/hbp`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/procedures?search=&specialty=&includeRetired=` | `billing.tariffs.manage` / `insurance.preauth.manage` / `insurance.claims.manage` | → `HbpProcedureDto[]` (the first 50 by code, active ones unless asked) |
| GET | `/procedures/{code}` | as above | → `HbpProcedureDto`; 404 `billing.hbp_not_found` |
| GET | `/imports` | as above | → `HbpImportDto[]`: the latest 10 imports, newest first (version, file name and SHA-256, read/new/changed/retired, rows not read, `status`: `Waiting`, `Applied`, `Rejected`, `Withdrawn`, `Expired` or `NotApplied`, who asked and who decided, the approver's note or why it was not applied) |
| POST | `/imports` | `billing.tariffs.manage` | multipart `file` (NHA's HBP master, .xlsx or CSV, at most 10 MB), `version` (for example `HBP 2022`) and `reason` (for the approver, at most 500 characters) → **202** `HbpImportAsked` (the import staged, with what it would change and the rows not read, and the approval request `billing.hbp_master`); nothing changes in the master until a second person approves it; 400 `file`, `version` or `reason`; 409 `approval.already_requested` while another import waits; 422 `hbp.unchanged` when the master already reads as the file does |

### Charges — `/api/v1/billing/charges`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/?patientId&visitId&admissionId&status&unbilledOnly&…` (paged) | `charges.read` / `op.create` / `ip.manage` | → `PagedResult<ChargeDto>` |
| GET | `/{id}` | same | → `ChargeDto` |
| POST | `/` | `billing.charges.manage` | `PostChargeRequest` → 201 `ChargeDto` (source `billing.manual`; price editable only if the service allows) |
| POST | `/{id}/cancel` | `billing.charges.manage` | `CancelChargeRequest` → 204 cancelled; 202 `ApprovalRequestDto` for an OT, procedure, implant or bed-day charge or one above `billing.chargeCancel.approvalAbove` (`billing.charge_cancel`); refused once billed. `ChargeDto` (list and read) carries `cancelNeedsApproval` and `cancellationAskedAt` |

### Bills — `/api/v1/billing/bills`
Readers = any of `billing.charges.read`, `billing.op.create`, `billing.ip.manage`, `billing.payments.collect`.

| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/?from&to&type&status&patientId&visitId&admissionId&payerId&withDueOnly&search` (paged, `sort=createdAt\|finalizedAt\|billNumber\|netAmount\|dueAmount`) | readers | → `PagedResult<BillListItem>` |
| GET | `/{id}` | readers | → `BillDetail` (lines as `BillLineDto` with `isReturn`, receipts, refunds, credit notes, pending discount, the `package` it is priced at; `eInvoice`, its live IRN, or `missingIrn`, `Pending` or `IrnCancelled` for a bill due an IRN without one, as every `BillDetail` answer has); PHI access guarded and audited |
| GET | `/{id}/document` | readers | → `PrintableDocumentDto` (title, file name, page size `A4`, HTML): the [printed bill](#the-printed-bill) for the staff app to print, drafts included; guarded and audited ("Printed bill …") |
| GET | `/{id}/pdf` | readers | → `application/pdf` attachment `bill-<number>.pdf` (a draft: `bill-draft-<UHID>-<yyyy-MM-dd>.pdf`), the same document; 503 `pdf.unavailable` when the server cannot make PDFs; guarded and audited ("Downloaded bill … as a PDF") |
| POST | `/` | `billing.op.create` | `CreateBillRequest(visitId?, patientId?, chargeIds?)` → 201 `BillDetail`: OP draft from the visit's unbilled charges, or a patient-level draft from charges with no visit/admission |
| POST | `/{id}/lines` | `billing.op.create` | `AddBillLineRequest` → `BillDetail` (ad-hoc service, posted as `billing.adhoc`) |
| DELETE | `/{id}/lines/{lineId}?version` | `op.create` / `ip.manage` | → `BillDetail` (removing an OP bill's package line removes its package) |
| PUT | `/{id}/package` | `billing.op.create` | `SetBillPackageRequest(packageId?, version)` → `BillDetail`: bills an OP draft at a package, or no longer ([Packages on OP bills](#packages-on-op-bills)). 400 `packageId` for a package that is inactive, for admissions only, or priced for another tariff plan; 422 `billing.use_ip_package` on an IP bill |
| POST | `/{id}/refresh` | `billing.op.create` | `BillVersionRequest` → `BillDetail` (pulls charges posted since the draft was made) |
| POST | `/{id}/discount` | `op.create` / `ip.manage` | `ApplyDiscountRequest` (bill % or ₹, line ₹, reason) → `DiscountOutcome` (applied, or an approval request above the user's limit) |
| POST | `/{id}/finalize` | `billing.bills.finalize` | `BillVersionRequest` → `BillDetail`; allocates `{FAC}OB{YY}{SEQ:7}` / `{FAC}IB{YY}{SEQ:7}`; adjusts the visit's online prepayment |
| POST | `/{id}/cancel` | `billing.bills.cancel` | `CancelBillRequest(reason, version)` → `BillDetail`; charges released, advances restored, cash refunds requested |
| DELETE | `/{id}?version` | `op.create` / `ip.manage` | → 204 (discard a draft) |
| POST | `/{id}/send-whatsapp` | `op.create` / `ip.manage` / `payments.collect` | → 204: queues the bill's PDF to the patient's mobile number on WhatsApp ([Bills on WhatsApp](#bills-on-whatsapp-int-wa)); 422 `billing.bill_not_shareable` (draft or cancelled), `billing.no_mobile`, `billing.whatsapp_opted_out` (the number replied STOP); guarded and audited ("Sent a bill to the patient on WhatsApp"). The staff app's bill page offers it as "Send on WhatsApp" in the More menu |
| POST | `/{id}/credit-notes` | `refunds.request` / `refunds.approve` | `IssueCreditNoteRequest(kind, amount, reason, lines?)` → 202 `ApprovalRequestDto` (kind `billing.credit_note` or `billing.write_off`): asks for it; 422 `billing.credit_exceeds` / `billing.bill_not_creditable` as the bill is now, 409 `approval.already_requested` while one of the kind waits. A credit note names the services it corrects (`lines`: `billLineId` and `amount`, GST included; `amount` is their sum, 400 otherwise), and must on a bill with GST (400 `lines`); 422 `billing.credit_exceeds_line` past what is left of a service after earlier credit notes, or on a return; a write-off names none (400 `lines`). The approver sees the GST each service gives back ([Credit notes are GST credit notes](#credit-notes-are-gst-credit-notes)). An approver, never the person who asked, issues it at `POST /api/v1/approvals/{id}/approve` (`{FAC}CN{YY}{SEQ:6}`, both names on the note), only on the bill's version it was asked against: 422 `approval.stale` after a payment, refund or another credit |
| POST | `/{id}/debit-notes` | `refunds.request` / `refunds.approve` | `IssueDebitNoteRequest(party, reason, lines)` → 202 `ApprovalRequestDto` (kind `billing.debit_note`): asks for a debit note ([Debit notes](#debit-notes)); `lines` are `billLineId` and the `taxableAmount` each service is raised by (GST at its rate is added); `party` `Patient` or `Payer`. 422 `billing.bill_not_debitable` (not finalized, or cancelled), `billing.no_payer`, `billing.debit_on_return`, `billing.debit_not_payer_payable`. An approver, never the person who asked, issues it (`{FAC}DB{YY}{SEQ:6}`), on the bill's version it was asked against (422 `approval.stale`) |
| GET | `/{id}/credit-notes/{noteId}/document` | readers | → `PrintableDocumentDto`: the printed credit note (or write-off note) with what CGST rule 53(1A) asks of it, and its IRN and QR code once the portal registered it (until then, for a note due one, a notice that it has none); guarded and audited ("Printed credit note …") |
| GET | `/{id}/credit-notes/{noteId}/pdf` | readers | → `application/pdf` attachment `credit-note-<number>.pdf` (`write-off-<number>.pdf`), the same document; guarded and audited |
| POST | `/{id}/adjust-advance` | `billing.payments.collect` | `AdjustAdvanceRequest(advanceId, amount?, version)` → 201 `ReceiptDto` (patient advance used on an OP bill) |

### Discounts — `/api/v1/billing/discount-approvals`, `/api/v1/billing/discount-limits`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/discount-approvals?status` (paged, pending by default) | `billing.discounts.approve` | → `PagedResult<DiscountApprovalDto>` |
| POST | `/discount-approvals/{id}/approve` · `/reject` | `billing.discounts.approve` | `DecideDiscountRequest(remarks)` → 204 (requester may not decide) |
| GET | `/discount-limits` | `billing.discounts.approve` | → `DiscountLimitDto[]` |
| GET | `/discount-limits/pending` | `billing.discounts.approve` | → `PendingDiscountLimitDto[]` (limits raised that wait: user, before and asked in words, who asked, `askedByYou`) |
| PUT | `/discount-limits` | `billing.discounts.approve` | `SaveDiscountLimitRequest(userId, maxPercent, maxAmount?, notes, version)` → 204 lowered; 202 `ApprovalRequestDto` raised (`billing.discount_limit`); 403 `billing.discount_limit_own` for one's own limit raised |
| DELETE | `/discount-limits/{id}` | `billing.discounts.approve` | → 204; 202 when the default lets the user give more |
| GET | `/discount-limits/me` | `op.create` / `ip.manage` | → the caller's authority (`maxPercent`, `maxAmount`; falls back to `billing.discount.defaultMaxPercent`), the approval permission giving no more |

### Payments, advances and refunds
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/api/v1/billing/receipts?from&to&patientId&billId&ipAccountId&purpose&counter` (paged) | `charges.read` / `payments.collect` | → `PagedResult<ReceiptListItem>` (`reversedAmount`: taken back as not received) |
| GET | `/api/v1/billing/receipts/{id}` | same | → `ReceiptDto` (each payment line's `id` and `reversed`; `reversals[]`, `reversedAmount`) |
| POST | `/api/v1/billing/receipts/{id}/reversal` | `billing.refunds.request` | `ReverseReceiptRequest(paymentIds[], reason, note)` → 202 `ApprovalRequestDto` (kind `billing.receipt_reversal`; [money that did not move](#money-that-did-not-move)); 422 `billing.reversal_cash`, `billing.reversal_mode`, `billing.reversal_done`, `billing.reversal_payer_settlement`, `billing.reversal_adjustment`, `billing.reversal_exceeds_held` |
| POST | `/api/v1/billing/receipts` | `billing.payments.collect` | `CollectPaymentRequest(billId, payments[], notes)` → 201 `ReceiptDto` (`{FAC}RC{YY}{SEQ:7}`; split modes; cash needs an open shift when `billing.shift.required`); 422 `payments.cash_limit` (the cash limit, above) |
| POST | `/api/v1/billing/ip-accounts/{id}/deposits` | `billing.deposits` | `CollectDepositRequest` → 201 `ReceiptDto`; 422 `payments.cash_limit` |
| GET | `/api/v1/billing/patients/{patientId}/dues` | `payments.collect` / `charges.read` | → `PatientDues` (open bills, IP deposits + open patient advances, `advances[]`) |
| GET | `/api/v1/billing/patients/{patientId}/advances?openOnly` | `payments.collect` / `charges.read` | → `PatientAdvanceDto[]` (amount, adjusted, refunded, balance, refund pending) |
| GET | `/api/v1/billing/refunds?status&patientId&billId` (paged) | `refunds.request` / `refunds.approve` / `charges.read` | → `PagedResult<RefundDto>` |
| GET | `/api/v1/billing/refunds/{id}` | same | → `RefundDto` (incl. `advanceId`) |
| POST | `/api/v1/billing/refunds` | `billing.refunds.request` | `RequestRefundRequest(billId? \| ipAccountId? \| advanceId?, amount, reason)` → 201 (`{FAC}RF{YY}{SEQ:6}`) |
| POST | `/api/v1/billing/refunds/{id}/approve` · `/reject` | `billing.refunds.approve` | `ApproveRefundRequest` / `RejectRefundRequest` → 204 |
| POST | `/api/v1/billing/refunds/{id}/not-paid` | `billing.refunds.request` | `ReverseRefundRequest(reason, note)` → 202 `ApprovalRequestDto` (kind `billing.refund_reversal`); 422 `billing.refund_not_paid`, `billing.refund_reversal_cash`, `billing.refund_reversal_advance`, `billing.refund_reversal_gateway`, `billing.refund_reversal_done`. `RefundDto.notPaidOut` once recorded |
| POST | `/api/v1/billing/refunds/{id}/pay` | `billing.payments.collect` | `PayRefundRequest(mode, reference)` → 204 (`PaymentGateway` reverses the original online payment; the refund is paid in HC360 when the gateway accepts it, and `RefundDto.gatewayStatus` then follows it: `Pending` until the bank confirms, `Processed` with the bank's reference in `gatewayReference`, or `Failed` with `gatewayError`) |

Counter payment evidence (`PaymentLineRules`): card last 4 digits, UPI/net-banking/wallet reference, 6-digit cheque
number + bank + date (no post-dated cheques, valid 3 months). Advance, insurance and gateway modes are never keyed at
the counter.

### Money that did not move

Receipts and refunds are never edited. When money recorded as received never arrived (a cheque the bank returned
unpaid, a card, UPI or online payment the bank never settled, or one a training simulator made up), or a refund
recorded as paid out never reached the patient, a **reversal** stands beside the record, dated the day it is made.
One person asks (`billing.refunds.request`); another approves (`billing.refunds.approve`; maker-checker, the person
who asked never approves). The reason is one of `ChequeReturned`, `NotSettled`, `TrainingSimulator`, `Other`, with a
note of 10 to 500 characters saying how it was found. `Chargeback` is refused here (400): a payment the patient's bank
took back is reversed only by accepting the loss of its [chargeback case](#chargebacks), so every such reversal has a
case, and its Tally journal reads "taken back by a chargeback" rather than "money not received".

* **Money not received** (`ReceiptReversal`, `{FAC}RR{YY}{SEQ:6}`): the payment lines asked for are taken back in full
  (what reversals have not taken already). Cash is never reversed: it was counted when the shift closed, and cash
  recorded and not received is the shift's shortage. Advance adjustments, insurers' settlements and receipts that only
  moved an advance onto a bill are not reversed either (reverse the advance's own receipt).
  * A bill payment: the bill owes it again (`Bill.ReversePayment`); a cancelled bill has that much less to refund.
    Only money the bill still holds can be taken back: a refund paid out of it is recorded as not paid out first.
  * An IP deposit or a patient advance: the advance holds that much less. If it already paid bills, those payments are
    undone first, newest first, each as a reversal of its adjustment's receipt that names the reversal of the money
    (`causedById`): the bills owe again. Refunds waiting to be paid from a bill or advance that no longer holds the
    money are closed (rejected, "Closed by RR…"). A settled stay whose bills owe again is reopened (discharged, not
    closed).
  * The approval's subject is the bill, the IP account or the advance: its page shows the request waiting, and one
    waits at a time. The checker sees what changes (what the bill owes, the bills paid from an advance, the refunds
    closed); the reversal is made only if the bill, account or advance is as it was when asked (a payment, credit or
    adjustment on it since changes what the reversal leaves) and it would still do exactly that (`approval.stale`
    otherwise).
* **Refund not paid out** (`RefundReversal`, `{FAC}RX{YY}{SEQ:6}`): a paid refund in a mode other than cash or an
  advance. Through the payment gateway, only one the gateway reports failed (and not being sent again) or one the
  payment simulator made up (`rfnd_sim…`); one reported processed reached the patient's bank. The refund stays as it
  was recorded (`notPaidOut` on it says so, and it is never paid or sent through the gateway again); its money is back
  on the bill, the IP deposit or the patient advance, from which a new refund pays the patient. Once per refund.
* Each is audited (`billing.receipt.reversed` on the receipt and `billing.bill.payment_reversed` on every bill that
  owes again; `billing.refund.not_paid_out`), is a Journal in the Tally day book of its day, and is in that day's
  day-end report and its records check. The patient's spend no longer counts money not received
  (`PaymentReversed`), and counts a refund not paid out again (`RefundPayoutReversed`). With the "paid" trigger, a
  bill that owes again reverses its doctors' fees; they accrue again when it is paid. A payment reversed is no longer
  reported unsettled, by the gateway's settlement check (its open exception is cleared) or in the card and UPI
  settlement list.
* The simulator sweep's guidance for payments, card sales and refunds is carried out with these reversals. The sweep
  counts each such record open until all its money is reversed, or its refund is recorded as not paid out, and takes no
  sign-off meanwhile (DEPLOYMENT.md, "Going live on a database used for training").

### POS terminals and payment links — `/api/v1/billing/pos/sales`, `/api/v1/billing/payment-links`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| POST | `/pos/sales` | `payments.collect` / `deposits` | `StartPosSaleRequest(billId? \| ipAccountId?, amount?, terminalId, channel)` → `PosSaleDto` |
| GET | `/pos/sales/{id}` | same | → `PosSaleDto` (polls the terminal; an approved sale records the receipt once, keyed on the terminal transaction id). A sale the counter stops polling is followed by `PosRecheckJob` (below) |
| POST | `/pos/sales/{id}/cancel` | same | → `PosSaleDto` |
| POST | `/payment-links` | `payments.collect` / `deposits` | `CreatePaymentLinkRequest` → 201 `GatewayOrderDto` (SMS/WhatsApp template `payment.link`) |
| GET | `/payment-links?billId&ipAccountId&status` (paged) | `payments.collect` / `charges.read` | → `PagedResult<GatewayOrderDto>` (`paidAt` = when the gateway took the payment; `lastError` = why it failed, expired or was cancelled, or why its paid receipt could not be recorded yet; `paymentId`) |
| POST | `/payment-links/{id}/refresh` | `payments.collect` / `deposits` | → `GatewayOrderDto` (asks the gateway for the real status: records a captured payment, or marks the order `Failed`, `Expired` or `Cancelled`; for a paid order whose receipt could not be recorded, it tries again) |
| POST | `/api/v1/billing/webhooks/payment-gateway` (optional `?facility=<id>`) | anonymous, rate limit `partner`; the signature is checked with the merchant account of the facility that owns the order, prepaid booking or refund named in the payload (`?facility=` only when the payload names none of them) | raw provider JSON (≤ 64 KB) → 200 `WebhookAck`; 401 bad signature (`X-Razorpay-Signature` / `X-PayU-Signature` / `X-Webhook-Signature` / `X-Signature`), 503 no gateway, 413 too large. The status is always re-fetched from the gateway; receipts are idempotent on the gateway payment id. Each verified event is kept by its id (`X-Razorpay-Event-Id`, else a SHA-256 of the body) in `gateway_webhook_events`: a redelivery of an event already handled gets `processed: false` and changes nothing. A webhook for a portal booking's prepayment confirms the booking and receipts its advance, also for an order the booking had before its current one (the booking's own payment, or a second payment kept as an advance with its refund asked for); one for a gateway refund updates the refund's status. A dispute's event (`payment.dispute.*`, whatever follows the prefix) names the dispute: it is read from the gateway with the account of the facility whose receipt holds the payment, and its [chargeback case](#chargebacks) follows it; nothing else in the body is used. Each delivery is checked with the account of the gateway that sent it (Razorpay's headers, else the facility's gateway), also after the facility switched gateways. |
| POST | `/api/v1/billing/webhooks/cashfree` (optional `?facility=<id>`) | anonymous, rate limit `partner`; checked with Cashfree's account (its client secret) of the facility that owns the link, booking or refund the body names (`?facility=` only when it names none of them) | Cashfree's raw JSON (≤ 64 KB) with `x-webhook-signature` and `x-webhook-timestamp` → 200 `WebhookAck`; 401 for a bad signature or one signed more than three days ago or five minutes ahead, 503 no gateway, 413 too large. A redelivery is known by a SHA-256 of its body. Only the ids in the body are used (as strings or numbers): the link, payment, refund or dispute is read from Cashfree. One naming nothing HC360 holds has the facility's open Cashfree links and bookings of the last week asked again. |

### Cash counter — `/api/v1/billing/shifts`, day-end
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/shifts/current` | `billing.shifts` | → `ShiftDto` or 204 |
| GET | `/shifts` (paged) · `/shifts/{id}` | `billing.shifts` | → `ShiftDto` |
| POST | `/shifts/open` | `billing.shifts` | `OpenShiftRequest(counter, openingFloat)` → `ShiftDto` (one open shift per cashier) |
| POST | `/shifts/{id}/close` | `billing.shifts` | `CloseShiftRequest(declaredCash, notes, version)` → 200 `ShiftDto` (expected per mode, variance); short beyond `billing.shift.shortageTolerance`, 202 `ShiftDto` with `shortageAwaiting` and `shortageApprovalId` (`billing.shift_shortage`), `notes` required as the reason (400) |
| POST | `/shifts/{id}/shortage` | `billing.shifts` | `AskShortageRequest(reason)` → 202 `ApprovalRequestDto`: asks again for a supervisor to accept a shortage still awaiting (after a rejection, withdrawal or expiry) |
| GET | `/api/v1/billing/reports/day-end?date&counter` | `billing.shifts` | → `DayEndReport` (per cashier, per mode, advances collected, bills, discounts, cancellations; on the whole day's report, the reversals made that day by mode, `notReceived` and `notPaidOut`, and `netAfterReversals`; each shift's `shortageAwaiting` and `shortageAcceptedBy`, and `shortagesAwaiting`) |

### In-patient accounts — `/api/v1/billing/ip-accounts`
Readers = `billing.ip.manage` or `billing.charges.read`.

| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/?status` (paged, `search`) · `/{id}` · `/by-admission/{admissionId}` | readers | → `IpAccountListItem` / `IpAccountDetail` |
| PUT | `/{id}` | `billing.ip.manage` | `UpdateIpAccountRequest` (payer, policy, plan, estimate; unbilled charges re-priced) → 204; a plan other than the payer's → 202 `ApprovalRequestDto` (`billing.ip_plan`, [the stay's plan](#the-plan-a-stay-is-billed-on)); 409 `approval.already_requested` while another waits |
| GET | `/{id}/running-bill` | readers | → `RunningBill` (by category, by day, lines with `isReturn`, `returnsNet`, package vs actuals, insurance estimate, deposit, estimated balance); guarded + audited |
| GET | `/{id}/package-status` | readers | → `PackageStatus` |
| PUT | `/{id}/package` | `billing.ip.manage` | `AssignPackageRequest(packageId?)` → 204; 400 `packageId` for a package for out-patient visits only, or one priced for another tariff plan than the stay's |
| POST | `/{id}/interim-bills` | `billing.ip.manage` | `CreateInterimBillRequest(upTo?)` → 201 `BillDetail`: the services not billed yet up to the end of `upTo` (today when empty). Self-pay, non-package stays only; 422 `billing.interim_insured`, `billing.interim_package`, `billing.ip_account_inactive`, `billing.nothing_to_bill` ([Interim bills](#interim-bills-ipb-05)) |
| POST | `/{id}/final-bill` | `billing.ip.manage` | → 201 `BillDetail`: draft final bill (or a supplementary bill for late services); 422 `billing.billed_on_interim_bills` when interim bills took every service, `billing.nothing_to_bill` when none was charged |
| POST | `/{id}/settle` | `billing.ip.manage` | → `SettlementResult` (advance adjusted oldest bill first; unused advance becomes a refund request). Once the discharge has been initiated (422 `billing.discharge_not_initiated` while the account is open, a revoked discharge included) and every draft interim bill is finalized (422 `billing.interim_bill_draft`: the advance pays finalized bills only); after the final bill, or with nothing for one (`finalBillId` null; `billing.final_bill_missing` while services wait for it) |
| GET | `/{id}/clearance` | readers | → `ClearanceDto` (`billedOnInterimBills`: interim bills took every service, so no final bill is needed) |
| POST | `/{id}/discharge-approval` | `billing.discounts.approve` | `ApproveDischargeWithDuesRequest(reason)` → 204 |

IP account statuses: `Open`, `DischargeInitiated`, `Discharged`, `Closed`, `Cancelled` (admission cancelled; no new
advances or packages; leftover services can still be billed; any advance is refunded through the refund flow).

#### The plan a stay is billed on
A stay is billed on its payer's plan: the one billing resolves for its payer when the patient is admitted (the payer's
own plan if in use, else its type's default, else the General default), shown as `payerPlanId`. Saving the stay's
payer, policy and plan (`PUT /{id}`) is made at once when it puts the stay on that plan (no `tariffPlanId`, or the
payer's), or keeps the plan the stay has while its payer stays the same. Any other plan (a concession, or a dearer plan)
is asked for (`billing.ip_plan`), with the payer and policy asked with it and a `reason`, and someone else who holds
`billing.discounts.approve` decides; the estimate and notes are saved at once. The request reads, for the approver,
what the stay's unbilled services come to as priced now, on its payer's plan and on the plan asked (each priced on a
copy that is never saved). Approving re-checks the stay's payer terms (`prices.changed` when any changed meanwhile), that
the stay is not discharged, the plan and payer are in use and no draft bill waits when re-pricing was asked, then sets
them and re-prices the unbilled services; audited `billing.ip.plan_changed`. `IpAccountDetail.pendingPlan` holds what
waits. One waiting request per stay: while it waits, a save asking other payer terms is refused (409).

#### Interim bills (IPB-05)
A self-paying patient's long stay can be billed in parts, so the family pays as the stay goes on:

* **What one takes.** Every service of the admission that is on no bill yet, not even a draft, up to the end of the chosen
  day (IST); with no day, up to the end of today. Later services wait for the next bill.
* **What it is.** An in-patient bill that is not the final bill, with no payer: the patient pays all of it. Its draft holds
  its services, so a second interim bill of the same days answers 422 `billing.nothing_to_bill`. Finalized, it takes the
  next number of the IP series and its services are billed, so no later bill carries them again: the final bill holds
  only what no interim bill took. At settlement the advance pays what is still due on every bill of the stay, interim
  bills included.
* **Only during the stay:** 422 `billing.ip_account_inactive` once the patient is discharged or the admission cancelled.
* **Finalized before settlement:** while a draft interim bill holds services, the discharge notice says to finalize it
  and settling answers 422 `billing.interim_bill_draft`, as the advance pays finalized bills only.
* **Not for insured, credit or package stays** (422 `billing.interim_insured` when the account has an insurer, corporate
  or government scheme as payer; `billing.interim_package` when it has a package). This is deliberate: such a stay is
  billed once, on the final bill, because its split or its package is worked out over the whole stay.
  * The payer's share (the co-pay, what the payer never pays, and at most the amount the pre-authorisation approved) is
    worked out per bill, and the TPA desk claims it from one bill. A stay billed in parts would cap each bill at the
    approved amount, so the insurer could be billed more than it approved, and would need a claim per bill. The payer
    settles the stay after discharge; meanwhile the running bill shows the estimated payer and patient shares.
  * A package absorbs the services it includes, up to its caps, from the lines of the final bill (`PackageCalculator`). A
    service billed earlier on an interim bill would be charged in full, outside the package, and the package price would
    still be charged at discharge. Taken off its package, a self-paying stay can be billed in parts again.
* **A stay the interim bills took in full** (for example one raised on the discharge day for everything up to today,
  with nothing charged after it) needs no final bill: it would hold nothing, and an empty bill cannot be finalized, so
  initiating the discharge drafts none and tells the billing desk to settle the advance. Preparing one answers 422
  `billing.billed_on_interim_bills`. The clearance waits only for the patient's share of the interim bills
  (`ClearanceDto.billedOnInterimBills`), and the account is settled once the discharge has been initiated (before that,
  more services may come: 422 `billing.discharge_not_initiated`): the advance pays what is still due on the interim
  bills, and what is left of it becomes a refund request, with no final bill (`SettlementResult.finalBillId` null).

`InterimBillTests` (in `tests/HyFib.HealthCare360.IntegrationTests`) proves these rules end to end.

### Insurance — `/api/v1/insurance`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/payers` · `/payers/{id}` | payers/policies/pre-auth/claims manage, `op.create`, `ip.manage`, `charges.read` | → `PayerListItem[]` / `PayerDetail` |
| POST · PUT | `/payers` · `/payers/{id}` | `insurance.payers.manage` | `SavePayerRequest` (`reason` for the approver) → 201 `PayerDetail` (a new payer out of use, `pendingTerms` when its terms wait) / 200 `PayerDetail` or 202 `ApprovalRequestDto` (`insurance.payer_terms`) when terms are asked; 409 `approval.already_requested` when other terms wait |
| GET | `/non-payable-items?payerId` | payers/claims manage, `ip.manage` | → `NonPayableItemDto[]`: the items in force, each with `waiting` when taking it off waits for approval, and the items asked to be added (`waiting.change` `Add`; not in force yet) |
| POST | `/non-payable-items` | `insurance.payers.manage` | `SaveNonPayableItemRequest` (payer or every payer, a service or a whole category, notes, `reason` for the approver) → **202** with the approval request (`insurance.non_payable`, approved with `masters.prices.approve`); nothing is added until it is approved; 409 `insurance.non_payable_exists` when the list has it, 409 `approval.already_requested` while the same item waits to be added |
| POST | `/non-payable-items/{id}/remove` | `insurance.payers.manage` | `RemoveNonPayableItemRequest(reason)` → **202** with the approval request; the item stays in force until it is approved; 409 while another change of it waits |
| GET | `/policies?patientId&includeInactive` · `/policies/{id}` | policies/pre-auth/claims manage, `ip.manage`, `op.create` | → `PolicyDto[]` (number masked) |
| GET | `/policies/search?policyNumber` | policies/claims manage | → `PolicyDto[]` (blind-index lookup) |
| GET | `/policies/{id}/policy-number` | policies/pre-auth/claims manage | → `PolicyNumberDto` (decrypted; audited) |
| POST · PUT | `/policies` · `/policies/{id}` | `insurance.policies.manage` | `SavePolicyRequest` → 201 / 204. Changing what a verification vouches for (payer, TPA, number, member, holder, relation, validity, sum insured, co-pay, in use) withdraws it; the card's scan and the notes do not. `PolicyDto.recordedByYou`: the signed-in user recorded those terms (or last changed them) |
| POST | `/policies/{id}/verify` | `insurance.policies.manage` | `VerifyPolicyRequest` → 204; 403 `insurance.policy_self_verify` to whoever recorded the terms or last changed them: [someone else verifies](#a-third-party-pays-only-on-a-second-persons-word) |
| GET | `/preauths?status&patientId&admissionId` (paged) · `/preauths/{id}` | pre-auth/claims manage, `ip.manage` | → `PreAuthListItem` / `PreAuthDetail` |
| POST · PUT | `/preauths` · `/preauths/{id}` | `insurance.preauth.manage` | `SavePreAuthRequest` → 201 / 204 (`{FAC}PA{YY}{SEQ:6}` on submit) |
| POST | `/preauths/{id}/submit` · `/query` · `/answer` · `/reject` · `/enhancements` | `insurance.preauth.manage` | `PreAuthTextRequest`, `RejectPreAuthRequest`, `RequestEnhancementRequest` → 204 |
| POST | `/preauths/{id}/approve` · `/enhancements/{enhancementId}/respond` · `/final-approval` | `insurance.preauth.manage` | `ApprovePreAuthRequest`, `RespondEnhancementRequest`, `FinalApprovalRequest`, each with the pre-authorisation's `version` (409 when it changed) → **202** `ApprovalRequestDto` (`insurance.preauth_amount`): [another insurance desk user checks the amount](#insurance-money-waits-for-a-second-person); a refused enhancement → 204 at once. `PreAuthDetail.amountAskedAt` says when one waits |
| GET | `/claims?status&payerId&patientId` (paged) · `/claims/{id}` | `insurance.claims.manage` | → `ClaimListItem` / `ClaimDetail` (`verifiedCoding`: the coding medical records verified for the bill's admission, or for its visit on any other bill, as claim text; null until verified) |
| POST | `/claims` | `insurance.claims.manage` | `CreateClaimRequest(billId, …)` → 201 (`{FAC}CL{YY}{SEQ:6}`); codes left empty are the verified coding, procedures included, or else the pre-authorisation's |
| PUT | `/claims/{id}/coding` | `insurance.claims.manage` | `UpdateClaimCodingRequest` (ICD / procedure codes as text) → 204 |
| POST | `/claims/{id}/query` · `/answer` · `/approve` | `insurance.claims.manage` | `ClaimTextRequest`, `ApproveClaimRequest` → 204 |
| POST | `/claims/{id}/settle` · `/reject` | `insurance.claims.manage` | `SettleClaimRequest` (tranche, deductions with reasons, UTR, the claim's `version`), `RejectClaimRequest(reason, version)` → 204 when the write-off is within `billing.claimWriteOff.approvalAbove`; otherwise **202** `ApprovalRequestDto` (`insurance.claim_writeoff`): the money received is posted at once, the deductions (or the rejection, a full disallowance) wait for [someone who approves insurance write-offs](#insurance-money-waits-for-a-second-person). 409 on a claim changed since it was read. `ClaimDetail` carries `writeOffAskedAt` and `writeOffApprovalAbove` |
| GET | `/receivables/ageing?asOf` | `insurance.claims.manage` | → `ReceivablesAgeingReport` (0-30 / 31-60 / 61-90 / 90+ by payer) |

**A payer's terms change only when two people agree.** A payer's category (whose default plan prices its patients
while it has no plan of its own), its tariff plan, its default co-pay (the patient's share where the policy states
none) and putting it in use are asked for (`insurance.payer_terms`), and someone else who holds
`masters.prices.approve` decides. Taking a payer out of use is made at once: it only stops new visits and stays being
billed to it. A new payer is added out of use, with no plan of its own and no co-pay, and goes live with the terms
approved. Its name, contact, GSTIN, credit period, portal and notes are saved at once. The request reads each change in
words, and that a new plan or category reaches out-patient services charged after approval and admissions after it
(stays already open keep their plan). Approving re-checks the terms it replaces (`prices.changed`); audited
`payer.terms_changed` with before and after. `PayerDetail.pendingTerms` holds what waits; while it waits, a save must ask
for the same terms (409).

### Insurance money waits for a second person

MAKER-CHECKER.md slice 12. **A write-off** (`insurance.claim_writeoff`): what the insurer deducts in a settlement, or the
unpaid claim when it rejects it, is never collected from the insurer. Above `billing.claimWriteOff.approvalAbove` (per
facility, ₹0 by default, so every write-off) the desk asks and someone else who holds
`insurance.claims.writeoff.approve` (the accountant, the hospital administrator) approves; the insurance desk may not
approve. In a settlement the money received is posted at once (its payer receipt, its UTR) and only the deductions
wait, the claim partly settled meanwhile; a rejection changes nothing until approved. The approver reads the claim and
bill, what the insurer owes before and after, each deduction and its reason, and why it waits. It is made only over the
claim as it was asked about: a payment, a query or anything else recorded meanwhile makes it stale, and the desk asks
again. Audited `insurance.claim.written_off` at Warning with both names. **An amount the insurer approved on a
pre-authorisation** (`insurance.preauth_amount`): the approval, an enhancement approved and the final approval set the
insurer's cap on the bill (`PayerCoverage.CoverageLimit`). Each is recorded for another user who holds
`insurance.preauth.manage` to check against the insurer's letter (the approver reads the amount, the reference and the
cap before and after); until then the cap stays where it was, none before the first approval, so the patient pays that
part. A bill drafted meanwhile keeps that split: discard it and draft again once the amount is approved. A rejection
or a refused enhancement is recorded at once. Audited `insurance.preauth.approved`, `.enhancement` or
`.final_approval` at Warning with both names.

### Doctor fees — `/api/v1/doctor-fees`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/rules?practitionerId&includeInactive` | rules or payouts manage | → `ShareRuleDto[]`, each with `pending` (the terms waiting for approval, who asked, when); a rule out of use with a change waiting is listed without `includeInactive` |
| POST · PUT | `/rules` · `/rules/{id}` | `doctorfees.rules.manage` | `SaveShareRuleRequest` (practitioner/item/category/payer type, % or fixed, effective dates, in use, notes, `reason` for the approver). The notes are saved at once; the terms are asked for (`doctorfees.rule`, approved with `masters.prices.approve`). POST → 201 `{ id, asked }` (the rule saved out of use, and the request when it was asked in use); PUT → 204 (notes only, or the terms that already wait), 202 with the request, or 409 while another change of the rule waits |
| DELETE | `/rules/{id}` | `doctorfees.rules.manage` | → 204 for a rule out of use; 409 `doctorfees.rule_in_use` for one in use (take it out of use first, which waits for approval) |
| GET | `/accruals?practitionerId&from&to&status` (paged) | `doctorfees.payouts.manage` | → `PagedResult<AccrualDto>` |
| GET | `/payouts?practitionerId&status` (paged) · `/payouts/{id}` | `doctorfees.payouts.manage` | → `PayoutDto` |
| POST | `/payouts` | `doctorfees.payouts.manage` | `CreatePayoutRequest(practitionerId, period, deductions, tdsPercent?)` → 201 (`{FAC}DP{YY}{SEQ:5}`; TDS computed under s.194J: nothing within the year's threshold, then 10 % (or `doctorfees.tds.percent`) on the year's fees not yet taxed, 20 % without a valid PAN; `tdsPercent` or the doctor's s.197 certificate can only lower it) |
| GET/PUT | `/tax-profiles/{practitionerId}` | `doctorfees.payouts.manage` | `SaveDoctorTaxProfileRequest {pan (null keeps, "" removes), nameAsPerPan, certificateNumber, certificateRate, certificateValidFrom, certificateValidTo, version}`: PAN encrypted, shown masked |
| GET | `/tds/26q/{fy}/{quarter}` · `/file` | `doctorfees.payouts.manage` | Form 26Q check, then the FVU input file (422 `tds_return.problems` until every month with TDS has a 26Q challan) |
| GET | `/tds/16a/{practitionerId}/{fy}/{quarter}` · `/pdf` | `doctorfees.payouts.manage` | The Form 16A statement to check the TRACES certificate against |
| POST | `/payouts/{id}/approve` | `doctorfees.payouts.manage` | `PayoutVersionRequest` → 204 (not by its preparer) |
| POST | `/payouts/{id}/pay` | `doctorfees.payouts.manage` | `PayPayoutRequest(reference)` → 204 |
| DELETE | `/payouts/{id}?version` | `doctorfees.payouts.manage` | → 204 (draft only) |
| GET | `/statements?practitionerId&from&to` | `doctorfees.payouts.manage` | → `DoctorStatement`: the doctor's fee lines earned in the period (by the IST day each was earned; a cancelled bill adds a negative line dated the day of the cancellation, which the doctor's next payout recovers if the fee was already in a payout), each with its bill, service, billed value, rule and share; the totals earned, reversed, in a payout, paid out and unpaid; and the payouts whose period overlaps it, each with gross, deductions, TDS and net. 400 when `to` is before `from` |

##TDS on doctors' fees follows s.194J and s.206AA (`ProfessionalFeeTds`, rates by financial year): the fees credited to
the doctor in the year by the facility are compared with the threshold (₹30,000 to FY 2024-25, ₹50,000 from FY 2025-26);
the payout that crosses it is taxed on all of the year's untaxed fees (`TdsBasis` records the amount taxed), and
`TdsRemark` carries the return's reason code (`C` without a PAN, `A` under a certificate). The deductor's TAN and the
challans are HR's statutory details; Form 16A itself comes from TRACES. See
[statutory-returns.md](statutory-returns.md#tds-on-doctors-fees-s194j).

## A sharing rule's terms are approved by a second person
A sharing rule decides what a doctor earns of each bill line (DFA-01), and so what the hospital pays out. Its terms
change only when two people agree (maker-checker): whoever keeps the rules (`doctorfees.rules.manage`) asks, and someone
else who approves prices (`masters.prices.approve`, the approver of every price in the catalogue) decides, in
**Approvals**. Nobody approves their own request, and an approval is made only over the terms it replaces (422
`prices.changed` when the rule changed meanwhile).

* **Every change of terms waits**: whose lines it shares, which (service, category, payer type), how much, when, and
  putting it in use **or taking it out of use**. The most specific rule in force decides a line's share, so the lines of a
  rule taken out of use fall back to a broader one (the hospital's default, say), which may share more: that is a change
  like a raise. A new rule is saved with the terms asked, out of use, until they are approved. Its notes are saved at
  once.
* **A rule in use is not deleted** (409): it is taken out of use first, which waits for approval; a rule out of use
  shares nothing and is deleted at once.
* **The approver is told what the change does**: the doctor (or every doctor, for the hospital's default), what it
  applies to, the share before and after, the dates in force, whether it is **backdated** (bills finalized from now for
  services since its start are shared at it, a long stay's final bill, say), and **what it would have changed on the last
  90 days' bill lines**: how many lines it would share differently and their share before and after, worked out by the
  rule that accrues fees, each line shared as on the day the change takes effect.
* Bills finalized while a change waits accrue at the rule in use; fees already accrued are never re-shared. Approved,
  the change is audited as `admin` / `doctorfees.rule_changed` (before and after, who asked, who approved), next to the
  approval's own `approval.requested` and `approval.approved`.

#### The non-payable list is changed by two people
The non-payable list (IRDAI non-payables: admission kits, toiletries and the like) decides who pays a charge on an
insured bill: the payer, or the patient. Both ways of changing it move money, so both wait for a second person
(maker-checker): whoever keeps the payers (`insurance.payers.manage`) asks to **add** an item (for one payer or every
payer; a service or a whole category) or to **take one off**, with a reason, and someone else who approves prices
(`masters.prices.approve`) decides in **Approvals** (`insurance.non_payable`).

* **Added**, the patient pays for the item on the insured bills made from then; until it is approved the payer is charged
  for it, and the list shows it as "to be added". **Taken off**, the payer is charged for it again (and an insurer may
  disallow it at the claim); until it is approved it stays on the list, shown as "to be taken off".
* **The approver is told what it does to the charges not yet billed** of insured patients, on out-patient visits on a
  payer and on stays in progress on one: how many charges, of how many patients, and how much would be charged to the
  patients instead of the payer, or the other way (charges another item of the list already makes non-payable are left
  out).
* One change of an item waits at a time: an item asked to be added is the same item whatever its notes (the same payer,
  and the same service or category), so asking for it again while it waits is refused (409
  `approval.already_requested`). An item the list already has is refused (409 `insurance.non_payable_exists`), and an
  approval adds nothing over the same item put on the list meanwhile (422 `insurance.non_payable_exists`); taking off an
  item removed or changed meanwhile takes off nothing (422 `prices.changed`). Nobody approves their own request.
  Approved, the change is audited as `admin` / `insurance.non_payable_changed` (who asked, who approved), next to the
  approval's own events.

### Tally — `/api/v1/billing/tally`
Everything works on the signed-in user's facility. See [The Tally day book](#the-tally-day-book) for what the day book
holds and how posting, the checks and the transition for older days work.

| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/exports?from&to` | `billing.tally.export` | → `TallyExportDto[]`, the posting log (at most a year; the last 30 days by default) |
| POST | `/exports` | `billing.tally.export` | `PostDayBookRequest(date, to?)` → 204; queues `billing.tally.post` for each day (at most 31, none in the future). 409 `billing.tally_busy` when someone queued one of the days at the same moment |
| GET | `/day-book?date` | `billing.tally.export` | → the day's `AccountingVoucher[]` |
| GET | `/export-file?date` | `billing.tally.export` | → Tally import file (XML through `IAccountingExporter`, else JSON); records the file's fingerprint on the day. Audited as `billing.tally.exported` |
| GET | `/days/{date}/vouchers` | `billing.tally.export` | → `TallyDayVouchersDto`: every voucher of the day book with Tally's answer, then vouchers that left it and vouchers found only in Tally |
| GET | `/days/{date}/check` | `billing.tally.export` | → `TallyDayCheckDto`, the records check as it stands now (nothing kept) |
| POST | `/days/{date}/check?againstTally` | `billing.tally.export` | → `TallyDayCheckDto`; runs the records check, and with `againstTally=true` reads the day back from Tally; keeps the results on the day. Audited as `billing.tally.checked` when Tally is read |
| POST | `/days/{date}/imported` | `billing.tally.export` | → `TallyExportDto`; marks the downloaded file imported (see below). Audited as `billing.tally.imported` |
| GET | `/ledgers` | `billing.tally.export` or `billing.tally.configure` | → `TallyLedgerMap` in effect, built-in names filled in |
| PUT | `/ledgers` | `billing.tally.configure` | `TallyLedgerMap` → 204; validated (every name present, one line, at most 100 characters, known payment modes). Audited as `billing.tally.ledgers` |

### GST e-invoices — `/api/v1/billing/einvoices` (`billing.einvoice`)
See [GST e-invoicing](#gst-e-invoicing). `{source}` is `bill`, `creditnote` (a bill's credit note), `debitnote` (a
bill's debit note to the payer), `pharmacy` or `pharmacyreturn` (a return of a B2B sale). Details carry
`portalConnected`, `ewayBillNeeded`, and on the registration its `provider` (`manual`, `nic`, `gsp`, `simulator`) and
the e-way bill (`ewbNo`, `ewbDate`, `ewbValidTill`). Rows and details carry `type` (`INV` or `CRN`) and, for a credit note, the
invoice it reduces (`against`, `againstDate`).

| Method | Path | Request → response |
|---|---|---|
| GET | `?from&to` | → `EInvoiceRegister`: whether e-invoicing applies and from when, and every B2B tax invoice of the days (the last 30 by default, at most 92, none before `gst.einvoice.from`) with its state (`Pending`, `Registered`, `IrnCancelled`, `NotNeeded`), the date it is due on the portal (30 days after the invoice) and its IRN |
| GET | `/{source}/{id}` | → `EInvoiceDetail`: buyer, place of supply, value, state, the problems that would make the portal refuse it, and the latest IRN with its QR code. 422 `einvoice.not_applicable` before e-invoicing applies |
| GET | `/{source}/{id}/json` | → the INV-01 (version 1.1) JSON, as a list of one invoice for the portal's bulk upload. 422 `einvoice.invalid` with the problems; 422 `einvoice.registered` once it has an IRN |
| POST | `/{source}/{id}/irn` | `RecordIrnRequest(ackNo, signedQrCode)` → `EInvoiceDetail`. The signed QR code must name this invoice (400 `signedQrCode` with what differs); 409 `einvoice.registered` while an IRN is live, 409 `einvoice.irn_exists` for an IRN already recorded; 422 `einvoice.document_cancelled` |
| POST | `/{source}/{id}/irn/generate` | → `EInvoiceDetail`: asks the connected portal for the IRN ([The portal connection](#the-portal-connection)). 422 `einvoice.irp_not_connected`, `einvoice.invalid` (with the problems), `einvoice.irp_refused` (the portal's code and words), `einvoice.irp_unreachable`, `einvoice.irp_mismatch` (the signed QR code is not this document's: nothing recorded); 409 `einvoice.registered`, `einvoice.irn_exists`; 422 `einvoice.document_cancelled` |
| POST | `/{source}/{id}/irn/cancel` | `CancelIrnRequest(reason, remark, version, cancelledOnPortal?)` → `EInvoiceDetail`. Within 24 hours of the IRN only (422 `einvoice.cancel_window`); reasons are the portal's: `Duplicate`, `DataEntryMistake`, `OrderCancelled`, `Other`. Connected to the portal, HC360 cancels it there first (`cancelledOnPortal: true` records one cancelled there by hand); a refusal is 422 `einvoice.irp_refused`. An invoice's IRN is not cancelled while a credit note with an IRN names it (422 `einvoice.credited`) |
| POST | `/{source}/{id}/ewaybill` | `GenerateEwayBillRequest(distanceKm, mode, vehicleNumber?, transporterGstin?, transporterName?, transportDocumentNumber?, transportDocumentDate?, overDimensionalCargo?)` → `EInvoiceDetail`: the e-way bill from the live IRN of an invoice of goods above `gst.ewaybill.threshold`. 422 `einvoice.ewaybill_goods`, `einvoice.ewaybill_not_needed`, `einvoice.not_registered`, `einvoice.irp_not_connected`, `einvoice.irp_refused`; 409 `einvoice.ewaybill_exists`; 400 without a vehicle or a transporter |

### GST returns — `/api/v1/billing/gst-returns` (`billing.gst.returns`)
See [GST returns](#gst-returns). `?month=2026-10`; 400 `month` otherwise; 422 `gst.no_gstin` when the signed-in facility
has no GSTIN, `facility.required` with none selected.

| Method | Path | Body → Response |
|---|---|---|
| GET | `/gstr1` | → `Gstr1Return {gstin, year, month, period (MMYYYY), b2b, b2cl, b2cs, cdnr, cdnur, nil, hsnB2b, hsnB2c, documentsIssued, problems}` |
| GET | `/gstr1/json` | → the GSTN offline tool's GSTR-1 JSON (`GSTR1_<gstin>_<MMYYYY>.json`) |
| GET | `/gstr3b` | → `Gstr3bReturn {outwardTaxable (3.1a), zeroRated, nilAndExempt (3.1c), inwardReverseCharge, nonGst (3.1e), interStateToUnregistered (3.2), itcAllOther (4A5), itcReversed, itcNet, purchaseInvoices, purchasesWithoutGstin, exemptShareOfTurnover, notes}` |

### Gateway reconciliation — `/api/v1/billing/gateway`
Every route needs `billing.gateway.reconcile` and works on the signed-in facility; a user without one gets 422
`facility.required` on every route. See
[Gateway reconciliation](#gateway-reconciliation-int-pg) for what happens behind them.

| Method | Path | Request → response |
|---|---|---|
| GET | `/summary` | → `ReconciliationSummaryDto` (reconciliation on or off, expected settlement days, open exceptions by kind with count and amount, last settlement day and when it was read) |
| GET | `/settlements?from&to` (IST days, the last week by default, at most 92 days) | → `SettlementDayDto[]`: per day the count and totals (payments, refunds, adjustments, charges without GST, GST, paid to the bank, transactions not matched) with that day's `GatewaySettlementDto` rows (id, UTR, status, `difference` between the payout and its transactions). A settlement another facility holds on a shared merchant account counts with this facility's part only (`isShare`: its own transactions' amounts, charges and payout, no difference) |
| GET | `/settlements/{id}` | → `GatewaySettlementDetail`: the settlement and its `SettlementLineDto` transactions (type, gateway id, method, time, amount, fee with GST, net, match, the receipt or refund number). A facility that shares the merchant account sees only its own transactions, and the settlement's totals as its part (`isShare`) |
| POST | `/settlements/fetch` | `FetchSettlementsRequest(date)` → `SettlementFetchResult(date, settlements, added, updated, message)`; reads that day from the gateway now (today or up to a year back). 422 `billing.gateway_unavailable` with the gateway's reason |
| GET | `/exceptions?status&kind` (paged) | → `PagedResult<GatewayDiscrepancyDto>`: open ones worst first (kind, then amount, then age); `status=Resolved` newest first with who resolved it and the note |
| GET | `/exceptions/{id}` | → `GatewayDiscrepancyDto` (summary in words, amounts in HC360 and at the gateway, bill, receipt, refund and appointment by number, `canRecordReceipt`, `version`) |
| POST | `/exceptions/{id}/record-receipt` | → `DiscrepancyActionResult(message, discrepancy)`; only for money taken without a receipt. Records the receipt against its bill, admission or booking after asking the gateway; a payment on an order a booking had before its current one is recorded as its webhook would have (the booking's own, or a second payment kept as an advance with its refund asked for); if the bill, admission or booking can no longer take it (a closed admission), keeps it as a patient advance. 422 `billing.payment_unlinked` when HC360 does not know what the payment was for. Audited `billing.gateway.receipt_recorded` |
| POST | `/exceptions/{id}/retry` | → `DiscrepancyActionResult`; by kind: asks the gateway again, reads the settlements of the next five days again, sends a failed gateway refund again (audited `billing.refund.gateway_retried`; 422 `billing.refund_retrying` while another request is sending it), or matches the settled transaction again. 422 `billing.nothing_to_retry` for a settlement-level difference |
| POST | `/exceptions/{id}/resolve` | `ResolveDiscrepancyRequest(note, version)` (5–500 characters) → `GatewayDiscrepancyDto`; 409 when someone changed it meanwhile. Audited `billing.gateway.exception_resolved` |

### Card and UPI reconciliation — `/api/v1/billing/pos/settlements`
Every route needs `billing.gateway.reconcile` and works on the signed-in facility (422 `facility.required` without
one). See [Card and UPI reconciliation](#card-and-upi-reconciliation-int-pos) for what happens behind them.

| Method | Path | Request → response |
|---|---|---|
| GET | `/summary` | → `PosSettlementSummaryDto` (expected settlement days, transactions to look at by reason with count and amount, payments not settled (Billing's receipts and the pharmacy's and canteen's payments) with their total, the last payout day, the last report imported, the other counters' terminals) |
| POST | `/imports` | multipart `file` (CSV or .xlsx, at most 10 MB), `acquirer` (at most 64 characters), `settledOn` (only for a report without a settlement date column) → 201 `PosSettlementImportResult(import, columns, problems, matched, toLookAt, message)`. 400 `file` when the report cannot be read (no headings found, an .xls file, too many rows; the message says why), 400 `settledOn` when it has no settlement dates and none was given, 409 `billing.pos_settlement_imported` for a report imported before (same content) that would bring nothing new, 409 `billing.pos_settlement_busy` when another import or match ran at the same time. Audited `billing.pos.settlement_imported` |
| GET | `/imports` (paged) | → `PagedResult<PosSettlementImportDto>` newest first (acquirer, file, rows, new, already imported, left out, the days paid, who imported it; `withdrawnOn`, `withdrawnByName` and `withdrawReason` once withdrawn, `withdrawalAskedAt` while a withdrawal waits for approval) |
| POST | `/imports/{id}/withdraw` | `WithdrawPosImportRequest(reason)` (5–500 characters) → 202 `ApprovalRequestDto` (kind `billing.pos_import_withdrawal`): asks to withdraw a report imported in error; the approver is shown its transactions, what they paid out, the days, the receipts and refunds they matched and the transactions reviewed. Someone else with `billing.refunds.approve` approves it (see [A report imported in error](#card-and-upi-reconciliation-int-pos)). 404 for an unknown report, 409 `billing.pos_settlement_import_withdrawn` for one withdrawn already, 409 `approval.already_requested` while a withdrawal of it waits. Audited `billing.pos.settlement_import_withdrawn` when approved |
| GET | `/days?from&to` (IST days, the last month by default, at most 92 days) | → `PosSettlementDayDto[]`: per day the payouts (`PosPayoutDto`: UTR, acquirer, transactions, payments, refunds and chargebacks, charges without GST, GST, paid to the bank, to look at, and the settlement journal's number) |
| GET | `/lines?attention&settledOn&utr&noUtr` (paged) | → `PagedResult<PosSettlementLineDto>`: `attention=true` the ones to look at, worst first (no receipt, amount differs, no refund, chargeback); `settledOn` with `utr` (or `noUtr=true`) one payout's transactions. Each with its references (RRN, approval code, card's last four digits, TID), amounts, what it matched (receipt or refund number and bill, or `counter` and `counterDocument`: the pharmacy's invoice or the canteen's bill; how it was found) and any review |
| POST | `/lines/{id}/review` | `ReviewPosSettlementLineRequest(note, version)` (5–500 characters) → `PosSettlementLineDto`; 422 `billing.pos_settlement_matched` for a line that matches, `billing.pos_settlement_reviewed` for one already reviewed, `billing.pos_settlement_withdrawn` for one whose report was withdrawn; 409 when someone changed it meanwhile. Audited `billing.pos.settlement_reviewed` |
| POST | `/match` | → `PosMatchResult(checked, matched, toLookAt, message)`: matches again the transactions without a receipt or refund, and those set aside as another counter's, settled in the last 90 days, for example after a missing receipt or a counter's sale was recorded |
| GET | `/unsettled` | → `UnsettledPosReceiptDto[]` oldest first: card and UPI receipts no report carries although the reports already go `settlementDays` past them, leaving out payments reversed as not received (receipt and bill, mode, amount, reference, card's last four digits, HC360's terminal, days waiting); with them the pharmacy's and the canteen's card and UPI payments no report carries although the reports carrying that counter's payments of that mode go `settlementDays` past them (`receiptId` and `billId` null, the invoice or bill as `receiptNumber`, `counter`, and `cancelled` when the invoice or bill was cancelled since) |
| GET | `/training-file?date` | → `text/csv` `training-card-settlement-<date>.csv`: the training terminal's approved sales of the day as an acquirer would report them, to practise the import; 422 `billing.pos_training_server` on a live server |

### Chargebacks — `/api/v1/billing/disputes`
Every route needs `billing.gateway.reconcile` and works on the signed-in facility (422 `facility.required` without
one). See [Chargebacks](#chargebacks) for what happens behind them.

| Method | Path | Request → response |
|---|---|---|
| GET | `/?open&terminal` (paged, at most 100) | → `PagedResult<PaymentDisputeDto>`: `open=true` the cases not yet won or lost, by their deadline (soonest first, those without one last); `open=false` the closed ones, newest first; `terminal=true` card and UPI terminal payments, `false` the gateway's. Each with its number, where it came from (`Gateway`, `Terminal`, `Notice`), status (`Open`, `Contested`, `Won`, `Lost`), the receipt, payment line, bill, mode and reference (the gateway's payment id or the RRN), the amount taken back, the notice's reason code, reason and answer-by date with `daysLeft` (negative when late; null once answered), the evidence sent, who asked to accept the loss and `lossWaiting` while that waits for a second person, how it ended and the reversal's number |
| GET | `/summary` | → `PaymentDisputeSummaryDto(open, openAmount, dueSoon, late)`: open cases and what they took back; those not answered whose deadline is within two days, or past |
| GET | `/{id}` | → `PaymentDisputeDto`; 404 for another facility's case |
| GET | `/payment-lines?receiptNumber` | → `DisputablePaymentDto[]` (id, mode, amount, the gateway's payment id or the recorded reference, and whether a case on it is open): the receipt's card, UPI and online payment lines, to choose the one a notice names; 404 for a receipt not in this facility |
| POST | `/` | `OpenPaymentDisputeRequest(receiptNumber, paymentId?, amount, reasonCode?, reason?, respondBy?)` → 201 `PaymentDisputeDto`: a case opened from the bank's or the gateway's notice. 400 `receiptNumber` for a receipt not in this facility, `paymentId` when the receipt has more than one card, UPI or online payment and none is chosen, `amount` for more than the payment; 422 `billing.dispute_not_card` for a receipt with no card, UPI, net banking, wallet or gateway payment; 409 `billing.dispute_open` while a case on the payment is open. Audited `billing.dispute.opened` |
| POST | `/{id}/details` | `DescribePaymentDisputeRequest(reasonCode?, reason?, respondBy?, version)` → `PaymentDisputeDto`: what the notice says and the date to answer by (a new date is reminded again). Audited `billing.dispute.described` |
| POST | `/{id}/evidence` | `PaymentDisputeEvidenceRequest(sentOn, note, version)` (note 5–500 characters) → `PaymentDisputeDto` `Contested`; 400 `sentOn` after today. Audited `billing.dispute.evidence_sent` |
| POST | `/{id}/won` | `PaymentDisputeNoteRequest(note, version)` → `PaymentDisputeDto` `Won`; 422 `billing.dispute_loss_waiting` while accepting the loss waits for a second person. Audited `billing.dispute.won` |
| POST | `/{id}/accept-loss` | `PaymentDisputeNoteRequest(note, version)` → 202 `ApprovalRequestDto` (kind `billing.receipt_reversal`): the receipt's payment line is asked to be reversed with reason `Chargeback`; someone else with `billing.refunds.approve` approves it, and the case is lost when they do. 422 `billing.dispute_part` when the case took back part of the payment. Audited `billing.dispute.loss_asked`, then `billing.dispute.lost` |

All of them answer 422 `billing.dispute_closed` for a case won or lost, and 409 when someone changed it meanwhile.

### Patient portal — `/api/v1/portal/bills`, `/api/v1/portal/refunds` (`patients.portal`, own/linked patients only)
| Method | Path | Request → response |
|---|---|---|
| GET | `/` · `/{id}` | → `PortalBillListItem[]` / `PortalBillDetail` (lines with `isReturn`, the bill's own `refunds`, no staff names or internal codes) |
| GET | `/{id}/pdf` | → `application/pdf` `bill-<number>.pdf`: the same [printed bill](#the-printed-bill) the counter prints, for the bills listed above only (finalized, part-paid, paid, refunded); audited ("Downloaded bill … as a PDF (patient portal)"); 503 `pdf.unavailable` when the server cannot make PDFs |
| POST | `/{id}/pay` | → `PortalPaymentOrder` (gateway checkout / link) |
| POST | `/{id}/payments/{orderId}/verify` | → `PortalPaymentStatus` |
| GET | `/api/v1/portal/refunds?patientId=` | → `PortalRefund[]`, newest first, of the patients the account sees (or of the one asked for; 403 `portal.patient_denied` for anyone else): number, bill number, purpose, amount, `stage`, `requestedAt`, `sentAt`, `mode`, `reference`, `creditedAt` |

Where a refund stands, for the patient (`PortalRefunds.StageOf`, first match):

| `stage` | When | `reference` |
|---|---|---|
| `Requested` · `Approved` · `NotApproved` | the refund's status (the reason for a rejection is a staff note and is not sent) | none |
| `Replaced` | paid, then recorded as not paid out (`notPaidOut`); the new refund that pays it is listed on its own | none |
| `KeptAsAdvance` | paid as an advance (`Advance`) | none |
| `Credited` | sent through the gateway and `Processed`; `creditedAt` is when the gateway said so | `gatewayReference` (ARN, RRN or UTR) |
| `BankReturned` | sent through the gateway, `Failed`, and the counter can send it again: not being sent again, or a retry that stopped part way (5 minutes, the rule that lets the counter send it again) | none |
| `WithTheBank` | sent through the gateway and waiting for the bank, or being sent again | `gatewayReference` once the gateway gives one |
| `PaidOut` | paid at the counter | the counter's transfer, card-reversal, UPI or cheque reference; none for cash |

`sentAt` is when the gateway took the refund (its latest attempt), otherwise when it was paid; `mode` is sent only once
it is paid. The gateway's refund id and error text, the staff's names and the approver's remarks are never sent.

## The billing desk's decisions wait for a second person

MAKER-CHECKER.md, slice 6. Each is an approval request (`platform.approval_requests`), decided in the Approvals inbox,
never by whoever asked (`approval.self`), and made only over the record as it was asked (`approval.stale`), audited with
both names.

* **Discounts.** Everyone discounts on their own only up to their own limit (`DiscountLimit`, else
  `billing.discount.defaultMaxPercent`); `billing.discounts.approve` gives no more (`DiscountPolicy` has no approver
  exemption, and nobody is recorded as approving a discount given within a limit). Above it the discount waits as a
  `DiscountApproval` for another approver (`billing.discount_self_approval`), who is recorded on the bill
  (`DiscountApprovedBy`, `ApprovedDiscountAmount`). Finalisation still checks the finaliser's own limit, so a discount
  within a cashier's limit and not approved by anyone is refused at finalisation by a colleague with a lower limit, who
  asks for approval. Free treatment by law is unaffected.
* **Discount limits** (`billing.discount_limit`). Raising a user's limit (a higher percentage, a rupee cap raised or
  taken away, or their limit removed so a higher default applies) is asked for by a holder of `billing.discounts.approve`
  and approved by another, never the user whose limit it is (`billing.discount_limit_own`, which also refuses asking to
  raise one's own). The approver reads "the default (0%) → 20%". Lowering is saved at once. Audited
  `billing.discount_limit.raised`.
* **Charges cancelled at the desk** (`billing.charge_cancel`). An OT (surgery), procedure, implant (`OT-IMPLANT`,
  `PHARM-IMPL`, or any charge with device details) or bed-day charge always waits; any other waits when its net amount is
  above `billing.chargeCancel.approvalAbove` (₹1,000). A credit (a return) cancelled never waits. Someone else who holds
  `billing.discounts.approve` approves; the approver reads the service, the patient, the amount, who posted it and why it
  waits. The charge stays unbilled and billable meanwhile, and is cancelled (taken off its draft first) only if it is
  still unbilled at the amount asked; billed meanwhile, the approval fails and a credit note is asked for. Audited
  `billing.charge.cancelled`. A module's own cancellations (`IChargePoster.CancelAsync`: an order withdrawn, a dispense
  returned, a visit or stay cancelled, a package changed) are not desk cancellations and are made at once.
* **Cash shortages** (`billing.shift_shortage`). A drawer closed short by more than `billing.shift.shortageTolerance`
  (₹0) closes the shift with the cashier's reason and marks it `shortageAwaiting`: the shift list and the day-end report
  show "closed — shortage awaiting supervisor" (`DayEndReport.shortagesAwaiting`), and the Tally records check says no
  supervisor has accepted it. Someone else who holds `billing.refunds.approve`, never the cashier whose shift it is
  (`billing.shift_shortage_own`), accepts it; the shift keeps who accepted it and when (`shortageAcceptedBy`,
  `shortageAcceptedAt`), audited `billing.shift.shortage_accepted`. Rejected, withdrawn or expired, the shortage still
  awaits and is asked again (`POST /shifts/{id}/shortage`). An excess is recorded and flagged (`billing.shift.variance`,
  Warning) and needs no approval: no money is missing. The shortage stays the shift's; nothing is written off.
* **A doctor's consultation fee** (`masters.practitioner_fee`, masters.md). The fee `VisitChargesHandler` charges through
  `PriceResolver.ConsultationOverride` (a payer's negotiated tariff still wins, and no fee of the doctor's own means the
  service's approved price) changes only when someone who approves prices agrees; the fee in force is charged meanwhile.
  `PriceResolver` is unchanged.

## A third party pays only on a second person's word

The desk chooses who pays for a visit (registration, an appointment's check-in), the admission desk for a stay, and the
billing desk changes a stay's payer. An insurer, a company or a government scheme pays (and its plan prices the
services: a company's open credit, an insurer's PPN rates) only when a second person already stands behind it
(`PayerChoice`, with `IPayerDirectory.StandingAsync`):

* **The payer is named, in use, and of the kind chosen.** "Company" with no company named, or with an insurer, is
  refused (400 `payerId` / `payerType`): an insurer billed as a company would pay without its approval. The patient,
  staff and charity name no payer; one sent with them is not kept, so no payer's plan prices a paying patient.
* **The patient's policy or membership with it is in date that day, and verified with the payer by someone other than
  whoever recorded its terms or last changed them.** Else 422 `payer.no_cover` (none recorded and valid today) or
  `payer.cover_not_verified` (recorded, not yet so verified), each saying what to do: record the card on Insurance ›
  Patient policies, have someone else verify it, and meanwhile register the patient as paying (an emergency is admitted
  as paying, and the stay is put on the payer once the policy is verified).
* **Verification is the second person's act.** Whoever recorded a policy, or last changed what a verification vouches
  for (payer, TPA, number, member, holder, relation, validity, sum insured, co-pay, in use), may not verify it (403
  `insurance.policy_self_verify`); such a change withdraws an earlier verification. A policy its own recorder verified
  before this rule does not count. The policies screen tells its recorder that someone else verifies it.
* A visit keeps the payer it was registered with; a stay already on a payer keeps it while its terms change. A company's
  health check-up opens its visit from the company's batch (with the company's order number), not at the desk.

## PM-JAY procedures (HBP)
AB PM-JAY pays fixed rates for the procedures of its Health Benefit Package master (HBP), which the National Health
Authority publishes and the State Health Agencies pass on as Excel. Each procedure code is two letters for the
specialty, three digits for the package and, for a stratified procedure, a letter (`BM001A` to `BM001D` are thermal
burns by %TBSA band). The master also gives the rate, the expected stay, what the stratification is by, and whether the
scheme reserves the procedure for public hospitals.

* **Import.** Whoever keeps the tariffs (`billing.tariffs.manage`) imports the master (**Billing › PM-JAY procedures ›
  Import master**), as .xlsx or CSV, under the name NHA gives it, with a reason for the approver (the circular that
  issued it, say). HC360 finds the columns by their headings rather than their place, since state copies order and
  name them differently: the procedure code and name are required; the specialty and its code, package name, rate or
  price, length of stay, reservation and stratification are read when present. A rate with a rupee sign or commas is
  read; a row whose code is not an HBP procedure code, has no name, or repeats an earlier code is left out and listed
  back with its row number.
* **A second person puts it in force (maker-checker).** The import is staged, not applied: HC360 keeps the procedures
  it read exactly as they will be applied, with the SHA-256 of the file and of those rows, and asks someone else who
  may approve prices (`masters.prices.approve`, the same approver as every price in the catalogue) to approve it
  (**Approvals**, kind `billing.hbp_master`). The request shows the master and its file, the file's SHA-256 (to match
  with `sha256sum` or `certutil -hashfile <file> SHA256` against the copy NHA or the State Health Agency sent), what it
  changes (procedures read, new, changed and retired, the largest changes of scheme rate, procedures newly reserved for
  public hospitals, the rows not read) and, by name, the hospital's **packages mapped to procedures it retires,
  newly reserves for public hospitals, or rates otherwise**. Approved, the rows staged are applied, in the approval's
  transaction, only if they still match their hash (422 `hbp.import_altered` otherwise) and only over the master they
  were read against (422 `hbp.master_changed`: what it would change is no longer what was approved; import the file
  again). One import waits at a time; the person who asked may withdraw it; nobody approves their own. A file that
  would change nothing is refused (422 `hbp.unchanged`). The master put in force is audited as `data` /
  `billing.hbp.imported` (version, file name and SHA-256, the rows' SHA-256, counts, who asked, who approved), next to
  the approval's own `approval.requested` and `approval.approved`.
* **The master's history.** **Billing › PM-JAY procedures › Imports** lists the latest imports: which master was put in
  force when, from which file, asked for and approved by whom, and an import still waiting, refused (with the
  approver's reason), withdrawn, expired or not applied (with why). An import is kept once decided.
* **A later master** updates the procedures it lists, adds the new ones, and **retires** (does not delete) those it no
  longer lists: a package or claim that names a retired procedure still reads, but a package cannot newly be mapped
  to it.
* **Mapping a package.** A package the scheme covers names its HBP procedure (`hbpCode`, on the package drawer, which
  shows the procedure's name, the scheme rate and whether it is reserved for public hospitals). The code must be in the
  imported master and active; a package already mapped to a procedure a later master retired keeps it until changed.
  The package's own price is not tied to the scheme rate, which states weight by tier and incentives.
* **Claims to the scheme** name the HBP procedure on each line and carry the IG's `AB-PMJAY` programme code
  ([NHCX claim documents](integrations.md#nhcx-claim-documents-not-live)).

## Packages on OP bills
A visit can be billed at a package (MB-02, OPB-01..07), as an admission can: for example a cataract operation done as
day care, a minor procedure, or dental work done in one sitting. Each package says what it is **used for**: admissions,
out-patient visits, or both. Packages made before out-patient bills could use them are for admissions.

* **Choosing the package.** On a draft OP bill, whoever prepares OP bills (`billing.op.create`) picks an active package
  for out-patient visits whose tariff plan is the bill's, or any plan (`PUT /bills/{id}/package`). The package becomes
  one line at its price, a `billing.package` charge (reference `{billId}:{unique id}`) with the visit's doctor, and
  `bills.package_id` records it.
* **Package vs actuals.** The package absorbs the services it includes, up to each inclusion's quantity or amount cap,
  earliest service first, exactly as an admission's package does (`PackageCalculator`). The rest is billed as usual and
  can be discounted. Lines of category Package (the package itself, a health checkup) are never absorbed. The cover is
  worked out again whenever the draft's lines change: a service added or removed, new services picked up, or a service
  cancelled by the module that charged it (`OpPackageCover`).
* **Changing or removing it.** Choosing another package replaces the line; choosing none, or removing the package's
  line, bills every service in full again. The package line belongs to its bill alone. It is cancelled when the package
  is changed or removed, when the draft is discarded, and when the finalized bill is cancelled: the other services
  return to the unbilled list, and the next bill chooses its package again.
* **What the bill shows.** `BillDetail.package` gives the package, its price as billed, the services at full price, what
  it covered and what is billed beyond it, for an OP bill and for an IP final bill (whose package is the IP account's).
  The staff bill screen shows them, with the package picker on OP drafts, and the printed bill names the package.
* An admission cannot be given a package for out-patient visits only, nor one priced for another tariff plan than the
  stay's: as an OP bill, a stay takes a package for its plan or for any plan.

## The printed bill
The A4 bill is made on the server (`Documents/BillDocument.cs`) from `BillDetail` and the facility letterhead, so the
copy printed at the counter, the staff PDF and the patient's PDF are the same document. The 80 mm counter copy is still
printed by the staff app. Its labels are in the language it is made in (PP-06): English at the counter, the one
chosen in the portal for a portal download, the record's preferred language with the "bill shared" message, where a Hindi bill is titled "कर बीजक (Tax invoice)" or "आपूर्ति बिल
(Bill of supply)"; amounts, codes and the amount in words stay as they are (print-templates.md, *The patient's
language*).

* Title: "Tax invoice" when any line carries GST, otherwise "Bill of supply"; IP bills add "IP bill" or "IP final bill".
  A draft prints as "Estimate (draft bill)" with DRAFT across every page; cancelled and refunded bills print CANCELLED
  and the cancellation date and reason.
* Header: logo, legal name, address, phone and e-mail, GSTIN and PAN, registration number; the title, bill number and
  date on the right.
* Patient, UHID, status, payer, tariff, package and co-pay; every line with service code, doctor, date, SAC code, quantity, rate,
  package cover (when a package covers anything), discount and, on a tax invoice, taxable value, GST rate, CGST and SGST
  (or IGST, with the place of supply, for a payer registered in another state).
* Totals from the gross amount to the net amount, the payer's and the patient's share, credit notes, paid, refunded,
  and the balance due or the refund due; the patient's share in words (Indian system, "Rupees … Only").
* On a tax invoice, the GST summary by SAC code and rate. Then the payments received (advances adjusted included), the
  bill's notes, signature lines for the patient and the authorised signatory, and the facility's letterhead note.
* A B2B tax invoice of e-invoicing's time carries its IRN, acknowledgement and signed QR code below the patient's box;
  while it has no IRN, a notice in their place says it is not yet a valid tax invoice for the buyer (GST rule 48(5)),
  in the bill's language ([GST e-invoicing](#gst-e-invoicing)). The 80 mm bill says the same.
* Every page's foot names the patient, UHID and bill number, with "Page X of Y".
* The **Bills** print template (Hospital setup › Print templates) decides the rest: on pre-printed stationery the
  header keeps only the GSTIN and PAN line with the title, number and date, and the page leaves the blank space set for
  the stationery; the logo can be left off; a closing note ends the bill. The 80 mm counter bill and the IP running bill
  follow the same template (an 80 mm roll always prints the letterhead); receipts and refund vouchers follow
  **Receipts and refund vouchers**. See `docs/modules/print-templates.md`.

## The rate list
The Clinical Establishments (Central Government) Rules 2012, r.9(i), make a registered establishment display the rates
it charges for each type of service and facility, in the local language as well as in English, where patients can see
them; the states' own Acts say the same. *Billing › Rate list* shows them and prints them for display
(`Domain/RateList.cs`, `Services/RateListService.cs`, `Documents/RateListDocument.cs`).

* **What a self-paying patient is charged, worked out as a bill works it out.** The plan is the one that prices a
  self-paying patient at the facility (`TariffService.ResolvePlanAsync(Self, …)`); each price is `PriceResolver`'s
  (the plan's price in force today, else the base price), less the plan's concession on a service that allows a discount,
  with GST on the rest (a room's by `RoomRentGst`), by `ChargeAmounts` — what `Charge.ApplyPrice` does for a bill's line.
  The list shows the rate before GST, the GST rate, and what the patient pays.
* **Parts.** The registration fee (REG-NEW, unless `billing.registrationFee.enabled` is off); each doctor's
  consultation (consultants, visiting doctors and residents, `PractitionerKinds.Doctors`: a visit with any of them is
  charged) in person and, when they consult by video, by video (their own fee, else the CONS-NEW
  or CONS-TELE price, as `VisitChargesHandler` charges), with their free follow-up days (a follow-up is free within them
  and a new consultation after, so CONS-FU has no price on the list); each class of bed of the facility's wards
  (`IRoomClassDirectory`, from Inpatient): a day of it with its GST, the nursing and consultant-visit charges of a day
  there, and the plan's multiplier for in-patient services in it; every other service in use, by category; the packages
  the plan may bill, at their price.
* **Marked prices.** A service the counter may price (`isPriceEditable`) is marked; one with no price of its own (the
  pharmacy's, charged at MRP) shows "By what is used".
* **Languages.** Each facility has a local language (`Platform/Localization/LocalLanguages.cs`): the one the hospital
  chose in the setting `billing.rateList.language` (a code or name, for the hospital or one facility), else the official
  language of the state its address names, else of its GSTIN's state. The state is read however it is written
  (`SharedKernel/IndianStates.cs`: "TN", "Tamilnadu", "U.P.", "Orissa", "Pondicherry", ISO and vehicle codes), and a
  facility's state is saved as GST writes it; one that names no state of India is refused. A state whose only official
  language is English, or that has several on an equal footing (Jammu and Kashmir, Chandigarh, Sikkim, Arunachal
  Pradesh, Nagaland, Meghalaya, Dadra and Nagar Haveli and Daman and Diu, Ladakh), gives none: the hospital chooses
  one. Puducherry's is the district's, by PIN code: Tamil, Malayalam in Mahe, Telugu in Yanam.
  * The list's own words are in English with the local language after them when HC360 writes it (Hindi, Tamil);
    otherwise in English.
  * Each service has a name in each language the hospital enters (`ServiceItem.LocalNames`, by language code; *Setup ›
    Services* offers one for each active facility's language): a facility's list prints the one in its own language
    under the English name, so one service prints in Tamil at a Chennai facility and in Hindi at a Delhi one.
  * The screen says which languages the list is in, and when the local language is not known, why: the state it could
    not read, a state with no one language, or a setting that names none.
  The texts are `doc.rates.*` in the patient text catalogues (`docs/requirements/hindi-review.md`, `tamil-review.md`).
* It prints under the **Other documents** template (print-templates.md). Print it again when prices change: the list
  says the day it is as on.
* Insurance, corporate and scheme patients pay their plan's rates; the list says so.

### On the portal
The same list is on the patient portal for anyone to read without signing in (*Rate list*, `/portal/rates`, linked
from the sign-in page and *Bills*), so a patient can see what a visit costs before coming
(`Features/Tariffs/PublicRateListEndpoints.cs`).

* **The counter's list, to the line.** The endpoint reads `RateListService.ForFacilityAsync` for the facility in its
  path, as the counter's screen does for the signed-in one; what the server does not know to be the facility's is not
  read through the anonymous request (every query of the list names the facility). It leaves out `unknownSetting` and
  `unknownState`, which are for staff to correct.
* **Which facilities.** Every active facility, unless the hospital sets `billing.rateList.publish` to false for it (or
  for all). The counter prints the list either way.
* **The page.** A branch to choose where there are several, a search by a service's English or local name or code, a
  doctor's name or department, or a ward; each service's name in the facility's language under the English one, marked
  with that language (`lang`, so a screen reader reads it as Tamil or Hindi: WCAG 3.1.2); and the printed list's notes.
  Its own words are in the patient's language (English, Hindi or Tamil), in the printed list's wording
  (`i18n/messages/rates.ts`). *Download PDF* gives the list the counter prints; *Print* prints the page.
* **The PDF.** The server's browser makes the PDFs staff print and send, a few at a time. Anonymous downloads take at
  most one of its places, and each version of the list is made once (`Services/PublicRateListPdf.cs`: kept by the
  SHA-256 of its HTML, an hour after it was last asked for), so they cannot hold up staff's.

## Credit notes are GST credit notes
A credit note corrects what a bill charged (a wrong rate, a service not given) and, under CGST Act 2017 s.34, lowers
the taxable value and the GST of the supply with it. A write-off of a due that cannot be recovered is not one: GST was
due on the supply whether or not it was paid, and a write-off reduces no tax.

* **It names the services it corrects.** On a bill with GST a credit note lists the bill lines it corrects and what it
  takes off each, GST included (400 `lines` without them): the GST credited is each line's own, at its own rate, so a
  credit on an exempt service gives back no GST and one on room rent above ₹5,000 gives back its 5%. A share of the
  whole bill would get both wrong. A line is credited at most to its value, less what earlier credit notes took off it
  (422 `billing.credit_exceeds_line`); a return or a line that comes to nothing is not credited. On a bill without GST
  the services may be named or not.
* **The split is exact.** Each line's credit is split into taxable value and GST in proportion to the line
  (`GstCreditNotes.Share`, in the shared kernel), as IGST on a supply to another state and as CGST and SGST within it.
  Each credit's share is the difference of the shares of the running totals before and after it, so however a line is
  credited, its credits add up to its own taxable value and GST to the paisa. The note keeps its lines
  (`credit_note_lines`) and its CGST, SGST and IGST.
* **Until 30 November after the financial year** (s.34(2)). The GST of a bill can be reduced by a credit note issued by
  30 November after the end of the financial year (April to March) of the bill (`GstCreditNotes.LastDay`). One issued
  later lowers what the patient owes and not the GST (`gstTimeBarred`): its lines carry no GST, the books take back the
  value only, and it is not reported to the e-invoice portal. The approver is shown which it will be, and the printed
  note says so.
* **The approver sees the GST.** The approval request lists each service with what it takes off and the GST in it, and
  the GST credited in all.
* **The books.** The Tally Credit Note debits sales returns with the taxable value and the output CGST, SGST or IGST
  ledgers with the GST ([The Tally day book](#the-tally-day-book)), so the GST in the books falls with the GST the
  portal is told of. A write-off posts as before.
* **The printed note** (`CreditNoteDocument`, `GET /bills/{id}/credit-notes/{noteId}/document` and `/pdf`) carries what
  CGST rule 53(1A) asks of a credit note: the hospital's name, address and GSTIN, "Credit note", its number and date,
  the recipient (a payer registered for GST, with its GSTIN and address; the patient otherwise), the number and date of
  the tax invoice it reduces, each service with its taxable value, GST rate and the GST credited, and the signature
  line; and its IRN and signed QR code once the portal registered it. A write-off prints as a write-off note, which says
  it is not a GST credit note. On the bill page each note has Print and PDF buttons, and its GST.
* **Notes issued before** services were named keep no lines and took back no GST; they print and post as they did.

### Payer credit notes
When a payer (insurer, TPA or corporate) settles less than its share, the settlement records the deduction and the
payer owes that much less (`payerDisallowedAmount`). Where the deduction is a price the hospital accepts (a tariff
disallowance), the payer's invoice must be reduced in GST terms too, or the hospital pays output tax on money it never
gets and the payer's GSTR-2B keeps the full credit. A **payer credit note** (`kind: PayerShare`) does that:
* up to what the payer's settlements disallowed, less earlier payer credit notes (422 `billing.payer_credit_exceeds`);
* naming the services, which must be ones the payer pays for (422 `billing.credit_not_payer_payable`), each taking back
  its own GST as any credit note does, and within the s.34(2) time limit;
* asked for and approved as a credit note is (approval `billing.credit_note`, the details showing the payer's share,
  settled, disallowed and already credited), numbered in the credit note series, linked to the bill;
* it leaves what the patient owes as it was, prints to the payer, and is a `CRN` e-invoice to a payer registered for
  GST. In the Tally day book its Credit Note debits sales returns and output GST and credits **Insurance
  Disallowances**, taking back the deduction the claim's journal put there.

## Debit notes
A **debit note** (supplementary invoice, CGST Act s.34(3), CGST rule 53) raises what a finalized bill charged for
services on it: a rate charged short, a quantity under-billed. The billing desk names the services and the taxable
value each is raised by; GST is added at each service's rate, IGST to another state or CGST and SGST within it
(`DebitNotes.Gst`). It is charged to the patient (`debitAmount`: the bill owes again, a paid bill becomes partly paid)
or to the payer (`payerDebitAmount`, added to what the payer owes). One person asks and another issues it (approval
`billing.debit_note`, permission `billing.refunds.approve`), on the bill as it was asked against. It is numbered in its
own series (`billing.debitnote`, `{FAC}DB{YY}{SEQ:6}`) and listed on the bill (`debitNotes`). A debit note to a payer
registered for GST on a bill with GST is a `DBN` e-invoice naming the bill (`DebitNoteEInvoiceSource`), after the
bill's IRN. It is in GSTR-1 (CDNR, or netted into B2CS for a patient) and GSTR-3B.

## GST e-invoicing
GST rule 48(4) requires a B2B tax invoice of a taxpayer above the notified turnover to carry an IRN from the Invoice
Registration Portal (IRP). The date e-invoicing applies from is the setting `gst.einvoice.from`; before it, and when it
is not set, nothing is listed and nothing can be recorded.

* **Which invoices.** Bills with GST to a payer registered for GST (a corporate or an insurer with a GSTIN in the payer
  master), and the pharmacy's B2B counter sales. Each module lists its own through the Platform contract
  `IEInvoiceSource` (`BillEInvoiceSource`, `PharmacyEInvoiceSource`); Billing keeps the register (`billing.einvoices`).
  Bills of supply (no GST) and sales to patients are not e-invoices.
* **Credit notes too.** A credit note against a B2B tax invoice is an e-invoice of type `CRN`: a bill's credit note
  that names its services, issued while the bill's GST could still be reduced, on a bill that is an e-invoice
  (`CreditNoteEInvoiceSource`, source `creditnote`), and the pharmacy's return of a B2B sale
  (`PharmacyReturnEInvoiceSource`, source `pharmacyreturn`). It goes to the bill's or the sale's buyer, at the
  invoice's place of supply, with each service or medicine at the taxable value and GST it takes back: a bill's service
  as one unit at that value, a medicine at the quantity returned with the GST carved out of its refund at the sale
  line's rate, exactly as the pharmacy's day book carves it for the accounts. A write-off, a credit note after the time
  to reduce the GST, and a return after it are not reported.
* **The JSON.** `InvoiceRegistrationPayload` writes the portal's INV-01 schema, version 1.1: B2B, `INV` or `CRN` (a
  credit note names the invoice it reduces in `RefDtls.PrecDocDtls`, its number and date), the facility as
  the supplier (legal name, address line, city, PIN code and state from its GSTIN), the buyer with the place of supply,
  every line with its SAC (services, `IsServc` Y) or HSN code (medicines, unit `NOS`), quantity, unit price, discount,
  taxable value, GST rate and IGST or CGST and SGST, and the totals. A pharmacy line goes at its taxable value (MRP
  includes GST), with no separate discount.
* **The portal's checks, made here first.** The invoice number (at most 16 letters, digits, `/` and `-`, not starting
  with 0); the hospital's GSTIN, legal name, address, city and PIN code; the buyer's GSTIN (not the hospital's own) and
  an address with a PIN code (`BuyerAddress` takes the line, the place and the PIN code out of the free-text address);
  each PIN code in its GSTIN's state (the state's postal circles); an SAC code for a service, an HSN code for goods; no
  negative amounts (a return goes on a credit note); a GST rate the portal takes; IGST to another state, CGST and SGST
  within it; each line's and the invoice's totals adding up. What fails is listed on the invoice instead of the JSON.
* **The IRN.** The hospital uploads the JSON to the IRP (or through its GST suvidha provider) and records the
  acknowledgement number and the signed QR code it returns. The QR code's payload must name this invoice: supplier and
  buyer GSTIN, number, type, date, value and number of lines. Its signature is the IRP's; HC360 does not hold the IRP's
  key, and the code is verified by scanning it with the GST verification app. The IRN, the acknowledgement and the QR
  code then print on the A4 bill, the 80 mm bill, the pharmacy invoice and a bill's credit note.
* **Until it has one, the print says so.** An invoice that should carry an IRN and does not is not an invoice (rule
  48(5)). Nothing asks the portal for an IRN by itself (see [Not covered yet](#not-covered-yet)), so a document due one
  can be printed, at the counter or as the patient's PDF, well before it has one. Its print then carries a notice where
  the IRN goes:
  * the A4 bill (and its PDF, in the language it is made in), the 80 mm bill and the pharmacy invoice: "E-invoice
    pending: this is not yet a valid tax invoice for the buyer (GST rule 48(5))"; and, once its IRN was cancelled on
    the portal (which never registers that number again: the invoice is cancelled here and issued again), "IRN
    cancelled: this is not a valid tax invoice for the buyer (GST rule 48(5))";
  * a bill's credit note: "E-invoice pending: this credit note has no IRN yet, which GST rule 48(4) requires" (or "IRN
    cancelled: …"). Rule 48(5) speaks of invoices; a credit note comes under rule 48(4) through notification
    13/2020-Central Tax ("invoice and other prescribed documents").

  Due is the register's own test, asked through `IEInvoiceRegister.PrintAsync` ([its contract](#ieinvoiceregister-implemented-by-einvoiceregisterlookup)):
  `gst.einvoice.from` is set for the document's facility on or before its date, its source finds it a B2B e-invoice,
  it is not cancelled here, and it has no live IRN: the documents the register lists as `Pending` or `IrnCancelled`.
  A live IRN always prints. A print is made when it is asked for: the next one after the IRN is
  recorded carries the IRN, and a copy printed before keeps its notice, so the copy for the buyer is one printed
  after. Debit notes and the pharmacy's returns have no print yet.
* **Cancelling.** The IRP cancels an IRN only within 24 hours of its generation; the cancellation done there is
  recorded here with the portal's reason. Until then the invoice cannot be cancelled in HC360 (422 `einvoice.active`
  on a bill or a pharmacy sale). After 24 hours the IRN stays and a credit note undoes the invoice. The portal does not
  register the same invoice number twice, so an invoice whose IRN was cancelled is cancelled here and issued again
  under a new number.
* **Due dates.** The register shows each invoice's date 30 days on, the latest the IRP accepts from taxpayers with an
  aggregate turnover of ₹10 crore or more; later ones are listed as overdue. A credit note has its own 30 days.
* **A credit note goes after its invoice.** A credit note against an invoice of e-invoicing's time is not ready while
  that invoice has no IRN: its problems say to record the invoice's IRN first (and by when the portal takes it), or,
  once the invoice is overdue, to settle it with the hospital's GST adviser. Otherwise the portal, the buyer's GSTR-2B
  and the hospital's GSTR-1 would hold a credit note against an invoice they never had. An invoice from before
  e-invoicing applied has no IRN, and its credit note goes without one. The signed QR code recorded for a credit note
  must say `CRN`, and the invoice's IRN is not cancelled while a credit note with an IRN names it (422
  `einvoice.credited`); the register row of a credit note names its invoice.

### The portal connection
A facility can be connected to the IRP (Administration → Integrations → *E-invoice portal (IRP)*, `integrations.irp`
and its secret, per facility since the credentials are per GSTIN). The platform contract is `IInvoiceRegistrationPortal`
(Platform), implemented in Integrations by `IrpRouter` over two providers:
* **`nic` / `gsp`** (`NicIrpClient`): the NIC e-invoice API v1.03 — sign in (`/eivital/v1.04/auth`), generate
  (`/eicore/v1.03/Invoice`), cancel (`…/Invoice/Cancel`), get by IRN (`…/Invoice/irn/{irn}`), e-way bill by IRN
  (`/eiewb/v1.03/ewaybill`), with NIC's headers (`client_id`, `client_secret`, `Gstin`, `user_name`, `AuthToken`). For
  `nic` the sign-in carries a random 32-byte app key under NIC's RSA public key, the session key comes back under the
  app key, and every payload travels under the session key (AES-256-ECB, `NicIrpCrypto`); a GSP passing the API
  through usually takes plain JSON (`encrypt` to switch it on). Paths are settings, so a GSP's own prefix works. The
  token and session key are kept per facility, GSTIN and credential until ten minutes before they expire, and asked
  for again once on 1005 (token not valid). POSTs are never retried at the HTTP layer.
* **`simulator`** (`SimulatorIrp`), for training and test servers only: a live server refuses it before any call. It
  makes the portal's IRN (SHA-256 of the GSTIN, financial year, document type and number), answers a document asked for
  twice with 2150 and the IRN it holds, signs nothing for real, cancels only within 24 hours (2270 after), and gives an
  e-way bill valid one day per 200 km. Its IRNs are marked `provider = simulator`.

`POST …/irn/generate` builds the same INV-01 JSON (refused with its problems as before), sends it, and records the IRN,
acknowledgement, signed invoice and signed QR code after checking the code names the document. A duplicate (2150, as
after an answer lost on the way back) is fetched by its IRN and recorded the same way, so asking twice never makes two.
Connected, cancelling goes to the portal first, within the 24 hours. **E-way bills:** an invoice of goods (the
pharmacy's B2B sales) above `gst.ewaybill.threshold` (₹50,000 by default; lower for movement within some states) gets
its e-way bill from its live IRN, with the vehicle (Part-B) or the transporter's id (Part-A); its number and validity
are kept on the registration.

## GST returns
`GstReturnService` builds the returns of the signed-in facility's GSTIN for a month, with every facility registered
under it, from every `IGstReturnSource` (Billing's `BillingGstReturnSource`: bills, credit notes, debit notes; the
pharmacy's `PharmacyGstReturnSource`: sales and returns) and every `IGstPurchaseSource` (the stores'
`GoodsReceiptGstSource`). `GstReturnBuilder` classifies them as GSTN does:
* **B2B** (4A): invoices to a registered buyer (a payer with a GSTIN, a pharmacy B2B buyer), taxable lines by rate.
* **B2CL** (5): invoices to an unregistered buyer in another state above ₹1,00,000 (₹2,50,000 before 1 August 2024).
* **B2CS** (7): other taxable supplies to unregistered buyers by place of supply and rate, with their notes netted in.
* **CDNR / CDNUR** (9B): credit and debit notes to registered buyers; notes to unregistered buyers whose invoice was
  B2CL. Write-offs and notes issued after the s.34(2) limit are not GST notes and are left out.
* **Table 8**: a bill line without GST is an exempt healthcare service; a medicine at 0% is nil-rated.
* **Table 12**: every line by HSN or SAC, unit (`NA` for services, `NOS` for medicines) and rate, credit notes taking
  off, split into B2B and B2C (GSTN, May 2025). A line without a code is listed as a problem.
* **Table 13**: per series (OP bills, IP bills, pharmacy invoices, credit notes, pharmacy returns, debit notes) the first
  and last number, how many and how many cancelled. Cancelled documents count only here. A bill number reserved for a
  downtime (docs/modules/downtime.md) and voided unused, spoilt or returned with its block, is counted in the month it
  was voided in the total and as cancelled, so the From–To range accounts for every serial.
* **GSTR-3B**: 3.1(a) the taxable outward supplies net of notes, 3.1(c) nil-rated and exempt, 3.1(e) non-GST, 3.2 the
  inter-state supplies to unregistered persons by state, 4(A)(5) the input tax of goods receipts from suppliers with a
  GSTIN, by the day received, less the input tax of goods sent back to them on the stores' posted debit notes, by the day
  posted. Returns are netted in 4(A)(5), not reversed in 4(B)(2), as GSTN builds 4(A)(5) from GSTR-2B: "4A(5) = (a) – (b)
  – (c)", (b) being the suppliers' B2B credit notes. A hospital's supplies are mostly exempt, so input tax used for them and a share of common
  credit are reversed (CGST rule 42): the return gives the exempt share of turnover; the reversal itself is the
  accountant's, as is checking the credit against GSTR-2B (rule 36(4)).

The screen is **Billing → GST returns** (`/billing/gst-returns`), with the GSTR-1 JSON to import into the GSTN offline
tool or upload on the portal.

**Filing.** Once GSTR-1 is filed on the portal, the accountant records it on the same screen with the ARN and the date
(`POST /api/v1/billing/gst-returns/gstr1/filing {month, arn, filedOn}`, permission `billing.gst.returns`). It is recorded
once per GSTIN and month (a second time is 409 `gst.already_filed`), not before the month ends nor in the future, and is
corrected only with a reason (`PUT` with `{month, arn, filedOn, reason, version}`); both steps are audited
(`billing.gstr1_filed`, `billing.gstr1_filing_corrected`), and a filing is never deleted. A month with nothing recorded
says so ("Filing not recorded") and raises no warnings.

**Paper bills entered after filing.** A bill written on paper during a downtime is dated at the time on the paper
(docs/modules/downtime.md), and can be entered days later. If its month's GSTR-1 was already recorded as filed when it
is entered, it is not in that return: the bill shows "Entered after its month's GSTR-1 was filed" (`BillDetail.gstFilingWarning`,
in the finalise response and every time the bill is opened), and the GSTR-1 of the month it was entered in lists it
under **Late paper entries: amend in this return** (`Gstr1Return.lateEntries`, also a line in its problems). HC360 does
not build the amendment tables (B2BA, B2CSA); the accountant reports the bill there on the portal.

## Contracts for other modules

### `IChargePoster` (implemented by `ChargeService`)
* `PostAsync` **stages** the charge in the caller's scoped `HospitalDbContext` and returns its id; it never saves and never
  opens a transaction, so it is safe inside a domain-event handler (`ServiceOrderPlaced`, …) and inside the caller's own
  transaction — the caller's `SaveChangesAsync` commits the order line and its charge together.
* Idempotent on (`SourceModule`, `SourceReference`) in every facility, including charges staged earlier in the same unit
  of work; re-posting returns the existing charge id. A unique index settles concurrent retries.
* Works without a signed-in user or facility (Inpatient bed-day job, SupportServices meal-plan job): facility, tariff and
  bed class come from the admission's IP account, else the visit (both looked up across facilities); a charge with
  neither falls back to the signed-in user's facility, then the patient's registration facility, at the self-pay tariff,
  and is billed on a patient-level OP bill (`POST /bills` with `patientId`).
* Price: the payer's tariff plan price (bed-class multiplier for procedures, surgery, nursing and doctor visits),
  else the service's base price; `UnitPriceOverride` wins (MRP, implant invoice price). GST comes from the service item.
* **A device** (`ChargeRequest.Device`, a `DeviceDetails`): the OT and the IP pharmacy send, for an implant or another
  device from stock, what NPPA asks its bill to say: brand, manufacturer or importer, specification, the batch and
  serial of the pack used, the MRP on that pack and, for a price-controlled implant (a coronary stent, a knee implant),
  its NPPA ceiling price. The charge and its bill line keep them (`device`, jsonb); the bill prints them beneath the
  line, in the patient's language, and so does the counter's 80 mm bill. A unit priced above the lower of the MRP and
  the ceiling is refused (`billing.device_above_mrp`, `billing.device_above_ceiling`), whichever module posts it. A
  credit (a device returned) gives back what was charged and is not held to the limit, which may have been cut since.
* **Implants are not charged by hand**: a manual charge (`POST /charges`) on `OT-IMPLANT` or `PHARM-IMPL` is refused
  (`billing.implant_from_record`, 422). An implant is billed from the surgery record or the IP pharmacy's issue, which
  name it and keep its price within its limits.
* Negative quantity = a **return**: it must name its admission (or visit) and may not credit more than was charged for
  the same service item there, net of earlier returns (`billing.credit_exceeds_charge`). Pharmacy posts these as
  `pharmacy.ip.return`; bill, running-bill and portal lines flag them with `isReturn`.
* **An automatic charge** (`ChargeRequest.Automatic`: Inpatient's bed and nursing days, and its consultant's visits,
  retried while their stay is in progress) is posted again on each run until billing takes it. Its refusal (the service
  out of use, the patient merged into another record) is kept in `billing.charge_refusals` (`ChargeRefusal`, one per
  source line, counted again on each retry), not only logged: the first refusal is audited (`billing.charge_refused`),
  System health warns while one stands (`billing.charges_refused`, naming the stays and why; a stay that ended by
  absconding or death, whose days are posted after it ended, included), and a stay's **final bill is not finalized**,
  nor the stay **cleared for discharge**, while one stands (422 `billing.charges_refused`, naming each charge and why;
  the clearance says the same). Its module posts it again however long ago the stay ended
  (`IBillingStatusProvider.GetStaysWithRefusedChargesAsync`), not only for the few days a discharged stay is
  revisited. Fixed, the charge posts within the hour, the refusal is cleared (audited `billing.charge_refusal_cleared`)
  and the final bill, refreshed, is finalized with it. A cancelled source line clears its refusal, and a cancelled stay
  all of its own; a merge moves them to the surviving record. A diet's day is charged on that day only, so it is not
  automatic (its refusal could never clear): the services master keeps a diet's service in use instead.
* `CancelAsync(sourceModule, sourceReference, reason)` also only stages: it joins the caller's transaction (Inpatient
  reverses all of a cancelled admission's charges atomically), takes an unbilled charge off any draft bill, succeeds for
  unknown or already-cancelled sources, and fails with **`billing.charge_billed`** (422) when the charge is already on a
  finalized bill — billing then cancels the bill or issues a credit note.

Source keys in use: `inpatient.bed` / `inpatient.nursing` (`{admissionId}:{yyyy-MM-dd}`), `inpatient.visit`
(consultant-visit id), `supportservices.diet` (`{dietOrderId}:{yyyy-MM-dd}`), `pharmacy.ip` (issue line id),
`pharmacy.ip.return` (return line id), `diagnostics.*`, SpecialtyCare sources, and Billing's own: `billing.consultation`
(visit id), `billing.registration` (patient id), `billing.order` (clinical order item id), `billing.package`,
`billing.manual`, `billing.adhoc`.

### `IBillingStatusProvider` (implemented by `BillingStatusProvider`)
* `GetStatesAsync(sourceModule, references)` → `NotFound` / `Unbilled` (also while on a draft) / `Billed` / `Paid` (its
  bill is paid) / `Cancelled`, for any source key, across facilities, keyed by the caller's strings. Charges posted or
  cancelled earlier in the caller's unit of work count, so a job that checks and posts in one pass (a mid-day diet change)
  never charges the same day twice.
* Health-checkup packages: Clinical posts the package's HC-* service item when the patient checks in (`PackageBilling`,
  see `clinical.md`), so the state of a package is available as `GetStatesAsync("healthcheck.package",
  [packageBookingId.ToString()])`. Component tests (`ServiceOrderPlaced.IsPackageComponent`) are never charged
  individually by Billing.
* `GetOutstandingAsync(patientId)` → the patient's unpaid share of finalized bills (all facilities).
* `GetDevicesAsync(patientId)` → the last 20 devices charged to the patient (charges with `Device` details, not cancelled,
  not credits), newest first, in every facility: the charge's description, its `DeviceDetails` (brand, maker,
  specification, batch, serial), whether it was billed under an implant service (`OT-IMPLANT`, `PHARM-IMPL`) and the IST
  day it was used. Quality fills in the device of an MvPI report from them (`quality.md`).
* `GetDischargeClearanceAsync(admissionId)` → cleared when every IP charge is on a finalized bill, the final bill exists
  and the patient's share is paid, or management approved discharge with dues (for the discharge under way: a revoked
  discharge withdraws the approval).

### `IPayerDirectory` (implemented by `PayerDirectory`)
* `GetCoverageAsync(patientId, payerId)` → `PayerCoverageSummary {payerName, policyNumber, validUntil}`, for the patient
  banner's payer line (UX-04; `GET /api/v1/patients/{id}/payer`, see `clinical.md`). Each part is shown only to staff
  whose permissions would let them read it on the insurance screens: the payer's name to payer readers
  (`PayerDirectory.PayerReaders`: payers, policies, pre-auth or claims managers, OP billing, IP billing, charge readers),
  the masked policy number and its last valid day to policy readers (`PolicyReaders`: policies, pre-auth or claims
  managers, IP billing, OP billing). The policy is the one billing would price today's bill with: active, in date
  today, the longest valid.
* Without the Billing module the Platform's `NullPayerDirectory` returns all three as null, and the banner shows the
  payer type alone.
* `StandingAsync(patientId, payerId, day)` → `PayerStanding {payer, cover, validUntil}` whatever the caller's
  permissions: the payer, and whether the patient's policy with it, active and in date that day, is verified by someone
  other than whoever recorded its terms (`Verified`), recorded but not so verified (`NotVerified`), or missing (`None`).
  The Patients and Inpatient modules, and Billing's stay payer change, decide with `PayerChoice.Refusal` from it
  ([a third party pays only on a second person's word](#a-third-party-pays-only-on-a-second-persons-word)). Without
  the Billing module there is no payer, and only the patient, staff and charity may be chosen.

### `IEInvoiceRegister` (implemented by `EInvoiceRegisterLookup`)
For the modules whose B2B documents are e-invoices ([GST e-invoicing](#gst-e-invoicing)), by `source` (the
`IEInvoiceSource` key: `bill`, `creditnote`, `debitnote`, `pharmacy`, `pharmacyreturn`) and id:
* `FindAsync` → the document's live IRN (`EInvoiceStamp`: IRN, acknowledgement, signed QR code and its SVG), or null.
  The pharmacy does not cancel a sale while it has one (422 `einvoice.active`).
* `PrintAsync` → `EInvoicePrint(stamp, missing)`, what the document's print says of its e-invoice: the live IRN,
  whatever else holds; otherwise, for a document due one, why it has none (`MissingIrn.Pending`: not asked of the
  portal or recorded yet; `MissingIrn.IrnCancelled`: its IRN cancelled on the portal); neither for any other document.
  Due is the register's own test (`EInvoiceService.PrintOf`, the `StateOf` its list and detail use): `gst.einvoice.from`
  set for the document's facility on or before its date, its source's `FindAsync` finding it a B2B e-invoice, and not
  cancelled. Billing asks it for every `BillDetail` and for a credit note when it is printed; the pharmacy for a B2B
  sale's detail (`PharmacySaleDetail.missingIrn`), which the invoice print reads.

## Events

Raised:
* `BillFinalized` (Platform) — on finalization, in the same transaction, with the facility.
* `BillCancelled` (Platform) — a finalized bill was cancelled (bill number, net amount, reason); doctor-fee reversal
  inside Billing, and Engagement drops the bill from the patient's spend.
* `PaymentReceived` (Platform) — for real patient money: counter, POS, gateway, IP deposits and patient advances; never
  for advance adjustments or payer settlements. Carries the facility.
* `RefundPaid` (Platform) — a refund was paid out (cash, card reversal, UPI, bank transfer, cheque or gateway); not
  for refunds kept as a patient advance.
* `PaymentReversed` (Platform) — money a receipt recorded from the patient never arrived (not for the advance
  adjustments undone with it); Engagement takes it off the patient's spend.
* `RefundPayoutReversed` (Platform) — a refund recorded as paid out never reached the patient; Engagement counts it
  again.
* `BillPaymentReversed` (Billing-internal) — a bill paid in full owes again; under the "paid" trigger its doctors'
  fees are reversed, and accrue again when it is paid.
* `BillPaid` (Billing-internal) — accrual under the "paid" trigger whichever
  payment settled the bill (counter, POS, gateway or an advance adjustment) or when the payer covers it all; a credit
  note or write-off clearing the due is not a payment.

Handled (all inside the raising transaction; handlers stage changes and never save):

| Event | Effect |
|---|---|
| `VisitCreated` | Consultation charge for OP/tele/ER visits with a doctor (`CONS-NEW/FU/TELE/ER`; doctor's fee unless the payer tariff prices it; free follow-up at 0) and `REG-NEW` on the first visit (`billing.registrationFee.enabled`); an unbilled registration fee left by a cancelled first visit moves to the new visit |
| `VisitCancelled` | OP visit: Billing's own unbilled charges of the visit cancelled, registration fee kept for the next visit, empty draft discarded. In-patient visit (= admission cancelled): drafts discarded, Billing's own charges cancelled, account → `Cancelled`, IP desk alerted if services, finalized bills or an advance remain |
| `ServiceOrderPlaced` | Prices **procedures** only (`billing.order`, quantity from the order); never package components; labs/imaging/cardiology (Diagnostics), surgery/physio/dialysis/blood (SpecialtyCare), diet (SupportServices) and consultations are charged by their own modules |
| `ServiceOrderCancelled` | Cancels the `billing.order` charge; if already billed, refund approvers are asked to issue a credit note |
| `PatientAdmitted` | Opens the IP account (payer, tariff, policy, bed class); re-prices charges staged earlier in the same transaction |
| `PatientTransferred` | Follows the bed class for later pricing |
| `DischargeInitiated` | Drafts the final bill (package vs actuals, payer split) and alerts the IP desk |
| `DischargeRevoked` | Opens the account again (no settlement while the patient is in), withdraws an approval to discharge with dues (audited as `billing.discharge_with_dues.withdrawn`: it was for that discharge, and the dues grow with the stay, so the next discharge needs a new one), and tells the IP desk what becomes of the final bill |
| `PatientDischarged` | Closes a settled account or alerts billing about dues |
| `PatientsMerged` | Re-points every billing row (incl. advances, policies, claims) to the surviving UHID |
| `AppointmentPaymentCaptured` (Scheduling) | Queues `billing.appointment.prepayment` (outbox) |
| `AppointmentPaidTwice` (Scheduling) | Queues the same message for the second payment, which is kept as an advance with its refund asked for |
| `AppointmentCancelled` | For a cancelled (not rescheduled) prepaid booking, queues the same message to refund it |
| `BillFinalized`, `BillPaid`, `BillCancelled`, `BillPaymentReversed` | Doctor-fee accrual (idempotent per bill line; a line whose accrual was reversed accrues again) and reversal |

### Appointment prepayments (OA-03)
Scheduling posts nothing to billing. The outbox handler `billing.appointment.prepayment` (retried with back-off)
receipts the verified online payment as a **patient advance** (`ReceiptPurpose.Advance`, mode `PaymentGateway`,
idempotent on the gateway payment id, receipt number allocated in the dispatcher's transaction), following merges to
the surviving UHID. The advance follows a rescheduled booking (keyed by the gateway payment). When the visit booked
from the appointment is billed, finalizing the OP bill adjusts the advance against it (a journal, mode `Advance`,
nothing collected twice); staff can also adjust any open advance with `POST /bills/{id}/adjust-advance`. If the
booking ends up cancelled, a refund request (purpose `Advance`) is raised for approval and, when paid by
`PaymentGateway`, reversed on the original payment. Cancelling a bill gives its advance back.

A portal patient who pays and then closes the page is still credited: the gateway's webhook for the booking's order,
or the reconciliation job, confirms the booking through Scheduling's `AppointmentPaymentService` and receipts the
advance at once (`PrepaymentChecker`). The advance is dated when the gateway took the money.

**A booking paid twice.** A patient who starts the payment again gets a new gateway order, and Scheduling remembers the
one it replaced (`ReplacedPaymentOrder`): the first checkout, left open on another tab or phone, can still be paid. The
gateway's webhook for that order, the reconciliation job (every run for two hours after the order was replaced, then
hourly for three days) or "Record the receipt" on the settlement's exception asks the gateway about it
(`PrepaymentChecker.ConfirmReplacedOrderAsync`). Money taken on it pays a booking nothing else has paid. On a booking
another payment already paid, it is a **second payment**: receipted at once as a patient advance linked to the booking
("Second online payment for appointment …, on an earlier order (to be refunded)"), dated when the gateway took it, with
a refund request of all of it raised for approval ("Appointment … was paid twice: payment … , on an earlier order, is
returned"). A second person approves it, and paid by `PaymentGateway` it is reversed on that payment; the payment that
paid the booking stays. Each step is keyed on the gateway payment id, so a webhook delivered again, the job and the
settlement record nothing twice.

## Gateway reconciliation (INT-PG)

What the payment gateway paid to the bank is checked against HC360's receipts and refunds, and what does not match
is listed for the accountants, worst first.

**Settlements.** `IPaymentGateway.GetSettlementsAsync(day, facility)` returns a day's settlements with their
transactions (Razorpay's settlement reconciliation API; the training gateway settles too, see
[integrations.md](integrations.md#payment-gateway-int-pg-and-the-signature-convention)). `GatewaySettlementService`
stores each settlement once (unique on provider and settlement id) with its lines: payment, refund or adjustment id,
the payment a refund came from, the order, amount, fee with GST, GST, credit, debit, method, and the times. Only ids,
amounts, the UTR and the method are kept, never card or bank details. A settlement already stored only has its status
updated (created → processed or failed). The reconciliation job reads today and yesterday every hour for every active
facility, in facility-code order; accountants can read any day of the last year from the screen. A settlement webhook
(`settlement.processed`, `settlement.failed`) reads its settlement's day again at once, however long ago it was
settled (a settlement not stored yet: today's, for the facility the webhook names), and the webhook log says so.

**A payout that fails afterwards.** A settlement paid out (`processed`) that the gateway later reports `failed` (the
bank refused or returned the transfer) keeps its settlement journal in its own day, which may be in Tally already;
the failure is a journal of its own, the settlement journal turned round, in the books of the day it is learnt
(`gateway_settlement_corrections`, voucher `{settlement}-R{n}`, each facility its own part). It is raised as a
`FailedSettlement` exception (keyed by that voucher number), told to whoever reconciles the gateway in each facility
with a part of it, and audited (`billing.gateway.settlement_failed`). Paid out after all, the settlement's journal is
restored the same way (`{settlement}-S{n}`, `billing.gateway.settlement_restored`) and the exception clears. Days in
Tally are never altered: the books show the journal, its reversal and its restoration each on its own day.

**Shared merchant accounts.** Facilities that use the same gateway account (the all-facility settings) share its
settlements. The settlement is kept under the first facility that read it, and each line takes the facility of the
receipt or refund it matched, else of its payment link, checkout or prepaid booking. Each facility's day book clears
its own receipts; the facility holding the settlement also takes any settlement-level charge and any unexplained
difference. A facility that does not hold a settlement sees only its own transactions and their totals in it, and
each facility is alerted about the exceptions that are its own.

**Matching** (by gateway id, prepaid bookings and the orders they had before included): a payment line matches the receipt payment whose
`ExternalId` is that payment id (mode `PaymentGateway`); a refund line matches the refund whose `GatewayRefundId` is
that refund id. A line is `Matched`, `AmountDiffers`, `NoReceipt`, `NoRefund`, or `NotApplicable` (an adjustment or
transfer by the gateway). Recording a receipt later matches the line again and raises or clears its exceptions (an
amount difference when the receipt records another amount); when that moves the line to the receipt's facility, the
settlement day's day books of both facilities are queued for Tally again.

Exceptions are raised once a settlement is stored and saved on their own. A settlement is marked
(`exceptions_raised`) only after its exceptions are saved, and every fetch of a day matches again and raises again
the settlements not marked or with lines that still do not match, so a run that stopped or clashed with a webhook
raising the same exception is made up by the next fetch; after a clash the exceptions are read again and saved, so
only the one that clashed is merely refreshed.

**Exceptions** (`GatewayDiscrepancy`, one per kind and gateway id, in `gateway_discrepancies`), worst first:

| Rank | Kind | Raised when | Clears itself when |
|---|---|---|---|
| 1 | `UnrecordedPayment` | a settled payment has no receipt; or the gateway reports a payment captured but its receipt could not be recorded (the reason is kept on the order and accountants get an alert); or a card/UPI terminal approved a sale whose receipt could not be recorded (keyed on the terminal transaction id; the reason is kept on the sale and accountants get an alert) | the receipt is recorded |
| 2 | `UnsettledReceipt` | an online receipt is not in any settlement `settlementDays` after the payment, while the gateway has settled something newer (receipts of the last month are looked at) | a settlement carries the payment, or the payment is reversed as not received |
| 2 | `FailedSettlement` | a settlement paid out fails afterwards; its journal is reversed in the books of the day it is learnt (accountants get an alert) | the gateway pays the settlement out after all (its journal is restored); "Read the settlements again" reads its day |
| 3 | `AmountMismatch` | a settled payment's amount differs from its receipt; the gateway adjusted a settlement; a settlement's payout differs from its transactions | the amounts agree on a later match |
| 4 | `RefundMismatch` | a settled refund is unknown to HC360 or has a different amount; a gateway refund failed (accountants get an alert) | the refund matches, or the bank confirms it; a failed refund only when it is sent again, recorded as not paid out ([Money that did not move](#money-that-did-not-move), once a second person approves) or the bank confirms it, never by a settlement line |

An exception a person resolved stays resolved; one HC360 cleared opens again if the cause comes back. Accountants
can record the receipt, try again or resolve it with a note (see the endpoints above); every action that records
money or closes an exception is audited. The list never shows patients' names, only document numbers and gateway ids.

**Closing the gaps.** `GatewayReconciliationJob` runs where `PlatformOptions.RunsJobs` is set, every 5 minutes:
- payment links and portal checkouts still waiting (or paid but not recorded) are asked about again: every run for
  two hours, then about hourly for two days, then about every six hours for 30 days. Each of these windows is asked
  for its newest 200 per run, so fresh links are never crowded out by many older ones. A paid one is recorded. A link
  the gateway reports expired or cancelled is marked `Expired` or `Cancelled`; a portal checkout nobody paid within
  `checkoutExpiryHours` is marked `Expired`. Money taken after that is still recorded;
- portal bookings whose online payment was started but not confirmed are asked about every run for two hours, then
  hourly while the booking is recent or still ahead;
- hourly: gateway refunds are followed until processed or failed, by when the latest attempt was sent (a refund sent
  again weeks after it was first paid out is followed like a new one): the newest 200 of the last two days every hour,
  the newest 200 of the rest of the month every six hours; the settlements of today and yesterday are read;
  unsettled receipts are listed.

**Sending a refund again.** A failed gateway refund is sent again by one request only: the retry is first claimed
(`refunds.gateway_retry_started_at`) in a save of its own under the refund's concurrency token, so a second click is
refused, and only then is the gateway called. Each attempt carries an idempotency key (the refund's id for the first
payout, the refund with the gateway refund that failed for a retry; Razorpay's `X-Refund-Idempotency` header and the
refund's `receipt`), so a request the gateway already acted on gets the same refund back rather than a second one. A
gateway that refuses the retry returns the refund to failed; one that does not answer leaves the claim, which another
request may take over after five minutes with the same key.

**Dates.** A gateway receipt is dated when the gateway took the money (`PaymentStatusResult.PaidAt`, never later than
now), so a payment recorded late belongs to the day the patient paid. When that day's day book was already posted to
Tally, it is queued again (`TallyCorrections`); vouchers keep their numbers, so Tally alters rather than duplicates.

**Settlement journal.** Each processed settlement is a Journal in the day book of its IST settlement day, numbered
with the settlement id (a shared account's other facilities add a suffix): the bank (`gatewaySettlementBank`) is
debited with the payout, `gatewayCharges` with the fees without GST, `gatewayInputGst` with the GST on them, and the
payment-gateway clearing ledger (the `PaymentGateway` mode ledger, "Payment Gateway Clearing" by default) is credited
with the gross (payments less refunds). The clearing ledger, debited by the day's online receipts and credited by
gateway refunds, is cleared this way; whatever stays in it is unreconciled. A settlement read after its day was posted
queues that day again.

**Webhook events** (`gateway_webhook_events`): each verified delivery is kept with the provider, event id, event type,
the gateway's order, payment, refund and settlement ids, the facility, when it arrived and was handled, the outcome
and how many times it was delivered. A delivery of an event still being handled (for less than two minutes) is
acknowledged and not acted on; after that, or after a failure, one delivery takes it over (a conditional update). An
event recorded as processed stays processed. Never the payload, which carries the patient's contact and card or bank details.
They are deleted with the integration log after its retention period (Privacy › Retention).

## Card and UPI reconciliation (INT-POS)

Card and UPI money reaches the bank through the acquirer (the bank or POS company behind the terminals), less its
charges (MDR) and the GST on them. The acquirer reports what it paid in a settlement report (its MIS), which an
accountant imports on **Billing › Card and UPI settlements**; HC360 checks it against the card and UPI receipts.

**Reading the report** (`PosSettlementFile`). CSV (comma, semicolon, tab or pipe separated, quoted fields allowed) or
an Excel workbook (.xlsx, read by HC360's own reader, `XlsxSheets`: cell values only, no DTDs, at most 150 MB unpacked);
an older .xls is refused with what to do. Every sheet with the headings is read (some acquirers put cards and UPI on
sheets of their own), a sheet without them (a summary) is passed over, and the problems then name their sheet. Banks lay their reports out differently, so the columns are found by their
headings, in the first 30 rows (title rows before them are skipped): RRN (UPI: the UPI reference), approval code,
transaction amount, MDR or commission, GST (or IGST, CGST and SGST added up), net amount, TID, transaction date and
time, card number, transaction type, payment mode, settlement date and UTR, each under the names banks use ("Txn
Amount", "Auth Code", "Cust Ref No", "MDR Amount", "Net Amount", "Settlement Date", "UTR No"…). A column whose heading is a
rate ("MDR %") is never taken for an amount. The hospital can add its acquirer's own headings in
`billing.pos.settlements` (`columns`). Dates are read day first (07/10/2026, 07-Oct-2026, 2026-10-07, Excel serial
numbers) in Indian time; amounts with commas, ₹, INR, brackets or Dr. Only the card's last four digits are kept, never
the card number or anything about the customer.

* A payment's net is what reached the bank; when the report gives it, the charges are the amount less GST less the net,
  so `amount = net + charges + GST` always holds. Without a net column, the net is the amount less MDR and GST.
* A refund, void or reversal (or a negative amount) takes money back; a chargeback, dispute or adjustment is kept for
  review. On these, any charge returned with the money counts as charges. An adjustment whose words say the money comes
  back (a chargeback reversal, a re-presentment, an adjustment credit, a dispute won; never one that says debit) adds
  to the payout instead, less any charge the acquirer keeps. It carries the chargeback's RRN but is keyed apart from it
  (`line_key`), so it is a transaction of its own and not the chargeback read again. Chargebacks without an RRN or approval code
  that look the same (terminal, amount, days, UTR) are all kept, numbered in the report's order, so a later report
  listing them again adds none of them.
* Rows left out, each with its reason (at most 20 listed): rows without an amount, rows with neither an RRN nor an
  approval code (except chargebacks), rows whose net, charges and GST do not add up, and repeated rows. The report's
  total rows are skipped and not counted. The import says which heading it read as what (`Amount from "Txn Amount"`).
* At most 20,000 transactions and 10 MB per file. Reports overlap: each transaction is stored once (`line_key`: its
  type and RRN, or without one its TID, approval code, amount and day), so a later report adds only what is new. The
  same file (by its SHA-256) is refused a second time while it would bring nothing new; a transaction of a report
  withdrawn since no longer counts as imported, so the same file imported again brings it.

**Matching** (each receipt payment, refund and counter payment once; a partial unique index on the matched payment, on
the refund and on the counter payment stops two imports taking the same one). References are compared by their letters
and digits, whatever the case, so an RRN typed as "6279 1012-3456" matches 627910123456: the receipts and refunds of the
report's days (from two days before its first transaction) are read and compared that way, and older ones by the
reference as typed. Billing's receipts and refunds are looked for at Billing's terminals; what is left, at any
terminal, is looked for among the pharmacy's and the canteen's card and UPI payments and refunds of the report's days,
read through the Platform contract `ICounterSalesSource.GetCardPaymentsAsync` (Pharmacy and SupportServices implement
it; Billing does not reference them). The pharmacy's and the canteen's counters ask for the RRN or approval code (card)
or the UPI reference (UTR) when they take the payment.

| Settled | Matches | Shown as |
|---|---|---|
| Payment | the card or UPI receipt payment whose reference is the RRN: the terminal's (a connected terminal's sale records it) or the one the cashier typed | `Matched` by the RRN, or `AmountDiffers` when HC360 recorded another amount |
| Payment | else a connected terminal's approved sale with the approval code and amount, within two days, or a payment whose typed reference is the approval code | `Matched` by the approval code |
| Payment by card | else a card payment typed with the same last four digits and amount, within a day | `Matched` by the card and amount |
| Payment | nothing | `NoReceipt` |
| Refund | the refund paid by card or UPI whose reference is the RRN or (same amount) the approval code | `Matched`, else `NoRefund` |
| Chargeback | the sale with the same RRN (without one, the approval code at the same terminal) that matched a receipt | followed as a [chargeback case](#chargebacks), not to look at; `Review` when the sale matched no receipt |
| Chargeback reversed (money back) | the case on that sale: open, or won without its money back yet | wins the open case by itself (or is the money back of a won one), not to look at; `Review` otherwise |
| Payment or refund that matched nothing above, at any terminal | the pharmacy's or the canteen's card or UPI payment (for a refund, money it paid back by card or UPI) whose typed reference is the RRN, else is the approval code with the same amount within two days; a payment of the same mode first | `Matched` by the RRN or the approval code, with the counter and its invoice or bill, or `AmountDiffers` |
| Any, at a TID in `otherTerminals`, that matched nothing | another counter's sale HC360 does not record, or one typed without its reference; Billing's receipts are not looked for there | `OtherCounter` |

`NoReceipt`, `AmountDiffers`, `NoRefund` and `Review` are listed to look at until an accountant reviews them with a
note. Transactions without a receipt or refund, and those set aside as another counter's, settled in the last 90 days
are matched again with each import and by **Match again** (after the missing receipt, or the counter's sale, is
recorded); one that matched stays matched.

**Not settled.** A card or UPI receipt is listed when no report carries it although the reports of its mode already go
`settlementDays` past it (from the first day a report covers, for the last 60 days, at most 500). The pharmacy's and
the canteen's card and UPI payments are listed the same way, counted from the transactions matched with that counter's
payments of that mode, so a counter whose terminal's reports the hospital does not import lists nothing rather than
everything; the transactions matched with a counter's payment do not count for Billing's receipts. Money paid back is
not listed. A payment whose invoice or bill was cancelled since is listed, marked cancelled: the terminal settles it
unless it was voided on the terminal.

**Settlement journal.** Each payout (a day's transactions under one UTR, or the day's without a UTR) is a Journal in
the day book of its settlement day, numbered `POS-<facility>-<UTR>` (`POS-<facility>-<yyyyMMdd>` without a UTR):
`posSettlementBank` is debited with what reached the bank, `posCharges` with the charges and `gatewayInputGst` with the
GST on them, and the Card and UPI mode ledgers ("Card Settlement Account", "UPI Settlement Account" by default) are
credited with the gross (payments less refunds and chargebacks). The receipts debited those ledgers, so what stays in
them is not settled. Importing a report queues the posted days it touches again (`TallyCorrections`); the journal
keeps its number, so Tally alters it.

**A report imported in error** (another facility's, the wrong acquirer account's, or the acquirer's provisional report
before its final one) is withdrawn, never deleted: an accountant asks with the reason, and another person who approves
reversals (`billing.refunds.approve`) approves it, shown what it takes out (its transactions and what they paid out,
the days, the receipts and refunds they matched, the transactions an accountant reviewed). Then its own transactions,
those it brought first, are `Withdrawn`: they match nothing, the receipts and refunds they matched are free for the
correct report or **Match again**, and they leave the payouts, the lists and the coverage of "not settled". A
transaction another report brought first stays. The payout's journal stays in the day book of its day, which may be in
Tally already; the report's part of it is turned round in the day book of the day of the withdrawal, as a Journal
`POS-<facility>-<UTR>-W<the report's first six id characters>` narrated "Reverses card and UPI settlement journal …
of …: its report was withdrawn as imported in error". So the books show the payout, its reversal and the correct
report's payout, each on its own day. A correct report imported afterwards, or before (imported again: it brought
nothing then), brings the transactions as they were paid.

**Training.** `/training-file` turns the training terminal's approved sales of a day into a report, paid the next day
with a 1.5 % charge on cards plus GST, under the UTR `SIMUTR<yyyyMMdd>`, so the whole flow can be tried without an
acquirer. Only a training or test server makes one (422 `billing.pos_training_server` on a hospital's live server, where
it would settle sales no card paid); see *Simulators only on a training or test server* in
[integrations.md](integrations.md).

## Chargebacks
A chargeback is the patient's bank taking back the money of a card, UPI or online payment through the card network, UPI
or the payment gateway (documented gap 10). HC360 follows each as a case (`PaymentDispute`, numbered
`billing.paymentdispute`), one per payment line of a receipt:

* **From a settlement.** A gateway settlement's adjustment that takes money back on a payment id a receipt recorded
  (`PaymentGateway` mode) opens a `Gateway` case for what it took, instead of an unexplained amount difference; one on
  a payment HC360 never recorded stays an exception. An acquirer's report that carries a chargeback under the RRN (or,
  without one, the approval code at the same terminal) of a sale that matched a receipt opens a `Terminal` case, and
  the chargeback leaves the transactions to look at; one whose sale matched nothing stays to look at and is tried again
  with each import and **Match again**. A settlement read again (each fetch of its day, **Match again**) finds its case
  and opens no second one; a run whose exceptions clash with another's lets go of the cases it staged with them, and
  reads them again.
* **From the notice.** An accountant opens a `Notice` case with the receipt number (choosing the payment the notice
  names when the receipt has more than one card, UPI or online payment), the amount and what the notice says, often
  before any settlement carries it; the settlement that takes the money later joins that case.
* **From the gateway's own record.** A dispute the gateway reports (below) on a payment a receipt recorded joins the
  open case on that payment that follows no other dispute, or opens a `Gateway` case.

**The gateway's own record of a dispute.** For a gateway payment, the gateway holds the dispute: its reason code, its
phase (`chargeback`, `pre_arbitration`, `arbitration`…), where it stands, and the time to answer by. HC360 reads it
(`IPaymentGateway.GetDisputeAsync` and `GetDisputesAsync`; Razorpay's `GET v1/disputes/{id}` and
`GET v1/disputes?from&count&skip`) when the dispute's webhook arrives, and hourly for the disputes raised in the last
30 days and every open case following an older one, so a webhook the gateway could not deliver, or HC360 could not
handle, still has its case. Only the ids, amount, reason code, phase, status and times are kept: the evidence's
documents and the hospital's written answer stay at the gateway. One case per dispute (`gateway_dispute_id`, unique):
the webhook, the hourly read and a settlement that carry the same dispute find the same case. The case takes:

| The gateway says | The case |
|---|---|
| a reason code, a phase, a time to answer by | the reason code and phase; the last day to answer is the Indian day before that midnight (Razorpay's `respond_by`). The accountant's words of the notice are kept, but its date cannot be typed over (400 `respondBy`) |
| `open` | as it is |
| `under_review` (the evidence was submitted there) | `Contested`, the evidence's day taken from the gateway when HC360 has none |
| `won` | `Won` by the gateway's decision |
| `lost` | stays open: those who reconcile are told once, its deadline is no longer reminded, and the next step is to accept the loss, which a second person approves as below |
| anything else (`closed`…) | as it is, with the gateway's word shown for a person to read |

A read that changes nothing leaves the case untouched (its version too), so an accountant who has it open is not told
it changed. A settlement adjustment that names the dispute (Razorpay's `dispute_id`) belongs to that dispute's case: it
is the money taken (joining the case) or, when it adds money, the money back of the case (winning an open one, or
completing one the gateway already decided won). A second debit or return for one case, or money returned on a lost
case, stays an exception for a person.

The accountant records the notice's reason code, reason and the date to answer by, then the evidence sent (the bill,
the receipt, the charge slip, the patient's consent…) with the day it was sent: the case is `Contested` until the bank
decides. It ends one of two ways:

* **Won.** An accountant records how the money came back; or the gateway gives back what it took in a later settlement
  (an adjustment adding that money on the same payment), which wins the open case by itself and is no exception either;
  or the gateway decides the dispute for the hospital; or the acquirer reverses a terminal chargeback in a later report
  (money back, under the sale's RRN), which wins the case by itself and adds to that day's payout. Part of the money
  given back (or more) does not win the case: the case stays open for what is still taken back, and the return is an
  amount-difference exception (or, from the acquirer, a transaction to look at) for the accountant to put against it.
* **Lost.** Accepting the loss never closes a case by one person's word. The accountant asks; HC360 asks for the
  receipt's payment line to be reversed with the reason `Chargeback` (*Taken back by a chargeback*), and someone else
  who approves reversals (`billing.refunds.approve`) approves or rejects it, as any receipt reversal. Approved, the
  receipt is reversed, the bill owes the money again, and the case is `Lost` with the reversal's number, in the same
  unit of work. While it waits, the case cannot be recorded as won; rejected or expired, it stays open. If the gateway
  gives the money back while it waits (the case is won by itself), approving it fails with `billing.dispute_won` and
  the receipt is not reversed. Only a whole payment line is reversed: part of a payment taken back is put right on the
  bill (a credit note or a new receipt).

**In the books.** The receipt put the money in the Card, UPI or payment-gateway clearing ledger. The settlement that
carried the chargeback cleared that much less of it (its gross is payments less refunds and chargebacks), so an open
case is money still in clearing, matching what the bank holds back. A lost case's reversal journal takes it out of
clearing against the patient's account; a won case's money comes back in a settlement, which clears it.

**Told and reminded.** `GatewayReconciliationJob` tells those holding `billing.gateway.reconcile` of each open case
HC360 opened itself (from a settlement, or from the gateway's dispute with its deadline) once it is saved, once of each
dispute the gateway decided against the hospital, and, once a day, of every case not yet answered and not yet decided
whose deadline is within two days or past; a deadline changed since is reminded again the same day. What was told when is kept apart from the
case (`PaymentDisputeReminder`), and each reminder is claimed with a conditional update before anyone is told: a
second worker or a step run again never tells the same thing twice, and telling never changes a case an accountant
has open (its version).

Cases are listed under **Chargebacks** on the card and UPI settlements screen (terminal payments) and on the
reconciliation screen (the gateway's).

## The Tally day book

`TallyService` builds one facility's day as balanced vouchers (`TallyVoucherBuilder`) with the ledger names of
`billing.tally.ledgers`:

* a Sales voucher per bill finalised (income by department, discount, CGST/SGST, round-off, patient and payer
  receivables), and a Credit Note `<bill>/C` for a bill cancelled on the day;
* a Receipt per receipt (a Journal for an advance adjustment), a Payment per refund paid, a Credit Note per credit note
  or write-off, a Journal per claim deduction (`<claim>/<six hex digits of the settlement>`). A credit note debits sales
  returns with its taxable value and the output CGST, SGST or IGST ledgers with the GST it takes back
  ([Credit notes are GST credit notes](#credit-notes-are-gst-credit-notes)); a write-off debits write-offs with the
  whole amount;
* a Journal per reversal made on the day, numbered by the reversal: money not received (`RR…`) turns the receipt's
  entries round for the lines taken back, and a refund not paid out (`RX…`) turns its payment round. The day the
  receipt or refund was recorded keeps its own voucher;
* a Journal per doctor for the day's fee accruals, numbered `DF-<facility>-<yyyyMMdd>-<doctor code>`
  (`DF-NH-20260925-DR005`). A doctor without a code is numbered by an eight-digit hash of the name. Two doctors whose
  numbers would be the same (a shared code) both add the last eight hex digits of their practitioner id, so neither
  number depends on the order the doctors come in. Payouts are Payments with TDS and recoveries;
* the pharmacy's and the canteen's day, read through the Platform contract `ICounterSalesSource` (implemented by
  Pharmacy and SupportServices): one summary Sales voucher (`PH-<facility>-<yyyyMMdd>`, `CT-…`) with income by GST
  rate, a Credit Note for returns (`…-R`) and a Receipt for balances paid later (`…-C`). A counter can be left out with
  `pharmacy.include` / `canteen.include` while its sales are still entered in Tally by hand;
* a Journal per payment gateway settlement processed on the day (see [Settlement journal](#gateway-reconciliation-int-pg));
* a Journal per card and UPI payout of the day, from the acquirer's reports (see
  [Card and UPI reconciliation](#card-and-upi-reconciliation-int-pos)).

Every voucher carries a reference; Tally keeps it as `REMOTEID="HC360-<reference>"`, so posting a day again alters its
vouchers instead of adding new ones. Narrations carry document numbers, never patient names.

**Per-voucher tracking.** Every voucher sent is a `TallyVoucherRecord` (`billing.tally_vouchers`): type, number,
remote id, amount, a SHA-256 of its content, what Tally answered (created, altered, accepted, refused, not confirmed,
not sent, or imported from the file) and the result of the last check against Tally. A voucher that leaves the day
book after Tally confirmed it stays listed as "No longer in HC360" until it is deleted in Tally. The staff screen shows
each voucher's status and what to do about it. A reference longer than 200 characters, or two vouchers with the same
reference, fails the day with that reason before anything is sent; a retry cannot fix either.

**Posting.** `POST /exports` and the nightly job start a post run on the day (`TallyExport`, one per facility and
date) and queue `billing.tally.post`. Each attempt sends what Tally has not confirmed in the run, 50 vouchers at a
time, and records Tally's answer for each voucher as it goes. The day is Posted when Tally confirmed every voucher;
otherwise it is Failed with the reason, and the outbox tries again with back-off (vouchers Tally confirmed are not sent
again in the same run). Ten seconds after a post, `billing.tally.check` reads the day back. Without a Tally connection
the day is Not configured (not a failure): the accountant downloads the file, imports it into Tally and marks the day
imported.

**Time limits and retries.** The outbox gives a handler 30 seconds and has no per-handler setting, so the post plans
inside it: an attempt starts no new chunk after 15 seconds (the rest follows in a new message of the same run), and
stops calling Tally after 24 seconds, when the exporter sends nothing new and cuts a call in progress short. Each call
to Tally has 20 seconds. A post that runs out of time anyway is recorded as Failed ("The post did not finish within
the 30 seconds …") and retried by the outbox; a check that runs out of time records its check against Tally as
Failed and is not retried. The post and the check run in a scope of their own and save as they go, so the outbox's
batch transaction holds only its message rows while Tally is called, and a conflict on a day fails that message alone.
`TallyDayBookScheduler` also queues again, every hour, any day still Queued two hours after it was queued or last
tried (its message was lost or gave up), so no day waits for ever.

**Concurrency.** `TallyExport` and `TallyVoucherRecord` carry an `xmin` concurrency token. A check that loses to a
post (or another check) starts again from what was saved, up to three times; a post that loses fails its message and
the outbox tries again; "mark imported" that loses answers 409 `billing.tally_busy`. Downloading the file at the same
moment as a post records the file on the row the post created.

**Mark imported.** Allowed once the file was downloaded and only while the day book still has the fingerprint of that
file; refused while the day is Queued (409 `billing.tally_posting`) and once Tally confirmed the day itself
(`billing.tally_posted`), as the screen hides it then. It marks every voucher imported and drops the records of
vouchers that left the day book (the screen lists them for deleting in Tally first).

**Records check** (`TallyRecordsCheck`, inside HC360). The day book's figures against the records they come from:
bills finalised and cancelled (count and amount), money received and paid back per payment mode against the cashier
day-end report, deposits and advances, advance adjustments, insurer settlements, claim deductions, credit notes,
write-offs, doctor-fee accruals and payouts. Open shifts and cash shortages or excesses are listed as notes (the day
book has no voucher for them). The pharmacy and canteen lines are totals only: the day book takes them from the
counter's own day, which HC360 has no second record of, so they are shown but never counted as matched or different.
Vouchers not in Tally (not sent, refused, changed since sent, no longer in HC360) count as differences too.

**Check against Tally** (read-back). `IAccountingExporter.ReadBackAsync` reads the day's vouchers from Tally (22
seconds at most) and matches them by remote id: present, amount, type and ledgers. HC360 vouchers Tally holds on the
day that no facility's day book has are listed as only in Tally, to delete there; a bill of this facility among them
is listed as "Bill of another day" with the day it belongs to, to post that day again instead (see below).

**Days posted before per-voucher tracking.** Before tracking, a doctor-fee journal was numbered by its place in the
day (`DF-20260925-001`, `REMOTEID="HC360-DF-20260925-001"`), and the credit note for a cancelled bill reused the
bill's own reference, so Tally turned the bill's sale into that credit note on the cancellation day. The
`TallyReconciliation` migration sets `legacy_references` on every day that existed then. Such a day:

* keeps the old doctor-fee numbers (`DF-yyyyMMdd-001`, doctors in order of code and name), so posting it again alters
  the journals Tally already holds instead of adding a second set. This is safer than asking the accountant to delete
  the old journals first: nothing is doubled at any moment, and it works the same for days imported from a file. The
  sequence may now give a doctor's journal another doctor's number; the post alters every journal of the day, so the
  totals stay right;
* when posted again, also queues the day each bill cancelled on it was finalised (unless that day is queued already,
  or has sent the bill since tracking began): the cancellation now goes as `<bill>/C`, and posting the bill's day
  turns `HC360-<bill>` back into the sale, so Tally does not reverse the bill twice. The day's message names the days
  queued. Where Tally is not set up, those days show Not configured and the accountant imports their files too;
* after the migration shows its vouchers as confirmed ("N of N in Tally") when it was Posted.

`HC360-<bill>` found only in Tally on a cancellation day is that pre-tracking credit note: the read-back says to post
the bill's day again, never to delete it.

## Outbox, jobs and notifications
* `billing.tally.post` — day book per facility and date (see [The Tally day book](#the-tally-day-book));
  `TallyDayBookScheduler` queues yesterday after 00:30 IST (`billing.tally.dailyPost.enabled`, only where
  `PlatformOptions.RunsJobs`), posts again a day someone posted before it ended, and queues again days stuck in Queued.
* `billing.tally.check` — reconciles a posted day with its records and with Tally; not retried.
* `billing.appointment.prepayment` — see above.
* `GatewayReconciliationJob` — re-checks open online payments, prepaid bookings and the orders they had before, follows
  gateway refunds, reads settlements; see [Gateway reconciliation](#gateway-reconciliation-int-pg). Every five minutes
  it also tells of new chargeback cases and reminds of their deadlines, and hourly it reads the gateway's disputes; see
  [Chargebacks](#chargebacks).
* `PosRecheckJob` — every minute where `PlatformOptions.RunsJobs` is set, follows the terminal sales the counter
  stopped polling (the screen closed, the browser crashed, the cashier moved on while the customer paid). Sales started
  more than a minute and less than two days ago that are still `Initiated` or `AwaitingCustomer`, or `Approved` without
  their receipt, are asked about with their own facility's terminal settings: an approval records the receipt (once,
  keyed on the transaction id, as when the counter polls), and a sale still waiting after the facility's
  `billing.pos.recheck` timeout is cancelled on the terminal and marked `TimedOut` ("Nobody completed the payment
  within 15 minutes; HC360 cancelled the sale on the terminal."), so a later approval cannot leave money unrecorded.
  If the terminal does not confirm the cancel, the sale stays open and is tried again on the next run. An answer from
  another provider than the one that started the sale (POS switched off or changed since) is not applied. An approved
  sale whose receipt the bill or admission refuses (an advance for a stay whose account was closed meanwhile, or that
  became free of cost) keeps the reason on the sale, and the counter shows it and warns not to take the money again; it
  opens an `UnrecordedPayment` exception and alerts the accountants once. A receipt recorded later clears it. The
  counter hears of the outcome through `billing.pos.changed`. Each run is idempotent, so a second worker or the counter polling
  at the same moment does no harm.
* SMS/WhatsApp templates used: `bill.receipt` (SMS, every counter/POS/gateway receipt; on WhatsApp too once its
  WhatsApp template is switched on, except with the payment that settles a bill sent there), `bill.shared` (WhatsApp,
  the bill's PDF; see below), `payment.link`. All in the patient's language.
* In-app alerts: discount approval needed, refund awaiting approval, discharge initiated (final bill drafted), patient
  discharged with dues, admission cancelled (billing follow-up), cancelled order already billed; to holders of
  `billing.gateway.reconcile`: an online payment not recorded, an online refund failed, settled transactions that do
  not match, a chargeback taken back or disputed, a chargeback's deadline near or past, a chargeback lost at the
  gateway.
* Realtime: `billing.bill.changed`, `billing.pos.changed` (facility group).

### Bills on WhatsApp (INT-WA)
`BillSharing` sends the A4 bill on WhatsApp as template `bill.shared`, whose document header carries the PDF made by
`BillDocumentSource` (document kind `billing.bill`): the same [printed bill](#the-printed-bill) the counter prints and
the portal downloads, made when the message goes out, so it shows the payments received by then. A draft (an
estimate) or a cancelled bill gives no document and is never sent.

* **When a payment settles the bill** (counter, POS terminal, payment link or portal checkout; not an advance
  adjustment, which sends no receipt either), alongside the `bill.receipt` SMS, unless `billing.bills.sendOnWhatsApp`
  is off, and while a version of its WhatsApp template is in use. The receipt of that payment then goes by SMS alone; a
  payment that leaves something due, or whose bill can't go on WhatsApp, sends its receipt on WhatsApp as well
  (`ReceiptService.ReceiptChannels`). The desk's send answers 422 `billing.whatsapp_template_off` while the template is
  not in use.
* **When the billing desk sends it** from the bill (`POST /bills/{id}/send-whatsapp`), for any finalised, part-paid or
  paid bill.
* **Consent.** A bill is a service message about the patient's own care, so, like a report, it needs no WhatsApp
  opt-in (see [integrations.md](integrations.md#opt-in-tracking)): it goes to the patient's registered number unless
  that number replied STOP, which the delivery handler checks when it goes out. The desk is told about a STOP up
  front instead of queueing a message that would be suppressed. Deceased patients' numbers are not messaged.
* **Template.** Seeded (always) as WhatsApp template name `bill_shared` with "Document header" on. Register it with
  Meta as a utility template with a document header, and its body as the hospital registers all its WhatsApp templates
  (one parameter, or the HC360 text's placeholders numbered or by name: see
  [integrations.md](integrations.md#template-parameters)); without a header there is nothing to send, so do not untick
  it. There is no receipt PDF: the 80 mm receipt prints in the browser.

## Number series
| Key | Format | Reset |
|---|---|---|
| `billing.bill.op` | `{FAC}OB{YY}{SEQ:6}` | yearly |
| `billing.bill.ip` | `{FAC}IB{YY}{SEQ:6}` | yearly |
| `billing.receipt` | `{FAC}RC{YY}{SEQ:7}` | yearly |
| `billing.refund` | `{FAC}RF{YY}{SEQ:6}` | yearly |
| `billing.creditnote` | `{FAC}CN{YY}{SEQ:6}` | yearly |
| `billing.debitnote` | `{FAC}DB{YY}{SEQ:6}` | yearly |
| `billing.receiptreversal` | `{FAC}RR{YY}{SEQ:6}` | yearly |
| `billing.refundreversal` | `{FAC}RX{YY}{SEQ:6}` | yearly |
| `billing.paymentdispute` | `{FAC}CB{YY}{SEQ:5}` | yearly |
| `insurance.preauth` | `{FAC}PA{YY}{SEQ:6}` | yearly |
| `insurance.claim` | `{FAC}CL{YY}{SEQ:6}` | yearly |
| `doctorfees.payout` | `{FAC}DP{YY}{SEQ:5}` | yearly |

Bills, credit notes and debit notes are GST series: their formats are held to 16 characters and may not start with 0
(GST rule 46; see `docs/modules/numbering.md`). The bill defaults were `{SEQ:7}`, which reaches 17 characters at a
6-letter facility code; series already in use keep the format they were created with.

## Settings (per facility, `ISettingsService`)
| Key | Default | Meaning |
|---|---|---|
| `gst.ewaybill.threshold` | `50000` | The value above which a B2B invoice of goods needs an e-way bill (CGST rule 138) |
| `integrations.irp`, `integrations.irp.secret` | — | The e-invoice portal connection, per facility (Administration → Integrations) |
| `billing.registrationFee.enabled` | `true` | Charge `REG-NEW` on a patient's first visit |
| `billing.discount.defaultMaxPercent` | `0` | Staff discount allowed without approval when the user has no limit row |
| `billing.shift.required` | `true` | Counter collections need an open cashier shift |
| `billing.chargeCancel.approvalAbove` | `1000` | A charge worth more (₹, net) cancelled at the desk waits for someone who approves discounts; 0 makes every cancellation wait. OT, procedure, implant and bed-day charges always wait. Raising it waits for a second administrator |
| `billing.claimWriteOff.approvalAbove` | `0` | How much (₹) the insurance desk writes off alone on a claim (the insurer's deductions in a settlement, or the unpaid claim on a rejection); more waits for someone who approves insurance write-offs. 0 makes every write-off wait. Raising it waits for a second administrator |
| `billing.shift.shortageTolerance` | `0` | How much (₹) a drawer may be short and still close on the cashier's word; beyond it a supervisor accepts the shortage. Raising it waits for a second administrator |
| `billing.tally.ledgers` | built-in names | `TallyLedgerMap` JSON (income by department, mode ledgers, payer and doctor ledgers, `pharmacy` and `canteen` ledgers, `salesLedgerPerGstRate`; for the settlement journal `gatewaySettlementBank` "Bank", `gatewayCharges` "Payment Gateway Charges", `gatewayInputGst` "Input GST", and the clearing ledger is `modeLedgers.PaymentGateway`, "Payment Gateway Clearing" by default; for the card and UPI settlement journal `posSettlementBank` "Bank" and `posCharges` "Card & UPI Charges (MDR)", GST on `gatewayInputGst`); set on the Tally screen |
| `billing.tally.dailyPost.enabled` | `true` | Queue the daily Tally post |
| `billing.bills.sendOnWhatsApp` | `true` | Send the bill's PDF on WhatsApp when a payment settles it ([Bills on WhatsApp](#bills-on-whatsapp-int-wa)); the desk can still send it by hand when off |
| `billing.gateway.reconciliation` | `{ "enabled": true, "settlementDays": 3, "checkoutExpiryHours": 48 }` | Gateway reconciliation: read settlements and list exceptions (`enabled`); an online receipt not settled after `settlementDays` (1–30) is an exception; a portal checkout nobody paid is marked expired after `checkoutExpiryHours` (1–720) |
| `billing.pos.recheck` | `{ "timeoutMinutes": 15 }` | Terminal sales: a sale still waiting for the customer after `timeoutMinutes` (2–240) is cancelled on the terminal and marked timed out by `PosRecheckJob` |
| `billing.pos.settlements` | `{ "settlementDays": 3, "otherTerminals": [], "columns": {} }` | Card and UPI reconciliation: a card or UPI payment not settled after `settlementDays` (1–30) is listed; `otherTerminals` are the acquirer's TIDs of the other counters (pharmacy, canteen): Billing's receipts are not looked for there, and a transaction there that no pharmacy or canteen payment names is set aside (`OtherCounter`); `columns` adds the acquirer's headings by field (`rrn`, `approvalCode`, `amount`, `charges`, `tax`, `net`, `terminalId`, `transactionDate`, `transactionTime`, `card`, `type`, `mode`, `settlementDate`, `utr`), tried before the built-in ones |
| `gst.einvoice.from` | not set | The first invoice date GST e-invoicing applies to (`yyyy-MM-dd`); not set, the hospital does not e-invoice |
| `doctorfees.accrual.trigger` | `finalized` | `finalized` or `paid` |
| `doctorfees.tds.percent` | `10` | TDS (194J) on doctor payouts |

## Seed data (order 400)
Always: tariff plan `GEN` ("General", default), and the WhatsApp template `bill.shared` (`bill_shared`, document
header). Demo data only: payers `STAR`, `HDFCERGO`, `ICICILOM`, `NIA`, TPAs
`MEDIASSIST`, `MDINDIA`, `PARAMOUNT`, schemes `PMJAY`, `CGHS`, `ECHS` (with CGHS/ECHS rate plans), the `PKG-NVD`
normal-delivery package, and doctor share rules (consultation 80 %, IP visit 80 %, procedure 30 %, surgery 40 %, plus a
fixed ₹250 government-scheme consultation rule for `DR005`).

## Persistence notes
Schema `billing`; enums stored as text (no ordering on enum columns in SQL; filtered indexes use the text values).
Key unique indexes: charge (`source_module`, `source_reference`); bill number (non-null); one OP draft per visit; one
draft final bill per admission; receipt payment (`mode`, `external_id`); gateway order (provider, order id), merchant
reference, gateway payment id; POS (provider, transaction id); patient advance gateway payment id and receipt;
one open shift per cashier; one doctor-fee accrual per bill line and occurrence (the next only after the one before
was reversed; one reversal per accrual); one Tally export per facility/day (`tally_exports`, with `xmin` as its
concurrency token); one Tally voucher record per export and reference
(`tally_vouchers`, also `xmin`); refund `gateway_refund_id` (non-null); gateway settlement (provider, settlement id);
gateway exception (kind, key); webhook event (provider, event id). Gateway settlements, their lines and webhook events
are append-mostly and not data-audited; exceptions are. The migrations live in
`HyFib.HealthCare360.Server/Persistence/Migrations`: `TallyReconciliation` adds the Tally voucher records and the
export's tracking columns, and `GatewayReconciliation` adds `gateway_settlements`, `gateway_settlement_lines`,
`gateway_discrepancies`, `gateway_webhook_events` and the gateway status, attempt and retry columns of `refunds`.
`PosSettlements` adds `pos_settlement_imports` (unique facility and file SHA-256) and `pos_settlement_lines` (unique
facility and `line_key`; unique matched `receipt_payment_id` and `refund_id` where set; `xmin` as the concurrency
token), and indexes on `receipt_payments.reference` and `pos_transactions.approval_code` for the matching. Both tables
are append-mostly and not data-audited; imports and reviews are audited. `OpBillPackages` adds `bills.package_id`
(foreign key to `packages`, restrict) and `packages.used_for` (text; packages that existed before are `InPatient`).
`CreditNoteGst` adds `credit_note_lines` (a note's bill lines with their taxable value and GST; foreign keys to the
note, cascade, and to the bill line, restrict; indexed by bill line), the CGST, SGST and IGST of `credit_notes` and
`gst_time_barred`, and the invoice a credit note's IRN names on `einvoices` (`preceding_source`, `preceding_id`,
indexed where set). Notes and IRNs from before keep zeros and nulls.

`GstReturnsIrpDebitNotes` adds `debit_notes` and `debit_note_lines` (foreign keys to the bill, restrict, and to the
bill line, restrict; unique number), `bills.debit_amount` and `payer_debit_amount`, and on `einvoices` the `provider`
(earlier IRNs `manual`), `signed_invoice` and the e-way bill (`ewb_no`, `ewb_date`, `ewb_valid_till`).

## Not covered yet
* **The IRP connection has not met NIC or a GSP.** The NIC API client is built to v1.03 and tested against a fake
  portal and the simulator; it needs the hospital's sandbox credentials to be run for real. IRNs are asked for one
  document at a time from the register, not in a batch after each day's billing.
* **A payer credit note does not come from the settlement.** The claim desk records the deduction; the billing desk
  asks for the GST credit note on the bill afterwards. A deduction that is not a price reduction (a non-payable item
  the patient pays) needs none.
* **Debit notes print and post nowhere yet.** A debit note has no printed document and no Tally voucher; the payer
  receivable ageing (insurance claims) does not add payer debit notes.
* **GST returns.** The canteen's bills are not in them; amendments of earlier periods (B2BA, CDNRA), filing through a
  GSP, the rule 42 reversal and the GSTR-2B match are the accountant's. A series already in use with a stored number
  format longer than 16 characters keeps it until an administrator changes it.
* **The annual return.** The GST of a bill can be reduced until 30 November after its financial year; when the
  hospital files its annual return earlier, that earlier date ends it (s.34(2)), and HC360 does not know it.
* **Returns on an IP bill.** A medicine returned to the IP pharmacy is a negative line on the stay's bill, not a credit
  note, so a B2B bill with returned services cannot be an e-invoice as it stands (its problems say so).
* **PM-JAY's tier and incentive weights and its reservation rule.** The master's base rate is shown, not the state's
  weighted rate, and a procedure reserved for public hospitals is flagged, not refused, since HC360 does not record
  whether the hospital is public.
* **Packages that span several visits**, such as antenatal care or a course of physiotherapy: an OP package prices one
  bill.
* **A visit's payer cannot be changed after registration.** A patient whose cover is verified later is registered
  again, or pays. Staff concessions (the staff tariff) rest on the desk choosing "Staff"; a company's check-up batch is
  keyed by one person from the company's list and order.
* **Card and UPI settlement reports are imported by hand.** HC360 does not fetch them from the acquirer (Pine Labs and
  the banks offer them as downloads or e-mails, not through the terminal API).
* **A pharmacy or canteen payment without its reference** (the canteen does not insist on one) is not matched, since
  the counters keep no card digits: its transaction is set aside at the counter's terminal (or listed to look at on
  Billing's), and the payment is listed as not settled; the accountant pairs them by amount and time. Likewise a
  refund the acquirer reports under an RRN of its own while the pharmacist typed the sale's. The canteen records no
  refunds: a cancelled card bill's money given back is the canteen's own business.
* **A failed payout settled again in another settlement.** A failed settlement paid out after all is restored, but
  if the gateway carries its transactions in a new settlement instead, their lines find their receipts already matched
  by the failed one; the accountant resolves the exception with the new settlement's UTR.
* **Part of a card or UPI report** cannot be withdrawn: the whole report is, and a corrected file imported in its place.
* **Answering the gateway from HC360.** Accepting a dispute at the gateway, or contesting it with documents, is done
  in the gateway's dashboard; HC360 reads what was answered (the dispute's status and when the evidence was
  submitted). Sending the patient's bill, consent or discharge summary to the gateway from HC360 is the hospital's
  decision on what patient data leaves for a payment dispute.
* **The training gateway's disputes** are raised by the tests (its inbox); a training screen to raise or decide one is
  not there yet, and its simulated settlements do not take or return the money of a dispute.
* **A gateway dispute on a payment HC360 never recorded** opens nothing (the webhook is recorded as ignored); its
  settlement debit stays an amount-difference exception.
* **Part of a payment taken back** is not accepted by reversing the receipt (`billing.dispute_part`): the accountant
  puts it right on the bill.
* **Orders replaced before migration `AppointmentReplacedOrders`** are not remembered: a payment on one is found only
  by the settlement match, as a payment without a receipt that names no booking.
