# Analytics module (MIS reports, executive KPIs, dashboards, scheduled e-mails, BI extracts)

Project: `src/server/Modules/HyFib.HealthCare360.Modules.Analytics` · schema `analytics` · reads every module's tables
through the shared `HospitalDbContext` (read-only, `AsNoTracking`), owns only its own four tables.
Covers SRS **MIS-01..05** (report catalogue, operational, financial, clinical and stock reports, Excel/CSV/PDF
export) and **BI-01..05** (executive dashboard, saved dashboards, drill-down, scheduled delivery, read replica, and
scheduled data extracts for external BI tools such as Power BI, with data-refresh monitoring).

Staff app: `src/web/apps/staff/src/modules/analytics` (manifest section *Insights*) and
`src/web/apps/staff/src/routes/_app/analytics`.

## Permissions

| Permission | Default roles | Purpose |
|---|---|---|
| `analytics.mis.read` | MisAnalyst, HospitalAdmin, Accountant | Operational reports (OP, IP, diagnostics, quality, people), stock reports, KPIs |
| `analytics.finance.read` | Accountant, HospitalAdmin | Revenue, collections, dues, discounts, refunds, doctor fees, pharmacy margin, stock value; revenue and collections KPIs |
| `analytics.clinical.read` | HospitalAdmin, QualityManager, MrdOfficer | Operational/clinical reports **and the only way to open reports that list patients** |
| `analytics.dashboards.manage` | HospitalAdmin, MisAnalyst | Create, change, copy and delete dashboards |
| `analytics.schedules.manage` | HospitalAdmin, MisAnalyst | Scheduled report e-mails and their delivery files, and BI extracts |

Every read endpoint needs *any* of the three read permissions; each report then checks its own audience:

| Audience | Reports | Needs any of |
|---|---|---|
| Operations | OP, IP, diagnostics TAT, OT, dialysis, blood bank, quality, feedback | `mis.read`, `clinical.read` |
| Finance | revenue, collections, dues, discounts, refunds, doctor fees, pharmacy sales & margin | `finance.read` |
| Stock | near-expiry, dead stock, purchase vs consumption | `mis.read`, `finance.read` |
| MIS | HR head count, attendance | `mis.read` |
| Clinical (patient-level) | adverse discharges register, critical values register | `clinical.read` |

Patient-level reports additionally always need `analytics.clinical.read`, can never be scheduled or put on a dashboard,
and every page view and export is audited per patient (see *Audit*).

## How reports run

* **Catalogue** (`Reports/ReportCatalog.cs`): 39 report definitions, each a `ReportDescriptor` (id, title, category,
  description, **definition/formula text**, audience, parameters, columns, default chart, default sort, patient-level
  flag, max days) plus a query. The catalogue is pure data; the unit tests check ids, columns, charts and permissions.
* **Inputs** (`ReportParameterBinder`, unit-tested): only declared parameters are read; values are trimmed and capped at
  64 characters; ids must be GUIDs; choices must be in the list; numbers within min–max. Periods are named presets
  (`today, yesterday, last7Days, last30Days, thisWeek, lastWeek, thisMonth, lastMonth, thisQuarter, lastQuarter,
  thisFinancialYear, lastFinancialYear`) or `custom` with `from`/`to` (`yyyy-MM-dd`, from 2000-01-01, `to` ≤ today,
  `from` ≤ `to`, at most `maxDays` — 366 by default, 93 for the critical values register). Errors come back as a 400
  keyed by parameter (`period`, `from`, `to`, `departmentId`…).
* **IST**: all day boundaries are IST calendar days (UTC+05:30, fixed). SQL buckets instants with
  `(ts + interval '330 minutes')::date`; ranges are half-open UTC instants `[start of from, start of to + 1)`.
* **Facility scoping**: reports run in a `HospitalDbContext` whose current facility is the report's facility, so the
  named facility and soft-delete query filters apply to every table. `facilityId=all` (consolidated) is allowed only
  for users without a facility restriction (`canConsolidate` in the catalogue). Users restricted to some facilities
  can choose among those.
* **Query rules**: SQL projection only (no entity loading); aggregates use `count(*) FILTER`/`sum` in SQL; enum columns
  are stored as text and are **never ordered in SQL** (ordering happens in memory); every list is capped
  (5,000 rows for aggregate reports, 10,000 for patient lists, 250,000 raw measurements for TAT percentiles); a hit cap
  sets `truncated` and a note. Sorting and paging of the finished rows happen in memory (`ReportShaping`), so no client
  input ever reaches SQL.
* **Caching**: a run is cached for 60 s per report, facility and canonical inputs (patient-level reports are never
  cached); KPIs for 2 minutes per facility, period and audience.
* **Read replica** (BI-05): when `ConnectionStrings:Analytics` (or the legacy `ConnectionStrings:Reporting`) is set,
  reports and KPIs read from it with a 120 s command timeout; on a transient `NpgsqlException` a run falls back to the
  primary once. Results carry `dataSource` ("Reporting replica" / "Live database"); `/status` reports replica lag.

## Endpoints

All under `/api/v1/analytics`, tag *Analytics*.

### Reports, KPIs and look-ups (any analytics read permission)

| Method | Path | Body / query → Response |
|---|---|---|
| GET | `/reports` | → `ReportCatalogueDto {categories[{id,label,count}], reports: ReportCatalogueItem[], periods[{value,label}], canManageDashboards, canManageSchedules, canSeePatientLevel, canConsolidate, dataSource}`; only reports the caller may open |
| GET | `/reports/{id}` | → `ReportCatalogueItem {id, title, category, categoryLabel, description, definition, parameters[{key,label,kind,required,defaultValue,choices,min,max,hint}], columns[{key,label,type,isPatientIdentifier}], chart{type,categoryKey,seriesKeys,timeSeries,topN}, containsPatientIdentifiers, canSchedule, maxDays}`; 403 when not allowed |
| POST | `/reports/{id}/run` | `RunReportRequest {parameters{key:value}, page, pageSize (≤ 500), sort, desc, chart}` → `ReportResultDto` |
| GET | `/reports/{id}/run` | the same with inputs as query parameters (`?period=lastMonth&departmentId=…&page=2&pageSize=50&sort=revenue&desc=true&chart=Pie`). The staff app uses this form: it is a read, so it is not stored by the idempotency layer |
| POST | `/reports/{id}/export?format=xlsx\|csv\|pdf` | `ExportReportRequest {parameters, sort, desc}` → file (`Cache-Control: no-store`); another format → 400 "Choose Excel (xlsx), CSV or PDF."; `pdf` → `application/pdf`, or 503 `pdf.unavailable` when the server cannot make PDFs; a PDF that leaves rows out carries the header `X-Report-Notice` (see *Exports*) |
| GET | `/reports/{id}/export?format=xlsx\|csv\|pdf&…inputs` | the same as a GET (browser downloads) |
| GET | `/kpis?from&to&facilityId` | → `KpiSet {from, to, previousFrom, previousTo, facilityName, kpis[], generatedAt, dataSource}`; default period = this month to date; max 366 days |
| GET | `/lookups/{kind}?search&facilityId` | `kind` = `facilities` (the caller's allowed facilities), `departments`, `practitioners`, `payers`, `wards`, `stores`, `theatres` → `LookupItem[] {id, code, name}` (≤ 200, active only) |
| GET | `/status` | → `AnalyticsStatusDto {dataSource, usesReplica, replicaLagSeconds, activeSchedules, lastDeliveryAt, failedDeliveriesLast7Days, checkedAt, activeExtracts, lateExtracts, failingExtracts, lastExtractRefreshAt}`; the three e-mail figures leave out extracts, which the last four cover (late and failing as in *BI extracts*) |

`ReportResultDto`: `reportId, title, category, from, to` (null for snapshot reports), `facilityName`,
`filters[{label,value}]` (inputs in words), `parameters` (canonical inputs as run — reuse them for export, drill-down
and schedules), `columns`, `rows` (one page, values aligned with `columns`: numbers, strings, `yyyy-MM-dd` for Date and
Month, ISO instants for DateTime), `rowKeys` (ids for drill-down), `totals` (whole report), `summary[{key,label,value,
type}]`, `chart {type, categoryKey, categoryLabel, categoryType, categories[], series[{key,label,type,values[]}],
timeSeries, keys[]}`, `drillDown {reportId, parameterKey, label, parameters}`, `notes[]`, `page, pageSize, totalRows,
truncated, sort, desc, generatedAt, dataSource, containsPatientIdentifiers, pdfNotice` (null unless the report has more
rows than a PDF lists: then what a PDF export will hold, for the export menu to show).

Charts: rankings show the top N categories (15 by default) and fold the rest into *Others* (only for additive series:
counts and money, never percentages); time series keep every point in order; a pie uses the first series.

Drill-down (BI-03): open `drillDown.reportId` with the row's key in `drillDown.parameterKey`, the same `period`/`from`/
`to`/`facilityId`, and the drill's fixed `parameters`. Built in: OP census → by doctor; bed occupancy → ward census;
ALOS → by consultant; discharge outcomes → adverse-discharge register (clinical users); revenue by department → revenue
by doctor; lab and radiology volume → by test/study; pharmacy sales by store → by medicine; HR head count → attendance.

### Dashboards (`/api/v1/analytics/dashboards`)

| Method | Path | Permission | Body → Response |
|---|---|---|---|
| GET | `/` | any read | → `DashboardListItem[] {id, name, description, isShared, isTemplate, isMine, canEdit, ownerName, widgetCount, updatedAt, version}`: templates, the caller's own and shared ones |
| GET | `/{id}` | any read | → `DashboardDetail {…, widgets[{id, reportId, title, view, width, parameters, canView, reportTitle}]}`; `canView` is false for tiles whose report the viewer may not open |
| POST | `/` | dashboards.manage | `SaveDashboardRequest {name, description, isShared, widgets[{id, reportId, title, view, width, parameters}], version}` → 201 `DashboardDetail` |
| PUT | `/{id}` | dashboards.manage (owner; templates by any manager) | same body with `version` (409 on a stale version) |
| POST | `/{id}/copy` | dashboards.manage | personal, unshared copy of any visible dashboard → 201 |
| DELETE | `/{id}?version=` | dashboards.manage (owner; templates by any manager) | 204 |

Widget rules (validated on save): the report exists and the saver may open it; no patient-level reports; `view` ∈
`Bar, StackedBar, Line, Area, Pie, Table, Summary` (reports without a chart only `Table`/`Summary`); `width` ∈ 1 (a
quarter), 2 (half), 4 (full row); inputs must bind (they are stored in canonical form); at most 24 widgets. The layout
is stored as JSON (`dashboards.layout_json`, jsonb): `[{"id","reportId","title","view","width","parameters"}]`.

### Scheduled e-mails and BI extracts (`/api/v1/analytics`, all need `analytics.schedules.manage`)

| Method | Path | Body → Response |
|---|---|---|
| GET | `/schedules?destination=` | → `ScheduleDto[]`: the facility's schedules by name, at most 500; `destination` `Email` or `ExtractFolder` lists one kind (each screen asks for its own) |
| GET | `/schedules/{id}` | → `ScheduleDto {id, name, reportId, reportTitle, parameters, format, frequency, time, dayOfWeek, dayOfMonth, when, recipients, isActive, ownerName, isMine, nextRunAt, lastRunAt, lastStatus, lastError, version, destination, extractName, extractFile, lastSucceededAt, extractState}`; the last four are for extracts (`extractState`: `Waiting`, `Fresh`, `Late`, `Failing`, `Paused`; null for e-mails) |
| POST | `/schedules` | `SaveScheduleRequest {name, reportId, parameters, format: Xlsx\|Csv\|Pdf, frequency: Hourly\|Daily\|Weekly\|Monthly, time "HH:mm" (IST; only the minutes for hourly), dayOfWeek (weekly), dayOfMonth 1–28 or 31 = last day (monthly), recipients (1–20), isActive, destination: Email\|ExtractFolder (default Email), extractName}` → 201 |
| PUT | `/schedules/{id}` | same with `version`; **the person who saves becomes the owner** (the report runs with the owner's permissions and facility access) |
| DELETE | `/schedules/{id}?version=` | soft delete → 204; an extract's file is removed from the folder |
| POST | `/schedules/{id}/send-now` | generates the file now and queues the e-mails, or rewrites the extract's file (schedule unchanged) → `DeliveryDto`, or 422 `analytics.delivery_failed` (also when a PDF cannot be made, or an extract's file cannot be written) |
| GET | `/extracts/folder` | → `ExtractFolderDto {root, facilityFolder, refreshLog, problem}`: where extracts are written, the signed-in user's facility's sub-folder, the refresh log, and why HC360 cannot write the folder (null when it can; checked by writing a file) |
| GET | `/schedules/{id}/deliveries?page&pageSize` | → `PagedResult<DeliveryDto {id, scheduleId, reportTitle, trigger, createdAt, status, periodFrom, periodTo, format, fileName, sizeBytes, rowCount, fileAvailable, error, sent, pending, recipients[{email, status, attempts, lastError, sentAt}]}>` |
| GET | `/deliveries/{id}/file` | the delivered file again while it is kept (audited); 422 `analytics.file_purged` after retention |

Schedule rules: the report must be one the saver may open and must not list patients; the facility is always the
schedule's own (`facilityId` inputs are dropped); periods must be relative (`custom` is rejected: "Choose a relative
period…"); without a period the e-mail covers the complete period that just ended (daily → yesterday, weekly → last
week, monthly → last month); recipients are lower-cased, de-duplicated and, when the setting
`analytics.schedules.allowedEmailDomains` is set, must belong to one of those domains. E-mails go daily, weekly or
monthly ("E-mails go daily, weekly or monthly."). An extract (`destination` `ExtractFolder`) also runs hourly, is
always CSV ("BI extracts are CSV files."), has no recipients (any sent are dropped), covers this financial year when no
period is chosen, and needs an `extractName`: lower-cased and trimmed, then lower-case letters, digits and dashes,
starting with a letter or digit, at most 64 (field `extractName`), not a Windows device name such as `con`, `nul` or
`com1` ("Windows keeps this name for a device…"), and unique among the facility's extracts ("Another BI extract of
this facility already writes …", also a unique index).

## Report catalogue

Money is in rupees, including GST unless stated. "Period" = named or custom period; "Facility" = the facility input.

### Out-patient
| Id | Title | Inputs | Definition |
|---|---|---|---|
| `op-census` | OP census by department and doctor | Period, Facility, Show by (department/doctor), Visit type, Department, Doctor | Visits = check-ins in the period, excluding cancelled visits (shown separately). Follow-up = a repeat consultation with the same doctor within the doctor's follow-up validity. Visits per day = visits ÷ days in the period. Drill: department → doctors |
| `op-visit-trend` | New vs follow-up visits (trend) | Period (last 30 days), Facility, Interval (day/week/month), Visit type, Department, Doctor | Visits = check-ins on each IST day, excluding cancelled visits; follow-up as above, all other visits are new |
| `op-hourly-footfall` | Hourly footfall | Period (last 30 days), Facility, Visit type, Department | Check-ins by IST hour and weekday. Average per day = check-ins in the hour ÷ days; share = the hour's check-ins ÷ all check-ins |
| `appointment-no-show` | Appointment no-show rate | Period, Facility, Department, Doctor | Appointments due in the period (by slot time), excluding rescheduled bookings. Attended = checked in, in consultation or completed. No-show rate = no-shows ÷ (attended + no-shows). Not marked = still booked after the slot |

### In-patient and beds
| Id | Title | Inputs | Definition |
|---|---|---|---|
| `ip-admissions-discharges` | Admissions and discharges | Period (last 30 days), Facility, Interval, Department | Admissions by IST admission date, discharges (all outcomes) by IST discharge date, cancelled admissions excluded. Midnight census = patients in a bed at the midnight ending each day; weekly/monthly rows show the average daily census |
| `bed-occupancy` | Bed occupancy by ward | Period, Facility, Ward | Inpatient days = patients in a bed of the ward at each IST midnight (transfers counted in the ward the patient was in). Available bed-days = beds in service × days. **Occupancy % = inpatient days ÷ available bed-days × 100**. Drill: ward → ward census |
| `bed-turnover` | Bed turnover by ward class | Period, Facility | Discharges counted in the patient's last ward. **Bed turnover rate = discharges ÷ beds in service. Turnover interval = (available bed-days − inpatient days) ÷ discharges.** ALOS as below |
| `alos` | Average length of stay (ALOS) | Period, Facility, Show by (department/consultant), Department, Consultant | Length of stay = IST calendar days from admission to discharge, at least one. **ALOS = total length of stay ÷ discharges** (all outcomes). Drill: department → consultants |
| `ward-census` | Census by ward (now) | Facility, Ward | Active beds by current status. Occupancy now = occupied ÷ beds in service (blocked and maintenance beds are out of service) |
| `discharge-outcomes` | Discharge outcomes, LAMA/DAMA and mortality | Period, Facility, Department | LAMA % and DAMA % = those discharges ÷ discharges. **Gross death rate = deaths ÷ discharges × 100. Net death rate = (deaths − deaths within 48 h of admission) ÷ (discharges − deaths within 48 h) × 100.** Drill (clinical users): → register |
| `adverse-discharges` | Deaths, LAMA and DAMA register *(patient list)* | Period, Facility, Department, Outcome | Stays ended in the period with the chosen outcomes; LOS in IST days; within 48 h flag. Audited per patient |

### Revenue and collections (finance)
Revenue rule: *net amount (after discounts, including GST) of bill lines on bills finalized in the period; cancelled
bills are excluded and returns reduce revenue.*

| Id | Title | Inputs | Definition |
|---|---|---|---|
| `revenue-by-department` | Revenue by department | Period, Facility, Bill type (OP/IP), Payer type, Department | Revenue rule by service department, with discounts (tariff concessions + staff discounts) and share of total. Drill: department → doctors |
| `revenue-by-doctor` | Revenue by doctor | Period, Facility, Bill type, Payer type, Service department, Doctor | Revenue rule by ordering/performing practitioner on the charge; services without a doctor grouped together |
| `revenue-by-service` | Revenue by service category | Period, Facility, Bill type, Payer type, Department | Revenue rule by service type (consultation, lab, radiology, procedure, bed…); units = quantity billed less returns |
| `revenue-by-payer` | Revenue by payer | Period, Facility, Bill type, Payer type, Payer | Bills finalized in the period by payer with patient and payer shares. Collected = patient payments − refunds; settled = insurer/TPA settlements; outstanding = patient due + payer share not settled or disallowed |
| `daily-revenue` | Revenue and collections (trend) | Period, Facility, Interval | OP and IP billing by IST finalization date; pharmacy = counter and prescription sales less returns; collections = money received (all modes except advance adjustments and pharmacy credit) less refunds paid |
| `collections-by-mode` | Collections by payment mode | Period, Facility | Billing receipts (payments, deposits, advances, settlements) and pharmacy payments by mode; net = received − refunds in the same mode |
| `outstanding-ageing` | Outstanding dues and ageing | Facility, Bill type, Payer type | As of today: patient dues (after credit notes) and payer dues by age of the bill (days since finalized, IST) in 0–30, 31–60, 61–90, 91–180 and over 180 days |
| `discounts` | Discounts given | Period, Facility, Bill type, Department | Tariff concession (payer plan) and staff discount (line discounts + share of bill discount) by department; discount % = total ÷ gross; credit notes and write-offs by date issued |
| `refunds` | Refunds | Period, Facility | Refund requests by date raised and purpose: paid, pending (requested/approved), rejected; days to pay = average request-to-payment days |
| `doctor-fee-summary` | Doctor fee summary | Period (last month), Facility, Doctor | Doctor-fee accruals by the IST date earned; share after reversals; split into paid, in payout and pending |

### Laboratory and radiology
| Id | Title | Inputs | Definition |
|---|---|---|---|
| `lab-volume-tat` | Laboratory volume and turnaround | Period (last 30 days), Facility, Show by (section/test), Lab section, TAT target (minutes) | Tests ordered in the period. Total TAT = order → release; in-lab TAT = sample received → release. **Median and 90th percentile by the nearest-rank method.** Within target % = released tests with TAT ≤ target. Drill: section → tests |
| `radiology-volume-tat` | Radiology volume and turnaround | Period (last 30 days), Facility, Show by (modality/study), Modality, TAT target | Studies ordered in the period; reported = reported, verified or released; TAT = order → first report; reporting time = images acquired → report. Drill: modality → studies |
| `critical-values` | Critical values register *(patient list)* | Period (last 7 days, max 93 days), Facility | Critical lab results (by result entry time) with their critical-call record, plus documented critical imaging and cardiology calls; minutes to inform = result → documented call. Audited per patient |

### Pharmacy and stores
| Id | Title | Inputs | Definition |
|---|---|---|---|
| `pharmacy-sales-margin` | Pharmacy sales and margin | Period, Facility, Show by (day/medicine/store), Store | Invoices less returns (cancelled excluded). Sales excl. GST = taxable value kept; cost = quantity kept × batch purchase rate; **gross margin = sales excl. GST − cost; margin % = margin ÷ sales excl. GST**. Drill: store → medicines |
| `near-expiry-stock` | Near-expiry and expired stock | Facility, Store, Expiring within (days, default 90) | Batches with stock whose expiry is within N days of today (expired included); value at cost and at MRP |
| `dead-stock` | Dead and non-moving stock | Facility, Store, No movement for (days, default 90) | Items with stock and no issue, sale, dispense or consumption from that store in N days; value at cost; last issued |
| `purchase-vs-consumption` | Purchase vs consumption | Period, Facility, Show by (category/item), Store | Purchased = GRNs; consumed = issues, sales, dispenses, consumption less returns (transfers excluded), at ledger cost; consumed ÷ purchased |

### OT, dialysis and blood bank
| Id | Title | Inputs | Definition |
|---|---|---|---|
| `ot-utilisation` | OT utilisation | Period, Facility, Theatre, Planned OT minutes per day (default 480) | Actual minutes = in room → out of room for completed cases; **utilisation % = actual minutes ÷ (days × planned minutes)**; first case on time = in room within 15 min of the scheduled start; turnover = gap between consecutive cases (gaps over 2 h are idle time) |
| `dialysis-sessions` | Dialysis sessions | Period, Facility, Show by (date/shift/machine) | Sessions scheduled in the period; completion % = completed ÷ (sessions − cancelled); average duration and ultrafiltration over completed sessions |
| `blood-bank-stock` | Blood bank stock (now) | Facility | Units by blood group and component: available, reserved (cross-matched), quarantine (screening incomplete), expiring within 72 h |
| `blood-usage` | Blood usage and wastage | Period, Facility | Prepared, cross-matched, issued, returned, discarded, expired by component; **C/T ratio = cross-matched ÷ issued**; wastage % = (discarded + expired) ÷ prepared |

### Quality and safety
| Id | Title | Inputs | Definition |
|---|---|---|---|
| `incident-rates` | Incident rates | Period, Facility, Department | Incidents by date occurred and type, harm (mild/moderate/severe/death), sentinel events, closure; **rate = incidents ÷ inpatient days × 1,000** |
| `capa-closure` | CAPA closure | Period (this quarter), Facility | CAPAs raised by source: closed (completed/verified), on time, overdue, verified effective; closure % = closed ÷ (raised − cancelled) |
| `nabh-indicators` | NABH quality indicators | Period (this financial year), Facility | Every active indicator: latest value, period average, target, met/not met and trend (latest vs mean of up to three earlier values, in the indicator's direction) |
| `top-diagnoses` | Top diagnoses (morbidity index) | Period, Facility, Department, Number of diagnoses (default 50) | ICD-coded provisional and final diagnoses (differentials excluded); patients = distinct patients; OP/IP split |

### Patient experience and people
| Id | Title | Inputs | Definition |
|---|---|---|---|
| `feedback-nps` | Patient feedback and NPS | Period, Facility, Show by (department/channel/form), Department | **NPS = % promoters (9–10) − % detractors (0–6)** among responses to the recommend question; average rating (1–5); low scores flagged for follow-up |
| `hr-headcount` | Head count and attrition | Period (this financial year), Facility, Department | Head count = employees not separated who have joined; cadre from the designation; **attrition % = leavers ÷ head count × 100**. Drill: department → attendance |
| `hr-attendance` | Attendance and overtime | Period (last month), Facility, Department | From the nightly attendance computation: attendance % = present ÷ (present + absent + leave); overtime hours = overtime minutes ÷ 60 |

## Executive KPIs (`GET /kpis`, BI-01)

Each KPI returns this period, the previous period of the same length (ending the day before `from`), the change %,
`higherIsBetter`, the report it drills into and its definition; counts and money also return a daily trend.

| Key | Label | Unit | Definition | Report |
|---|---|---|---|---|
| `opVisits` | OP visits | count | OP, emergency, day-care, telehealth and health-check check-ins, excluding cancelled | `op-visit-trend` |
| `admissions` | Admissions | count | In-patient admissions (cancelled excluded) | `ip-admissions-discharges` |
| `occupancy` | Bed occupancy | percent | Inpatient days (midnight census) ÷ (beds in service × days) × 100 | `bed-occupancy` |
| `alos` | Average length of stay | days (lower is better) | Total LOS of discharges ÷ discharges | `alos` |
| `labTat` | Lab turnaround (median) | minutes (lower is better) | Median order → release minutes for tests ordered in the period | `lab-volume-tat` |
| `nps` | Net Promoter Score | score | % promoters − % detractors | `feedback-nps` |
| `revenue` | Revenue | inr (finance only) | Net billed on finalized bills + pharmacy sales less returns | `daily-revenue` |
| `collections` | Collections | inr (finance only) | Money received at billing and pharmacy counters less refunds paid | `collections-by-mode` |

KPI maths lives in `Domain/KpiMath.cs`, `Domain/CensusMath.cs` and `Domain/Percentiles.cs` (unit-tested): occupancy,
ALOS (minimum one day), bed turnover rate and interval (never negative), gross/net death rate, NPS, change %, no-show
rate, per-thousand rates, margin %, nearest-rank percentiles and within-target %.

## Exports (MIS-05)

`Reports/Export/ReportExporter.cs` (ClosedXML for Excel), `Reports/Export/ReportPdf.cs` (PDF layout) and
`Reports/Export/ReportFileWriter.cs`, which the export endpoint and scheduled e-mails both write through:

* **Excel**: header block with the hospital (facility letterhead name, or the organisation for consolidated
  reports), report title, every input in words (period, facility, filters), *Generated by* (name and user name) and
  time (IST), data source; headline figures; the table with typed number formats (₹ with Indian grouping, %, dates),
  auto-filter, frozen header, totals row, notes and the definition; a red *Confidential* notice at the end; printed
  page header "hospital — title", page footer "Confidential", landscape, fit to width, header row repeated on every page.
* **CSV**: UTF-8 with BOM, CRLF, the same header lines, rows, totals, notes, definition and notice. Every text field is
  RFC 4180 quoted when needed and **formula injection is neutralised** (a leading `= + - @`, tab or CR gets a `'`
  unless the value is a number).
* **PDF**: an A4 document in the house document style (the platform document layer), made by the server's PDF
  renderer (headless Edge). The facility letterhead with its logo, named as in the Excel header (for a consolidated
  report the organisation, with the logo only); the title; every input in words, *Generated by*, *Generated on* (IST)
  and the data source; headline figures; the table; notes; the definition; the confidentiality notice; "— End of
  report —". Column headings repeat on every page. Number columns are right-aligned in the Excel formats with Indian
  digit grouping: `1,23,456` (integers), `₹1,23,456.00` (money; negative amounts also in red), `30.1%`, `1,234.5`
  (decimals, days, minutes). Dates print as `15 Sep 2026`, months as `Sep 2026`, times as `15 Sep 2026, 3:45 pm`
  (IST). The totals row closes the table once (it is not repeated as page totals). Every page has the running footer
  "report title · organisation · generated date, time · Confidential" and "Page X of Y".
* **PDF orientation**: landscape when the table has more than 9 columns, or when its columns are wider together than a
  portrait line holds (120 characters of the table's 8.5 pt type). Each column counts its widest value or the longest
  word of its heading, text at most 40 characters (it wraps), plus 2 for padding. Otherwise portrait.
* **PDF row limit**: a PDF lists at most **2,000 rows** (about 40 to 60 pages, kept small enough for the renderer's
  30-second limit; aggregate reports stop at 5,000 rows anyway). A longer report is cut, not refused, so a scheduled
  PDF e-mail still goes out on the day its report runs long: the PDF lists the first 2,000 rows in the order on screen
  and says at the top and in its notes "The PDF lists the first 2,000 of N rows; the Excel export has every row." (and
  "The totals are for all rows."). The download carries the same text in the header `X-Report-Notice`, and the report
  result's `pdfNotice` gives it to the screen before the export.
* **When PDFs can't be made** (no Edge on the server, or it failed or took too long): the export answers 503 with the
  code `pdf.unavailable` and the reason ("… Export to Excel or CSV instead."), as every document PDF does. Nothing is
  audited, since nothing was exported.
* Sorted as on screen; the whole report (not just the page). File name `{reportId}_{yyyyMMdd}[-{yyyyMMdd}].xlsx|csv|pdf`.
* Patient identifiers appear only in patient-level reports, which need `analytics.clinical.read`; aggregate reports never
  contain patient columns. A PDF of a patient list keeps the Excel rules: the same permission, the patient-information
  notice, "Confidential" on every page, never e-mailed, and a PHI audit entry for every patient in the file (for a PDF
  cut at 2,000 rows, exactly the patients in those rows).

## Scheduled delivery (BI-04)

* **Job**: `ScheduledReportJob` (hosted `BackgroundService`, registered by the module) runs only where
  `PlatformOptions.RunsJobs` is true. Every minute it takes up to 10 due schedules (`is_active and next_run_at <= now`),
  claims each by moving `next_run_at` forward under optimistic concurrency (`xmin`), so two servers never send the same
  e-mail; a schedule missed while the server was down sends once on catch-up, not once per missed slot.
* **Run**: `ReportDeliveryService` re-checks the owner (active staff user with the report's permissions and access to
  the schedule's facility, else the run is *Skipped* with the reason), runs the report for the schedule's facility and
  relative period, writes the file in the schedule's format (Excel, CSV or PDF), stores it **encrypted**
  (`IFieldProtector`, purpose `analytics.delivery`) in `analytics.report_deliveries`, creates one recipient row per
  address and one outbox message (`analytics.report.email`) per recipient in the same transaction. The delivery's row
  count is the rows in the file (at most 2,000 for a PDF).
* **PDF failures**: when the PDF cannot be made, the delivery is *Failed* with the reason, as when the report cannot
  run: the schedule's last status and error show it, nothing is e-mailed, the schedule runs again at its next time, and
  *Send now* tries again at once (422 `analytics.delivery_failed` with the reason if it still fails).
* **E-mail**: `ScheduledReportEmailHandler` (outbox handler) renders the template `analytics.scheduled_report`
  (seeded; subject `{{report}} — {{period}} ({{hospital}})`) and sends it through the platform `IEmailSender` with
  the file **attached** when it is at most 10 MB (PDFs too); larger files are sent as a notice to download it from
  HC360 (*Delivery history* → download, audited). Failed sends throw so the outbox retries with back-off; each recipient's
  attempts and last error are kept. Idempotent per recipient.
* **Why not the notification service**: `INotificationService.Queue` has no attachment support and `IFileStore` accepts
  only clinical document types, so the module owns this small handler (see *Maintainers*).
* **Retention**: file contents are purged after `analytics.deliveries.retentionDays` (default 30) by the same job every
  6 hours; the delivery rows (who, when, what) remain.

## BI extracts (BI-05)

A BI extract is a schedule whose file goes to a folder instead of e-mail: the report's rows as a CSV file that Power BI
(or Tableau, Qlik, Excel) reads, replaced at every run so the BI tool always reads the latest data. Screen: Analytics ›
BI extracts.

* **Folder** (`ExtractFolder`): `HealthCare360:Analytics:ExtractDirectory` in the server's configuration (on a server,
  the machine environment variable `HC360_HealthCare360__Analytics__ExtractDirectory`; `%VARIABLES%` in it are
  expanded, and a relative path is taken from the data folder), by default `bi-extracts` in the data folder
  (`%ProgramData%\HyFib\HealthCare360\bi-extracts`).
  Each facility has a sub-folder named by its code, so `NH\daily-revenue.csv`. Only the server's settings choose the
  folder; the staff app chooses file names, which are checked, so nothing is written outside it. IT shares the folder,
  read-only, with the BI tool (`docs/deployment/DEPLOYMENT.md`, *BI tools*).
* **File** (`ReportExporter.ExtractCsv`): UTF-8 with a byte-order mark, CRLF lines, a header row (`Facility code`, then
  the report's column labels) and one row per report row with the facility's code first, so the files of several
  facilities combine. No title, inputs, totals or notes (a BI tool adds rows up itself). As in CSV exports, numbers
  are plain (`55000.50`), dates `yyyy-MM-dd` (months `yyyy-MM`, times `yyyy-MM-dd HH:mm` in IST), and a cell that is
  not a number and starts with `=`, `+`, `-`, `@`, a tab or a carriage return is prefixed with `'` (formula injection). The file is written to `.writing` in the folder and moved over the old one, so a
  reader never sees half a file; a file held open by a reader is retried for about a second.
* **Row limit**: an extract's report reads up to 100,000 rows (`ReportRunner.ExtractRowCap`; screens and e-mails stop
  at 5,000), and the names of what it groups by (items, doctors, wards) are read up to the same limit (at least 20,000).
  It is never served from or kept in the one-minute report cache. A report that still reaches a limit is
  not written: the refresh fails ("The report reached its row limit, so the file would leave out data. Narrow the
  extract's inputs…") and the last complete file stays, so a BI tool never reads part of the data unknowingly.
* **Run**: the same job, claim and owner check as e-mails (*Scheduled delivery*). A run writes the file, records a
  delivery (status `Ready`, rows, size and period; the content is not kept in HC360, so `fileAvailable` is false),
  sets the schedule's `lastSucceededAt`, and audits it as an export (`analytics.report.exported`, "BI extract
  NH\daily-revenue.csv"). A file that cannot be written fails the run with the reason ("The file could not be written to
  the extract folder (…). Ask IT to check the folder and its permissions."); the previous file stays.
* **Refresh log** (`ExtractMonitor`): `refresh-log.csv` at the top of the folder, rewritten after every run, save and
  delete: one line per extract of every facility with `Facility code, Extract, File, Report, Refresh, Last refreshed
  (IST), State, Problem`, for a "data as of" card in the BI report. A log that cannot be written is logged and does not
  fail the run.
* **State** (`ReportSchedule.State`): `Paused` (not active), `Failing` (the last run failed or was skipped), `Late` (no
  file for longer than one interval of the schedule plus 30 minutes, counted from the last file, or from when the
  extract was set up or last saved: the server was off or its jobs are not running), `Waiting` (no file yet), else
  `Fresh`. Changing an extract's file name, report or inputs clears `lastSucceededAt`, so it waits for the refresh that
  writes the new file; a new name removes the old file, so a BI report says it is missing rather than reading data
  that is no longer refreshed.
* **Alerts**: when an extract starts failing (not again at each failed run), everyone with `analytics.schedules.manage`
  at the facility gets the in-app notification "BI extract "…" was not refreshed" (warning, link `/analytics/extracts`)
  with the reason.
* **Several servers**: only nodes that run jobs refresh on schedule, but *Refresh now* writes from the node that
  answers it, so every node must have the same shared folder (a UNC path) in its settings.

## Settings and configuration

| Key | Default | Purpose |
|---|---|---|
| `ConnectionStrings:Analytics` (or `ConnectionStrings:Reporting`) | not set | Read replica for reports and KPIs (BI-05) |
| `HealthCare360:Analytics:ExtractDirectory` (server configuration, e.g. `HC360_HealthCare360__Analytics__ExtractDirectory`) | `bi-extracts` in the data folder | Folder BI extracts are written to (*BI extracts*) |
| `analytics.schedules.allowedEmailDomains` (setting, string[]) | not set = any | Only these e-mail domains may receive scheduled reports, e.g. `["nanahospitals.in"]` |
| `analytics.deliveries.retentionDays` (setting, int) | 30 | Days a delivered file can be downloaded again |

## Data (schema `analytics`)

| Table | Purpose |
|---|---|
| `dashboards` | name, description, owner (null = hospital template), shared flag, `layout_json` (jsonb) · index (owner_user_id, name), is_shared |
| `report_schedules` | report, `parameters_json` (jsonb), format, frequency, time (IST), weekday / day of month, recipients, active, owner, next/last run, last status/error, `destination` (`Email` or `ExtractFolder`), `extract_name`, `last_succeeded_at` · facility scoped, soft delete, index (is_active, next_run_at), owner_user_id, unique (facility_id, extract_name) for extracts not deleted |
| `report_deliveries` | one generated file: trigger, period, status, encrypted content (purged after retention), size, rows, error · facility scoped · index (schedule_id, created_at), created_at |
| `report_delivery_recipients` | one row per recipient: status, attempts, last error, sent at · cascade from deliveries |

Seed (`AnalyticsSeeder`, order 190): the e-mail template and two dashboard templates, *Hospital operations* (OP
visit trend, bed occupancy, admissions and discharges, lab volume and TAT, discharge outcomes, NPS) and *Finance
overview* (revenue and collections trend, revenue by department, collections by mode, revenue by payer, outstanding
ageing).

## Audit

| Event | Category | When |
|---|---|---|
| `analytics.report.viewed` + `patient.accessed` per patient | PhiAccess | each page of a patient-level report opened |
| `analytics.report.exported` (+ `patient.accessed` per patient for patient lists) | Data / PhiAccess | every export (report, format, inputs, rows in the file), and every BI extract written; a PDF that could not be made is not audited |
| `analytics.delivery.downloaded` | Data | a delivered file downloaded again |
| data audit | Data | dashboards and schedules (create/change/delete) via the platform interceptor |

## Staff app

| Route | Screen |
|---|---|
| `/analytics` | Executive dashboard: period (presets or custom dates) and facility, KPI tiles with change vs previous period (arrow + words + colour), sparklines, definitions, daily trend chart, dashboards and featured report shortcuts; each tile opens its report for the same period |
| `/analytics/reports` | Report catalogue by area with search (title, description, formula), counts per area, patient-list badges |
| `/analytics/reports/$reportId` | Report inputs (applied as you change them; kept in the URL so views can be bookmarked and shared), headline figures, chart with a view switch, sortable/paged table with totals, row drill-down, notes, “How it is calculated”, Excel/CSV export (patient lists confirm first), add to dashboard, e-mail regularly |
| `/analytics/dashboards` | Templates, mine and shared; new, copy, delete |
| `/analytics/dashboards/$dashboardId` | Tiles (chart, table or headline figures; small/medium/full width) with a period override for all tiles; edit mode to add, change, reorder, resize and remove tiles with a live preview; unsaved-change guard |
| `/analytics/schedules` | Scheduled e-mails: status, list, create/change drawer (relative periods, IST time, recipients), send now, pause/resume, delivery history with re-download |
| `/analytics/extracts` | BI extracts: active, late and failing extracts and the last refresh; the facility's folder and the refresh log (with copy buttons) and how to connect Power BI, or why HC360 cannot write the folder; list with file, schedule, data as of and state; the same drawer with a file name instead of recipients (CSV only, hourly allowed); refresh now, pause/resume, refresh history, delete |

Home widget *Hospital at a glance* (this month's first four KPIs) for any analytics reader.

## Tests

`tests/Modules/HyFib.HealthCare360.Modules.Analytics.Tests` (xUnit + Shouldly):
KPI maths, census days, IST bucketing and period presets, schedule calendar, nearest-rank percentiles and TAT summary,
parameter binding (every rule), row sorting/paging/charting, CSV/Excel export (header, footer, formula injection,
number formats), the PDF export (`ReportPdfTests`: headings, number formats and alignment, totals, the landscape rule,
the row limit and its notice, encoding of hostile text, consolidated letterhead, export format parsing, schedule
format validation, the 503 answer, and the patients audited for a cut PDF), catalogue consistency and visibility per
role, dashboard layout, model (tables, indexes, jsonb,
facility filters), and SQL translation of **every report** with defaults, filters and each choice (`ToQueryString`:
facility and soft-delete filters present, IST interval used, no enum ordering).
BI extracts: `ExtractFolderTests` (names, the folder setting, whole-file replacement, nothing outside the folder, an
unwritable folder reported), the extract CSV, hourly schedules and extract states, the unique file name index; and the
end-to-end `BiExtractTests` in `tests/HyFib.HealthCare360.IntegrationTests` (rules, file and refresh log written, rename
and delete, a failing refresh alerting once). `AnalyticsModuleTests` checks that the server's catalogue has every
report.
Web: `format.test.ts`, `params.test.ts`, `charts.test.ts`, `schedule-form.test.ts` in the module folder.

## Maintainers

* **Migration needed** for the `analytics` schema (four tables above); the module adds no columns to other schemas.
  Formats are stored as text (`format`, varchar 64), so `Pdf`, added after `Xlsx` and `Csv`, needed no migration; add
  any new format at the end of `ExportFormat` too.
* **Contract gaps**: e-mail attachments are not supported by `INotificationService` (the module's outbox handler uses
  `IEmailSender` directly); `IFileStore` rejects XLSX/CSV, so delivered files are kept encrypted in the module's table.
  Adding attachment support to notifications would let this handler go.
* The shared `EChart` wrapper exposes no events, so drill-down is from table rows (charts are visual only).
