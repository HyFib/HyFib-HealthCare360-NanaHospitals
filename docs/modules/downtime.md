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
4. **When HC360 is back**, the desk enters each form on *Register patient* with *Entering a paper form filled in
   while HC360 was down* switched on, the UHID on the form and the time on the form. The patient keeps that UHID and
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
