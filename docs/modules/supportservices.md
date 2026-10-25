# Support Services module (`supportservices`)

CSSD (sterile instrument supply with recall by load), dietary (diet orders, ward-wise meal plans, kitchen production,
delivery tracking, optional diet charges) and the canteen (POS with GST, staff credit with payroll deduction).
SRS **CSSD-01..05**, **DIET-01..05**, **CAN-01..04**.

Project: `src/server/Modules/HyFib.HealthCare360.Modules.SupportServices` (references Patients and Inpatient — layer 6 → 5,
for the admission's current ward and bed). Schema `supportservices`.
Tests: `tests/Modules/HyFib.HealthCare360.Modules.SupportServices.Tests` (167 unit tests), and the end-to-end `DietChargeTests`
in `tests/HyFib.HealthCare360.IntegrationTests` (diet charges on the IP bill with the setting on, and none with it off).

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
| `canteen.sales.cancel` | Cancel today's bills | CanteenManager, HospitalAdmin |
| `canteen.menu.manage` | Menu, prices, stock | CanteenManager, HospitalAdmin |
| `canteen.reports.read` | Sales reports | CanteenManager, HospitalAdmin, Accountant |
| `canteen.credit.manage` | Staff credit balances, statements, payroll export and settlement | CanteenManager, HrManager, HospitalAdmin |
| `bmw.record` | Weigh and bar-code bags of bio-medical waste where it is generated | Nurse, LabTechnician, CssdTechnician, OtCoordinator, QualityManager, HospitalAdmin |
| `bmw.manage` | Hand waste over to the treatment facility, the register and the annual report (Form IV) | QualityManager, HospitalAdmin |
| `mortuary.manage` | Receive and release bodies, the mortuary register and the release slip | Nurse, HospitalAdmin |
| `mortuary.configure` | Set up the cold-storage slots | HospitalAdmin |
| `ambulance.dispatch` | Take ambulance calls, send vehicles and record each trip; the trip log | FrontOffice, Nurse, HospitalAdmin |
| `ambulance.fleet` | Keep the ambulance fleet and its certificates | HospitalAdmin |

## CSSD — `/api/v1/cssd`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/sets?includeInactive&search` | `cssd.read` or `cssd.requests` | → `InstrumentSetDto[]` (method, shelfLifeDays, count sheet `items`, instrumentCount, packsAvailable) |
| GET | `/sets/{id}` | same | → `InstrumentSetDto` |
| POST | `/sets` | `cssd.masters.manage` | `SaveInstrumentSetRequest` (code, name, category, ownerDepartmentId, method Steam/EthyleneOxide/Plasma, shelfLifeDays, items[name, quantity]) → 201 |
| PUT | `/sets/{id}` | `cssd.masters.manage` | `SaveInstrumentSetRequest` (+`version`) → 204 |
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
| GET · POST | `/handovers` | `bmw.manage` | → the last week's `WasteHandoverDto[]` / `HandOverWasteRequest{barcodes, cbwtf?, vehicleNumber, receivedBy, receiptNumber?}`: every bag must be waiting; the handover counts those kept beyond 48 hours |
| GET | `/register?year&month` · `/register/export` | `bmw.manage` | → `WasteMonth` / CSV |
| GET | `/annual?year` | `bmw.manage` | → `WasteYear` (Form IV totals by colour, average a day, handovers, late and voided bags) |

Not built: printing the bar-code label (the number is shown to write or print on the bag), liquid waste and the Form I
accident report.

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

| Method | Path | Permission | Request → Response |
|---|---|---|---|
| GET | `/slots` | `mortuary.manage` or `mortuary.configure` | → `MortuarySlotDto[]` (who is in each, `unclaimed`) |
| PUT | `/slots` | `mortuary.configure` | `SaveSlotsRequest{slots: [{id?, code, chamber?, isActive, version?}]}` (new slots without an id; changed ones with their version); 422 `mortuary.slot_occupied` |
| GET | `/bodies?status&from&to` | `mortuary.manage` | → `BodyDto[]`: in storage, or received in the period (at most a year; default the last 30 days) |
| POST | `/bodies` | `mortuary.manage` | `ReceiveBodyRequest{source, patientId?, name?, identified, sex?, ageYears?, diedAt?, broughtBy?, isMlc, mlcNumber?, policeStation?, identificationMarks?, belongings?, slotId}`; 422 `mortuary.death_not_recorded`, 409 `mortuary.slot_occupied` / `mortuary.already_in` |
| GET | `/bodies/{id}` | `mortuary.manage` | → `BodyDto` (a patient's body through the patient access guard) |
| POST | `/bodies/{id}/release` | `mortuary.manage` | `ReleaseBodyRequest{to, receivedBy, relationship?, idProof?, policeClearance?, belongingsHandedOver, notes?, version}`; 422 `mortuary.released`, `mortuary.unidentified`, `mortuary.mlc_donation` |
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
km, and `AMB-KM` each kilometre beyond; a trip can be left uncharged with a reason (a free scheme transfer, say). The
trip log gives the period's trips, kilometres, and the median call-to-dispatch and call-to-patient times of emergencies.

| Method | Path | Permission | Request → Response |
|---|---|---|---|
| GET · POST | `/vehicles` | `ambulance.dispatch` or `ambulance.fleet` · `ambulance.fleet` | → `AmbulanceVehicleDto[]` (with `lapsed` certificates and the trip it is out on) / `SaveVehicleRequest{id?, registrationNumber, kind, fitnessValidUntil?, insuranceValidUntil?, pollutionValidUntil?, odometerKm, isActive, version?}`; 409 `ambulance.duplicate` |
| GET · POST | `/trips` | `ambulance.dispatch` | → open trips, oldest call first / `RequestTripRequest{purpose, patientId?, patientName?, callerName, callerPhone, pickupAddress, dropAddress?, complaint?}` |
| POST | `/trips/{id}/dispatch` | `ambulance.dispatch` | `DispatchRequest{vehicleId, driverName, crewName?, version}`; 422 `ambulance.papers_lapsed`, 409 `ambulance.vehicle_busy` |
| POST | `/trips/{id}/at-scene` · `/left-scene` | `ambulance.dispatch` | `TripStepRequest{version}`; 422 `ambulance.out_of_order` |
| POST | `/trips/{id}/complete` | `ambulance.dispatch` | `CompleteTripRequest{endOdometerKm, waiveReason?, version}`: posts the charges; 422 `ambulance.no_tariff` when a fare is missing from the catalogue |
| POST | `/trips/{id}/cancel` | `ambulance.dispatch` | `CancelTripRequest{reason, version}`; not once the patient is on board |
| GET | `/trips/log?from&to` | `ambulance.dispatch` | → `TripLog` (at most 92 days; default the last week) |

Not built: GPS tracking, the crew's patient care record, fuel and maintenance logs.

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
* Settings: `supportservices.diet.chargesEnabled` (bool, default false; explained on the staff app's Settings page), `supportservices.canteen.staffCreditLimit` (decimal, default 3000; 0 = no limit).
* Number series (yearly): `cssd-wash` → `{FAC}CW{YY}{SEQ:6}`, `cssd-cycle` → `{FAC}CS{YY}{SEQ:6}`, `cssd-request` → `{FAC}CQ{YY}{SEQ:6}`, `canteen-bill` → `{FAC}CN{YY}{SEQ:7}`, `mortuary-body` → `{FAC}MB{YY}{SEQ:5}`, `ambulance-trip` → `{FAC}AT{YY}{SEQ:6}`.
* Notifications: in-app only — CSSD recall (critical) and count discrepancy (warning) to `cssd.recalls`; urgent request (warning) to `cssd.process`.
* Job: `DailyMealPlanJob` — at start-up and hourly when `PlatformOptions.RunsJobs`: generates/refreshes today's and tomorrow's plans for every facility and posts today's diet charges when enabled.
* Seed `SupportServicesSeeder` (Order 170, every installation, idempotent by code): service items `DIET-STD` (Rs. 300/day), `DIET-THER` (Rs. 450/day), `DIET-ENT` (Rs. 400/day), and the ambulance fares `AMB-BLS` (Rs. 1,000), `AMB-ALS` (Rs. 2,500), `AMB-PTS` (Rs. 800), `AMB-HEARSE` (Rs. 1,200) and `AMB-KM` (Rs. 25 a km), SAC 999315; 13 diet types (NORMAL, SOFT, SEMI-SOLID, LIQUID, CLEAR-LIQUID, DIABETIC, RENAL, CARDIAC, HIGH-PROTEIN, LOW-FAT, BLAND, RT-FEED, NPO); 8 instrument sets for the default facility (MAJOR-TRAY, MINOR-TRAY, LSCS-SET, DELIVERY-SET, DRESSING-SET, SUTURE-SET, CATH-SET, LAPARO-SET); a 28-item canteen menu (idli, dosa, meals, thali, biryani, tea, coffee, snacks, juices, sweets, packaged water and biscuits with tracked stock; HSN 220190 for water, 190531 for biscuits and 080119 for tender coconut, SAC 996333 for everything made in the canteen).

## Tables (schema `supportservices`)

Bio-medical waste: `waste_bags` (unique `barcode`), `waste_handovers` (unique `number`).

Ambulance: `ambulance_vehicles` (unique registration), `ambulance_trips` (unique `number` per facility; one open trip per vehicle).

Mortuary: `mortuary_slots` (unique `code` per facility), `body_records` (unique `number` per facility; one body in
storage per slot).

`instrument_sets`, `instrument_set_items`, `cssd_requests`, `cssd_request_lines`, `soiled_receipts`,
`soiled_receipt_lines`, `wash_cycles`, `wash_cycle_trays`, `sterilization_loads`, `sterile_packs` (with
`wash_cycle_id`), `set_issues`, `diet_types`, `diet_orders`,
`planned_meals`, `menu_items`, `canteen_stock_movements`, `canteen_sales`, `canteen_sale_lines`, `staff_credit_entries`.
