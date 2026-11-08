# Inpatient module (`inpatient`)

ADT (admission, discharge, transfer), ward and bed management with a live bed board, housekeeping, occupancy and
census statistics, automatic bed/nursing/consultant-visit charges, and nursing documentation including the medication
administration record. SRS **WB-01..06**, IPB auto charges (IPB-01), PH-03 MAR feed.

Project: `src/server/Modules/HyFib.HealthCare360.Modules.Inpatient` (references Patients, and Clinical for the ward
round). Schema `inpatient`. Tests: `tests/Modules/HyFib.HealthCare360.Modules.Inpatient.Tests` (214 unit tests).

It implements the Platform contracts `IRoomClassDirectory` (Billing's rate list) and `IAdmissionDirectory` (whether a
stay goes on, read in every facility: Clinical takes no order for a stay that is discharged or cancelled), and asks
`IBillingStatusProvider` and every `IDischargeHoldSource` before it completes a discharge.

## Permissions

| Permission | Purpose | Default roles |
|---|---|---|
| `inpatient.admissions.read` | Admissions, bed board, discharges, census | FrontOffice, Nurse, Doctor, HospitalAdmin, Billing, Cashier, InsuranceDesk, Pharmacist, Dietitian, MrdOfficer |
| `inpatient.admissions.admit` | Admit, edit, cancel admissions | FrontOffice, Nurse, Doctor, HospitalAdmin |
| `inpatient.admissions.transfer` | Bed/ward transfers | FrontOffice, Nurse, Doctor, HospitalAdmin |
| `inpatient.admissions.discharge` | Initiate, revoke, complete discharge | Doctor, Nurse, HospitalAdmin |
| `inpatient.masters.manage` | Wards, rooms, beds | HospitalAdmin |
| `beds.manage` | Reserve, block, maintenance, send for cleaning | FrontOffice, Nurse, HospitalAdmin |
| `beds.housekeeping` | Housekeeping queue, release cleaned beds | Nurse, HospitalAdmin |
| `nursing.chart` | Notes, handovers, intake/output, risk assessments, consultant visits | Nurse, Doctor |
| `nursing.mar` | Medication chart and MAR | Nurse, Doctor |
| `nursing.in_charge` | Ward nurse in charge: the verbal and telephone orders and nurses' stops not countersigned in time, listed and told of each | HospitalAdmin (give it to the ward sisters' role) |

"Read set" below = any of `inpatient.admissions.read`, `beds.manage`, `beds.housekeeping`, `nursing.chart`, `inpatient.masters.manage`.

## Endpoints

### Ward and room masters — `/api/v1/inpatient`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/wards?includeInactive&type` | read set | → `WardDto[]` (code, name, type, floor, genderPolicy, bedServiceItemId/-Code, totalBeds, occupiedBeds, version) |
| GET | `/wards/{id}` | read set | → `WardDto` |
| POST | `/wards` | `inpatient.masters.manage` | `SaveWardRequest` → 201 `{ id }` (bed tariff must be a `BedCharge` service) |
| PUT | `/wards/{id}` | `inpatient.masters.manage` | `SaveWardRequest` (+`version`) → 204; closing a ward with occupied/reserved beds → 422 |
| GET | `/wards/{wardId}/rooms` | read set | → `RoomDto[]` |
| POST | `/rooms` | `inpatient.masters.manage` | `SaveRoomRequest` → 201 |
| PUT | `/rooms/{id}` | `inpatient.masters.manage` | `SaveRoomRequest` (+`version`) → 204 |

### Beds — `/api/v1/beds`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/?wardId&status&includeInactive` | read set | → `BedDto[]` (status, statusReason, currentAdmissionId, reservedFor/Until, features, `nextStatuses`, version) |
| GET | `/available?patientId&wardType&wardId` | admit, transfer or `beds.manage` | → `AvailableBedDto[]` with `isEligible`/`ineligibleReason` (sex-segregated wards, NICU ≤ 28 days, PICU < 18 y, maternity) |
| GET | `/board?wardId` | `inpatient.admissions.read` | → `BedBoardWard[]` (counts + beds with `BedOccupant`: name/UHID/age/sex, consultant, LOS days, expected discharge; `alerts`) |
| GET | `/occupancy` | admissions.read or `beds.manage` | → `OccupancyReport` (total, by ward, by ward type; `BedCounts.occupancyPercent` = occupied ÷ beds in service) |
| GET | `/{id}` | read set | → `BedDto` |
| GET | `/{id}/history` (paged) | `inpatient.admissions.read` | → `PagedResult<BedHistoryItem>` |
| POST | `/` | `inpatient.masters.manage` | `SaveBedRequest` → 201 `BedDto` |
| POST | `/bulk` | `inpatient.masters.manage` | `CreateBedsRequest` (labelPrefix, startNumber, count ≤ 100) → `BedDto[]` |
| PUT | `/{id}` | `inpatient.masters.manage` | `SaveBedRequest` (+`version`) → 204 |
| POST | `/{id}/reserve` | `beds.manage` | `ReserveBedRequest` (patientId, reservedUntil ≤ 7 days, note) → `BedDto` |
| POST | `/{id}/cancel-reservation` | `beds.manage` | → `BedDto` |
| POST | `/{id}/block` · `/{id}/maintenance` | `beds.manage` | `BedReasonRequest` (reason required) → `BedDto` |
| POST | `/{id}/unblock` · `/{id}/maintenance/complete` | `beds.manage` | → `BedDto` (maintenance ends in Cleaning) |
| POST | `/{id}/cleaning` | `beds.manage` or `beds.housekeeping` | `BedNoteRequest` (send `{}` when empty) → `BedDto` |
| GET | `/housekeeping?status&wardId` | `beds.housekeeping` or `beds.manage` | → `HousekeepingTaskDto[]` (open tasks by default, waiting minutes) |
| POST | `/housekeeping/{taskId}/start` | `beds.housekeeping` | → `HousekeepingTaskDto` |
| POST | `/housekeeping/{taskId}/complete` | `beds.housekeeping` | `BedNoteRequest` → `HousekeepingTaskDto` (turnaround minutes; bed → Vacant) |

Bed board alerts (`BedAlert`): `Mlc`, `Restricted` (VIP — name shown as "Restricted patient" without `patients.restricted.read`),
`HighFallRisk` (Morse ≥ 45), `PressureInjuryRisk` (Braden ≤ 12), `SeverePain` (≥ 7), `MedicationOverdue`,
`DischargeInitiated`, `DischargeDueToday`, `ReservationExpired`, `CleaningDelayed` (> 60 min).

### Admissions — `/api/v1/inpatient/admissions`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/?search&status&activeOnly&wardId&consultantId&from&to` (paged) | `inpatient.admissions.read` | → `PagedResult<AdmissionListItem>` (search: UHID/name/phone/ABHA or admission number) |
| GET | `/{id}` | `inpatient.admissions.read` | → `AdmissionDetail` (restricted-record guard + PHI audit; policy number masked) |
| POST | `/` | `inpatient.admissions.admit` | `AdmitPatientRequest` (patientId, bedId, type, source, sourceVisitId, departmentId, admitting/consultant practitioner, provisionalDiagnosis, payerType/payerId, policyNumber, isMlc/mlcNumber, expectedDischargeDate, attendant*, admittedAt ≤ 3 days back, notifyPatient) → 201 `AdmissionCreated` (id, admissionNumber, visitId, visitNumber, bedId, bedLabel). The visit named as `sourceVisitId` must be the patient's own (400) and one the Admit screen offers (staff app `modules/inpatient/sourceVisit.ts`), else 422 naming it: of the source's kind, an OPD visit for `OutPatient` and a casualty visit for `Emergency`, none for `Referral` or `Direct` (`admission.source_visit_type`); not cancelled (`admission.source_visit_cancelled`); checked in no later than the admission time (`admission.source_visit_after_admission`: a late entry cannot come from a later visit) and no more than 3 days before it (`admission.source_visit_too_old`). An admission entered as earlier counts back from its own `admittedAt`. An OPD visit so named, while still open, is completed in the same transaction with its queue token, as `VisitService` completes a visit at the desk, so it no longer shows as in consultation nor stops a check-in the same day; an emergency visit is left to the ER, whose disposition closes it. An insurer, company or government scheme as payer must be named, in use and of that kind (400 `payerId`/`payerType`), and stand behind the patient through a policy in date and verified by someone other than whoever recorded it (422 `payer.no_cover`, `payer.cover_not_verified`; billing.md, A third party pays only on a second person's word): until then the patient is admitted as paying |
| PUT | `/{id}` | `inpatient.admissions.admit` | `UpdateAdmissionRequest` (+`version`; consultant, department, diagnosis, expected discharge, policy number, MLC, attendant, notes, `changeReason?`) → 204. The payer is fixed at admission (changed in Billing). Marking the stay medico-legal, or giving an MLC its number, is saved at once. Clearing the flag, or changing or removing the MLC number it is linked by, unlinks the stay from the MLC register (and ends a victim's free treatment under BNSS s.397): the rest is saved and the answer is 202 `ApprovalRequestDto` (`inpatient.mlc_clear`, `changeReason` required), approved by someone else who keeps the register (`mrd.mlc.register`); until then the stay, its visit and the free treatment stay as they are (maker-checker slice 7; audited `admission.mlc_cleared`). `GET /{id}` shows it as `pendingMlc`. |
| POST | `/{id}/transfer` | `inpatient.admissions.transfer` | `TransferPatientRequest` (toBedId, reason, consultant/department change, transferredAt ≤ 24 h back) → 204 |
| POST | `/{id}/cancel` | `inpatient.admissions.admit` | `CancelAdmissionRequest` → 204; reverses this module's charges in the same transaction (422 `admission.charges_billed` if Billing refuses) |
| GET | `/{id}/wristband` | `inpatient.admissions.read` | → `WristbandDto` (admissionId, uhid, patientName, gender, age, admissionNumber, wardName, bedLabel, `allergies`: `Recorded` \| `NoKnownAllergies` \| `NotRecorded`). Patients still in a bed only (422 `admission.not_in_bed`); restricted-record guard; audited "Printed a wristband". See *Wristband* below |
| GET | `/{id}/bed-history` | `inpatient.admissions.read` | → `BedAssignmentDto[]` |
| GET | `/{id}/charges` | `inpatient.admissions.read` | → `DailyChargeDto[]` (bed/nursing days posted, for reconciliation) |
| GET | `/{id}/consultant-visits` | admissions.read or `nursing.chart` | → `ConsultantVisitDto[]` |
| POST | `/{id}/consultant-visits` | `nursing.chart` | `RecordConsultantVisitRequest` (practitionerId defaults to the signed-in doctor, visitedAt, notes) → 201 `ConsultantVisitDto`; 409 within 30 min of the same doctor's visit |
| POST | `/consultant-visits/{visitId}/cancel` | `nursing.chart` | `CancelConsultantVisitRequest` → 204 (charge reversed) |
| GET | `/api/v1/patients/{patientId}/admissions` | `inpatient.admissions.read` | → `PatientAdmissionItem[]` (guard + PHI audit) |

**Wristband (FO-03).** The staff app prints the band at admission and from the admission page, reading
`/{id}/wristband` fresh for each print. The allergy status comes from the chart (Clinical's allergies and "no known
allergies" statement), so a band printed at the admission desk carries the mark even though the front office cannot
read the allergies themselves; only the status is shared. The band prints it three ways:

| `allergies` | Band |
|---|---|
| `Recorded` (an active allergy on the chart) | Black ALLERGY mark |
| `NoKnownAllergies` (a clinician recorded "no known allergies") | No mark |
| `NotRecorded` (nobody has recorded allergies or their absence) | Outlined "ALLERGIES / NOT RECORDED" mark |

A printed band does not change when the chart does. After allergies are first recorded (or "no known allergies" is
recorded), staff reprint the band from the admission page. On a ZPL or TSPL band printer the server writes the band
(see [integrations.md](integrations.md#barcode-labels-int-bar)); otherwise the staff app prints its band page.

Conflicts: 409 `admission.already_admitted`, `bed.unavailable`, `bed.taken` (concurrent allocation; the bed's `xmin`
version and partial unique indexes on active admissions per patient/bed make double allocation impossible).

### Ward round — `GET /api/v1/inpatient/rounds?practitionerId&departmentId`
Needs `inpatient.admissions.read` **and** `clinical.emr.read` (the round shows the clinical record). It is the ward-round
section of the doctor's worklist (EMR-01, see the Clinical module).

→ `WardRound{date, practitionerId, departmentId, patients[WardRoundPatient]}`, at most 200 patients:

* **Whose patients**: by default the signed-in doctor's: admissions under them as consultant, and admissions they have
  recorded a visit to during the stay (`isPrimaryConsultant: false`, shown as a cross-consult). Without a doctor, the
  department's admissions. Only patients still in a bed (admitted, or discharge in progress), in the bed board's order
  (ward and bed display order, then name or label).
* **Each patient**: admission number, UHID, name, age and sex; ward, bed and critical care; day of the stay (the
  admission day is day 1); expected discharge, provisional diagnosis, MLC; the consultant; the banner's flags (active
  allergies, critical alerts).
* **What needs attention**: `lastVisitAt` and `seenToday` (a consultant visit recorded today, IST); `lastNoteAt` (the
  latest clinical note or encounter during the stay); `resultsToReview` and `hasCriticalResults` (results of the stay
  not yet reviewed, whoever ordered them); the latest vital signs of the stay with their worst flag (`vitalsSeverity`)
  and up to three flags in words (`VitalSignsAssessor`). On a doctor's round, visits and notes are the doctor's own; on
  a department's, anyone's.
* **Restricted (VIP) patients**, as on the bed board: unless the user may open restricted records, the name reads
  "Restricted patient" and the diagnosis and vital-sign details are left out (`isRestricted: true`). The bed, UHID,
  flags and what needs attention stay, and opening the chart asks for break-the-glass.

### Discharge — `/api/v1/inpatient/admissions/{id}/discharge`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| POST | `/initiate` | `inpatient.admissions.discharge` | `InitiateDischargeRequest` (expectedDischargeDate, notes) → `DischargeResult` (all due bed days are posted first) |
| POST | `/revoke` | `inpatient.admissions.discharge` | `RevokeDischargeRequest` → `DischargeResult` |
| GET | `/clearance` | discharge or admissions.read | → `DischargeClearanceDto` (from `IBillingStatusProvider`) |
| POST | `/complete` | `inpatient.admissions.discharge` | `CompleteDischargeRequest` (dischargeType Normal/Referred/Lama/Dama/Absconded/Expired, dischargedAt ≤ 48 h back, notes — required for LAMA/DAMA/absconded, notifyPatient; `againstAdvice` for LAMA/DAMA, `absconded` for Absconded, `certificatePendingPostMortem` for a medico-legal death) → 200 `DischargeResult`, or 202 `ApprovalRequestDto` for Absconded (`inpatient.absconded`) and a death pending post-mortem (`inpatient.expired_pending_postmortem`); 422 `discharge.in_theatre` while the stay has a surgery in pre-op or in progress (the detail names each case), then 422 `discharge.billing_pending` (`data.outstanding`) until Billing clears. Expired: 422 `discharge.death_certificate_required`, `discharge.death_certificate_unconfirmed`, `discharge.death_certificate_other_stay`, `discharge.postmortem_not_mlc`. Absconded: 422 `discharge.police_intimation_required` for a medico-legal stay. LAMA/DAMA: 422 `against_advice.doctor_required`, `against_advice.witness_is_doctor`, `witness.confirmation_unusable`. 409 `approval.already_requested` while a closing waits |
| GET | `/death-certificate` | discharge or admissions.read | → `DischargeDeathCertificateDto` (the patient's certificate: number, `settled`, `forThisStay`, `confirmedBy`, `confirmationWaitingSince`, `medicoLegalStay`, `status` in words) |
| GET | `/api/v1/inpatient/discharges/pending?wardId` | `inpatient.admissions.read` | → `PendingDischargeItem[]` |

Rules: routine discharges (Normal, Referred, LAMA, DAMA) must be initiated and cleared by Billing; death and abscondence
skip billing clearance and the "discharged" SMS (a body is not held for dues), the dues left recorded on the stay's
declaration. Maker-checker (slice 8): Expired closes only with the patient's death certificate (MCCD) a second doctor
confirmed (or reported, or at a facility that asks for no confirmation); a medico-legal death whose certificate waits
for the post-mortem waits for a second doctor who confirms certificates (`mrd.deaths.confirm`). Absconded waits for the
nurse in charge (`nursing.in_charge`), with the report (last seen, who searched, police and relatives told; for a
medico-legal stay the police station and reference). LAMA and DAMA record who signed the form (patient, a relative named
with the relationship, or "refused to sign"), the doctor who explained the risks (the user signed in when a doctor, or
their confirmation at the screen, witness purpose `against_advice.doctor`) and a witness confirming with their own
credentials (purpose `discharge.against_advice`, `nursing.chart`), never the doctor, and optionally the scanned form (a
file id). The patient stays admitted while a closing waits (`GET /{id}` shows `pendingDischarge`); once closed, `GET
/{id}` shows the `declaration` (form, witness, report, certificate, dues, who recorded and who approved). An approved
closing bills the remaining bed days in its own transaction. Audited `discharge.expired`, `discharge.against_advice`,
`discharge.absconded`, `discharge.expired_pending_postmortem`. Whatever the type, a discharge is not
completed while another department still has the patient: before anything is charged or changed, `DischargeService`
asks every `IDischargeHoldSource` (Platform contract; today SpecialtyCare's theatre: a surgery of the stay received in
pre-op or under way), and refuses with the first hold's code and every hold's message. It asks again in its transaction,
under the stay's lock (`EncounterLocks`, held to the commit), which the theatre also takes to receive the patient in
pre-op or start the operation: a case taken into the theatre at the same moment is either seen, or finds the stay
ended. Completion closes the visit,
closes the bed assignment and sends the bed to housekeeping.

### Census — `GET /api/v1/inpatient/census?from&to&wardId` (`inpatient.admissions.read`)
→ `CensusReport`: hospital and per-ward `CensusSummary` (admissions, discharges, deaths, daily midnight census,
transfers in/out per ward, inpatient days, ALOS, bed occupancy rate), housekeeping tasks and average turnaround. IST
dates, default last 7 days, at most 93 days.

### Nursing — `/api/v1/nursing` (all `nursing.chart`)
| Method | Path | Request → response |
|---|---|---|
| GET | `/admissions/{admissionId}/notes?dutyDate` | → `NursingNoteDto[]` |
| POST | `/admissions/{admissionId}/notes` | `AddNursingNoteRequest` (category, note, noteFor ≤ 24 h back) → 201 (late entries flagged; notes are never edited) |
| GET | `/admissions/{admissionId}/handovers` | → `HandoverDto[]` (SBAR) |
| POST | `/admissions/{admissionId}/handovers` | `AddHandoverRequest` (situation, background, assessment, recommendation, pendingTasks) → 201 |
| POST | `/handovers/{id}/acknowledge` | → `HandoverDto` (receiving nurse only) |
| GET | `/wards/{wardId}/handovers?dutyDate&shift` | → `WardHandoverItem[]` (audited) |
| GET | `/admissions/{admissionId}/intake-output?dutyDate` | → `IntakeOutputChart` (08:00–08:00 chart day, totals, shift and route subtotals) |
| POST | `/admissions/{admissionId}/intake-output` | `AddIntakeOutputRequest` (direction, route, volumeMl 1–10 000, recordedAt) → 201 |
| POST | `/intake-output/{id}/void` | `VoidEntryRequest` → 204 (soft delete with reason) |
| GET | `/admissions/{admissionId}/assessments?type` | → `AssessmentDto[]` |
| POST | `/admissions/{admissionId}/assessments/morse` · `/braden` · `/pain` | `MorseAssessmentRequest` · `BradenAssessmentRequest` · `PainAssessmentRequest` → 201 `AssessmentDto` (score, risk, interpretation, responses) |
| GET | `/admissions/{admissionId}/safety-events` | → `NursingSafetyEventDto[]` (falls and pressure injuries) |
| POST | `/admissions/{admissionId}/safety-events` | `RecordSafetyEventRequest` (kind Fall or PressureInjury, occurredAt, location, description, immediateAction; a fall: injury None/Minor/Moderate/Major/Death on the NDNQI levels and witnessed; a pressure injury: NPIAP stage, body site, presentOnAdmission) → 201 `NursingSafetyEventDto` with the incident number it raised |

**Falls and pressure injuries** (`NursingSafetyEvent`, table `inpatient.nursing_safety_events`) are never edited (a
mistake is corrected with a nursing note). Every fall, and a pressure injury that developed in hospital, raises an incident
in the Quality register in the same save (`IIncidentRegister.ReportAsync`), graded by its harm: a fall by its injury, a
pressure injury stage 1–2 mild, stage 3 moderate, stage 4, unstageable or deep tissue severe. A pressure injury present on
admission is documented and raises none, and the pressure ulcer incidence indicator leaves it out. Both count in the
computed NABH indicators (falls and pressure ulcers per 1,000 patient days, [quality.md](quality.md)).

Reads are PHI-audited and honour restricted records; writes need the chart open (admitted, in discharge, or ≤ 24 h
after discharge). Vitals are owned by Clinical and are not duplicated here.

### Medication chart and MAR — `/api/v1/nursing`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/admissions/{admissionId}/medication-orders?activeOnly` | `nursing.mar` or `nursing.chart` | → `MedicationOrderDto[]` (incl. `roundTimes`) |
| POST | `/admissions/{admissionId}/medication-orders` | `nursing.mar` | `AddMedicationOrderRequest` (a prescriber's own order, signed at once, or a doctor's verbal or telephone order: `orderedByPractitionerId`, `mode`, `readBack`, `emergency`; next 24 h scheduled unless the doses are held) → 201; 422 `medication.verbal_order_required`, `medication.read_back_required`; 400 on `orderedByPractitionerId` (no doctor) or `drugItemId` (not in the master); after the discharge, 422 `medication.discharged` |
| POST | `/medication-orders/{id}/stop` | `nursing.mar` | `StopMedicationOrderRequest` → `MedicationOrderDto`: a prescriber's stop cancels the future doses at once; anyone else's is asked for (`stopPending`, doses held) |
| POST | `/medication-orders/{id}/countersign` | `clinical.prescriptions.sign` | `CountersignMedicationOrderRequest` (`version`) → `MedicationOrderDto`; only the doctor named, from their own account (403 `medication.countersign_not_prescriber`); held doses are scheduled |
| POST | `/medication-orders/{id}/stop/countersign` | `clinical.prescriptions.sign` | `ConfirmStopRequest` (`version`) → the order stopped, the doses held since the stop was asked for cancelled |
| POST | `/medication-orders/{id}/stop/decline` | `clinical.prescriptions.sign` | `DeclineStopRequest` (reason, `version`) → the order goes on; the nurse who asked is told |
| GET | `/medication-orders/awaiting-countersignature?wardId&mine&overdueOnly` | `nursing.mar` or `nursing.in_charge` | → `AwaitingCountersignatureItem[]` (what waits for a doctor: for a ward, for the signed-in doctor, past its time) |
| GET | `/drugs?search` | `nursing.mar` | → `WardDrugDto[]` (the drug master for the ward's order form, with its controlled and high-alert flags) |
| POST | `/medication-orders/{id}/schedule` | `nursing.mar` | → `ScheduleResult` (extend 24 h); 422 while the doses are held |
| GET | `/admissions/{admissionId}/mar?date` | `nursing.mar` or `nursing.chart` | → `MarEntryDto[]` (day + older undocumented doses, `isOverdue`) |
| POST | `/admissions/{admissionId}/mar` | `nursing.mar` | `AddAdHocDoseRequest` (SOS/STAT; `givenNow`; `witnessConfirmationId` as below) → 201 `MarEntryDto`; after the discharge (late entries, 24 h) only a dose given before it, with its time, else 422 `medication.discharged` |
| POST | `/mar/{id}/record` | `nursing.mar` | `RecordAdministrationRequest` (outcome Given/Held/Refused/Missed, administeredAt, reason — required unless given, batch, `witnessConfirmationId` — required for a controlled drug or high-alert medicine given, or a dose with no order, notes, `version`) → `MarEntryDto` (`witnessName`, `needsWitness`, `heldBecause`); a held dose is not given (422 `medication.awaiting_countersignature`, `medication.stop_pending`) |
| GET | `/wards/{wardId}/mar/due?withinMinutes` | `nursing.mar` | → `WardMarItem[]` (ward due/overdue worklist) |

A controlled drug given (an order marked controlled: Schedule H1 or X, or NDPS) or a high-alert medicine given is
witnessed by a second nurse, who proves who they are at the recording nurse's screen (ADR-014). An order that names an
item of the drug master takes both flags from it when it is made, whatever the order form or the prescription line
sent; so does the order made for a medicine the pharmacy issues without a prescription line. A ward order typed in
without an item of the master is always high-alert (its flags cannot be cleared by typing a name), and a dose with no
order behind it is always witnessed. The witnesses type their own user name and password there, and
their authenticator code when their account has one (`POST /api/v1/auth/witness`, purpose `mar.dose`, with the
patient). Whoever keeps the MAR may witness (`nursing.mar`: nurses and doctors), never the nurse recording. The answer is
a confirmation the recording nurse sends with the dose as `witnessConfirmationId`. It is good once, for that nurse, that
patient and five minutes. It is spent in the dose's own save (422 `witness.confirmation_unusable` otherwise). The dose
then names the witness's account (`WitnessUserId`) and name: not a name typed, as before (doses recorded before keep
theirs). Without one, 422 `mar.witness_required`. The witness's password never reaches this module.

**Verbal and telephone orders (maker-checker slice 5).** A ward order is signed at once only by a prescriber
(`clinical.prescriptions.sign`) writing in their own name: the practitioner named is the one linked to their account.
Anyone else, a nurse or a doctor writing another doctor's order, writes that doctor's verbal or telephone order: the
doctor, the mode and the read-back are required, the order is audited at Warning (`medication.verbal_order`), the doctor
is told, and it stays awaiting countersignature until the doctor named countersigns it from their own account. A
high-alert, controlled or chemotherapy medicine (the drug master's high-alert flag covers chemotherapy) gives no dose
until then: nothing goes on the round, and no extra dose is recorded against it, unless the nurse wrote it as an
emergency verbal order, read back, when its doses are given, each witnessed, and it stays flagged. The doctor has
`inpatient.verbalOrderCountersignHours` (default 24, 1 to 72; fixed on the order when it is written) to countersign. A
nurse's stop is asked for: the order stays on the chart with every dose held until the doctor named (or the patient's
consultant, for an order that names none) confirms it, when the doses held since it was asked for are cancelled, or
declines it, when they are given again. `CountersignOverdueJob` (every 15 minutes, nodes that run jobs) tells the doctor
and the facility's holders of `nursing.in_charge` once of each order or stop past its time, audited at Warning
(`medication.countersign_overdue`, `medication.stop_countersign_overdue`); the staff app lists them under In-patients ›
Orders to countersign.

Frequencies understood: OD/QD/Daily, BD/BID, TDS/TID, QID/QDS, HS/Nocte, `1-0-1`-style patterns (3 or 4 parts,
fractions allowed), `Q4H`/`6 hourly`…, STAT (one dose), SOS/PRN/"As directed" (never auto-scheduled).

## Integration events
| Event | Direction | Notes |
|---|---|---|
| `PatientAdmitted` | raised | on admission; includes `WardId`, `BedId`, `BedServiceItemId` (ward tariff), `PayerType` |
| `PatientTransferred` | raised | includes `FromBedId`, `ToBedId`, `ToBedServiceItemId`, `ToWardId` |
| `AdmissionCancelled` | raised | on cancellation, with the reason. The admission's visit is cancelled in the same transaction (Billing reacts to `VisitCancelled`); Dietary stops the diet order and future meals, Pharmacy cancels pending ward indents, SpecialtyCare cancels the surgeries booked during the stay and still waiting (and gives back those booked before it) and unissued blood requests, Diagnostics withdraws the tests not drawn and the studies not done |
| `DischargeInitiated` | raised | on initiation (and on direct completion of death/abscondence) |
| `DischargeRevoked` | raised | when an initiated discharge is revoked: Billing opens the IP account again, so the stay is not settled while the patient is in |
| `PatientDischarged` | raised and handled | `DischargeType` = `Normal`, `Referred`, `Lama`, `Dama`, `Absconded` or `Expired`; Diagnostics withdraws the stay's tests whose sample is not drawn. Handled here too: the stay's chart lines stop ("Patient discharged") and the doses scheduled from the discharge on are cancelled; doses that fell due before it stay for the nurse to document |
| `VisitCreated` / `VisitCompleted` / `VisitCancelled` (Patients) | raised | the in-patient visit is created with Patients' numbering (`visit`, `{FAC}V{YY}{SEQ:7}`), completed on discharge, cancelled with the admission; the OPD visit the patient was admitted from is completed at admission |
| `PrescriptionSigned` | handled | in-patient lines become medication-chart lines (no doses until dispensed) |
| `PrescriptionCancelled` | handled | chart lines stopped, future doses cancelled |
| `MedicationDispensed` | handled | next 24 h of doses scheduled; unlinked lines charted as one dose to confirm |
| `PatientsMerged` (Patients) | handled | re-points patient ids across facilities |

Real-time (ids/status only): `beds.changed` → `Topics.Beds(facilityId)` and `Topics.Ward(wardId)`
`{ bedId, wardId, status }`; `admissions.changed` → facility `{ admissionId, status }`;
`inpatient.discharge_initiated` → facility `{ admissionId }`.

## Charges (via `IChargePoster`)
| SourceModule | SourceReference | Service |
|---|---|---|
| `inpatient.bed` | `{admissionId}:{yyyy-MM-dd}` | bed class of the ward occupied at the census point (`BED-GEN`, `BED-SEMI`, `BED-PVT`, `BED-DLX`, `BED-ICU`, `BED-NICU`) |
| `inpatient.nursing` | `{admissionId}:{yyyy-MM-dd}` | `NURS-DAY` |
| `inpatient.visit` | consultant visit id | `VISIT-DR` or `VISIT-DR-ICU` (ICU/NICU/PICU/HDU) |

Bed and nursing charges use different source modules so the two charges of one day never collide. A local ledger
(`bed_day_charges`) records accepted charges. Day counting follows facility setting `inpatient.bedDayPolicy`:
`MidnightCensus` (default: a day is charged when the patient is in bed at midnight; a stay crossing no midnight is one
day) or `CalendarDay` (every calendar day touched). Due days are posted at discharge initiation and before the
clearance check, so the final bill is complete.

## Settings, numbers, notifications, jobs, seed
* Settings: `inpatient.bedDayPolicy` (`MidnightCensus` | `CalendarDay`), `inpatient.marGraceMinutes` (5–240, default 30), `inpatient.verbalOrderCountersignHours` (1–72, default 24; lengthening either waits for a second administrator, SettingGuard).
* Number series: `admission` → `{FAC}IP{YY}{SEQ:6}`, yearly reset.
* Notifications (platform templates, SMS): `admission.admitted` {name, ward, bed, doctor}; `discharge.completed` {name}.
* Job: `CountersignOverdueJob` — every 15 minutes when `PlatformOptions.RunsJobs`: tells the doctor and the nurses in charge of each verbal or telephone order, and each nurse's stop, past its countersign time (once each).
* Job: `BedDayChargeJob` — runs at start-up and hourly when `PlatformOptions.RunsJobs`; posts missing bed/nursing days of active and recently discharged admissions (`InpatientChargeService.StaysDueAsync`) and retries unaccepted consultant-visit charges. A day or visit billing refuses is kept by billing until it is charged: System health warns of it, the stay's final bill and discharge wait for it, and the stay's days are posted again however long ago it ended (`docs/modules/billing.md`, automatic charges); a ward's bed charge, and the one a stay in progress took with its bed, can't be taken out of use meanwhile (`docs/modules/masters.md`). Each bed day tells Billing whether the bed was in a critical-care ward (`Ward.IsCriticalCareType`: ICU, NICU, PICU, HDU) or a room, which decides its GST (room rent above ₹5,000 a day, see `docs/modules/billing.md`).
* Seed `InpatientSeeder` (Order 160, only with `SeedDemoData`): GWM General Ward — Male (12, BED-GEN), GWF General Ward — Female (12, BED-GEN), SPW Semi-Private (8 in 4 rooms, BED-SEMI), PVT Private (8 rooms, BED-PVT), DLX Deluxe (4 rooms, BED-DLX), ICU (8, BED-ICU, monitors, 4 ventilators), NICU (6, BED-NICU).

## Tables (schema `inpatient`)
`wards`, `rooms`, `beds`, `housekeeping_tasks`, `admissions`, `bed_assignments`, `bed_day_charges`,
`consultant_visits`, `nursing_notes`, `shift_handovers`, `intake_output_entries`, `nursing_assessments`,
`medication_orders`, `medication_administrations`. The policy number is AES-GCM encrypted (`IFieldProtector`).
