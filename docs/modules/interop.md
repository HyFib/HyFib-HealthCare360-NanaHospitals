# Interop module (`interop`) — HL7 FHIR R4 read API and ABDM health records

Read-only HL7 FHIR R4 API that lets other providers read a patient's record, **subject to the patient's consent**
(SRS **EHR-05**): Patient, Encounter, Observation, Condition, AllergyIntolerance, MedicationRequest, DiagnosticReport
and DocumentReference under `/fhir/r4`. It is built with the Firely .NET SDK (`Hl7.Fhir.R4` 6.6.0), returns JSON only
and writes nothing.

The module also turns a signed discharge summary into an **ABDM health record** that medical records staff download
(SRS **EHR-08**, in part; [ABDM health records](#abdm-health-records-ehr-08)): a FHIR document built to the NRCeS
profiles, behind the `abdm` feature switch, under the same consent rules.

| | |
|---|---|
| Project | `src/server/Modules/HyFib.HealthCare360.Modules.Interop` (layer 8; references Patients, Clinical, Diagnostics and Inpatient) |
| Schema | none. The module owns no tables and reads the other modules' entities through `HospitalDbContext` (no migration) |
| Requirements | EHR-05 (with EHR-06 consent and EHR-07 restricted records); EHR-08 in part (ABDM document bundles of discharge summaries; ABHA linking and the ABDM gateway are not built); PRV-04 (exports are audited); ABDM-friendly identifiers (ABHA number, HFR id) |
| Tests | `tests/Modules/HyFib.HealthCare360.Modules.Interop.Tests`: every mapper, the vital-sign codes and BP components, lab values and ranges, missing data, the consent and sharing rules, the SQL patient search, search parsing (`_count` cap, dates, tokens, strict parameters), in-memory search and paging links, OperationOutcomes, the CapabilityStatement, FHIR JSON round trips, the FHIR 401/403 handler and the SQL translation of every query (Npgsql, no database). ABDM (`Abdm/`): the discharge summary mapping, the document builder, and validation of the bundles against the NRCeS profiles ([Validation](#validation)). `AbdmExportTests` (IntegrationTests): the export end to end |

## Permission and set-up

| Permission | Purpose | Default roles |
|---|---|---|
| `fhir.read` | Read and search patient records through the FHIR API (patients with record-sharing consent only; every access is audited) | HospitalAdmin |
| `abdm.export` | Download signed discharge summaries as ABDM health records (FHIR document bundles; patients with record-sharing consent only; every export is audited) | HospitalAdmin, MrdOfficer |

Integration partners (a referral hospital, a health information exchange) get their own service user:

1. Create a role such as "FHIR integration" with `fhir.read`. Add `patients.restricted.read` only if restricted (VIP)
   records may also be shared.
2. Create a user for the partner, give it only that role, and sign it in with `POST /api/v1/auth/login` (and
   `/api/v1/auth/refresh`). Access tokens live 10 minutes; send them as `Authorization: Bearer <token>`.
3. The user's facility decides which visits, encounters, orders, results and reports it sees (platform facility filter).

## Consent and access rules

Every read and search passes the same gate for the patient the data belongs to:

| Rule | Read (`GET {type}/{id}`) | Search |
|---|---|---|
| Patient does not exist | 404 | patient left out |
| Patient record deleted | 410 `deleted` | patient left out |
| Patient merged into another UHID | 410 `patient-merged` (names the surviving `Patient/{id}` when the caller may read it) | patient left out |
| No active record-sharing consent | **403 `consent-required`** | **patient left out** |
| Restricted (VIP) record the caller may not open | 403 `restricted-record` | patient left out |

* **Consent** is the Patients module's `PatientConsent` of type **`DataSharing`** (captured under EHR-06; the existing
  type, so no new consent type was added). A patient's record is shared while they hold a granted, unrevoked
  `DataSharing` consent that no later recorded refusal supersedes, the same rule Engagement applies to marketing
  consent. Withdrawing the consent (revoking it, or recording a refusal) stops sharing at once.
* **Restricted records** use the platform guard `PatientAccessGuard` (EHR-07): reads and patient-scoped searches need
  `patients.restricted.read` or an active break-the-glass grant. Patient searches include restricted patients only
  for callers with `patients.restricted.read` (a break-the-glass grant opens one record, not a search).
* Consent comes first: a record without consent is never opened, not even by break-the-glass.
* A patient-scoped search whose patient is left out returns an empty `searchset` with one `outcome` entry
  (information, `suppressed`, `records-withheld`). Its wording is the same whatever the reason, so it reveals nothing
  about the patient.
* Only complete records are shared: released lab results and reports, released radiology and cardiology reports,
  signed prescriptions (and signed ones cancelled later), signed or amended discharge summaries. Drafts, unverified
  and unreleased work never leave the hospital. Identity proofs and insurance cards are never shared.
* Deactivated (not merged) patients stay readable by id (`active: false`) but are not returned by Patient searches.
* ABDM health records pass the same gate, with answers worded for staff
  ([ABDM health records](#abdm-health-records-ehr-08)).

## Audit and logging

| Event | Category / action | Recorded |
|---|---|---|
| Read | `phi` / `patient.accessed` | patient id, `fhir.{Type}`, resource id, `{ api, interaction: read, resourceType }` |
| Search in a patient's record | `phi` / `patient.accessed` | one entry: patient id, `fhir.{Type}`, returned and total counts, parameter **names** |
| Patient search | `phi` / `patient.accessed` | one entry per returned patient, written in one transaction |
| Patient search that returned nobody | `integration` / `fhir.search` | counts and parameter names |
| Read or search refused for consent or restriction | `security` / `fhir.access_denied` (Warning) | patient id, type, id, reason |
| ABDM health record downloaded | `phi` / `abdm.record.exported` | patient id, `DischargeSummary` and its id, `{ format: fhir-r4-document, profile, document, resources }` (`document` is the bundle's identifier, `resources` its entry count) |
| ABDM download refused for consent or restriction | `security` / `abdm.export_refused` (Warning) | patient id, `DischargeSummary` and its id, reason (`consent-required`, `restricted-record`) |

Downloads appear in the patient's record access history like any other PHI access. No part of the document's content is
written to the audit trail.

Parameter values (identifiers, names, dates of birth) are never stored in the audit trail. Application logs contain no
identifiers: the only messages are an unexpected error (route pattern and resource type) and an ABHA number that could
not be decrypted (no value).

## Endpoints (`/fhir/r4`)

| Method | Path | Access | Response |
|---|---|---|---|
| GET | `/metadata` | anonymous, rate limit `public` | `CapabilityStatement` (FHIR 4.0.1, JSON, read + search-type for the eight types, every search parameter) |
| GET | `/{type}/{id}` | `fhir.read` | the resource, or an `OperationOutcome` |
| GET | `/{type}?…` | `fhir.read` | `searchset` `Bundle` |
| POST | `/{type}/_search` | `fhir.read` | same as GET; parameters as `application/x-www-form-urlencoded` (keeps identifiers out of the request line) |
| any | anything else under `/fhir` | anonymous, rate limit `public` | `OperationOutcome` 404 (unknown interaction) or 405 (the API is read-only) |

* Content type `application/fhir+json; charset=utf-8`; `_pretty=true` indents. `_format` and `Accept` must allow JSON
  (406 otherwise). Responses carry `Cache-Control: no-store` (the CapabilityStatement `no-cache`).
* Paging: `_count` (default 20, capped at **100** — the `self` link shows the size used; `0` returns only `total`) and
  `_offset`. Bundles have `total`, `self`, `previous` and `next` links (which repeat the search parameters), and one
  `match` entry per resource with its `fullUrl`.
* Every type except Patient is searched **one patient at a time** (`patient`/`subject` = `Patient/{id}`, `{id}` or an
  absolute URL). Results are newest first by the resource's main date, then by id.
* Parameters are checked strictly: an unknown parameter, a modifier (`:exact`, `:text`…), a malformed value or a second
  patient is a 400 `OperationOutcome` instead of being ignored, so a mistyped filter never widens the result.
* Dates take `eq` (default), `ne`, `gt`, `ge`, `lt` and `le` with FHIR range semantics at the value's precision
  (`2026`, `2026-09`, `2026-09-25`, `2026-09-25T10:15[:30][Z|±hh:mm]`). Values without a zone are IST. Repeat a
  parameter for a range (`date=ge2026-01-01&date=le2026-03-31`). Tokens take `code`, `system|code`, `|code` or
  `system|`, with comma-separated alternatives; codes compare case-insensitively.

### Errors

Every error is an `OperationOutcome` whose `issue.details.coding` (`urn:hc360:fhir-issue`) states the reason.

| Status | Issue type | Reason codes |
|---|---|---|
| 400 | invalid / not-supported | `invalid-parameter`, `unsupported-parameter`, `patient-required` |
| 401 | login (with the scheme's `WWW-Authenticate`) | `authentication-required` |
| 403 | forbidden | `permission-required`, `consent-required`, `restricted-record` |
| 404 | not-found / not-supported | `not-found`, `unknown-resource-type` |
| 405 | not-supported | `read-only` |
| 406 | not-supported | `not-acceptable` |
| 410 | deleted | `deleted`, `patient-merged` |
| 415 | not-supported | `invalid-parameter` (POST `_search` without a form body) |
| 500 | exception | `server-error` (quotes the correlation id) |

The 401/403 bodies come from `FhirAuthorizationResultHandler`, which decorates the platform's authorization result
handler for `/fhir` paths only; every other route behaves as before. 429 (rate limit) has no body.

### Search parameters

All types also take `_id` and `_lastUpdated` (meta.lastUpdated).

| Type | Parameters |
|---|---|
| Patient | `identifier` (UHID `urn:hc360:uhid`, ABHA number `https://healthid.ndhm.gov.in`; a bare value tries both), `name` (every word must appear; trigram ILIKE), `birthdate` (date only), `gender`. At least one of `_id`, `identifier`, `name`, `birthdate`, `_lastUpdated` is required |
| Encounter | `patient`/`subject`, `date` (period), `status`, `class` |
| Observation | `patient`/`subject`, `category` (`vital-signs`, `laboratory`, `survey`, `exam`), `code` (LOINC), `date` (effective), `status` (`entered-in-error` only when asked for) |
| Condition | `patient`/`subject`, `clinical-status`, `verification-status`, `category`, `code` (ICD-10), `onset-date`, `recorded-date` |
| AllergyIntolerance | `patient`, `clinical-status`, `verification-status` (`entered-in-error` only when asked for), `category`, `date` |
| MedicationRequest | `patient`/`subject`, `status`, `intent`, `category`, `authoredon` |
| DiagnosticReport | `patient`/`subject`, `category` (`LAB`, `RAD`, `EC`, `CUS`, `OTH`), `code`, `date`, `status` |
| DocumentReference | `patient`/`subject`, `type` (LOINC), `date`, `status` |

## Resource ids

| Resource | Id | Source |
|---|---|---|
| Patient | `{patientId}` | Patients `Patient` |
| Encounter | `visit-{visitId}`, `enc-{encounterId}` | Patients `Visit` (+ Inpatient `Admission`), Clinical `Encounter` |
| Observation | `vs-{vitalsId}-{bp,hr,spo2,temp,rr,wt,ht,bmi,pain,gcs,glucose}`, `lab-{resultId}` | Clinical `VitalSigns`, Diagnostics `LabResult` |
| Condition | `{diagnosisId}` | Clinical `Diagnosis` |
| AllergyIntolerance | `{allergyId}` | Clinical `Allergy` |
| MedicationRequest | `{prescriptionItemId}` | Clinical `PrescriptionItem` + `Prescription` |
| DiagnosticReport | `lab-{labReportId}`, `rad-{radiologyReportId}`, `card-{cardiologyReportId}` | Diagnostics reports |
| DocumentReference | `doc-{documentId}`, `ds-{dischargeSummaryId}` | Patients `PatientDocument` (+ stored file metadata), Clinical `DischargeSummary` |

`meta.lastUpdated` is the source rows' audit timestamp (`UpdatedAt`, else `CreatedAt`; the latest of the rows a
resource is built from). Times are written in IST (+05:30), clinical times to the second.

## Mapping

### Patient
| FHIR | HC360 |
|---|---|
| `identifier` | UHID (`urn:hc360:uhid`, type MR, use usual); ABHA number when on file (`https://healthid.ndhm.gov.in`, `NN-NNNN-NNNN-NNNN`, type NI, use official; decrypted only for the response). Aadhaar is never shared |
| `active` | active and not merged |
| `name` | official: text = full name, family = last name, given = first + middle, prefix = title |
| `telecom` | mobile (full E.164 number, rank 1), alternate phone, e-mail |
| `gender` | Male/Female/Other/Unknown → male/female/other/unknown |
| `birthDate` | date of birth; an **estimated** date of birth is published with the year only |
| `deceased[x]` | `deceasedDateTime` when the time is known, else `deceasedBoolean` |
| `address` | home address: lines, city, district, state, PIN (`postalCode`), country |
| `maritalStatus` | v3 MaritalStatus S, M, D, W, L; Unknown → NullFlavor UNK |
| `contact` | emergency contact (v2-0131 `C`, relation as text, phone); guardian with the v3 RoleCode of the "S/O, D/O, W/O…" relation (PRN, HUSB, WIFE, CHILD, GUARD) |
| `communication` | preferred language as BCP-47 (en, hi, ta, te, kn, ml, mr, bn, gu, pa, or, ur, as), preferred = true |
| `managingOrganization` | registering facility (display name, HFR id `https://facility.ndhm.gov.in`) |

### Encounter
* **Visit** (`visit-`): class (v3 ActCode) OutPatient/HealthCheckup AMB, InPatient IMP, Emergency EMER, DayCare SS,
  Telehealth VR; status Open → arrived, InProgress → in-progress, Completed → finished, Cancelled → cancelled; period
  = visit time to completion; visit number (`urn:hc360:visit-number`, type VN); type text; department as `serviceType`;
  doctor as attender (ATND); chief complaint as `reasonCode`; facility as `serviceProvider`.
* An **in-patient visit** carries its **admission**: period = admitted → discharged; status Admitted and
  DischargeInitiated → in-progress, Cancelled → cancelled, every discharge outcome → finished; admission number
  (`urn:hc360:admission-number`); admitting (ADM) and treating (ATND) doctors; `hospitalization.admitSource`
  (OutPatient outp, Emergency emd, Referral mp, Direct other, TransferIn hosp-trans) and `dischargeDisposition`
  (Normal home, Referred other-hcf, LAMA/DAMA/Absconded aadvice with the text, Expired exp).
* **Clinical encounter** (`enc-`): `partOf` the visit (or the admission's visit), class from its type, in-progress
  until signed (then finished, period end = signing), the doctor as primary performer (PPRF), chief complaint.

### Observation — vital signs (category `vital-signs`, FHIR vital-signs profiles in `meta.profile`)
| Measurement | LOINC | UCUM |
|---|---|---|
| Blood pressure panel | 85354-9, components systolic 8480-6 and diastolic 8462-4 (a missing one has a data-absent reason) | `mm[Hg]` |
| Heart rate | 8867-4 | `/min` |
| SpO₂ | 59408-5 and 2708-6 (a note says when on supplemental oxygen) | `%` |
| Body temperature | 8310-5 (kept in the unit entered) | `Cel` or `[degF]` |
| Respiratory rate | 9279-1 | `/min` |
| Body weight | 29463-7 | `kg` |
| Body height | 8302-2 | `cm` |
| BMI | 39156-5 (`derivedFrom` weight and height) | `kg/m2` |
| Pain score (category survey) | 72514-3 | `{score}` |
| Glasgow coma score (category exam) | 9269-2 | `{score}` |
| Bedside glucose (category laboratory) | 41653-7, timing in the text | `mg/dL` |

Only recorded measurements become observations. A set marked "entered in error" keeps its values with status
`entered-in-error` (read by id; searches leave it out unless asked for). Stored decimals lose trailing zeros.

### Observation — laboratory (category `laboratory`)
Released, verified results only. `code`: the analyte's LOINC code when the test master has one, plus the HC360 test code
(`urn:hc360:lab-test`), text = test name. Numeric results are quantities parsed from the printed value (comparators
`<`, `<=`, `>=`, `>` and Indian digit grouping kept), UCUM when the printed unit is known (for example `10³/µL` →
`10*3/uL`, `mm/hr` → `mm/h`, `µIU/mL` → `u[IU]/mL`) and otherwise the unit as text without a system. Text, option and
culture results are strings; organism and colony count are notes. Flag → v3 interpretation (N, L, H, LL, HH, A, AA).
The printed reference interval is the `referenceRange` text, with low/high for "a - b", "Up to b", "a and above",
"< b" and "> a". `effective` = sample collection (else order) time, `issued` = release time, encounter = the visit.
A result re-verified after release (a numbered amendment) is `amended`.

### Condition
ICD-10 (`http://hl7.org/fhir/sid/icd-10`, code upper-cased, title as display) when chosen from the catalogue; free-text
diagnoses are text only. Clinical status: Active and Chronic → active (Chronic in the text), Resolved → resolved (with
`abatementDateTime`). Verification: Provisional → provisional, Final → confirmed, Differential → differential.
Category: `encounter-diagnosis` when made in an encounter; `problem-list-item` for active and chronic non-differential
diagnoses (and diagnoses recorded outside an encounter). Onset date, recorded time, encounter and notes.

### AllergyIntolerance
Allergen as text; category Drug → medication, Food → food, Environmental → environment; criticality high for Severe
and Anaphylaxis, low otherwise; reaction with the recorded manifestation and severity (Anaphylaxis → severe with
SNOMED CT 39579001). Clinical status active / inactive / resolved. Entered in error → verification status
`entered-in-error` without a clinical status (FHIR rule). Notes and the status reason as notes.

### MedicationRequest
One per medicine line of a signed prescription; `groupIdentifier` = prescription number. Status: signed → active
until the last day of the course (signing day = day 1, IST) and completed after; a signed prescription cancelled later
→ cancelled with the reason. Intent order; category inpatient/outpatient; medication text = brand, strength and form
(unless in the name) and the generic. Dosage: text ("1 tab, twice a day, for 5 days. After food"), timing code (OD QD,
BD BID, TDS TID, QID QID, HS BED with `when` HS, weekly WK) with frequency/period and a `boundsDuration`, STAT as one
dose, SOS as `asNeededBoolean`, route as text. Dispense: quantity, refills (`numberOfRepeatsAllowed`), expected supply
duration. Requester = prescribing doctor; encounter = the clinical encounter, else the visit, else the admission's visit.

### DiagnosticReport
* Laboratory (`lab-`): one per accession; category v2-0074 LAB; code LOINC 11502-2 with the released tests' names as
  text; `result` → the released lab observations; status final, `partial` while tests are pending, `amended` after a
  correction; accession number (type ACSN); effective = first sample collection; issued = release.
* Radiology (`rad-`): category RAD; code LOINC 18748-4 and the service code (`urn:hc360:service`); `conclusion` = the
  impression; `presentedForm` = the whole report as UTF-8 text (technique, history, comparison, findings, impression,
  addenda); `appended` once addenda exist.
* Cardiology (`card-`): category EC (ECG, Holter, TMT), CUS (echo) or OTH; ECG also coded LOINC 11524-6;
  `conclusion` and the study type's measurements as text (ECG intervals, LVEF, TMT, Holter, ABPM).

### DocumentReference
* Patient documents (`doc-`): type in LOINC where one fits (discharge summary 18842-5, referral note 57133-1, consent
  59284-0, prescription 57833-6, imaging 18748-4, clinical photograph 72170-4; others as text); description = title;
  author = the stated source; attachment metadata only (content type, size, title, document date). The encrypted file
  is not served by this API.
* Discharge summaries (`ds-`): LOINC 18842-5; docStatus final or amended; author and authenticator = the signing
  doctor; context = the admission's encounter and stay period; the whole summary embedded as UTF-8 text (diagnoses and
  ICD-10 codes, complaints, course, procedures, investigations, condition at discharge, medications, advice, follow-up).

## ABDM health records (EHR-08)

Medical records staff download a signed discharge summary as an ABDM health record: a FHIR document (`Bundle` of type
`document`) built to the **NRCeS "FHIR Implementation Guide for ABDM"**, package `ndhm.in` **6.5.0** (canonical
`https://nrces.in/ndhm/fhir/r4`), profiles **DocumentBundle** and **DischargeSummaryRecord**. HC360 does not send it
anywhere: the file is for the hospital to hand over or upload itself. Sending records through ABDM needs the
registrations listed under [Limitations](#limitations-and-follow-ups).

* **Switch.** `features.abdm` ("ABDM health records", [features.md](features.md)) is **off** until an administrator
  turns it on under Hospital setup > Features. While it is off the endpoint answers 503 `feature.disabled` and the
  staff app does not offer the download.
* **Staff app.** **Download ABDM record** on a signed or amended discharge summary (patient chart > Admissions >
  Discharge summary), for users with `abdm.export`; disabled while the summary has unsaved changes, and hidden while it
  is being amended. A refusal is shown with the server's reason.

| Method | Path | Access | Response |
|---|---|---|---|
| GET | `/api/v1/abdm/discharge-summaries/{id}/bundle` | `abdm.export`, switch `abdm` | the document, indented `application/fhir+json; charset=utf-8`, as the attachment `discharge-summary-{UHID}-{discharge date}-abdm.json`, `Cache-Control: no-store` |

Refusals are problem+json, in the order they are checked; the patient's consent, restriction, deletion and merging
follow the FHIR API's rules ([Consent and access rules](#consent-and-access-rules)):

| Situation | Answer |
|---|---|
| No `abdm.export` | 403 |
| Switch off | 503 `feature.disabled` (nothing is read) |
| No such summary (or deleted) | 404 |
| Summary of another facility than the user's | 422 `abdm.other_facility` |
| Patient deleted, or merged into another UHID | 422 `abdm.patient_deleted`, `abdm.patient_merged` |
| No active record-sharing (`DataSharing`) consent | **403 `abdm.consent_required`**, audited |
| Restricted (VIP) record the user may not open | 403 `patient.restricted` (`breakGlassAvailable`), audited |
| Summary not signed (draft) | 422 `abdm.not_signed` |

### What the document holds

What the doctor wrote is free text and stays text: each part of the summary is the narrative of a section, as written
(escaped, paragraphs and line breaks kept). Only what HC360 holds in coded form becomes coded entries. HC360 keeps
complaints, investigations, procedures and discharge medicines as text, so the document has no Observation, Procedure
or MedicationRequest entries for them, and no codes are invented. Each entry claims its NRCeS profile in `meta.profile`.

| Entry | From |
|---|---|
| Composition (first entry) | SNOMED CT 373942005 "Discharge summary"; status `final`, `amended` once amended; date = amendment, else signing; author = the doctor who signed (else the treating consultant, else the hospital); attester `legal` = the signing doctor at signing; custodian = the hospital; identifier `urn:uuid:{summary id}`, the same for every export |
| Patient | as in the FHIR API: UHID, the ABHA number when on file, name, gender, birth date, contact details, address |
| Encounter | the admission's in-patient visit, as in the FHIR API (admission number, period, admit source, discharge disposition, doctors); an admission that can no longer be read becomes an in-patient encounter with the summary's dates and status `unknown` |
| Practitioner | the author: the medical council registration number (v2-0203 `MD`, the council as assigner) when on file, and the HC360 practitioner code (`urn:hc360:practitioner`, `PRN`); name, gender, qualification as text; no contact details |
| Organization | the summary's facility: the HFR id (`https://facility.ndhm.gov.in`, `PRN`, official) when on file, and the HC360 facility code (`urn:hc360:facility`, `XX`); type `prov`; address, telephone, e-mail |
| Condition, one per code | each ICD code of the summary: ICD-10 coded with its catalogue title; ICD-11 codes and codes without a title in the text only (the profile admits ICD-10 and SNOMED CT codings, each with a display). Clinical status, onset and resolution as a doctor recorded the code during the stay (else active); confirmed; `encounter-diagnosis`; recorder = the author |
| CarePlan | the advice, and the follow-up as an appointment activity (date and instructions); only when the summary has either |
| DocumentReference | the whole summary as UTF-8 text, as the FHIR API's `ds-` document; type LOINC 18842-5 and SNOMED CT 373942005 |

Composition sections, in this order, each only when it has text or entries:

| Section | Code | Entries |
|---|---|---|
| Presenting complaints | SNOMED CT 422843007 Chief complaint section | |
| Condition at discharge | SNOMED CT 425044008 Physical exam section | |
| Investigations | SNOMED CT 721981007 Diagnostic studies report | |
| Medicines on discharge | SNOMED CT 1003606003 Medication history section | |
| Procedures and surgeries | SNOMED CT 1003640003 History of past procedure section | |
| Advice and follow-up | SNOMED CT 734163000 Care plan | CarePlan |
| Discharge summary (full text) | SNOMED CT 373942005 Discharge summary | DocumentReference |
| Final diagnosis | LOINC 11535-2 Hospital discharge Dx Narrative (with the list of codes) | Conditions |
| Course in hospital | LOINC 8648-8 Hospital course Narrative | |

The profile slices sections by code and is open at the end, so the two sections it does not define come last.

* **Ids.** Entries are `urn:uuid:{id}`. Patient, practitioner, facility, stay and composition keep HC360's record ids;
  the conditions, care plan and document reference have ids derived from the summary (RFC 9562 version 8 UUIDs from
  SHA-256), the same at every export. Every reference points at an entry, or names its target by display or identifier
  only (other doctors of the stay); the stay does not list the conditions back, so references never run in a circle.
* **Each download is a new document**: a new `Bundle.id` and identifier (`urn:uuid:` and a version 7 UUID),
  `meta.versionId` 1 (the profile requires it), `timestamp` = time of the download.

### Validation

The tests validate HC360's documents with Firely's validator (`Firely.Fhir.Validation.R4` 3.4.0, BSD-3-Clause, test
projects only) against the FHIR R4 core definitions (`Hl7.Fhir.Specification.Data.R4` 6.6.0) and the NRCeS profiles,
copied unchanged into `tests/Modules/HyFib.HealthCare360.Modules.Interop.Tests/TestData/ndhm.in-6.5.0` (its README says
where they came from).

* SNOMED CT and LOINC cannot be checked offline, so terminology-server checks are skipped and listed. Everything else
  is checked: structure, cardinality, slicing, fixed values, invariants, and bindings to value sets that can be expanded
  offline. HC360's documents must pass with no error, no warning and no skipped check (`DischargeSummaryRecordTests`: a
  full summary, the least a signed summary holds (no codes, ABHA number, HFR id, doctor or admission record), an
  amended one, one with markup and control characters in its text, and one with a diagnosis resolved during the stay).
* The guide's own discharge summary example passes, and nine deliberately broken documents fail with the rule they
  break (`ValidatorSetupTests`), so a validator that checks nothing cannot pass the tests.
* `AbdmExportTests` (IntegrationTests) validates the file downloaded from the endpoint, after checking the switch,
  permission, consent and signing rules and before checking the audit trail.

## Limitations and follow-ups

* **EHR-08 is done in part.** Signed discharge summaries download as NRCeS DischargeSummaryRecord documents
  ([ABDM health records](#abdm-health-records-ehr-08)). Still to build, and blocked until the National Health
  Authority (NHA) and the hospital provide what they need:
  * **ABHA verification and linking.** HC360 stores the ABHA number as entered; checking it, creating or linking ABHA
    numbers and addresses needs the ABDM APIs, with client credentials NHA issues (sandbox, then production).
  * **ABDM health information provider and user (HIP/HIU).** Linking care contexts to a patient's ABHA, answering
    consent requests from the consent manager and pushing encrypted records through the ABDM gateway, and requesting
    records from other providers. Needs those credentials, the hospital's Health Facility Registry (HFR) id, its
    registration as a HIP/HIU (bridge and service registration), and NHA's sign-off of the integration.
  * **Doctors' Healthcare Professionals Registry (HPR) ids.** HC360 does not record them; ABDM documents name the
    doctor by council registration number and HC360 code until it does.
  * **Other NRCeS documents** (PrescriptionRecord, DiagnosticReportRecord, OPConsultRecord and others), a digital
    signature on the bundle (`Bundle.signature`), and the summary's PDF in the document. The FHIR API's own resources
    still claim no NRCeS profiles.
  * The privacy data summary lists FHIR API readers as recipients of a patient's data. ABDM downloads are in the
    patient's record access log, but not listed as recipients, as HC360 does not know who receives the file.
* No `vread`/history, `_include`/`_revinclude`, `_sort`, `_summary`, `_elements`, XML, or bulk export (`$export`);
  clinical searches are per patient.
* Practitioner, Organization and Location are not served by the FHIR API: references carry a display name (and the
  HFR id for facilities). ABDM documents carry their doctor and hospital as entries. Binary is not served, so uploaded
  documents are metadata only.
* Not exported yet: immunizations, procedures, clinical notes, antibiotic susceptibilities, RPM device readings,
  vital-sign interpretation flags, the primary-diagnosis rank.
* Offset paging: rows added or changed between two page requests can shift the pages. The `next` links repeat the
  search parameters (including identifiers); POST `_search` keeps them only out of the first request line.
* Sign-in is the HC360 bearer token (10-minute access token and refresh). A SMART-on-FHIR / OAuth client-credentials
  flow for system-to-system access is a follow-up.
* Consent is all-or-nothing per patient (`DataSharing`); purpose-of-use, recipient-specific or expiring consents are a
  follow-up.
* Maintainer: add `application/fhir+json` to the server's response-compression MIME types in `Program.cs` (FHIR
  responses are not compressed today).
