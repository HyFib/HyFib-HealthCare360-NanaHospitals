# Support Services module (`supportservices`)

CSSD (sterile instrument supply with recall by load), dietary (diet orders, ward-wise meal plans, kitchen production,
delivery tracking, optional diet charges) and the canteen (POS with GST, staff credit with payroll deduction).
SRS **CSSD-01..05**, **DIET-01..05**, **CAN-01..04**.

Project: `src/server/Modules/HyFib.HealthCare360.Modules.SupportServices` (references Patients and Inpatient — layer 6 → 5,
for the admission's current ward and bed). Schema `supportservices`.
Tests: `tests/Modules/HyFib.HealthCare360.Modules.SupportServices.Tests` (101 unit tests).

## Permissions

| Permission | Purpose | Default roles |
|---|---|---|
| `cssd.read` | View sets, loads, packs, issues, reports | CssdTechnician, Nurse, OtCoordinator, QualityManager, HospitalAdmin |
| `cssd.requests` | Request sterile sets; record packs used on patients | Nurse, OtCoordinator, CssdTechnician, HospitalAdmin |
| `cssd.process` | Soiled receipts, loads, issues | CssdTechnician, HospitalAdmin |
| `cssd.release` | Release loads, record biological indicators (triggers recalls) | CssdTechnician, HospitalAdmin |
| `cssd.recalls` | Receive recall/discrepancy alerts; acknowledge recalled packs | CssdTechnician, OtCoordinator, Nurse, QualityManager, HospitalAdmin |
| `cssd.masters.manage` | Instrument sets and count sheets | CssdTechnician, HospitalAdmin |
| `dietary.orders.read` | Diet orders and ward meal plans | Dietitian, Nurse, Doctor, CanteenManager, HospitalAdmin, Billing |
| `dietary.orders.manage` | Write, stop, review diet orders | Doctor, Dietitian, Nurse |
| `dietary.meals.manage` | Generate plans, production list, record deliveries | Dietitian, CanteenManager, Nurse, HospitalAdmin |
| `dietary.masters.manage` | Diet master | Dietitian, HospitalAdmin |
| `canteen.sales.create` | POS billing, staff credit repayments | CanteenManager |
| `canteen.sales.cancel` | Cancel today's bills | CanteenManager, HospitalAdmin |
| `canteen.menu.manage` | Menu, prices, stock | CanteenManager, HospitalAdmin |
| `canteen.reports.read` | Sales reports | CanteenManager, HospitalAdmin, Accountant |
| `canteen.credit.manage` | Staff credit balances, statements, payroll export and settlement | CanteenManager, HrManager, HospitalAdmin |

## CSSD — `/api/v1/cssd`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/sets?includeInactive&search` | `cssd.read` or `cssd.requests` | → `InstrumentSetDto[]` (method, shelfLifeDays, count sheet `items`, instrumentCount, packsAvailable) |
| GET | `/sets/{id}` | same | → `InstrumentSetDto` |
| POST | `/sets` | `cssd.masters.manage` | `SaveInstrumentSetRequest` (code, name, category, ownerDepartmentId, method Steam/EthyleneOxide/Plasma, shelfLifeDays, items[name, quantity]) → 201 |
| PUT | `/sets/{id}` | `cssd.masters.manage` | `SaveInstrumentSetRequest` (+`version`) → 204 |
| GET | `/requests?status&departmentId&openOnly` | `cssd.read` or `cssd.requests` | → `CssdRequestDto[]` (urgent first) |
| POST | `/requests` | `cssd.requests` | `CreateCssdRequestRequest` (departmentId, requiredBy, priority, lines[setId, quantity]) → 201 `CssdRequestDto` (urgent requests alert CSSD) |
| POST | `/requests/{id}/cancel` | `cssd.requests` | `CancelCssdRequestRequest` → 204 |
| GET | `/receipts?from&to&discrepanciesOnly` | `cssd.read` | → `SoiledReceiptDto[]` |
| POST | `/receipts` | `cssd.process` | `ReceiveSoiledRequest` (departmentId, lines[setId, setIssueId?, counts[itemName, received, damaged]]) → 201 `SoiledReceiptDto` (count check; discrepancies alert `cssd.recalls` holders) |
| GET | `/packs?setId&status&expiringWithinDays` | `cssd.read` or `cssd.process` | → `SterilePackDto[]` (available by default, soonest expiry first) |
| GET | `/issues?departmentId&loadId&openRecalls` (paged) | `cssd.read` or `cssd.recalls` | → `PagedResult<SetIssueDto>` |
| POST | `/issues` | `cssd.process` | `IssuePacksRequest` (departmentId, cssdRequestId?, issuedToName, packIds ≤ 100) → `SetIssueDto[]` (expired/unreleased/recalled packs refused; 409 if issued concurrently) |
| POST | `/issues/{id}/use` | `cssd.requests` or `cssd.process` | `RecordPackUseRequest` (patientId, usedAt) → 204 (traceability) |
| POST | `/issues/{id}/acknowledge-recall` | `cssd.recalls` | `AcknowledgeRecallRequest` (action) → 204 |
| GET | `/reports/summary?from&to` | `cssd.read` | → `CssdSummary` (loads, released, failed, failure rate, BI pending, by method, packs issued, open requests, discrepancy receipts, open recalls, stock by set, expiring ≤ 2 days) |

### Sterilizer loads — `/api/v1/cssd/loads`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/?status&method&biologicalPending&from&to&search` (paged) | `cssd.read` or `cssd.process` | → `PagedResult<LoadListItem>` |
| GET | `/{id}` | same | → `LoadDto` (parameters, indicators, packs with expiry) |
| GET | `/{id}/recall` | `cssd.read` or `cssd.recalls` | → `RecallReport` (every issue of the load with department, status, patient, acknowledgement) |
| POST | `/` | `cssd.process` | `CreateLoadRequest` (sterilizerName, method, program, containsImplants, operatorName, packs[setId, quantity] ≤ 200; sets must match the method) → 201 `LoadDto`; packs numbered `{cycle}-001…` |
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
| GET | `/meal-plans/production?date` | `dietary.meals.manage` | → `ProductionSummary` (per round and diet: veg/non-veg/egg/Jain counts, special notes) |
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
| POST | `/sales` | `canteen.sales.create` | `CreateSaleRequest` (customerType Staff/Attendant/Visitor/Patient, customerName, employeeCode + name (StaffCredit), patientId, paymentMode Cash/Upi/Card/StaffCredit, paymentReference, lines[menuItemId, quantity]) → 201 `CanteenSaleDto` (bill number, taxable, CGST, SGST, gross, round-off, net, lines with HSN/SAC code and GST rate) |
| GET | `/sales?date&status&paymentMode&employeeCode&search` (paged) | sales.create or reports.read | → `PagedResult<CanteenSaleListItem>` |
| GET | `/sales/{id}` | same | → `CanteenSaleDto` |
| POST | `/sales/{id}/cancel` | `canteen.sales.cancel` | `CancelSaleRequest` → `CanteenSaleDto` (same business day only; stock restored; staff credit reversed) |
| GET | `/reports/daily?date` | `canteen.reports.read` | → `DailySalesSummary` (bills, cancelled, taxable, CGST, SGST, round-off, net; by payment mode, customer type, category; item consumption and closing stock; `byHsnSac`: quantity, taxable, CGST, SGST and total per HSN/SAC code and GST rate, for the GSTR-1 HSN summary) |

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
| `supportservices.diet` | `{dietOrderId}:{yyyy-MM-dd}` | once per admission per day when facility setting `supportservices.diet.chargesEnabled` is true and the diet type has a charge service; a day already charged under an earlier order of the same admission is skipped (checked with `IBillingStatusProvider.GetStatesAsync`) |

## Settings, numbers, notifications, jobs, seed
* Settings: `supportservices.diet.chargesEnabled` (bool, default false), `supportservices.canteen.staffCreditLimit` (decimal, default 3000; 0 = no limit).
* Number series (yearly): `cssd-cycle` → `{FAC}CS{YY}{SEQ:6}`, `cssd-request` → `{FAC}CQ{YY}{SEQ:6}`, `canteen-bill` → `{FAC}CN{YY}{SEQ:7}`.
* Notifications: in-app only — CSSD recall (critical) and count discrepancy (warning) to `cssd.recalls`; urgent request (warning) to `cssd.process`.
* Job: `DailyMealPlanJob` — at start-up and hourly when `PlatformOptions.RunsJobs`: generates/refreshes today's and tomorrow's plans for every facility and posts today's diet charges when enabled.
* Seed `SupportServicesSeeder` (Order 170, every installation, idempotent by code): service items `DIET-STD` (Rs. 300/day), `DIET-THER` (Rs. 450/day), `DIET-ENT` (Rs. 400/day); 13 diet types (NORMAL, SOFT, SEMI-SOLID, LIQUID, CLEAR-LIQUID, DIABETIC, RENAL, CARDIAC, HIGH-PROTEIN, LOW-FAT, BLAND, RT-FEED, NPO); 8 instrument sets for the default facility (MAJOR-TRAY, MINOR-TRAY, LSCS-SET, DELIVERY-SET, DRESSING-SET, SUTURE-SET, CATH-SET, LAPARO-SET); a 28-item canteen menu (idli, dosa, meals, thali, biryani, tea, coffee, snacks, juices, sweets, packaged water and biscuits with tracked stock; HSN 220190 for water, 190531 for biscuits and 080119 for tender coconut, SAC 996333 for everything made in the canteen).

## Tables (schema `supportservices`)
`instrument_sets`, `instrument_set_items`, `cssd_requests`, `cssd_request_lines`, `soiled_receipts`,
`soiled_receipt_lines`, `sterilization_loads`, `sterile_packs`, `set_issues`, `diet_types`, `diet_orders`,
`planned_meals`, `menu_items`, `canteen_stock_movements`, `canteen_sales`, `canteen_sale_lines`, `staff_credit_entries`.
