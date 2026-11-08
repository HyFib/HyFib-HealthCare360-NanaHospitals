# SpecialtyCare module — Operation Theatre, Dialysis, Physiotherapy, Blood Bank, Termination of Pregnancy

Project `src/server/Modules/HyFib.HealthCare360.Modules.SpecialtyCare` · schema `specialtycare` · layer 6 (references Patients and SupplyChain)
· SRS **OT-01..08, DIA-01..05, PHY-01..04, BB-01..07**; MTP Act 1971 (amended 2021) and MTP Regulations 2003.

All times are UTC `timestamptz`; `date` query parameters are hospital-local (IST) calendar dates. Money is INR.
Enums are stored as text, so lists never sort on an enum column directly: priorities, shifts, sero categories, component types and
surgery status are ranked in their declared/clinical order (`SqlOrdering`, SQL `CASE`).
Every endpoint requires the permission shown. Detail views of patient records call `PatientAccessGuard.EnsureCanOpenAsync`
and write a PHI-access audit entry. Donor detail views write a `donor.accessed` audit entry.

## Patient-safety rules enforced on the server

| Rule | Where | Tests |
|---|---|---|
| Surgery cannot start (knife to skin) before the WHO **Sign-in** and **Time-out** are complete, and cannot be completed before the **Sign-out**. Phases run in order, each item records who confirmed it and when, and a completed phase is locked. Sign-in requires a complete pre-op checklist. | `SurgeryBooking.Start/Complete/ConfirmSafety` | `SurgicalSafetyChecklistTests` |
| **Side or procedure after the Sign-in** (maker-checker slice 10). Before the WHO Sign-in the plan is edited as before. After it, a change of side, level or procedure is made only by the operating surgeon or the anaesthetist of the case, from their own sign-in, with a reason (403 `surgery.site_change_team`; 400 `changeReason`): the Sign-in and the Time-out done are voided (their confirmations kept in `checklistVoids` with who, why, before → after), the pre-op site mark is redone, the change is audited at Warning (`surgery.site_changed_after_sign_in`), and the case's team (surgeon, assistants, anaesthetist, nurse with an account) is told. After the incision the side or procedure is not changed (422 `surgery.site_after_incision`): what was found goes in the operation note. A change of side or level before the Sign-in also takes back a mark already recorded. | `SiteChangeRule`, `SurgeryBooking.VoidChecklistForSiteChange/ResetSiteMarkingIfMoved`, `SurgeryService.UpdatePlanAsync` | `SiteChangeTests`, `TheatreSiteChangeTests` |
| **Site marking** (slice 10). When the booking names a side (left, right, bilateral) or a level (`siteLevel`: a spinal level, a finger, a tooth), "site marked" is never "not applicable" on the pre-op checklist or at the Sign-in (422 `checklist.site_marking_required`), and the pre-op mark is recorded by a surgeon (`ot.surgery.perform`) from their own sign-in, so the record names who marked it (403 `checklist.site_marked_by_surgeon` for anyone else); the checklist sent again whole keeps the surgeon's mark. The Sign-in's "site marked" is confirmed only over a recorded mark (422 `surgery.site_not_marked`). Not applicable stays, with the reason, for a midline incision or a single organ. | `SiteMarking`, `SurgeryBooking.RecordPreOp/ConfirmSafety` | `SiteMarkingTests`, `TheatreSiteChangeTests` |
| **Swab, needle and instrument counts** (slice 10). Each count (initial, additional, final, per category) is made by the signed-in member of the team and witnessed by a second at the same screen (the scrub and the circulating nurse: `POST /api/v1/auth/witness`, purpose `ot.count`, `ot.checklists.record`). A final count that does not tally (initial + added) opens a discrepancy, audited at Warning; the WHO Sign-out records nothing while one is open (422 `surgery.count_discrepancy_open`). It is settled as found (where), an X-ray with no retained item (its result), or a retained item, which raises a never event in the Quality incident register (`IIncidentRegister`: sentinel, `RetainedForeignObject`, the harm graded, the quality team alerted) and names the incident. "Counts are correct" at the Sign-out is confirmed only from the counts: something counted and every category counted at the start counted at the end (422 `surgery.counts_missing`, `surgery.counts_incomplete`); "not applicable" only when nothing was counted (422 `surgery.counts_recorded`). Counts close with the Sign-out. | `SurgicalCounts`, `SurgeryBooking.RecordCount/ResolveDiscrepancy`, `OtClinicalService.RecordCountAsync/ResolveDiscrepancyAsync` | `SurgicalCountTests`, `TheatreCountTests` |
| **Recovery room (PACU)** (OT-08). From the WHO Sign-out until the discharge from recovery the case is *in recovery* (`inRecovery` on the detail and the list; the staff app shows "In recovery"). The patient is received by the signed-in nurse (`ot.checklists.record`), after the Sign-out only (422 `recovery.not_signed_out`), once (`recovery.already_received`); the receiving nurse, time in, airway, oxygen and the systolic before anaesthesia (by default the first systolic of the anaesthesia chart) are recorded. The anaesthetist hands over from their own sign-in (holds `ot.anaesthesia.record`; anyone else 403 `recovery.handover_anaesthetist`) or confirms at the nurse's screen (`POST /api/v1/auth/witness`, purpose `ot.recovery_handover`, a holder of `ot.anaesthesia.record`), once (`recovery.handover_recorded`). Observations at intervals (pulse, BP, SpO₂ and whether on oxygen, respiration, temperature, AVPU, breathing, limbs moved on command, pain 0–10, PONV), never before the arrival. The **modified Aldrete score** of the latest row (activity, respiration, circulation as the systolic against the pre-anaesthetic one, consciousness, SpO₂; each 0–2, total 0–10; Aldrete 1995) names what is missing. The patient leaves only once the surgery is completed in the theatre (`recovery.surgery_not_completed`) and, when the case had an anaesthetist, after the handover (`recovery.handover_missing`): to the ward at a score of 9 or more from observations at most 15 minutes old (else 422 `recovery.score_too_low`, saying why), or on an anaesthetist's override from their own sign-in (`…/discharge/override`, `ot.anaesthesia.record`, with the reason; not where no override is needed, `recovery.override_not_needed`), audited at Warning `ot.recovery_override`; to ICU or HDU at any score. The discharge is audited `ot.recovery_discharged` and closes the record (`recovery.discharged`). | `AldreteScore`, `RecoveryRecord`, `RecoveryService` | `AldreteScoreTests`, `RecoveryRecordTests`, `RecoveryRoomTests` |
| **Implants and consumables after the operation starts** (slice 10). Once the operation has started (knife to skin, or the Sign-out done) a line is not removed (422 `surgery.material_locked`): a wrong batch, serial, expiry or quantity is corrected (`POST …/materials/{id}/corrections`, 202, `ot.material_correction`), approved by the OT in-charge (`ot.materials.approve`), never the one who asked (`approval.self`). The original line is kept, superseded; its charge is withdrawn and its stock returned, and a line with the corrected values is issued from stock and charged in its place (none for a quantity of 0: not used), in the approval's transaction, only over the line as asked (`approval.stale`), audited `ot.material_corrected` with both names. A charge already on a final bill cannot be withdrawn, so the approval then fails and a credit note is asked for. Before the operation a line entered by mistake is removed at once, as before. | `OtClinicalService.RemoveMaterialAsync/RequestCorrectionAsync/ApplyCorrectionAsync`, `MaterialCorrectionApproval` | `MaterialFactsTests`, `ImplantCorrectionTests` |
| No double booking of a theatre, surgeon, anaesthetist or patient. Overlap uses half-open intervals, so back-to-back cases are allowed. The check runs under PostgreSQL advisory locks, across all facilities. | `SurgeryScheduling`, `SurgeryService`, `SchedulingLocks` | `SurgerySchedulingTests` |
| The OT store look-up offers only stock that can be issued now (not expired, blocked or fully reserved), from the same OT store materials are consumed from, with batches in FEFO order. | `OtStoreLookup` | `OtStoreLookupTests` |
| A dialysis machine must match the patient's sero status (Negative/HBsAg/HCV/HIV). This is checked when the session is scheduled and again when it starts. The machine must also be disinfected after its last session and not be running another one. | `SeroSegregation`, `DialysisMachine.EnsureReadyFor` | `SeroSegregationTests`, `DialysisSessionTests` |
| A dialyzer is used only for its own patient and only up to its maximum uses. HBsAg-positive patients are always single-use. | `Dialyzer.Use`, `DialysisPatientProfile.EffectiveMaxDialyzerUses` | `DialyzerTests` |
| Donor eligibility (NBTC 2017): Hb ≥ 12.5 g/dL; weight ≥ 45 kg (≥ 55 kg for 450 ml); age 18–65 (first-time donors ≤ 60); BP 100–140/60–90; pulse 60–100; temperature ≤ 37.5 °C; whole-blood interval 90 days (men) / 120 days (women); questionnaire deferrals. | `DonorEligibility` | `DonorEligibilityTests` |
| A unit leaves quarantine only when it is grouped **and** all five TTI markers are non-reactive **and** another blood bank officer has verified those results (below, *The second officer*). Any reactive marker discards the unit and defers the donor at once. | `BloodComponent.ApplyLabResults/Release`, `Donation.RecordLabResults/Verify` | `BloodComponentTests`, `SecondOfficerTests`, `BloodBankVerificationTests` |
| ABO/Rh(D) compatibility table: PRBC compatible, whole blood ABO-identical, plasma/platelets/cryo plasma-compatible, Bombay handled, unknown group never compatible. | `BloodCompatibility` | `BloodCompatibilityTests` |
| Issue gate: red cells need a compatible cross-match done within 72 h. An emergency release, authorised by a doctor from their own sign-in, allows only uncross-matched O Rh(D)-negative PRBC, or AB plasma when the patient is ungrouped. Expired units, units that have not cleared TTI or are awaiting verification, and units reserved for another request are never issued. | `IssueCompatibility`, `BloodComponent.Issue`, `BloodRequest.AuthoriseEmergencyRelease` | `IssueCompatibilityTests`, `BloodBankVerificationTests` |
| Grouping, cross-match and issue are not all one person: a unit is not issued by the officer who both grouped the patient and cross-matched it (plasma, platelets, cryo: who grouped; an uncross-matched unit: the doctor who released it), unless the facility has switched the second officer off. | `IssueSeparation` | `SecondOfficerTests`, `BloodBankVerificationTests` |
| Cold chain: a returned unit is restocked only if it comes back within 30 min with temperature maintained. Thawed FFP/cryo are never restocked. | `ColdChain` | `BloodComponentTests` |
| Shelf life from collection: WB 35 d, PRBC 35 d, FFP 365 d, platelets 5 d, cryo 365 d. Plasma and platelets must be separated within 8 h. | `ComponentShelfLife` | `ComponentShelfLifeTests` |
| Blood-bank grouping of the patient sample is used for compatibility, never the group from registration. A second grouping that disagrees is refused (possible wrong blood in tube). A grouping that disagrees with the groups confirmed on the patient's other requests holds the request until another officer groups a new sample. | `BloodRequest.RecordGrouping/Regroup`, `GroupingHistory` | `IssueCompatibilityTests`, `SecondOfficerTests`, `BloodBankVerificationTests` |
| A termination of pregnancy (MTP Act s.3) needs the woman's written consent, or her guardian's when she is under 18 or mentally ill; the opinion of one doctor up to 20 weeks, of two from 20 to 24 weeks for a woman in a rule 3B category; beyond 24 weeks only a substantial foetal abnormality, with the Medical Board's opinion. A contraceptive failure is a ground up to 20 weeks only. Once the termination is recorded the case cannot change. | `MtpCase.Certify/Terminate` | `MtpCaseTests`, `MtpTests` |
| From 20 weeks the second registered medical practitioner records their own opinion (their sign-in, or their own credentials at the first's screen), from their own practitioner record, never the certifying doctor nor the first opinion's registration; the termination waits for it and a correction asks for it again (maker-checker, slice 9). | `MtpCase.RecordSecondOpinion`, `MtpService.RecordSecondOpinionAsync` | `MtpCaseTests`, `MtpSecondOpinionTests` |

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
| POST | `/bookings` | `ot.bookings.manage` | `CreateSurgeryBookingRequest` (patientId, admissionId?, visitId?, procedureServiceItemId?, procedureName, laterality, surgeonId, assistantIds, anaesthetistId, nurseId? (the assisting nurse or OT in-charge: a nurse or OT technician from the practitioner master who is in use, else 400 `nurseId`), anaesthesiaType GA/Spinal/Epidural/Regional/Local/Sedation/Topical (Local and Topical are given by the surgeon: no anaesthetist needed and no anaesthetist fee), priority Elective/Urgent/Emergency, preferredDate, optional theatreId+scheduledStart+scheduledEnd) → 201 `SurgeryCreated` (id, bookingNumber, status). 409 `surgery.slot_conflict` with the conflicts. Without an `admissionId`, a patient in a bed gets the current admission whatever visit is named (the visit is kept; without one, the stay's), so the OT and implant charges go to the IP bill (`SpecialtyLookups.SurgeryEncounterAsync`) |
| PUT | `/bookings/{id}` | `ot.bookings.manage` | `UpdateSurgeryPlanRequest` (procedure, side, siteLevel?, team with the nurse / OT in-charge, anaesthesia, priority, version, changeReason?) → 204. A booked slot is re-checked for conflicts. After the WHO Sign-in a change of side, level or procedure needs the operating surgeon or anaesthetist and `changeReason`, and voids the Sign-in and Time-out (above); after the incision it is refused |
| POST | `/bookings/{id}/schedule` | `ot.bookings.manage` | `ScheduleSurgeryRequest` (theatreId, scheduledStart, scheduledEnd, version) → 204 / 409 |
| POST | `/bookings/{id}/postpone` · `/cancel` | `ot.bookings.manage` | `SurgeryReasonRequest` (reason) → 204. Postpone resets the WHO checklist |
| POST | `/bookings/{id}/receive` | `ot.checklists.record` | → 204 (Scheduled → InPreOp) |
| PUT | `/bookings/{id}/pre-op-checklist` | `ot.checklists.record` | `RecordChecklistRequest` {items[{key, state Pending/Done/NotApplicable, remarks}]} → 204 |
| POST | `/bookings/{id}/safety-checklist/{sign-in\|time-out\|sign-out}` | `ot.checklists.record` | `RecordChecklistRequest` → 204. Site marking and the counts as above |
| POST | `/bookings/{id}/counts` | `ot.checklists.record` | `RecordSurgicalCountRequest` (category Swabs/Needles/Instruments, stage Initial/Additional/Final, number, witnessConfirmationId, remarks?) → 204. The detail's `counts`, `countTallies` (initial, added, expected, final, tallies) and `countDiscrepancies` |
| POST | `/bookings/{id}/counts/discrepancies/{discrepancyId}/resolve` | `ot.checklists.record` | `ResolveCountDiscrepancyRequest` (resolution Found/XrayNoRetainedItem/RetainedItem, note, xrayResult? (required for an X-ray), harm? (required for a retained item)) → `CountDiscrepancyResolved` (incidentId, incidentNumber for a retained item) |
| POST | `/bookings/{id}/start` · `/complete` | `ot.surgery.perform` | → 204. Complete posts the OT charges |
| POST | `/bookings/{id}/charges/retry` | `ot.bookings.manage` | → `ChargeOutcome` (status, error) |
| GET/PUT | `/bookings/{id}/anaesthesia` | `ot.read` / `ot.anaesthesia.record` | `SaveAnaesthesiaRecordRequest` (asaGrade I–VI + E, technique, airway, drugs[], fluids[], blood loss, complications, recovery notes, sign, version) → `AnaesthesiaRecordDto`. Signed records are locked |
| POST | `/bookings/{id}/anaesthesia/vitals` | `ot.anaesthesia.record` | `AddAnaesthesiaVitalsRequest` {rows[at, HR, BP, SpO₂, EtCO₂, temp, RR]} → `AnaesthesiaRecordDto` |
| GET | `/bookings/{id}/recovery` | `ot.read` | → `RecoveryRecordDto` (time in, receiving nurse, airway, oxygen, pre-anaesthetic systolic, handover, observations, `score` {components[sign, points, missing], total, at, missing, meetsWardDischarge, wardBlockedBy}, time out, destination, score at discharge, override); 404 before the patient is received. PHI access audited |
| POST | `/bookings/{id}/recovery/receive` | `ot.checklists.record` | `ReceiveInRecoveryRequest` (airway, oxygen, oxygenLitresPerMinute?, preAnaesthesiaSystolic?, receivedAt?, notes?) → `RecoveryRecordDto` |
| PUT | `/bookings/{id}/recovery` | `ot.checklists.record` | `SaveRecoveryRecordRequest` (airway, oxygen, litres, pre-anaesthetic systolic, notes, version) → `RecoveryRecordDto` |
| POST | `/bookings/{id}/recovery/handover` | `ot.checklists.record` | `RecoveryHandoverRequest` (witnessConfirmationId? (purpose `ot.recovery_handover`), notes?) → `RecoveryRecordDto`. Without a confirmation the signed-in user must hold `ot.anaesthesia.record` |
| POST | `/bookings/{id}/recovery/observations` | `ot.checklists.record` | `AddRecoveryObservationRequest` (heartRate, systolic, diastolic, spO2, onOxygen, respiratoryRate, temperatureC, consciousness Alert/Voice/Pain/Unresponsive, breathing DeepBreathAndCough/Limited/Apnoeic, limbsMoving 0–4, painScore 0–10, ponv None/Nausea/Vomiting, at?) → `RecoveryRecordDto` |
| POST | `/bookings/{id}/recovery/discharge` | `ot.checklists.record` | `DischargeFromRecoveryRequest` (destination Ward/Icu/Hdu) → `RecoveryRecordDto`. The ward needs the score (above) |
| POST | `/bookings/{id}/recovery/discharge/override` | `ot.anaesthesia.record` | `OverrideRecoveryDischargeRequest` (reason) → `RecoveryRecordDto`: to the ward below the score |
| GET/PUT | `/bookings/{id}/operation-note` | `ot.read` / `ot.surgery.perform` | `SaveOperationNoteRequest` (diagnoses, procedure performed, findings, details, specimens[], drains, blood loss, complications, **postOpOrders**, sign) → `OperationNoteDto` |
| GET | `/bookings/{id}/materials` | `ot.read` | → `SurgeryMaterialDto[]` |
| GET | `/store-items?search=&take=` | `ot.materials.record` | → `OtStoreItem[]` for the materials dialog: active items with issuable stock (unreserved, unexpired, unblocked) in the facility's OT store — the store `POST /bookings/{id}/materials` issues from, resolved with `StockService.ResolveStoreAsync(facility, OT)`. Fields: itemId, code, name, genericName, manufacturer, type, baseUnit, available, nearestExpiry, mrp (of the batch FEFO issues first) and `batches[]` (batchId, batchNumber, expiryDate, available, mrp; the first 3 in FEFO order). Search matches code, barcode, name, generic or brand; name order; `take` 20 by default, at most 50. 422 `stock.no_store` when the facility has no active OT store, `stock.store_ambiguous` when it has more than one, `facility.required` without a signed-in facility. Record the pick with `AddSurgeryMaterialRequest` (`supplyItemId = itemId`, `itemName = name`, `lotNumber = batchNumber`, `expiryDate`) |
| POST | `/bookings/{id}/materials` | `ot.materials.record` | `AddSurgeryMaterialRequest` (kind Implant/Consumable, supplyItemId?, itemName, manufacturer, lot/serial (required for an implant not from the OT store), expiry, quantity, unitPrice, brand?, specification?, mrp?, ceilingPrice?) → 201 `SurgeryMaterialDto` (with brand, specification, mrp and ceilingPrice). With `supplyItemId` the item is issued from the facility's OT store (stock ledger source `specialtycare.ot`), in the same transaction as the charge: its brand, manufacturer, specification and NPPA ceiling price come from the item master (what the dialog typed is not used), its lot is the batch the store issued when none was written (FEFO), and its MRP the lowest of the batches issued. A line names one batch, so that a recall by lot finds every patient: packs that FEFO would take from several batches are refused unless each is recorded on a line of its own with its lot (422 `surgery.material_batches`). Without it (consignment or patient-supplied) only the charge is posted, and an implant needs its `brand`, `manufacturer` and `mrp` from the pack (400 `brand` / `manufacturer` / `mrp`); a price-controlled one (a coronary stent, a knee implant) also takes its `ceilingPrice` with GST from the hospital's NPPA price list, which only an implant may have (400 `ceilingPrice`). A unit priced above the lower of the MRP and the ceiling is refused (400 `unitPrice`, naming the limit). The charge carries these as its `device` (NPPA), printed beneath the line on the bill. Stock errors (no OT store, not enough stock) are returned as 422 |
| DELETE | `/bookings/{id}/materials/{materialId}` | `ot.materials.record` | → 204 (withdraws the charge first, then returns the item to the OT store). Before the operation starts only: after, 422 `surgery.material_locked` |
| POST | `/bookings/{id}/materials/{materialId}/corrections` | `ot.materials.record` | `CorrectSurgeryMaterialRequest` (lotNumber?, serialNumber?, expiryDate?, quantity (0 = not used), reason) → 202 `ApprovalRequestDto` (`ot.material_correction`, decided by `ot.materials.approve`). 422 `surgery.material_not_locked` before the operation, `surgery.material_superseded` for a line already corrected. `SurgeryMaterialDto` shows `pendingCorrection`, `supersededAt`, `supersededByMaterialId`, `correctsMaterialId`, the reason and both names |
| GET | `/api/v1/patients/{patientId}/surgeries` | `ot.read` | → paged surgery history |

**The OT register** is the Analytics report `ot-register` ([analytics.md](analytics.md)): the paper register's columns,
written from the booking, the operation note, the anaesthesia record, the practitioner master and the stay's beds, on
screen and in Excel, CSV and PDF. It needs `analytics.clinical.read` and is audited per patient.

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
| GET | `/donations?status=&pendingResults=&awaitingVerification=&oneOfficer=&search=` · `/donations/{id}` | `bloodbank.inventory.manage` | → paged `DonationListItem` (awaitingVerification, verifiedByEnterer) / `DonationDetail` (TTI panel, who entered and who verified, awaitingVerification, separateVerifier, components). `oneOfficer`: released on one officer's word, for the medical officer's review |
| POST | `/donations/{id}/components` | `bloodbank.inventory.manage` | `SeparateComponentsRequest` {components[{type WholeBlood/PRBC/FFP/PlateletConcentrate/Cryoprecipitate, volumeMl, storageLocation}]} → `BloodUnitDto[]` (in quarantine) |
| PUT | `/donations/{id}/lab-results` | `bloodbank.inventory.manage` | `RecordLabResultsRequest` (bloodGroup, hiv, hbsAg, hcv, syphilis, malaria: Pending/NonReactive/Reactive, version) → 204; a reactive result discards the units, defers the donor and alerts officers; results that clear the donation leave the units in quarantine awaiting verification. Complete results are corrected only by whoever entered them (403 `bloodbank.results_entered_by_other`), never once verified (`bloodbank.results_final`) |
| POST | `/donations/{id}/lab-results/verify` | `bloodbank.inventory.manage` | `VerifyLabResultsRequest` (version) → 204; another officer verifies the results as shown and the units go to stock (403 `bloodbank.verify_own_results` for the officer who entered them while `bloodbank.separateVerifier` is on); audited `bloodbank.results_verified` with both names |
| GET | `/inventory` | `bloodbank.read` | → `BloodInventoryDashboard` (stock cells by component × group: available/reserved/quarantine/expiringSoon, units expiring within 3 days, open and emergency requests) |
| GET | `/units?status=&component=&bloodGroup=&search=` · `/units/by-number/{unitNumber}` | `bloodbank.read` | → paged `BloodUnitDto` / barcode look-up |
| POST | `/units/{id}/discard` | `bloodbank.inventory.manage` | `DiscardUnitRequest` → 204 |
| GET | `/requests?status=&openOnly=true&patientId=&oneOfficer=&search=` · `/requests/{id}` | `bloodbank.read` | → paged `BloodRequestListItem` (emergency first) / `BloodRequestDetail` (who grouped and who cross-matched by account, a grouping held against the patient's earlier requests, the doctor's release, separateVerifier, cross-matches, issues). `oneOfficer`: requests with a unit issued on one officer's checks, open or closed |
| POST | `/requests` | `bloodbank.requests.create` | `CreateBloodRequestRequest` (patientId, component, units, urgency Routine/Urgent/Emergency, indication, requiredBy, pre-transfusion Hb/platelets) → 201 `{id, requestNumber}`. Emergency requests alert issuers |
| POST | `/requests/{id}/cancel` | `bloodbank.requests.create` | `BloodReasonRequest` → 204 (frees reserved units) |
| POST | `/requests/{id}/grouping` | `bloodbank.crossmatch.perform` | `GroupPatientRequest` (bloodGroup, newSample) → 204. 422 `bloodbank.grouping_discrepancy` against this request's first sample; a grouping against the patient's earlier requests holds the request. `newSample`: another officer's grouping of a new sample for a held request (403 `bloodbank.regroup_own_sample`) |
| POST | `/requests/{id}/emergency-release` | `bloodbank.requests.create`, and a doctor's practitioner record | `EmergencyReleaseRequest` (reason) → 204 (emergency requests only; 403 `bloodbank.doctor_required`; the doctor's account, practitioner record and time recorded; audited and the blood bank told) |
| GET | `/requests/{id}/compatible-units` | `bloodbank.crossmatch.perform` | → `CompatibleUnitDto[]` (compatible, in date, first-expiry-first-out) |
| POST | `/requests/{id}/crossmatches` | `bloodbank.crossmatch.perform` | `RecordCrossMatchRequest` (componentId, method, result) → `CrossMatchDto`; a compatible result reserves the unit |
| POST | `/requests/{id}/issues` | `bloodbank.units.issue` | `IssueBloodRequest` (componentId, issuedToName) → `BloodIssueDto` (issuedOnOwnChecks). 403 `bloodbank.issue_own_checks` when the issue rests on the issuer's checks alone; 422 `bloodbank.grouping_history_discrepancy` while the grouping is held |
| POST | `/issues/{id}/return` | `bloodbank.units.issue` | `ReturnBloodRequest` (temperatureMaintained, notes) → `BloodIssueDto` (restocked or discarded). Refused once the unit was started on the ward (`bloodbank.transfusion_started`) |
| GET | `/patients/{patientId}/units` | `bloodbank.transfuse` or `bloodbank.read` | → `PatientBloodUnits`: the patient's units issued, newest first, each with when it must be started by, until when its cross-match stands, the consent a start would rely on and its transfusion (audited as a read of the patient's record) |
| POST | `/issues/{id}/transfusion` | `bloodbank.transfuse` | `StartTransfusionRequest` (wristbandUhid, bagUnitNumber, witnessConfirmationId, baseline observations, consentDeferredReason) → 201 `TransfusionDto`. The bedside check, below |
| POST | `/transfusions/{id}/observations` | `bloodbank.transfuse` | `RecordTransfusionObservationRequest` (kind FifteenMinutes/Later, recordedAt, values, version) → `TransfusionDto` |
| POST | `/transfusions/{id}/end` | `bloodbank.transfuse` | `EndTransfusionRequest` (endedAt, stopped, stopReason, volumeMl, observations, version) → `TransfusionDto` |
| POST | `/issues/{id}/reactions` | `bloodbank.reactions.report` | `ReportReactionRequest` (onset, symptoms[], vitals, transfusionStopped, volume, actions, severity) → 201 `{id}`; alerts investigators |
| GET | `/reactions?status=` | `bloodbank.read` | → paged `TransfusionReactionDto` |
| POST | `/reactions/{id}/investigate` · `/close` | `bloodbank.reactions.investigate` | `CloseReactionRequest` (type (HvPI classification), severity, findings, reportedToHaemovigilance) → 204 |

**The bedside check.** A unit is started on the ward only after two staff check it at the bedside, against the
patient and the issue (BSH guideline on the administration of blood components, 2017). On the nursing chart's *Blood*
tab, the nurse who starts it:

* reads the patient's wristband and the bag, by scanning them or typing what they say. The server compares them with
  the issue. Another patient's wristband (`bloodbank.wrong_patient`) or another bag (`bloodbank.wrong_unit`) starts
  nothing, and each is audited as a near miss (`bloodbank.bedside_mismatch`, a warning);
* takes the observations before the start: temperature, pulse, blood pressure, respiratory rate, and SpO₂ when measured;
* has the second member of staff confirm at the same screen with their own user name and password, as a witness on
  the MAR does (ADR-014; purpose `blood.bedside_check`). The witness holds `bloodbank.transfuse` and is never the one
  starting it. The confirmation is spent in the start's own save.

The server refuses the start when:

* the patient's stay has ended (`bloodbank.stay_ended`): a unit issued but never started does not hold the discharge,
  and goes back to the blood bank, so a bedside dialog left open from before cannot start it after;
* the unit has expired (`bloodbank.unit_expired`);
* its cross-match is more than 72 hours old, unless it was released in an emergency (`bloodbank.crossmatch_expired`);
* it left the blood bank more than 4 hours ago, so it cannot be given in time and goes back to be discarded
  (`bloodbank.out_of_storage_too_long`);
* the patient's consent to transfusion is not recorded for this stay (`bloodbank.consent_required`): given since the
  admission, or, for blood asked for outside an admission, in the day before the request. A unit released in an
  emergency may start without it when the nurse says why it cannot be taken now; the start then records the reason and
  is audited as a warning (`bloodbank.consent_deferred`).

While it runs, observations are added (at 15 minutes, and later), the unit cannot be returned to stock, and the stay
cannot be discharged (`discharge.transfusion_running`). Each holds when both happen at the same moment: the start is
saved under the stay's lock, which the discharge takes before it asks for the running units, so one of the two sees the
other; and the start writes the issue, as a return does, so of a start and a return saved together the second is
refused on the issue's version (409). It ends completed or stopped, with the volume given and the
observations at the end; a stop says why (`bloodbank.transfusion_stopped`, a warning), and a reaction is reported as
before. With the issue, the transfusion says what became of each unit: returned, or transfused.

**The second officer** (maker-checker; Drugs and Cosmetics Rules, Schedule F Part XII-B; NBTC standards). No unit
leaves the blood bank on one officer's word:

* a donation's results that clear it (grouped, all five markers non-reactive) leave its units in quarantine, *awaiting
  verification*. Another officer who holds `bloodbank.inventory.manage` checks them against the worksheet and the
  analyser's printout and verifies them, and the units go to stock; a component separated later goes to stock too. The
  donation names who entered and who verified. Markers still pending are completed by any officer; complete results are
  corrected only by whoever entered them, and verified ones by nobody. A reactive result discards at once;
* grouping and cross-match record the officer's account. A unit is not issued by the officer who both grouped the
  patient and cross-matched it; plasma, platelets and cryoprecipitate, which go on the group alone, not by the officer
  who grouped; an uncross-matched unit not by the doctor who released it (`bloodbank.issue_own_checks`). The issue
  screen says so once the unit is scanned, before it is handed over;
* the patient's grouping is compared twice. With this request's own first sample, as before: a second grouping that
  disagrees is refused (`bloodbank.grouping_discrepancy`), as possible wrong blood in the tube. With the groups confirmed
  on the patient's other requests, in any facility: one that disagrees is recorded, but holds the request (audited at
  Warning, and the officers who cross-match are told). Nothing is cross-matched or issued on it
  (`bloodbank.grouping_history_discrepancy`) until another officer groups a new sample. The new sample must agree with
  the first sample or with the earlier group (a third group is refused), and its grouping replaces the first; if it
  agrees with the first sample, the earlier grouping is to be investigated. While the request is held, a doctor's
  emergency release still lets O Rh(D)-negative red cells and AB plasma go, as for an ungrouped patient;
* uncross-matched release is authorised by the treating doctor on the request, from their own sign-in: one who may ask
  for blood and whose account is linked to a doctor (consultant, resident or visiting) in the practitioner list
  (`bloodbank.doctor_required` otherwise). The blood bank no longer types a name. Their account, practitioner record and
  the time are kept, the blood bank is told, and each unit issued on it is audited naming the doctor and the officer.

A blood bank with one officer on duty (a night shift) sets `bloodbank.separateVerifier` to `false` for its facility,
which waits for a second administrator (SettingGuard). That officer then verifies their own results and issues on their
own grouping and cross-match; each is audited at Warning and flagged (`verifiedByEnterer`, `issuedOnOwnChecks`), and the
donations and requests screens list them for the medical officer's review (`oneOfficer=true`). The setting does not
lift the hold on a grouping that disagrees with earlier requests.

**Unit labels (BB-03).** From a donation's components and from a unit, the staff app prints a label per unit,
without a dialog, on the workstation's label printer: blood group with POS or NEG in full, component and volume, the
unit number as Code 128 for scanning at issue and at the bedside, expiry and storage. A unit not yet cleared by
screening is labelled QUARANTINE / NOT FOR ISSUE in white on black. On a ZPL or TSPL printer the server writes the
label (`blood-units`); otherwise the label page prints. See [integrations.md](integrations.md#barcode-labels-int-bar).

### Termination of pregnancy — `/api/v1/mtp` (tag *Termination of pregnancy*)
Under s.5A of the MTP Act no one but a person authorised by law may learn of a termination, so every case opens only to
the MTP permissions below; opening a case puts the patient's record in the MTP sensitive category (R-4), which closes the
rest of her chart to staff without `patients.sensitive.mtp`. A case is opened only once the facility's
`mtp.approvalNumber` (the place's certificate of approval, Form B) is set (422 `mtp.not_approved`). Every opening of a
case, its print and the register export are audited.

| Method | Path | Permission | Request → Response |
|---|---|---|---|
| GET | `/cases?patientId=&status=` · `/cases/{id}` | `mtp.read` or `mtp.certify` | → `MtpCaseListItem[]` / `MtpCaseDto`; guard; audit "Viewed MTP case …" |
| POST | `/cases` | `mtp.certify` | `SaveMtpCaseRequest` (patient, age, mentally ill, consent by Woman/Guardian with name and relation, consent date and scan, weeks, grounds, rule 3B category, Medical Board reference, opinions[{doctorName, registrationNumber, opinionOn}]) → `MtpCaseDto` (Certified, with the Form III serial number; `awaitingSecondOpinion` from 20 weeks); flags the record MTP. From 20 weeks only the first doctor's opinion is typed (422 `mtp.second_opinion_typed`) |
| PUT | `/cases/{id}` | `mtp.certify` | `SaveMtpCaseRequest` with `version` → `MtpCaseDto`; 422 `mtp.terminated` / `mtp.cancelled` once closed |
| POST | `/cases/{id}/second-opinion` | `mtp.certify` | `RecordSecondOpinionRequest` (opinionOn, doctorConfirmationId?, version) → `MtpCaseDto`: the second doctor's opinion from 20 weeks, given signed in as themselves or, with a confirmation from `POST /api/v1/auth/witness` (purpose `mtp.second_opinion`), at the first doctor's screen; name and registration from their practitioner record. 422 `mtp.second_opinion_not_rmp`, `mtp.second_opinion_same_doctor`, `mtp.second_opinion_not_needed`, `mtp.second_opinion_recorded`, `witness.confirmation_unusable`; audited `mtp.second_opinion_recorded` |
| POST | `/cases/{id}/termination` | `mtp.certify` | `RecordMtpTerminationRequest` (date, method, performed by, discharge date, result, remarks, version) → `MtpCaseDto` (Terminated); 422 `mtp.second_opinion_required` while a case from 20 weeks waits for the second doctor |
| POST | `/cases/{id}/cancel` | `mtp.certify` | `CancelMtpCaseRequest` (reason, version) → `MtpCaseDto` (Cancelled; stays out of the register) |
| GET | `/cases/{id}/document` · `/pdf` | as GET | → Form C, Form I and the Form III entry, marked confidential / its PDF; audited |
| GET | `/register?year=&month=` | `mtp.register` | → `MtpRegister` (Form III: terminations done in the month and cases certified in it not yet done) |
| GET | `/register/export?year=&month=` | `mtp.register` | → CSV of the register; audited `mtp.register.exported` |
| GET | `/monthly-report?year=&month=` | `mtp.register` | → `MtpMonthlyReport` (Form II: terminations by gestation, religion, ground, method and age) |

## Permissions and default roles
| Permission | Default roles |
|---|---|
| `ot.read` | OtCoordinator, Doctor, Nurse, HospitalAdmin |
| `ot.theatres.manage` | OtCoordinator, HospitalAdmin |
| `ot.bookings.manage` | OtCoordinator, Doctor |
| `ot.checklists.record` (also the recovery room record) | Nurse, Doctor, OtCoordinator |
| `ot.surgery.perform`, `ot.anaesthesia.record` (also the anaesthetist's recovery handover and the override of the recovery score) | Doctor |
| `ot.materials.record` | Nurse, OtCoordinator, Doctor |
| `ot.materials.approve` (the OT in-charge: approves an implant or consumable line corrected after the operation started) | OtCoordinator, HospitalAdmin |
| `ot.reports.read` | OtCoordinator, HospitalAdmin, MisAnalyst |
| `dialysis.read` | DialysisTechnician, Nurse, Doctor, HospitalAdmin |
| `dialysis.machines.manage` | DialysisTechnician, HospitalAdmin |
| `dialysis.patients.manage`, `dialysis.sessions.manage` | Doctor, DialysisTechnician, Nurse |
| `physio.read` | Physiotherapist, Doctor, Nurse, HospitalAdmin |
| `physio.referrals.manage` | Doctor, Physiotherapist |
| `physio.treatment.manage` | Physiotherapist |
| `bloodbank.read` | BloodBankOfficer, Doctor, Nurse, HospitalAdmin |
| `bloodbank.donors.manage`, `bloodbank.inventory.manage` (also verifies another officer's screening results), `bloodbank.crossmatch.perform`, `bloodbank.units.issue`, `bloodbank.reactions.investigate` | BloodBankOfficer |
| `bloodbank.requests.create` (a doctor among them, linked to a doctor's practitioner record, authorises uncross-matched release), `bloodbank.reactions.report` | Doctor, Nurse, BloodBankOfficer |
| `bloodbank.transfuse` (check a unit at the bedside, start and end it, witness another's check) | Nurse, Doctor |
| `mtp.read`, `mtp.register` | MrdOfficer |
| `mtp.certify` | none: grant it, with `patients.sensitive.mtp`, to the gynaecologists the place's approval names |

## Events
**Raised:** `SurgeryCompleted` (Platform) by `SurgeryBooking.Complete`, when the patient leaves the theatre after the WHO
Sign-out: booking, patient, admission/visit, procedure code and name, side, surgeon, knife-to-skin and completion times.
Clinical records it as a procedure on the patient's record (EHR-01). Otherwise the module consumes clinical orders and ADT.

**Handled** (in the publisher's transaction):
| Event | Handler | Effect |
|---|---|---|
| `ServiceOrderPlaced` (Category **Physiotherapy**) | `PhysioOrderHandler` | Creates a `PhysioReferral` (idempotent on `OrderItemId`); order `Quantity` = sessions prescribed (`sessionsOrdered`), the default for the treatment plan |
| `ServiceOrderPlaced` (Category **BloodBank**, codes BB-WB/PRBC/FFP/PLT/CRYO) | `BloodOrderHandler` | Creates a `BloodRequest` for order `Quantity` units (at least 1; Stat → Emergency) |
| `ServiceOrderPlaced` (Category **Surgery**) | `SurgeryOrderHandler` | Creates a *Requested* `SurgeryBooking` with the ordering doctor as surgeon; ordered for a patient in a bed, from whatever visit, it is the stay's (as `POST /bookings`) |
| `ServiceOrderCancelled` | the three handlers above | Cancels the work item if no treatment, issue or surgery has happened yet, and frees reserved blood |
| `PatientAdmitted` / `PatientDischarged` / `AdmissionCancelled` | `AdmissionEventHandlers` | Maintains `specialtycare.admissions`, so charges go to the running IP bill. On admission, the patient's surgeries at the admission's facility that wait with no admission (Requested, Scheduled, Postponed) join the stay, keeping their visit. On discharge or cancellation: cancels the waiting surgeries booked during the stay, gives back those booked before it (created before the admission time: no admission again, slot kept), cancels unissued blood requests (frees reserved units), and detaches future dialysis sessions from the closed admission |
| `PatientsMerged` (Patients) | `PatientMergeHandler` | Re-points every patient-linked row to the surviving UHID (all facilities). The rows move in set-based statements, so one audit entry in the merge's transaction, `specialtycare.records_moved` (on the duplicate's record, under the surviving patient), lists the ids moved from each table, the duplicate's dialysis profiles deactivated and its sessions cancelled for clashing with the survivor's: what an unmerge would put back |

**Discharge hold** (`TheatreDischargeHolds`, the Platform contract `IDischargeHoldSource` Inpatient asks before it
completes a discharge): while a surgery of the stay is received in pre-op or in progress, the discharge, of whatever
type, is refused with 422 `discharge.in_theatre`, naming the case ("Surgery {no.} ({procedure}) is in progress…", "The
patient is in pre-op for surgery {no.}…: complete, postpone or cancel it before the discharge"). A case still waiting for
its day holds nothing; the discharge deals with it as above. Receiving the patient in pre-op and starting the operation
take the stay's lock (`EncounterLocks`) and read the case again under it, in the transaction that saves them, as the
discharge asks the theatre under it: a discharge saved at the same moment either waits for the theatre and is refused,
or commits first, and the theatre then finds the case cancelled or given back.

From the WHO Sign-out until the patient is discharged from recovery the stay is held too, with 422 `discharge.in_recovery`
("The patient is in recovery after surgery {no.} ({procedure}): discharge them from recovery before the discharge"), and
the board of patients in the hospital shows the stay's place as "In recovery · {bed}" ("In theatre", "In pre-op" before
it); the ward has the patient back, the bed alone, only once the recovery room discharges them. The patient's surgery
journey runs on to *In recovery* and *Discharged from recovery* (to the ward, the ICU or the HDU), with the OT billing
alongside. Operations completed before the recovery record existed count as discharged from recovery when they left the
theatre (the migration `RecoveryRoom` sets it).

A unit of blood being transfused holds the stay the same way (`TransfusionDischargeHolds`): the discharge is refused
with 422 `discharge.transfusion_running` ("Unit {no.} is being transfused: end it on the ward, completed or stopped,
before the discharge"). A unit issued but not started holds nothing; it goes back to the blood bank.

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
| `mtp-register` | `{FAC}MTP{YY}{SEQ:4}` (the Form III serial number) | yearly, per facility |

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
`theatres`, `surgery_bookings` (pre-op and WHO checklists, counts, count discrepancies and checklists voided as `jsonb`; `site_level`; `recovery_discharged_at`, `recovery_destination`), `anaesthesia_records` (vitals, drugs and fluids as `jsonb`), `recovery_records` (one per booking; observations as `jsonb`), `operation_notes`,
`surgery_materials` (a corrected line kept with `superseded_at`, `superseded_by_material_id`, `corrects_material_id`, the reason and both names), `dialysis_machines`, `dialysis_profiles`, `dialyzers`, `dialysis_sessions` (filtered unique indexes: one active session per
machine per shift and per patient per shift), `physio_referrals`, `physio_assessments`, `physio_treatment_plans`, `physio_sessions`, `donors`,
`donor_screenings`, `donations`, `blood_components`, `blood_requests`, `crossmatches`, `blood_issues`, `transfusions` (one per issue), `transfusion_observations`, `transfusion_reactions`, `admissions`,
`mtp_cases` (opinions as `jsonb`).
