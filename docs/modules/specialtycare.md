# SpecialtyCare module — Operation Theatre, Dialysis, Physiotherapy, Blood Bank

Project `src/server/Modules/HyFib.HealthCare360.Modules.SpecialtyCare` · schema `specialtycare` · layer 6 (references Patients and SupplyChain)
· SRS **OT-01..07, DIA-01..05, PHY-01..04, BB-01..07**.

All times are UTC `timestamptz`; `date` query parameters are hospital-local (IST) calendar dates. Money is INR.
Enums are stored as text, so lists never sort on an enum column directly: priorities, shifts, sero categories, component types and
surgery status are ranked in their declared/clinical order (`SqlOrdering`, SQL `CASE`).
Every endpoint requires the permission shown. Detail views of patient records call `PatientAccessGuard.EnsureCanOpenAsync`
and write a PHI-access audit entry. Donor detail views write a `donor.accessed` audit entry.

## Patient-safety rules enforced on the server

| Rule | Where | Tests |
|---|---|---|
| Surgery cannot start (knife to skin) before the WHO **Sign-in** and **Time-out** are complete, and cannot be completed before the **Sign-out**. Phases run in order, each item records who confirmed it and when, and a completed phase is locked. Sign-in requires a complete pre-op checklist. | `SurgeryBooking.Start/Complete/ConfirmSafety` | `SurgicalSafetyChecklistTests` |
| No double booking of a theatre, surgeon, anaesthetist or patient. Overlap uses half-open intervals, so back-to-back cases are allowed. The check runs under PostgreSQL advisory locks, across all facilities. | `SurgeryScheduling`, `SurgeryService`, `SchedulingLocks` | `SurgerySchedulingTests` |
| The OT store look-up offers only stock that can be issued now (not expired, blocked or fully reserved), from the same OT store materials are consumed from, with batches in FEFO order. | `OtStoreLookup` | `OtStoreLookupTests` |
| A dialysis machine must match the patient's sero status (Negative/HBsAg/HCV/HIV). This is checked when the session is scheduled and again when it starts. The machine must also be disinfected after its last session and not be running another one. | `SeroSegregation`, `DialysisMachine.EnsureReadyFor` | `SeroSegregationTests`, `DialysisSessionTests` |
| A dialyzer is used only for its own patient and only up to its maximum uses. HBsAg-positive patients are always single-use. | `Dialyzer.Use`, `DialysisPatientProfile.EffectiveMaxDialyzerUses` | `DialyzerTests` |
| Donor eligibility (NBTC 2017): Hb ≥ 12.5 g/dL; weight ≥ 45 kg (≥ 55 kg for 450 ml); age 18–65 (first-time donors ≤ 60); BP 100–140/60–90; pulse 60–100; temperature ≤ 37.5 °C; whole-blood interval 90 days (men) / 120 days (women); questionnaire deferrals. | `DonorEligibility` | `DonorEligibilityTests` |
| A unit leaves quarantine only when it is grouped **and** all five TTI markers are non-reactive. Any reactive marker discards the unit and defers the donor. | `BloodComponent.ApplyLabResults` | `BloodComponentTests` |
| ABO/Rh(D) compatibility table: PRBC compatible, whole blood ABO-identical, plasma/platelets/cryo plasma-compatible, Bombay handled, unknown group never compatible. | `BloodCompatibility` | `BloodCompatibilityTests` |
| Issue gate: red cells need a compatible cross-match done within 72 h. A doctor-authorised emergency release allows only uncross-matched O Rh(D)-negative PRBC, or AB plasma when the patient is ungrouped. Expired units, units that have not cleared TTI, and units reserved for another request are never issued. | `IssueCompatibility`, `BloodComponent.Issue` | `IssueCompatibilityTests` |
| Cold chain: a returned unit is restocked only if it comes back within 30 min with temperature maintained. Thawed FFP/cryo are never restocked. | `ColdChain` | `BloodComponentTests` |
| Shelf life from collection: WB 35 d, PRBC 35 d, FFP 365 d, platelets 5 d, cryo 365 d. Plasma and platelets must be separated within 8 h. | `ComponentShelfLife` | `ComponentShelfLifeTests` |
| Blood-bank grouping of the patient sample is used for compatibility, never the group from registration. A second grouping that disagrees is refused (possible wrong blood in tube). | `BloodRequest.RecordGrouping` | `IssueCompatibilityTests` |

## Endpoints

### Operation theatre — `/api/v1/ot` (tag *Operation Theatre*)
| Method | Path | Permission | Request → Response (key fields) |
|---|---|---|---|
| GET | `/theatres?includeInactive=` | `ot.read` | → `TheatreDto[]` (code, name, type, equipment, isActive, version) |
| POST | `/theatres` | `ot.theatres.manage` | `SaveTheatreRequest` (code, name, type Major/Minor/Cardiac/Ortho/Emergency, equipment[], location) → 201 `{id}` |
| PUT | `/theatres/{id}` | `ot.theatres.manage` | `SaveTheatreRequest` (+version) → 204 |
| GET | `/calendar?date=&view=day\|week&theatreId=&surgeonId=` | `ot.read` | → `OtCalendar` {theatres[{bookings: `OtCalendarBooking`}], awaitingSlot[]}. Use `surgeonId` for the surgeon calendar |
| GET | `/utilisation?from=&to=&theatreId=&minutesPerDay=480` | `ot.reports.read` | → `OtUtilisationReport` per theatre: completed/cancelled/postponed, bookedMinutes, actualMinutes, actualToBookedPercent, utilisationPercent, firstCaseOnTimePercent (≤ 15 min), averageTurnoverMinutes (gaps ≤ 120 min) |
| GET | `/implants/trace?lot=&serial=` | `ot.theatres.manage` | → `ImplantTraceItem[]` for a device recall (phone masked; audited) |
| GET | `/bookings?from=&to=&status=&theatreId=&surgeonId=&search=&sort=scheduledStart\|bookingNumber\|status\|priority\|createdAt` | `ot.read` | → paged `SurgeryListItem` |
| GET | `/bookings/{id}` | `ot.read` | → `SurgeryDetail` (patient summary, team, slot, status, timestamps, pre-op and WHO checklists with items and attestations, charge status) |
| POST | `/bookings` | `ot.bookings.manage` | `CreateSurgeryBookingRequest` (patientId, admissionId?, procedureServiceItemId?, procedureName, laterality, surgeonId, assistantIds, anaesthetistId, anaesthesiaType GA/Spinal/Epidural/Regional/Local/Sedation, priority Elective/Urgent/Emergency, preferredDate, optional theatreId+scheduledStart+scheduledEnd) → 201 `SurgeryCreated` (id, bookingNumber, status). 409 `surgery.slot_conflict` with the conflicts |
| PUT | `/bookings/{id}` | `ot.bookings.manage` | `UpdateSurgeryPlanRequest` (procedure, team, anaesthesia, priority, version) → 204. A booked slot is re-checked for conflicts |
| POST | `/bookings/{id}/schedule` | `ot.bookings.manage` | `ScheduleSurgeryRequest` (theatreId, scheduledStart, scheduledEnd, version) → 204 / 409 |
| POST | `/bookings/{id}/postpone` · `/cancel` | `ot.bookings.manage` | `SurgeryReasonRequest` (reason) → 204. Postpone resets the WHO checklist |
| POST | `/bookings/{id}/receive` | `ot.checklists.record` | → 204 (Scheduled → InPreOp) |
| PUT | `/bookings/{id}/pre-op-checklist` | `ot.checklists.record` | `RecordChecklistRequest` {items[{key, state Pending/Done/NotApplicable, remarks}]} → 204 |
| POST | `/bookings/{id}/safety-checklist/{sign-in\|time-out\|sign-out}` | `ot.checklists.record` | `RecordChecklistRequest` → 204 |
| POST | `/bookings/{id}/start` · `/complete` | `ot.surgery.perform` | → 204. Complete posts the OT charges |
| POST | `/bookings/{id}/charges/retry` | `ot.bookings.manage` | → `ChargeOutcome` (status, error) |
| GET/PUT | `/bookings/{id}/anaesthesia` | `ot.read` / `ot.anaesthesia.record` | `SaveAnaesthesiaRecordRequest` (asaGrade I–VI + E, technique, airway, drugs[], fluids[], blood loss, complications, recovery notes, sign, version) → `AnaesthesiaRecordDto`. Signed records are locked |
| POST | `/bookings/{id}/anaesthesia/vitals` | `ot.anaesthesia.record` | `AddAnaesthesiaVitalsRequest` {rows[at, HR, BP, SpO₂, EtCO₂, temp, RR]} → `AnaesthesiaRecordDto` |
| GET/PUT | `/bookings/{id}/operation-note` | `ot.read` / `ot.surgery.perform` | `SaveOperationNoteRequest` (diagnoses, procedure performed, findings, details, specimens[], drains, blood loss, complications, **postOpOrders**, sign) → `OperationNoteDto` |
| GET | `/bookings/{id}/materials` | `ot.read` | → `SurgeryMaterialDto[]` |
| GET | `/store-items?search=&take=` | `ot.materials.record` | → `OtStoreItem[]` for the materials dialog: active items with issuable stock (unreserved, unexpired, unblocked) in the facility's OT store — the store `POST /bookings/{id}/materials` issues from, resolved with `StockService.ResolveStoreAsync(facility, OT)`. Fields: itemId, code, name, genericName, manufacturer, type, baseUnit, available, nearestExpiry, mrp (of the batch FEFO issues first) and `batches[]` (batchId, batchNumber, expiryDate, available, mrp; the first 3 in FEFO order). Search matches code, barcode, name, generic or brand; name order; `take` 20 by default, at most 50. 422 `stock.no_store` when the facility has no active OT store, `stock.store_ambiguous` when it has more than one, `facility.required` without a signed-in facility. Record the pick with `AddSurgeryMaterialRequest` (`supplyItemId = itemId`, `itemName = name`, `lotNumber = batchNumber`, `expiryDate`) |
| POST | `/bookings/{id}/materials` | `ot.materials.record` | `AddSurgeryMaterialRequest` (kind Implant/Consumable, supplyItemId?, itemName, manufacturer, lot/serial (required for implants), expiry, quantity, unitPrice) → 201 `SurgeryMaterialDto`. With `supplyItemId` the item is issued from the facility's OT store (stock ledger source `specialtycare.ot`), in the same transaction as the charge; without it (consignment or patient-supplied) only the charge is posted. Stock errors (no OT store, not enough stock) are returned as 422 |
| DELETE | `/bookings/{id}/materials/{materialId}` | `ot.materials.record` | → 204 (withdraws the charge first, then returns the item to the OT store) |
| GET | `/api/v1/patients/{patientId}/surgeries` | `ot.read` | → paged surgery history |

### Dialysis — `/api/v1/dialysis` (tag *Dialysis*)
| Method | Path | Permission | Request → Response |
|---|---|---|---|
| GET | `/machines?includeInactive=` | `dialysis.read` | → `DialysisMachineDto[]` (code, model, station, seroDedication, status, lastDisinfectedAt, needsDisinfection) |
| POST / PUT | `/machines`, `/machines/{id}` | `dialysis.machines.manage` | `SaveDialysisMachineRequest` → 201/204. The sero dedication can be changed only when the machine is idle and disinfected |
| POST | `/machines/{id}/disinfection` | `dialysis.sessions.manage` | `RecordDisinfectionRequest` (method) → 204 |
| GET | `/patients?sero=&search=` · `/patients/{patientId}` | `dialysis.read` | → paged / single `DialysisProfileDto` (dry weight, access AVF/AVG/Catheter + site, scheduleDays, shift, seroStatus, seroTestedOn, seroRescreeningDue, reuse policy, effectiveMaxDialyzerUses) |
| PUT | `/patients/{patientId}` | `dialysis.patients.manage` | `SaveDialysisProfileRequest` → `{id}` (enrol or update) |
| GET / POST | `/patients/{patientId}/dialyzers` | `dialysis.read` / `dialysis.sessions.manage` | `RegisterDialyzerRequest` (serialNumber, brand, model, surfaceArea) → `DialyzerDto` (usesCount, maxUses, remainingUses, discarded) |
| POST | `/dialyzers/{id}/discard` | `dialysis.sessions.manage` | `DialysisReasonRequest` → 204 |
| GET | `/board?date=&shift=` | `dialysis.read` | → `DialysisBoard` (machines grouped by sero category with their sessions, unassigned sessions, patients due today by weekly schedule but not booked) |
| GET | `/sessions?from=&to=&status=&patientId=&search=` · `/sessions/{id}` | `dialysis.read` | → paged `DialysisSessionListItem` / `DialysisSessionDetail` |
| POST | `/sessions` | `dialysis.sessions.manage` | `ScheduleDialysisRequest` (patientId, scheduledDate, shift, machineId?) → 201 `{id}` |
| PUT | `/sessions/{id}/machine` | `dialysis.sessions.manage` | `AssignDialysisMachineRequest` → 204 |
| POST | `/sessions/{id}/pre-assessment` | `dialysis.sessions.manage` | `PreDialysisRequest` (weight, BP, pulse, temp, targetUf) → 204 |
| POST | `/sessions/{id}/start` | `dialysis.sessions.manage` | `StartDialysisRequest` (machineId, dialyzerId **or** newDialyzer, heparin loading/hourly) → 204 |
| POST | `/sessions/{id}/readings` | `dialysis.sessions.manage` | `AddDialysisReadingsRequest` (hourly BP, pulse, blood flow, UF rate, venous/arterial/TMP) → 204 |
| POST | `/sessions/{id}/complete` | `dialysis.sessions.manage` | `CompleteDialysisRequest` (post weight, BP, pulse, UF achieved (defaults to weight lost), complications[]) → 204; posts charges |
| POST | `/sessions/{id}/cancel` | `dialysis.sessions.manage` | `CancelDialysisRequest` (reason, patientDidNotCome → Missed) → 204 |
| POST | `/sessions/{id}/charges/retry` | `dialysis.sessions.manage` | → `ChargeOutcome` |

### Physiotherapy — `/api/v1/physio` (tag *Physiotherapy*)
| Method | Path | Permission | Request → Response |
|---|---|---|---|
| GET | `/referrals?status=&openOnly=true&search=` | `physio.read` | → paged `PhysioReferralListItem` (stat/urgent first) |
| GET | `/referrals/{id}` | `physio.read` | → `PhysioReferralDetail` (assessments, plan, sessions) |
| POST | `/referrals` | `physio.referrals.manage` | `CreatePhysioReferralRequest` (patientId, priority, reason, sessionsOrdered?) → 201 `{id}` |
| POST | `/referrals/{id}/cancel` | `physio.referrals.manage` | `PhysioReasonRequest` → 204 (refused once treatment has been given) |
| POST | `/referrals/{id}/complete` | `physio.treatment.manage` | `PhysioReasonRequest` (discharge summary) → 204 |
| POST | `/referrals/{id}/assessments` | `physio.treatment.manage` | `RecordPhysioAssessmentRequest` (type Initial/Review/Discharge, complaint, pain 0–10, rangeOfMotion[joint, movement, side, active/passive °], strength[muscle, side, MRC 0–5], functional status, goals) → 201 |
| PUT | `/referrals/{id}/plan` | `physio.treatment.manage` | `SaveTreatmentPlanRequest` (modalities IFT/TENS/UST/SWD/HotPack/Traction/Exercises/…, exercise programme, sessionsPlanned (empty = the referral's sessionsOrdered), sessionsPerWeek, minutes, startDate, version) → `{id}`. Requires an assessment |
| POST | `/referrals/{id}/sessions` | `physio.treatment.manage` | `SchedulePhysioSessionsRequest` (firstSessionAt, count, intervalDays?) → `Guid[]`. Cannot exceed the plan |
| GET | `/sessions?date=&status=` | `physio.read` | → `PhysioWorklistItem[]` (day list) |
| PUT | `/sessions/{id}` | `physio.treatment.manage` | `ReschedulePhysioSessionRequest` → 204 |
| POST | `/sessions/{id}/attend` | `physio.treatment.manage` | `AttendPhysioSessionRequest` (modalities, progress note, pain before/after, minutes) → 204; posts charges |
| POST | `/sessions/{id}/close` | `physio.treatment.manage` | `ClosePhysioSessionRequest` (reason, missed) → 204 |
| POST | `/sessions/{id}/charges/retry` | `physio.treatment.manage` | → `ChargeOutcome` |

### Blood bank — `/api/v1/blood-bank` (tag *Blood Bank*)
| Method | Path | Permission | Request → Response |
|---|---|---|---|
| GET | `/donor-questions` | `bloodbank.donors.manage` | → `DonorQuestionDto[]` (screening questionnaire with deferral periods) |
| GET | `/donors?bloodGroup=&eligibleOnly=&search=` · `/donors/{id}` | `bloodbank.donors.manage` | → paged `DonorListItem` (phone masked, nextEligibleOn) / `DonorDetail` (screenings, donations) |
| POST / PUT | `/donors`, `/donors/{id}` | `bloodbank.donors.manage` | `SaveDonorRequest` (name, gender, DOB, group, Indian mobile, address, consentToNotify) → 201 `{id}` / 204. 409 `bloodbank.donor_exists` |
| POST | `/donors/{id}/defer` | `bloodbank.donors.manage` | `DeferDonorRequest` (until, permanent, reason) → 204 |
| POST | `/donors/{id}/screenings` | `bloodbank.donors.manage` | `ScreenDonorRequest` (Hb, weight, BP, pulse, temperature, answers[{key, yes}]) → `DonorScreeningDto` (outcome Eligible/TemporarilyDeferred/PermanentlyDeferred, reasons, deferredUntil, maxBagVolumeMl) |
| POST | `/donors/{id}/donations` | `bloodbank.donors.manage` | `CollectDonationRequest` (screeningId, bagNumber (scan) or generated, bagType Single/Double/Triple/Quadruple, volume 350/450, type Voluntary/Replacement, replacementForPatientId) → 201 `{id}` |
| GET | `/donations?status=&pendingResults=&search=` · `/donations/{id}` | `bloodbank.inventory.manage` | → paged `DonationListItem` / `DonationDetail` (TTI panel, components) |
| POST | `/donations/{id}/components` | `bloodbank.inventory.manage` | `SeparateComponentsRequest` {components[{type WholeBlood/PRBC/FFP/PlateletConcentrate/Cryoprecipitate, volumeMl, storageLocation}]} → `BloodUnitDto[]` (in quarantine) |
| PUT | `/donations/{id}/lab-results` | `bloodbank.inventory.manage` | `RecordLabResultsRequest` (bloodGroup, hiv, hbsAg, hcv, syphilis, malaria: Pending/NonReactive/Reactive) → 204; releases or discards units, defers reactive donors, alerts officers |
| GET | `/inventory` | `bloodbank.read` | → `BloodInventoryDashboard` (stock cells by component × group: available/reserved/quarantine/expiringSoon, units expiring within 3 days, open and emergency requests) |
| GET | `/units?status=&component=&bloodGroup=&search=` · `/units/by-number/{unitNumber}` | `bloodbank.read` | → paged `BloodUnitDto` / barcode look-up |
| POST | `/units/{id}/discard` | `bloodbank.inventory.manage` | `DiscardUnitRequest` → 204 |
| GET | `/requests?status=&openOnly=true&patientId=&search=` · `/requests/{id}` | `bloodbank.read` | → paged `BloodRequestListItem` (emergency first) / `BloodRequestDetail` (cross-matches, issues) |
| POST | `/requests` | `bloodbank.requests.create` | `CreateBloodRequestRequest` (patientId, component, units, urgency Routine/Urgent/Emergency, indication, requiredBy, pre-transfusion Hb/platelets) → 201 `{id, requestNumber}`. Emergency requests alert issuers |
| POST | `/requests/{id}/cancel` | `bloodbank.requests.create` | `BloodReasonRequest` → 204 (frees reserved units) |
| POST | `/requests/{id}/grouping` | `bloodbank.crossmatch.perform` | `GroupPatientRequest` (bloodGroup) → 204 |
| POST | `/requests/{id}/emergency-release` | `bloodbank.units.issue` | `EmergencyReleaseRequest` (authorisedBy doctor, reason) → 204 (emergency requests only; audited) |
| GET | `/requests/{id}/compatible-units` | `bloodbank.crossmatch.perform` | → `CompatibleUnitDto[]` (compatible, in date, first-expiry-first-out) |
| POST | `/requests/{id}/crossmatches` | `bloodbank.crossmatch.perform` | `RecordCrossMatchRequest` (componentId, method, result) → `CrossMatchDto`; a compatible result reserves the unit |
| POST | `/requests/{id}/issues` | `bloodbank.units.issue` | `IssueBloodRequest` (componentId, issuedToName) → `BloodIssueDto` |
| POST | `/issues/{id}/return` | `bloodbank.units.issue` | `ReturnBloodRequest` (temperatureMaintained, notes) → `BloodIssueDto` (restocked or discarded) |
| POST | `/issues/{id}/reactions` | `bloodbank.reactions.report` | `ReportReactionRequest` (onset, symptoms[], vitals, transfusionStopped, volume, actions, severity) → 201 `{id}`; alerts investigators |
| GET | `/reactions?status=` | `bloodbank.read` | → paged `TransfusionReactionDto` |
| POST | `/reactions/{id}/investigate` · `/close` | `bloodbank.reactions.investigate` | `CloseReactionRequest` (type (HvPI classification), severity, findings, reportedToHaemovigilance) → 204 |

**Unit labels (BB-03).** From a donation's components and from a unit, the staff app prints a label per unit,
without a dialog, on the workstation's label printer: blood group with POS or NEG in full, component and volume, the
unit number as Code 128 for scanning at issue and at the bedside, expiry and storage. A unit not yet cleared by
screening is labelled QUARANTINE / NOT FOR ISSUE in white on black. On a ZPL or TSPL printer the server writes the
label (`blood-units`); otherwise the label page prints. See [integrations.md](integrations.md#barcode-labels-int-bar).

## Permissions and default roles
| Permission | Default roles |
|---|---|
| `ot.read` | OtCoordinator, Doctor, Nurse, HospitalAdmin |
| `ot.theatres.manage` | OtCoordinator, HospitalAdmin |
| `ot.bookings.manage` | OtCoordinator, Doctor |
| `ot.checklists.record` | Nurse, Doctor, OtCoordinator |
| `ot.surgery.perform`, `ot.anaesthesia.record` | Doctor |
| `ot.materials.record` | Nurse, OtCoordinator, Doctor |
| `ot.reports.read` | OtCoordinator, HospitalAdmin, MisAnalyst |
| `dialysis.read` | DialysisTechnician, Nurse, Doctor, HospitalAdmin |
| `dialysis.machines.manage` | DialysisTechnician, HospitalAdmin |
| `dialysis.patients.manage`, `dialysis.sessions.manage` | Doctor, DialysisTechnician, Nurse |
| `physio.read` | Physiotherapist, Doctor, Nurse, HospitalAdmin |
| `physio.referrals.manage` | Doctor, Physiotherapist |
| `physio.treatment.manage` | Physiotherapist |
| `bloodbank.read` | BloodBankOfficer, Doctor, Nurse, HospitalAdmin |
| `bloodbank.donors.manage`, `bloodbank.inventory.manage`, `bloodbank.crossmatch.perform`, `bloodbank.units.issue`, `bloodbank.reactions.investigate` | BloodBankOfficer |
| `bloodbank.requests.create`, `bloodbank.reactions.report` | Doctor, Nurse, BloodBankOfficer |

## Events
**Raised:** `SurgeryCompleted` (Platform) by `SurgeryBooking.Complete`, when the patient leaves the theatre after the WHO
Sign-out: booking, patient, admission/visit, procedure code and name, side, surgeon, knife-to-skin and completion times.
Clinical records it as a procedure on the patient's record (EHR-01). Otherwise the module consumes clinical orders and ADT.

**Handled** (in the publisher's transaction):
| Event | Handler | Effect |
|---|---|---|
| `ServiceOrderPlaced` (Category **Physiotherapy**) | `PhysioOrderHandler` | Creates a `PhysioReferral` (idempotent on `OrderItemId`); order `Quantity` = sessions prescribed (`sessionsOrdered`), the default for the treatment plan |
| `ServiceOrderPlaced` (Category **BloodBank**, codes BB-WB/PRBC/FFP/PLT/CRYO) | `BloodOrderHandler` | Creates a `BloodRequest` for order `Quantity` units (at least 1; Stat → Emergency) |
| `ServiceOrderPlaced` (Category **Surgery**) | `SurgeryOrderHandler` | Creates a *Requested* `SurgeryBooking` with the ordering doctor as surgeon |
| `ServiceOrderCancelled` | the three handlers above | Cancels the work item if no treatment, issue or surgery has happened yet, and frees reserved blood |
| `PatientAdmitted` / `PatientDischarged` / `AdmissionCancelled` | `AdmissionEventHandlers` | Maintains `specialtycare.admissions`, so charges go to the running IP bill. On discharge or cancellation: cancels waiting surgeries, cancels unissued blood requests (frees reserved units), and detaches future dialysis sessions from the closed admission |
| `PatientsMerged` (Patients) | `PatientMergeHandler` | Re-points every patient-linked row to the surviving UHID (all facilities) |

## Charges (`IChargePoster`, idempotent on SourceModule + SourceReference)
| SourceModule | SourceReference | Service code | When |
|---|---|---|---|
| `specialtycare.ot` | `{bookingId}:procedure` | booking's procedure item (PractitionerId = surgeon) | surgery completed |
| | `{bookingId}:theatre-time` | OT-MAJOR-HR / OT-MINOR-HR × billable hours (min 1 h, then ½-h slabs) | surgery completed |
| | `{bookingId}:surgeon-fee` · `{bookingId}:anaesthetist-fee` | OT-SURGEON-FEE / OT-ANAES-FEE (PractitionerId set) | surgery completed |
| | `{bookingId}:material:{id}` | OT-IMPLANT / OT-CONSUMABLE with UnitPriceOverride | item recorded (cancelled if removed) |
| `specialtycare.dialysis` | `{sessionId}:session` · `dialyzer:{id}` | DIA-SESSION · DIA-DIALYZER (first use) | session completed |
| `specialtycare.physio` | `{sessionId}:{code}` | PHY-IFT/TENS/UST/EXERCISE, plus PHY-SESSION for other modalities | session attended |
| `specialtycare.bloodbank` | `crossmatch:{id}` · `issue:{id}` | BB-CROSSMATCH · BB-WB/PRBC/FFP/PLT/CRYO | cross-match · issue (cancelled if restocked on return) |

Charge failures never block clinical work. The outcome is stored (`chargeStatus`, `chargeError`) and can be retried from the `/charges/retry` endpoints.
`DialysisChargeTests` (`tests/HyFib.HealthCare360.IntegrationTests`) checks the dialysis charges in Billing end to end: a session on a new dialyzer
charges the session and the dialyzer, a session reusing it charges the session only, and retrying either session's charges posts nothing twice.

## Number series
| Key | Format | Reset |
|---|---|---|
| `ot-booking` | `{FAC}OT{YY}{SEQ:6}` | yearly, per facility |
| `dialysis-session` | `{FAC}DL{YY}{SEQ:6}` | yearly, per facility |
| `blood-request` | `{FAC}BR{YY}{SEQ:6}` | yearly, per facility |
| `blood-bag` | `{FAC}BB{YY}{SEQ:6}` (when no pre-printed bag barcode is scanned) | yearly, per facility |
| `blood-donor` | `{FAC}DN{SEQ:6}` | never (group-wide) |

## Notifications and real time
- **In-app** (`NotifyPermissionHoldersAsync`, no SMS templates): emergency blood request → `bloodbank.units.issue`; reactive TTI (donor recall) → `bloodbank.donors.manage`; transfusion reaction → `bloodbank.reactions.investigate`; daily expired / expiring-in-3-days summary → `bloodbank.inventory.manage`.
- **SignalR** (ids only): `ot:{facilityId}` `ot.booking.changed`; facility group `dialysis.board.changed`, `physio.worklist.changed`, `bloodbank.changed`.

## Background job
`BloodUnitExpiryJob` runs at start-up and then daily at 00:05 IST (skipped when the node role is API-only). It marks in-stock units past expiry as *Expired*, releases their reservations, and sends the in-app summary. Issue and reservation also check expiry themselves, so the job is housekeeping, not the safety gate.

## Seeded data (`SpecialtyCareSeeder`, order 170)
Service items, INR (editable items marked *): OT-MAJOR-HR 4000, OT-MINOR-HR 1500, OT-SURGEON-FEE 10000*, OT-ANAES-FEE 4000*,
OT-IMPLANT 0*, OT-CONSUMABLE 0*, PROC-LSCS 35000*, PROC-APPENDECTOMY 30000*, PROC-LAP-CHOLE 55000*, PROC-TKR 150000*, PROC-HERNIA 35000*,
DIA-SESSION 1800, DIA-DIALYZER 1200*, PHY-IFT 300, PHY-TENS 250, PHY-UST 300, PHY-EXERCISE 350, PHY-SESSION 400, BB-WB 1450, BB-PRBC 1450,
BB-FFP 400, BB-PLT 400, BB-CRYO 250, BB-CROSSMATCH 250.
With `SeedDemoData`: theatres OT-1 (Major), OT-2 (Ortho), OT-3 (Minor); dialysis machines HD-01..03 (Negative), HD-04 (HBsAg), HD-05 (HCV), HD-06 (HIV).

## Tables (`specialtycare` schema)
`theatres`, `surgery_bookings` (pre-op and WHO checklists as `jsonb`), `anaesthesia_records` (vitals, drugs and fluids as `jsonb`), `operation_notes`,
`surgery_materials`, `dialysis_machines`, `dialysis_profiles`, `dialyzers`, `dialysis_sessions` (filtered unique indexes: one active session per
machine per shift and per patient per shift), `physio_referrals`, `physio_assessments`, `physio_treatment_plans`, `physio_sessions`, `donors`,
`donor_screenings`, `donations`, `blood_components`, `blood_requests`, `crossmatches`, `blood_issues`, `transfusion_reactions`, `admissions`.
