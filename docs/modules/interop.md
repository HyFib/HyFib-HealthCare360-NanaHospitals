# Interop module (`interop`) — HL7 FHIR R4 read API and ABDM health records

Read-only HL7 FHIR R4 API that lets other providers read a patient's record, **subject to the patient's consent**
(SRS **EHR-05**): Patient, Encounter, Observation, Condition, AllergyIntolerance, MedicationRequest, DiagnosticReport
and DocumentReference under `/fhir/r4`. It is built with the Firely .NET SDK (`Hl7.Fhir.R4` 6.5.0), returns JSON only
and writes nothing.

It also exports discharge summaries, lab reports, consultation notes and prescriptions as **ABDM health records**
(SRS **EHR-08**): FHIR documents of the NRCeS implementation guide for the Ayushman Bharat Digital Mission, carrying the
patient's ABHA number, behind the `features.abdm` switch, which is **off** until an administrator turns it on
([ABDM health records](#abdm-health-records-ehr-08)). The ABDM gateway that would send them is set up in settings but not
live.

| | |
|---|---|
| Project | `src/server/Modules/HyFib.HealthCare360.Modules.Interop` (layer 8; references Patients, Clinical, Diagnostics and Inpatient) |
| Schema | none. The module owns no tables and reads the other modules' entities through `HospitalDbContext` (no migration) |
| Requirements | EHR-05 (with EHR-06 consent and EHR-07 restricted records); EHR-08, **partly met**: ABHA capture and the ABDM document export of consultations, prescriptions, lab reports and discharge summaries are built; verifying an ABHA with ABDM and linking care contexts to it need the ABDM gateway (the hospital's NHA credentials, not live), and imaging reports are not exported yet |
| Tests | `tests/Modules/HyFib.HealthCare360.Modules.Interop.Tests`: every mapper, the vital-sign codes and BP components, lab values and ranges, missing data, the consent and sharing rules, the SQL patient search, search parsing (`_count` cap, dates, tokens, strict parameters), in-memory search and paging links, OperationOutcomes, the CapabilityStatement, FHIR JSON round trips, the FHIR 401/403 handler and the SQL translation of every query (Npgsql, no database); ABDM: `AbdmDocumentTests` (each record type against the IG, ids, references, versions, the validated samples in `Fixtures/Abdm`) and `AbdmGatewayTests`. Elsewhere: `HealthIdTests` (Verhoeff for Aadhaar, ABHA number incl. the IG's example numbers, ABHA address, Platform.Tests), `AbhaValidationTests` (Patients.Tests), `AbdmEndpointTests` (ArchitectureTests: switch, permissions, read-only), `AbdmExportTests` (IntegrationTests: registration, export, who may export), staff `modules/patients/validation.test.ts` |

## Permission and set-up

| Permission | Purpose | Default roles |
|---|---|---|
| `fhir.read` | Read and search patient records through the FHIR API (patients with record-sharing consent only; every access is audited) | HospitalAdmin |
| `abdm.export` | Export discharge summaries, lab reports, consultation notes and prescriptions as ABDM health records (every export is audited). Staff only: it does not check the record-sharing consent, so never give it to an integration service account | HospitalAdmin, MrdOfficer |

Integration partners (a referral hospital, a health information exchange) get their own service user:

1. Create a role such as "FHIR integration" with `fhir.read`. Add `patients.restricted.read` only if restricted (VIP)
   records may also be shared. Never add `abdm.export`: the ABDM export is a staff download that does not check the
   patient's record-sharing consent ([ABDM health records](#abdm-health-records-ehr-08)).
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

## Audit and logging

| Event | Category / action | Recorded |
|---|---|---|
| Read | `phi` / `patient.accessed` | patient id, `fhir.{Type}`, resource id, `{ api, interaction: read, resourceType }` |
| Search in a patient's record | `phi` / `patient.accessed` | one entry: patient id, `fhir.{Type}`, returned and total counts, parameter **names** |
| Patient search | `phi` / `patient.accessed` | one entry per returned patient, written in one transaction |
| Patient search that returned nobody | `integration` / `fhir.search` | counts and parameter names |
| Read or search refused for consent or restriction | `security` / `fhir.access_denied` (Warning) | patient id, type, id, reason |

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

The Ayushman Bharat Digital Mission (ABDM) links a patient's records to their **ABHA** (Ayushman Bharat Health Account)
and moves them as FHIR documents of the NRCeS "FHIR Implementation Guide for ABDM" (package `ndhm.in`, validated against
**6.5.0**). HC360 captures the ABHA, builds the documents and exports them one at a time; sending them to ABDM through
its gateway needs the hospital's NHA credentials and is not live ([ABDM gateway](#abdm-gateway-not-live)).

### The switch

`features.abdm` ("ABDM health records", [features.md](features.md)) is **off by default**, unlike the switches that
existed before their feature: it is new and, once the gateway is live, sends records outside the hospital. While it is
off every `/api/v1/abdm/*` endpoint answers 503 `feature.disabled`. ABHA numbers and addresses are recorded and shown
whatever the switch says.

### ABHA on the patient

| | |
|---|---|
| Fields | `Patient.AbhaNumberCipher` (14 digits, AES-256-GCM), `AbhaNumberIndex` (blind index for search and duplicate checks), `AbhaNumberMasked` (`XX-XXXX-XXXX-1234`), `AbhaAddress` (lower case). No new columns |
| Number | 14 digits, typed run together or grouped with spaces or hyphens; anything else is refused ("Enter a valid 14-digit ABHA number."). **There is no check digit**: no NHA or ABDM specification publishes one, and the ABHA numbers of the IG's own example patients (`21-1521-3828-3227`, `22-7225-4829-5255`, `25-1531-3528-3228` in `ndhm.in#6.5.0`) fail a Verhoeff check, as about 9 in 10 of any 14 digits would. Refusing them would turn real ABHA cards away at the desk, so any 14 digits are taken, and a mistyped number cannot be told from a real one until it is checked with ABDM, which needs the gateway ([not live](#abdm-gateway-not-live)). Shown as `xx-xxxx-xxxx-xxxx` (`Abha.Format`) where the full number is needed (ABDM documents, the FHIR API) and masked everywhere else |
| Address | 3 to 32 letters or digits, which single dots or underscores may join, then `@abdm`, or `@sbx` on the ABDM sandbox (`meera.k@abdm`). Case does not matter; it is stored in lower case |
| Where | Registration and the patient record's edit screen (staff app, Health IDs); the same rules on the server (`CommonRules.AbhaNumber`, `AbhaAddress`) and in the form (`isValidAbhaNumber`, `isValidAbhaAddress`). Leaving the number blank on edit keeps it; "Remove ABHA" clears it |
| Audit | `patient.abha_changed` (category data), one entry when the number is linked, replaced or removed or the address added, changed or removed, at registration or later: "ABHA number XX-XXXX-XXXX-9010 linked; ABHA address added". The number appears masked only; the blind index is kept out of the data-change audit like the cipher (`[SensitiveData]`) |
| Privacy | Listed in the personal-data catalogue under registration details, shown masked in the patient's data summary ([privacy.md](privacy.md)); log lines and AI prompts mask ABHA numbers and addresses (`SensitiveText`, `PhiRedactor`) |

The code lives in SharedKernel (`Abha` in `HealthIds.cs`, next to the `Verhoeff` check digit that Aadhaar numbers, not
ABHA numbers, end in).

### Endpoints (`/api/v1/abdm`)

| Method | Path | Permission | Response |
|---|---|---|---|
| GET | `/bundles/discharge-summaries/{id}` | `abdm.export` | DischargeSummaryRecord document of a signed or amended discharge summary |
| GET | `/bundles/lab-reports/{id}` | `abdm.export` | DiagnosticReportRecord document of a released (or amended) lab report |
| GET | `/bundles/op-consultations/{id}` | `abdm.export` | OPConsultRecord document of a signed out-patient, emergency, telehealth or health check-up consultation (clinical encounter id) |
| GET | `/bundles/prescriptions/{id}` | `abdm.export` | PrescriptionRecord document of a signed prescription |
| GET | `/gateway` | `admin.integrations.manage` (no role has it by default: the super administrator) | `AbdmGatewayStatus`: how the gateway is set up, what is missing, `live: false` and what remains |

* Bundles are `application/fhir+json; charset=utf-8`, never cached; `?pretty=true` indents.
* The record must be in the caller's facility (404 otherwise, as for a record that does not exist) and of a patient
  who is not deleted (404). A restricted (VIP) record needs `patients.restricted.read` or break-the-glass (403), as on
  screen, and this is checked before anything else about the record is told. Then the patient must not be merged (422
  `abdm.patient_merged`) and the record must be complete (422 `abdm.not_exportable`: a draft, a cancelled
  prescription, an unreleased report, an unsigned note, an in-patient note, which travels with the discharge summary).
* An export is a staff download, like the printed copy, so it does not need the record-sharing consent of the FHIR
  API, and `abdm.export` is for hospital staff only: an integration partner's service user reads records through
  `fhir.read`, where consent applies. Sending a record to ABDM is authorised by the patient's ABDM consent artefact,
  through the gateway.
* Every export is audited: `phi` / `patient.accessed`, "ABDM health record exported: discharge summary", entity
  `abdm.{Type}` and the record id, details `{ api: "abdm", record, document, version, entries }`.
* The staff app has no export button yet: the API is what the gateway client will use, and what the hospital's staff
  call for NHA's sandbox testing.

### The documents

Each document is a `document` Bundle (profile `DocumentBundle`) whose first entry is the Composition, followed by every
resource it uses, each once, with a `urn:uuid:` full URL; every reference resolves inside the document. The resources
come from the FHIR read API's mappers above (same rules: only signed, released, verified data), fitted to the IG's
profiles.

| Record | Composition (`meta.profile`, `type`) | Sections (code: entries) |
|---|---|---|
| Prescription | `PrescriptionRecord`, SNOMED CT 440545006 "Prescription record" | 440545006: one MedicationRequest per medicine, and the printed PDF as Binary; advice as text |
| Lab report | `DiagnosticReportRecord`, SNOMED CT 721981007 "Diagnostic studies report" | 4241000179101 "Laboratory report": the DiagnosticReport (results as Observations) and the printed PDF as DocumentReference. The laboratory (facility) is the author |
| Consultation | `OPConsultRecord`, SNOMED CT 371530004 "Clinical consultation report" | 422843007 chief complaint: Conditions; 371529009 medical history (HPI); 425044008 physical examination: vital-sign Observations; 722446000 allergies; 721912009 medications: MedicationRequests; 390906007 follow-up; 306206005 referral; then LOINC 51847-2 assessment and plan (the IG has no such section; its slicing is open) |
| Discharge summary | `DischargeSummaryRecord`, SNOMED CT 373942005 "Discharge summary" | 422843007 complaints and diagnoses: the stay's Conditions; 722446000 allergies; 721981007 investigations: the stay's lab DiagnosticReports; 1003606003 medications: the stay's MedicationRequests; 1003640003 procedures; 734163000 care plan (advice, follow-up); 373942005 the summary as DocumentReference (the printed PDF, else the summary's text); then LOINC 8648-8 hospital course |

Section and document codes, systems and displays are the ones the IG's profiles fix. Empty sections are left out;
every section has a narrative (its text, or one line per entry). Every resource has a generated narrative.

| Resource | Profile | What the IG adds to the read API's resource |
|---|---|---|
| Patient | `Patient` | ABHA number (`https://healthid.ndhm.gov.in`, `xx-xxxx-xxxx-xxxx`) typed `ABHA` from the IG's identifier types; UHID typed MR. A patient without an ABHA is still exported, with the UHID |
| Practitioner | `Practitioner` | From the practitioner master: medical-council registration (v2-0203 MD, the council as assigner) and staff code (PRN), name, gender, qualification. No personal phone or e-mail |
| Organization | `Organization` | From the facility master: HFR id (`https://facility.ndhm.gov.in`, PRN), facility code (XX), type `prov`, work phone, e-mail, address |
| Encounter | `Encounter` | The visit (with its admission for a stay) or the clinical encounter |
| Condition | `Condition` | ICD-10 coding only (free-text diagnoses as text) |
| AllergyIntolerance | `AllergyIntolerance` | Allergen as text; allergies recorded in error left out |
| MedicationRequest | `MedicationRequest` | As the read API (medicine as text, dosage with v3-GTSAbbreviation timing codes and their displays) |
| Observation | `ObservationVitalSigns`, `ObservationBodyMeasurement` (weight, height, BMI) or `Observation` (lab results, pain, GCS) | LOINC (and SNOMED CT) codings only, each with a display; performer = the laboratory (results) or the hospital (vital signs) |
| DiagnosticReport | `DiagnosticReportLab` | Category SNOMED CT 394595002 "Pathology"; LOINC 11502-2; performer and results interpreter = the laboratory; conclusion (the IG requires one; the laboratory writes none) = the results the laboratory flagged ("Flagged by the laboratory: Haemoglobin (Low)."), or "See the individual results." when it flagged none: an unflagged result is not a normal one (a qualitative result, or one without a reference range, is never flagged), so nothing is said about the results |
| DocumentReference, Binary | `DocumentReference`, `Binary` | The printed PDF (made by the same document sources as the download and the messages); when this server cannot make PDFs, the lab report and prescription go without it and the discharge summary carries its text |

* **Ids** are UUIDs derived from HC360's ids (SHA-256, RFC 9562 version 8), so exporting a record again gives the
  same ids and a patient or doctor has one id in every document.
* **Versions.** `meta.versionId` of the Bundle and Composition is the latest change to anything in the document (Unix
  milliseconds); the Bundle's id and `identifier` (`urn:ietf:rfc:3986`, `urn:uuid:…`) change with it, the
  Composition's id does not. An amended record is an `amended` Composition. `meta.security` is v3-Confidentiality V.
* **Not coded:** medicines (no SNOMED CT drug codes), allergens, free-text diagnoses, lab tests without a LOINC code in
  the test master. They travel as text, which the IG allows.

### Validation

The four sample documents built by `AbdmDocumentTests` are kept in
`tests/Modules/HyFib.HealthCare360.Modules.Interop.Tests/Fixtures/Abdm` (a test fails when the code builds anything
else; `HC360_WRITE_ABDM_FIXTURES=1` writes them again, after which they are validated again). They were checked on
2026-10-16 with the official **HL7 FHIR validator** (`validator_cli.jar` 7.0.2) against `ndhm.in#6.5.0`:

```
java -jar validator_cli.jar Fixtures/Abdm/*.json -version 4.0.1 -ig ndhm.in#6.5.0 -tx n/a
```

The development machine reaches neither tx.fhir.org nor packages.fhir.org, so the packages came from the npm mirror of
the FHIR package registry (checksums checked) and the run had **no terminology server** (`-tx n/a`). The IG's own
example documents were validated the same way, as a control.

| Sample | Errors | Warnings | The IG's example of the same type, same set-up |
|---|---|---|---|
| `prescription.json` | 3 | 4 | `Bundle-Prescription-example-06`: 5 errors, 4 warnings |
| `lab-report.json` | 3 | 6 | `Bundle-DiagnosticReport-Lab-example-03`: 8 errors, 12 warnings |
| `op-consultation.json` | **0** | 23 | `Bundle-OPConsultNote-example-05`: 5 errors, 4 warnings |
| `discharge-summary.json` | 3 | 7 | `Bundle-DischargeSummary-example-04`: 8 errors, 14 warnings |

* **Every error is the MIME type of an attachment** (`application/pdf`, `text/plain; charset=utf-8`): its required
  binding to the MimeType value set can only be checked by a terminology server, and the section entry holding that
  Binary or DocumentReference then "matches no profile". The IG's own examples fail in exactly the same way
  offline. Nothing else is an error.
* The warnings are codes that cannot be checked without a terminology server (UCUM units, BCP-47 languages), and the
  visit number's identifier type (v2-0203 `VN`), which is not in the R4 IdentifierType value set (an extensible
  binding, so a warning).
* To be done where tx.fhir.org is reachable: run the validator without `-tx n/a`, expecting 0 errors.

### ABDM gateway (not live)

Sending records to ABDM makes the hospital a Health Information Provider (HIP) on NHA's gateway (the HIE-CM). What is
built:

| | |
|---|---|
| Settings | `abdm.gateway` (hospital-wide JSON): `enabled` (false), `baseUrl` (https, no user name, e.g. the sandbox `https://dev.abdm.gov.in/api/hiecm`), `hipId` (the HFR id registered as HIP), `cmId` (`sbx` or `abdm`), `timeoutSeconds` (30). `abdm.gateway.secret` (encrypted secret setting): `clientId`, `clientSecret` issued by NHA. Both are added under Administration › Settings (Add setting); the secret is never shown again |
| Status | `GET /api/v1/abdm/gateway`: enabled, configured, `live` (always false), the URL and ids, whether the client id and secret are set (never their values), the problems in the set-up and what remains |
| Client | `IAbdmGatewayClient` (gateway v3): `CreateSessionAsync` (`/gateway/v3/sessions`), `LinkCareContextsAsync` (HIP-initiated linking of exported records to the patient's ABHA address), `AcknowledgeConsentAsync` (`consent/v3/request/hip/on-notify`), `TransferHealthInformationAsync` (`data-flow/v3/health-information/hip/on-request`, the encrypted push to the HIU, `notify`). The registered `DisabledAbdmGatewayClient` sends nothing and answers every call with 422 `abdm.gateway_disabled`, naming what is missing |
| Encryption | `IFideliusCipher` (X25519 key agreement, AES-256-GCM, as ABDM's Fidelius). The registered `UnavailableFideliusCipher` refuses every call |

What remains, in order (also in the status's `remaining`):

1. Register the hospital as a HIP with its HFR id and get NHA **sandbox** client credentials (an outside approval).
2. Build the live client and the callback endpoints the gateway calls (patient discovery, link initiation and
   confirmation, consent notification, health-information request), confirm the paths against the sandbox, and link
   care contexts to the patient's ABHA when a record is completed.
3. Implement Fidelius and test it against NHA's reference implementation.
4. Pass NHA's sandbox functional tests and certification, then switch to the production gateway.
5. Sign documents (`Bundle.signature`, optional in the IG) with the doctor's DSC when the DSC signing of prescriptions
   (ERX-04) covers them.

## Limitations and follow-ups

* **EHR-08 is partly met.** ABHA capture and the ABDM document export are built (behind `features.abdm`, off by
  default). Not met: "link ABHA numbers" in ABDM's sense, that is verifying the ABHA with ABDM and linking the
  hospital's care contexts to it, needs the ABDM gateway (HIP linking, consent, encrypted data push), which needs the
  hospital's NHA credentials and is not live ([ABDM gateway](#abdm-gateway-not-live)); and radiology and cardiology
  reports are not exported as ABDM documents (the IG's DiagnosticReportImaging), nor are immunisations, wellness and
  invoice records. The FHIR read API's resources keep the ABDM-friendly identifiers (ABHA number, HFR id) but claim no
  NRCeS profiles; the ABDM documents do.
* No `vread`/history, `_include`/`_revinclude`, `_sort`, `_summary`, `_elements`, XML, or bulk export (`$export`);
  clinical searches are per patient.
* Practitioner, Organization and Location are not served: references carry a display name (and the HFR id for
  facilities). Binary is not served, so uploaded documents are metadata only.
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
