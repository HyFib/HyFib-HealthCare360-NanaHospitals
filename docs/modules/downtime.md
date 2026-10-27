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
| is dated before its block was issued, or in the future, or more than 90 days ago | 400 on `paper.registeredAt` |

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

### Not covered here

Only UHIDs are issued ahead of time. Visits, bills and receipts made on paper during downtime are entered afterwards
and get their numbers then.

## Paper forms

*Patients → Downtime UHIDs → Paper forms* prints the kit's blank forms on A4, for copying. Each follows HC360's own
screen, so it is entered field by field when HC360 is back, and each has a box for who entered it in HC360 and when,
and who checked it:

| Form | Who fills it in | What it holds |
|---|---|---|
| Patient registration | Registration desk | Every field of *Register patient*, in its order, under the kit's next downtime UHID, with the time registered; the privacy notice and WhatsApp consent; the patient's signature. The Aadhaar number is not written on paper: it is entered from the card at the next visit |
| OP consultation and prescription | Doctors | Department, doctor, vitals, complaints, findings, diagnosis, allergies, medicines (dose, route, frequency, duration), investigations advised, follow-up, signature and registration number |
| Laboratory and imaging requisition | Wards and OPD | Ordering doctor, priority, clinical details, the tests, and the sample's collection and receipt |
| Medication administration record | Nurses | Allergies, weight, and each dose given with the time and who gave it, or why it was not |
| Back-entry tally | Each area | The downtime's number, and per form how many were kept on paper, entered and checked, signed by the area in-charge; the quality team signs off the downtime from these |

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
     requisitions, medication administration records, back-entry tallies), and the manual receipt book.
   - Medical records keeps the kits stocked: a block is replaced when it runs low, and its unused numbers are voided.
   - IT keeps the list of who to call and how a downtime is announced (overhead page, phone tree, WhatsApp group).
2. **Planned downtime.**
   - IT announces it at least 48 hours ahead and holds it at the quietest time.
   - Before it starts, the wards print what they need to work without HC360: the census, the active medication orders,
     allergies and the day's due tasks.
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
     registrations, admissions, orders, results, medication administration, notes, bills.
   - The paper originals go to medical records with the patient's file.
6. **Check and sign off.**
   - Each area counts its paper records and checks them against what was entered; a second person checks a sample.
   - Medical records voids the kit UHIDs that were not used.
   - The quality team signs off the back-entry in the downtime log with the number of records entered.
7. **Review.**
   - The quality committee reviews the downtime figures at least each quarter.
   - An unplanned downtime that harmed or delayed care is reported as an incident and investigated.
   - A downtime caused by a cyber incident is also a reportable incident to CERT-In within six hours (see the data
     breach register in `docs/modules/privacy.md`).
8. **Train and drill.**
   - New staff learn the procedure at induction.
   - The procedure is rehearsed at least once a year, and after any change to it, in a drill recorded in the log.

