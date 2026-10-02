# Number formats (UHIDs, bills, accession numbers and the other numbers HC360 issues)

Platform feature: `src/server/HyFib.HealthCare360.Platform/Numbering` · table `platform.number_series` · screen
**Hospital setup → Number formats** (`/setup/numbering` in the staff app) · permission `admin.numbering.manage`
(HospitalAdmin by default).

Every kind of number HC360 issues is a **number series**: UHIDs, visit numbers, OP and IP bills, receipts, lab accession
numbers, purchase orders and so on, 54 in all. Each module declares its series, so all of them are on the screen from
the first start, before a single number is issued. Administrators can set the format, when numbers start again from 1,
and the next number of each series, before go-live or later. A change applies to numbers issued from then on: numbers
already issued never change.

## How a number is made

A format is fixed text and parts in braces. Each number fills in the parts at the moment it is issued.

| Part | Becomes | Example |
|---|---|---|
| `{FAC}` | the facility code (2 to 6 capital letters, set in the facility master) | `NH` |
| `{YYYY}` | the year, 4 digits | `2026` |
| `{YY}` | the year, 2 digits | `26` |
| `{MM}` | the month, 2 digits | `10` |
| `{DD}` | the day of the month, 2 digits | `02` |
| `{SEQ}` | the running number | `124` |
| `{SEQ:n}` | the running number padded with zeros to n digits (n from 1 to 12) | `{SEQ:7}` → `0000124` |

So `{FAC}OB{YY}{SEQ:7}` makes `NHOB260000124`, and `{FAC}/OP/{YYYY}/{SEQ:6}` makes `NH/OP/2026/000124`. The running
number is never cut short: once it outgrows its padding it simply has more digits.

Dates are India Standard Time. A series shared by all facilities fills in `{FAC}` with the code of the first facility set
up (the hospital's main one).

Under the GST rules, the serial number of an invoice or a bill of supply has at most 16 characters (letters, digits, `-`
and `/`) and is unique in a financial year. Keep the formats of bills, pharmacy invoices and canteen bills within 16
characters for the facility codes in use.

## Starting again from 1

| Choice | Numbers start again |
|---|---|
| Never | never: one running count |
| Every year | on 1 January |
| Every month | on the 1st of each month |
| Every day | at midnight |

Numbers that start again must contain the date of their period, or the same numbers would be issued again: the year
for every year, the year and month for every month, the year, month and day for every day.

Changing when a series starts again never issues a number twice. Counting carries on in the current period and starts
again from 1 only at the next boundary. For example, an OP bill series at bill 500 of 2026, changed in October to
`{FAC}OB{YY}{MM}{SEQ:4}` starting again every month, issues `NHOB26100501` next, and `NHOB26110001` on 1 November.

## The rules a format must follow

The screen checks these as the administrator types, with the same words the server uses when it checks them again on
saving.

* Only the parts above, and one running number (`{SEQ}` or `{SEQ:n}`).
* Fixed text uses capital letters A to Z, digits, `-` and `/`; no spaces.
* The date parts of the period, when numbers start again (see above).
* `{FAC}`, for numbers each facility counts on its own, once the hospital has more than one facility. Number columns
  are unique across all facilities, so without the facility code two facilities would issue the same numbers.
* The numbers must fit where they are stored. The longest number is measured with the longest facility code in use and
  a running number of at least 8 digits (it grows over the years). Most numbers have room for 24 characters; UHIDs, tube
  barcodes, donor numbers and employee codes for 20. The message says which part to shorten.
* A changed format must not be able to make the numbers of another series (OP and IP bills share one table, so that
  would issue a bill number twice), nor those this series makes at another facility (number columns are unique across
  facilities). Two formats can make the same number when their characters (letters, `-`, `/`, and the facility code
  that `{FAC}` stands for) are the same in the same places, and each run of digits (the date parts, the running number
  and digits in the fixed text) can come out the same: lengths that can meet, and the same fixed digits where both
  start or end with some.
  - **Clash:** `{FAC}OB{YY}{SEQ:7}`, `{FAC}OB{SEQ:9}` and `{FAC}OB26{SEQ}` (`{SEQ:6}` reaching 1000000 makes the
    numbers of `{SEQ:7}`), and `{SEQ}` with `{SEQ:1}`.
  - **Clash at facility AB:** `{FAC}AB{YY}{SEQ:7}` and `AB{FAC}{YY}{SEQ:7}`, and `{FAC}-{SEQ}` and `AB-{SEQ}`.
    `{FAC}` is the facility's own code for a per-facility series and the main facility's for a shared one.
  - **Clash across facilities:** AB's `{FAC}COB…` and ABC's `{FAC}OB…`.
  - **Never clash:** `B1{SEQ}` and `B2{SEQ}`, or one format at facilities NH1 and NH2.

  The editor checks the facility you are signed in to as you type; saving checks every facility. Restoring a default
  is always allowed:
  refunds and pharmacy refill requests share the default `{FAC}RF{YY}{SEQ:6}`, and credit notes and canteen bills both
  start `{FAC}CN{YY}`, but each pair is kept in different tables.
* A stored running number padded to more than 12 digits (possible only from before these rules) is padded to 12 when
  numbers are made, rather than to the width written.

A format that already differs from these rules (for example a visitor pass format without `{FAC}` in a hospital that
has since opened a second facility) is checked in full when its format or reset is changed. Setting only its next
number still works.

## Locked series

Other systems read these numbers by their shape, so their formats are fixed. Their next number can still be raised,
for example to carry on from an older system, within the padding of the running number so that the shape stays.

| Series | Why |
|---|---|
| UHID (`uhid`) | The AI assistant recognises UHIDs by their shape (the facility code followed by 8 digits) to find patients and to keep their details private. |
| Sample tube barcode (`lab.sample`) | Laboratory analysers and barcode scanners read tube barcodes as plain 9-digit numbers. |
| Lab accession number (`lab.accession`) | Barcode scanners on the laboratory screens recognise accession numbers by their shape. |
| Radiology accession number (`radiology.accession`) | Barcode scanners on the diagnostics screens recognise them by their shape. |
| Cardiology accession number (`cardiology.accession`) | Barcode scanners on the diagnostics screens recognise them by their shape. |

A department's daily queue tokens (`token:{departmentId}`, `{SEQ}`, every day) are numbers too, but internal: they are
not listed and cannot be changed.

## The next number

Leave **Next number** empty to carry on counting. To continue from an older system, enter the next number it would have
issued. It must be above the last number issued in the current period (this year for yearly numbers, and so on) and
have at most 8 digits (or as many as `{SEQ:n}` pads to, if more). A later number leaves a gap in the series; the change
is recorded in the audit trail as a warning, because auditors of bills and receipts ask about gaps.

## Who sees what

The list shows the series of the facility the administrator is signed in to, and the series shared by all facilities
(UHID, tube barcodes, blood donors and data breaches). A facility's change applies to that facility only (switch
facility to set up another one); a shared series' change applies everywhere. Each row shows the format, the next
number as it would be issued now, when numbers start again, and how many have been issued in the current period.
Customised series and locked ones are marked.

## API

| Method | Path | Permission | Body → Response |
|---|---|---|---|
| GET | `/api/v1/admin/number-series` | admin.numbering.manage | → `NumberSeriesList {facilityCount, longestFacilityCode, series: NumberSeriesItem[]}`, by area then name |
| PUT | `/api/v1/admin/number-series/{key}` | admin.numbering.manage | `SaveNumberSeriesRequest {format, resetPolicy, startAt?, expectedFormat, expectedResetPolicy}` → 204 |

`NumberSeriesItem {key, name, area, scope: "facility"|"global", format, resetPolicy: Never|Yearly|Monthly|Daily,
lastNumber (the running number last issued in the current period, 0 if none), nextExample (the number the next
allocation would make now), facilityCode, isCustomised, isLocked, lockedReason, maxLength, defaultFormat,
defaultResetPolicy}`. Hidden series are left out; a series with no row yet shows its defaults.

The PUT creates the series' row if it has none yet, so formats can be set before the first number. `expectedFormat` and
`expectedResetPolicy` are the values the editor started from: if they differ from those in use, someone else changed
the series meanwhile. A save that meets a number being issued at the same moment is retried, not refused.

| Status | Code | When |
|---|---|---|
| 400 | `validation` | a rule above is broken; the messages are on `format`, `resetPolicy` or `startAt` |
| 404 | `numbering.not_found` | no such series (or an internal one) |
| 409 | `numbering.changed` | the format or reset is no longer the one the editor started from |
| 409 | `numbering.busy` | numbers kept being issued while saving, five times over; save again |
| 422 | `numbering.locked` | the format or reset of a locked series was changed |
| 422 | `facility.required` | no facility is selected (needed for per-facility series) |

Every save writes an admin audit entry `numbering.changed` with the format, the reset and the next number before and
after (a warning when the next number was set), besides the data audit of the row.

## Declaring a series (developers)

Each module lists its series in `<Module>NumberSeries` (for example `BillingNumberSeries`) and returns them from
`IModule.NumberSeries`. Every allocation passes the definition, never a key:

```csharp
public static NumberSeriesDefinition LabAccession { get; } =
    new("lab.accession", "Lab accession number", "Laboratory", "{FAC}LB{YY}{SEQ:7}", NumberResetPolicy.Yearly, MaxLength: 24)
    {
        LockedReason = "Barcode scanners on the laboratory screens recognise accession numbers by their shape, so the format cannot change.",
    };

order.AccessionNumber = await numbers.NextAsync(DiagnosticsNumberSeries.LabAccession, facilityId, ct);
```

* `Key` is the stable key of the counter; never rename it once numbers have been issued.
* `Name` and `Area` are what administrators read: plain words, the area as the staff app's menu names it.
* `MaxLength` is the length of the shortest column the number is stored or copied in.
* `PerFacility = false` for one counter shared by all facilities (`NextAsync` then ignores the facility).
* `LockedReason` for numbers other code or devices read by their shape; `Hidden` for internal counters, which may
  have a key per use (queue tokens).

`NextAsync` refuses a definition no module declares, and the architecture tests check that every definition is declared
and that every default format follows the rules above. Allocate inside the transaction that saves the document, so
numbers stay gap-free.

## All series

| Area | Number | Key | Default format | Starts again | Counted | Room |
|---|---|---|---|---|---|---|
| Appointments | Appointment | `appointment` | `{FAC}AP{YY}{SEQ:7}` | every year | per facility | 24 |
| Billing | Credit note | `billing.creditnote` | `{FAC}CN{YY}{SEQ:6}` | every year | per facility | 24 |
| Billing | IP bill | `billing.bill.ip` | `{FAC}IB{YY}{SEQ:7}` | every year | per facility | 24 |
| Billing | OP bill | `billing.bill.op` | `{FAC}OB{YY}{SEQ:7}` | every year | per facility | 24 |
| Billing | Receipt | `billing.receipt` | `{FAC}RC{YY}{SEQ:7}` | every year | per facility | 24 |
| Billing | Refund | `billing.refund` | `{FAC}RF{YY}{SEQ:6}` | every year | per facility | 24 |
| Blood bank | Blood bag | `blood-bag` | `{FAC}BB{YY}{SEQ:6}` | every year | per facility | 24 |
| Blood bank | Blood donor | `blood-donor` | `{FAC}DN{SEQ:6}` | never | shared | 20 |
| Blood bank | Blood request | `blood-request` | `{FAC}BR{YY}{SEQ:6}` | every year | per facility | 24 |
| Canteen | Canteen bill | `canteen-bill` | `{FAC}CN{YY}{SEQ:7}` | every year | per facility | 24 |
| Cardiology | Cardiology accession number (locked) | `cardiology.accession` | `{FAC}CD{YY}{SEQ:7}` | every year | per facility | 24 |
| Clinical | Clinical order | `clinical-order` | `{FAC}OR{YY}{SEQ:7}` | every year | per facility | 24 |
| Clinical | Prescription | `prescription` | `{FAC}RX{YY}{SEQ:7}` | every year | per facility | 24 |
| CSSD | CSSD request | `cssd-request` | `{FAC}CQ{YY}{SEQ:6}` | every year | per facility | 24 |
| CSSD | Sterilisation cycle | `cssd-cycle` | `{FAC}CS{YY}{SEQ:6}` | every year | per facility | 24 |
| Dialysis | Dialysis session | `dialysis-session` | `{FAC}DL{YY}{SEQ:6}` | every year | per facility | 24 |
| Doctor fees | Doctor payout | `doctorfees.payout` | `{FAC}DP{YY}{SEQ:5}` | every year | per facility | 24 |
| Equipment and assets | Asset tag | `supplychain.asset` | `{FAC}AS{SEQ:6}` | never | per facility | 24 |
| Equipment and assets | Maintenance ticket | `supplychain.ticket` | `{FAC}MT{YY}{SEQ:6}` | every year | per facility | 24 |
| Front office | Visitor pass | `visitor-pass` | `VP{YY}{MM}{DD}-{SEQ:4}` | every day | per facility | 24 |
| Health check-ups | Corporate check-up drive | `healthcheck-batch` | `{FAC}HCB{YY}{SEQ:4}` | every year | per facility | 24 |
| Health check-ups | Health check-up booking | `healthcheck-booking` | `{FAC}HC{YY}{SEQ:6}` | every year | per facility | 24 |
| Human resources | Employee code | `hr.employee` | `{FAC}EMP{SEQ:5}` | never | per facility | 20 |
| In-patients | Admission (IP number) | `admission` | `{FAC}IP{YY}{SEQ:6}` | every year | per facility | 24 |
| Insurance | Insurance claim | `insurance.claim` | `{FAC}CL{YY}{SEQ:6}` | every year | per facility | 24 |
| Insurance | Pre-authorisation | `insurance.preauth` | `{FAC}PA{YY}{SEQ:6}` | every year | per facility | 24 |
| Laboratory | Lab accession number (locked) | `lab.accession` | `{FAC}LB{YY}{SEQ:7}` | every year | per facility | 24 |
| Laboratory | Sample tube barcode (locked) | `lab.sample` | `{SEQ:9}` | never | shared | 20 |
| Medical records | Medical record request | `mrd-request` | `{FAC}RR{YY}{SEQ:6}` | every year | per facility | 24 |
| Operation theatre | Surgery booking | `ot-booking` | `{FAC}OT{YY}{SEQ:6}` | every year | per facility | 24 |
| Patient feedback | Complaint | `feedback.complaint` | `{FAC}CMP{YY}{SEQ:5}` | every year | per facility | 24 |
| Patient relations | Lead | `crm.lead` | `{FAC}LD{YY}{SEQ:5}` | every year | per facility | 24 |
| Patients | UHID (patient number) (locked) | `uhid` | `{FAC}{YY}{SEQ:6}` | never | shared | 20 |
| Patients | Visit number (out-patient visits and admissions) | `visit` | `{FAC}V{YY}{SEQ:7}` | every year | per facility | 24 |
| Pharmacy | Pharmacy invoice | `pharmacy.invoice` | `{FAC}PH{YY}{SEQ:7}` | every year | per facility | 24 |
| Pharmacy | Refill request | `pharmacy.refill` | `{FAC}RF{YY}{SEQ:6}` | every year | per facility | 24 |
| Pharmacy | Sale return | `pharmacy.return` | `{FAC}PHR{YY}{SEQ:6}` | every year | per facility | 24 |
| Pharmacy | Ward indent | `pharmacy.ward_indent` | `{FAC}WI{YY}{SEQ:6}` | every year | per facility | 24 |
| Pharmacy | Ward issue | `pharmacy.ip_issue` | `{FAC}IPI{YY}{SEQ:6}` | every year | per facility | 24 |
| Pharmacy | Ward return | `pharmacy.ward_return` | `{FAC}WR{YY}{SEQ:6}` | every year | per facility | 24 |
| Privacy | Data breach | `privacy.breach` | `DPB{YY}{SEQ:4}` | every year | shared | 24 |
| Privacy | Data request | `privacy.request` | `{FAC}DPR{YY}{SEQ:5}` | every year | per facility | 24 |
| Purchase | Goods receipt (GRN) | `supplychain.grn` | `{FAC}GRN{YY}{SEQ:6}` | every year | per facility | 24 |
| Purchase | Purchase order | `supplychain.po` | `{FAC}PO{YY}{SEQ:6}` | every year | per facility | 24 |
| Purchase | Purchase requisition | `supplychain.pr` | `{FAC}PR{YY}{SEQ:6}` | every year | per facility | 24 |
| Quality and safety | Corrective action (CAPA) | `quality.capa` | `{FAC}CAPA{YY}{SEQ:4}` | every year | per facility | 24 |
| Quality and safety | Incident report | `quality.incident` | `{FAC}IR{YY}{SEQ:5}` | every year | per facility | 24 |
| Quality and safety | Risk | `quality.risk` | `{FAC}RSK{SEQ:4}` | never | per facility | 24 |
| Radiology | Radiology accession number (locked) | `radiology.accession` | `{FAC}RD{YY}{SEQ:7}` | every year | per facility | 24 |
| Stores and inventory | Stock adjustment | `supplychain.adjustment` | `{FAC}ADJ{YY}{SEQ:6}` | every year | per facility | 24 |
| Stores and inventory | Stock count | `supplychain.count` | `{FAC}PSC{YY}{SEQ:5}` | every year | per facility | 24 |
| Stores and inventory | Stock transfer | `supplychain.transfer` | `{FAC}STN{YY}{SEQ:6}` | every year | per facility | 24 |
| Stores and inventory | Store indent | `supplychain.indent` | `{FAC}IND{YY}{SEQ:6}` | every year | per facility | 24 |
| Stores and inventory | Store issue | `supplychain.issue` | `{FAC}ISS{YY}{SEQ:6}` | every year | per facility | 24 |

Employee codes have room for 20 characters because the canteen's staff credit keeps them that long. The visitor pass
default has no `{FAC}`: a hospital with more than one facility should add it, or two facilities would issue the same
pass numbers on the same day.
