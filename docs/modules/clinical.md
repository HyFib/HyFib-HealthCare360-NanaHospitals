# Clinical module (`clinical`)

EHR / EMR physician desktop, e-prescribing, clinical orders, discharge summaries, medical records with ICD coding (MRD)
and master health checkups (MHC). Project: `src/server/Modules/HyFib.HealthCare360.Modules.Clinical` (layer 4,
references Patients and SupplyChain — the latter read-only, for the prescribers' drug search). Database schema: `clinical`.
Tests: `tests/Modules/HyFib.HealthCare360.Modules.Clinical.Tests`.

| Requirement | Where |
|---|---|
| EHR-01 longitudinal record | Encounters, vitals, allergies, diagnoses/problems, notes, immunisations, orders, prescriptions, discharge summaries; `GET /emr/patients/{id}/timeline` |
| EHR-02 timeline by type/department/date | `GET /emr/patients/{id}/timeline?types=&from=&to=&departmentId=` |
| EHR-03 banner flags | `GET /emr/patients/{id}/summary` (allergies with `allergyStatus`, alerts, problems, current medicines, last vitals + flags) for the chart; `GET /emr/patients/{id}/flags` (allergies and alerts alone, `clinical.flags.read`) for every other clinical screen's banner (see *Banner flags on every clinical screen*); `GET /emr/patients/{id}/allergies/status`, "no known allergies"; real-time `clinical.flags.changed` on `patient:{id}` |
| EHR-04/06/07 documents, consent, break-the-glass | Patients module; every clinical read goes through `PatientAccessGuard` + `IAuditLogger.PatientRecordAccessedAsync` (`ClinicalAccess`) |
| EMR-01 worklist | `GET /emr/worklist` (queue, open encounters, unsigned notes, draft prescriptions, results to review, chart deficiencies) |
| EMR-02..07 | Encounters (SOAP, follow-up, referral, sign, amend), templates, vitals, diagnoses (ICD), orders, prescriptions, favourites |
| ERX-01..06 | Prescriptions, favourites/templates, safety checks, signing → `PrescriptionSigned`, the printed A5 prescription and its PDF with QR verification, refills/repeat, Schedule H1/X confirmation + register |
| MRD-01..06 | ICD-10 / ICD-10-PCS catalogues + ranked search + WHO ICD-API lookup, coding at discharge, chart deficiencies, record requests, retention/archival, morbidity index |
| MHC-01..05 | Packages (billable service items), bookings, itinerary, doctor summary, consolidated report, corporate batches |

All paths below are under `/api/v1`. Lists accept `page`, `pageSize`, `search` where noted. Updates carry `version`
(PostgreSQL `xmin`); a stale version returns 409. Dates in query strings are IST calendar dates.

## Permissions

| Permission | Purpose | Default roles |
|---|---|---|
| `clinical.emr.read` | Read the clinical record | Doctor, Nurse, MrdOfficer |
| `clinical.flags.read` | See allergies and alerts on the patient banner, without the chart | Doctor, Nurse, MrdOfficer, Pharmacist, LabTechnician, Pathologist, Radiographer, Radiologist, CardiologyTechnician, Cardiologist, OtCoordinator, DialysisTechnician, Physiotherapist, BloodBankOfficer, Dietitian |
| `clinical.emr.write` | Encounters, diagnoses, notes, referrals, checkup summaries, result acknowledgement | Doctor |
| `clinical.encounters.sign` | Sign/amend encounters, sign notes | Doctor |
| `clinical.vitals.record` | Record vitals | Doctor, Nurse |
| `clinical.allergies.manage` | Record/update allergies | Doctor, Nurse |
| `clinical.alerts.manage` | DNR, fall-risk, isolation, infection-control, pregnancy alerts | Doctor, Nurse |
| `clinical.immunizations.record` | Record immunisations | Doctor, Nurse |
| `clinical.orders.place` | Place/cancel orders | Doctor |
| `clinical.templates.manage` | Templates, order sets, interaction and dose tables | Doctor, HospitalAdmin |
| `clinical.prescriptions.write` | Draft/check/repeat prescriptions | Doctor |
| `clinical.prescriptions.sign` | Sign/cancel prescriptions | Doctor |
| `clinical.prescriptions.refill` | Issue an authorised refill | Doctor, Pharmacist |
| `clinical.discharge_summary.write` | Draft/sign/amend discharge summaries | Doctor |
| `mrd.coding` | Clinical coding and verification | MrdOfficer |
| `mrd.records` | Charts, deficiencies, record requests, archival, morbidity report | MrdOfficer, HospitalAdmin |
| `healthcheck.manage` | Packages, bookings, corporate batches | HospitalAdmin, FrontOffice |

## Endpoints

### EMR — physician desktop (`/emr`, tag EMR)

| Method | Path | Permission | Request → Response |
|---|---|---|---|
| GET | `/emr/worklist?date&practitionerId&departmentId` | emr.read | → `EmrWorklist` (defaults: today and the signed-in doctor; department optional) |
| POST | `/emr/results/{id}/acknowledge` | emr.write | `AcknowledgeResultRequest{note}` → 204 |
| GET | `/emr/patients/{patientId}/summary` | emr.read | → `PatientClinicalSummary` (patient, `allergiesEverRecorded`, `allergyStatus` (`Recorded`\|`NoKnownAllergies`\|`NotRecorded`), allergies, alerts, activeProblems, currentMedications, lastVitals+flags, recentEncounters(5), resultsAwaitingReview) |
| GET | `/emr/patients/{patientId}/flags` | flags.read or emr.read | → `PatientFlags{allergyStatus, allergies[AllergySummary], alerts[AlertSummary]}`: the summary's flags alone (same rule, same order: most severe first, alerts in force today). Restricted-record guard (403 `patient.restricted`) and a PHI-access audit entry ("Viewed allergies and alerts") like the chart; 404 for an unknown patient |
| GET | `/emr/patients/{patientId}/timeline?types=Encounter,Vitals,…&from&to&departmentId&page` | emr.read | → `PagedResult<TimelineEntry>` (types: Encounter, Vitals, Diagnosis, Prescription, Order, Note, Immunization, Document, DischargeSummary, Result) |
| GET | `/emr/patients/{patientId}/medications` | emr.read | → `CurrentMedication[]` |
| GET | `/emr/patients/{patientId}/encounters?type&page` | emr.read | → `PagedResult<EncounterListItem>` |
| POST | `/emr/encounters` | emr.write | `StartEncounterRequest{patientId, visitId?, admissionId?, type?, departmentId?, practitionerId?, templateId?, chiefComplaint?}` → 201 `EncounterDetail` (409 `encounter.already_open` with `encounterId`) |
| GET | `/emr/encounters/{id}` | emr.read | → `EncounterDetail` (SOAP, follow-up, referral, amendments) |
| PUT | `/emr/encounters/{id}` | emr.write | `UpdateEncounterRequest{chiefComplaint, historyOfPresentIllness, examination, assessment, plan, followUpDate, followUpAdvice, referral*, version}` |
| POST | `/emr/encounters/{id}/sign` | encounters.sign | `SignEncounterRequest{version}` → `EncounterDetail` |
| POST | `/emr/encounters/{id}/amend` | encounters.sign | `AmendEncounterRequest{reason, …content, version}` |
| GET / POST | `/emr/patients/{patientId}/vitals` | emr.read / vitals.record | `RecordVitalsRequest{encounterId?, recordedAt?, temperature, temperatureUnit, pulse, respiratoryRate, systolicBp, diastolicBp, spO2, onSupplementalOxygen, heightCm, weightKg, painScore, bloodGlucose, glucoseTiming, gcs, notes}` → `VitalsDto` with `flags` |
| POST | `/emr/patients/{patientId}/vitals/preview` | vitals.record | same request → `VitalFlag[]` (nothing saved) |
| POST | `/emr/vitals/{id}/entered-in-error` | vitals.record | `EnteredInErrorRequest{reason}` |
| GET / POST | `/emr/patients/{patientId}/allergies` | emr.read / allergies.manage | `RecordAllergyRequest{allergen, type, reaction, severity, onsetDate, notes}` → `AllergyDto` (409 `allergy.duplicate`). Recording an allergy removes a "no known allergies" statement in the same save |
| PUT | `/emr/allergies/{id}` | allergies.manage | `UpdateAllergyRequest{reaction, severity, status, statusReason, notes, version}`. Re-activating an allergy also removes a "no known allergies" statement |
| GET | `/emr/patients/{patientId}/allergies/status` | emr.read | → `AllergyStatusDto{status: Recorded\|NoKnownAllergies\|NotRecorded, recordedAt?, recordedByName?}` (see *No known allergies*); restricted-record guard and PHI audit like the allergy list |
| POST | `/emr/patients/{patientId}/allergies/none` | allergies.manage | no body → 200 `AllergyStatusDto` (`NoKnownAllergies`). 409 `allergy.active_exist` (`data.activeAllergies`) while an active allergy exists; idempotent (repeats return the existing statement); 422 `patient.merged` on a merged-away UHID; publishes `clinical.flags.changed` |
| GET / POST | `/emr/patients/{patientId}/alerts` | emr.read / alerts.manage | `RaiseAlertRequest{type, title?, detail?, severity?, expiresOn?}` → `PatientAlertDto` |
| POST | `/emr/alerts/{id}/deactivate` | alerts.manage | `DeactivateAlertRequest{reason}` |
| GET | `/emr/patients/{patientId}/diagnoses?encounterId&problemsOnly` | emr.read | → `DiagnosisDto[]` |
| POST | `/emr/encounters/{encounterId}/diagnoses` | emr.write | `AddDiagnosisRequest{icdCode?, title?, type, isPrimary, onsetDate, status, notes}` |
| PUT | `/emr/diagnoses/{id}` | emr.write | `UpdateDiagnosisRequest{type, isPrimary, status, onsetDate, notes, version}` |
| GET / POST | `/emr/patients/{patientId}/notes` | emr.read / emr.write | `WriteNoteRequest{encounterId?, admissionId?, type, title?, body}` → `ClinicalNoteDto` |
| PUT | `/emr/notes/{id}` | emr.write | `EditNoteRequest{type, title, body, version}` (author only, unsigned only) |
| POST | `/emr/notes/{id}/sign` | encounters.sign | `SignNoteRequest{version}` |
| POST | `/emr/notes/{id}/addendum` | emr.write | `AddendumRequest{body}` → new note with `amendsNoteId` |
| GET / POST | `/emr/patients/{patientId}/immunizations` | emr.read / immunizations.record | `RecordImmunizationRequest{vaccineName, vaccineCode, doseNumber, administeredOn, route, site, batchNumber, manufacturer, expiryDate, administeredBy, isExternal, nextDueOn, notes}` |
| POST | `/emr/immunizations/{id}/entered-in-error` | immunizations.record | `EnteredInErrorRequest{reason}` |
| GET | `/emr/templates?specialtyCode&search&includeInactive` | emr.read | → `TemplateListItem[]` (shared + own personal) |
| GET | `/emr/templates/{id}` | emr.read | → `TemplateDto` (SOAP defaults, complaints, default orders, medicines) |
| POST / PUT | `/emr/templates`, `/emr/templates/{id}` | templates.manage | `SaveTemplateRequest` |
| GET / POST / DELETE | `/emr/favourites`, `/emr/favourites/{id}` | emr.read / emr.write or prescriptions.write | `SaveFavouriteRequest{kind: Order|Drug, label, serviceItemId?, drugItemId?, dose defaults…}` |
| GET | `/emr/drugs?search&take` | prescriptions.write | → `PrescribableDrug[]` (see *Drug search*); `take` defaults to 20, capped at 50 |

### Clinical orders and e-prescribing (`/clinical`)

| Method | Path | Permission | Request → Response |
|---|---|---|---|
| POST | `/clinical/orders` | orders.place | `PlaceOrderRequest{patientId, encounterId?, visitId?, admissionId?, priority, clinicalNotes, items[{serviceItemId, notes, quantity?}], orderSetId?, confirmDuplicates}` → 201 `OrderDto` (409 `order.duplicate` lists pending duplicates). `quantity` (default 1) may exceed 1 only for blood components (≤ 10 units) and physiotherapy/dialysis (≤ 30 sessions); a service listed twice is rejected (422) rather than merged |
| GET | `/clinical/orders?patientId&encounterId&visitId&admissionId&status&page` | emr.read | → `PagedResult<OrderDto>` |
| GET | `/clinical/orders/{id}` | emr.read | → `OrderDto` |
| POST | `/clinical/orders/{id}/cancel`, `/clinical/orders/items/{itemId}/cancel` | orders.place | `CancelOrderRequest{reason}` |
| POST | `/clinical/orders/items/{itemId}/status` | orders.place | `UpdateOrderItemStatusRequest{status: InProgress|Completed, resultReference}` |
| GET / POST / PUT | `/clinical/order-sets[/{id}]` | emr.read / templates.manage | `SaveOrderSetRequest{code, name, specialtyCode, description, serviceItemIds, isActive, version?}` |
| POST | `/clinical/prescriptions` | prescriptions.write | `CreatePrescriptionRequest{patientId, encounterId?, visitId?, admissionId?, isInpatient, practitionerId?, notes, advice, refillsAllowed, items[PrescriptionItemRequest]}` → 201 `PrescriptionDto` (Draft) |
| GET | `/clinical/prescriptions?patientId&encounterId&status&page` | emr.read | → `PagedResult<PrescriptionListItem>` |
| GET / PUT | `/clinical/prescriptions/{id}` | emr.read / prescriptions.write | `UpdatePrescriptionRequest{notes, advice, refillsAllowed, items, version}` (drafts only) |
| POST | `/clinical/prescriptions/{id}/check` | prescriptions.write | → `SafetyCheckResponse{isBlocked, requiresAcknowledgement, hasControlledDrugs, alerts[SafetyAlert]}` |
| POST | `/clinical/prescriptions/{id}/sign` | prescriptions.sign | `SignPrescriptionRequest{overrideReason?, acknowledgeWarnings, confirmControlledDrugs, version}` → `SignPrescriptionResult{prescription, alerts}`; 422 `prescription.safety_block` / `prescription.warnings_unacknowledged` / `prescription.controlled_confirmation_required` with `data` = alerts; 403 `prescription.not_prescriber` |
| POST | `/clinical/prescriptions/{id}/cancel` | prescriptions.sign | `CancelPrescriptionRequest{reason}` |
| POST | `/clinical/prescriptions/{id}/repeat` | prescriptions.write | → 201 new Draft |
| POST | `/clinical/prescriptions/{id}/refill` | prescriptions.refill | `RefillPrescriptionRequest{overrideReason?, acknowledgeWarnings, confirmControlledDrugs}` → 201 `SignPrescriptionResult` (new signed prescription, `refillOfPrescriptionId`) |
| GET | `/clinical/prescriptions/{id}/print` | emr.read | → `PrescriptionPrintModel` (letterhead, prescriber qualification + council registration, patient, diagnoses, known allergies, lines with English/Hindi directions, advice, follow-up, `verificationToken`, `verificationPath`, `verificationUrl`) — print `verificationUrl` as the QR code (see *QR verification links*) |
| GET | `/clinical/prescriptions/{id}/document` | emr.read | → `PrintableDocumentDto{title, fileName, pageSize: "A5", landscape, html}`: the printed prescription for the staff app to print (see *Prescription and discharge summary documents*). Same rules as `/print`: signed only (422 `prescription.not_printable`), restricted-record guard, audit "Printed prescription {number}" |
| GET | `/clinical/prescriptions/{id}/pdf` | emr.read | → `application/pdf` attachment `prescription-{number}.pdf`, the same document. Same rules; audit "Downloaded prescription {number} as a PDF"; 503 `pdf.unavailable` when the server cannot make PDFs |
| GET / POST / PUT | `/clinical/drug-interactions[/{id}]` | emr.read / templates.manage | `SaveDrugInteractionRequest{drugA, drugB, severity, effect, management, isActive, version?}` |
| GET / POST / PUT | `/clinical/dose-limits[/{id}]` | emr.read / templates.manage | `SaveDoseLimitRequest{genericName, maxDailyDoseMg, route, notes, isActive, version?}` |
| GET | `/clinical/controlled-drug-register?from&to&page` | prescriptions.sign or mrd.records | → `PagedResult<ControlledDrugRegisterDto>` |
| GET | `/clinical/discharge-summaries/by-admission/{admissionId}` | emr.read | → `DischargeSummaryDto` |
| PUT | `/clinical/discharge-summaries/by-admission/{admissionId}` | discharge_summary.write | `SaveDischargeSummaryRequest{patientId, encounterId?, practitionerId?, admittedAt?, dischargedAt?, diagnoses, diagnosisCodes[], presentingComplaints, courseInHospital, procedures, investigationsSummary, conditionAtDischarge, medications, advice, followUpDate, followUpInstructions, version?}` |
| GET | `/clinical/discharge-summaries/by-admission/{admissionId}/prefill?patientId` | discharge_summary.write | → `DischargeSummaryPrefill` (from diagnoses, orders, prescriptions) |
| POST | `/clinical/discharge-summaries/{id}/sign` / `/amend` | discharge_summary.write | `SignDischargeSummaryRequest{version}` / `AmendDischargeSummaryRequest{reason, …, version}` |
| GET | `/clinical/discharge-summaries/{id}/print` | emr.read | → `DischargeSummaryPrint` |
| GET | `/clinical/discharge-summaries/{id}/document` | emr.read | → `PrintableDocumentDto` (`pageSize: "A4"`): the printed discharge summary. Same rules as `/print`: drafts print too, marked "DRAFT — not signed"; restricted-record guard; audit "Printed discharge summary" |
| GET | `/clinical/discharge-summaries/{id}/pdf` | emr.read | → `application/pdf` attachment `discharge-summary-{UHID}-{yyyy-MM-dd}.pdf`, the same document. Same rules; audit "Downloaded discharge summary as a PDF"; 503 `pdf.unavailable` |

Public (anonymous, rate limit policy `public`): `GET /public/prescriptions/verify/{token}` → `PrescriptionVerification{found, status: Valid|Cancelled, doctor, qualification, registrationNumber, facility, issuedOn, itemCount}` — no patient data.

#### QR verification links

`verificationPath` is the server-relative verification API above; it resolves only on the hospital LAN and is kept for
compatibility. `verificationUrl` is the absolute page a phone can open anywhere:
`{portal.baseUrl}/portal/verify/prescription/{token}` (the patient portal's public page, which calls the verification
API). It is null until the prescription is signed or while `portal.baseUrl` is not configured. `portal.baseUrl` is the
hospital's public https address (for example `https://care.nanahospitals.in`); patient links are `{base}/portal/...`.

#### Prescription and discharge summary documents

The server lays out both documents (ADR-012) from the data of the print models, as the staff app's print templates
did (`Documents/PrescriptionDocument.cs`, `Documents/DischargeSummaryDocument.cs`, shared parts and style in
`Documents/ClinicalDocumentParts.cs`). The staff app prints the HTML from `/document`; the PDFs for staff, the portal and
messages are made from the same HTML, so every copy is the same.

| | Prescription | Discharge summary |
|---|---|---|
| Page | A5 | A4 |
| Content | Letterhead with the facility logo and the prescriber's block on the right (name, qualification, specialty, council registration); patient strip (patient, UHID, age / sex, date signed); known allergies (in red, or "none recorded"); diagnosis; ℞ and one row per medicine: name, strength, Schedule H1/X tag, generic and form, dose with English directions and the route unless oral, the Hindi line under it (`lang="hi"`), instructions, duration ("Once" for STAT, whole weeks from 14 days), quantity; advice; follow-up; refills allowed; the QR code with the prescription number; "Digitally signed on …" and the prescriber's signature image above the name | Letterhead with the facility logo and the consultant's block; "Signed …" (and "amended … (reason)"); patient strip with the admission and discharge dates; diagnosis with ICD-10 codes, presenting complaints, course in hospital, procedures, investigations, condition at discharge, medicines on discharge, advice, follow-up; the emergency line; the consultant's signature image above the name |
| Footer on every page | "Asha Rao · UHID NH-000123 · Prescription NHRX260000123", and "Page X of Y" | "Asha Rao · UHID NH-000123 · Discharge summary · Discharged 25 Sep 2026", and "Page X of Y" |
| File name | `prescription-{number}.pdf` | `discharge-summary-{UHID}-{yyyy-MM-dd}.pdf`: the discharge date in IST, else the signing or admission date |
| Not final | Only signed prescriptions are printed. A draft or cancelled copy would say "DRAFT — not valid for dispensing" or "CANCELLED — do not dispense" at the top and in the footer | A draft says "DRAFT — not signed" at the top and in the footer, and has no signature image |

* The QR code opens `verificationUrl` (see *QR verification links*). Without `portal.baseUrl` the verification code is
  printed without a QR code, as on the lab reports.
* The logo and signatures come from the file store (`DocumentAssets`: PNG, JPEG, WebP or SVG up to 1 MB); a missing
  image is left out. The signature image is printed only once the document is signed.
* Every value is HTML-encoded, and the doctor's text keeps its line breaks.

#### No known allergies

A clinician can record that the patient has **no known allergies** (`NoKnownAllergiesStatement`, table
`clinical.no_known_allergies`: patientId, recordedAt, recordedByUserId, recordedByName). It is patient-wide like
allergies; a unique index on live rows (`ux_no_known_allergies_patient`, `is_deleted = false`) allows one statement per
patient, and removed statements stay soft-deleted as history. The status shown on the banner
(`NoKnownAllergiesStatement.StatusOf`, unit-tested) is:

| Chart | `status` | `recordedAt` / `recordedByName` |
|---|---|---|
| at least one active allergy (a leftover statement never hides it) | `Recorded` | the most recently recorded active allergy and its author |
| no active allergy, statement recorded | `NoKnownAllergies` | the statement |
| neither (including only inactive, resolved or entered-in-error allergies) | `NotRecorded` | null |

The statement cannot be recorded while an active allergy exists (409 `allergy.active_exist`). Recording an allergy, or
re-activating one, soft-deletes the statement in the same save. A patient merge keeps at most one live statement (the
surviving record's first) and none when either record has an active allergy.

#### Banner flags on every clinical screen

The chart reads its banner from the summary. Every other clinical screen, whose staff often may not read the chart,
reads `GET /emr/patients/{id}/flags` with `clinical.flags.read`, so the lab, imaging, pharmacy, theatre, dialysis,
physiotherapy, blood bank and dietitians see the same allergies and alerts as the doctor. The front office, billing,
insurance and CRM desks do not hold the permission by default (data minimisation); administrators can grant it.

In the staff app, `PatientBannerWithFlags` (`src/modules/emr`) adds the flags to a screen's own banner. It is used on lab
orders, sample result entry and reports, imaging and cardiology studies, pharmacy dispensing and ward indents, the
nursing station, admissions, OT bookings, dialysis, physiotherapy, blood requests, teleconsultations, remote monitoring,
health checkups, the patient profile and the billing banners. `PatientFlagsStrip` shows the flags alone where a dialog
or counter acts on a patient without a banner: sample collection, the pharmacy counter and diet orders. Both:
* say "Checking allergies…" while loading, and "Allergies and alerts hidden: restricted record" or "… could not be
  loaded" when they cannot be had, so a missing flag is never read as "no allergies";
* show the clinical alerts toned by severity (critical in red), ahead of the screen's own (bed alerts, MLC, emergency),
  without repeating a label;
* refresh on `clinical.flags.changed` (topic `patient:{id}`, which every staff member may follow).

#### Drug search

`GET /emr/drugs?search&take` lets prescribers, who hold no inventory permission, search the SupplyChain drug master:
active items of type Drug matched on code, barcode, name, generic or brand (`InventoryQueries.Search`), in name order.
`PrescribableDrug{id, code, name, genericName, brandName, strength, dosageForm, schedule (None|Otc|G|H|H1|X),
isControlled, controlledSchedule, requiresPrescription, isColdChain, baseUnit, available, nearestExpiry}`:
* `isControlled` is true for Schedule H1/X and NDPS items (the lines needing explicit confirmation and the register);
  `controlledSchedule` is then `X` for Schedule X, else `H1`.
* `available` / `nearestExpiry` sum the issuable stock of the signed-in facility's active pharmacy stores
  (`InventoryQueries.AvailabilityAsync`); both are null when the facility has no pharmacy store, `available` is 0 when
  out of stock.
* Fill a `PrescriptionItemRequest` with `drugItemId = id`, `drugName = name`, `genericName`, `strength`, `dosageForm`,
  `isControlledDrug = isControlled`, `controlledSchedule`.

### Medical records (`/mrd`, tag Medical Records)

| Method | Path | Permission | Request → Response |
|---|---|---|---|
| GET | `/mrd/icd-codes?search&chapter&billableOnly&release&limit` | emr.read or mrd.coding | → ranked `IcdCodeDto[]` (code prefix incl. "e119" → E11.9, title words, shorthand keywords) |
| GET | `/mrd/icd-codes/online?search&release&limit` | emr.read or mrd.coding | → `OnlineIcdResult[]` via `IIcdLookup` (422 `icd.online_unavailable` / `icd.online_failed`) |
| GET | `/mrd/icd-classification` | emr.read or mrd.coding | chapters with their blocks |
| POST / PUT | `/mrd/icd-codes[/{id}]` | mrd.coding | `SaveIcdCodeRequest{code, title, release, isBillable, keywords, fromOnlineLookup, isActive}` |
| GET / POST / PUT | `/mrd/procedure-codes[/{id}]` | emr.read or mrd.coding / mrd.coding | `SaveProcedureCodeRequest` |
| GET | `/mrd/codings?status&from&to&page` | mrd.coding | coding worklist (pending first) |
| POST | `/mrd/codings` | mrd.coding | `CreateCodingRequest{patientId, encounterId?, admissionId?, visitId?, episodeDate?, notes}` |
| GET / PUT | `/mrd/codings/{id}` | mrd.coding | `SaveCodingRequest{entries[{kind, code, isPrimary}], notes, version}` (catalogue codes only; categories with subdivisions rejected) |
| POST | `/mrd/codings/{id}/verify` | mrd.coding | `VerifyCodingRequest{version}` (a different coder must verify) |
| GET | `/mrd/codings/{id}/suggestions` | mrd.coding | → `CodingSuggestion[]` |
| GET | `/mrd/reports/morbidity?from&to&groupBy=code|block|chapter&source=clinical|coded` | mrd.records | → `MorbidityReport` (episodes, patients, sex and age bands; ≤ 1 year) |
| GET / POST | `/mrd/records` | mrd.records | list (`status`, `overdue`, `retentionDue`, `assignedPractitionerId`, `search`) / `CreateMedicalRecordRequest` |
| GET | `/mrd/records/{id}` | mrd.records | → `MedicalRecordDto` with deficiencies |
| POST | `/mrd/records/{id}/deficiencies` | mrd.records | `AddDeficiencyRequest{type, description, assignedPractitionerId, dueDate}` |
| POST | `/mrd/records/deficiencies/{deficiencyId}/resolve` | mrd.records or emr.write | `ResolveDeficiencyRequest{note}` |
| POST | `/mrd/records/{id}/scan` | mrd.records | detects unsigned encounters/notes, missing diagnosis, missing/unsigned discharge summary, pending coding; auto-closes fixed ones |
| POST | `/mrd/records/{id}/complete`, `/archive` | mrd.records | `ArchiveMedicalRecordRequest{location}`; completion sets `retainUntil` (OP 3 y, IP 5 y, MLC permanent) |
| GET / POST | `/mrd/record-requests` | mrd.records | `CreateRecordRequestRequest{patientId, medicalRecordId?, requestedByName, requestingDepartment, purpose, details}`; status filter `Overdue` supported |
| POST | `/mrd/record-requests/{id}/issue` / `return` / `cancel` | mrd.records | `IssueRecordRequest{dueBackOn}` / — / `CancelRecordRequest{reason}` |

### Health checkups (`/health-checkups`, tag Health Checkups)

| Method | Path | Permission | Request → Response |
|---|---|---|---|
| GET | `/health-checkups/packages?search&includeInactive`, `/{id}` | healthcheck.manage or emr.read | → `PackageDto` |
| GET | `/health-checkups/packages/{id}/eligibility?patientId` | healthcheck.manage or emr.read | → `PackageEligibility{isEligible, reason, excludedItems}` |
| POST / PUT | `/health-checkups/packages[/{id}]` | healthcheck.manage | `SavePackageRequest{code "HC-…", name, description, gender, minAgeYears, maxAgeYears, price, isActive, items[{kind, serviceItemId?, departmentCode?, gender, minAgeYears}], version?}` — also creates/updates the package's `org.service_items` row (category HealthCheckup) |
| GET / POST | `/health-checkups/bookings` | healthcheck.manage or emr.read / healthcheck.manage | `BookCheckupRequest{patientId, packageId, scheduledDate, visitId?, notes}` (a `visitId` makes it a walk-in: checked in and charged at once) → `BookingDto` (itinerary + one ClinicalOrder for the included lab/radiology/cardiology tests) |
| GET | `/health-checkups/bookings/{id}` | healthcheck.manage or emr.read | → `BookingDto` |
| POST | `/health-checkups/bookings/{id}/check-in` | healthcheck.manage | `CheckInCheckupRequest{visitId?}` — posts the package charge (see *Package billing*); refused if Billing refuses the charge, if the package has no service item (422 `package.not_billable`) or if the visit is another patient's |
| POST | `/health-checkups/bookings/{id}/itinerary/{itemId}/complete` / `skip` | healthcheck.manage, emr.write or vitals.record | `CompleteStopRequest{notes}` / `SkipStopRequest{reason}` |
| PUT | `/health-checkups/bookings/{id}/summary` | emr.write | `CheckupSummaryRequest{summary, recommendations}` (reviewing doctor) |
| POST | `/health-checkups/bookings/{id}/complete` | healthcheck.manage or emr.write | all stops done/skipped + summary required |
| POST | `/health-checkups/bookings/{id}/cancel` / `no-show` | healthcheck.manage | cancels the package charge, then the booking and the remaining tests (`ServiceOrderCancelled`); 422 `booking.package_billed` (booking unchanged) when the charge is on a finalised bill |
| GET | `/health-checkups/bookings/{id}/report` | healthcheck.manage or emr.read | → `CheckupReport{booking, patient, letterhead, reviewingDoctor, order}` |
| GET | `/health-checkups/bookings/{id}/report/document`, `/report/pdf` | healthcheck.manage or emr.read | The full check-up report (summary, then every released lab, radiology and cardiology result) as a printable document or PDF. Served by the Diagnostics module, which reads the results; see [diagnostics.md](diagnostics.md) |
| GET / POST | `/health-checkups/corporate-batches`, `GET /{id}` | healthcheck.manage | `CorporateBatchRequest{corporateName, contactPerson, contactPhone, reference, packageId, scheduledDate, patientIds[≤500], notes}` → `CorporateBatchResult{batchId, batchNumber, booked, skipped[{patientId, reason}]}` |

#### Package billing

A checkup is billed as **one charge** through `IChargePoster` (Billing), posted when the patient **checks in** — when the
front desk collects the package fee — or at booking for a walk-in booked with a visit:

| Field | Value |
|---|---|
| `SourceModule` / `SourceReference` | `healthcheck.package` / the booking id (`booking.Id.ToString()`); posting is idempotent on this key |
| `ServiceItemId`, `Quantity` | the package's HC-* service item, 1 |
| `PatientId`, `VisitId`, `PractitionerId` | the booking's patient, the check-in visit (if any), none |
| `Description`, `ServiceDate` | "*package name* (*booking number*)", the check-in time |

Billing is always asked before the booking changes, and the charge is committed with the booking in one unit of work
(IChargePoster never saves). Cancelling or marking a no-show calls `CancelAsync` for the same key first; if the charge is
on a finalised bill the booking is left unchanged and the call returns 422 `booking.package_billed` (billing cancels the
bill or issues a credit note, then the booking can be cancelled). The component tests carry `PackageBookingId` on
`ServiceOrderPlaced` and are never priced individually. Diagnostics can gate on the charge through
`IBillingStatusProvider.GetStatesAsync("healthcheck.package", [bookingId])`.

### Patient portal (`/portal`, permission `portal.access`, scoped by `PortalPatientScope`)

| Method | Path | Response |
|---|---|---|
| GET | `/portal/prescriptions?patientId&page` | `PagedResult<PortalPrescriptionListItem>` (signed/cancelled only) |
| GET | `/portal/prescriptions/{id}` | `PortalPrescriptionDetail` (bilingual directions, doctor registration, QR `verificationToken` and `verificationUrl`, see *QR verification links*) |
| GET | `/portal/prescriptions/{id}/pdf` | `application/pdf` attachment `prescription-{number}.pdf`: the printed prescription. Signed only: 404 for a draft, 422 `prescription.not_printable` for a cancelled one; audit "Downloaded prescription {number} as a PDF (patient portal)"; 503 `pdf.unavailable` |
| GET | `/portal/health-records?patientId` | `PortalHealthRecord{allergies, problems, recentEncounters (no free-text notes), immunizations, dischargeSummaries}` |
| GET | `/portal/discharge-summaries/{id}` | `DischargeSummaryDto` (signed only) |
| GET | `/portal/discharge-summaries/{id}/pdf` | `application/pdf` attachment `discharge-summary-{UHID}-{yyyy-MM-dd}.pdf`: the printed discharge summary (signed or amended only, 404 for a draft); audit "Downloaded discharge summary as a PDF (patient portal)"; 503 `pdf.unavailable` |
| POST | `/portal/record-requests` | `PortalRecordRequest{patientId, purpose, details}` → 201 |

## Prescribing safety (ERX-02/06)

Checks run in `Domain/Safety` (pure, unit-tested) against the prescription, the patient's current medicines (signed
prescriptions still within their course, all facilities) and active drug/other allergies:

| Check | Result |
|---|---|
| Allergy — ingredient matches allergen (combination products split; Indian/US spellings and salts normalised) | Major, **blocking** |
| Class allergy — penicillins, cephalosporins, carbapenems, sulfonamides, NSAIDs (aspirin included), fluoroquinolones, macrolides, tetracyclines, aminoglycosides, opioids, ACE inhibitors | Major, **blocking** |
| Cross-sensitivity (penicillin ↔ cephalosporin/carbapenem) | Moderate/Minor warning |
| Interaction table — Major / Moderate / Minor | Major **blocking**; others warnings |
| Duplicate therapy (same ingredient in two products, including current medicines; two NSAIDs) | Moderate warning |
| Maximum daily dose, **summed per ingredient** across the new lines and current medicines | Major, **blocking** |
| Dose not computable although a limit exists | Minor warning (never silently skipped) |
| Methotrexate on a daily schedule | Major, **blocking** |
| Schedule H1/X medicine | Info + explicit `confirmControlledDrugs` + controlled-drug register entry |

Blocking alerts need `overrideReason` (≥ 10 characters; audited as `prescription.safety_override`, severity Critical);
every other warning needs `acknowledgeWarnings`. The alerts shown at signing are stored on the prescription
(`safety_alerts` jsonb). Only the prescribing doctor can sign.

## Events

Raised:

| Event | When |
|---|---|
| `ServiceOrderPlaced` (Platform) | one per order line on placement (encounter orders, order sets, health-checkup tests); carries the line's `Quantity`, and `PackageBookingId` (so `IsPackageComponent`) on health-checkup tests, which Billing charges once under the package. An order refuses to place if its source and package link disagree (`order.package_link`) |
| `ServiceOrderCancelled` (Platform) | line or order cancelled, checkup cancelled / no-show |
| `PrescriptionSigned` (Platform) | prescription signed or refill issued (lines with snapshot and quantity) |
| `PrescriptionCancelled` (Platform) | signed prescription cancelled |
| `EncounterSigned`, `EncounterAmended` (Clinical.Domain) | encounter signed / amended (follow-up and referral data for CRM, doctor fees) |
| `DischargeSummarySigned` (Clinical.Domain) | discharge summary first signed |
| `HealthCheckupCompleted` (Clinical.Domain) | checkup completed |

Handled (same transaction):

| Event | Action |
|---|---|
| `PatientsMerged` (Patients) | re-points every clinical row (all facilities, including soft-deleted) to the surviving patient; keeps at most one "no known allergies" statement, and none when an active allergy exists |
| `PatientAdmitted` | opens the MRD chart of the admission |
| `PatientDischarged` | sets chart discharge time and due date (+7 days), queues ICD coding (Pending), stamps the discharge summary |
| `DiagnosticReportReleased` | completes the order line and adds/re-opens a "result to review" for the ordering doctor |

Real-time (ids only): `clinical.order.placed` / `clinical.order.changed` (facility), `pharmacy.prescription.signed` /
`pharmacy.prescription.cancelled` (`pharmacy:{facilityId}`), `clinical.flags.changed` / `clinical.vitals.recorded`
(`patient:{id}`). In-app notifications: referral to the referred doctor on signing; critical vitals to the treating doctor.

## Number series

| Key | Format | Reset |
|---|---|---|
| `clinical-order` | `{FAC}OR{YY}{SEQ:7}` | yearly, per facility |
| `prescription` | `{FAC}RX{YY}{SEQ:7}` | yearly, per facility |
| `healthcheck-booking` | `{FAC}HC{YY}{SEQ:6}` | yearly, per facility |
| `healthcheck-batch` | `{FAC}HCB{YY}{SEQ:4}` | yearly, per facility |
| `mrd-request` | `{FAC}RR{YY}{SEQ:6}` | yearly, per facility |

## Notification templates

Uses the platform template `prescription.shared` (WhatsApp) for OP prescriptions after signing. No new templates.

Documents that messages can carry (`INotificationDocumentSource`, made when the message is sent):

| Kind | Document | Used by |
|---|---|---|
| `clinical.prescription` | The printed prescription's PDF; none once the prescription is cancelled | `prescription.shared`: WhatsApp sends it in the template's document header when the template is marked `whatsAppDocumentHeader` |
| `clinical.discharge-summary` | The signed (or amended) discharge summary's PDF; none for a draft | Nothing in this module queues a message with it yet |

## Seed data (`ClinicalSeeder`, Order 300, idempotent)

* 594 WHO ICD-10 (2019) codes across all 22 chapters with chapter/block, billable flag and clinical shorthand keywords.
* 96 ICD-10-PCS procedure codes (general surgery, obstetrics/gynaecology, orthopaedics, urology/nephrology, cardiology,
  GI, critical care, ENT/eye/dental, transfusion, imaging).
* 60 drug–drug interactions (36 Major, 22 Moderate, 2 Minor) and 48 maximum adult daily doses.
* 13 templates (GM ×4, PED ×2, ORTH ×2, OBG ×2, CARD ×2, ENT) and 5 order sets (fever, diabetes review, chest pain,
  pre-op, first ANC).
* Health packages, each also registered as a service item (category HealthCheckup): `HC-BASIC`, `HC-EXEC`,
  `HC-WOMEN`, `HC-SENIOR`, `HC-DIABETES`, `HC-CARDIAC`, built from LAB-CBC, LAB-FBS, LAB-PPBS, LAB-HBA1C, LAB-LIPID,
  LAB-LFT, LAB-KFT, LAB-TFT, LAB-URINE-RE, LAB-ESR, LAB-CRP, LAB-VITD, LAB-VITB12, LAB-PSA, LAB-ELECTROLYTES,
  RAD-XR-CHEST, RAD-USG-ABD, RAD-USG-PELVIS, RAD-MAMMO, CARD-ECG, CARD-ECHO, CARD-TMT and CONS-NEW. Codes not yet
  seeded by Diagnostics are skipped (logged) and added on a later start unless the hospital has edited the entry.
