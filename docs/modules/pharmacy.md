# Pharmacy module (`pharmacy`) — OP and IP pharmacy

The e-prescription queue; OP dispensing with FEFO batches, generic substitution, partial supply and counselling;
counter (OTC) sales; GST invoices with split payments and dues; returns and same-day cancellation; IP ward indents,
issues to admitted patients (charged to the IP account) and ward returns; Schedule H/H1/X and NDPS registers;
chronic-medication refills with reminders; and the pharmacist dashboard. SRS **PH-01..06** (with ERX-03/06, PP-05).

Project: `src/server/Modules/HyFib.HealthCare360.Modules.Pharmacy` (references SupplyChain for stock and Patients).
Schema `pharmacy`. Tests: `tests/Modules/HyFib.HealthCare360.Modules.Pharmacy.Tests` (95 unit and EF-model tests);
`PharmacyJourneyTests` in `tests/HyFib.HealthCare360.IntegrationTests` drives the journey end to end (an e-prescription
signed over an allergy, acknowledged, dispensed, paid and returned with approval; a ward indent for a narcotic issued,
charged and returned, with the stock ledger, the NDPS register and the running bill in step); `DrugPriceCeilingTests`
proves the ceilings;
`PharmacyRefundApprovalTests` in `tests/HyFib.HealthCare360.IntegrationTests` proves the refunds and write-offs that wait
for a second person, over the real API.
All stock movements go through SupplyChain's `StockService` (see `supplychain.md`) in the same transaction as the
pharmacy document. The pharmacy never changes batch quantities itself. A pharmacy store's stock positions and its
reorder, near-expiry and expired-stock reports (PH-04) are SupplyChain's stock screen (`/api/v1/inventory/stock`) and
inventory reports, read with the store's id; `PharmacyStockReportTests` in `tests/HyFib.HealthCare360.IntegrationTests`
proves them on stock received with chosen batches and expiry dates.

## Permissions

| Permission | Purpose | Default roles |
|---|---|---|
| `pharmacy.queue.read` | E-prescription queue and pharmacy stock | Pharmacist, HospitalAdmin |
| `pharmacy.dispense` | Dispense prescriptions (FEFO, substitution, counselling); close queue entries | Pharmacist, HospitalAdmin |
| `pharmacy.sales` | Counter sales, GST invoices, collection of dues | Pharmacist, HospitalAdmin |
| `pharmacy.returns` | Sales returns and same-day invoice cancellation; a cancellation, and a return paying money out or writing stock off, waits for someone who approves refunds | Pharmacist, HospitalAdmin |
| `pharmacy.ip.issue` | Issue to admitted patients, ward returns, cancel ward indents; approve another pharmacist's ward return not restocked | Pharmacist, HospitalAdmin |
| `billing.refunds.approve` (Billing's) | Approve a pharmacy cancellation, and a return paying money out or writing stock off, someone else asked for, as every other refund | Accountant, HospitalAdmin |
| `pharmacy.ip.indent` | Raise ward indents for admitted patients | Pharmacist, HospitalAdmin, **Nurse** |
| `pharmacy.registers` | View/export the controlled-drug and Schedule H registers, and the NDPS accounts | Pharmacist, HospitalAdmin |
| `pharmacy.ndps.count` | Count the narcotics at a change of shift, and witness another's count | Pharmacist, Nurse |
| `pharmacy.adr.report` | Report suspected adverse drug reactions (PvPI) | Pharmacist, Doctor, Nurse |
| `pharmacy.adr.manage` | Pharmacovigilance: every report, the causality assessment, sending it to the AMC | Pharmacist, QualityManager, HospitalAdmin |
| `pharmacy.refills` | Authorise refills; approve/reject refill requests | Pharmacist, HospitalAdmin |

**Counter staff** below means any of `pharmacy.queue.read`, `pharmacy.dispense`, `pharmacy.sales`, `pharmacy.ip.issue`.
Detail views that show a patient (`/queue/{id}`, `/sales/{id}`, `/ward-indents/{id}`, `/ip-issues/{id}`) go through
`PatientAccessGuard` and are audited. Searches match protected patient fields through `IFieldProtector`.

## Endpoints (`/api/v1/pharmacy`, tag Pharmacy)

### Queue and dispensing (PH-01, ERX-03)
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/queue?page&status&inpatient&includeClosed` | any of `pharmacy.queue.read`, `pharmacy.dispense`, `pharmacy.ip.issue` | → paged `QueueListItem` (oldest first; waiting minutes, controlled flag, refill flag, `hasUnacknowledgedOverride`) |
| GET | `/queue/{id}?storeId` | same | → `QueueEntryDetail` (lines with availability, MRP and nearest expiry in `storeId`; prescriber and registration number; refill status; `safetyOverrideReason`, and on each line its `safetyOverride` and who acknowledged it, when, and their note) |
| POST | `/queue/{id}/lines/{lineId}/acknowledge-override` | `pharmacy.dispense` | `AcknowledgeOverrideRequest` (note?, up to 500) → 204. 422 `pharmacy.no_override`, `pharmacy.override_acknowledged`, `queue.closed`; 403 `pharmacy.override_own_prescription` for the account that signed the prescription |
| POST | `/queue/{id}/dispense` | `pharmacy.dispense` | `DispenseRequest` (storeId, lines[queueLineId, itemId, quantity, batchId?, substitutionReason, prescriberConsent], counsellingNotes, discountPercent, payments[mode Cash/Card/Upi/Credit, amount, reference, terminalSaleId?], confirmControlledDrugs) → 201 `PharmacySaleCreated` (id, invoiceNumber, netAmount, paidAmount, dueAmount, status) |
| POST | `/queue/{id}/close` | `pharmacy.dispense` or `pharmacy.ip.issue` | `PharmacyReasonRequest` → 204. Partly dispensed lines count as dispensed, untouched lines are cancelled |
| POST | `/queue/{id}/refills` | `pharmacy.refills` | `AuthorizeRefillsRequest` (refills 1–12, validUntil > today, note) → 204 |

Dispensing rules. OP entries only; IP prescriptions are issued to the ward. The store must be a pharmacy store.
* **A prescriber's safety override** (MAKER-CHECKER.md slice 5, the clinical report's item 25). A line the doctor
  signed over a blocking safety alert (an allergy or class allergy, a contraindicated interaction, a dose above the
  maximum, a high-alert pattern) arrives with the alerts it was signed over, in words, and the prescription with the
  doctor's reason (`PrescriptionSigned`). The queue marks the entry ("Safety override"), the dispensing screen shows the
  alert and the reason, and the line is neither dispensed nor issued to a ward (422 `pharmacy.override_unacknowledged`)
  until a pharmacist (`pharmacy.dispense`) acknowledges it from their own sign-in, with what they did if they wish ("spoke
  to the doctor"). The account that signed the prescription never acknowledges it (403). The acknowledgement is audited
  at Warning (`pharmacy.safety_override_acknowledged`) with the patient, the alert, the doctor's reason, the note and the
  pharmacist. A refill is acknowledged again by whoever dispenses it. A ward indent is not tied to a prescription line, so
  an issue against an indent does not carry the override (the ward's own order did, and the MAR witnesses its doses).
* **Partial supply**: each line dispenses up to its remaining quantity (`queue.over_dispense`). The entry stays
  *PartiallyDispensed* until every line is done.
* **FEFO** or a scanned batch, with a minimum shelf life of min(course days, 30), so medicine never expires mid-course.
* **Substitution** (`SubstitutionPolicy`). Dispensing the prescribed item, or any brand of a generically prescribed
  drug in the same strength, is not a substitution. Anything else must be the same generic and strength, and needs
  a reason and the prescriber's consent. It is flagged on the invoice line with the prescribed drug name.
  Error codes: `pharmacy.substitution_not_equivalent`, `pharmacy.substitution_consent`.
* **Controlled drugs** (H1/X/NDPS) need `confirmControlledDrugs` (`pharmacy.controlled_confirmation_required`) and
  write register lines with patient, prescriber and registration number.
* **Price ceilings** (DPCO 2013): a batch of a scheduled medicine whose MRP is above the item's NPPA ceiling price
  (supplychain.md, *Price ceilings*) is not dispensed, sold at the counter or issued to a ward (422
  `pharmacy.price_above_ceiling`, naming the batch, its MRP and the ceiling): choose another batch, or the store returns
  it. An implant is charged at the lower of MRP and ceiling instead, as before.
* **Pricing**: MRP is GST-inclusive. The discount is capped by the facility setting `pharmacy.discount.max_percent`
  (default 10, `pharmacy.discount_limit`). Each line shows taxable value and CGST = SGST (half the rate each), and
  the bill is rounded to the rupee (round-off shown).
* **Free treatment by law** (BNSS 2023, s.397): the medicines of a visit the MLC register marks (the victim of a sexual
  offence or an acid attack; `QueueEntryDetail.freeTreatmentReason`) are given, not sold. The sale prices every line at
  nothing (its whole MRP off, recorded as `freeTreatmentReason`, not as a staff discount, so the limit does not apply)
  and takes no payment: a payment offered is refused (422 `pharmacy.free_treatment`). A refill of such a prescription
  is free too. The invoice prints "Free treatment by law (…)" where a discount would be.
* **B2B invoices**: a counter sale may name a `buyer` registered for GST (GSTIN with a valid check character, legal
  name, and the state the goods are dispatched to, if any). The hospital's own GSTIN must be set
  (`gst.facility_gstin`). Goods handed over at the counter are supplied in the hospital's state (IGST Act s.10(1)(a):
  the movement ends with delivery there); goods dispatched to another state carry **IGST** instead of CGST and SGST.
  The MRP, and so the amount payable, does not change. The invoice prints the buyer, their GSTIN and the place of
  supply, and the day book posts the IGST. The buyer's address with its PIN code is kept for the GST e-invoice: when
  the hospital e-invoices, the sale is listed in Billing's e-invoice register, and once its IRN is recorded the invoice
  prints the IRN and the signed QR code and can no longer be cancelled here (422 `einvoice.active`) until the IRN is
  cancelled on the portal. Until then (or once the IRN is cancelled) the invoice prints, where the IRN goes, that it is
  not a valid tax invoice for the buyer (GST rule 48(5); `missingIrn` on the sale's detail, billing.md *GST
  e-invoicing*). A **return** of such a sale is a GST credit note (CGST Act s.34(1)(b), goods returned):
  it is listed in the register too, as a `CRN` naming the sale's invoice, each medicine at the quantity returned with
  the GST carved out of its refund at the sale line's rate (`PharmacyReturnEInvoiceSource`), the same split the day
  book posts. A return made after 30 November following the financial year of the sale (s.34(2)) takes back the value
  and not the GST, in the day book too, and is not reported.
  When the facility is connected to the e-invoice portal (`integrations.irp`, billing.md *The portal connection*), the
  register asks the portal for the sale's IRN itself, on its own as the sale (or the return) is saved: the sale queues
  it through `IEInvoiceQueue` (billing.md *Automatic IRNs*); a B2B invoice above `gst.ewaybill.threshold` (₹50,000 by
  default) then gets its **e-way bill** from the IRN, with the vehicle number or the transporter's GSTIN and the
  distance (one day's validity per 200 km), and the number and validity are kept with the IRN.
* **GST returns.** Counter sales and their returns are in Billing's GSTR-1 and GSTR-3B (`PharmacyGstReturnSource`): a
  sale to a registered buyer is B2B; to a patient, B2CS, or B2CL when dispatched to another state above ₹1,00,000; a
  medicine at 0% is nil-rated; returns are credit notes at the GST carved out of their refund (CDNR, CDNUR, or netted
  into B2CS), and each medicine is in the HSN summary at its HSN code and quantity (`NOS`). Sales and returns are their
  own series in the documents issued.
* **Payments** may be split across modes and must add up to the bill (`payment.mismatch`). Credit (a due) is
  allowed only for registered patients (`payment.credit_not_allowed`). A card or UPI payment may be taken on the
  hospital's terminal (see *Card and UPI on the terminal* below); the rest of a split bill can be cash.

**Medicine labels.** After dispensing (automatically when the pharmacist's preference is on, or from the dispensed
entry) the staff app prints one label per medicine, without a dialog, on the workstation's label printer: patient,
medicine and quantity, directions, batch and expiry, hospital and date. On a ZPL or TSPL printer the server writes the
label (`dispensing`); otherwise the label page prints. See [integrations.md](integrations.md#barcode-labels-int-bar).

**Pharmacy tags and scanning (INT-BAR).** Tags with the medicine, batch, expiry, MRP per unit and a barcode of the item
code and batch (`PCM500#AB1234`) are printed from a posted goods receipt or a batch on the stock screen. Scanning a tag
at the counter adds the medicine with that batch; in the dispensing queue, "Scan to check a medicine" checks the strip
or tag in hand against the prescription and picks the tag's batch for its line. A batch that is not in the store,
blocked, expired or used up is warned about, not picked. Scanners that type like a keyboard and scanners on a COM port
(through HC360 Desktop) both work. See [integrations.md](integrations.md#barcode-labels-int-bar).

### Counter sales, payments, returns, stock — `/sales`, `/stock` (PH-02, PH-04)
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/stock?storeId&search&inStockOnly&take` | Counter staff | → `PharmacyStockRow[]` (schedule, controlled, Rx-only, cold chain, available, MRP, nearest expiry). A pharmacy tag's barcode as `search` (`PCM500#AB1234`) finds the item by its exact code and adds `scanned` (the batch it names, or why it cannot be handed out) |
| GET | `/stock/batches?storeId&itemId&includeEmpty` | Counter staff | → `BatchStock[]` (FEFO order) |
| GET | `/sales?page&from&to&status&type&storeId&withDues` | `pharmacy.sales` or `pharmacy.returns` | → paged `PharmacySaleListItem` |
| GET | `/sales/{id}` · `/sales/{id}/returns` | same | → `PharmacySaleDetail` (GST invoice: HSN, batch, expiry, CGST/SGST, round-off, payments, each refund with the payment it paid back; `pending`: a cancellation or return waiting for approval; `ownSale`: the reader sold it or took its payment) · `SaleReturnDto[]` |
| POST | `/sales/counter` | `pharmacy.sales` | `CounterSaleRequest` (storeId, patientId? or customerName/customerPhone, prescription? {prescriberName, prescriberRegistrationNumber, prescriptionDate, reference}, lines[itemId, quantity, batchId?], discountPercent, payments, confirmControlledDrugs, buyer? {gstin, legalName, deliveryState?, address?}) → 201 `PharmacySaleCreated`. The outside prescription is kept as presented, its date with it (`PharmacySaleDetail.prescriptionDate`, printed on the invoice beside its reference), so the sale is matched to the paper it was made against; a date after today is refused (400 `prescription.prescriptionDate`). Cash is held to the Income-tax Act's limit with the billing counters' (422 `payments.cash_limit`, see `docs/modules/billing.md`): an invoice is one transaction, and a patient's cash counts with what they paid at billing that day |
| POST | `/sales/{id}/payments` | `pharmacy.sales` | `CollectPaymentRequest` (mode, amount, reference, terminalSaleId?) → 204 (`payment.exceeds_due`, `payments.cash_limit`) |
| POST | `/terminal-sales` | `pharmacy.dispense` or `pharmacy.sales` | `{amount, terminalId, mode Card/Upi, patientId?}` → `CounterTerminalSale` (id, counter, provider, terminalId, transactionId, amount, mode, status, approvalCode, cardLast4, rrn, message, usedFor, createdAt, completedAt). Pushes the amount to the terminal |
| GET | `/terminal-sales/{id}` | same | → `CounterTerminalSale` (polls the terminal; the screen asks every 2 seconds) |
| POST | `/terminal-sales/{id}/cancel` | same | → `CounterTerminalSale`; a sale the customer paid first stays approved |
| GET | `/terminal-sales/unused` | same | → `CounterTerminalSale[]`: approved, no invoice records it yet and the accountants have not closed it, within 2 days, newest first |
| POST | `/sales/{id}/returns` | `pharmacy.returns` | `SaleReturnRequest` (lines[saleItemId, quantity, restock], reason; each sale line once) → 201 `SaleReturnCreated` (returnNumber, refundAmount, adjustedAgainstDue, cashRefund) when restocked and set wholly against the balance due; otherwise 202 `ApprovalRequestDto` (kind `pharmacy.sale_return`). 403 `pharmacy.return_own_sale` for whoever sold it or took its payment |
| POST | `/sales/{id}/cancel` | `pharmacy.returns` | `PharmacyReasonRequest` → 202 `ApprovalRequestDto` (kind `pharmacy.sale_cancel`). Same local day only and no returns (`sale.cancel_window`, `sale.has_returns`), no IRN (`einvoice.active`); 403 `pharmacy.cancel_own_sale` for whoever sold it or took its payment. Approved, every payment is paid back the way it was paid, the stock goes back to the original batches and the queue entry reopens |

OTC blocking (`SchedulePolicy`, Drugs & Cosmetics Rules): Schedule H, H1, X and NDPS medicines are never sold
without a prescription (`pharmacy.prescription_required`, with the offending items in `data.items`). H1/X also
need the buyer's identity for the register (`pharmacy.buyer_required`). Returns refund against any due first, then
pay out, the way the sale was paid. Only unexpired stock is restocked; expired or damaged returns are refunded and
written off (see below).

A card or UPI payment or collection needs its reference: the RRN or approval code from the charge slip, or the UPI
reference (UTR), unless it was taken on the terminal. A refund carries the reference of the payment it pays back, under
which the acquirer reports it. Billing's card and UPI settlement matches the acquirer's transactions with the invoice,
and a refund with the refund, by it, and lists a payment no settlement carries (through the Platform contract
`ICounterSalesSource`; see *Card and UPI reconciliation* in [billing.md](billing.md)).

**Card and UPI on the terminal.** The pharmacy takes card and UPI payments on the hospital's Pine Labs terminal through
the Platform contract `ICounterTerminal`, which Billing implements (see
[integrations.md](integrations.md#cardupi-pos-int-pos)). The counter starts the sale on `/terminal-sales`, follows it
until the customer has paid, and sends its id as `terminalSaleId` on the card or UPI payment of the dispense
(`DispenseRequest`), the counter sale (`CounterSaleRequest`) or the balance collection (`CollectPaymentRequest`). No
reference is typed. A `terminalSaleId` on a cash or credit payment is refused, and each terminal sale can be named once
in a request.
* The payment keeps the terminal's RRN as its `reference` (else the approval code), and the `approvalCode`, `cardLast4`
  and `terminalSaleId` the terminal reported (columns of `sale_payments`). `PharmacyPaymentDto` has the three, and the
  invoice shows them in its payments.
* The terminal sale is recorded in the same database transaction as the invoice, once. Another invoice naming it is
  refused (409 `pos.sale_used`). The amount and mode must be the sale's, and the sale must be the pharmacy's and
  approved: 422 `pos.sale_unknown`, `pos.sale_pending` (the customer has not paid yet), `pos.sale_not_paid` (declined,
  cancelled or timed out), `pos.sale_amount`, `pos.sale_mode`, `pos.sale_closed` (the accountants closed it, refunded
  on the terminal, say).
* A sale approved on the terminal that no invoice records within the facility's `billing.pos.recheck` time (15 minutes
  by default) is raised in Billing › Gateway reconciliation as an unrecorded payment, and the accountants are alerted
  once. Recording it on an invoice later clears it; once an accountant resolves it instead (the money refunded on the
  terminal), it is no longer offered to the counter or recorded.
* Billing's settlement match finds the payment by its RRN, or by its approval code (`CounterCardPayment.ApprovalCode`),
  so nothing typed is needed; the transaction is never matched with a Billing receipt.

**Screens.** In the payment panel of the dispensing screen and the counter sale, a card or UPI row offers **Take on
terminal**, as does the invoice's **Collect payment**. It opens a dialog with the row's amount:
1. Choose card or UPI, and the terminal id (on the terminal's label; the computer remembers it, for the pharmacy).
2. **Send to terminal**. The screen follows the terminal; **Cancel on the terminal** is offered while it waits.
3. Once approved, the row shows "Paid on the terminal · Card ending 1111 · RRN …" and cannot be edited.

The dialog also lists **Already paid on the terminal** (approved, not on an invoice), each with **Use this payment**,
for a payment taken before the screen was closed.

### Refunds and write-offs wait for a second person (maker-checker)

Money out of the pharmacy, and stock written off, are made only when a second person approves them
(`platform.approval_requests`, the Approvals inbox; see SECURITY.md, Maker-checker, and MAKER-CHECKER.md, slice 2):
* **Who.** A pharmacist (`pharmacy.returns`) asks for a cancellation or a return; someone else who approves every other
  refund (`billing.refunds.approve`: accountants and hospital administrators by default, so at night whoever of them is on
  duty) approves (`pharmacy.sale_cancel`, `pharmacy.sale_return`). Whoever sold the invoice, or took a payment on it,
  neither asks (403 `pharmacy.cancel_own_sale`, `pharmacy.return_own_sale`) nor approves (the same codes, the request
  waiting for someone else), whatever their permissions; the one who asked never approves (`approval.self`).
* **What waits.** A cancellation, always. A return that pays money out, or that writes stock off (a line not restocked,
  or expired), even when nothing is paid back. A return restocked and set wholly against the balance due is made at
  once (201). Nothing moves while a request waits: no refund, no stock back, no register entry. The invoice shows it
  waiting (`pending`), and the staff app's cancel and return dialogs say "Asked for approval".
* **What the approver reads.** The invoice and who sold it, the patient or customer, the items and quantities, the
  amount, how the money goes back ("₹400.00 in cash, ₹600.00 back to the card (RRN or approval code …)"), what comes off
  the balance due, whether the stock goes back or is written off, and the reason.
* **Back the way it came** (`SaleRefunds`). Each refund is a payment of the sale naming the payment it pays back
  (`refund_of_payment_id`), in that payment's mode and with its reference: cash from the till only for money that came in
  as cash, a card payment back to the card, UPI by UPI. A cancellation pays every payment back whole. A return pays back
  what is left after the balance due, shared among the payments in proportion to what each has left, to the paisa (each
  share rounded down, the paise left over to the largest remainders); payments taken together count cash, then card, then
  UPI. Refunds made before refunds named a payment are taken off the last payments first. The day book counts a return's
  money in each mode it went back in.
* **Applied only as asked.** Approved, the change is made in the approval's transaction, only over the invoice as it was
  asked (its version): a payment, a return or a cancellation made since, a line that expired, or a refund that would no
  longer split the same way fails the request (`approval.stale`) and changes nothing. A cancellation is approved only on
  the invoice's own day (the day book keeps a same-day cancellation out of the books, and a previous day's invoice is
  corrected by a return, a credit note): after midnight, or once the invoice has an IRN, it fails and the pharmacist
  takes a return instead. The stock ledger and the register name the approver as who recorded it, with "asked for by …,
  approved by …", and the change is audited (`pharmacy.sale_cancelled`, `pharmacy.sale_returned`) with both names.
* **A write-off is recorded.** A line not restocked comes back into its batch (`Return`) and goes out again as a
  write-off (`Adjustment`, or `ExpiryWriteOff` for an expired one), under the return's reference with "not restocked: …":
  the ledger shows what came back and what was destroyed, and a Schedule H1/X or NDPS drug has both lines in the
  controlled-drug register (the NDPS annual return counts it as returned by the patient and written off).
* **Ward returns.** A ward return restocked is made at once. One with a line not restocked waits for a second pharmacist
  (`pharmacy.ward_return`, approved by another holder of `pharmacy.ip.issue`, who must be able to open the patient's
  record); nothing is credited to the stay or written off until then. Approved, it is written off as above: a controlled
  drug is entered in the register as returned from the ward and written off, recorded by the approving pharmacist, so the
  second pharmacist is the witness of what is destroyed. The issue shows the return waiting (`pending`), and the request
  names any controlled or high-alert drug among the lines. A high-alert drug that is not on a register is written off in
  the stock ledger only.

### In-patient pharmacy — `/ward-indents`, `/ip-issues` (PH-03)
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/ward-indents?page&status&admissionId&includeClosed` · `/ward-indents/{id}?storeId` | `pharmacy.ip.issue` or `pharmacy.ip.indent` | → paged `WardIndentListItem` (STAT first, then oldest) · `WardIndentDetail` (lines with availability) |
| POST | `/ward-indents` | `pharmacy.ip.indent` | `RaiseWardIndentRequest` (patientId, admissionId, visitId, wardName, bedLabel, urgency Routine/Stat, prescriptionId?, remarks, lines[itemId, quantity, remarks]) → 201 `PharmacyDocumentCreated` |
| POST | `/ward-indents/{id}/cancel` | `pharmacy.ip.issue` or `pharmacy.ip.indent` | `PharmacyReasonRequest` → 204 (what was already issued stays issued) |
| GET | `/ip-issues?page&admissionId&patientId` · `/ip-issues/{id}` | same | → paged `IpIssueListItem` · `IpIssueDetail` (lines with charge ids, returns, `pending`: a ward return waiting for a second pharmacist) |
| POST | `/ip-issues` | `pharmacy.ip.issue` | `IpIssueRequest` (storeId, wardIndentId? or queueEntryId?, patientId, admissionId, visitId, lines[itemId, quantity, batchId?, wardIndentLineId?, queueLineId?, substitutionReason, prescriberConsent], confirmControlledDrugs) → 201 `IpIssueCreated` (id, number, totalAmount) |
| POST | `/ip-issues/{id}/returns` | `pharmacy.ip.issue` | `WardReturnRequest` (lines[ipIssueLineId, quantity, restock], reason; each line once) → 201 `WardReturnCreated` (id, number, creditAmount) when every line is restocked; otherwise 202 `ApprovalRequestDto` (kind `pharmacy.ward_return`) for a second pharmacist |

**Charges** go through `IChargePoster`, which is implemented by Billing and idempotent on SourceModule + SourceReference:
* Each issue line is posted at its price (`UnitPriceOverride` = the line's `unitPrice`) with
  `SourceModule = "pharmacy.ip"` and `SourceReference = <issue line id>`. Service `PHARM-MED` (drugs), `PHARM-IMPL`
  (implants) or `PHARM-CONS` (consumables and surgicals). The price is the batch MRP, or for a price-controlled implant
  its NPPA ceiling price (item master) when that is lower, as for a pack printed before the ceiling was cut; the line
  keeps both (`unitMrp`, `unitPrice`), and a return is credited at `unitPrice`.
* A device line (an implant, a consumable or a surgical item) also sends its `device`: brand, manufacturer, specification,
  batch, MRP and ceiling, printed beneath it on the bill (NPPA); its name carries no batch. A medicine's name keeps
  "(batch …)". A return's credit line carries the device as it was issued: its ceiling is the price charged when that
  was below the pack's MRP, not a ceiling revised since.
* A ward return of a whole, untouched line cancels that charge (`CancelAsync("pharmacy.ip", lineId)`). Otherwise
  (a partial return, or Billing refuses because the line is already billed) it posts a **negative-quantity** credit
  line with `SourceModule = "pharmacy.ip.return"` and `SourceReference = <return line id>`.
* Missing or inactive service items fail with `pharmacy.service_items_missing`.

### Registers — `/registers` (PH-06 statutory)
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/registers/controlled?page&storeId&itemId&schedule&from&to` | `pharmacy.registers` | → paged `ControlledRegisterRow` (Schedule H1/X and NDPS; per item and store in posting order: movement, quantity in/out, running balance, batch/expiry, patient name/UHID/address, prescriber and registration number, prescription and document number, recorded by) |
| GET | `/registers/controlled/export?…` | `pharmacy.registers` | → CSV (UTF-8 BOM, IST times, formula-injection-safe cells); the export is audited |
| GET | `/registers/schedule-h?page&storeId&from&to` | `pharmacy.registers` | → paged `ScheduleHRegisterRow` (Schedule H supplies from sales and IP issues, with patient, prescriber and prescription; H1 lines are in the controlled register) |
| GET | `/registers/schedule-h/export?…` | `pharmacy.registers` | → CSV, audited |

The controlled-drug register holds exactly one line per stock-ledger movement (unique `ledger_entry_id`). Pharmacy
documents write their own lines with the patient and prescriber. Everything else arrives through
`ControlledStockMoved` (see Events): GRN, transfers, adjustments, write-offs, OT consumption.

### Recall trace — `/recalls/trace`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/recalls/trace?itemId&batchNumber` | `pharmacy.registers` | → `RecallTrace`: every hand-over of the batch (matched by item and batch number, any case) to a patient, oldest first: OP dispensing against an e-prescription, counter sales (to a registered patient, or a walk-in by the name and phone given at the counter) and issues to the ward for an admitted patient, each with the document, the store, the patient's UHID, name and phone, what was given, what came back and what the patient kept; cancelled sales are left out. Also the stores still holding the batch, and whether each holds it on recall. Audited `pharmacy.recall_trace` (PHI access, Warning) with who ran it, the batch and how many patients |

The staff app's **Recall trace** page (Pharmacy) runs it only when asked, never as one types, and prints the call-back
list on the letterhead with a column to note each patient contacted. It does not reach implants recorded against an
operation or stock consumed in a ward sub-store, which are not issued to a named patient by batch through the
pharmacy; the stock ledger follows those. A batch is held on recall from Stock (`inventory`), and released only with a
second person's approval (see the Supply chain module).

### NDPS accounts — `/ndps` (NDPS Rules 52-O to 52-T, as amended in 2015)
The essential narcotic drugs (`EssentialNarcoticDrugs`: morphine, fentanyl, methadone, oxycodone, codeine and
hydrocodone, by the item's generic name) are accounted for from the controlled-drug register, which already holds every
movement with its running balance. Nothing is entered twice: the forms are views of the register.

| Method | Path | Permission | Request → Response |
|---|---|---|---|
| GET | `/ndps/stores` | `pharmacy.ndps.count` or `pharmacy.registers` | → the stores that have held an END |
| GET | `/ndps/daily?storeId&date` · `/ndps/daily/export` | `pharmacy.registers` | → `NdpsDailyAccount` (Form 3H): per drug the opening stock, received, dispensed with each patient's UHID, other movements, closing stock, and `balanced` (the closing stock agrees with the register's balance) / CSV. Both audited |
| GET | `/ndps/patients/{patientId}` | `pharmacy.registers` | → `NdpsPatientRecord` (Form 3E): the ENDs dispensed to the patient, with the prescriber; restricted records refused (VIP guard), the read audited |
| GET | `/ndps/annual?year` · `/ndps/annual/export` | `pharmacy.registers` | → `NdpsAnnualReturn`: January to December, all stores together (transfers between them cancel out): opening, received, consumed (less patients' returns), written off, closing, `balanced`, and next year's estimate (Form 3J: consumption plus 10%, in whole units) / CSV |
| GET | `/ndps/counts?storeId` | `pharmacy.ndps.count` or `pharmacy.registers` | → the last 50 counts with their lines |
| GET | `/ndps/counts/sheet?storeId` | `pharmacy.ndps.count` | → the drugs to count: names only, so the count is blind to the balance |
| POST | `/ndps/counts` | `pharmacy.ndps.count` | `RecordNarcoticCountRequest{storeId, lines[{itemId, counted}], explanation}`: the server takes the register balance at that moment; every drug with stock must be counted |
| POST | `/ndps/counts/{id}/witness` | `pharmacy.ndps.count` | `WitnessNarcoticCountRequest{explanation, version}`: a second user, never the counter (422 `ndps.witness_self`); a difference must be explained (400 `explanation`) and alerts every holder of `pharmacy.registers` after the save |

Not built: the application for recognition (Form 3F) and the certificate (Form 3G), which are paper between the
hospital and the State Drugs Controller (left out of Release 1 again: the recognition is held on paper and its number
is not yet recorded anywhere in HC360). The annual return's form number and due date are taken from secondary sources
(Form 3I, before 31 March); check them against the Gazette before relying on them.

### Adverse drug reactions — `/adr` (Pharmacovigilance Programme of India)
A suspected reaction is reported on the PvPI suspected ADR reporting form by whoever saw it: the reaction and its dates,
seriousness (death, life-threatening, hospitalisation, disability, congenital anomaly, intervention, other) and outcome,
the suspected and concomitant drugs with batch, dose and dates, dechallenge and rechallenge, tests and history. The
patient is named by initials, as the form asks. A reporter sees and corrects their own drafts; pharmacovigilance
(`pharmacy.adr.manage`) sees every report, assesses causality (WHO-UMC) and records it as sent to the ADR monitoring
centre, after which it cannot change. A serious reaction is due within 15 days of the hospital learning of it
(`dueBy`, `overdue`). Reads and prints go through the patient guard and are audited.

A new report raises an incident (type AdverseDrugReaction) in the Quality register in the same save, unless an incident
number was given on the form: death is graded death; life-threatening, disability or a congenital anomaly severe (so a
root-cause analysis is due); any other seriousness moderate; a non-serious reaction mild. The incident's number is kept
on the report.

| Method | Path | Permission | Request → Response |
|---|---|---|---|
| GET | `/adr?status` | `pharmacy.adr.report` or `.manage` | → `AdrReportListItem[]` (one's own, or all for pharmacovigilance) |
| GET | `/adr/{id}` · `/adr/{id}/document` | reporter or `pharmacy.adr.manage` | → `AdrReportDto` / the printable PvPI form |
| GET | `/adr/patients/{patientId}/medicines` | `pharmacy.adr.report` or `.manage` | → what the pharmacy gave the patient in the last 90 days (sales and ward issues), to fill in the drugs |
| POST · PUT | `/adr` · `/adr/{id}` | `pharmacy.adr.report` (PUT: reporter or `.manage`) | `SaveAdrReportRequest` (PUT with `version`); 422 `adr.submitted` once sent |
| POST | `/adr/{id}/submit` | `pharmacy.adr.manage` | `SubmitAdrReportRequest{causality, submittedOn, reference, version}` |

Not built: sending to VigiFlow (the report is entered there, or mailed to the AMC, from the printed form).

### Refills — `/refills` and the patient portal (PH-05, PP-05)
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/api/v1/pharmacy/refills?page&status` | `pharmacy.refills` | → paged `RefillRequestDto` |
| POST | `/api/v1/pharmacy/refills` | `pharmacy.refills` | `CreateRefillRequest` (queueEntryId, preferredPickupDate, notes) → 201 `RefillRequestCreated` |
| POST | `/api/v1/pharmacy/refills/{id}/approve` | `pharmacy.refills` | → 200 `{queueEntryId}`: a new queue entry `<Rx number>/R<n>` with the full prescription; the patient gets `pharmacy.refill_ready` |
| POST | `/api/v1/pharmacy/refills/{id}/reject` | `pharmacy.refills` | `PharmacyReasonRequest` → 204 |
| GET | `/api/v1/portal/refills` | `portal.access` | → `PortalRefillView` (refillable prescriptions and requests of the patients linked to the portal user) |
| POST | `/api/v1/portal/refills` | `portal.access` | `CreateRefillRequest` → 201 (only for linked patients, via `PortalPatientScope`) |

Refill rules. Authorised on the original OP prescription only, at most 12 refills, with an expiry date.
* Never for prescriptions containing H1/X/NDPS medicines (`refill.controlled`).
* A refill needs the prescription dispensed first (`refill.not_dispensed`) and authorisations left
  (`refill.none_left`, `refill.expired`).
* A request is decided only once (`refill.decided`).
* Cancelling the prescription revokes the remaining refills and lapses pending requests.

### Dashboard
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/api/v1/pharmacy/dashboard?storeId` | any of `pharmacy.queue.read`, `pharmacy.sales`, `pharmacy.ip.issue` | → `PharmacyDashboard` (pending OP/IP entries, partly dispensed, pending ward indents and refills, today's sales/amount/refunds by payment mode, near-expiry (90 days) batches and MRP value, average OP wait) |

## Events

| Event | Direction | Behaviour |
|---|---|---|
| `PrescriptionSigned` (Platform.Contracts, from Clinical) | handled, same transaction | Creates the queue entry in the prescriber's transaction, with the doctor's override reason, the account that signed, and each line's overridden alerts. Idempotent: at most one non-refill entry per prescription (unique filtered index) |
| `PrescriptionCancelled` | handled | Cancels open entries, revokes refills, lapses pending refill requests |
| `PatientDischarged` (Inpatient) | handled | Cancels pending ward indents and closes open IP queue entries |
| `PatientsMerged` (Patients) | handled | Moves queue entries, sales, returns, ward indents/issues/returns, refill requests and register links to the surviving patient (printed names/UHIDs on register lines are kept) |
| `ControlledStockMoved` (SupplyChain) | handled | Writes the register line for movements whose reference module does not start with `pharmacy.` (pharmacy writes its own) |
| `MedicationDispensed` (Platform.Contracts) | raised | On OP dispensing and counter sales to a registered patient (walk-in customers have none), and on every IP issue. Feeds medication history and the nursing MAR (handled by Inpatient) |

Stock ledger references written by this module: `pharmacy.dispense`, `pharmacy.sale`, `pharmacy.return`,
`pharmacy.cancel`, `pharmacy.ip_issue`, `pharmacy.ward_return`. A return not restocked writes a `Return` and a
write-off (`Adjustment` or `ExpiryWriteOff`) under its return's reference.

## Real time

Topic `Topics.Pharmacy(facilityId)` carries ids and status only; clients refetch with their own permissions:
* `pharmacy.queue.changed` `{entryId, status}`
* `pharmacy.ward_indent.changed` `{indentId, status}`
* `pharmacy.refill.changed` `{requestId, status}`

Changes made inside another module's transaction (the event handlers above) go through the outbox (type
`pharmacy.queue.changed`, expires after 10 minutes), so screens never refetch uncommitted data.

## Background job and notifications

`RefillReminderJob` runs only when `PlatformOptions.RunsJobs` is set. It ticks every 30 minutes and runs once a day
after 09:00 IST. Patients whose last dispensed supply of a refillable prescription runs out within 3 days get one
reminder per supply. The supply end is the local dispense date plus the longest course. The reminder goes only
while SMS/WhatsApp consent is active.

| Template key | Channels | Placeholders | Sent when |
|---|---|---|---|
| `pharmacy.refill_reminder` | SMS, WhatsApp | `name`, `medicines`, `reference`, `date`, `hospital` | Supply runs out soon |
| `pharmacy.refill_ready` | SMS, WhatsApp | `name`, `reference`, `date`, `hospital` | Refill request approved |

## Number series (per facility, yearly reset)

| Series key | Format |
|---|---|
| `pharmacy.invoice` | `{FAC}PH{YY}{SEQ:6}` (OP dispensing and counter sales; a GST series, at most 16 characters) |
| `pharmacy.return` | `{FAC}SR{YY}{SEQ:6}` (a credit note; a GST series, at most 16 characters) |
| `pharmacy.ward_indent` · `pharmacy.ip_issue` · `pharmacy.ward_return` | `{FAC}WI{YY}{SEQ:6}` · `{FAC}IPI{YY}{SEQ:6}` · `{FAC}WR{YY}{SEQ:6}` |
| `pharmacy.refill` | `{FAC}RF{YY}{SEQ:6}` |

## Settings

`pharmacy.discount.max_percent`: per facility (decimal, default 10). Neither a dispense nor a counter sale can
exceed it.

## Seed data (`PharmacySeeder`, order 160, idempotent)

* Service items `PHARM-MED`, `PHARM-CONS`, `PHARM-IMPL` (department PHARM, base price 0, price editable: charged at
  batch MRP).
* The four notification templates above.
* With `SeedDemoData`: two demo queue entries (`DEMO-RX-0001`, `DEMO-RX-0002`).

## Tables (schema `pharmacy`)

`queue_entries` (unique `prescription_id` where `refill_of_entry_id IS NULL`; `safety_override_reason`,
`signed_by_user_id`), `queue_lines` (`safety_override` and who acknowledged it, when, with their note), `sales` (unique
`invoice_number`), `sale_items`, `sale_payments` (a refund's `refund_of_payment_id`; `approval_code`, `card_last4` and
`terminal_sale_id` of a payment taken on the terminal), `sale_returns` (unique `return_number`), `sale_return_items`,
`ward_indents`, `ward_indent_lines`, `ip_issues`, `ip_issue_lines`, `ward_returns`, `ward_return_lines`,
`controlled_drug_register` (unique `ledger_entry_id`), `refill_requests`, `narcotic_counts`, `narcotic_count_lines`,
`adr_reports` (unique `number`; drugs as jsonb). Queue entries and sales use `xmin` optimistic concurrency. Foreign keys
go to the Patients `Patient` table and to the SupplyChain store, item and stock-batch tables.

## Open items for maintainers

* **GST series in use.** The invoice and return defaults were `{FAC}PH{YY}{SEQ:7}` and `{FAC}PHR{YY}{SEQ:6}`, 17 characters at a
  6-letter facility code; a series already in use keeps its stored format until an administrator changes it.

* **ServiceCategory**: the IP pharmacy service items are seeded as `ServiceCategory.Consumable`. A
  `ServiceCategory.Pharmacy` value in Billing would give cleaner bill grouping.
* **Refill count at signing**: `PrescriptionSigned`/`PrescriptionLine` carry no refill authorisation today, so refills
  are authorised by the pharmacist on the queue entry. If Clinical adds a refill count to the event, the handler
  can set it when the entry is queued.
* Clinical's `DrugItemId` on prescription lines should reference `supplychain.items` (the drug master used here).
