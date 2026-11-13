# Support Services module (`supportservices`)

CSSD (sterile instrument supply with recall by load), dietary (diet orders, ward-wise meal plans, kitchen production,
delivery tracking, optional diet charges), the canteen (POS with GST, staff credit with payroll deduction), bio-medical
waste, the mortuary, the ambulance fleet, linen and laundry, and housekeeping beyond the bed turnaround.
SRS **CSSD-01..05**, **DIET-01..05**, **CAN-01..04**. Maker-checker: a canteen bill's cancellation and an ambulance
trip's waiver wait for a second person (see *Second person* below).

Project: `src/server/Modules/HyFib.HealthCare360.Modules.SupportServices` (references Patients and Inpatient — layer 6 → 5,
for the admission's current ward and bed). Schema `supportservices`.
Tests: `tests/Modules/HyFib.HealthCare360.Modules.SupportServices.Tests` (192 unit tests), and in
`tests/HyFib.HealthCare360.IntegrationTests` `DietChargeTests` (diet charges on the IP bill with the setting on, and none
with it off), `CanteenCancellationApprovalTests`, `AmbulanceWaiverApprovalTests`, `BioMedicalWasteTests` and
`HousekeepingTests`; screen tests in `src/web/apps/staff/src/modules/housekeeping/supportServices.test.tsx`.

## Permissions

| Permission | Purpose | Default roles |
|---|---|---|
| `cssd.read` | View sets, wash cycles, loads, packs, issues, reports | CssdTechnician, Nurse, OtCoordinator, QualityManager, HospitalAdmin |
| `cssd.requests` | Request sterile sets; record packs used on patients | Nurse, OtCoordinator, CssdTechnician, HospitalAdmin |
| `cssd.process` | Soiled receipts, wash cycles, loads, issues | CssdTechnician, HospitalAdmin |
| `cssd.release` | Release loads, record biological indicators (triggers recalls) | CssdTechnician, HospitalAdmin |
| `cssd.recalls` | Receive recall/discrepancy alerts; acknowledge recalled packs | CssdTechnician, OtCoordinator, Nurse, QualityManager, HospitalAdmin |
| `cssd.masters.manage` | Instrument sets and count sheets | CssdTechnician, HospitalAdmin |
| `dietary.orders.read` | Diet orders and ward meal plans | Dietitian, Nurse, Doctor, CanteenManager, HospitalAdmin, Billing |
| `dietary.orders.manage` | Write, stop, review diet orders | Doctor, Dietitian, Nurse |
| `dietary.meals.manage` | Generate plans, production list, record deliveries | Dietitian, CanteenManager, Nurse, HospitalAdmin |
| `dietary.masters.manage` | Diet master | Dietitian, HospitalAdmin |
| `canteen.sales.create` | POS billing, staff credit repayments | CanteenManager |
| `canteen.sales.cancel` | Ask for today's bills to be cancelled | CanteenManager, HospitalAdmin |
| `canteen.cancellations.approve` | Approve a bill's cancellation another asked for, never one's own sale (the money goes back) | CanteenManager, Accountant, HospitalAdmin |
| `canteen.menu.manage` | Menu, prices, stock | CanteenManager, HospitalAdmin |
| `canteen.reports.read` | Sales reports | CanteenManager, HospitalAdmin, Accountant |
| `canteen.credit.manage` | Staff credit balances, statements, payroll export and settlement | CanteenManager, HrManager, HospitalAdmin |
| `bmw.record` | Weigh and bar-code bags of bio-medical waste where it is generated | Nurse, LabTechnician, CssdTechnician, OtCoordinator, QualityManager, HospitalAdmin |
| `bmw.manage` | Hand waste over to the treatment facility, the register and the annual report (Form IV) | QualityManager, HospitalAdmin |
| `mortuary.manage` | Receive and release bodies, the mortuary register and the release slip | Nurse, HospitalAdmin |
| `mortuary.configure` | Set up the cold-storage slots | HospitalAdmin |
| `ambulance.dispatch` | Take ambulance calls, send vehicles and record each trip; the trip log | FrontOffice, Nurse, HospitalAdmin |
| `ambulance.waivers.approve` | Approve the waiver of a completed trip's charge another asked for | Accountant, HospitalAdmin |
| `ambulance.fleet` | Keep the ambulance fleet and its certificates | HospitalAdmin |
| `linen.ward` | Send a ward's soiled linen to the laundry and report what it has lost | Nurse, HospitalAdmin |
| `linen.manage` | Issue, receive, wash and condemn linen; ward par levels; the linen list | StoreKeeper, HospitalAdmin |
| `housekeeping.request` | Ask for a spill, terminal or routine cleaning | Nurse, Doctor, HospitalAdmin |
| `housekeeping.work` | Do the scheduled and asked-for cleaning against the checklists | Nurse, HospitalAdmin |
| `housekeeping.verify` | Verify cleaning someone else did; the housekeeping report | Nurse, QualityManager, HospitalAdmin |
| `housekeeping.manage` | The areas cleaned on a schedule, their frequency and checklists | QualityManager, HospitalAdmin |

HC360 has no housekeeping role: a hospital gives its housekeeping staff a role holding `housekeeping.work`, and its
supervisors one holding `housekeeping.verify`.

## CSSD — `/api/v1/cssd`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/sets?includeInactive&search` | `cssd.read` or `cssd.requests` | → `InstrumentSetDto[]` (method, shelfLifeDays, count sheet `items`, instrumentCount, packsAvailable) |
| GET | `/sets/{id}` | same | → `InstrumentSetDto` |
| POST | `/sets` | `cssd.masters.manage` | `SaveInstrumentSetRequest` (code, name, category, ownerDepartmentId, method Steam/EthyleneOxide/Plasma, shelfLifeDays, items[name, quantity]) → 201 |
| PUT | `/sets/{id}` | `cssd.masters.manage` | `SaveInstrumentSetRequest` (+`version`) → 204. Saving replaces the count sheet; the set's audit event lists it as it was and as it is (`items`) |
| GET | `/requests?status&departmentId&openOnly` | `cssd.read` or `cssd.requests` | → `CssdRequestDto[]` (urgent first) |
| POST | `/requests` | `cssd.requests` | `CreateCssdRequestRequest` (departmentId, requiredBy, priority, lines[setId, quantity]) → 201 `CssdRequestDto` (urgent requests alert CSSD) |
| POST | `/requests/{id}/cancel` | `cssd.requests` | `CancelCssdRequestRequest` → 204 while the request is open or part issued (packs already issued stay issued); 422 `cssd.request_closed` once it is issued or cancelled |
| GET | `/receipts?from&to&discrepanciesOnly` | `cssd.read` | → `SoiledReceiptDto[]` |
| POST | `/receipts` | `cssd.process` | `ReceiveSoiledRequest` (departmentId, lines[setId, setIssueId?, counts[itemName, received, damaged]]) → 201 `SoiledReceiptDto` (count check; discrepancies alert `cssd.recalls` holders) |
| GET | `/packs?setId&status&expiringWithinDays` | `cssd.read` or `cssd.process` | → `SterilePackDto[]` (available by default, soonest expiry first) |
| GET | `/issues?departmentId&loadId&openRecalls` (paged) | `cssd.read` or `cssd.recalls` | → `PagedResult<SetIssueDto>` |
| POST | `/issues` | `cssd.process` | `IssuePacksRequest` (departmentId, cssdRequestId?, issuedToName, packIds ≤ 100) → `SetIssueDto[]` (expired/unreleased/recalled packs refused, and with them the whole issue; 409 `cssd.pack_taken` if a pack was issued concurrently). A `cssdRequestId` must be an open or part-issued request of the same department (400 otherwise). Each pack counts towards its set's line until the line is filled, so the request goes Open → PartiallyIssued → Issued; a pack beyond the line, or of a set not asked for, is issued to the department but not counted. The request's version guards its line counts: a counter issuing against it while another does gets 409 `cssd.request_changed` and scans again, so no pack goes uncounted (`CssdRequestTests`, `CssdRequestIssueTests`) |
| POST | `/issues/{id}/use` | `cssd.requests` or `cssd.process` | `RecordPackUseRequest` (patientId, usedAt) → 204 (traceability) |
| POST | `/issues/{id}/acknowledge-recall` | `cssd.recalls` | `AcknowledgeRecallRequest` (action) → 204 |
| GET | `/reports/summary?from&to` | `cssd.read` | → `CssdSummary` (loads, released, failed, failure rate, BI pending, by method, packs issued, open requests, discrepancy receipts, open recalls, stock by set, expiring ≤ 2 days) |

### Wash cycles — `/api/v1/cssd/wash-cycles`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/?status&setId&from&to&search` (paged) | `cssd.read` or `cssd.process` | → `PagedResult<WashCycleListItem>` (`setId`: cycles that washed that set, as the load dialog offers them) |
| GET | `/{id}` | same | → `WashCycleDto` (printout, A0, inspection, trays, and the packs made from them with their load) |
| POST | `/` | `cssd.process` | `StartWashCycleRequest` (washerName, program, operatorName, notes, trays[setId, quantity] ≤ 200) → 201 `WashCycleDto` (`{FAC}CW{YY}{SEQ:6}`) |
| POST | `/{id}/complete` | `cssd.process` | `CompleteWashCycleRequest` (temperatureC, holdMinutes, inspectedClean) → `WashCycleDto` |
| POST | `/{id}/abort` | `cssd.process` | `AbortWashCycleRequest` (reason) → `WashCycleDto` |

**Wash cycles (CSSD-04).** Trays received soiled go through a washer-disinfector before they are packed. A cycle is
Running until its result is recorded from the washer's printout: the temperature held during thermal disinfection and
for how long, and whether the trays were clean on inspection. It passes when the disinfection reaches A0 3000
(ISO 15883-1: each second counts 10^((T − 80) / 10), so 90 °C held for 5 minutes gives 3000) and the trays are clean;
otherwise, or when the operator aborts it, it fails and the trays are washed again in a new cycle. A sterilizer load
may name, for each set, the passed cycle its trays were washed in. The load is refused when that cycle has not passed
(`cssd.wash_not_passed`), did not wash the set (`cssd.wash_set_mismatch`), or has fewer trays of the set left than the
load packs (`cssd.wash_trays_used`): each tray washed makes one pack, and only the packs of a load that failed before
release (Reprocess) are packed again from the same cycle. Loads naming the same cycle take turns, so two loads saved
together cannot both pack its last tray. Each pack keeps the link, so a pack traces back through its load to its wash,
and a wash cycle lists the packs made from its trays. Naming the wash cycle is optional.

### Sterilizer loads — `/api/v1/cssd/loads`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/?status&method&biologicalPending&from&to&search` (paged) | `cssd.read` or `cssd.process` | → `PagedResult<LoadListItem>` |
| GET | `/{id}` | same | → `LoadDto` (parameters, indicators, packs with expiry) |
| GET | `/{id}/recall` | `cssd.read` or `cssd.recalls` | → `RecallReport` (every issue of the load with department, status, patient, acknowledgement) |
| POST | `/` | `cssd.process` | `CreateLoadRequest` (sterilizerName, method, program, containsImplants, operatorName, packs[setId, quantity, washCycleId?] ≤ 200; sets must match the method; a named wash cycle must have passed and washed at least that many trays of the set not yet packed) → 201 `LoadDto`; packs numbered `{cycle}-001…` and linked to their wash cycle |
| POST | `/{id}/start` | `cssd.process` | → `LoadDto` |
| POST | `/{id}/complete` | `cssd.process` | `CompleteLoadRequest` (temperatureC, pressureKpa, exposureMinutes, chemicalIndicator) → `LoadDto`; out-of-range parameters or a failed CI fail the load at once |
| POST | `/{id}/release` | `cssd.release` | → `LoadDto` (packs dated release date + shelf life; implant loads wait for BI pass) |
| POST | `/{id}/biological-indicator` | `cssd.release` | `BiologicalIndicatorRequest` (result, lot) → `LoadDto`; a failure on a released load triggers the recall |
| POST | `/{id}/abort` | `cssd.process` | `FailLoadRequest` (reason) → `LoadDto` |

Load states: Loaded → Running → Completed → Released, or Failed. Validated minimums: steam 121 °C/15 min, 126 °C/10 min,
134 °C/3 min (≤ 138 °C); ethylene oxide 37–63 °C ≥ 60 min; plasma 40–60 °C ≥ 24 min. **Recall** (CSSD-05): when a
released load's biological indicator fails, every issue from the load is flagged (including used and returned packs,
whose patients are listed for infection-control review), shelf and ward packs are marked Recalled, a critical audit
entry is written and `cssd.recalls` holders get a critical in-app alert.

**Pack labels (CSSD-04).** From a load the staff app prints a label per pack, without a dialog, on the workstation's
label printer: set name, the pack number as Code 128 (scanned at issue and use), method and use-by date. Until the
load is released the label says NOT RELEASED in white on black. On a ZPL or TSPL printer the server writes the label
(`cssd-packs`); otherwise the label page prints. See [integrations.md](integrations.md#barcode-labels-int-bar).

## Dietary — `/api/v1/dietary`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/diet-types?includeInactive` | any dietary orders/masters permission | → `DietTypeDto[]` |
| POST | `/diet-types` | `dietary.masters.manage` | `SaveDietTypeRequest` (code, name, category, chargeServiceItemId, defaultMealSlots) → 201 |
| PUT | `/diet-types/{id}` | `dietary.masters.manage` | `SaveDietTypeRequest` (+`version`) → 204 |
| GET | `/orders?status&wardId&needsReview` | `dietary.orders.read` | → `DietOrderDto[]` (active by default) |
| GET | `/orders/{id}` | `dietary.orders.read` | → `DietOrderDto` |
| GET | `/admissions/{admissionId}/orders` | `dietary.orders.read` | → `DietOrderDto[]` |
| POST | `/orders` | `dietary.orders.manage` | `CreateDietOrderRequest` (admissionId, dietTypeId, preference Vegetarian/NonVegetarian/Eggetarian/Jain, restrictions, allergyNotes, instructions, fluidRestrictionMl, calorieTargetKcal, mealSlots, startDate) → 201; supersedes the admission's current order and replans today (and tomorrow if planned) |
| POST | `/orders/{id}/stop` | `dietary.orders.manage` | `StopDietOrderRequest` → `DietOrderDto` (meals not yet served are cancelled) |
| POST | `/orders/{id}/review` | `dietary.orders.manage` | → `DietOrderDto` (clears `needsReview`) |
| POST | `/meal-plans/generate` | `dietary.meals.manage` | `GenerateMealPlanRequest` (date: yesterday…+2 days) → `MealPlanResult` (added, cancelled, planned, charges posted) |
| GET | `/meal-plans?date&wardId&slot` | `dietary.meals.manage` or `dietary.orders.read` | → `WardMealPlan[]` (ward-wise `PlannedMealDto` lines with bed, diet, preference, restrictions, allergies, delivery status) |
| GET | `/meal-plans/production?date` | `dietary.meals.manage` | → `ProductionSummary` (per round and diet: veg/non-veg/egg/Jain counts, special notes "bed: restrictions (allergy: …)"; rounds in service order, diets by code). Counts the facility's planned meals of the date (`KitchenProduction`): cancelled ones are left out, a nil-by-mouth patient has none, and after a diet change the rounds already served stay with the old diet and the rest are counted under the new one, once the plan is regenerated; a stopped order drops out (`KitchenProductionTests`, `KitchenProductionListTests`, `DietProductionTests`) |
| POST | `/meals/{id}/delivery` | `dietary.meals.manage` | `RecordDeliveryRequest` (Delivered/NotTaken — reason required/Returned, remarks, `version`) → `PlannedMealDto` |
| POST | `/meal-plans/deliveries` | `dietary.meals.manage` | `BulkDeliveryRequest` (date, slot, wardId, status Delivered) → `BulkDeliveryResult` |

Meal rounds (IST): 06:00 early morning, 08:00 breakfast, 11:00 mid-morning, 13:00 lunch, 16:30 evening, 19:30
dinner, 21:30 bedtime. Planning is idempotent: meals are added only for rounds not yet served, planned meals are
cancelled when the order stops or changes (nil-by-mouth cancels the next meal immediately), served meals are never
changed. Delivery: Planned → Delivered or NotTaken; Delivered → Returned.

## Canteen — `/api/v1/canteen`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/menu?category&availableOnly` | `canteen.sales.create` or `canteen.menu.manage` | → `MenuItemDto[]` |
| POST | `/menu` | `canteen.menu.manage` | `SaveMenuItemRequest` (code, name, category, price, gstRate 0/5/12/18/28, hsnSac (4, 6 or 8 digits; empty = SAC 996333), priceIncludesGst, isVeg, isAvailable, trackStock) → 201 |
| PUT | `/menu/{id}` | `canteen.menu.manage` | `SaveMenuItemRequest` (+`version`) → 204 |
| POST | `/menu/{id}/stock` | `canteen.menu.manage` | `StockMovementRequest` (Receipt/Adjustment/Wastage, quantity, reason) → `MenuItemDto` |
| POST | `/sales` | `canteen.sales.create` | `CreateSaleRequest` (customerType Staff/Attendant/Visitor/Patient, customerName, employeeCode + name (StaffCredit), patientId, paymentMode Cash/Upi/Card/StaffCredit, paymentReference, terminalSaleId?, lines[menuItemId, quantity]) → 201 `CanteenSaleDto` (bill number, taxable, CGST, SGST, gross, round-off, net, lines with HSN/SAC code and GST rate; for a bill paid on the terminal also `approvalCode`, `cardLast4` and `terminalSaleId`) |
| POST | `/terminal-sales` | `canteen.sales.create` | `{amount, terminalId, mode Card/Upi, patientId?}` → `CounterTerminalSale` (id, counter, provider, terminalId, transactionId, amount, mode, status, approvalCode, cardLast4, rrn, message, usedFor, createdAt, completedAt). Pushes the amount to the terminal |
| GET | `/terminal-sales/{id}` | same | → `CounterTerminalSale` (polls the terminal) |
| POST | `/terminal-sales/{id}/cancel` | same | → `CounterTerminalSale`; a sale the customer paid first stays approved |
| GET | `/terminal-sales/unused` | same | → `CounterTerminalSale[]`: approved, no bill records it yet, within 2 days, newest first |
| GET | `/sales?date&status&paymentMode&employeeCode&search` (paged) | sales.create or reports.read | → `PagedResult<CanteenSaleListItem>` |
| GET | `/sales/{id}` | same | → `CanteenSaleDto` with `pendingCancellation` (a cancellation waiting) and `ownSale` (the reader sold it) |
| POST | `/sales/{id}/cancel` | `canteen.sales.cancel` | `CancelSaleRequest` → 202 the approval request (`canteen.sale_cancel`); the bill stands until approved. Same business day only (422 `canteen.day_closed`, `canteen.sale_cancelled`) |
| GET | `/reports/daily?date` | `canteen.reports.read` | → `DailySalesSummary` (bills, cancelled, taxable, CGST, SGST, round-off, net; by payment mode, customer type, category; item consumption and closing stock; `byHsnSac`: quantity, taxable, CGST, SGST and total per HSN/SAC code and GST rate, for the GSTR-1 HSN summary) |

A bill is paid when it is made, so its cancellation pays money back and waits for a second person (maker-checker): any
canteen user who may cancel asks with a reason (the cashier who sold it included), and someone who holds
`canteen.cancellations.approve` approves it from the approvals inbox, never the one who asked (`approval.self`) nor the
cashier who sold the bill (403 `canteen.cancel_own_sale`; the seller is the account that made the bill). Approved, the
money goes back the way it was paid, whole: cash from the till, a card or UPI bill to the card or UPI under its
reference (never from the till), a staff credit bill off the employee's ledger (a reversal entry); packaged stock comes
back; the bill keeps who asked, who approved and how the money went back (`refundMode`, `refundAmount`), and the audit
trail has `canteen.sale_cancelled` with both names. Approved only over the bill as asked, on its business day
(`approval.stale` otherwise). The day's report counts a cancelled bill and leaves it out of the takings.

A card or UPI bill's `paymentReference` (the RRN or approval code from the charge slip, or the UPI reference) is how
Billing's card and UPI settlement matches the acquirer's transaction with the bill; a bill without one is listed there
as not settled. See *Card and UPI reconciliation* in [billing.md](billing.md).

**Card and UPI on the terminal.** The canteen takes card and UPI payments on the hospital's Pine Labs terminal through
the Platform contract `ICounterTerminal`, which Billing implements (see
[integrations.md](integrations.md#cardupi-pos-int-pos)). The counter starts the sale on `/terminal-sales` for the bill's
total and follows it until the customer has paid, and `CreateSaleRequest.terminalSaleId` then saves the bill with it.
The bill's net amount must equal the terminal sale's, and `terminalSaleId` is refused unless the mode is Card or Upi.
Nothing is typed: `paymentReference` becomes the terminal's RRN (else the approval code), and the bill keeps
`approvalCode`, `cardLast4` and `terminalSaleId` (columns of `canteen_sales`; `CanteenSaleDto` has them).
* The terminal sale is recorded in the same database transaction as the bill, once. Another bill naming it is refused
  (409 `pos.sale_used`). A sale that is not the canteen's, not approved or of another amount is refused: 422
  `pos.sale_unknown`, `pos.sale_pending`, `pos.sale_not_paid`, `pos.sale_amount`, `pos.sale_mode`.
* A sale approved on the terminal that no bill records within the facility's `billing.pos.recheck` time (15 minutes by
  default) is raised in Billing › Gateway reconciliation as an unrecorded payment, and the accountants are alerted
  once. Recording it on a bill later clears it.
* Billing's settlement match finds the bill by its RRN or approval code, and never matches it with a Billing receipt.

On the canteen's bill screen, **Take on terminal** asks for the card or UPI and the terminal id (remembered on the
computer) and sends the bill's total. Once approved, **Finish the bill** saves the bill with the payment. The cart is
locked while a terminal payment is attached. The dialog also lists **Already paid on the terminal** with **Use this
payment**, for a payment taken before the screen was closed.

### Staff credit — `/api/v1/canteen/staff-credit`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/balances?outstandingOnly` | `canteen.credit.manage` | → `StaffBalanceDto[]` |
| GET | `/statement?employeeCode&period=yyyy-MM` | `canteen.credit.manage` | → `StaffStatement` (opening, purchases, credits, closing, running balance lines) |
| GET | `/export?period=yyyy-MM` | `canteen.credit.manage` | → `text/csv` for HR payroll deduction (formula-safe, audited) |
| POST | `/settlements` | `canteen.credit.manage` | `SettleStaffCreditRequest` (period, payroll reference, employeeCodes?) → `SettlementResult` (period, employees, amount, skipped[]). Posts each balance to HR payroll as a `CANTEEN` deduction in one transaction; idempotent per employee and month; the month must have ended. Balances of employees missing from HR or in an approved payroll month stay outstanding and are listed in `skipped` |
| POST | `/payments` | `canteen.credit.manage` or `canteen.sales.create` | `StaffPaymentRequest` (employeeCode, amount, reference) → `StaffBalanceDto` |

GST: 5 % restaurant GST by default, prices GST-inclusive (taxable = price × 100 ÷ 105); tax per line, split equally into
CGST/SGST (CGST takes the odd paisa); bill rounded to the nearest rupee with a round-off line. Each menu item may carry an
HSN code (packaged goods) or SAC; food and drink made in the canteen without one is billed under SAC 996333 (services
provided by canteens, `MenuItem.DefaultSac`). The code, like the name, price and rate, is copied onto the bill line when
the bill is made. The receipt is a tax invoice (CGST Rules, rule 46): the facility's legal name, address and GSTIN from
the facility master (the GSTIN line is left out when none is configured), bill number and date, each line's HSN/SAC code
and GST rate, taxable value, CGST, SGST (also by rate) and the total; a bill with only nil-rated items is titled "Bill of
supply", and one with both "Invoice-cum-bill of supply" (rules 49 and 46A). Staff credit is only for
active employees in the HR directory (unknown codes → field error on `employeeCode`, separated staff → 422
`canteen.employee_inactive`); the bill carries the employee's name from HR, and the outstanding balance must stay within
`supportservices.canteen.staffCreditLimit`.

## Bio-medical waste — `/api/v1/bmw` (BMW Management Rules 2016, as amended in 2018)
Each bag or container is weighed and bar-coded where the waste is generated (`bmw-bag` number), in its Schedule I
colour: yellow (anatomical, soiled, discarded medicines, chemical, lab and cytotoxic), red (contaminated recyclable
plastics), white (sharps, in translucent puncture-proof containers) or blue (glassware and metallic implants). Bags wait
in the storage area until the common bio-medical waste treatment facility (CBWTF) collects them; untreated waste is not
kept beyond 48 hours, so older bags are flagged, and each handover counts the bags that were. A bag labelled in error is
voided with its reason before it leaves. The month's register (day by category, in kg) is what the hospital publishes on
its website; the year's totals, with the authorisation (`bmw.authorisationNumber`) and the CBWTF (`bmw.cbwtf`) set under
Administration › Settings, are for the annual report (Form IV) due to the State Pollution Control Board by 30 June.

| Method | Path | Permission | Request → Response |
|---|---|---|---|
| GET | `/bags?from&to` · `/bags/pending` | `bmw.record` or `bmw.manage` | → `WasteBagDto[]` (at most 92 days) / `PendingWaste` (oldest first, totals by colour, `overdue`) |
| POST | `/bags` | `bmw.record` | `RecordWasteBagRequest{category, departmentId?, source?, weightKg, collectedAt?}` (more than 0, at most 50 kg; not in the future nor over a week old) → the bag with its bar code |
| POST | `/bags/{id}/void` | `bmw.record` or `bmw.manage` | `VoidWasteBagRequest{reason, version}`; 422 `bmw.bag_not_pending` once handed over |
| GET | `/bags/{id}/label` | `bmw.record` or `bmw.manage` | → `WasteBagLabel` (bar code, category, colour, symbol, where filled, weight, date, HCF identifier, hospital); 422 `bmw.hcf_id_required` until `bmw.hcfId` or `bmw.authorisationNumber` is set |
| GET · POST | `/handovers` | `bmw.manage` | → the last week's `WasteHandoverDto[]` (with each bag's two weights and `mismatchedBags`) / `HandOverWasteRequest{bags: [{barcode, weightKg}], cbwtf?, vehicleNumber, receivedBy, receiptNumber?}`: every bag must be waiting and is weighed again at the handover, once; the handover counts those kept beyond 48 hours and those whose weight differs |
| GET | `/register?year&month` · `/register/export` | `bmw.manage` | → `WasteMonth` / CSV |
| GET | `/annual?year` | `bmw.manage` | → `WasteYear` (Form IV totals by colour, average a day, handovers, late and voided bags) |
| GET | `/annual/form-iv?year` | `bmw.manage` | → `FormIv`: the form's numbered items answered from the year's records |
| GET · POST | `/accidents?year` · `/accidents` | `bmw.manage` · `bmw.record` or `bmw.manage` | → the year's `WasteAccidentDto[]` / `RecordWasteAccidentRequest` (Form I's items) → 201 |
| GET | `/accidents/{id}/form-i` | `bmw.manage` | → `FormI`: the form's numbered items answered from the record |

**Labels.** Every bag's label (Schedule IV, as amended in 2018) carries the bar code, the category in white on black
with its symbol (biohazard; cytotoxic for yellow), the hospital's HCF identifier (`bmw.hcfId`, the identifier for the
CPCB bar-code system; the authorisation number when it is not set), the weight and the date, and where it was filled.
The staff app prints it on the workstation's label printer through `POST /api/v1/integrations/labels/waste-bags` (ZPL or
TSPL; the weight, the date and the HCF identifier are never cut short, and a label too small for them is refused), or on
the printer's Windows driver.

**The weighed handover.** Each bag is scanned and weighed again on the scale in front of the CBWTF's driver, and both
weights are kept per bag (`handoverWeightKg`); a bag whose weight at handover differs from where it was filled by more
than 5% (at least 100 g) is flagged (`weightMismatch`) and counted on the handover (`mismatchedBags`): opened, added to,
or swapped. The handover's total is the weights at handover, what the CBWTF signs for.

**Form I and Form IV.** An accident (a needle-stick or sharps injury, a spill, an exposure, a fire, waste mixed, an
accident while carrying waste) is recorded with Form I's items: when, what, the sequence of events, whether the
authority was told, the waste involved, the persons affected and the effects, the emergency measures, the steps to
alleviate and to prevent recurrence, and who reported it. Form I and Form IV are generated from the records, item by
item in the forms' numbering; what HC360 does not record (the emergency control policy, on-site treatment, training
given, incinerator emissions, liquid waste) is answered "To be filled by the occupier", never left to look answered.
Form IV counts the beds (active beds), the waste generated and handed over by category, the bags late and weighed
differently, and the year's accidents with the persons affected and the remedial action.

Not built: liquid waste, the training register, sending the forms to the Board (they are printed to sign).

## Mortuary — `/api/v1/mortuary`
The administrator sets up the cold-storage slots (a label per chamber door; a slot holding a body cannot be taken out of
use). A body is received into a free slot (`mortuary-body` number) with who brought it, its identification marks and
the belongings that came with it. A patient who died in the hospital comes in only once the death is on record (the
cause of death certified, or the stay discharged as expired); their name, sex, age and time of death are the patient's,
and the body is medico-legal if their last visit was. A body brought dead or by the police is described (an
unidentified one as "Unknown male, about 40"), and a medico-legal or police body names its police station. A body kept
beyond 72 hours unreleased is shown as unclaimed, for the police to be told.

A body leaves to someone named, on the authority its case needs: relatives with their relationship and identity proof,
and only an identified body; a medico-legal body to relatives only on the police's clearance (the inquest or post-mortem
reference); the police for a post-mortem on the officer's name, number and station; the municipality, for an unclaimed
body, on the police's clearance; a medical college for body donation, never a medico-legal body. The release frees the
slot, keeps the slot it was in for the register, and prints the release slip with the belongings handed over.

A release on the police's clearance (a medico-legal body to the relatives, an unclaimed body to the municipality) is
witnessed by a second member of the mortuary staff at the screen, with their own user name and password (maker-checker,
slice 9; `POST /api/v1/auth/witness`, purpose `mortuary.release`, `mortuary.manage`), never the one who releases it. The
body record (`releaseWitnessName`) and the slip name the witness, and every release is audited `mortuary.body_released`
with who released it and who witnessed.

| Method | Path | Permission | Request → Response |
|---|---|---|---|
| GET | `/slots` | `mortuary.manage` or `mortuary.configure` | → `MortuarySlotDto[]` (who is in each, `unclaimed`) |
| PUT | `/slots` | `mortuary.configure` | `SaveSlotsRequest{slots: [{id?, code, chamber?, isActive, version?}]}` (new slots without an id; changed ones with their version); 422 `mortuary.slot_occupied` |
| GET | `/bodies?status&from&to` | `mortuary.manage` | → `BodyDto[]`: in storage, or received in the period (at most a year; default the last 30 days) |
| POST | `/bodies` | `mortuary.manage` | `ReceiveBodyRequest{source, patientId?, name?, identified, sex?, ageYears?, diedAt?, broughtBy?, isMlc, mlcNumber?, policeStation?, identificationMarks?, belongings?, slotId}`; 422 `mortuary.death_not_recorded`, 409 `mortuary.slot_occupied` / `mortuary.already_in` |
| GET | `/bodies/{id}` | `mortuary.manage` | → `BodyDto` (a patient's body through the patient access guard) |
| POST | `/bodies/{id}/release` | `mortuary.manage` | `ReleaseBodyRequest{to, receivedBy, relationship?, idProof?, policeClearance?, belongingsHandedOver, notes?, version, witnessConfirmationId?}`; 422 `mortuary.released`, `mortuary.unidentified`, `mortuary.mlc_donation`, `mortuary.witness_required`, `mortuary.witness_is_releaser`, `witness.confirmation_unusable` |
| GET | `/bodies/{id}/slip` | `mortuary.manage` | → the release slip (`PrintableDocumentDto`), audited for a patient |

Not built: embalming, the post-mortem itself, freezer temperature logging, and mortuary charges.

## Ambulance — `/api/v1/ambulance`
The administrator keeps the fleet: each vehicle's registration, kind (basic or advanced life support, patient transport,
hearse), the dates its fitness certificate, insurance and pollution certificate run to, and its odometer, which only goes
forward. A call is taken with the caller, the pickup and, for anything but an emergency, where the trip goes; a
registered patient is linked (a body transport only for a patient whose death is on record). A vehicle is sent only
while all three certificates are on record and in force, only if it suits the trip (an emergency needs BLS or ALS, a body
goes only in the hearse, the hearse carries no patient), only while it is not out on another trip, and with a technician
or paramedic for an emergency or a transfer. The crew's steps follow in order: reached the pickup, patient on board,
back, when the odometer gives the kilometres run. A completed trip for a registered patient is charged on their bill in
the same save: the base fare for the vehicle's kind (`AMB-BLS`, `AMB-ALS`, `AMB-PTS`, `AMB-HEARSE`) covers the first 10
km, and `AMB-KM` each kilometre beyond. Waiving the charge (a free scheme transfer, a staff member's family) waits for a
second person (maker-checker, slice 15): the desk asks with the reason, at completion (`waiveReason`) or afterwards, and
the charge stays on the bill until someone who holds `ambulance.waivers.approve` approves it, never the one who asked;
then the trip's charges are withdrawn from the bill (the module cancels its own charges) and the trip keeps the reason,
who asked and who waived it. A charge already on a final bill cannot be withdrawn: the approval fails and a credit note
is asked for at the billing desk. A hospital lets the desk waive alone only by switching `ambulance.waiverApproval` off
(a second administrator approves, SettingGuard); the waiver is then made at once and audited at Warning
(`ambulance.charge_waived`). A trip with no registered patient is not charged, and its reason is kept as given. The
trip log gives the period's trips, kilometres, and the median call-to-dispatch and call-to-patient times of emergencies.

| Method | Path | Permission | Request → Response |
|---|---|---|---|
| GET · POST | `/vehicles` | `ambulance.dispatch` or `ambulance.fleet` · `ambulance.fleet` | → `AmbulanceVehicleDto[]` (with `lapsed` certificates and the trip it is out on) / `SaveVehicleRequest{id?, registrationNumber, kind, fitnessValidUntil?, insuranceValidUntil?, pollutionValidUntil?, odometerKm, isActive, version?}`; 409 `ambulance.duplicate` |
| GET · POST | `/trips` | `ambulance.dispatch` | → open trips, oldest call first / `RequestTripRequest{purpose, patientId?, patientName?, callerName, callerPhone, pickupAddress, dropAddress?, complaint?}` |
| POST | `/trips/{id}/dispatch` | `ambulance.dispatch` | `DispatchRequest{vehicleId, driverName, crewName?, version}`; 422 `ambulance.papers_lapsed`, 409 `ambulance.vehicle_busy` |
| POST | `/trips/{id}/at-scene` · `/left-scene` | `ambulance.dispatch` | `TripStepRequest{version}`; 422 `ambulance.out_of_order` |
| POST | `/trips/{id}/complete` | `ambulance.dispatch` | `CompleteTripRequest{endOdometerKm, waiveReason?, version}`: posts the charges, and with a `waiveReason` asks for the waiver (the trip comes back with `waiverPending`); 422 `ambulance.no_tariff` when a fare is missing from the catalogue |
| POST | `/trips/{id}/waive` | `ambulance.dispatch` | `WaiveTripChargeRequest{reason, version}` → 202 the approval request (`ambulance.charge_waiver`), or 200 the trip where the desk waives alone; 422 `ambulance.not_completed`, `ambulance.nothing_charged`, `ambulance.already_waived` |
| POST | `/trips/{id}/cancel` | `ambulance.dispatch` | `CancelTripRequest{reason, version}`; not once the patient is on board |
| GET | `/trips/log?from&to` | `ambulance.dispatch` | → `TripLog` (at most 92 days; default the last week) |

Not built: GPS tracking, the crew's patient care record, fuel and maintenance logs.

## Linen and laundry — `/api/v1/linen`
Linen is counted in pieces at three places: clean in the linen store, in use on each ward (against the ward's par level,
the clean pieces it should hold), and soiled at the laundry. Every movement is counted: new linen received into the
store, issued to a ward, sent back soiled by the ward, washed back to the store, condemned (torn, stained beyond washing
or worn out, from the store or the laundry), or lost at a ward's count. No place gives up more than it holds
(`linen.short`). Soiled linen from an isolation room or soaked in blood or body fluids is sent as infected and kept apart
at the laundry; it is washed only by thermal disinfection, and a batch counts as disinfected only at 71 °C held for 3
minutes or 65 °C for 10 (HTM 01-04); below that the batch is refused (`linen.not_disinfected`) to be washed again.
Each item has its expected life in washes; the washes so far per piece in circulation show how near the stock is to it.
A ward records soiled returns and losses; the rest is the linen store's and the laundry's.

| Method | Path | Permission | Request → Response |
|---|---|---|---|
| GET · POST | `/items` | `linen.ward` or `linen.manage` · `linen.manage` | → `LinenItemDto[]` (with average washes) / `SaveLinenItemRequest{id?, code, name, lifeWashes, isActive, version?}` |
| GET | `/wards` | `linen.ward` or `linen.manage` | → the facility's wards in use |
| GET | `/stock` | `linen.ward` or `linen.manage` | → `LinenStock` (each item at the store, wards, laundry and infected; each ward against its par) |
| POST | `/par` | `linen.manage` | `SetParRequest{wardId, lines: [{linenItemId, quantity}]}` |
| GET · POST | `/movements?from&to&kind` | `linen.ward` or `linen.manage` | → the period's movements (at most 92 days) / `RecordLinenRequest{kind, wardId?, infected, process?, temperatureC?, holdMinutes?, condemnReason?, from?, notes?, lines}`; a ward may record only `SoiledReturn` and `Lost` |

Not built: RFID tags on pieces, an outsourced laundry's challan, linen charges.

## Housekeeping — `/api/v1/housekeeping`
Beyond the bed turnaround (Inpatient's terminal cleaning of a bed after discharge, which keeps an unclean bed off the
admission desk's list), housekeeping cleans areas on a schedule and answers the wards.

**Areas on a schedule.** The quality manager sets up each area (code, name, the ward it belongs to, its risk zone: high
for theatres, ICUs and isolation, moderate for wards, low for corridors and offices), how often it is cleaned (daily, so
many times a day up to six, or weekly on set days) and its checklist. The day's board lists every cleaning due with
what was done about it; a cleaning is recorded against the area's checklist, every item answered and one not done saying
why, once per slot.

**Asked for by the wards.** A ward asks for a spill (blood or body fluid, chemical, mercury), a terminal cleaning (a room
or bed after discharge, death or isolation) or anything else. Housekeeping is told at once (a spill as critical); the
response time, from the request to someone on the way (or the cleaning done, if nobody said), is measured against the
kind's target: 15 minutes for a spill, an hour for a terminal cleaning, two hours for anything else, and a late one is
flagged while it waits and afterwards. Each kind is done against its own checklist (a spill: area cordoned, PPE, spill
kit, 1% sodium hypochlorite, waste in the yellow bag; a terminal cleaning: linen and curtains, surfaces top to bottom,
bed and mattress, equipment, floor, waste and sharps).

**Verified by someone else.** Every cleaning, scheduled or asked for, is verified by a supervisor (`housekeeping.verify`)
who did not do it (403 `housekeeping.verify_own`). One found not clean is sent back with what was not clean, the cleaner
is told, and it is done again and checked again.

| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET · POST · PUT | `/areas` · `/areas/{id}` | any housekeeping permission · `housekeeping.manage` | → `CleaningAreaDto[]` / `SaveCleaningAreaRequest{code, name, wardId?, riskZone, frequency, timesPerDay, weekDays?, checklist, isActive, version?}`; 409 `housekeeping.code_in_use` |
| GET | `/board?date` | `housekeeping.work`, `.verify` or `.manage` | → `CleaningBoard` (slots due with their latest record; due, not done, to verify, sent back) |
| POST | `/areas/{id}/cleanings` | `housekeeping.work` | `RecordCleaningRequest{date, slot, checklist: [{item, done, note?}], notes?}` (today, or yesterday after midnight); 409 `housekeeping.slot_done` |
| POST | `/cleanings/{id}/verify` | `housekeeping.verify` | `VerifyCleaningRequest{passed, remarks?, version}` (remarks required when not clean); 403 `housekeeping.verify_own` |
| GET · POST | `/requests?open&from&to` · `/requests` | any housekeeping permission · `housekeeping.request` or `.manage` | → open requests by kind, oldest first (or the period's) / `RaiseCleaningRequest{kind, wardId?, location, details?}` → 201 |
| POST | `/requests/{id}/acknowledge` · `/complete` · `/verify` · `/cancel` | `housekeeping.work` · `.work` · `.verify` · `.request` or `.manage` | `{version}` · `CompleteCleaningRequest{checklist, notes?, version}` · `VerifyCleaningRequest` · `CancelCleaningRequest{reason, version}` |
| GET | `/report?from&to` | `housekeeping.verify` or `.manage` | → `HousekeepingReport` (requests by kind: count, median response, late, sent back; scheduled cleanings due, done, verified, sent back) |

Not built: assigning a cleaner to an area or a shift, stock of cleaning materials, and swab cultures of surfaces.

## Integration events
| Event | Direction | Notes |
|---|---|---|
| `ServiceOrderPlaced` (Category `Dietary`, with `AdmissionId`) | handled | creates the diet order (service code `DIET-DIABETIC` or `DIABETIC` → diet type; otherwise NORMAL flagged for review), supersedes the current order, replans today; idempotent per order item |
| `ServiceOrderCancelled` | handled | stops the diet order created from that order item and cancels meals not yet served |
| `PatientDischarged` | handled | completes active diet orders and cancels meals served after the discharge time |
| `PatientsMerged` (Patients) | handled | re-points patient ids on diet orders, planned meals, pack issues and canteen bills |

No integration events are raised.

## Charges (via `IChargePoster`)
| SourceModule | SourceReference | When |
|---|---|---|
| `supportservices.ambulance` | `{tripId}:base`, `{tripId}:km` | when a trip for a registered patient is completed and not waived: the base fare for the vehicle's kind, and `AMB-KM` × kilometres beyond the first 10 |
| `supportservices.diet` | `{dietOrderId}:{yyyy-MM-dd}` | once per admission per day when facility setting `supportservices.diet.chargesEnabled` is true and the diet type has a charge service; a day already charged under an earlier order of the same admission is skipped, also when that order was superseded the same day (checked with `IBillingStatusProvider.GetStatesAsync`). Posted and saved when the day's plan is generated (`DailyMealPlanJob` or `POST /meal-plans/generate`) |

## Settings, numbers, notifications, jobs, seed
* Settings: `supportservices.diet.chargesEnabled` (bool, default false; explained on the staff app's Settings page), `supportservices.canteen.staffCreditLimit` (decimal, default 3000; 0 = no limit), `bmw.authorisationNumber`, `bmw.cbwtf`, `bmw.hcfId` (the HCF identifier on every bag's label), `ambulance.waiverApproval` (bool, on unless false; switching it off waits for a second administrator).
* Number series (yearly): `cssd-wash` → `{FAC}CW{YY}{SEQ:6}`, `cssd-cycle` → `{FAC}CS{YY}{SEQ:6}`, `cssd-request` → `{FAC}CQ{YY}{SEQ:6}`, `canteen-bill` → `{FAC}CN{YY}{SEQ:7}`, `mortuary-body` → `{FAC}MB{YY}{SEQ:5}`, `ambulance-trip` → `{FAC}AT{YY}{SEQ:6}`, `linen-movement` → `{FAC}LN{YY}{SEQ:6}`.
* Notifications: in-app only — CSSD recall (critical) and count discrepancy (warning) to `cssd.recalls`; urgent request (warning) to `cssd.process`.
* Job: `DailyMealPlanJob` — at start-up and hourly when `PlatformOptions.RunsJobs`: generates/refreshes today's and tomorrow's plans for every facility and posts today's diet charges when enabled.
* Seed `SupportServicesSeeder` (Order 170, every installation, idempotent by code): service items `DIET-STD` (Rs. 300/day), `DIET-THER` (Rs. 450/day), `DIET-ENT` (Rs. 400/day), and the ambulance fares `AMB-BLS` (Rs. 1,000), `AMB-ALS` (Rs. 2,500), `AMB-PTS` (Rs. 800), `AMB-HEARSE` (Rs. 1,200) and `AMB-KM` (Rs. 25 a km), SAC 999315; 13 diet types (NORMAL, SOFT, SEMI-SOLID, LIQUID, CLEAR-LIQUID, DIABETIC, RENAL, CARDIAC, HIGH-PROTEIN, LOW-FAT, BLAND, RT-FEED, NPO); a linen list for the default facility (bed sheet, draw sheet, pillow cover, patient gown, OT gown, towel, blanket, with their expected washes); 8 instrument sets for the default facility (MAJOR-TRAY, MINOR-TRAY, LSCS-SET, DELIVERY-SET, DRESSING-SET, SUTURE-SET, CATH-SET, LAPARO-SET); a 28-item canteen menu (idli, dosa, meals, thali, biryani, tea, coffee, snacks, juices, sweets, packaged water and biscuits with tracked stock; HSN 220190 for water, 190531 for biscuits and 080119 for tender coconut, SAC 996333 for everything made in the canteen).

## Tables (schema `supportservices`)

Bio-medical waste: `waste_bags` (unique `barcode`; `handover_weight_kg`), `waste_handovers` (unique `number`;
`mismatched_bags`), `waste_accidents`.

Housekeeping: `cleaning_areas` (unique `code` per facility), `cleaning_records` (one standing cleaning per area, day and
slot; one sent back makes way for the next), `cleaning_requests`.

Canteen bills keep `cancel_requested_by(_name)`, `cancelled_by_name`, `refund_mode` and `refund_amount`, and
`approval_code`, `card_last4` and `terminal_sale_id` when paid on the terminal; ambulance trips keep
`charge_waived_by(_name)`, `charge_waived_at` and `waiver_requested_by_name`.

Ambulance: `ambulance_vehicles` (unique registration), `ambulance_trips` (unique `number` per facility; one open trip per vehicle).

Linen: `linen_items` (unique `code` per facility), `linen_holdings` (one count per item at each place; never below 0),
`linen_movements` (unique `number` per facility), `linen_movement_lines`.

Mortuary: `mortuary_slots` (unique `code` per facility), `body_records` (unique `number` per facility; one body in
storage per slot).

`instrument_sets`, `instrument_set_items`, `cssd_requests`, `cssd_request_lines`, `soiled_receipts`,
`soiled_receipt_lines`, `wash_cycles`, `wash_cycle_trays`, `sterilization_loads`, `sterile_packs` (with
`wash_cycle_id`), `set_issues`, `diet_types`, `diet_orders`,
`planned_meals`, `menu_items`, `canteen_stock_movements`, `canteen_sales`, `canteen_sale_lines`, `staff_credit_entries`.
