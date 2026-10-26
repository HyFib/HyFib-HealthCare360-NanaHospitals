# Pharmacy module (`pharmacy`) — OP and IP pharmacy

The e-prescription queue; OP dispensing with FEFO batches, generic substitution, partial supply and counselling;
counter (OTC) sales; GST invoices with split payments and dues; returns and same-day cancellation; IP ward indents,
issues to admitted patients (charged to the IP account) and ward returns; Schedule H/H1/X and NDPS registers;
chronic-medication refills with reminders; and the pharmacist dashboard. SRS **PH-01..06** (with ERX-03/06, PP-05).

Project: `src/server/Modules/HyFib.HealthCare360.Modules.Pharmacy` (references SupplyChain for stock and Patients).
Schema `pharmacy`. Tests: `tests/Modules/HyFib.HealthCare360.Modules.Pharmacy.Tests` (58 unit and EF-model tests).
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
| `pharmacy.returns` | Sales returns and same-day invoice cancellation | Pharmacist, HospitalAdmin |
| `pharmacy.ip.issue` | Issue to admitted patients, ward returns, cancel ward indents | Pharmacist, HospitalAdmin |
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
| GET | `/queue?page&status&inpatient&includeClosed` | any of `pharmacy.queue.read`, `pharmacy.dispense`, `pharmacy.ip.issue` | → paged `QueueListItem` (oldest first; waiting minutes, controlled flag, refill flag) |
| GET | `/queue/{id}?storeId` | same | → `QueueEntryDetail` (lines with availability, MRP and nearest expiry in `storeId`; prescriber and registration number; refill status) |
| POST | `/queue/{id}/dispense` | `pharmacy.dispense` | `DispenseRequest` (storeId, lines[queueLineId, itemId, quantity, batchId?, substitutionReason, prescriberConsent], counsellingNotes, discountPercent, payments[mode Cash/Card/Upi/Credit, amount, reference], confirmControlledDrugs) → 201 `PharmacySaleCreated` (id, invoiceNumber, netAmount, paidAmount, dueAmount, status) |
| POST | `/queue/{id}/close` | `pharmacy.dispense` or `pharmacy.ip.issue` | `PharmacyReasonRequest` → 204. Partly dispensed lines count as dispensed, untouched lines are cancelled |
| POST | `/queue/{id}/refills` | `pharmacy.refills` | `AuthorizeRefillsRequest` (refills 1–12, validUntil > today, note) → 204 |

Dispensing rules. OP entries only; IP prescriptions are issued to the ward. The store must be a pharmacy store.
* **Partial supply**: each line dispenses up to its remaining quantity (`queue.over_dispense`). The entry stays
  *PartiallyDispensed* until every line is done.
* **FEFO** or a scanned batch, with a minimum shelf life of min(course days, 30), so medicine never expires mid-course.
* **Substitution** (`SubstitutionPolicy`). Dispensing the prescribed item, or any brand of a generically prescribed
  drug in the same strength, is not a substitution. Anything else must be the same generic and strength, and needs
  a reason and the prescriber's consent. It is flagged on the invoice line with the prescribed drug name.
  Error codes: `pharmacy.substitution_not_equivalent`, `pharmacy.substitution_consent`.
* **Controlled drugs** (H1/X/NDPS) need `confirmControlledDrugs` (`pharmacy.controlled_confirmation_required`) and
  write register lines with patient, prescriber and registration number.
* **Pricing**: MRP is GST-inclusive. The discount is capped by the facility setting `pharmacy.discount.max_percent`
  (default 10, `pharmacy.discount_limit`). Each line shows taxable value and CGST = SGST (half the rate each), and
  the bill is rounded to the rupee (round-off shown).
* **B2B invoices**: a counter sale may name a `buyer` registered for GST (GSTIN with a valid check character, legal
  name, and the state the goods are dispatched to, if any). The hospital's own GSTIN must be set
  (`gst.facility_gstin`). Goods handed over at the counter are supplied in the hospital's state (IGST Act s.10(1)(a):
  the movement ends with delivery there); goods dispatched to another state carry **IGST** instead of CGST and SGST.
  The MRP, and so the amount payable, does not change. The invoice prints the buyer, their GSTIN and the place of
  supply, and the day book posts the IGST. The buyer's address with its PIN code is kept for the GST e-invoice: when
  the hospital e-invoices, the sale is listed in Billing's e-invoice register, and once its IRN is recorded the invoice
  prints the IRN and the signed QR code and can no longer be cancelled here (422 `einvoice.active`) until the IRN is
  cancelled on the portal.
* **Payments** may be split across modes and must add up to the bill (`payment.mismatch`). Credit (a due) is
  allowed only for registered patients (`payment.credit_not_allowed`).

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
| GET | `/sales/{id}` · `/sales/{id}/returns` | same | → `PharmacySaleDetail` (GST invoice: HSN, batch, expiry, CGST/SGST, round-off, payments) · `SaleReturnDto[]` |
| POST | `/sales/counter` | `pharmacy.sales` | `CounterSaleRequest` (storeId, patientId? or customerName/customerPhone, prescription? {prescriberName, prescriberRegistrationNumber, prescriptionDate, reference}, lines[itemId, quantity, batchId?], discountPercent, payments, confirmControlledDrugs, buyer? {gstin, legalName, deliveryState?, address?}) → 201 `PharmacySaleCreated` |
| POST | `/sales/{id}/payments` | `pharmacy.sales` | `CollectPaymentRequest` (mode, amount, reference) → 204 (`payment.exceeds_due`) |
| POST | `/sales/{id}/returns` | `pharmacy.returns` | `SaleReturnRequest` (lines[saleItemId, quantity, restock], reason, refundMode, refundReference) → 201 `SaleReturnCreated` (returnNumber, refundAmount, adjustedAgainstDue, cashRefund) |
| POST | `/sales/{id}/cancel` | `pharmacy.returns` | `PharmacyReasonRequest` → 204. Same local day only and no returns (`sale.cancel_window`, `sale.has_returns`). Stock goes back to the original batches and the queue entry reopens |

OTC blocking (`SchedulePolicy`, Drugs & Cosmetics Rules): Schedule H, H1, X and NDPS medicines are never sold
without a prescription (`pharmacy.prescription_required`, with the offending items in `data.items`). H1/X also
need the buyer's identity for the register (`pharmacy.buyer_required`). Returns refund against any due first, then
pay out. Only unexpired stock is restocked; expired or damaged returns are refunded but not put back.

### In-patient pharmacy — `/ward-indents`, `/ip-issues` (PH-03)
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/ward-indents?page&status&admissionId&includeClosed` · `/ward-indents/{id}?storeId` | `pharmacy.ip.issue` or `pharmacy.ip.indent` | → paged `WardIndentListItem` (STAT first, then oldest) · `WardIndentDetail` (lines with availability) |
| POST | `/ward-indents` | `pharmacy.ip.indent` | `RaiseWardIndentRequest` (patientId, admissionId, visitId, wardName, bedLabel, urgency Routine/Stat, prescriptionId?, remarks, lines[itemId, quantity, remarks]) → 201 `PharmacyDocumentCreated` |
| POST | `/ward-indents/{id}/cancel` | `pharmacy.ip.issue` or `pharmacy.ip.indent` | `PharmacyReasonRequest` → 204 (what was already issued stays issued) |
| GET | `/ip-issues?page&admissionId&patientId` · `/ip-issues/{id}` | same | → paged `IpIssueListItem` · `IpIssueDetail` (lines with charge ids, returns) |
| POST | `/ip-issues` | `pharmacy.ip.issue` | `IpIssueRequest` (storeId, wardIndentId? or queueEntryId?, patientId, admissionId, visitId, lines[itemId, quantity, batchId?, wardIndentLineId?, queueLineId?, substitutionReason, prescriberConsent], confirmControlledDrugs) → 201 `IpIssueCreated` (id, number, totalAmount) |
| POST | `/ip-issues/{id}/returns` | `pharmacy.ip.issue` | `WardReturnRequest` (lines[ipIssueLineId, quantity, restock], reason) → 201 `WardReturnCreated` (id, number, creditAmount) |

**Charges** go through `IChargePoster`, which is implemented by Billing and idempotent on SourceModule + SourceReference:
* Each issue line is posted at its batch MRP (`UnitPriceOverride`) with `SourceModule = "pharmacy.ip"` and
  `SourceReference = <issue line id>`. Service `PHARM-MED` (drugs), `PHARM-IMPL` (implants) or `PHARM-CONS`
  (consumables and surgicals).
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
hospital and the State Drugs Controller. The annual return's form number and due date are taken from secondary sources
(Form 3I, before 31 March); check them against the Gazette before relying on them.

### Adverse drug reactions — `/adr` (Pharmacovigilance Programme of India)
A suspected reaction is reported on the PvPI suspected ADR reporting form by whoever saw it: the reaction and its dates,
seriousness (death, life-threatening, hospitalisation, disability, congenital anomaly, intervention, other) and outcome,
the suspected and concomitant drugs with batch, dose and dates, dechallenge and rechallenge, tests and history. The
patient is named by initials, as the form asks. A reporter sees and corrects their own drafts; pharmacovigilance
(`pharmacy.adr.manage`) sees every report, assesses causality (WHO-UMC) and records it as sent to the ADR monitoring
centre, after which it cannot change. A serious reaction is due within 15 days of the hospital learning of it
(`dueBy`, `overdue`). Reads and prints go through the patient guard and are audited.

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
| `PrescriptionSigned` (Platform.Contracts, from Clinical) | handled, same transaction | Creates the queue entry in the prescriber's transaction. Idempotent: at most one non-refill entry per prescription (unique filtered index) |
| `PrescriptionCancelled` | handled | Cancels open entries, revokes refills, lapses pending refill requests |
| `PatientDischarged` (Inpatient) | handled | Cancels pending ward indents and closes open IP queue entries |
| `PatientsMerged` (Patients) | handled | Moves queue entries, sales, returns, ward indents/issues/returns, refill requests and register links to the surviving patient (printed names/UHIDs on register lines are kept) |
| `ControlledStockMoved` (SupplyChain) | handled | Writes the register line for movements whose reference module does not start with `pharmacy.` (pharmacy writes its own) |
| `MedicationDispensed` (Platform.Contracts) | raised | On OP dispensing and counter sales to a registered patient (walk-in customers have none), and on every IP issue. Feeds medication history and the nursing MAR (handled by Inpatient) |

Stock ledger references written by this module: `pharmacy.dispense`, `pharmacy.sale`, `pharmacy.return`,
`pharmacy.cancel`, `pharmacy.ip_issue`, `pharmacy.ward_return`.

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
| `pharmacy.invoice` | `{FAC}PH{YY}{SEQ:7}` (OP dispensing and counter sales) |
| `pharmacy.return` | `{FAC}PHR{YY}{SEQ:6}` |
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

`queue_entries` (unique `prescription_id` where `refill_of_entry_id IS NULL`), `queue_lines`, `sales` (unique
`invoice_number`), `sale_items`, `sale_payments`, `sale_returns` (unique `return_number`), `sale_return_items`,
`ward_indents`, `ward_indent_lines`, `ip_issues`, `ip_issue_lines`, `ward_returns`, `ward_return_lines`,
`controlled_drug_register` (unique `ledger_entry_id`), `refill_requests`, `narcotic_counts`, `narcotic_count_lines`, `adr_reports` (unique `number`; drugs as jsonb). Queue entries and sales use `xmin`
optimistic concurrency. Foreign keys go to the Patients `Patient` table and to the SupplyChain store, item and
stock-batch tables.

## Open items for maintainers

* **ServiceCategory**: the IP pharmacy service items are seeded as `ServiceCategory.Consumable`. A
  `ServiceCategory.Pharmacy` value in Billing would give cleaner bill grouping.
* **Refill count at signing**: `PrescriptionSigned`/`PrescriptionLine` carry no refill authorisation today, so refills
  are authorised by the pharmacist on the queue entry. If Clinical adds a refill count to the event, the handler
  can set it when the entry is queued.
* Clinical's `DrugItemId` on prescription lines should reference `supplychain.items` (the drug master used here).
