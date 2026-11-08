# SupplyChain module (`supplychain`) — inventory, purchase and assets

Item master, stores and vendors; batch-wise stock with an exact running-balance ledger; purchase (requisition →
order → goods receipt); indents, inter-store transfers, adjustments and physical counts; inventory reports
(near-expiry, expired, reorder, valuation, ABC/VED/FSN); and biomedical asset management (register, AMC/CMC
contracts, preventive maintenance, breakdown tickets, calibration, depreciation, condemnation and disposal).
SRS **INV-01..08**, **AST-01..06**.

Project: `src/server/Modules/HyFib.HealthCare360.Modules.SupplyChain`. Schema `supplychain`.
Tests: `tests/Modules/HyFib.HealthCare360.Modules.SupplyChain.Tests` (144 unit and EF-model tests, no database needed),
and the end-to-end `PharmacyStockReportTests` in `tests/HyFib.HealthCare360.IntegrationTests` (PH-04: a pharmacy
store's stock, reorder, near-expiry and expired reports on goods received with chosen batches and expiry dates).

India conventions: amounts in INR. Purchase GST is exclusive and split CGST + SGST within a state, or IGST when
the vendor's GSTIN state differs from the facility's. MRP is GST-inclusive. HSN codes are 6-digit headings. Dates
are local (IST) calendar dates through `HospitalClock` / `TimeProvider`.

## Permissions

| Permission | Purpose | Default roles |
|---|---|---|
| `inventory.items.manage` | Item master, categories, stores; block/unblock batches | StoreKeeper, HospitalAdmin, PurchaseOfficer |
| `inventory.stock.read` | Stock, batches, stock ledger, inventory reports | StoreKeeper, HospitalAdmin, PurchaseOfficer, Pharmacist |
| `inventory.indents.raise` | Raise (and cancel own) indents | StoreKeeper, HospitalAdmin, Nurse, Pharmacist |
| `inventory.indents.issue` | Approve (reserve), issue, reject and close indents | StoreKeeper, HospitalAdmin |
| `inventory.transfers` | Dispatch, receive and cancel transfers | StoreKeeper, HospitalAdmin, Pharmacist |
| `inventory.adjustments.approve` | Approve/reject adjustments and write-offs (never your own) | HospitalAdmin |
| `inventory.counts` | Physical stock counts; raise adjustments | StoreKeeper, HospitalAdmin |
| `purchase.manage` | Vendors, requisitions, purchase orders | StoreKeeper, HospitalAdmin, PurchaseOfficer |
| `purchase.approve` | Approve/reject requisitions and orders | HospitalAdmin |
| `purchase.grn.receive` | Goods receipt against vendor invoices; send a receipt for approval | StoreKeeper, HospitalAdmin |
| `purchase.grn.approve` | Check a goods receipt against the supplier's invoice and post it into stock (never one's own) | HospitalAdmin, PurchaseOfficer |
| `inventory.cold_chain.record` | Record refrigerator and freezer temperatures (the twice-daily register) and the action taken on an excursion | StoreKeeper, HospitalAdmin, Pharmacist, Nurse |
| `inventory.cold_chain.manage` | Set up refrigerators and freezers, their allowed range and their data loggers' keys | StoreKeeper, HospitalAdmin, Pharmacist |
| `masters.prices.approve` (Platform's) | Approve an item's NPPA ceiling price raised or removed, or its GST rate changed, asked for by someone else | Accountant, HospitalAdmin |
| `assets.read` | Asset register; report equipment breakdowns | BiomedicalEngineer, HospitalAdmin, StoreKeeper, PurchaseOfficer, Nurse |
| `assets.manage` | Register assets and contracts; decide condemnation; dispose | BiomedicalEngineer, HospitalAdmin |
| `assets.maintenance` | PM plans/visits, tickets, calibration; daily reminder | BiomedicalEngineer, HospitalAdmin |

Reader sets used below:
* **Catalog readers**: any of `inventory.items.manage`, `inventory.stock.read`, `inventory.indents.raise`,
  `inventory.indents.issue`, `inventory.transfers`, `inventory.counts`, `purchase.manage`, `purchase.grn.receive`.
* **Batch readers**: any of `inventory.stock.read`, `inventory.indents.issue`, `inventory.transfers`,
  `inventory.counts`, `purchase.grn.receive`, `inventory.adjustments.approve`.
* **Vendor readers**: any of `purchase.manage`, `purchase.approve`, `purchase.grn.receive`, `purchase.grn.approve`,
  `assets.manage`, `assets.maintenance`.
* **Asset readers**: any of `assets.read`, `assets.manage`, `assets.maintenance`.
* **Cold-chain readers**: any of `inventory.cold_chain.record`, `inventory.cold_chain.manage`, `inventory.stock.read`.

## Endpoints

`page` is the platform paging query (`PageQuery`). Masters take a `version` for optimistic concurrency on update.
Failures return problem details that carry the error `code`: 400 validation, 404, 409 conflict (a stale
`version`, `stock.concurrent_update`), 422 business rule.

### Item master, categories and stores — `/api/v1/inventory` (tag Inventory)
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/categories?activeOnly` | Catalog readers | → `ItemCategoryDto[]` |
| POST · PUT | `/categories` · `/categories/{id}` | `inventory.items.manage` | `SaveItemCategoryRequest` (code, name, defaultType, description, isActive, version) → 201 `{id}` · 204 |
| GET | `/items?page&type&categoryId&schedule&controlledOnly&includeInactive` | Catalog readers | → paged `ItemListItem` (trigram name search; sort code/name/genericName/type/gstRate) |
| GET | `/items/lookup?search&storeId&type&take` | Catalog readers | → `ItemLookup[]` (≤ 50, with available stock, MRP and nearest expiry in `storeId`) |
| GET | `/items/by-code/{code}` · `/items/{id}` | Catalog readers | → `ItemDetail` |
| POST · PUT | `/items` · `/items/{id}` | `inventory.items.manage` | `SaveItemRequest` (code, name, genericName, brandName, type, categoryId, strength, dosageForm, packSize, baseUnit, purchaseUnit, purchaseUnitFactor, hsnCode, gstRate ∈ {0, 0.25, 3, 5, 12, 18, 28, 40}, schedule None/Otc/G/H/H1/X, isControlled, isHighAlert, isColdChain, vedClass, reorder/min/max levels, manufacturer (or importer), barcode, isActive, version, specification, ceilingPrice, ceilingNotification, classChangeReason, priceChangeReason) → 201 `{id}` · 204 · 202 `ApprovalRequestDto` (a looser class or pricing waits, below). `specification` says what a device is, as its bill prints it (for a stent, bare-metal or drug-eluting first, then material and size). `ceilingPrice` is the NPPA ceiling price of a base unit, GST included, on a scheduled medicine (DPCO 2013) or a price-controlled implant (a coronary stent, a knee implant); nothing else carries one, and it is more than 0 (`ceilingPrice`, 400). `ceilingNotification` is the NPPA notification that fixed it (S.O. number and date). An implant is charged no more than its ceiling, whatever an older pack's MRP: the OT and the IP pharmacy price within it, and Billing refuses more. A medicine is not received or sold above it (*Price ceilings*, below). NPPA revises it, so the store updates it |
| GET | `/stores?type&includeInactive` | Catalog readers or `inventory.adjustments.approve` | → `StoreDto[]` (central, pharmacy, ward, OT, lab, canteen, CSSD) |
| GET | `/stores/{id}` | Catalog readers | → `StoreDto` |
| POST · PUT | `/stores` · `/stores/{id}` | `inventory.items.manage` | `SaveStoreRequest` (code, name, type, departmentId, inChargeUserId, location, isActive, version) → 201 `{id}` · 204 |

### Stock — `/api/v1/inventory/stock`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/?storeId&page&type&belowReorder` | `inventory.stock.read` | → paged `StockSummaryRow` (on hand, reserved, available, expired, nearest expiry, value at cost, below-reorder flag, blocked quantity, on order). Available is usable stock: not reserved, not expired, not in a blocked batch. The flag is the reorder report's rule (`ReorderCalculator.IsDue`): active, with a level, and usable stock plus what is still due on the store's approved, sent or part-received purchase orders at or under the level. Filtered with `belowReorder=true`, an item with no stock left in the store is listed too, as the report lists it |
| GET | `/batches?storeId&itemId&includeEmpty` | Batch readers | → `BatchStock[]` in FEFO order |
| GET | `/ledger?page&storeId&itemId&from&to&movement` | `inventory.stock.read` | → paged `LedgerRow` (stock card with the running balance; IST dates) |
| GET | `/reservations?storeId&itemId` | Batch readers | → `ReservationRow[]` |
| POST | `/batches/{id}/block` · `/batches/{id}/unblock` | `inventory.items.manage` or `inventory.adjustments.approve` | `BlockBatchRequest` (reason) → 204. Quality hold or recall: never allocated while blocked |

### Reports — `/api/v1/inventory/reports`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/summary?storeId` | `inventory.stock.read` | → `InventorySummary` (store dashboard) |
| GET | `/near-expiry?storeId&days` | `inventory.stock.read` | → `ExpiryReport`: batches holding stock that expire from today to the `days`-th day (default 90, 1–730), soonest first, with days to expiry and value at cost and at MRP |
| GET | `/expired?storeId` | `inventory.stock.read` | → `ExpiryReport`: batches holding stock whose expiry day has passed (a batch is usable on its expiry day) |
| GET | `/reorder?storeId` | `inventory.stock.read` or `purchase.manage` | → `ReorderRow[]`: every active item with a reorder level whose usable stock (unreserved, in batches neither blocked nor expired) plus what is still to come on approved, sent or part-received orders is at or under that level. The suggestion tops up to the maximum stock (with no maximum, to the reorder level plus the reorder quantity), never less than the reorder quantity, net of what is on order, in purchase units rounded up; vital items first, then essential, then desirable, each by name (`ReorderCalculator`, unit-tested) |
| GET | `/valuation?storeId` | `inventory.stock.read` | → `ValuationReport` (at cost and at MRP, by store and item type) |
| GET | `/analysis?from&to&storeId` | `inventory.stock.read` or `purchase.manage` | → `InventoryAnalysis`: ABC (70/90 cumulative value), FSN (issue frequency), VED and the ABC-VED matrix (I: AV, AE, AD, BV, CV; II: BE, CE, BD; III: CD). Default is the last 12 months, max 3 years |

### Indents — `/api/v1/inventory/indents` (INV-05)
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/?page&status&fromStoreId&toStoreId&departmentId&openOnly` | any of `inventory.indents.raise`, `inventory.indents.issue`, `inventory.stock.read` | → paged `IndentListItem` (urgent first, then oldest) |
| GET | `/{id}` · `/{id}/issues` | same | → `IndentDetail` · `IndentIssueDto[]` |
| POST | `/` | `inventory.indents.raise` | `CreateIndentRequest` (fromStoreId, toStoreId (sub-store) or departmentId, priority Routine/Urgent, remarks, lines[itemId, quantity, remarks]) → 201 `DocumentCreated` |
| POST | `/{id}/approve` | `inventory.indents.issue` | `ApproveIndentRequest` (lines[lineId, approvedQuantity]) → 204; reserves stock FEFO |
| POST | `/{id}/issue` | `inventory.indents.issue` | `IssueIndentRequest` (lines[lineId, quantity, batchId?], receivedByName, remarks) → 200 `DocumentCreated` (issue slip) |
| POST | `/{id}/reject` · `/{id}/close` | `inventory.indents.issue` | `InventoryReasonRequest` → 204 (releases reservations) |
| POST | `/{id}/cancel` | `inventory.indents.raise` or `inventory.indents.issue` | `InventoryReasonRequest` → 204 |

### Transfers — `/api/v1/inventory/transfers` (INV-06, PH-04)
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/?page&status&storeId` · `/{id}` | `inventory.transfers` or `inventory.stock.read` | → paged `TransferListItem` · `TransferDetail` |
| POST | `/` | `inventory.transfers` | `DispatchTransferRequest` (fromStoreId, toStoreId, remarks, lines[itemId, quantity, batchId?]) → 201 `DocumentCreated`; stock leaves the source (in transit) |
| POST | `/{id}/receive` | `inventory.transfers` | → 204; same batch, expiry and MRP booked in at the destination |
| POST | `/{id}/cancel` | `inventory.transfers` | `InventoryReasonRequest` → 204; returns stock to the exact source batches |

### Adjustments and physical counts — `/api/v1/inventory/adjustments`, `/api/v1/inventory/counts` (INV-07)
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/adjustments?page&status&storeId&reason` · `/adjustments/{id}` | any of `inventory.counts`, `inventory.adjustments.approve`, `inventory.stock.read` | → paged `AdjustmentListItem` · `AdjustmentDetail` |
| POST | `/adjustments` | `inventory.counts` or `inventory.adjustments.approve` | `CreateAdjustmentRequest` (storeId, reason, remarks, lines[batchId, quantityChange ±]) → 201 `DocumentCreated` |
| POST | `/adjustments/expired-write-off?storeId` | same | → 201 `DocumentCreated` (every expired batch in the store) |
| POST | `/adjustments/{id}/approve` · `/reject` | `inventory.adjustments.approve` | — · `InventoryReasonRequest` → 204. Self-approval is refused |
| GET | `/counts?page&storeId&status` · `/counts/{id}` | as adjustments | → paged `CountListItem` · `CountDetail` |
| POST | `/counts` | `inventory.counts` | `StartCountRequest` (storeId, itemType?, categoryId?, remarks) → 201 `DocumentCreated` (freezes the book quantities) |
| PUT | `/counts/{id}/lines` | `inventory.counts` | `RecordCountsRequest` (lines) → 204 |
| POST | `/counts/{id}/submit` · `/cancel` | `inventory.counts` | → 200 `{adjustmentId}` (variance adjustment awaiting approval; null when nothing differs) · 204 |

### Vendors, requisitions and purchase orders — `/api/v1/purchase` (INV-03, tag Purchase)
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/vendors?page&includeInactive` · `/vendors/{id}` | Vendor readers | → paged `VendorListItem` · `VendorDetail` (PAN is `[SensitiveData]`: never written to logs or audit diffs) |
| POST · PUT | `/vendors` · `/vendors/{id}` | `purchase.manage` | `SaveVendorRequest` (code, name, gstin, pan (must match GSTIN), drugLicenseNumber, drugLicenseValidUntil, contact, address, paymentTermsDays, rating, notes, isActive, version) → 201 `{id}` · 204 |
| GET | `/requisitions?page&status&storeId` · `/requisitions/{id}` | any of `purchase.manage`, `purchase.approve`, `purchase.grn.receive` | → paged `RequisitionListItem` · `RequisitionDetail` |
| POST | `/requisitions` | `purchase.manage` | `CreateRequisitionRequest` (storeId, requiredBy, remarks, lines[itemId, quantity, remarks]) → 201 `DocumentCreated` |
| POST | `/requisitions/from-reorder?storeId` | `purchase.manage` | → 201 `DocumentCreated` (from the reorder report: its first 300 items, most critical first, each for the suggested purchase units and at least one) |
| POST | `/requisitions/{id}/approve` · `/reject` | `purchase.approve` | — · `InventoryReasonRequest` → 204 |
| POST | `/requisitions/{id}/cancel` | `purchase.manage` | `InventoryReasonRequest` → 204 |
| GET | `/orders?page&status&vendorId&storeId&from&to` · `/orders/{id}` | as requisitions | → paged `PurchaseOrderListItem` · `PurchaseOrderDetail` (CGST/SGST or IGST per line) |
| POST · PUT | `/orders` · `/orders/{id}` | `purchase.manage` | `SavePurchaseOrderRequest` (vendorId, storeId, requisitionId?, expectedDeliveryDate, paymentTermsDays, remarks, lines[itemId, quantity, freeQuantity, rate, discountPercent, gstRate], version) → 201 `DocumentCreated` · 204 |
| POST | `/orders/{id}/submit` · `/send` · `/close` | `purchase.manage` | → 204 (`close` takes `InventoryReasonRequest`) |
| POST | `/orders/{id}/approve` · `/return` | `purchase.approve` | — · `InventoryReasonRequest` → 204 |
| POST | `/orders/{id}/cancel` | `purchase.manage` or `purchase.approve` | `InventoryReasonRequest` → 204 |

### Goods receipt — `/api/v1/purchase/grns`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/?page&status&vendorId&storeId&from&to` · `/{id}` | any of `purchase.grn.receive`, `purchase.grn.approve`, `purchase.manage`, `purchase.approve` | → paged `GoodsReceiptListItem` · `GoodsReceiptDetail` (each line with the item's code and base unit, for pharmacy tags; `vendorInvoiceAmount`, the invoice's total as printed; `approvalWaitingSince` while a request to post it waits) |
| POST | `/` | `purchase.grn.receive` | `CreateGoodsReceiptRequest` (vendorId, storeId, purchaseOrderId?, vendorInvoiceNumber, vendorInvoiceDate, receivedOn, remarks, lines[itemId, purchaseOrderLineId?, batchNumber, manufactureDate, expiryDate, quantity, freeQuantity, rate, mrp, discountPercent, gstRate, acceptShortExpiry], sendForApproval, vendorInvoiceAmount?) → 201 `GoodsReceiptCreated` (id, number, status `Draft`, netAmount, approvalId when sent for approval) |
| POST | `/{id}/approval` | `purchase.grn.receive` | — → `ApprovalRequestDto`: asks for a second person to post a draft into stock (below) |
| POST | `/{id}/cancel` | `purchase.grn.receive` | `InventoryReasonRequest` → 204 (a draft only; one sent for approval then posts nothing, `approval.stale`) |

GRN checks: duplicate vendor invoice (unique vendor + invoice number unless cancelled); the vendor's drug licence
must be valid on the receipt date for drug items; an expired batch is refused; a batch expiring within 90 days
needs `acceptShortExpiry`; a drug's or an implant's batch needs the MRP printed on its pack (`grn.mrp_required`: an
implant's MRP is the most its bill may charge, NPPA); rate ≤ MRP; purchase units are converted to base units, free goods
lower the unit cost, and re-receipt of the same batch number/expiry/MRP averages the cost.

### A goods receipt goes into stock when someone else approves it

The store enters the receipt as a draft: nothing is in stock. It is sent for approval as it is saved
(`sendForApproval`) or later (`/{id}/approval`), as `purchase.grn` in the approvals inbox (`/api/v1/approvals`), and
someone who holds `purchase.grn.approve` approves it, which posts it into stock (batches, ledger, the purchase order's
received quantities) as that person. The request shows what they check it against: the supplier, the invoice number
and date, the store and order, the number of lines, the total worked out from the lines beside the invoice's total as
printed (`vendorInvoiceAmount`, as the store entered it) and the difference, and the goods accepted though expiring
within 90 days.

Two people are kept apart:
- whoever **asked** never approves (`approval.self`, as for every approval);
- whoever **entered** the receipt never posts it either (`grn.post_own`, 403), even when someone else sent it for
  approval; the request then waits for a third person.

The receipt is posted as it was sent: one cancelled since posts nothing (`approval.stale`). A posted receipt is
corrected with a stock adjustment, a draft by cancelling it.

Pharmacy tags (INT-BAR): a posted receipt prints a tag per line (Print tags), with the medicine, batch, expiry, MRP per
base unit and a barcode of the item code and batch (`PCM500#AB1234`); so does a batch on the stock screen (Tags). An
inventory search for a tag's barcode finds the item by its exact code. See
[integrations.md](integrations.md#barcode-labels-int-bar).

### Assets — `/api/v1/assets` (AST-01/02/06, tag Assets)
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/?page&category&status&criticality&departmentId&includeRetired` | Asset readers | → paged `AssetListItem` (next PM due) |
| GET | `/dashboard` | Asset readers | → `AssetDashboard` (by status, open/critical tickets, PM due/overdue, calibration, warranty and contract expiries, month downtime) |
| GET | `/by-tag/{tag}` · `/{id}` | Asset readers | → `AssetDetail` (barcode scan lookup) |
| POST · PUT | `/` · `/{id}` | `assets.manage` | `SaveAssetRequest` (name, category, make, model, serialNumber, department, location, custodian, purchase details, installation, warrantyExpiry, criticality, depreciationMethod SL/WDV, usefulLifeYears, salvageValue, depreciationRatePercent, requiresCalibration, barcode, notes, version) → 201 `DocumentCreated` (tag `{FAC}AS{SEQ:6}`) · 204 |
| POST | `/{id}/operational` | `assets.manage` or `assets.maintenance` | `SetAssetOperationalRequest` (inUse) → 204 |
| GET | `/{id}/contracts` | Asset readers | → `AssetContractDto[]` |
| POST · PUT | `/{id}/contracts` · `/{id}/contracts/{contractId}` | `assets.manage` | `SaveAssetContractRequest` (type AMC/CMC (warranty is on the asset), vendorId, contractNumber, startDate, endDate, cost, coverageNotes, version) → 201 · 204 |
| GET | `/{id}/depreciation?asOf` | Asset readers | → `AssetDepreciation` (yearly schedule, rate, book value pro-rated to `asOf`) |
| POST | `/{id}/condemnation` | `assets.maintenance` or `assets.manage` | `RequestCondemnationRequest` (reason) → 201 (book value recorded at request) |
| GET | `/condemnations?status` | Asset readers | → `CondemnationDto[]` |
| POST | `/condemnations/{id}/approve` · `/reject` | `assets.manage` | `DecideCondemnationRequest` (remarks) → 204; decided by a second person |
| POST | `/{id}/dispose` | `assets.manage` | `DisposeAssetRequest` (disposedOn, disposalValue, method) → 204 (condemned assets only) |

**Asset tag (AST-01).** From the asset page the staff app prints the tag, without a dialog, on the workstation's
label printer: hospital, asset name and the tag as Code 128 for the biomedical team's scanner. On a ZPL or TSPL
printer the server writes the tag (`asset-tags`); otherwise the tag page prints. See [integrations.md](integrations.md#barcode-labels-int-bar).

### Maintenance and calibration — `/api/v1/assets` (AST-03/04/05)
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/maintenance/due?days` | Asset readers | → `MaintenanceDueItem[]` (PM, calibration, warranty and contract items due or overdue) |
| GET | `/pm-plans?assetId&dueOnly` | Asset readers | → `PmPlanDto[]` |
| POST · PUT | `/{assetId}/pm-plans` · `/{assetId}/pm-plans/{planId}` | `assets.maintenance` | `SavePmPlanRequest` (title, frequencyDays, nextDueDate, checklist, assignedTo, vendorId, isActive, version) → 201 · 204 |
| POST | `/pm-plans/{planId}/visits` | `assets.maintenance` | `RecordPmVisitRequest` (visitDate, performedBy, vendorId, outcome, findings, cost, serviceReportFileId) → 201; advances the next due date |
| GET | `/{assetId}/pm-visits` | Asset readers | → `PmVisitDto[]` |
| GET | `/tickets?page&status&priority&assetId&openOnly` · `/tickets/{id}` | Asset readers | → paged `TicketListItem` · `TicketDetail` |
| POST | `/tickets` | Asset readers (ward staff report breakdowns) | `ReportBreakdownRequest` (assetId, issue, priority?, assetDown) → 201 `DocumentCreated`; priority defaults from asset criticality; `assetDown` puts the asset under maintenance and alerts biomedical engineering |
| POST | `/tickets/{id}/assign` · `/start` · `/resolve` · `/close` · `/reopen` | `assets.maintenance` | `AssignTicketRequest` · — · `ResolveTicketRequest` (resolution, rootCause, cost, downtimeMinutes) · — · — → 204 |
| GET · POST | `/{assetId}/calibrations` | Asset readers · `assets.maintenance` | → `CalibrationDto[]` · `RecordCalibrationRequest` (calibratedOn, agency, certificateNumber, certificateFileId, nextDueDate, outcome, remarks) → 201; only the latest certificate counts (`Asset.RecordCalibration`, unit-tested): it sets the asset's next calibration date, and a failed one puts the asset under maintenance. An older certificate entered later changes neither |

### Cold chain — `/api/v1/inventory/cold-chain` (PH-04, INV-04)
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/units?storeId` | Cold-chain readers | → `ColdStorageUnitDto[]` (store, range, last reading and whether it was out of range, open excursion, logger and when it was last heard, today's morning and evening checks: Upcoming, Due, Taken or Missed) |
| POST · PUT | `/units` · `/units/{id}` | `inventory.cold_chain.manage` | `SaveColdStorageUnitRequest` (storeId, code, name, kind Refrigerator/Freezer/ColdRoom, minCelsius?, maxCelsius?, isActive, version) → 201 id · 204. No range takes the kind's (2–8 °C; a freezer −25 to −15 °C). A range changed is audited (Warning) with what it was |
| POST | `/units/{id}/logger-key` · `/units/{id}/logger-key/revoke` | `inventory.cold_chain.manage` | → `ColdChainLoggerKey` (key, shown once; path and header to send readings with) · 204. A new key stops the old one; only its SHA-256 is kept |
| POST | `/units/{id}/readings` | `inventory.cold_chain.record` | `RecordTemperatureRequest` (celsius to one decimal, takenAt? within the last 12 hours, note) → 201 `TemperatureReadingRecorded` (outOfRange, allowedRange, excursionId) |
| GET | `/units/{id}/register?date` | Cold-chain readers | → `ColdChainDailyRegister`: the day's readings (by hand with who, or from the logger), low and high, the morning and evening checks taken or missed, and the excursions that day with their action |
| GET | `/excursions?storeId&openOnly` | Cold-chain readers | → `ColdChainExcursionDto[]` (from the first reading out of range to the first back in it: extreme, readings out of range, the cold-chain stock in the store when it began, action taken and by whom). `openOnly` lists those still out of range or with no action recorded |
| POST | `/excursions/{id}/action` | `inventory.cold_chain.record` | `ExcursionActionRequest` (what was done about the stock) → 204, audited |
| POST | `/logger/readings` | anonymous, the unit's `X-Device-Key` (rate-limited as patient monitors are) | `LoggerUploadRequest` (readings[takenAt, celsius], up to 500) → 200 `LoggerUploadResult` (accepted, duplicates, rejected with why). A reading already received is skipped (unique unit + time for logger readings); an unknown or wrong key is the same 401 |

**Rules.** A reading is judged at one decimal against the unit's range as it was then (kept on the reading). One out of
range with no excursion open opens one and alerts, once, the store's in-charge (`Store.InChargeUserId`; with none,
everyone in the facility holding `inventory.cold_chain.manage`) in the app, critical, with the reading, the range and
the cold-chain items (`Item.IsColdChain`) with stock in the store, by batch and quantity. Later readings out of range
widen the same excursion; the first back in range ends it. A logger reading older than the unit's last reading (sent late
after an outage) is kept in the register and moves nothing. Freezing is an excursion like warming.

**The twice-daily check.** A manual reading between 06:00 and 11:00, and one between 16:00 and 21:00, hospital time, is
the morning and the evening check. A logger does not stand in for it: the thermometer is read and entered by hand twice a
day whether or not a logger is fitted, which is also how a failed logger is noticed. `ColdChainJob` (job-running nodes
only, every 10 minutes) alerts each check missed since it last looked, once (the unit keeps the last check it looked at,
`missed_alerted_through`; at most two days back; a unit set up during a window is not held to that check), and a logger
not heard from for two hours, once until it is heard again; both go to the in-charge as warnings.

## Price ceilings (DPCO 2013)

A scheduled formulation's ceiling price is kept per base unit, with GST, on the item (`ceilingPrice`), as NPPA notifies
it per tablet, capsule or ml. A pack's ceiling is that times the units in the pack, to the paisa (`PriceCeiling`), so a
pack printed at its ceiling is never refused for the rounding of its MRP per unit.

* **Receipt.** A goods receipt line of a medicine whose printed MRP is above its pack's ceiling is refused when it is
  entered (422 `grn.mrp_above_ceiling`, naming the batch, the MRP and the ceiling) and again when it is posted, against
  the ceiling then: a receipt entered before a cut is not posted above the new ceiling. The line keeps the ceiling it was
  entered under.
* **Sale.** A batch whose MRP per unit is above the ceiling is not sold at the counter or dispensed, and not issued to a
  ward (422 `pharmacy.price_above_ceiling`, with the item and batch; see pharmacy.md). The pharmacist picks another
  batch, or the store returns it to the supplier.
* **Implants** keep their own rule: charged at the lower of MRP and ceiling, an older pack still used.

## An item's ceiling price or GST rate loosened waits for someone who approves prices

On an item already in the master, a ceiling **raised** or **removed**, or the **GST rate** changed either way, waits
for a second person (`inventory.item_price`, MAKER-CHECKER.md slice 13). Someone else who holds `masters.prices.approve`
(accountants and hospital administrators by default, as for every other price master) approves it in Approvals; the one
who asked never does (`approval.self`). The rest of the item is saved at once, `PUT /items/{id}` answers 202 with the
request, and the item keeps its ceiling, notification and rate until then (`pendingPricing` on `GET /items/{id}`). The
request says before and after in words ("Ceiling ₹2.50 a TAB (₹25.00 a STRIP of 10) with GST, S.O. …; GST 5%") and what
the change lets through (packs priced higher received and sold; every sale and purchase taxed at the new rate). Approved,
it is made only over the item's pricing as it was asked (`approval.stale` otherwise), audited at Warning with both names
(`inventory.item_price_changed`). A ceiling **set** or **lowered** applies at once: NPPA's cut is in force from its
notification. While a change waits, a save asking other loosened pricing is refused (409 `inventory.item_price_waiting`).
A save that loosens both the class and the pricing asks for both; the answer carries the classification's request, and
the pricing's waits beside it in Approvals. A new item's pricing applies at once.

## Stock rules (INV-04)

* `StockService` is the only code that changes stock quantities; documents never write batches directly.
* **FEFO** (`FefoAllocator`, pure and unit-tested): earliest expiry first and non-expiring stock last; ties go to
  the earliest receipt, then batch number. Expired, blocked and empty batches are never allocated, but the expiry
  day itself is usable. An optional minimum shelf life skips batches that expire before a course ends. A
  scanned batch is used first.
* **Invariants**: `0 ≤ quantity_reserved ≤ quantity_on_hand` on every batch, enforced in the domain and by the check
  constraint `ck_stock_batches_quantities`. A batch is identified by (store, item, batch number, expiry, MRP).
* **Concurrency**: movements of one item in one store are serialised with a transaction-scoped
  `pg_advisory_xact_lock`. Keys are taken in sorted order, so two transfers never deadlock. This keeps the ledger's
  running balance (`balance_after`, `batch_balance_after`) exact, and the statutory registers built on it. Batch
  rows also carry the `xmin` version: `SaveAsync` maps a lost race to `stock.concurrent_update` (409), and a
  check violation to `stock.negative` (409). Both are safe to retry.
* **Error codes**: `stock.insufficient` (with `{itemName, available, requested}`), `stock.batch_expired`,
  `stock.batch_short_expiry`, `stock.batch_blocked`, `stock.batch_not_found`, `stock.wrong_batch`,
  `stock.invalid_quantity`, `stock.no_lines`, `stock.same_store`, `stock.no_store`, `stock.store_ambiguous`,
  `store.inactive`.
* The ledger (`stock_ledger`) is append-only and is itself the audit trail: who, when, movement type, document
  (`reference_module`, id, number), batch, cost and MRP.

## Services for other modules

### `StockService` (scoped; `HyFib.HealthCare360.Modules.SupplyChain.Services`)

Every movement is **staged in the caller's unit of work**. Call it inside `db.Database.BeginTransactionAsync()`,
because the advisory locks need a transaction; without one it throws `InvalidOperationException`. Then call
`stock.SaveAsync(ct)` and commit, so your document and the stock change commit together. Pharmacy uses it this way.

| Method | Use |
|---|---|
| `GetBatchesAsync(storeId, itemId, includeEmpty)` | Batches in FEFO order (`BatchStock`: available, reserved, expired/blocked flags, days to expiry) |
| `GetAvailableAsync(storeId, itemIds)` | Quantity FEFO could issue now, per item |
| `PlanAsync(storeId, itemId, qty, options)` | FEFO preview, no locks and no changes |
| `IssueAsync(StockIssueRequest)` | Outward movement (Issue, Sale, Dispense, Consumption, TransferOut): scanned batch or FEFO, optional partial, shelf-life rule, consumes the document's own reservations first → `StockIssueResult` (movements per batch with MRP, cost, GST rate, ledger id, `RequiresRegister`; shortfalls) |
| `ReceiveAsync(StockReceipt, movement, reference, counterpartStoreId)` | Inward (Grn, TransferIn, Opening, Return): tops up the matching batch or creates it |
| `ReturnToBatchAsync(batchId, qty, Return/TransferIn, reference, counterpart)` | Put stock back into the batch it came from |
| `AdjustBatchAsync(batchId, delta, Adjustment/ExpiryWriteOff, reference)` | Approved correction |
| `TransferAsync(from, to, lines, reference, consumeReservations)` | One-step store-to-store move (same batch/expiry/MRP) |
| `ReserveAsync(...)` / `ReleaseReservationsAsync(module, referenceId, lineId?)` | Set stock aside for a document / release it |
| `ResolveStoreAsync(facilityId, StoreType)` | The facility's single active store of a type (for example the OT store) |
| `ConsumeAsync(StockConsumption)` | Stock used on a patient or in a procedure (see below) |
| `ReverseConsumptionAsync(StockReference)` | Undo a consumption, idempotently |
| `SaveAsync(ct)` · `LockAsync(keys, ct)` | Save with stock-error mapping · take item/store locks explicitly |

`StockReference(Module, Id, Number, Remarks)` is stamped on every ledger row. Use your module's prefix, for example
`"specialtycare.ot"` or `"pharmacy.dispense"`.

### Consuming stock from another module (OT implants and consumables, CSSD, procedure rooms)

`ConsumeAsync(new StockConsumption(storeId, itemId, quantity, reference, batchNumber?, expiryDate?))` books a
`Consumption` movement. With a lot/batch number (as written on the pack), only that lot is used; rows that share
the number (a re-priced MRP, or two expiries) are used FEFO, and `expiryDate` narrows it to one. Without a lot
number, FEFO picks the batch. It never issues part of the quantity: it fails with `stock.batch_not_found`,
`stock.batch_expired`, `stock.batch_blocked` or `stock.insufficient` and stages nothing. `reference.Id` is required:
use the id of your consuming record. Register items (Schedule H1/X, NDPS), such as midazolam or morphine used in
OT, also reach the Pharmacy controlled-drug register through `ControlledStockMoved`. Consumption feeds ABC/FSN
analysis.

`ReverseConsumptionAsync(reference)` returns whatever is still consumed under (module, id) to the original batches,
for example when a wrongly entered OT material is removed. A second call finds nothing outstanding and returns no
movements.

SpecialtyCare uses both for operation theatre materials (`OtClinicalService`, which references SupplyChain). Recording a
material that names a supply item consumes it from the facility's OT store, in the same transaction as its charge;
consignment and patient-supplied items name none and take no stock. Removing the entry reverses the consumption and
withdraws the charge.

### Read helpers (`InventoryQueries`, static)

`Search(term)` (item code, name, generic, brand, barcode), `ItemSummariesAsync(db, ids)` (`ItemSummary`: name,
generic, strength, form, HSN, GST, schedule, controlled/cold-chain, `RequiresRegister`),
`AvailabilityAsync(db, storeId, ids, today)` (`StockAvailability`: on hand, available, nearest expiry, current MRP),
and `ExpiringAsync(db, storeIds, today, days, includeExpired)` (`ExpiringBatch` with value at cost and MRP).

## Events

| Event | Direction | When / use |
|---|---|---|
| `ControlledStockMoved` (domain event, same transaction) | raised | Every ledger row of a register item (Schedule H1/X or `IsControlled`), with ledger id, batch, signed quantity, balance after and reference. Handled by Pharmacy, which writes the controlled-drug register line for movements made outside pharmacy screens (GRN, transfers, adjustments, write-offs, OT consumption) |

The module handles no events from other modules, and publishes no real-time events. Cold-chain alerts go through the
outbox (`supplychain.cold-chain-alert`), so they are sent only once the reading is saved.

## Background job and notifications

`AssetReminderJob` runs only when `PlatformOptions.RunsJobs` is set. It ticks every 30 minutes and runs once a day
after 07:00 IST. It sends holders of `assets.maintenance` in each facility one in-app summary: PM and
calibrations due within 7 days or overdue, and warranties or AMC/CMC contracts lapsing within 30 days (link
`/assets/maintenance`; severity *warning* when anything is overdue). The last run date is kept in the setting
`supplychain.assets.reminders.last_run`, so a restart never sends the same day twice. A breakdown reported with
`assetDown` notifies the same holders immediately. There are no SMS/WhatsApp templates.

`ColdChainJob` (above, *Cold chain*) alerts missed temperature checks and silent loggers every 10 minutes.

## Number series (per facility)

| Series key | Format | Reset |
|---|---|---|
| `supplychain.pr` | `{FAC}PR{YY}{SEQ:6}` | yearly |
| `supplychain.po` | `{FAC}PO{YY}{SEQ:6}` | yearly |
| `supplychain.grn` | `{FAC}GRN{YY}{SEQ:6}` | yearly |
| `supplychain.indent` / `supplychain.issue` | `{FAC}IND{YY}{SEQ:6}` / `{FAC}ISS{YY}{SEQ:6}` | yearly |
| `supplychain.transfer` | `{FAC}STN{YY}{SEQ:6}` | yearly |
| `supplychain.adjustment` / `supplychain.count` | `{FAC}ADJ{YY}{SEQ:6}` / `{FAC}PSC{YY}{SEQ:5}` | yearly |
| `supplychain.asset` | `{FAC}AS{SEQ:6}` (asset tag) | never |
| `supplychain.ticket` | `{FAC}MT{YY}{SEQ:6}` | yearly |

Numbers are taken inside the insert transaction, so they stay gap-free.

## A drug handled less strictly waits for a pharmacist

A drug's class is its schedule, whether it is a controlled (NDPS) drug, and whether it is a high-alert medicine
(`DrugSafetyClass`). On an item already in the master, saving it **less strict** in any of these ways waits for a
second person (`inventory.drug_class`, maker-checker):

* no longer controlled;
* no longer high-alert;
* a lower schedule (X above H1 above H above the rest).

The rest of the item is saved at once, and `PUT /items/{id}` answers 202 with the request (`classChangeReason` is the
reason the approver reads). Until it is approved, the drug keeps its class: its register entries, the witness at the
bedside and the prescription it needs all stay. Someone else who holds `inventory.drug_class.approve` (pharmacists and
hospital administrators by default) approves it in Approvals. The request shows what the change switches off.

While a change waits:

* a save asking for another, still less strict class is refused (409 `inventory.drug_class_waiting`);
* a save asking for the same one, or keeping the current one, saves the rest;
* a stricter class applies at once. The waiting request is then refused when approved (`approval.stale`), because the
  class it would replace has changed.

A request nobody decides before it runs out no longer holds anything: the drug keeps its class, and saving the change
again asks anew.

Made stricter, and on a new item, the class applies at once. `GET /items/{id}` shows the waiting change
(`pendingClass`). The ward's medicine orders copy the class when they are made (inpatient.md), so a change reaches
orders made after it.

## Seed data (`SupplyChainSeeder`, order 150, idempotent by code)

* Every installation: 13 item categories and about 79 items (6-digit HSN, GST mostly 5%, Drugs & Cosmetics
  schedules; tramadol and alprazolam H1 controlled, morphine X controlled, midazolam H1, insulin cold chain; insulins,
  heparin, morphine and midazolam high-alert in a new installation), plus
  stores `CS` Central Store, `OPPH` OP Pharmacy, `IPPH` IP Pharmacy, `ICUS` ICU Store and `OTS` OT Store.
* With `SeedDemoData`: 5 vendors, opening stock through `StockService` (including near-expiry and expired
  batches), and 10 assets with PM plans, contracts, calibrations and one open breakdown ticket.

## Tables (schema `supplychain`)

`item_categories`, `items` (trigram index on name), `stores` (unique facility + code), `vendors`, `stock_batches`
(check constraint above; unique store + item + batch + expiry + MRP with nulls not distinct), `stock_ledger`,
`stock_reservations`, `purchase_requisitions`/`_lines`, `purchase_orders`/`_lines`, `goods_receipts`/`_lines` (unique
vendor + vendor invoice number, filter `status <> 'Cancelled'`), `indents`, `indent_lines`, `indent_issues`,
`indent_issue_lines`, `stock_transfers`/`_lines`, `stock_adjustments`/`_lines`, `stock_counts`/`_lines`, `assets`
(unique tag), `asset_contracts`, `pm_plans`, `pm_visits`, `maintenance_tickets`, `calibration_records`,
`condemnation_requests`, `cold_storage_units` (unique facility + code), `temperature_readings` (unique unit + time for
logger readings), `cold_chain_excursions`. Items carry `ceiling_notification`; goods receipt lines the `ceiling_price`
they were entered under. Precision: quantities (18,3), rates (18,4), percentages (6,2). Enums are stored as text,
so queries that rank by an enum (indent priority, store type) rank explicitly or sort in memory.

## Open items for maintainers

* Please verify the seeded GST rates and HSN headings with the hospital's tax consultant before go-live.
