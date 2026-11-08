# When HC360 is down (business continuity)

NABH's information management standards (IMS, 6th edition) expect a hospital to keep caring for patients when its
information system is unavailable and to bring what was done on paper back into the system afterwards; JCI's MOI
standards ask the same: a plan, staff who know it, paper records kept during the downtime, and the data entered and
checked once the system is back. This page describes what HC360 gives the hospital for that. The hospital's own
downtime procedure, adopted by its quality team, decides who does what.

> Check the clause numbers against the hospital's licensed copy of the NABH standards before citing them in the
> hospital's procedure: NABH does not publish the 6th edition text openly.

## Downtime UHIDs (registration on paper)

A patient registered while HC360 is down must still leave with the UHID they will keep: it goes on their card, their
wristband, their lab samples and every paper form. So each registration desk keeps a **downtime kit**: blank paper
registration forms and a printed sheet of UHIDs issued to the kit in advance.

1. **Issue a block** (*Patients → Downtime UHIDs → Issue UHIDs*, permission `patients.downtime`, held by hospital
   administrators and medical records officers). Name the kit ("Main reception, kit 2") and say how many UHIDs it
   needs, at most 200: about a day's registrations at that desk. HC360 takes them from the UHID series at once, so it
   never gives them to anyone else.
2. **Print the sheet** and keep it with the forms. Each UHID is printed with its barcode, to cut out for the
   patient's card or wristband, and a line for the patient's name and the time.
3. **During downtime**, the desk registers each new patient on a paper form under the next UHID on the sheet, and
   writes the date and time on the form. A spoilt label is crossed out and marked "void".
4. **When HC360 is back**, the desk enters each form on *Register patient* after pressing *Paper form* at the top of
   the page, with the UHID on the form and the time on the form. The patient keeps that UHID and
   the record shows the time on the form as the registration time. The duplicate check still runs: a patient who was
   already registered should not be registered again.
5. **Close the block**: the block shows each UHID as *Entered* (with the patient and when and by whom it was
   entered), *Voided* (with the reason) or *Not entered*. Void the numbers that were spoilt or never used, so that a
   block with nothing left *Not entered* is fully accounted for.

HC360 refuses an entry when:

| The form's UHID | Answer |
|---|---|
| was not issued for a downtime kit | 400 on `paper.uhid`: check the number on the form |
| has already been entered | 409 `downtime.uhid_entered`: two forms carry the same UHID, or the form was entered already |
| was voided | 400 on `paper.uhid`, with the reason it was voided |
| is dated before its block was issued, or in the future | 400 on `paper.registeredAt` |
| is dated before the registration's back-entry window (90 days unless the administrator sets less), with no late back-entry approval covering it | 422 `backentry.late` (see *Back-entry windows*) |

Issuing a block (`patients.downtime_uhids_issued`), voiding a UHID (`patients.downtime_uhid_voided`) and entering a
registration from paper (`patients.registered_from_paper`, with the patient) are written to the audit trail.

### API

| Method | Path | Permission | Body → answer |
|---|---|---|---|
| GET | `/api/v1/patients/downtime-blocks` | `patients.downtime` | The facility's last 100 blocks, newest first: `DowntimeUhidBlockSummary {id, label, count, firstUhid, lastUhid, issuedAt, issuedByName, entered, voided, open}` |
| GET | `/api/v1/patients/downtime-blocks/{id}` | `patients.downtime` | `DowntimeUhidBlockDetail {block, facilityName, numbers[{position, uhid, status (Open, Entered, Voided), patientId, patientName, paperRegisteredAt, enteredAt, enteredByName, voidedAt, voidReason, voidedByName, version}]}` |
| POST | `/api/v1/patients/downtime-blocks` | `patients.downtime` | `IssueDowntimeUhidsRequest {label, count (1–200)}` → 201 `DowntimeUhidBlockDetail` |
| POST | `/api/v1/patients/downtime-blocks/{id}/void` | `patients.downtime` | `VoidDowntimeUhidRequest {uhid, reason, version}` → 204; 422 `downtime.uhid_entered` for an entered UHID |
| POST | `/api/v1/patients` | `patients.register` | `RegisterPatientRequest` with `paper {uhid, registeredAt}` for a registration from paper |

The numbers are reserved with `INumberSeries.ReserveAsync`, which moves the series' counter on by the block's size in
one statement, as issuing one number does.

## Downtime visit, bill and receipt numbers

A visit slip, a bill or a receipt written on paper during a downtime must carry the number it will keep: the patient
leaves with the receipt, a GST bill's serial number cannot change afterwards, and the paper has to be matched to the
record entered. So, as for UHIDs, each counter holds **blocks of numbers issued ahead**, printed on its sheet and on the
pre-downtime pack.

1. **Issue a block** (*Patients → Downtime continuity → Issue numbers*, permission `patients.downtime`): what the
   numbers are for (visits, OP bills, IP bills or receipts), the counter that keeps them ("OPD billing counter 1") and
   how many, at most 500. HC360 reserves them from the same series the counter would use (`INumberSeries.ReserveAsync`),
   so it never issues them again, and records the series' period at issue (the year for a yearly series).
2. **Print the sheet** for the counter: each number in order with lines for the patient, the time and the amount.
3. **During downtime** the counter uses the numbers **in order**, one per paper, and writes the date and time on it.
4. **When HC360 is back** each paper is entered with its number and the time on it:
   - a visit through *Check in* with `paper {number, at}` (no queue token: the patient has been seen);
   - a bill through *Finalize* with `paper {number, at}`: it keeps the number and is dated at the time on the paper;
   - a receipt through *Collect payment* with `paper {number, at}`: it keeps the number and the time; the money is
     counted into the cashier's shift open at entry.
5. **Return the block** when it is no longer needed: every number not entered is voided as "Returned unused", in one
   audited step, and the block takes no more entries. A single spoilt number is voided on its own with a reason.

HC360 refuses a number from paper when:

| The paper's number | Answer |
|---|---|
| was not issued for that kind at this facility (a receipt number on a bill, a number issued normally) | 400 on `paper.number` |
| has already been entered | 409 `downtime.number_entered` |
| was voided, or its block returned | 400 on `paper.number`, with the reason |
| is dated before its block was issued, or in the future | 400 on `paper.at` |
| is dated in another period of its series than the one it was issued in (a yearly series' number used on paper of the next year) | 400 on `paper.at`: a number of the wrong year or month cannot be entered |
| is out of order: dated after a later number of the block already entered, or before an earlier one | 400 on `paper.at`, naming the other number |
| is older than its record's back-entry window, with no late back-entry approval covering it | 422 `backentry.late` |

Issuing (`patients.downtime_numbers_issued`), voiding (`patients.downtime_number_voided`), returning
(`patients.downtime_block_returned`, with how many were entered and voided) and each entry from paper
(`patients.entered_from_paper`, Warning when entered late under an approval) are written to the audit trail.

| Method | Path | Permission | Body → answer |
|---|---|---|---|
| GET | `/api/v1/downtime/number-blocks?kind` | `patients.downtime` | The facility's last 100 blocks: `DowntimeNumberBlockSummary {id, kind, kindName, counter, count, firstNumber, lastNumber, issuedAt, issuedByName, entered, voided, open, returnedAt, returnedByName, returnNote, version}` |
| GET | `/api/v1/downtime/number-blocks/{id}` | `patients.downtime` | `DowntimeNumberBlockDetail {block, facilityName, numbers[{position, number, status, entityType, entityId, patientId, patientName, paperAt, enteredAt, enteredByName, lateBackEntry, voidedAt, voidReason, voidedByName, version}]}` |
| POST | `/api/v1/downtime/number-blocks` | `patients.downtime` | `IssueDowntimeNumbersRequest {kind (Visit, OpBill, IpBill, Receipt), counter, count (1–500)}` → 201 |
| POST | `/api/v1/downtime/number-blocks/{id}/void` | `patients.downtime` | `{number, reason, version}` → 204; 422 `downtime.number_entered` |
| POST | `/api/v1/downtime/number-blocks/{id}/return` | `patients.downtime` | `{note?, version}` → the block; 422 `downtime.block_returned` when already returned |
| POST | `/api/v1/visits` | `visits.manage` | `CreateVisitRequest` with `paper {number, at}` and `issueToken: false` |
| POST | `/api/v1/billing/bills/{id}/finalize` | `billing.bills.finalize` | `FinalizeBillRequest {version, paper?}` |
| POST | `/api/v1/billing/receipts` | `billing.payments.collect` | `CollectPaymentRequest` with `paper?` |

## Back-entry windows and late back-entry

How far back the time on a paper record (or a time typed in for the past) may be is set **per record type** by the
administrator, as a setting in hours, *Administration → Settings*:

| Record | Setting | Default | At most |
|---|---|---|---|
| Registration from paper | `downtime.backEntryHours.registration` | 90 days | 90 days |
| Visit from paper | `downtime.backEntryHours.visit` | 72 hours | 14 days |
| Bill from paper | `downtime.backEntryHours.bill` | 72 hours | 14 days |
| Receipt from the manual receipt book | `downtime.backEntryHours.receipt` | 72 hours | 14 days |
| Admission time | `downtime.backEntryHours.admission` | 72 hours (the 3 days it was) | 14 days |
| Bed transfer time | `downtime.backEntryHours.transfer` | 24 hours | 14 days |
| Consultant's ward visit time | `downtime.backEntryHours.consultantVisit` | 48 hours | 14 days |
| Nursing notes and intake/output | `downtime.backEntryHours.nursingChart` | 24 hours | 14 days |

A value outside 1 hour to the maximum acts as the default. **Lengthening a window waits for a second administrator**
(SettingGuard, `settings.change`); shortening it applies at once. The windows in force are listed at
`GET /api/v1/downtime/back-entry/windows` (`patients.back_entry`) and printed on the pre-downtime pack.

A record **older than its window** is entered only after a second person approves a **late back-entry**: whoever will
enter the records asks once for the batch (`POST /api/v1/downtime/back-entry/late`, `patients.back_entry`:
`RequestLateBackEntry {record, paperFrom, paperTo, records, reason}`, the count from the back-entry tally), answered 202
with the approval. Someone else who holds `patients.back_entry.approve` (medical records officer, hospital
administrator) approves it in the approvals inbox (`patients.late_back_entry`; never the one who asked,
`approval.self`). The approval lets **that user** enter up to that many records of **that type**, dated within the
span, for 3 days. A paper within the window needs no approval (422 `backentry.within_window`). Entries under it are
counted for registrations, visits, bills and receipts, and marked "late, approved" on the block; for admissions,
transfers, ward visits and nursing charting the approval opens the span for the user until it expires (the validators
that check those times save nothing, so they do not count). The approvals of the last 30 days are listed at
`GET /api/v1/downtime/back-entry/late`.

## Pre-downtime pack

What the hospital prints to keep caring for patients without HC360, generated **on demand** (*Patients → Downtime
continuity → Print pack now*, `POST /api/v1/downtime/pack`, permission `patients.downtime_pack`: nurses, doctors,
medical records, hospital administrators) and **every night** after 02:00 for each facility by the server
(`DowntimePackJob`), so a pack less than a day old exists when HC360 goes down unannounced. Each is kept a week; the
latest is `GET /api/v1/downtime/pack/latest`. Printing, reading the latest and the nightly run are audited
(`patients.downtime_pack_printed`, `patients.downtime_pack_read`, `patients.downtime_pack_made`).

| Section | What | From |
|---|---|---|
| Census by ward | Every patient in a bed: bed, patient and UHID, age/sex, provisional diagnosis, allergies (or "No known allergies", or **ALLERGIES NOT RECORDED**), isolation (an active isolation or infection-control alert, or an isolation room), consultant, admission number, day of stay, MLC | Inpatient, Clinical |
| Medication sheets | One page per in-patient: allergies and isolation, each active medication order with the doses due in the next 24 hours, high-alert, controlled and "held: awaiting doctor" marked, and a column to sign each dose given | Inpatient (the MAR) |
| Pending lab and imaging orders | Tests and studies ordered and not yet reported: every in-patient's, and any ordered in the last 24 hours | Diagnostics |
| OT list | Operations booked in the next 24 hours and those under way, theatre by theatre, with side and level | SpecialtyCare |
| Downtime numbers issued | Each desk's and counter's open blocks (UHIDs, visits, bills, receipts) with the next number to use | Patients |
| Back-entry windows | The windows in force | Patients |

Each module writes its own section (`IDowntimePackSource`), reading its own records for the facility it is given, as
print-ready tables, so the print, the screen and the offline copy show the same thing. A restricted (VIP) patient is
printed as "Restricted patient" with the UHID unless the one printing may open restricted records; the nightly pack
never shows the name. The pack is a snapshot: anything ordered after it was made is on paper only, and the cover says
when it was made.

`DowntimePack {id, kind (Nightly, OnDemand), generatedAt, windowEndsAt, facilityName, generatedByName, patients,
sections[{key, title, note, pagePerTable, tables[{heading, note, alert, columns[], rows[][]}]}]}`.

## Read-only offline mode (staff app)

While the staff app can reach HC360 it keeps an **offline copy**: the latest pack and the user's worklists (OPD, lab,
radiology reporting, those the user may open), taken at sign-in and every 15 minutes. When the server cannot be reached
the app says so and shows **"Read-only copy from <time>. It is <age> old"** with *Open the offline copy*, which shows the
pack and the worklists. Nothing can be saved from it: the ward records on paper.

- The copy is kept on the computer **encrypted** (AES-GCM) under a key derived (HKDF-SHA-256) from the key the server
  gives the signed-in user with the session (`AuthResult.draftKey`), with the user and the slot as associated data. The
  computer's storage (IndexedDB) holds only ciphertext.
- The derived key is held in memory and in that tab's session storage, so reloading the tab while the server is down
  still opens the copy; closing the tab or the app forgets it, and then the printed pack is the copy.
- A **service worker** (`/sw.js`) keeps the app's own page, scripts and styles so the app still opens; it never keeps an
  API answer.
- **Signing out wipes** the copy, the key and the kept app files, whether or not the server can be reached; a session
  that ends while the server can be reached wipes them too.

### Not covered

- The nightly pack is made with no one signed in and never prints a restricted patient's name; print one on demand
  for the names.
- Until the in-patient workstream's downtime ward sheet reaches main, the census and medication sections read the
  records directly; once it does, they take that sheet's rows, so the two prints agree.
- A late back-entry approval for admissions, transfers, ward visits and nursing charting is not counted entry by entry.
- A number issued in one period and written on paper of the next (a yearly series across 1 January, or a financial
  year) is refused: issue blocks for the new period before a planned downtime that crosses it.

## Paper forms

*Patients → Downtime UHIDs → Paper forms* prints the kit's blank forms on A4, for copying. Each follows HC360's own
screen, so it is entered field by field when HC360 is back (each clinical form also has a box for the visit number from
the counter's downtime block), and each has a box for who entered it in HC360 and when,
and who checked it:

| Form | Who fills it in | What it holds |
|---|---|---|
| Patient registration | Registration desk | Every field of *Register patient*, in its order, under the kit's next downtime UHID, with the time registered; the restricted-record flag (VIP, staff or high-profile patient) and notes for the front desk; the privacy notice and WhatsApp consent; the patient's signature. The Aadhaar number is not written on paper: it is entered from the card at the next visit |
| OP consultation and prescription | Doctors | Department, doctor, vitals, complaints, findings, diagnosis, allergies, medicines (dose, route, frequency, duration), investigations advised, follow-up, signature and registration number |
| Laboratory and imaging requisition | Wards and OPD | Ordering doctor, priority, clinical details, the tests, and the sample's collection and receipt |
| Medication administration record | Nurses | Allergies, weight, and each dose given with the time and who gave it, or why it was not |
| Bill and receipt | Billing counter | Under the counter's next downtime bill and receipt numbers: the services, quantities and amounts, discount and who approved it, payment mode, amount and reference, the patient's signature |
| Back-entry tally | Each area | The downtime's number, and per form the downtime numbers used, how many were kept on paper, entered and checked; how many were older than their window, and the late back-entry approval for them; signed by the area in-charge. The quality team signs off the downtime from these |

The forms are built in the staff app (`modules/patients/paperForms.ts`); a hospital that prefers its own stationery
can copy their fields.

## The downtime log (Quality)

Every spell of working without HC360 is recorded in *Quality and safety → Downtime* (permission
`quality.downtime.manage`, held by quality managers and hospital administrators), and so is every drill of the
procedure below. It is usually recorded afterwards, since HC360 was down: its kind (unplanned, planned, or a drill),
its cause, what could not be used, when it started and ended, and what was done. A downtime is **ongoing** until it
ends, then **back-entry pending** until the paper records kept during it have been entered and checked, and **closed**
when that is signed off with the number of paper records entered.

The screen shows the last 12 months' figures, which the quality committee reviews:

| Figure | How it is counted |
|---|---|
| Unplanned downtime | Spells that started in the period; their minutes within the period, and the longest |
| Available (unplanned downtime only) | 100 % less the unplanned minutes as a share of the period so far (planned downtime and drills excluded) |
| Mean time to restore | Mean length of the unplanned spells that started in the period and have ended |
| Mean time to back-entry | Mean time from the end of a spell (or drill) to the sign-off of its back-entry |
| Drills | Drills started in the period, and the date of the last drill ever recorded |

Recording, ending and signing off are written to the audit trail (`quality.downtime_recorded`, `quality.downtime_ended`,
`quality.downtime_back_entry_signed`). Each downtime gets a number from the *Downtime* series (`{FAC}DT{YY}{SEQ:4}`).

| Method | Path | Body → answer |
|---|---|---|
| GET | `/api/v1/quality/downtime?from&to&status` | The facility's downtime started in the period (default the last 12 months, at most three years), newest first: `DowntimeEventDto {id, number, kind, cause, affected, description, startedAt, endedAt, minutes, status, recordedByName, actionsTaken, endedByName, backEntryCompletedAt, paperRecords, backEntryNote, backEntrySignedByName, version}` |
| GET | `/api/v1/quality/downtime/summary?from&to` | `DowntimeSummary {from, to, unplanned, planned, drills, unplannedMinutes, plannedMinutes, longestUnplannedMinutes, unplannedAvailabilityPercent, meanMinutesToRestore, meanHoursToBackEntry, ongoing, backEntryPending, lastDrillAt}` |
| GET | `/api/v1/quality/downtime/{id}` | `DowntimeEventDto` |
| POST | `/api/v1/quality/downtime` | `RecordDowntimeRequest {kind (Unplanned, Planned, Drill), cause (ServerFailure, NetworkOutage, PowerFailure, SoftwareFault, CyberIncident, Maintenance, Other), affected, description?, startedAt, endedAt?, actionsTaken?}` → 201; 400 for a start or end in the future or an end before the start |
| POST | `/api/v1/quality/downtime/{id}/end` | `EndDowntimeRequest {endedAt, actionsTaken?, version}`; 422 `downtime.ended` |
| POST | `/api/v1/quality/downtime/{id}/back-entry` | `SignOffBackEntryRequest {paperRecords, note?, version}`; 422 `downtime.ongoing` before it ends, `downtime.closed` when already signed off |

All permission `quality.downtime.manage`; a stale `version` is refused with 409.

## Downtime procedure (draft for the quality team)

This is a starting point for the hospital's own procedure, built from what NABH's IMS chapter and JCI's MOI standards
ask for (planning, staff who know the procedure, records kept during downtime, data entered and checked afterwards,
the procedure tested and downtime monitored). The quality team adapts it to the hospital and adopts it; the
hospital's procedure, not this page, is what staff follow.

1. **Prepare.**
   - Every registration desk keeps a downtime kit: a block of downtime UHIDs with its printed sheet, blank paper
     registration forms, and the paper forms the other areas use (see *Paper forms*: OP consultations, lab and imaging
     requisitions, medication administration records, bills and receipts, back-entry tallies), and the manual receipt
     book.
   - Every OPD registration and billing counter holds blocks of downtime visit, bill and receipt numbers with their
     printed sheets.
   - HC360 makes the pre-downtime pack every night; each ward keeps the latest printed copy where the procedure says.
   - Medical records keeps the kits stocked: a block is replaced when it runs low, and its unused numbers are voided.
   - IT keeps the list of who to call and how a downtime is announced (overhead page, phone tree, WhatsApp group).
2. **Planned downtime.**
   - IT announces it at least 48 hours ahead and holds it at the quietest time.
   - Before it starts, the wards print what they need to work without HC360: the census, the active medication orders,
     allergies and the day's due tasks. HC360 prints this as the **pre-downtime pack** (*Patients → Downtime
     continuity → Print pack now*): see *Pre-downtime pack*.
3. **Declare a downtime.**
   - When HC360 cannot be used, or IT expects it to be down for more than 15 minutes (the hospital sets the
     threshold), the duty manager declares the downtime procedure and the announcement goes out.
   - IT gives an expected time back and updates it.
4. **During downtime.**
   - Registration registers on paper under the kit's UHIDs, in order.
   - Clinicians write on the paper forms, with the UHID, date and time on every page.
   - The laboratory phones critical results and files paper reports.
   - Pharmacy dispenses against paper prescriptions.
   - Billing issues receipts from the manual receipt book.
   - Nothing waits for HC360 that patient care needs.
5. **Recover.**
   - IT confirms HC360 is back and the announcement goes out.
   - Each area enters its paper records with the time on the paper, not the time of entry, in this order:
     registrations, admissions, orders, results, medication administration, notes, bills. Visits, bills and receipts
     are entered under the downtime numbers on the paper.
   - Paper older than its back-entry window is entered only after a late back-entry approval: the area in charge asks
     once for the batch, with the count from the tally.
   - The paper originals go to medical records with the patient's file.
6. **Check and sign off.**
   - Each area counts its paper records and checks them against what was entered; a second person checks a sample.
   - Medical records voids the kit UHIDs that were not used, and takes back the counters' number blocks (returning a
     block voids its unused numbers).
   - The quality team signs off the back-entry in the downtime log with the number of records entered.
7. **Review.**
   - The quality committee reviews the downtime figures at least each quarter.
   - An unplanned downtime that harmed or delayed care is reported as an incident and investigated.
   - A downtime caused by a cyber incident is also a reportable incident to CERT-In within six hours (see the data
     breach register in `docs/modules/privacy.md`).
8. **Train and drill.**
   - New staff learn the procedure at induction.
   - The procedure is rehearsed at least once a year, and after any change to it, in a drill recorded in the log.

