# Clinical module (`clinical`)

EHR / EMR physician desktop, e-prescribing, clinical orders, discharge summaries, medical records with ICD coding (MRD)
and master health checkups (MHC). Project: `src/server/Modules/HyFib.HealthCare360.Modules.Clinical` (layer 4,
references Patients, Scheduling and SupplyChain; the last two read-only, for the doctor's appointments on the worklist
and the prescribers' drug search). Database schema: `clinical`.
Tests: `tests/Modules/HyFib.HealthCare360.Modules.Clinical.Tests`.

| Requirement | Where |
|---|---|
| EHR-01 longitudinal record | Encounters, vitals, allergies, diagnoses/problems, notes, procedures (see *Procedures*), immunisations, orders, prescriptions, discharge summaries; `GET /emr/patients/{id}/timeline` |
| EHR-02 timeline by type/kind of care/department/date | `GET /emr/patients/{id}/timeline?types=&encounterTypes=&from=&to=&departmentId=` |
| EHR-03 banner flags | `GET /emr/patients/{id}/summary` (allergies with `allergyStatus`, alerts, problems, current medicines, last vitals + flags) for the chart; `GET /emr/patients/{id}/flags` (allergies and alerts alone, `clinical.flags.read`) for every other clinical screen's banner (see *Banner flags on every clinical screen*); `GET /emr/patients/{id}/allergies/status`, "no known allergies"; real-time `clinical.flags.changed` on `patient:{id}` |
| EHR-04/06/07 documents, consent, break-the-glass | Patients module (documents: see *Patient documents (EHR-04)*; consents: see *Consents (EHR-06)*); every clinical read goes through `PatientAccessGuard` + `IAuditLogger.PatientRecordAccessedAsync` (`ClinicalAccess`) |
| INT-BIO patient identity verification | Patients module: checking a patient's identity with their Aadhaar fingerprint (see *Identity checks (INT-BIO)*) |
| EMR-01 worklist | `GET /emr/worklist` (queue, the doctor's appointments, open encounters, unsigned notes, draft prescriptions, results to review, chart deficiencies) and the ward round `GET /inpatient/rounds` (Inpatient module); see *The doctor's worklist (EMR-01)* |
| EMR-02..07 | Encounters (SOAP, follow-up, referral, sign, amend), templates, vitals, diagnoses (ICD), orders, prescriptions, favourites; voice dictation into a SOAP draft with ICD suggestions (*Dictate with AI*, AI module) |
| ERX-01..06 | Prescriptions, favourites/templates, safety checks, signing → `PrescriptionSigned`, the printed A5 prescription and its PDF with QR verification, refills/repeat, Schedule H1/X confirmation + register. ERX-04: bilingual (English/Hindi) directions, the prescriber's council registration number, the doctor's own DSC signature from HC360 Desktop (see *Signing with the doctor's DSC*) and the QR code that re-verifies it |
| MRD-01..06 | ICD-10 / ICD-10-PCS catalogues + ranked search + WHO ICD-API lookup, coding at discharge, chart deficiencies, record requests, retention/archival, morbidity index |
| MHC-01..05 | Packages (billable service items), bookings, itinerary, doctor summary, consolidated report, corporate batches |
| IMG-02/03 (the record's part) | Released lab, imaging and cardiology reports open from the chart's timeline and orders, with an imaging study's images and the PACS viewer; see *Results on the patient's record* |

All paths below are under `/api/v1`. Lists accept `page`, `pageSize`, `search` where noted. Updates carry `version`
(PostgreSQL `xmin`); a stale version returns 409. Dates in query strings are IST calendar dates.

## Permissions

| Permission | Purpose | Default roles |
|---|---|---|
| `clinical.emr.read` | Read the clinical record | Doctor, Nurse, MrdOfficer |
| `clinical.flags.read` | See allergies and alerts on the patient banner, without the chart | Doctor, Nurse, MrdOfficer, Pharmacist, LabTechnician, Pathologist, Radiographer, Radiologist, CardiologyTechnician, Cardiologist, OtCoordinator, DialysisTechnician, Physiotherapist, BloodBankOfficer, Dietitian |
| `clinical.emr.write` | Encounters, diagnoses, notes, procedures, referrals, checkup summaries, result acknowledgement | Doctor |
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
| `mrd.catalogue.import` | Import the WHO ICD-10 and CMS ICD-10-PCS catalogue files (changes every user's code lists) | HospitalAdmin |
| `healthcheck.manage` | Packages, bookings, corporate batches | HospitalAdmin, FrontOffice |

## Endpoints

### EMR — physician desktop (`/emr`, tag EMR)

| Method | Path | Permission | Request → Response |
|---|---|---|---|
| GET | `/emr/worklist?date&practitionerId&departmentId` | emr.read | → `EmrWorklist` (defaults: today and the signed-in doctor; department optional; restricted patients masked, see below). `appointments`: the doctor's bookings for the day in slot order, or the department's when no doctor is chosen, without cancelled and rescheduled ones; each with its status, visit type, video flag, and once checked in the visit, queue token and encounter |
| POST | `/emr/results/{id}/acknowledge` | emr.write | `AcknowledgeResultRequest{note}` → 204 |
| GET | `/emr/patients/{patientId}/summary` | emr.read | → `PatientClinicalSummary` (patient, `allergiesEverRecorded`, `allergyStatus` (`Recorded`\|`NoKnownAllergies`\|`NotRecorded`), allergies, alerts, activeProblems, currentMedications, lastVitals+flags, recentEncounters(5), resultsAwaitingReview) |
| GET | `/emr/patients/{patientId}/flags` | flags.read or emr.read | → `PatientFlags{allergyStatus, allergies[AllergySummary], alerts[AlertSummary]}`: the summary's flags alone (same rule, same order: most severe first, alerts in force today). Restricted-record guard (403 `patient.restricted`) and a PHI-access audit entry ("Viewed allergies and alerts") like the chart; 404 for an unknown patient |
| GET | `/emr/patients/{patientId}/timeline?types=Encounter,Vitals,…&encounterTypes=OutPatient,InPatient,…&from&to&departmentId&page` | emr.read | → `PagedResult<TimelineEntry>` (types: Encounter, Vitals, Diagnosis, Prescription, Order, Note, Immunization, Document, DischargeSummary, Result, Procedure). `encounterTypes` (OutPatient, InPatient, Emergency, Telehealth, HealthCheckup) and `departmentId` keep the matching encounters and the entries recorded in one of them; documents and entries recorded outside an encounter then drop out. An unknown name is a 400 on `types` or `encounterTypes`. A `Result`'s `id` is the released report's id in its department and `reportKind` (`Laboratory`, `Radiology`, `Cardiology`; null on other entries) says which (see *Results on the patient's record*) |
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
| GET / POST | `/emr/patients/{patientId}/procedures?includeEnteredInError` | emr.read / emr.write | `RecordProcedureRequest{encounterId?, performedAt, code?, display?, performerPractitionerId?, performerName?, side, bodySite?, outcome?, notes?}` → 201 `ProcedureDto` (see *Procedures*); the list is newest first, from every facility, with a PHI-access audit entry ("Viewed procedures") |
| PUT | `/emr/procedures/{id}` | emr.write | `CorrectProcedureRequest{…same fields, reason}` → `ProcedureDto` (`Amended`); 422 `procedure.entered_in_error` once voided |
| POST | `/emr/procedures/{id}/entered-in-error` | emr.write | `EnteredInErrorRequest{reason}` → `ProcedureDto` (`EnteredInError`); 422 `procedure.already_voided` |
| GET | `/emr/templates?specialtyCode&search&includeInactive` | emr.read | → `TemplateListItem[]` (shared + own personal) |
| GET | `/emr/templates/{id}` | emr.read | → `TemplateDto` (SOAP defaults, complaints, default orders, medicines) |
| POST / PUT | `/emr/templates`, `/emr/templates/{id}` | templates.manage | `SaveTemplateRequest` |
| GET / POST / DELETE | `/emr/favourites`, `/emr/favourites/{id}` | emr.read / emr.write or prescriptions.write | `SaveFavouriteRequest{kind: Order|Drug, label, serviceItemId?, drugItemId?, dose defaults…}` |
| GET | `/emr/drugs?search&take` | prescriptions.write | → `PrescribableDrug[]` (see *Drug search*); `take` defaults to 20, capped at 50 |

#### Procedures

`ProcedureRecord` (`clinical.procedure_records`) holds the procedures done for the patient as structured data (EHR-01), apart
from the free-text "Procedure" note and MRD coding. A record has the time it was done, a code from the procedure
catalogue (`mrd/procedure-codes`, ICD-10-PCS; a code not in the catalogue is a 400 on `code`) or a description alone
(`display`, which otherwise defaults to the code's title), the performer (a practitioner, or a name for someone from
elsewhere; neither means the recording doctor), side, site, outcome and notes, and optionally the encounter it was done
in (its admission and visit are copied). A correction changes the fields and leaves the record `Amended` with the reason
(the previous values are in the data-change audit); a record made in error is `EnteredInError` with the reason, kept
for the audit trail but off the timeline. Operations completed in the theatre arrive on their own (`Source`
`OperationTheatre`, see *Events*). The chart's *Procedures* tab lists, records, corrects and voids them.

#### Results on the patient's record (IMG-02, IMG-03)

A lab, imaging or cardiology report is on the chart once Diagnostics releases it (`DiagnosticReportReleased`, see
*Events*): as a *Result* on the timeline, which names the report and its department (`id`, `reportKind`), and on its
order line, which the release completes with the report's id as `resultReference`. Clinical keeps only that id and
kind (`ResultReview`): it may not read Diagnostics' tables. The chart opens the report in a dialog (`ResultDialog`, the
worklist's result review without the note and *Acknowledge*) through the department's own endpoints, so their
permissions, released-only rule, restricted-record guard and PHI audit apply as on the department's screens:

| Report | The chart asks | It shows | Permission |
|---|---|---|---|
| Lab | `GET /lab/reports/{id}` | The results with their flags and reference ranges; Print and Download PDF of the printed report (`…/document`, `…/pdf`); *Full report* opens the lab report screen | `lab.orders.read` |
| Imaging | `GET /radiology/reports/{id}` for the study, then `GET /radiology/studies/{studyId}` and `…/report` | The signed report as the study screen shows it, with Print and Download PDF; once the study is linked with PACS (`pacsLinkedAt`), its series, thumbnails and image viewer (`…/images`) and *Open in PACS viewer* (`…/viewer`); *Open study* opens the study screen | Report: `radiology.studies.read` or `radiology.reports.write` (both endpoints allow them). Images and viewer: `radiology.images.view` |
| Cardiology | `GET /cardiology/reports/{id}` for the study, then `GET /cardiology/studies/{studyId}` | The signed report with Print and Download PDF; *Open in PACS viewer* once the PACS holds the study's DICOM files; *Open study* opens the study screen | `cardiology.studies.read` (or `cardiology.studies.manage`, `cardiology.reports.verify`) |

* The timeline opens a result's report only for staff who may open that department's reports; for anyone else a result
  leads to the *Orders* tab, as before. A completed order line, on the chart's *Orders* tab and in the consultation,
  offers *View report* on the same terms. Images and the PACS viewer are offered only for a study linked with PACS, and
  only to staff who may see images (not nurses, by default).
* Restricted (VIP) records: the chart opens only through the restricted-record guard, and each department endpoint
  checks it again, so no report, study or image is shown without access; the dialog then says the record is restricted.
  The report-to-study look-ups return ids and the report's status only.
* The departments read in the signed-in facility, as on their screens: a result released at another of the hospital's
  facilities is on the timeline, which spans every facility, but its report opens there only; the dialog says so.
* The doctor's worklist shows the same report in its result review, with the note and *Acknowledge*.

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
| POST | `/clinical/prescriptions/{id}/refill` | prescriptions.refill | `RefillPrescriptionRequest{overrideReason?, acknowledgeWarnings, confirmControlledDrugs}` → 201 `SignPrescriptionResult` (new signed prescription, `refillOfPrescriptionId`, in the original prescriber's name). Where the DSC is required its `finalCopyAvailable` is false: only that prescriber can make it printable, by signing it with their DSC (*Signing with the doctor's DSC*) |
| GET | `/clinical/prescriptions/{id}/print` | emr.read | → `PrescriptionPrintModel` (letterhead, prescriber qualification + council registration, patient, diagnoses, known allergies, lines with English/Hindi directions, advice, follow-up, `verificationToken`, `verificationPath`, `verificationUrl`) — print `verificationUrl` as the QR code (see *QR verification links*) |
| GET | `/clinical/prescriptions/{id}/document` | emr.read | → `PrintableDocumentDto{title, fileName, pageSize: "A5", landscape, html}`: the printed prescription for the staff app to print (see *Prescription and discharge summary documents*). Same rules as `/print`: signed only (422 `prescription.not_printable`), where the hospital requires the doctor's DSC a valid DSC signature (422 `prescription.digital_signature_required`, `…_untrusted` or `…_invalid`, see *Facility setting*), restricted-record guard, audit "Printed prescription {number}" |
| GET | `/clinical/prescriptions/{id}/pdf` | emr.read | → `application/pdf` attachment `prescription-{number}.pdf`, the same document. Same rules; audit "Downloaded prescription {number} as a PDF"; 503 `pdf.unavailable` when the server cannot make PDFs |
| GET | `/clinical/prescriptions/{id}/signing-content` | prescriptions.sign | → `PrescriptionSigningContent{prescriptionId, prescriptionNumber, format, content (base64 canonical bytes), contentHash, patientName, patientUhid, itemCount}` for HC360 Desktop to sign. The prescribing doctor only (403 `prescription.not_prescriber`, also for another doctor who may sign prescriptions); the bytes name the patient, so the restricted-record guard applies and each fetch is in the PHI access audit ("Fetched prescription {number} to sign with a DSC"); 422 `prescription.signature_off` while the setting is off, `prescription.signature_not_set_up` while the DSC trust folder holds no root (before the doctor enters a PIN), `prescription.not_signed` for a draft or cancelled one; 409 `prescription.already_digitally_signed` |
| POST | `/clinical/prescriptions/{id}/digital-signature` | prescriptions.sign | `AttachDigitalSignatureRequest{signature}` (DER detached CMS, base64, ≤ 64 KB) → `PrescriptionDigitalSignatureDto`. Same rules as `signing-content`, and 422 `prescription.signature_invalid` (the check failed, also for a signing time or a certificate extension that cannot be read; the detail says why), `prescription.signature_time` (signed more than 15 minutes before, or more than 5 minutes after, the server's clock) or `prescription.signature_untrusted` (the certificate does not chain to a root in the DSC trust folder: not a DSC, whatever the policy), `prescription.signer_mismatch` (the certificate is issued to someone other than the prescribing doctor: see *DSC trust folder*) or `prescription.certificate_unsupported` (a name longer than 200 characters, a subject or issuer longer than 1,000, or a serial or thumbprint longer than 128: HC360 cannot keep it). Audited as `prescription.digitally_signed`. Queues the "prescription shared" message if the patient was not told at signing (*Facility setting*) |
| GET | `/clinical/prescriptions/{id}/digital-signature` | emr.read | → `PrescriptionDigitalSignatureDto{state: NotSigned|Valid|Invalid|Untrusted, policy: Off|Optional|Required, signedAt, signerName, signerSubject, certificateSerialNumber, certificateIssuer, certificateThumbprint, contentHash, contentMatchesRecord, chainStatus, chainTrusted, message, finalCopyAvailable}`, re-checked on every read. `chainStatus` is "Trusted" or why the chain does not hold, built against the trust folder as it is now; when the signature itself does not check out the chain is not built and it reads "Not checked: the signature does not check out", never the "Trusted" stored at signing. `finalCopyAvailable`: the DSC rules let the prescription be printed, downloaded and shared (always unless the policy is `Required`, and then only with a Valid signature). `PrescriptionDto` carries the same `finalCopyAvailable` |
| GET | `/clinical/prescriptions/{id}/signature.p7s` | emr.read | → `application/pkcs7-signature` attachment `prescription-{number}.p7s`; 404 until signed with a DSC |
| GET | `/clinical/prescriptions/{id}/signed-content` | emr.read | → `application/json` attachment `prescription-{number}.json`: the exact bytes the DSC signed. They name the patient, so each download is in the PHI access audit ("Downloaded the signed content of prescription {number}") |
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

A signed or amended summary, and a signed prescription, also download as ABDM health records (NRCeS FHIR documents)
from the Interop module: `GET /abdm/bundles/discharge-summaries/{id}` and `GET /abdm/bundles/prescriptions/{id}`,
permission `abdm.export`, while ABDM health records are switched on. Like the printed copy, the download does not need
the patient's record-sharing consent, and a restricted record needs the right to open it. A cancelled prescription is
not exported, and a prescription is exported only under the DSC rule of its printout
([interop.md](interop.md#abdm-health-records-ehr-08)).

Public (anonymous, rate limit policy `public`): `GET /public/prescriptions/verify/{token}` → `PrescriptionVerification{found, status: Valid|Cancelled, doctor, qualification, registrationNumber, facility, issuedOn, itemCount, prescriptionNumber, digitalSignature?: {state: Valid|Invalid|Untrusted, signerName, certificateSerialNumber, certificateIssuer, signedAt, contentHash, chainTrusted, message}, signaturePath}` — no patient data; the DSC signature, and its chain to the DSC trust folder's roots, are verified again for each answer. `GET /public/prescriptions/verify/{token}/signature.p7s` → the .p7s (404 without a DSC signature).

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
| Page | A5, or A4 when the **Prescriptions** print template says so | A4 |
| Content | Letterhead with the facility logo and the prescriber's block on the right (name, qualification, specialty, council registration); patient strip (patient, UHID, age / sex, date signed); known allergies (in red, or "none recorded"); diagnosis; ℞ and one row per medicine: name, strength, Schedule H1/X tag, generic and form, dose with English directions and the route unless oral, the Hindi line under it (`lang="hi"`), instructions, duration ("Once" for STAT, whole weeks from 14 days), quantity; advice; follow-up; refills allowed; the QR code with the prescription number; "Digitally signed by {certificate name} ({registration no.}) — certificate {serial}, {time}" and "SHA-256 {hash of the signed bytes}" while the doctor's DSC signature checks out (re-checked for every copy: Valid), with the registration number and the prescriber's name, qualification and council as the doctor signed them (from the signed bytes, not the practitioner master as it is today); else, and for an Invalid or Untrusted signature, "Signed electronically on …" (signing in HC360 is not a digital signature under the IT Act); the prescriber's signature image above the name | Letterhead with the facility logo and the consultant's block; "Signed …" (and "amended … (reason)"); patient strip with the admission and discharge dates; diagnosis with ICD-10 codes, presenting complaints, course in hospital, procedures, investigations, condition at discharge, medicines on discharge, advice, follow-up; the emergency line; the consultant's signature image above the name |
| Footer on every page | "Asha Rao · UHID NH-000123 · Prescription NHRX260000123", and "Page X of Y" | "Asha Rao · UHID NH-000123 · Discharge summary · Discharged 25 Sep 2026", and "Page X of Y" |
| Language (PP-06) | English with the Hindi after every label ("Medicine / दवा"), and each duration in Hindi under it, whoever reads it (ERX-04); medicine names, doses and codes as written | The headings and labels in the patient's language (English for staff; on the portal and in messages the patient's, see print-templates.md); the doctor's text as written |
| File name | `prescription-{number}.pdf` | `discharge-summary-{UHID}-{yyyy-MM-dd}.pdf`: the discharge date in IST, else the signing or admission date |
| Not final | Only signed prescriptions are printed. A draft or cancelled copy would say "DRAFT — not valid for dispensing" or "CANCELLED — do not dispense" at the top and in the footer | A draft says "DRAFT — not signed" at the top and in the footer, and has no signature image |

* The QR code opens `verificationUrl` (see *QR verification links*). Without `portal.baseUrl` the verification code is
  printed without a QR code, as on the lab reports.
* The logo and signatures come from the file store (`DocumentAssets`: PNG, JPEG, WebP or SVG up to 300 KB); a missing
  image is left out. The signature image is printed only once the document is signed.
* Both follow their print template (Hospital setup › Print templates: **Prescriptions**, **Clinical notes**). On
  pre-printed stationery only the doctor's block is printed at the top and the page leaves the blank space set for the
  stationery; the logo can be left off; a closing note ends the document. The consultation summary and the operation
  note, which the staff app prints, follow **Clinical notes** too. See `docs/modules/print-templates.md`.
* Every value is HTML-encoded, and the doctor's text keeps its line breaks.

#### Signing with the doctor's DSC (ERX-04)

Under the IT Act 2000 and the CCA's rules a prescription is digitally signed only with the doctor's own Digital
Signature Certificate (DSC). The doctor first signs the prescription in HC360 as before (safety checks, the pharmacy
queue); then, in HC360 Desktop, **Sign with DSC** on the prescription:

1. The staff app fetches `signing-content`: the prescription's canonical bytes (below), made by the server.
2. It calls the bridge method `dsc.signPrescription` (only from the staff app's own pages). HC360 Desktop reads the
   prescription out of the bytes itself, lists the current user's Windows "My" store and keeps the certificates that
   can sign now: a private key, valid today, key usage digitalSignature or nonRepudiation, not a CA's. A DSC on a USB token
   (ePass, Proxkey, SafeNet…) is there through its vendor's CSP/KSP, so no vendor code is involved. The doctor picks
   one in the Windows certificate dialog, which shows the prescription number, patient, doctor and medicines, and the
   token asks for its PIN.
3. HC360 Desktop returns a **detached CMS/PKCS#7 SignedData** (RFC 5652): SHA-256, the signing time as a signed
   attribute, the signer's certificate and the CA certificates above it that the computer has (the token's software
   installs them; never the root), PKCS#1 v1.5 for RSA keys
   (`System.Security.Cryptography.Pkcs`, MIT; `WindowsInterop/DigitalSignatures/DetachedCms.cs`, shared with the
   server).
4. The server rebuilds the bytes itself and checks the signature (`DetachedCms.Verify`): exactly one signer, SHA-256 or
   stronger, a signature that matches the bytes, a signing time within 15 minutes before and 5 minutes after the
   server's clock, a person's certificate (not a CA's) valid at that time whose key usage allows signing, and one that
   chains to a root in the DSC trust folder (below). It keeps the signature, the bytes, their SHA-256 and the
   certificate's name, subject, serial, issuer and thumbprint on the prescription (`clinical.prescriptions`:
   `digital_signature`, `signed_content`, `signed_content_hash`, `digitally_signed_at`, `signer_*`). A certificate whose
   details do not fit those columns is refused (`prescription.certificate_unsupported`), never cut short. The data
   audit records the two byte columns by their length and SHA-256, not their content, which names the patient.

**DSC trust folder.** Under the IT Act a DSC is issued by a Certifying Authority the CCA licensed, under a CCA India
root. A signature is accepted only from a certificate that chains, at the signing time, to a root certificate in the
server's DSC trust folder: `HealthCare360:Clinical:DscTrustDirectory` in the server's configuration (a relative path
from the data folder), by default `certs\dsc-trust` in the data folder (`DscTrustStore`, `DscTrustAnchors`). The chain
is built with custom root trust: never the operating system's roots, which hold every public web CA and, on a domain,
the hospital's enterprise CA (whose user certificates may sign), and never a root the signature carries. **IT installs**
the CCA India root certificates there (`.cer`, `.crt`, `.der` or `.pem`), downloaded from cca.gov.in with their
thumbprints checked against the ones the CCA publishes; if signatures arrive without their CA's certificate, the
licensed CAs' certificates can go there too (they are used to build chains, never trusted on their own). HC360 does
not ship the roots. The folder is read again when its files change. It is in neither the database nor the backups, so
a reinstall, a restore on new hardware or a failover to the standby needs the roots copied again (DEPLOYMENT.md,
sections 4, 8 and 9). A folder the server cannot read (its permissions, a disk error) trusts nothing, as an empty one
does, and is not an error: every check then reports the signatures as Untrusted, and where the DSC is required those
prescriptions are refused with 422 `prescription.digital_signature_untrusted`, never a 500. While DSC signing is on
(`clinical.prescriptions.signature` not off, at any facility) System health warns when the folder holds no root
(`prescriptions.dsc_trust_empty`, critical where the DSC is required), when it cannot be read
(`prescriptions.dsc_trust_unreadable` with the reason, critical where the DSC is required) or when it holds files that
are not certificates (`prescriptions.dsc_trust_unreadable`, a warning); Clinical adds these through Platform's
`IHealthWarningSource` (`DscTrustHealthWarnings`). While it holds no root, or cannot be read, DSC signing is refused
before the doctor enters a PIN (`prescription.signature_not_set_up`, saying which). The check is offline (no revocation lists, no
downloads; the hospital LAN has no route to the CAs), so a revoked DSC is not detected; the QR page says so. A
self-signed certificate (`New-SelfSignedCertificate`), a domain user certificate or any other certificate that may sign
is refused (`prescription.signature_untrusted`), whatever the setting.

**The doctor's own DSC.** The printed copy pairs the certificate's name with the prescriber's registration number, so
the certificate must be the prescribing doctor's: its common name, the name an Indian DSC is issued in (as on the
holder's PAN or Aadhaar), must contain every word of the doctor's name in HC360 (`DscSignerName`). Case, spacing,
punctuation, titles ("Dr", "Prof", "Shri", "Smt"…) and the order of the words do not count, and the certificate may
carry more words (a middle name): "MEERA RAJESH SHAH" signs for "Dr. Meera Shah". A colleague's DSC, or an
organisation's Class 3 certificate on a shared workstation ("NANA HOSPITALS PRIVATE LIMITED"), is refused with
`prescription.signer_mismatch`, which names both. An initial in HC360 ("Dr. M. Shah") does not match a full name on the
DSC: the administrator writes the doctor's name in full under Hospital setup › Doctors and clinical staff. Only the
prescribing doctor's own account may sign (403 `prescription.not_prescriber`); the staff app offers **Sign with DSC**
to them alone, and tells anyone else who must sign.

**Immutable once signed.** A signed prescription never changes (its medicines, advice and refills are fixed at
signing), so the signature stays valid; a second signature is refused (409). A changed prescription is a new one —
*Repeat* makes a draft to sign, and DSC-sign, again. Cancelling is still possible: the verification page then says
"cancelled". Every check (`GET …/digital-signature`, the public page) verifies the signature again over the stored
bytes, compares their SHA-256, and rebuilds the bytes from the stored prescription (with the patient and doctor as
signed), so a change made in the database outside HC360 shows as **Invalid** ("The prescription was changed after it
was signed. Do not dispense it."). Merging the patient's duplicate record is not such a change: the merge moves the
prescription to the surviving record, and the check follows the merged record (`patients.merged_into_patient_id`, through
later merges too) from the record the bytes name to the one the prescription is on now; a prescription moved to any
other patient is Invalid. A signature whose certificate no longer chains to a root in the trust folder (a root removed
since) is **Untrusted**: the prescription is as signed, but it is not presented as a DSC signature: the printed copy, the
PDF and the copy sent to the patient say "Signed electronically", as for an Invalid one.

**Canonical bytes** (`Domain/PrescriptionCanonicalForm.cs`, format `hc360.prescription.v1`): RFC 8785 (JCS) JSON
restricted to strings, integers, booleans and null — UTF-8 without BOM, no whitespace, members sorted by name, strings
escaped only where JSON requires (`\" \\ \b \f \n \r \t`, other control characters `\u00xx`) and otherwise as they are,
no Unicode normalisation; quantities as decimal strings without trailing zeros, times as UTC to the second, ids as
lower-case GUIDs. Content: `format`; `facility` {id, name}; `patient` {id, uhid, name}; `prescriber` {id, name,
qualification, registrationNumber, registrationCouncil}; `prescription` {id, number, encounterId, visitId, admissionId,
isInpatient, signedAt, refillsAllowed, refillOfPrescriptionId, notes, advice, verificationToken, items[{sequence,
drugName, genericName, strength, dosageForm, route, dose, frequency, customFrequency, durationDays, quantity,
instructions, isControlledDrug, controlledSchedule}]}. Why this and not the PDF: the PDF is a rendering of these
values that changes with the print template, logo and renderer, and it carries the signature line and QR code
itself, so it cannot be signed before it exists. The encounter's diagnoses and follow-up are left out because they
belong to the encounter and may change later; the patient (record id, UHID, name) and the doctor are kept as signed.

**Facility setting** `clinical.prescriptions.signature` (JSON string; per facility, else hospital-wide):

| Value | Effect |
|---|---|
| `"off"` (default; anything unrecognised) | Nothing changes: no DSC signing, no badge or section in the staff app |
| `"optional"` | Doctors may sign with their DSC; prescriptions without one print and are shared as before |
| `"required"` | A prescription is issued (printed, downloaded, shared) only with a **valid** DSC signature (`PrescriptionFinalCopies`). `/print`, `/document`, `/pdf` and the portal's PDF are refused with 422 `prescription.digital_signature_required` until the prescribing doctor signs it with their DSC, `prescription.digital_signature_untrusted` while its certificate no longer chains to a root in the trust folder (a DSC HC360 cannot confirm counts as none), and `prescription.digital_signature_invalid` when the signature does not check out (the record was changed after signing). The portal shows the prescription without its print, PDF or QR code and says, in English and Hindi, that the doctor has not digitally signed it yet; its 422 carries a few words for the patient, and the portal words each code in the patient's language. The "prescription shared" message (WhatsApp, and the SMS after a video consultation) is not sent at signing but when the DSC signature is attached, or, if the hospital stops requiring the DSC first, then (below). The pharmacy queue is not held back |

The patient is told once per prescription (`patient_notified_at`, raised as `PrescriptionSharedWithPatient`): at
signing, unless the DSC was required then, else when the DSC signature is attached — whatever the setting is by then. A
prescription the patient was told about at signing is not announced again when the doctor later adds a DSC. Where the DSC
was required at signing the message is held (`patient_notice_held_at`); if the hospital stops requiring the DSC (the
facility's setting or the hospital-wide one made `"optional"` or `"off"`, or the facility's own value removed) before
the doctor signs with it, the prescription can be issued without it, and the held message goes within about five
minutes: `PrescriptionNoticeReleaseJob` (where `PlatformOptions.RunsJobs` is set) has `PrescriptionNoticeRelease` look
through the held messages every five minutes and send those of facilities that no longer require the DSC, at most 200
a time. It sends each once (sending it, or finding the patient has no
mobile number, lets it go; a cancelled prescription's is dropped) and only for prescriptions held in the last 7 days: a
week on, the pharmacy has dispensed the prescription or the course is over, and the message would be no news (the
prescription is on the portal either way). A DSC added later still sends a message never sent. However it is sent
(at signing, with the DSC, by the job), the message goes as the prescription's facility: its templates, name and
WhatsApp account, not those of the job (which has no signed-in user) or of the facility the doctor has since switched to. The PDF
with that message is made when the message goes out, from the signature as it checks out then; if the DSC rules no
longer let the prescription be issued (the setting or the trust folder changed in between), the message goes without
its PDF, or, on a WhatsApp template with a document header, is dropped (the patient finds the prescription on the
portal).

**Refills where the DSC is required.** A refill (ERX-05) is a new prescription in the original prescriber's name, so it
needs that doctor's DSC before it can be printed or given to the patient; the pharmacy queue is not held back. The staff
app's refill dialog then reads "Issue refill" (not "… and print") and does not print: the prescriber is taken to the
refill to sign it with their DSC; a pharmacist or a colleague is told which doctor must sign it. After a doctor signs
a prescription in HC360, the confirmation offers **Sign with DSC…** instead of Print and PDF, which would be refused.

In the staff app the prescription shows a **DSC** badge ("DSC signed", "DSC signature invalid", "DSC issuer not
verified", "DSC signature required", "Not DSC signed") and a *Digital signature* section with the state, signer,
certificate serial, issuer, thumbprint, SHA-256 and chain, the .p7s and signed-data downloads, and **Sign with DSC**
for the prescribing doctor alone — in HC360 Desktop only (a browser cannot reach the token; it says so; HC360 Desktop
saves the .p7s like other HC360 documents). Anyone else who may sign prescriptions is told that only the prescriber can
sign it with their DSC. Where the DSC is required, Print and PDF are offered only once the prescription has a valid DSC
signature, and a signature that no longer checks out says the prescription is not printed or shared until it is
resolved. The QR page shows the signature as re-checked, whether the certificate chains to the CCA
India root (it says "the signature checks out" only then), the hash, that revocation is not checked, and a .p7s
download, never the patient or the medicines.

**Checking a signature with OpenSSL alone.** Download the .p7s (from the QR page or the chart) and the signed data
(`signed-content`, from the chart: it names the patient), then:

```
openssl cms -verify -binary -inform DER -in prescription-NHRX260000123.p7s \
  -content prescription-NHRX260000123.json -noverify -out /dev/null
# CMS Verification successful
sha256sum prescription-NHRX260000123.json   # the hash printed on the prescription and shown on the QR page
```

`-noverify` skips the certificate chain; to check it too, pass the CCA India root instead: `-CAfile cca-india-root.pem
-purpose any` (the .p7s carries the licensed CA's certificate when the doctor's computer had it; otherwise add that with
`-certfile`). `PrescriptionSignatureOpensslTests` runs exactly these commands
against a signature made by HC360's code wherever `openssl` is installed.

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

#### The banner's payer (UX-04)

The banner's payer line ("Payer: Insurance · Star Health", "Last payer: Self-pay") comes from the server:
`GET /api/v1/patients/{id}/payer` (Patients module, `visits.read`) → `{current: {visitId, isCurrentVisit, payerType,
payerId, payerName, policyNumber, validUntil} | null}`. The rule (`PatientPayerRule`) is the patient's *primary
payer*: the payer is recorded on each visit and admission, not on the patient, so it is the payer of the newest open
visit (checked in or in consultation); when none is open, of the newest visit that was not cancelled
(`isCurrentVisit: false`, shown as "Last payer"); and none when the patient has had no visit in the facility. Billing
(`IPayerDirectory`) adds, for a payer other than self-pay, the payer's name for staff who may look payers up, and for
staff who may read policies the masked number and last valid day of the patient's policy with that payer that is
active and in date today (the longest valid, as billing picks it). Both checks are the insurance endpoints' own, so the
Super Admin's wildcard (`*`) passes them. Screens that know the payer better (an admission's) show their own. `usePatientPayer` (`src/modules/patients/payer.ts`) only words it; it shares the patient's visits'
query key, so whatever refreshes the visits refreshes the payer.

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
| GET | `/mrd/icd-codes?search&chapter&billableOnly&release&limit` | emr.read or mrd.coding or mrd.catalogue.import | → ranked `IcdCodeDto[]` (code prefix incl. "e119" → E11.9, title words, shorthand keywords) |
| GET | `/mrd/icd-codes/online?search&release&limit` | emr.read or mrd.coding | → `OnlineIcdResult[]` via `IIcdLookup` (422 `icd.online_unavailable` / `icd.online_failed`) |
| GET | `/mrd/icd-classification` | emr.read or mrd.coding or mrd.catalogue.import | chapters with their blocks (the imported WHO file's, else the built-in list) |
| POST / PUT | `/mrd/icd-codes[/{id}]` | mrd.coding | `SaveIcdCodeRequest{code, title, release, isBillable, keywords, fromOnlineLookup, isActive}` (see "Catalogue import" for what an edit means to later imports) |
| GET | `/mrd/catalogue` | emr.read or mrd.coding or mrd.catalogue.import | → `CatalogueStatusDto`: per catalogue the active, codable, withdrawn and switched-off codes, codes by source, the last import; the 10 latest imports |
| POST | `/mrd/catalogue/imports` | mrd.catalogue.import | multipart `file` (at most 100 MB) and `dryRun` → `CatalogueImportResult` (200 for a dry run, 201 for an import; 400 `file` with the reason; 409 `catalogue.import_running`) |
| GET / POST / PUT | `/mrd/procedure-codes[/{id}]` | emr.read or mrd.coding / mrd.coding | `SaveProcedureCodeRequest` |
| GET | `/mrd/codings?status&from&to&page` | mrd.coding | coding worklist (pending first) |
| POST | `/mrd/codings` | mrd.coding | `CreateCodingRequest{patientId, encounterId?, admissionId?, visitId?, episodeDate?, notes}` |
| GET / PUT | `/mrd/codings/{id}` | mrd.coding | `SaveCodingRequest{entries[{kind, code, isPrimary}], notes, version}` (catalogue codes only; categories with subdivisions rejected) |
| POST | `/mrd/codings/{id}/verify` | mrd.coding | `VerifyCodingRequest{version}` (a different coder must verify); insurance claims then take the coding (MB-03) |
| GET | `/mrd/codings/{id}/suggestions` | mrd.coding | → `CodingSuggestion[]` |
| GET | `/mrd/reports/morbidity?from&to&groupBy=code|block|chapter&source=clinical|coded` | mrd.records | → `MorbidityReport` (episodes, patients, sex and age bands at the end of the period, most episodes first; ≤ 1 year). `coded`: the facility's codings a second coder verified, by the day the episode ended, every diagnosis line counted and procedures left out (`MorbidityIndexTests`) |
| GET / POST | `/mrd/records` | mrd.records | list (`status`, `overdue`, `retentionDue`, `assignedPractitionerId`, `search`) / `CreateMedicalRecordRequest` |
| GET | `/mrd/records/{id}` | mrd.records | → `MedicalRecordDto` with deficiencies |
| POST | `/mrd/records/{id}/deficiencies` | mrd.records | `AddDeficiencyRequest{type, description, assignedPractitionerId, dueDate}` |
| POST | `/mrd/records/deficiencies/{deficiencyId}/resolve` | mrd.records or emr.write | `ResolveDeficiencyRequest{note}` |
| POST | `/mrd/records/{id}/scan` | mrd.records | detects unsigned encounters/notes, missing diagnosis, missing/unsigned discharge summary, pending coding; auto-closes fixed ones |
| POST | `/mrd/records/{id}/complete`, `/archive` | mrd.records | `ArchiveMedicalRecordRequest{location}`; completion sets `retainUntil` (OP 3 y, IP 5 y, MLC permanent) |
| GET / POST | `/mrd/record-requests` | mrd.records | `CreateRecordRequestRequest{patientId, medicalRecordId?, requestedByName, requestingDepartment, purpose, details}`; status filter `Overdue` supported |
| POST | `/mrd/record-requests/{id}/issue` / `return` / `cancel` | mrd.records | `IssueRecordRequest{dueBackOn}` / — / `CancelRecordRequest{reason}` |

#### Coding for insurance claims

Once a second coder has verified a coding, Billing's insurance claims use it (MB-03), through the Platform contract
`IVerifiedCodingSource` that this module implements. A claim on an in-patient bill takes its admission's coding; a claim
on any other bill takes the out-patient coding of its visit or of an encounter in that visit. A new claim takes the verified codes
where the insurance desk typed none, ahead of the pre-authorisation's provisional codes, and the claim screen offers them
while the claim's codes differ. The claim gets each code with its catalogue title, or the codes alone when the titles do
not fit in the 1,000 characters each of the claim's two code fields holds. A coding that is coded but not yet verified
is never used, and re-coding a verified coding withdraws it until it is verified again.

#### Catalogue import

HC360 ships a starter list of common ICD-10 and ICD-10-PCS codes. A hospital can load the full catalogues from files it
supplies, in Medical records > ICD catalogue > Import or with the server command
`HyFib.HealthCare360.Server.exe catalogue import --file <file> [--dry-run]` on the Server Node.

* **Files accepted.** The WHO ICD-10 2019 ClaML file (`icd102019en.xml`, or the `icd102019en.xml.zip` WHO provides) and
  the CMS ICD-10-PCS order file (`icd10pcs_order_YYYY.txt`, or its zip). The file is recognised by its content. A zip
  must hold exactly one catalogue file; the `__MACOSX/` folder and `._` files a Mac adds are ignored. A file with fewer
  than 5,000 ICD-10 categories, or an order file with fewer than 40,000 valid codes, is refused as partial.
* **Licensing.** HC360 does not include the WHO file. The hospital obtains its own copy under its WHO licence and
  imports it; the import record keeps the file name, its SHA-256 and the title and version the file states, which serve
  as the attribution of the source. The CMS ICD-10-PCS order file is in the public domain.
* **Dry run.** `dryRun=true` (or `--dry-run`) reads the file and reports what would be added, updated, brought back and
  withdrawn, and changes nothing. Check the file this way first.
* **What an import changes.** A code in the file is added, or brought in line with it: title, chapter, block, whether it
  is codable, dagger or asterisk, inclusion terms. Search words are kept. Starter-list PCS codes keep their everyday
  titles. An ICD-10 import also replaces the chapters and blocks used by the browser and the morbidity index.
* **Withdrawals.** A code from the starter list or an earlier import that the file no longer has, and that is in use, is
  withdrawn: switched off with the date, never deleted. A later file that has it again brings it back. Diagnoses,
  codings and discharge summaries that already have a withdrawn code keep it and can be saved again; it cannot be added
  to a record that does not have it.
* **What is left alone.** Codes MRD added by hand or saved from the WHO online search; starter or imported codes MRD
  edited by hand (code, title, whether it is codable, PCS category), which become `Source = Manual`; and a withdrawn code
  MRD switched back on, which also becomes `Manual`. Switching a code off or changing its search words is not such an
  edit: the import keeps updating the code, and a code MRD switched off stays off.
* **Record.** Each import is saved in one transaction with a `CatalogueImport` row (file, SHA-256, counts, who, when,
  upload or server console) and the audit entry `mrd.catalogue.imported`. Only one import runs at a time (a PostgreSQL
  advisory lock). Every server node and the server command see a new import at once: the cached chapters and blocks are
  tagged with the latest import and read again when it changes.

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
| GET | `/portal/prescriptions/{id}` | `PortalPrescriptionDetail` (bilingual directions, doctor registration, QR `verificationToken` and `verificationUrl`, see *QR verification links*, `finalCopyAvailable`, `digitalSignatureState`). Where the hospital requires the doctor's DSC and the prescription has no valid one, `finalCopyAvailable` is false and the token and URL are null: the portal offers no print, PDF or QR code and says why (`digitalSignatureState` `NotSigned`: the doctor has not digitally signed it yet) |
| GET | `/portal/prescriptions/{id}/pdf` | `application/pdf` attachment `prescription-{number}.pdf`: the printed prescription. Signed only: 404 for a draft, 422 `prescription.not_printable` for a cancelled one, and where the hospital requires the doctor's DSC and it has no valid one, 422 `prescription.digital_signature_required` ("Your doctor has not digitally signed this prescription yet."), `…_untrusted` or `…_invalid` ("This prescription cannot be issued right now. Please contact the hospital."); the portal words each by its code in the patient's language; audit "Downloaded prescription {number} as a PDF (patient portal)"; 503 `pdf.unavailable` |
| GET | `/portal/health-records?patientId` | `PortalHealthRecord{allergies, problems, recentEncounters (no free-text notes), immunizations, dischargeSummaries}` |
| GET | `/portal/discharge-summaries/{id}` | `DischargeSummaryDto` (signed only) |
| GET | `/portal/discharge-summaries/{id}/pdf` | `application/pdf` attachment `discharge-summary-{UHID}-{yyyy-MM-dd}.pdf`: the printed discharge summary (signed or amended only, 404 for a draft); audit "Downloaded discharge summary as a PDF (patient portal)"; 503 `pdf.unavailable` |
| POST | `/portal/record-requests` | `PortalRecordRequest{patientId, purpose, details}` → 201 |

## The doctor's worklist (EMR-01)

*My worklist* (`/emr`) is the doctor's home screen. The filters (date, department, doctor) apply to every list, and
the side panel keeps results to review, unsigned encounters and notes, draft prescriptions and chart deficiencies. A
result to review opens the report itself, with an imaging study's images and the PACS viewer (see *Results on the
patient's record*), a note and *Acknowledge*. The main panel has three tabs, kept in the address (`?view=appointments`,
`?view=rounds`):

* **Queue**: today's tokens for the department, as before. F2 calls the next patient.
* **Appointments**: the day's bookings in slot order, with a count of those still to come, arrived, seen and absent.
  A booking not arrived 15 minutes after its slot is marked *Late*; an arrived one shows its token. *Start* or
  *Continue* opens the consultation from the visit exactly as the queue does (the token goes in service), and a video
  booking links to the video consultations. The department's view adds each booking's doctor.
* **Ward round** (needs `inpatient.admissions.read` as well): the doctor's in-patients from
  `GET /api/v1/inpatient/rounds`, in the bed board's walking order or *most urgent first* (critical vital signs or a
  critical result, then abnormal vital signs, results waiting and patients not yet seen today). Each row shows the bed
  and ward, the day of the stay and provisional diagnosis, the safety flags, MLC, a discharge in progress and
  *Cross-consult* for another consultant's patient; whether the doctor has seen the patient today (a recorded
  consultant visit) and when they last wrote a note; results to review; and the latest vital signs' worst flag, with
  the flags in a tooltip. *Record visit* records the consultant visit, which is charged to the in-patient bill (the
  same dialog as on the admission). A row opens the chart. The round always shows the patients in hospital now.

**Restricted (VIP) patients** follow the bed board in every list, unless the user may open restricted records
(`patients.restricted.read`). They are named "Restricted patient", with a *Restricted* badge, and their clinical text
is left out: the complaint, booking reason and note title, a result's name and a deficiency's description. The
round also leaves out the diagnosis and vital-sign details. The token, time, bed, UHID and safety flags stay, so the
patient is still called and seen. Opening the chart asks for break-the-glass access.

The page header sums it up: the people waiting, the in-patients still to see, and the results to review.

**Dictating the note.** On the encounter workspace, *Dictate with AI* turns the doctor's dictation into a SOAP draft
with ICD-10 suggestions, which the doctor checks and inserts into the note (AI module, see `docs/modules/ai.md`). The
signing checklist warns while a placeholder from the draft (`[NAME_1]`) remains in the note.

## Consents (EHR-06)

Consents are the Patients module's `PatientConsent` records (`/api/v1/patients/{id}/consents`): one consent history
per patient, which Engagement (campaigns, messages) and Interop (record sharing) read. Staff record and withdraw them
on the patient profile and in the chart's **Consents** tab.

* **Recording** (`patients.consents`): the consent, the decision (given or refused), who signed (the patient, or a
  named relative or guardian with the relation), the witness, the form's version and notes.
* **Withdrawing** (`patients.consents`, a reason of up to 300 characters): only a consent that was given and not
  withdrawn; a refusal has nothing to withdraw (422 `consent.not_granted`), and a withdrawal is final
  (422 `consent.revoked`). The consent stays in the list, marked withdrawn, with when, by whom
  (`RevokedByUserId`) and why. To agree again, a new consent is recorded.
* **Standing permissions and one-off consents** (`PatientConsent.IsStandingPermission`). Sharing records, SMS and
  WhatsApp updates, health offers, video consultations, research and photographs hold until withdrawn, so withdrawing
  one, or recording a refusal, ends every earlier grant of the type ("The patient refused when asked again."). General
  treatment, high-risk procedure, anaesthesia and transfusion consents belong to one stay or operation: each is
  withdrawn on its own.
* **What stops at once**: record sharing with other providers (Interop), health-offer campaigns, feedback requests
  and follow-up reminders (Engagement). For the others, HC360 records the withdrawal and the screen tells staff what to
  do (for a procedure consent: tell the treating doctor; the procedure must not go ahead without a new consent).
* **Access**: listing, recording and withdrawing follow the record's restricted-record rules (403
  `patient.restricted`), and every listing is in the audit trail ("Viewed consents"). Withdrawals from the portal
  (the patient), WhatsApp STOP replies (no user) and opt-out links go through the same `PatientConsent.Revoke`.

## Patient documents (EHR-04)

Scans, outside reports, referral letters and signed forms attached to the record are the Patients module's
`PatientDocument` records, each with its type, the date on the document and where it came from. The file itself is
uploaded first to the file store (`POST /api/v1/files`), which keeps it encrypted at rest. Staff attach them on the
patient profile's **Documents** tab, and open them there and on the chart's.

| Method | Path | Permission | Notes |
|---|---|---|---|
| GET | `/patients/{patientId}/documents` | patients.documents | → `PatientDocumentDto[]{id, fileId, type, title, documentDate, source, notes, createdAt, fileName, contentType, sizeBytes}`, newest first; audit "Listed documents" |
| POST | `/patients/{patientId}/documents` | patients.documents | `AttachDocumentRequest{fileId, type, title, documentDate?, source?, notes?}` → 201 `{id}`. `type`: IdProof, InsuranceCard, ExternalReport, ReferralLetter, Consent, Prescription, DischargeSummary, Imaging, Photo, Other. `title` (required) and `source` (such as the outside lab or hospital) up to 200 characters, `notes` up to 1,000, none with control characters; `documentDate` not after today in the hospital (IST). 400 on `fileId` for a file not uploaded; 422 `document.other_patient` for a file uploaded for another patient |
| DELETE | `/patients/documents/{documentId}` | patients.documents | → 204. The document leaves the list; the encrypted file is kept |

* **Access**: listing, attaching and removing follow the record's restricted-record rules (403 `patient.restricted`
  until break-the-glass, or with `patients.restricted.read`), and a patient who does not exist is a 404
  (`patient.not_found`). Each document attached or removed is in the audit trail as a data change
  (`PatientDocument.create`, `PatientDocument.delete`), with the patient, who and when.

## Identity checks (INT-BIO)

The front desk can check that a patient is the holder of the Aadhaar number they give, with their fingerprint: the
Patients module's `PatientIdentityCheck` records (`/api/v1/patients/{id}/identity-checks`), on the patient profile's
**Identity** tab. UIDAI answers through the hospital's AUA (`IAadhaarAuthenticator`; the protocol, the settings and the
simulator are in [integrations.md](integrations.md#aadhaar-authentication-int-bio-patient-identity)). It is off until
an administrator sets up `integrations.aadhaar`.

| Method | Path | Permission | Notes |
|---|---|---|---|
| GET | `/patients/identity-checks/setup` | `patients.identity.verify` | `AadhaarCaptureSetup {provider, problem, environment, fingerDataType, fingerCount, timeoutSeconds, training}`: what the workstation's reader is asked for, or why checks are not available |
| GET | `/patients/{id}/identity-checks` | `patients.read` | The last 50 checks, newest first (`IdentityCheckDto`). Restricted-record rules apply (403 `patient.restricted`), and every listing is in the audit trail ("Viewed identity checks") |
| POST | `/patients/{id}/identity-checks` | `patients.identity.verify` | `VerifyIdentityRequest {aadhaarNumber, pidData, consent}` → `IdentityCheckDto`. See below |

`patients.identity.verify` ("Check a patient's identity with their Aadhaar fingerprint (UIDAI)") is given to
FrontOffice and HospitalAdmin.

* **The request.** `aadhaarNumber` is the 12-digit Aadhaar number or 16-digit Virtual ID as the patient gives it
  (spaces and dashes allowed; an Aadhaar number must pass the Verhoeff check). `pidData` is the capture HC360 Desktop's
  fingerprint reader returned, encrypted for UIDAI (at most 2 MB). `consent` must be true: the patient agreed after
  being told what the check is for and that they may show another identity document instead (400 otherwise).
* **Refused before UIDAI is asked**: a merged record (422 `patient.merged`: check the surviving one); another Aadhaar
  number than the one on the record (422 `patient.aadhaar_mismatch`, naming its last 4 digits); a number linked to
  another patient (409 `patient.aadhaar_in_use`, naming the UHID). When UIDAI could not be asked (not set up, a capture
  that could not be used, a training capture sent to `uidai`), the answer is 422 `patient.identity_check_failed` with
  the reason, and nothing is kept.
* **Kept.** Every check UIDAI (or the simulator) answered: `outcome` (`Verified`, `NotVerified`, `Failed`), the
  message, the number's last 4 digits, `usedVirtualId`, the provider, UIDAI's error code, HC360's transaction id,
  UIDAI's response code, the reader's provider and model, who checked and when. Never the number or the capture. The
  check is audited (`patient.identity_checked`, a warning when the fingerprint did not match).
* **Linked.** A verified Aadhaar number is then linked to a record that had none (`linkedAadhaar`: last 4 digits and
  blind index, as at registration; audited as `patient.aadhaar_linked`). The database keeps one record per Aadhaar
  number (`ux_patients_aadhaar_index`). If another desk linked the number to another record while UIDAI answered, the
  check is still kept but the number is not linked, and the answer is 409 `patient.aadhaar_in_use`, naming that UHID.
  Registration and editing give the same answer when they lose such a race.
* **Merging** records moves their identity checks to the surviving record.

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
| `PrescriptionSharedWithPatient` (Platform) | an out-patient was told their prescription is ready (the "prescription shared" message): at signing, or, where the DSC was required then, once it was signed with the DSC; once per prescription (`patient_notified_at`). Virtual Care adds its SMS after a video consultation |
| `EncounterSigned`, `EncounterAmended` (Clinical.Domain) | encounter signed / amended (follow-up and referral data for CRM, doctor fees). Engagement starts the follow-up and chronic-review calls from a signed note, and keeps the follow-up in step with an amendment |
| `DischargeSummarySigned` (Clinical.Domain) | discharge summary first signed |
| `HealthCheckupCompleted` (Clinical.Domain) | checkup completed; Engagement schedules the next check-up's recall |
| `ImmunizationRecorded`, `ImmunizationEnteredInError` (Clinical.Domain) | dose recorded (with the recording facility and `nextDueOn`) / marked as entered in error; Engagement keeps the next dose's reminder |

Handled (same transaction):

| Event | Action |
|---|---|
| `PatientsMerged` (Patients) | re-points every clinical row (all facilities, including soft-deleted) to the surviving patient; keeps at most one "no known allergies" statement, and none when an active allergy exists. A DSC-signed prescription stays Valid: its signed bytes keep the duplicate's record, which the check follows to the survivor (*Signing with the doctor's DSC*) |
| `PatientAdmitted` | opens the MRD chart of the admission |
| `PatientDischarged` | sets chart discharge time and due date (+7 days), queues ICD coding (Pending), stamps the discharge summary |
| `DiagnosticReportReleased` | completes the order line, with the report's id as its `resultReference`, and adds/re-opens a "result to review" for the ordering doctor, which is the report's *Result* on the timeline: one per report, however many of its tests are released, together (one announcement per test in the same save) or one after another. A critical result the doctor has not acknowledged keeps the review critical, whatever is released with it or after it |
| `SurgeryCompleted` (SpecialtyCare) | `SurgeryCompletedHandler` records the operation as a `ProcedureRecord` (source `OperationTheatre`, performed at knife to skin, the surgeon as performer, the booking's side), once per booking (unique `surgery_booking_id`), in the latest encounter of the admission (or visit) that began before it ended. The booking's code is kept only when the procedure catalogue has it, since theatres often book with the hospital's tariff code |

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

Uses the platform template `prescription.shared` (WhatsApp) for OP prescriptions after signing (where the hospital
requires the doctor's DSC, once signed with it; once per prescription). No new templates.

Documents that messages can carry (`INotificationDocumentSource`, made when the message is sent):

| Kind | Document | Used by |
|---|---|---|
| `clinical.prescription` | The printed prescription's PDF, with the DSC signature as it checks out when the message goes out; none once the prescription is cancelled, nor while the DSC rules do not let it be issued (the message then goes without it, or is dropped on a template with a document header) | `prescription.shared`: WhatsApp sends it in the template's document header when the template is marked `whatsAppDocumentHeader` |
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
