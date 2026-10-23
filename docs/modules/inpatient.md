# Inpatient module (`inpatient`)

ADT (admission, discharge, transfer), ward and bed management with a live bed board, housekeeping, occupancy and
census statistics, automatic bed/nursing/consultant-visit charges, and nursing documentation including the medication
administration record. SRS **WB-01..06**, IPB auto charges (IPB-01), PH-03 MAR feed.

Project: `src/server/Modules/HyFib.HealthCare360.Modules.Inpatient` (references Patients, and Clinical for the ward
round). Schema `inpatient`. Tests: `tests/Modules/HyFib.HealthCare360.Modules.Inpatient.Tests` (188 unit tests).

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
| POST | `/` | `inpatient.admissions.admit` | `AdmitPatientRequest` (patientId, bedId, type, source, sourceVisitId, departmentId, admitting/consultant practitioner, provisionalDiagnosis, payerType/payerId, policyNumber, isMlc/mlcNumber, expectedDischargeDate, attendant*, admittedAt ≤ 3 days back, notifyPatient) → 201 `AdmissionCreated` (id, admissionNumber, visitId, visitNumber, bedId, bedLabel) |
| PUT | `/{id}` | `inpatient.admissions.admit` | `UpdateAdmissionRequest` (+`version`; consultant, department, diagnosis, expected discharge, policy number, MLC, attendant, notes) → 204. The payer is fixed at admission (changed in Billing). |
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
| POST | `/complete` | `inpatient.admissions.discharge` | `CompleteDischargeRequest` (dischargeType Normal/Referred/Lama/Dama/Absconded/Expired, dischargedAt ≤ 48 h back, notes — required for LAMA/DAMA/absconded, notifyPatient) → `DischargeResult`; 422 `discharge.billing_pending` (`data.outstanding`) until Billing clears |
| GET | `/api/v1/inpatient/discharges/pending?wardId` | `inpatient.admissions.read` | → `PendingDischargeItem[]` |

Rules: routine discharges (Normal, Referred, LAMA, DAMA) must be initiated and cleared by Billing; death and abscondence
are recorded immediately without clearance and without the "discharged" SMS. Completion closes the visit, closes the
bed assignment and sends the bed to housekeeping.

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

Reads are PHI-audited and honour restricted records; writes need the chart open (admitted, in discharge, or ≤ 24 h
after discharge). Vitals are owned by Clinical and are not duplicated here.

### Medication chart and MAR — `/api/v1/nursing`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/admissions/{admissionId}/medication-orders?activeOnly` | `nursing.mar` or `nursing.chart` | → `MedicationOrderDto[]` (incl. `roundTimes`) |
| POST | `/admissions/{admissionId}/medication-orders` | `nursing.mar` | `AddMedicationOrderRequest` (transcribed order; next 24 h scheduled) → 201 |
| POST | `/medication-orders/{id}/stop` | `nursing.mar` | `StopMedicationOrderRequest` → `MedicationOrderDto` (future doses cancelled) |
| POST | `/medication-orders/{id}/schedule` | `nursing.mar` | → `ScheduleResult` (extend 24 h) |
| GET | `/admissions/{admissionId}/mar?date` | `nursing.mar` or `nursing.chart` | → `MarEntryDto[]` (day + older undocumented doses, `isOverdue`) |
| POST | `/admissions/{admissionId}/mar` | `nursing.mar` | `AddAdHocDoseRequest` (SOS/STAT; `givenNow`) → 201 `MarEntryDto` |
| POST | `/mar/{id}/record` | `nursing.mar` | `RecordAdministrationRequest` (outcome Given/Held/Refused/Missed, administeredAt, reason — required unless given, batch, witness — required for controlled drugs, notes, `version`) → `MarEntryDto` |
| GET | `/wards/{wardId}/mar/due?withinMinutes` | `nursing.mar` | → `WardMarItem[]` (ward due/overdue worklist) |

Frequencies understood: OD/QD/Daily, BD/BID, TDS/TID, QID/QDS, HS/Nocte, `1-0-1`-style patterns (3 or 4 parts,
fractions allowed), `Q4H`/`6 hourly`…, STAT (one dose), SOS/PRN/"As directed" (never auto-scheduled).

## Integration events
| Event | Direction | Notes |
|---|---|---|
| `PatientAdmitted` | raised | on admission; includes `WardId`, `BedId`, `BedServiceItemId` (ward tariff), `PayerType` |
| `PatientTransferred` | raised | includes `FromBedId`, `ToBedId`, `ToBedServiceItemId`, `ToWardId` |
| `AdmissionCancelled` | raised | on cancellation, with the reason. The admission's visit is cancelled in the same transaction (Billing reacts to `VisitCancelled`); Dietary stops the diet order and future meals, Pharmacy cancels pending ward indents, SpecialtyCare cancels waiting surgeries and unissued blood requests |
| `DischargeInitiated` | raised | on initiation (and on direct completion of death/abscondence) |
| `DischargeRevoked` | raised | when an initiated discharge is revoked: Billing opens the IP account again, so the stay is not settled while the patient is in |
| `PatientDischarged` | raised | `DischargeType` = `Normal`, `Referred`, `Lama`, `Dama`, `Absconded` or `Expired` |
| `VisitCreated` / `VisitCompleted` / `VisitCancelled` (Patients) | raised | the in-patient visit is created with Patients' numbering (`visit`, `{FAC}V{YY}{SEQ:7}`), completed on discharge, cancelled with the admission |
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
* Settings: `inpatient.bedDayPolicy` (`MidnightCensus` | `CalendarDay`), `inpatient.marGraceMinutes` (5–240, default 30).
* Number series: `admission` → `{FAC}IP{YY}{SEQ:6}`, yearly reset.
* Notifications (platform templates, SMS): `admission.admitted` {name, ward, bed, doctor}; `discharge.completed` {name}.
* Job: `BedDayChargeJob` — runs at start-up and hourly when `PlatformOptions.RunsJobs`; posts missing bed/nursing days of active and recently discharged admissions and retries unaccepted consultant-visit charges.
* Seed `InpatientSeeder` (Order 160, only with `SeedDemoData`): GWM General Ward — Male (12, BED-GEN), GWF General Ward — Female (12, BED-GEN), SPW Semi-Private (8 in 4 rooms, BED-SEMI), PVT Private (8 rooms, BED-PVT), DLX Deluxe (4 rooms, BED-DLX), ICU (8, BED-ICU, monitors, 4 ventilators), NICU (6, BED-NICU).

## Tables (schema `inpatient`)
`wards`, `rooms`, `beds`, `housekeeping_tasks`, `admissions`, `bed_assignments`, `bed_day_charges`,
`consultant_visits`, `nursing_notes`, `shift_handovers`, `intake_output_entries`, `nursing_assessments`,
`medication_orders`, `medication_administrations`. The policy number is AES-GCM encrypted (`IFieldProtector`).
