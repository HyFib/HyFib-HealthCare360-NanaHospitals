# Diagnostics module (`diagnostics`)

Laboratory information system with quality control, radiology / medical imaging with PACS, and cardiology diagnostics.
SRS **LIS-01..08**, **IMG-01..05**, **CAR-01..04**. Work is created from clinicians' orders (`ServiceOrderPlaced`) or at
the lab counter, charged through Billing (`IChargePoster`), and every released report raises
`DiagnosticReportReleased` for the EHR, the patient portal and patient notifications.

Project: `src/server/Modules/HyFib.HealthCare360.Modules.Diagnostics` (references Patients, and Clinical for the health
check-up report). Schema `diagnostics`.
Tests: `tests/Modules/HyFib.HealthCare360.Modules.Diagnostics.Tests` (415 unit tests: range/flag evaluation, delta check,
auto-validation, Westgard rules, sample state machine, tube planning, payment gate, report release/amendment, imaging and
cardiology workflows, DICOM formatting, critical call register, seeded catalogue checks incl. LOINC check digits, EF model,
QR verification links, report id → study look-ups, and the printed documents: content, released-only rules, encoding of
hostile text, footers and file names).

## Permissions

| Permission | Purpose | Default roles |
|---|---|---|
| `lab.orders.read` | Lab orders, samples, results, reports, TAT, cumulative history | LabTechnician, Pathologist, Doctor, Nurse |
| `lab.orders.create` | Walk-in / outside-referral registration, cancel tests, retry charges | LabTechnician, FrontOffice |
| `lab.samples.collect` | Pending-collection list, collect, print labels | LabTechnician, Nurse |
| `lab.samples.receive` | Receive by barcode, reject, recollect, send out to reference labs | LabTechnician, Pathologist |
| `lab.results.enter` | Worklist, result entry, external reports | LabTechnician, Pathologist |
| `lab.results.verify` | Verify, release, amend, reopen; critical calls; QC lockout override | Pathologist |
| `lab.qc.manage` | QC lots, runs, reviews | LabTechnician, Pathologist |
| `lab.masters.manage` | Test master, ranges, analysers, reference labs | Pathologist, HospitalAdmin |
| `radiology.studies.read` | Imaging studies and released radiology reports | Doctor, Nurse, Radiographer, Radiologist |
| `radiology.worklist` | Schedule/perform studies, MWL, PACS reconciliation | Radiographer, Radiologist |
| `radiology.reports.write` | Draft and submit radiology reports | Radiologist |
| `radiology.reports.verify` | Sign off, release, addenda; report templates | Radiologist |
| `cardiology.studies.read` | Cardiology studies and released reports | Doctor, Nurse, CardiologyTechnician |
| `cardiology.studies.manage` | Perform ECG/Echo/TMT/Holter/ABPM, upload device output | CardiologyTechnician |
| `cardiology.reports.verify` | Report and sign off cardiology studies | Cardiologist (give cardiologists the Doctor role too; grant this to Doctor where physicians report ECGs) |
| `diagnostics.critical.alerts` | Receive in-app critical value / critical finding alerts | Doctor, Nurse |

`lab.orders.create`, `radiology.studies.read`, `cardiology.studies.read` and `diagnostics.critical.alerts` were added to
the specified set so read access, counter registration and critical alerts can be granted separately.

Report visibility: users without `lab.results.enter`/`lab.results.verify` (lab) or the department's own permissions
(radiology/cardiology) see **released** results only. Every patient-level read goes through `PatientAccessGuard`
(restricted/VIP records) and writes a PHI access audit entry.

## Endpoints

All lists take the standard `PageQuery` (`page`, `pageSize`, `search`, `sort`, `desc`) and return `PagedResult<T>`.
Worklists are ordered by urgency (STAT, urgent, routine) with an explicit rank, because enums are stored as text.

### Laboratory masters — `/api/v1/lab`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/tests?section&orderable&includeInactive` | orders.read, orders.create or masters.manage | → `PagedResult<LabTestListItem>` (code, name, section, specimen, container, resultType, unit, isPanel, isProvisional, price, preparation) |
| GET | `/tests/{id}` | same | → `LabTestDetail` (all master fields, `components`, `ranges`, version) |
| POST | `/tests` | `lab.masters.manage` | `SaveLabTestRequest` (code, name, section, specimenType, container, method, tatHours, loinc, serviceItemId, isOrderable, isPanel, resultType, unit, decimals, options, instrumentChannelCodes, qcGroup, deltaThresholdPercent, allowAutoValidation, preparation, drawSeparately, interpretation, defaultReferralLabId) → 201 `{ id }` |
| PUT | `/tests/{id}` | `lab.masters.manage` | `SaveLabTestRequest` (+`version`) → 204 |
| PUT | `/tests/{id}/components` | `lab.masters.manage` | `SetComponentsRequest` (`ComponentEntry[]` testId/groupName, version) → 204; cycles are refused |
| PUT | `/tests/{id}/ranges` | `lab.masters.manage` | `SetRangesRequest` (`ReferenceRangeDto[]`: gender, ageFromDays, ageToDays, low, high, criticalLow, criticalHigh, text, normalValues, criticalValues) → 204 |
| GET | `/instruments?includeInactive` | masters.manage or qc.manage | → `LabInstrumentDto[]` |
| POST · PUT | `/instruments` · `/instruments/{id}` | `lab.masters.manage` | `SaveLabInstrumentRequest` (code, name, section, protocol Hl7/Astm, isBidirectional, autoValidationEnabled) → 201 / 204 |
| GET | `/referral-labs?includeInactive` | masters.manage, samples.receive or orders.read | → `ReferralLabDto[]` |
| POST · PUT | `/referral-labs` · `/referral-labs/{id}` | `lab.masters.manage` | `SaveReferralLabRequest` (code, name, contact, accreditationNumber, tatDays) → 201 / 204 |

### Lab orders — `/api/v1/lab`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/orders?from&to&status&priority&patientClass&patientId` | `lab.orders.read` | → `PagedResult<LabOrderListItem>` (accession, UHID, patient, class, source, priority, status, tests, reportStatus); sort keys `orderedAt`, `accessionNumber`, `priority`, `status` |
| GET | `/orders/{id}` | `lab.orders.read` | → `LabOrderDetail` (items `LabOrderItemDto` incl. chargeId/chargeError, packageBookingId, quantity; samples `LabSampleDto`) |
| POST | `/orders` | `lab.orders.create` | `CreateLabOrderRequest` (patientId, visitId?, testIds, priority, source WalkIn/OutsideReferral, orderingPractitionerId?, referringDoctorName, clinicalNotes) → 201 `LabOrderCreated` (id, accessionNumber, samples with barcodes, chargeWarnings) |
| POST | `/orders/{id}/charges/retry` | `lab.orders.create` | → `ChargeRetryOutcome` (posted, failures) |
| POST | `/orders/items/{itemId}/cancel` | `lab.orders.create` | `CancelLabItemRequest` (reason) → 204; 422 `lab.item_completed` / `lab.item_partly_verified` once results are verified |
| GET | `/patients/{patientId}/history?labTestId` | `lab.orders.read` | → `PatientLabHistory[]` (cumulative verified results per analyte for trend charts) |
| GET | `/tat?from&to&section` | `lab.orders.read` | → `LabTatReport` (order→release and receipt→verification median/P90, % within target TAT, rejection rate by reason) |
| GET | `/critical-calls?from&to&kind` | lab.orders.read, radiology.studies.read or cardiology.studies.read | → `PagedResult<CriticalCallDto>` (critical call register) |

### Samples — `/api/v1/lab`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/samples/pending-collection?section&priority&patientClass` | samples.collect or orders.read | → `PagedResult<PendingCollectionItem>` (barcode, container, tests, patient preparation, isRecollection) |
| POST | `/orders/{id}/collect` | `lab.samples.collect` | `CollectSamplesRequest` (sampleIds? — all pending when empty, ≤ 50; collectedAt ≤ now + 5 min; collectionSite) → `SampleLabel[]` for barcode printing; 422 `lab.payment_pending` (OP/external, `data.tests` lists unpaid tests) |
| GET | `/orders/{id}/labels?sampleId` | samples.collect or samples.receive | → `SampleLabel[]` (reprint) |
| GET | `/samples/by-barcode/{barcode}` | samples.receive, results.enter or orders.read | → `SampleLookup` |
| POST | `/samples/receive` | `lab.samples.receive` | `ReceiveSamplesRequest` (barcodes) → `ReceiveOutcome[]` (per barcode; one bad label never blocks the rack) |
| POST | `/samples/{id}/reject` | `lab.samples.receive` | `RejectSampleRequest` (reason, remarks — required for Other, recollect) → `SampleRejected` (recollection barcode) |
| POST | `/samples/{id}/recollect` | `lab.samples.receive` | → `SampleRejected` (new linked tube) |

### Results and verification — `/api/v1/lab`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/worklist?section&status&priority` | results.enter or results.verify | → `PagedResult<WorklistItem>` (STAT first, then tubes with unverified critical values; pending/entered/verified/critical counts) |
| GET | `/samples/{id}/results` | results.enter or results.verify | → `ResultSheet` (`ResultSheetLine`: value, flag, isCritical, referenceText, previous value, delta %, autoValidated, susceptibilities, hasCriticalCall, version) |
| POST | `/results` | `lab.results.enter` | `EnterResultsRequest` (`ResultEntry[]`: resultId, value, remarks, organism, colonyCount, susceptibilities) → `ResultSheetLine[]` (flags, reference text and delta computed server-side) |
| POST | `/results/verify` | `lab.results.verify` | `VerifyResultsRequest` (resultIds, qcOverrideReason, criticalCall `CriticalCallDetails`, release) → `VerificationOutcome`; 422 `lab.qc_lockout` (data: locked assays) or `lab.critical_call_required` |
| POST | `/results/{id}/reopen` | `lab.results.verify` | `ReopenResultRequest` (reason) → 204 (unreleased results only; released ones are amended) |
| POST | `/results/{id}/critical-calls` | results.verify or results.enter | `CriticalCallDetails` (informedPersonName, role, informedPractitionerId, method, informedAt, readBackConfirmed = true, notes) → 201 |

### Reports — `/api/v1/lab/reports`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/?status&from&to` | `lab.orders.read` | → `PagedResult<LabReportListItem>` |
| GET | `/{id}` | `lab.orders.read` | → `LabReportView` (letterhead, patient, sections → tests → result lines, signatories, amendment history, auto-validation note, QR `verificationCode` + `verificationPath` + `verificationUrl`) |
| GET | `/{id}/document` | `lab.orders.read` | → `PrintableDocumentDto` (title, fileName, pageSize `A4`, landscape, html): the A4 report for the staff app to print. Same released-only rule and guard as `/{id}`; audit "Printed lab report …" |
| GET | `/{id}/pdf` | `lab.orders.read` | → the same report as `application/pdf` (`lab-report-<number>.pdf`); 503 `pdf.unavailable` when the server can't make PDFs. Audit "Downloaded lab report … as a PDF" |
| POST | `/{id}/release` | `lab.results.verify` | → `ReleaseOutcome` (releasedItemIds, isPartial, verificationCode) |
| POST | `/{id}/amend` | `lab.results.verify` | `AmendReportRequest` (reason, `AmendmentChange[]` resultId/value/remarks, criticalCall) → 204; revision + 1, old → new values recorded and re-announced |

### Quality control — `/api/v1/lab/qc`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/lots?includeInactive&labTestId` | qc.manage or results.verify | → `QcLotDto[]` |
| POST · PUT | `/lots` · `/lots/{id}` | `lab.qc.manage` | `SaveQcLotRequest` (labTestId, material, level, lotNumber, expiryDate, targetMean, targetSd, unit, instrumentCode) → 201 / 204; targets are frozen once runs exist |
| GET | `/lots/{id}/levey-jennings?from&to` | qc.manage or results.verify | → `LeveyJenningsChart` (mean, SD, ±1/2/3 SD limit lines, points with z-score, outcome, violations) |
| GET | `/runs?labTestId&lotId&outcome&from&to` | qc.manage or results.verify | → `PagedResult<QcRunDto>` |
| POST | `/runs` | `lab.qc.manage` | `RecordQcRunRequest` (lotId, value, runAt) → 201 `QcRunDto` (Westgard outcome and violations) |
| POST | `/runs/{id}/review` | `lab.qc.manage` | `ReviewQcRunRequest` (note, correctiveAction — required for a rejected run) → `QcRunDto` |
| GET | `/status` | qc.manage, results.verify or results.enter | → `QcStatusRow[]` (today's latest run per lot, locked, overridden) |
| POST | `/overrides` | `lab.results.verify` | `QcOverrideRequest` (labTestId, reason) → `{ overrides }` (audited) |

### Outsourcing — `/api/v1/lab/outsourcing`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/?pendingOnly&referralLabId` | orders.read or samples.receive | → `PagedResult<OutsourcedItem>` (dueAt from the reference lab TAT, isOverdue) |
| POST | `/items/{itemId}/send` | `lab.samples.receive` | `SendOutRequest` (referralLabId, dispatchReference) → 204 |
| POST | `/items/{itemId}/receive` | `lab.results.enter` | `ReceiveExternalReportRequest` (externalReference, reportFileId — uploaded via `/api/v1/files`, same patient) → 204 |

### Radiology studies — `/api/v1/radiology`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/studies?from&to&status&modality&patientId` | studies.read, worklist or reports.write | → `PagedResult<ImagingStudyListItem>` |
| GET | `/studies/{id}` | same | → `ImagingStudyDetail` (milestones, contrast, dose, StudyInstanceUID, PACS link, repeats, packageBookingId, quantity) |
| POST | `/studies/{id}/schedule` | `radiology.worklist` | `ScheduleStudyRequest` (scheduledAt, room, stationAeTitle) → `ImagingStudyDetail` |
| POST | `/studies/{id}/arrive` · `/start` | `radiology.worklist` | → `ImagingStudyDetail` |
| POST | `/studies/{id}/acquire` | `radiology.worklist` | `AcquireStudyRequest` (contrastUsed, contrastAgent — required with contrast, volume, doseNote, notes) → `ImagingStudyDetail` |
| POST | `/studies/{id}/repeats` | `radiology.worklist` | `RecordRepeatRequest` (reason, note) → `ImagingStudyDetail` |
| POST | `/studies/{id}/cancel` | `radiology.worklist` | `CancelStudyRequest` (reason) → `ImagingStudyDetail`; refused once reported |
| POST | `/studies/{id}/reconcile` | `radiology.worklist` | → `ImagingStudyDetail` (PACS lookup by accession; patient ID must match) |
| POST | `/pacs/reconcile` | `radiology.worklist` | → `PacsReconciliationSummary` (recent unlinked studies, batches of 50) |
| GET | `/studies/{id}/viewer` | studies.read, worklist or reports.write | → `ViewerLink` (422 `pacs.not_configured` when no PACS client is registered) |
| GET · POST | `/studies/{id}/ai-findings` | GET: reports.write/verify · POST: `radiology.worklist` | `SubmitAiFindingRequest` (engine, version, finding, confidence, isUrgent) → `AiFindingDto` |
| POST | `/ai-findings/{findingId}/review` | reports.write or reports.verify | `ReviewAiFindingRequest` (acknowledge, note) → `AiFindingDto` |
| GET | `/worklist/mwl?date&station&modality` | `radiology.worklist` | → `ModalityWorklistItem[]` (DICOM-formatted: PN names, `yyyyMMdd`/`HHmmss`, sex M/F/O, XR sent as CR, 2.25 UIDs) for the MWL gateway |
| GET | `/reporting-worklist?modality` | reports.write or reports.verify | → `PagedResult<ImagingStudyListItem>` (urgent AI findings, then STAT, then oldest acquisition) |
| GET | `/reports/{reportId}` | studies.read, worklist or reports.write (same as `/studies`) | → `ReportStudyRef` (studyId, reportId, status). Opens a report from the doctor's worklist (`WorklistResult.reportId`) on the study screen; 404 when the report or its study is not visible in the facility. Ids only, so no PHI audit (opening the study audits) |
| GET | `/tat?from&to&modality` | studies.read or reports.verify | → `RadiologyTatReport` (order→acquisition→report median/P90 per modality, repeat rate and reasons) |

### Radiology reports — `/api/v1/radiology`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/studies/{id}/report` | studies.read, reports.write or reports.verify | → `RadiologyReportDto` (findings, impression, critical flag, status, addenda) |
| GET | `/studies/{id}/report/print` | same | → `RadiologyReportView` (letterhead, signatories, QR `verificationCode` + `verificationPath` + `verificationUrl`) |
| GET | `/studies/{id}/report/document` | same | → `PrintableDocumentDto`: the A4 report for the staff app to print. Released report only for users without reports.write/verify; guard; audit "Printed radiology report …" |
| GET | `/studies/{id}/report/pdf` | same | → the same report as `application/pdf` (`radiology-report-<accession>.pdf`); 503 `pdf.unavailable`. Audit "Downloaded radiology report … as a PDF" |
| PUT | `/studies/{id}/report` | `radiology.reports.write` | `SaveRadiologyReportRequest` (templateId, technique, clinicalHistory, comparison, findings, impression, isCriticalFinding, criticalFindingNote, version) → `RadiologyReportDto` |
| POST | `/studies/{id}/report/submit` | `radiology.reports.write` | → `RadiologyReportDto` |
| POST | `/studies/{id}/report/verify` | `radiology.reports.verify` | `SignReportRequest` (criticalCall — required for a critical finding, release = true) → `RadiologyReportDto`; 422 `radiology.critical_call_required` |
| POST | `/studies/{id}/report/release` | `radiology.reports.verify` | → `RadiologyReportDto` |
| POST | `/studies/{id}/report/addenda` | `radiology.reports.verify` | `AddAddendumRequest` (text, reason, isCriticalFinding, criticalCall) → `RadiologyReportDto` |
| GET | `/templates?modality&bodyPart&includeInactive` | reports.write or reports.verify | → `ReportTemplateDto[]` (matching body part first) |
| POST · PUT | `/templates` · `/templates/{id}` | `radiology.reports.verify` | `SaveReportTemplateRequest` → 201 / 204 |
| GET | `/procedures?modality&includeInactive` | studies.read, worklist or reports.verify | → `ImagingProcedureDto[]` |
| POST · PUT | `/procedures` · `/procedures/{id}` | reports.verify or `masters.manage` | `SaveImagingProcedureRequest` (code, modality, bodyPart, serviceItemId, durationMinutes, requiresContrast, patientPreparation) → 201 / 204 |

### Cardiology — `/api/v1/cardiology`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/studies?from&to&status&studyType&patientId` | studies.read, studies.manage or reports.verify | → `PagedResult<CardiologyStudyListItem>` |
| GET | `/studies/{id}` | same | → `CardiologyStudyDetail` (device files, report, packageBookingId, quantity) |
| POST | `/studies/{id}/schedule` · `/start` | `cardiology.studies.manage` | `ScheduleCardiologyRequest` / `StartCardiologyRequest` (deviceName) → `CardiologyStudyDetail` |
| POST | `/studies/{id}/files` | `cardiology.studies.manage` | `AttachDeviceFilesRequest` (fileIds uploaded via `/api/v1/files`; must belong to this patient) → `CardiologyStudyDetail` |
| POST | `/studies/{id}/complete` | `cardiology.studies.manage` | `CompleteCardiologyRequest` (notes) → `CardiologyStudyDetail`; 422 `cardiology.no_tracing` without device output |
| POST | `/studies/{id}/cancel` | `cardiology.studies.manage` | `CancelCardiologyRequest` (reason) → `CardiologyStudyDetail` |
| PUT | `/studies/{id}/report` | studies.manage or reports.verify | `SaveCardiologyReportRequest` (ecg: rate/rhythm/PR/QRS/QT/QTc/axis/ST-T/interpretation; echo: LVEF/chambers/valves/RWMA/diastolic function/pericardium/conclusion; tmt: protocol/duration/METs/max HR/%target/BP response/result; holter; abpm; conclusion; recommendations; isCriticalFinding) → `CardiologyStudyDetail` |
| POST | `/studies/{id}/report/submit` | studies.manage or reports.verify | → `CardiologyStudyDetail` |
| POST | `/studies/{id}/report/verify` | `cardiology.reports.verify` | `SignCardiologyReportRequest` (criticalCall, release = true) → `CardiologyStudyDetail`; 422 `cardiology.critical_call_required` |
| POST | `/studies/{id}/report/release` | `cardiology.reports.verify` | → `CardiologyStudyDetail` |
| GET | `/studies/{id}/report/print` | studies.read, studies.manage or reports.verify | → `CardiologyReportView` (only the measurements of the study type; QR `verificationCode` + `verificationPath` + `verificationUrl`) |
| GET | `/studies/{id}/report/document` | same | → `PrintableDocumentDto`: the A4 report for the staff app to print. Released report only for users without studies.manage/reports.verify; guard; audit "Printed cardiology report …" |
| GET | `/studies/{id}/report/pdf` | same | → the same report as `application/pdf` (`cardiology-report-<accession>.pdf`); 503 `pdf.unavailable`. Audit "Downloaded cardiology report … as a PDF" |
| GET | `/reporting-worklist?studyType` | `cardiology.reports.verify` | → `PagedResult<CardiologyStudyListItem>` |
| GET | `/reports/{reportId}` | studies.read, studies.manage or reports.verify (same as `/studies`) | → `ReportStudyRef` (studyId, reportId, status). Opens a report from the doctor's worklist on the study screen; 404 when the report or its study is not visible in the facility. Ids only, so no PHI audit |

### Patient portal — `/api/v1/portal/reports` (permission `portal.access`, `PortalPatientScope`, PHI audit)
| Method | Path | Response |
|---|---|---|
| GET | `/?patientId` | `PortalReportItem[]` (lab, radiology and cardiology reports released to the patient or their linked family members) |
| GET | `/lab/{id}` · `/radiology/{id}` · `/cardiology/{id}` | `LabReportView` (released tests only) · `RadiologyReportView` · `CardiologyReportView` |
| GET | `/lab/{id}/pdf` · `/radiology/{id}/pdf` · `/cardiology/{id}/pdf` | The released report as `application/pdf`, the same document the hospital prints and sends; 404 until released; 503 `pdf.unavailable`. Audit "Downloaded … report … as a PDF (patient portal)" |

The lab id is the lab report's; the radiology and cardiology ids are the study's (as in `PortalReportItem.id`).

### Health check-up report — `/api/v1/health-checkups/bookings` (mapped by this module)
Bookings belong to Clinical, which serves the report's data at `GET /bookings/{id}/report`. The document is made here
because it includes the diagnostics results. Same permissions as Clinical's `/report`, `PatientAccessGuard` and a PHI
access audit entry; the results are read after the guard.

| Method | Path | Permission | Response |
|---|---|---|---|
| GET | `/{id}/report/document` | `healthcheck.manage` or `clinical.emr.read` | `PrintableDocumentDto`: the full check-up report for the staff app to print. Audit "Printed health check-up report …" with the booking number |
| GET | `/{id}/report/pdf` | same | The same report as `application/pdf` (`health-checkup-report-<booking no.>.pdf`); 503 `pdf.unavailable`. Audit "Downloaded health check-up report … as a PDF" |

### Public QR verification (anonymous, rate limit policy `public`)
`GET /api/v1/lab/reports/verify/{code}`, `/api/v1/radiology/reports/verify/{code}`, `/api/v1/cardiology/reports/verify/{code}`
→ `ReportAuthenticity` (valid, report number, facility, patient initials, released at, test names, revision). No result
values are revealed. Codes are 20 symbols from a 32-symbol alphabet without 0/O/1/I (100 bits); malformed codes are
refused before the database is queried.

**QR links on printed reports.** `LabReportView`, `RadiologyReportView` and `CardiologyReportView` carry
`verificationPath` (the server-relative API above, which resolves only on the hospital LAN; kept for compatibility) and
`verificationUrl`, the absolute page to print as the QR code: `{portal.baseUrl}/portal/verify/{lab|radiology|cardiology}/{code}`,
the patient portal's public verification page, which calls the API. `verificationUrl` is null until the report has a
code (release) or while `portal.baseUrl` is not configured. `portal.baseUrl` is the hospital's public https address
(for example `https://care.nanahospitals.in`); patient links are `{base}/portal/...`.

## Documents

The reports a patient receives are laid out on the server, one HTML template each (`Documents/`, ADR-012). The staff
app prints that HTML (`GET …/document`), and the server turns the same HTML into the PDF that is downloaded
(`GET …/pdf`) or sent with the "report ready" message. So the printed copy, the download and the copy sent to the
patient are the same. When the server can't make PDFs, the `/pdf` endpoints answer 503 with code `pdf.unavailable` and
printing still works.

The lab, radiology and cardiology reports share one A4 frame (`ReportDocumentFrame`): the letterhead with the facility
logo, the report's title, a red banner on a copy that is not final, the patient block, the report itself, notes, the
signatories with their qualification, registration number and signature image, the QR code that proves the report is
genuine (the code alone while `portal.baseUrl` is not set) and the end line. Every page has a footer with the patient,
UHID and report number, and "Page X of Y". Values are encoded, so text typed into a report can never become markup.

| Document | Made from | File name | What it prints |
|---|---|---|---|
| Lab report | `LabReportView` | `lab-report-<accession>` | Tests by section with value, flag, unit and reference interval; cultures with organism and antibiogram; interpretations, amendments; notes for partial, auto-verified and amended reports. Banner "Preliminary copy for the laboratory: not released" on an unreleased copy |
| Radiology report | `RadiologyReportView` | `radiology-report-<accession>` | Study, modality and body part; clinical history, technique, comparison, findings and impression (empty sections left out); the critical finding note; signed addenda. Note: the report is a professional opinion. Banner "Signed, not yet released" or "Draft: not signed by the radiologist" |
| Cardiology report | `CardiologyReportView` | `cardiology-report-<accession>` | Titled "ECG report", "Echo report", "TMT report", "Holter report" or "ABPM report" ("Cardiology report" for other tests). The measurements of the study type with units (unrecorded ones left out), conclusion and recommendations, the critical finding note. Device recordings are not printed; a note says how many are stored with the report. Banner "Signed, not yet released" or "Draft: not signed by the cardiologist" |
| Health check-up report | Clinical's `CheckupReport` and the booking's diagnostics results | `health-checkup-report-<booking no.>` | Patient, booking, package (and company), check-up date and completion; the doctor's summary and recommendations; the package's investigations with their status; the stations of the day with their notes. Then every released lab, radiology and cardiology result of the booking (work items with its `PackageBookingId`), printed exactly as in its own report under its report number and release time; then "Results pending" with the investigations not yet released; then the signatories of those results and the reviewing doctor (or a space for one), and the end line. Until the check-up is completed, a banner gives its status, for example "In progress — not final" |

Released-only rules: on the staff copies, users without the department's entry or verification permissions get
released content only, as on the report endpoints, and a value that is not verified is never printed on a released lab
copy. Portal downloads and the copies sent with messages are released reports only. The health check-up report prints
released results only, whoever asks: a test or report that is not released is listed under "Results pending" by name,
never with a value, and cancelled work is left out. Its results are read in the user's facility, like every
diagnostics screen.

## Workflow rules

* **Tube planning**: tests share a tube when container, specimen, bench (section) and destination (in-house or reference
  lab) match, and only while the tube is still Pending. Timed specimens (`drawSeparately`, e.g. LAB-PPBS) always get
  their own tube. Barcodes are numeric and unique across facilities.
* **Sample state machine**: Pending → Collected → Received → InProcess → Completed; Rejected from Collected/Received/
  InProcess (reason; remarks required for Other) with a linked recollection tube; Cancelled when every test on it is
  cancelled. A second scan at receipt returns 409 `sample.already_received`; receipt before collection is refused.
* **Payment gate** (`lab.requirePaymentBeforeCollection`, default true): OP and external patients must have paid each test
  (`IBillingStatusProvider.GetStatesAsync("diagnostics.lab", itemIds)` = Paid). IP, ER and day-care work is never held,
  and health-checkup package components are settled with the package.
* **Interpretation**: the reference interval is chosen by sex and age in days (sex-specific first, then the narrowest age
  band); an unknown age only matches all-age intervals. Flags are computed on the unrounded value (a potassium of 6.54
  prints "6.5" but is CriticalHigh against 6.5). Comparator values (`<0.005`, `>600`, `≤`/`≥`) and Indian digit grouping
  (`1,20,000`) are parsed. Answer-type results flag Abnormal/Critical from the interval's normal/critical answer lists.
* **Delta check**: % change against the patient's last verified result of the analyte within `lab.deltaCheckWindowDays`
  (default 30); threshold per test or `lab.deltaCheckPercent` (default 50). A change away from zero is always flagged.
* **Auto-validation** (analyser results): numeric, in range (Normal), test and analyser allow it, QC accepted today, delta
  passed and only benign analyser flags. Anything else waits for a pathologist, with the reasons listed.
* **Westgard QC**: z-scores against the lot targets; 1-2s warning; 1-3s, 2-2s, R-4s, 4-1s and 10x reject (all rules are
  always checked, limits are strict). Tests in the same QC group (FBS/PPBS share `GLU`) share QC. While today's latest run
  of any lot of the assay is rejected, verification is locked (`lab.qc_lockout`) unless a pathologist records an override
  reason (audited, bound to that run). A rejected run needs a corrective action at review.
* **Critical values**: verifying a critical result (or signing a critical imaging/cardiology finding) requires a
  documented call with read-back (`CriticalCallLog`). The ordering doctor gets the `lab.critical` /
  `diagnostics.critical_finding` SMS in the same transaction, plus in-app alerts to the doctor's user and to holders of
  `diagnostics.critical.alerts` after commit. Critical results are never auto-validated.
* **Release**: tests are released as soon as they are verified (a CBC is not held back by a culture); the report is
  "Released" and `isPartial` while other tests are pending. Each released test raises `DiagnosticReportReleased` with
  the clinical `OrderItemId`. The QR code is assigned at first release. Corrections after release are numbered
  amendments with the reason and old → new values, re-announced as "(amended)".
* **Charges**: posted when the item/study is created (`diagnostics.lab`, `diagnostics.radiology`,
  `diagnostics.cardiology`; SourceReference = item/study id; quantity from the order; departments LAB, RAD, CDX) and
  reversed on cancellation. A billing failure never blocks clinical work: the error is stored and can be retried.
  Health-checkup package components (`ServiceOrderPlaced.PackageBookingId`) are never charged per test/study.
* **Unmapped services**: a clinical lab order for a service without a test set-up creates a provisional free-text test and
  alerts `lab.masters.manage` holders, so the order is never lost; radiology infers modality and body part from the code.
* **PACS** (`IPacsClient`, optional): studies are matched by accession number, the PACS patient ID must match the UHID
  before linking, and images found in PACS complete a study left in progress. Without a PACS client the endpoints return
  `pacs.not_configured` and the workflow continues.

## Events

| Direction | Event | Notes |
|---|---|---|
| Handled | `ServiceOrderPlaced` (Category Laboratory / Radiology / Cardiology) | Creates a lab accession item (one accession per clinical order), an imaging study or a cardiology study inside the ordering transaction; idempotent on `OrderItemId` (unique index + change-tracker check). Package components and `Quantity` are honoured. |
| Handled | `ServiceOrderCancelled` | Cancels the matching test/study and reverses its charge; verified/reported work is not undone — the department is alerted to review it. |
| Raised | `DiagnosticReportReleased` | Per released lab test, radiology report or cardiology report (and on amendment/addendum), with `Kind`, `OrderItemId`, title and `HasCriticalFindings`. |

Realtime (`Topics.Lab(facilityId)`): `lab.order.created`, `lab.order.cancelled`, `lab.sample.collected`,
`lab.sample.received`, `lab.sample.rejected`, `lab.sample.recollection`, `lab.results.entered`, `lab.results.received`
(analyser), `lab.results.verified`, `lab.results.reopened`, `lab.critical`, `lab.report.released`, `lab.report.amended`,
`lab.qc.run`, `lab.qc.override`, `lab.outsourcing.sent`, `lab.outsourcing.received`, `radiology.study.changed`,
`radiology.worklist.changed`, `radiology.ai.finding`, `cardiology.study.changed`. Pushes and staff alerts raised inside
event handlers go through the outbox (`diagnostics.realtime`, `diagnostics.staff-alert`) so they are sent only after commit.

## Notification templates

| Key | Channels | Placeholders | Use |
|---|---|---|---|
| `lab.report_ready` | SMS, WhatsApp (platform defaults), Email (seeded here) | `name`, `reference`, `hospital` | Any diagnostic report released (lab, radiology, cardiology); skipped for deceased patients |
| `lab.critical` | SMS (platform default) | `patient`, `uhid`, `test`, `value` | Critical lab value to the ordering doctor |
| `diagnostics.critical_finding` | SMS (seeded here) | `patient`, `uhid`, `study`, `finding` | Critical imaging/cardiology finding to the ordering doctor |

**The report's PDF with "report ready".** The message names the released report as a notification document; the PDF is
made when the message is sent, so the patient gets the report as it is then, and it is never stored. The e-mail carries
it as an attachment, and the WhatsApp message carries it when its template has a document header. SMS never carries it.
Only released content is ever made into these PDFs.

| Document kind | Id | Made by |
|---|---|---|
| `diagnostics.lab-report` | Lab report | `LabReportDocumentSource` |
| `diagnostics.radiology-report` | Imaging study | `RadiologyReportDocumentSource` |
| `diagnostics.cardiology-report` | Cardiology study | `CardiologyReportDocumentSource` |

## Settings

| Key | Default | Meaning |
|---|---|---|
| `lab.requirePaymentBeforeCollection` | `true` | OP/external collection requires payment |
| `lab.deltaCheckPercent` | `50` | Facility delta threshold when the test has none |
| `lab.deltaCheckWindowDays` | `30` | How far back the previous result is looked for |
| `diagnostics.reports.sendPdf` | `true` | Send the report's PDF with the "report ready" e-mail, and with the WhatsApp message when its template has a document header. When `false`, patients are told the report is ready and open it on the portal. Use a WhatsApp template without a document header then: a template with one can't go without its PDF, so the WhatsApp message is skipped (the notification log says why) while the SMS and e-mail still go |

## Number series

| Key | Format | Reset | Scope |
|---|---|---|---|
| `lab.accession` | `{FAC}LB{YY}{SEQ:7}` (e.g. `NHLB260000123`) | Yearly | Facility |
| `lab.sample` | `{SEQ:9}` (barcode) | Never | Global (analysers look samples up by barcode only) |
| `radiology.accession` | `{FAC}RD{YY}{SEQ:7}` | Yearly | Facility |
| `cardiology.accession` | `{FAC}CD{YY}{SEQ:7}` | Yearly | Facility |

## Seed data

`DiagnosticsSeeder` (Order 200, idempotent — only missing codes are added, so laboratory edits are never overwritten)
creates ServiceItems (SAC 999316) and masters with 2026 INR tariffs:

* **Laboratory** (with analytes, age/sex reference intervals, critical limits, LOINC codes, analyser channels):
  LAB-CBC ₹350 (Hb, TLC, DLC N/L/M/E/B, RBC, PCV, MCV, MCH, MCHC, platelets), LAB-FBS ₹80, LAB-PPBS ₹80, LAB-HBA1C ₹550,
  LAB-LIPID ₹600 (TC, TG, HDL, LDL, VLDL), LAB-LFT ₹650, LAB-KFT ₹700 (urea, creatinine, uric acid, Na, K, Cl),
  LAB-TFT ₹600 (T3, T4, TSH), LAB-URINE-RE ₹150, LAB-ESR ₹120, LAB-CRP ₹500, LAB-DENGUE-NS1 ₹700, LAB-MALARIA ₹350,
  LAB-WIDAL ₹250, LAB-HBSAG ₹350, LAB-HIV ₹450, LAB-HCV ₹600, LAB-VITD ₹1,400, LAB-VITB12 ₹1,000, LAB-PSA ₹800,
  LAB-ELECTROLYTES ₹450, LAB-COAG ₹700 (PT, INR, APTT), LAB-BLOOD-GROUP ₹150, LAB-STOOL-RE ₹150, LAB-CULTURE-URINE ₹700.
* **Radiology** (procedures with modality, body part, duration, contrast, preparation): RAD-XR-CHEST ₹400,
  RAD-XR-SPINE-LS ₹700, RAD-USG-ABD ₹1,200, RAD-USG-PELVIS ₹1,000, RAD-CT-HEAD ₹3,000, RAD-CT-ABD ₹6,500 (contrast),
  RAD-MRI-BRAIN ₹7,500, RAD-MRI-SPINE ₹8,000, RAD-MAMMO ₹2,000.
* **Cardiology**: CARD-ECG ₹250, CARD-ECHO ₹2,000, CARD-TMT ₹2,000, CARD-HOLTER ₹3,000, CARD-ABPM ₹2,500.
* **Report templates**: TPL-DX-CHEST, TPL-DX-LSSPINE, TPL-US-ABDOMEN, TPL-US-PELVIS-F, TPL-CT-HEAD, TPL-CT-ABDOMEN,
  TPL-MR-BRAIN, TPL-MR-LSSPINE, TPL-MG-BIRADS (one per seeded procedure).
* **Notification templates**: `lab.report_ready` (Email), `diagnostics.critical_finding` (SMS).

`DiagnosticsDemoSeeder` (Order 210, only with `SeedDemoData`): analysers XN550 (Sysmex XN-550), EM360 (Erba EM 360),
CL900I (Mindray CL-900i), reference lab MRL, and two-level QC lots for glucose (FBS/PPBS), creatinine, potassium and
haemoglobin with 21 days of Westgard-evaluated runs for Levey-Jennings charts.

## Analyser interface

`IInstrumentResultSink` is implemented by `InstrumentResultSink` (registered in `AddServices`). `ReceiveAsync` maps the
instrument code + channel (`XN550:HGB` beats a generic `HGB`) to the analyte, finds the tube by barcode (a collected
tube is received automatically), records unverified results, auto-validates when allowed and raises critical alerts.
`GetOrderForSampleAsync` answers bidirectional host queries with the channels still pending for the barcode.
