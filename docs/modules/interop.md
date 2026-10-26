# Interop module (`interop`) — HL7 FHIR R4 read API and ABDM health records

Read-only HL7 FHIR R4 API that lets other providers read a patient's record, **subject to the patient's consent**
(SRS **EHR-05**): Patient, Encounter, Observation, Condition, AllergyIntolerance, MedicationRequest, DiagnosticReport
and DocumentReference under `/fhir/r4`. It is built with the Firely .NET SDK (`Hl7.Fhir.R4` 6.6.0), returns JSON only
and writes nothing.

It also exports discharge summaries, lab reports, radiology and cardiology reports, consultation notes and prescriptions as **ABDM health records**
(SRS **EHR-08**): FHIR documents of the NRCeS implementation guide for the Ayushman Bharat Digital Mission, carrying the
patient's ABHA number, behind the `features.abdm` switch, which is **off** until an administrator turns it on
([ABDM health records](#abdm-health-records-ehr-08)). Completed records can be linked to the patient's ABHA through the
ABDM gateway (HIP-initiated linking, ABDM M2) once the hospital has NHA credentials; consent and the transfer of
records to other providers are not built yet ([ABDM gateway](#abdm-gateway)).

| | |
|---|---|
| Project | `src/server/Modules/HyFib.HealthCare360.Modules.Interop` (layer 8; references Patients, Clinical, Diagnostics and Inpatient) |
| Schema | `interop`: `abdm_link_requests` (a request to link records to a patient's ABHA and where it has got to) and `abdm_link_tokens` (the link token the gateway issued for an ABHA address, encrypted); migration `AbdmLinking`. Everything else is read from the other modules' entities through `HospitalDbContext` |
| Requirements | EHR-05 (with EHR-06 consent and EHR-07 restricted records); EHR-08, **partly met**: ABHA capture and the ABDM document export of consultations, prescriptions, lab reports, radiology and cardiology reports and discharge summaries are built, and so is linking completed records to the patient's ABHA through the ABDM gateway (HIP-initiated linking, tested against a fake gateway built to NHA's wrapper; it needs the hospital's NHA credentials and a sandbox run); verifying an ABHA with ABDM, user-initiated linking, consent and the transfer of records are not built |
| Tests | `tests/Modules/HyFib.HealthCare360.Modules.Interop.Tests`: every mapper, the vital-sign codes and BP components, lab values and ranges, missing data, the consent and sharing rules, the SQL patient search, search parsing (`_count` cap, dates, tokens, strict parameters), in-memory search and paging links, OperationOutcomes, the CapabilityStatement, FHIR JSON round trips, the FHIR 401/403 handler and the SQL translation of every query (Npgsql, no database); ABDM: `AbdmDocumentTests` (each record type against the IG, ids, references, versions, narratives, statuses and attesters, referral laboratories, the validated samples in `Fixtures/Abdm`), `AbdmDocumentVersionTests` (a document as of its record, its PDF made again, the order of rows, the PDF rule), `ValidatorSetupTests` (`Abdm/`: the Firely validator set-up, see [Validation](#validation)), `AbdmGatewayTests`, `HttpAbdmGatewayClientTests` (`Abdm/`: the gateway calls against a fake gateway, see [Linking](#linking-records-to-the-patients-abha-abdm-m2)) and `AbdmCallbackVerifierTests`. Elsewhere: `HealthIdTests` (Verhoeff for Aadhaar, ABHA number incl. the IG's example ids, ABHA address, Platform.Tests), `AbhaValidationTests` (Patients.Tests), `AbdmEndpointTests` (ArchitectureTests: switch, permissions, the one write, the anonymous signed callbacks), `AbdmLinkingTests` (IntegrationTests: a record linked end to end through a fake gateway and its signed callbacks, a kept token reused and a refused one dropped, forged and unsigned callbacks refused, nothing sent while the gateway is off), `AbdmExportTests` (IntegrationTests: registration, export, who may export, refusals audited, amended notes, records under a merged duplicate (a restricted one at either end of the merge, in a discharge summary, the survivor deleted), an ABHA number a merge links, a later allergy left out of a signed note, the LOINC name rule, the download's file and the DSC rule, every downloaded document validated), `LabTestLoincTests` (Diagnostics.Tests: a LOINC name only with its code), staff `modules/patients/validation.test.ts`, `modules/emr/AbdmRecordButton.test.tsx`, `modules/emr/abdmRecords.test.tsx` |

## Permission and set-up

| Permission | Purpose | Default roles |
|---|---|---|
| `fhir.read` | Read and search patient records through the FHIR API (patients with record-sharing consent only; every access is audited) | HospitalAdmin |
| `abdm.export` | Export discharge summaries, lab reports, radiology and cardiology reports, consultation notes and prescriptions as ABDM health records (every export is audited). Staff only: it does not check the record-sharing consent, so never give it to an integration service account | HospitalAdmin, MrdOfficer |

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
| ABDM health record downloaded | `phi` / `abdm.record.exported` | patient id, `DischargeSummary` or `Prescription` and its id, `{ format: fhir-r4-document, profile, document, resources }` (`document` is the bundle's identifier, `resources` its entry count) |
| ABDM download refused for consent or restriction | `security` / `abdm.export_refused` (Warning) | patient id, `DischargeSummary` or `Prescription` and its id, reason (`consent-required`, `restricted-record`) |

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
Released, verified results only. `code`: the analyte's LOINC code when the test master has one (with the LOINC name
entered for it in the test master as display), plus the HC360 test code (`urn:hc360:lab-test`), text = test name.
Numeric results are quantities parsed from the printed value (comparators `<`, `<=`, `>=`, `>` and Indian digit grouping
kept), UCUM when the printed unit is known (for example `10³/µL` → `10*3/uL`, `mm/hr` → `mm/h`, `µIU/mL` → `u[IU]/mL`)
and otherwise the unit as text without a system. Text, option and culture results are strings; organism and colony count
are notes. Flag → v3 interpretation (N, L, H, LL, HH, A, AA). The printed reference interval is the `referenceRange`
text, with low/high for "a - b", "Up to b", "a and above", "< b" and "> a". `effective` = sample collection (else order)
time, `issued` = release time, encounter = the visit. A result re-verified after release (a numbered amendment) is
`amended`.

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
  impression, then each addendum with its time (an addendum may correct the impression, and a reader may see only the
  conclusion); `presentedForm` = the whole report as UTF-8 text (study, modality, body part, technique, history,
  comparison, findings, impression, addenda); `appended` once addenda exist.
* Cardiology (`card-`): category EC (ECG, Holter, TMT), CUS (echo) or OTH; coded with the LOINC study where LOINC names
  one: 11524-6 EKG study, 59281-6 Transthoracic cardiac echo study report (not for a transoesophageal echo), 18752-6
  Exercise stress test study (TMT), 18754-2 Holter monitor study (none for ABPM or other studies);
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
its gateway needs the hospital's NHA credentials; linking records to the patient's ABHA is built, consent and the transfer
of records are not ([ABDM gateway](#abdm-gateway)).

### The switch

`features.abdm` ("ABDM health records", [features.md](features.md)) is **off by default**, unlike the switches that
existed before their feature: it is new and, once the gateway is live, sends records outside the hospital. While it is
off every `/api/v1/abdm/*` endpoint answers 503 `feature.disabled`. ABHA numbers and addresses are recorded and shown
whatever the switch says.

### ABHA on the patient

| | |
|---|---|
| Fields | `Patient.AbhaNumberCipher` (14 digits, AES-256-GCM), `AbhaNumberIndex` (blind index for search and duplicate checks), `AbhaNumberMasked` (`XX-XXXX-XXXX-1234`), `AbhaAddress` (lower case). No new columns |
| Number | 14 digits, typed run together or grouped with spaces or hyphens; anything else is refused ("Enter a valid 14-digit ABHA number."). **There is no check digit**: no NHA or ABDM specification publishes one, and the ABHA number of the IG's own example patient (`22-7225-4829-5255` in `ndhm.in#6.5.0`) fails a Verhoeff check, as do the 14-digit HPR ids of its example doctors (`21-1521-3828-3227`, `25-1531-3528-3228`, under `https://doctor.ndhm.gov.in`) and about 9 in 10 of any 14 digits. Refusing them would turn real ABHA cards away at the desk, so any 14 digits are taken, and a mistyped number cannot be told from a real one until it is checked with ABDM, which is not built yet ([ABDM gateway](#abdm-gateway)). Shown as `xx-xxxx-xxxx-xxxx` (`Abha.Format`) where the full number is needed (ABDM documents, the FHIR API) and masked everywhere else |
| Address | 3 to 32 letters or digits, which single dots or underscores may join, then `@abdm`, or `@sbx` on the ABDM sandbox (`meera.k@abdm`). Case does not matter; it is stored in lower case |
| Where | Registration and the patient record's edit screen (staff app, Health IDs); the same rules on the server (`CommonRules.AbhaNumber`, `AbhaAddress`) and in the form (`isValidAbhaNumber`, `isValidAbhaAddress`). Leaving the number blank on edit keeps it; "Remove ABHA" clears it |
| Audit | `patient.abha_changed` (category data), one entry when the number is linked, replaced or removed or the address added, changed or removed, at registration or later: "ABHA number XX-XXXX-XXXX-9010 linked; ABHA address added". A merge that gives the surviving record the duplicate's number (when it has none of its own) is a link too: "Merged from NH-000077: ABHA number XX-XXXX-XXXX-9010 linked". The number appears masked only; the blind index is kept out of the data-change audit like the cipher (`[SensitiveData]`) |
| Privacy | Listed in the personal-data catalogue under registration details, shown masked in the patient's data summary ([privacy.md](privacy.md)); log lines and AI prompts mask ABHA numbers and addresses (`SensitiveText`, `PhiRedactor`) |

The code lives in SharedKernel (`Abha` in `HealthIds.cs`, next to the `Verhoeff` check digit that Aadhaar numbers, not
ABHA numbers, end in).

### Endpoints (`/api/v1/abdm`)

| Method | Path | Permission | Response |
|---|---|---|---|
| GET | `/bundles/discharge-summaries/{id}` | `abdm.export` | DischargeSummaryRecord document of a signed or amended discharge summary, as `discharge-summary-{UHID}-{discharge date}-abdm.json` |
| GET | `/bundles/lab-reports/{id}` | `abdm.export` | DiagnosticReportRecord document of a released (or amended) lab report, as `lab-report-{report number}-abdm.json` |
| GET | `/bundles/radiology-reports/{id}` | `abdm.export` | DiagnosticReportRecord document of a released radiology report (the report's id), as `radiology-report-{accession number}-abdm.json`; amended once an addendum is added after the release |
| GET | `/bundles/cardiology-reports/{id}` | `abdm.export` | DiagnosticReportRecord document of a released ECG, echo, TMT, Holter or ABPM report (the report's id), as `cardiology-report-{accession number}-abdm.json` |
| GET | `/bundles/op-consultations/{id}` | `abdm.export` | OPConsultRecord document of a signed out-patient, emergency, telehealth or health check-up consultation (clinical encounter id), as `consultation-{UHID}-{date}-abdm.json` |
| GET | `/bundles/prescriptions/{id}` | `abdm.export` | PrescriptionRecord document of a signed prescription, as `prescription-{number}-abdm.json` |
| GET | `/gateway` | `admin.integrations.manage` (no role has it by default: the super administrator) | `AbdmGatewayStatus`: how the gateway is set up, what is missing, `live: false` and what remains |
| POST | `/links` | `abdm.export` | 202 `AbdmLinkView`, `Location: /api/v1/abdm/links/{id}`: records of one patient (`{ records: [{ type, id }] }`, at most 50) to link to their ABHA ([Linking](#linking-records-to-the-patients-abha-abdm-m2)) |
| GET | `/links/{id}` | `abdm.export` | `AbdmLinkView`: the request's records and where it has got to (`AwaitingToken`, `Sent`, `Linked`, `Failed` with the gateway's code and reason) |

* Bundles are `application/fhir+json; charset=utf-8`, files to save (`Content-Disposition: attachment`, with the names
  above; dates are the day in IST), never cached; `?pretty=true` indents.
* The record must be in the caller's facility (404 otherwise, as for a record that does not exist) and of a patient
  who is not deleted (404). A restricted (VIP) record needs `patients.restricted.read` or break-the-glass (403), as on
  screen, and this is checked before anything else about the record is told, even that its patient was since deleted.
  A refusal is audited as a security
  warning: `security` / `abdm.export_denied`, "ABDM health record export refused: lab report", details
  `{ api: "abdm", record, reason }`. Then the record must be complete (422 `abdm.not_exportable`: a draft, a cancelled
  prescription, an unreleased report, an unsigned note, an in-patient note, whose diagnoses and prescriptions are in
  the stay's discharge summary). A prescription is a copy of the prescription like its printout, so where the facility
  requires the doctor's DSC and the prescription has no valid one it is refused with the printout's codes: 422
  `prescription.digital_signature_required`, `…_untrusted` or `…_invalid` ([clinical.md](clinical.md)).
* A record still kept under a duplicate UHID that was merged into another patient (a lab, radiology or cardiology report: the Diagnostics module
  leaves its records where they are) is exported as the surviving patient's, with that patient's UHID and ABHA and the
  duplicate's UHID as an old one (its printed copy names it), once every patient on the way may be opened (a
  restricted patient at either end needs the permission). A discharge summary holds the stay's lab reports kept under
  a duplicate merged into its patient on the same condition: when that duplicate is restricted and the caller may not
  open it, the export is refused with 403 `abdm.restricted_duplicate`, which does not name the duplicate (breaking the
  glass for the patient on screen would not open it), and the refusal is audited under the duplicate. A report of the
  stay kept under anyone else's record is left out. A chain of merges is followed to its end; a deleted patient on it,
  or a loop, is answered as a record that does not exist (404, naming no patient).
* The printed PDF is part of the document. A server that cannot make PDFs (no browser) exports every document without
  it; one that can but fails now (the browser crashed or took too long) answers 503 `pdf.unavailable`, to be tried
  again, rather than a different document without its PDF.
* An export is a staff download, like the printed copy, so it does not need the record-sharing consent of the FHIR
  API, and `abdm.export` is for hospital staff only: an integration partner's service user reads records through
  `fhir.read`, where consent applies. Sending a record to ABDM is authorised by the patient's ABDM consent artefact,
  through the gateway.
* Every export is audited under the document's patient: `phi` / `patient.accessed`, "ABDM health record exported:
  discharge summary", entity `abdm.{Type}` and the record id, details `{ api: "abdm", record, document, version,
  entries }`.
* **Staff app**, for users with `abdm.export` while the switch is on; a refusal is shown with the server's reason:
  **Download ABDM record** on a signed or amended discharge summary (patient chart > Admissions > Discharge summary;
  disabled while it has unsaved changes, hidden while it is being amended) and in the menu of a signed prescription
  (the patient chart's and the encounter's prescriptions). Lab reports, radiology reports and consultations are exported through the API,
  which is also what the hospital's staff call for NHA's sandbox testing. Linking records to the patient's ABHA is
  API-only too in this release.

### The documents

Each document is a `document` Bundle (profile `DocumentBundle`) whose first entry is the Composition, followed by every
resource it uses, each once, with a `urn:uuid:` full URL; every reference resolves inside the document. The resources
come from the FHIR read API's mappers above (same rules: only signed, released, verified data), fitted to the IG's
profiles.

| Record | Composition (`meta.profile`, `type`) | Sections (code: entries) |
|---|---|---|
| Prescription | `PrescriptionRecord`, SNOMED CT 440545006 "Prescription record" | 440545006: one MedicationRequest per medicine, and the printed PDF as Binary; advice as text |
| Lab report | `DiagnosticReportRecord`, SNOMED CT 721981007 "Diagnostic studies report" | 4241000179101 "Laboratory report": the DiagnosticReport (results as Observations) and the printed PDF as DocumentReference. The laboratory (facility) is the author |
| Radiology report | `DiagnosticReportRecord`, SNOMED CT 721981007 "Diagnostic studies report" | 4201000179104 "Imaging report" (the parent of the IG's per-modality imaging report codes, so it fits every modality): the DiagnosticReport (`DiagnosticReportImaging`, with its ImagingStudy) and the printed PDF as DocumentReference. The radiologist who signed it off is the author and attester; an addendum after the release makes it `amended`, with the doctor who added the latest one as a second author and the attester from then (no one when that user is not a doctor). An addendum before the release is part of the report as first released |
| Cardiology report | `DiagnosticReportRecord`, SNOMED CT 721981007 "Diagnostic studies report" | 4201000179104 "Imaging report", as for radiology: the IG's DiagnosticReportRecord takes a DiagnosticReportLab or a DiagnosticReportImaging, and gives the imaging one to "imaging services like Radiology, Cardiology, Endoscopy". The DiagnosticReport (`DiagnosticReportImaging`, with no ImagingStudy: an ECG, TMT or Holter is a recording, and HC360 keeps no echo series UIDs) and the printed PDF as DocumentReference, typed with the report's LOINC study, else LOINC 26441-6 "Cardiology studies (set)". The cardiologist who signed it off is the author and attester; a released cardiology report is not changed afterwards, so the document is final |
| Consultation | `OPConsultRecord`, SNOMED CT 371530004 "Clinical consultation report" | 422843007 chief complaint: Conditions; 371529009 medical history (HPI); 425044008 physical examination: vital-sign Observations; 722446000 allergies; 721912009 medications: MedicationRequests; 390906007 follow-up; 306206005 referral; then LOINC 51847-2 assessment and plan (the IG has no such section; its slicing is open) |
| Discharge summary | `DischargeSummaryRecord`, SNOMED CT 373942005 "Discharge summary" | 422843007 complaints and diagnoses: the stay's Conditions; 722446000 allergies; 721981007 investigations: the stay's lab DiagnosticReports; 1003606003 medications: the doctor's medicines on discharge as text, then the MedicationRequests prescribed during the stay; 1003640003 procedures; 734163000 care plan (advice, follow-up); 373942005 the summary as DocumentReference (the printed PDF, else the summary's text); then LOINC 8648-8 hospital course |

Section and document codes, systems and displays are the ones the IG's profiles fix. Empty sections are left out.
A section's narrative reads whole on its own, since it is what the doctor attests: the free text the doctor wrote,
then one line per entry under a heading ("Diagnoses:", "Vital signs:", "Medicines:", "Lab reports:", "Prescribed
during the stay:"), with status `additional`; a section without free text has one line per entry, `generated`. Every
resource has a generated narrative. A diagnosis's line says when it is provisional, differential, chronic or resolved,
and an allergy's when it is inactive or resolved, its high criticality and its reaction ("Viral fever (differential)",
"Allergy Penicillin (high criticality): Hives (severe)"): in FHIR those statuses change what the resource means, and a
narrative must read safely on its own.

| Resource | Profile | What the IG adds to the read API's resource |
|---|---|---|
| Patient | `Patient` | ABHA number (`https://healthid.ndhm.gov.in`, `xx-xxxx-xxxx-xxxx`) typed `ABHA` from the IG's identifier types; UHID typed MR, and the UHIDs of merged duplicates that records in the document are still kept under, `old`. A patient without an ABHA is still exported, with the UHID |
| Practitioner | `Practitioner` | From the practitioner master: medical-council registration (v2-0203 MD, the council as assigner) and staff code (PRN), name, gender, qualification. No personal phone or e-mail |
| Organization | `Organization` | From the facility master: HFR id (`https://facility.ndhm.gov.in`, PRN), facility code (XX), type `prov`, work phone, e-mail, address. For a test sent out, the referral laboratory from the laboratory's master: its code (`urn:hc360:referral-lab`, XX), name, phone, e-mail, address |
| Encounter | `Encounter` | The visit (with its admission for a stay) or the clinical encounter; a consultation's link to its visit (`partOf`) is left out, as the visit is not in the document |
| Condition | `Condition` | ICD-10 coding only (free-text diagnoses as text) |
| AllergyIntolerance | `AllergyIntolerance` | Allergen as text; allergies recorded in error left out |
| MedicationRequest | `MedicationRequest` | As the read API (medicine as text, dosage with v3-GTSAbbreviation timing codes and their displays) |
| Observation | `ObservationVitalSigns`, `ObservationBodyMeasurement` (weight, height, BMI) or `Observation` (lab results, pain, GCS) | LOINC (and SNOMED CT) codings only, and only with the code system's own display, which the IG requires: a lab result's LOINC coding needs its LOINC name in the test master, else the test goes by its name as text; performer = the laboratory (results; the referral laboratory for a test sent out to one, as the printed report says) or the hospital (vital signs) |
| DiagnosticReport | `DiagnosticReportLab` | Category SNOMED CT 394595002 "Pathology"; LOINC 11502-2; performer and results interpreter = the laboratory; conclusion (the IG requires one; the laboratory writes none) = the results the laboratory flagged ("Flagged by the laboratory: Haemoglobin (Low)."), or "See the individual results." when it flagged none: an unflagged result is not a normal one (a qualitative result, or one without a reference range, is never flagged), so nothing is said about the results |
| DiagnosticReport (radiology) | `DiagnosticReportImaging` | Category SNOMED CT 394914008 "Radiology"; LOINC 18748-4 only (the service code dropped); performer = the hospital; results interpreter = the doctors who wrote it, signed it off and added addenda (else the hospital); `imagingStudy`; issued = the release or the latest addendum after it. The profile requires a key image (`media`): HC360 keeps none (the images stay in PACS), so the one media entry says so in its comment and its link carries data-absent-reason `unsupported`, rather than something that is not a key image |
| DiagnosticReport (cardiology) | `DiagnosticReportImaging` | Category SNOMED CT 394579002 "Cardiology"; the LOINC study only, where LOINC names one (see the cardiology mapping above; the service code dropped); performer = the hospital; results interpreter = the doctors who wrote and signed off the report; no ImagingStudy; the media entry and the measurements in `presentedForm` as for radiology. The measurements are not separate coded Observations: LOINC's codes for the ECG intervals, LVEF and ambulatory BP could not be checked against LOINC when this was built, and an unchecked code is worse than none |
| ImagingStudy | `ImagingStudy` | Accession number (ACSN) and, once PACS linked the images, the study instance UID (`urn:dicom:uid`) with the number of series and images; modality as a DICOM code as the worklist sends it (an X-ray as CR); started; interpreters; body part in the narrative (a series needs PACS's series UID, which HC360 does not keep) |
| DocumentReference, Binary | `DocumentReference`, `Binary` | The printed PDF (made by the same document sources as the download and the messages); when this server cannot make PDFs, the lab report and prescription go without it and the discharge summary carries its text. An amended discharge summary's DocumentReference is authenticated by the doctor who amended it, an author with the signer (by no one when it was not a doctor), as its Composition is attested |

* **Ids** are UUIDs derived from HC360's ids (SHA-256, RFC 9562 version 8), so exporting a record again gives the
  same ids and a patient or doctor has one id in every document.
* **Versions.** A document exported twice is the same, byte for byte, or a new version. `meta.versionId` of the
  Bundle and Composition is the latest change to what the document holds (Unix milliseconds), a hyphen and the first 8
  bytes of a SHA-256 hash of its resources as JSON, the printed PDF included, in hex (`1790306700000-b5fc9c62149f2e9d`
  in the sample prescription). The hash catches what moves no change time forward: an entry that drops out (an allergy
  recorded in error), or a PDF that prints something else (the patient's age on a later day, the DSC signature as it
  checks out then). A PDF is made again at every export, and two PDFs of the same page differ only in what the browser
  numbers as it goes: their creation and modification dates, which are set to the document's date (in UTC), and, in a
  tagged PDF, the ids of table header cells (`(node00000070)`), taken from a counter the browser tab keeps from one PDF
  to the next, which are numbered again from zero in the same order. Each keeps its length, so the file's offsets hold:
  the same page gives the same bytes (shown with PDFs Chromium printed twice in one tab, and on CI's Windows runner,
  where Edge prints the PDFs of `AbdmExportTests`, by exporting a prescription and a lab report twice). A doctor the
  record names but the document leaves out (a consultation's referral doctor) does not count. The Bundle's `timestamp` is its version's time, not the export's, as a document is
  assembled once per version; its id and `identifier` (`urn:ietf:rfc:3986`, `urn:uuid:…`) change with the version,
  the Composition's id does not. Every list is in an order of its own (time, then id), so the order the database
  returns rows in changes nothing. `meta.security` is v3-Confidentiality V.
* **As of the record.** A document holds what its record held when it was completed: signed, released, or amended
  for an amended record (`AbdmAsOf`). An allergy, diagnosis, vital sign, prescription or lab report entered later is
  not in it (a lab report counts from its first release, a result from its test's release); a diagnosis resolved since
  (on a later day, or that day after the time) is active, a prescription cancelled since is signed, a lab report
  completed since is partial with the tests released by then, and a medicine is active or completed as of that day; an
  entry recorded in error since is left out, as never true. These entries carry no `meta.lastUpdated`: their latest
  change may be after the date, and would make a new version of a document that says the same. HC360 keeps no history
  of other changes, which show as they are now and make a new version: an allergy made inactive, a diagnosis
  confirmed, an edited reaction, an amended lab result (its report stays in the summary, amended), the patient's
  details, records moved to the patient by a later merge, and the PDF as the hospital prints it today.
* **Status and attestation.** An amended record is an `amended` Composition. A lab report released before all its
  results is `preliminary` (its Composition and its PDF's DocumentReference), as its DiagnosticReport is `partial`.
  The signing doctor is the author and the legal attester. An amended consultation note or discharge summary has two
  authors, the signer and the doctor who made the latest amendment, and that doctor attests it at the time of the
  amendment; when the amendment was made by a user who is not a doctor, no one is named as its legal attester. A lab
  report has no attester: the laboratory is its author.
* **Not coded:** medicines (no SNOMED CT drug codes), allergens, free-text diagnoses, lab tests without a LOINC code
  and its LOINC name in the test master. They travel as text, which the IG allows. A LOINC name stays only with the
  code it names: the test master's form clears it when the code changes, and the server refuses a changed code sent
  with the old name (400 on `loincName`). None of the seeded tests has one, so out of the box no result is LOINC-coded
  until the laboratory enters the names from the LOINC table.

### Validation

**In CI**, every document is validated with Firely's validator (`Firely.Fhir.Validation.R4`, BSD-3-Clause, test
projects only) against the FHIR R4 core definitions (`Hl7.Fhir.Specification.Data.R4`) and the NRCeS `ndhm.in` 6.5.0
profiles, copied unchanged into `tests/Modules/HyFib.HealthCare360.Modules.Interop.Tests/TestData/ndhm.in-6.5.0` (its
README says where from). Structure, cardinality, slicing, fixed values, invariants, references and the bindings that
can be checked offline (MIME types, languages, UCUM, HL7 code systems) are checked; SNOMED CT and LOINC need a
terminology server, so those checks are skipped and listed (`AbdmProfileValidator`), never passed silently.

* `AbdmDocumentTests`: the six sample documents (consultation, prescription, lab report, radiology report, cardiology report, discharge summary) validate
  with no error and no warning.
* `Abdm/ValidatorSetupTests`: the guide's own discharge summary and prescription examples validate, and fourteen
  deliberately broken documents fail on the rule they break, so a validator that checks nothing cannot pass.
  `AbdmDocumentTests` does the same for the radiology sample against DiagnosticReportImaging and ImagingStudy (seven
  breakages: no media entry, an empty media link, an absent reason the value set lacks, no interpreter, no
  conclusion, the service code kept, a series without its UID).
* `AbdmExportTests` (IntegrationTests): every document downloaded from the endpoints validates.

The five sample documents built by `AbdmDocumentTests` are kept in
`tests/Modules/HyFib.HealthCare360.Modules.Interop.Tests/Fixtures/Abdm` (a test fails when the code builds anything
else; `HC360_WRITE_ABDM_FIXTURES=1` writes them again, after which they are validated again). They were checked on
2026-10-16, and again after each review's fixes (2026-10-20 and 2026-10-21) with the same results, with the official
**HL7 FHIR validator** (`validator_cli.jar` 7.0.2) against `ndhm.in#6.5.0`:

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
| `radiology-report.json` (2026-10-23) | 6 | 4 | `Bundle-DiagnosticReport-Imaging-DCM-example-01`: 11 errors, 12 warnings; `…-Imaging-Media-example-02`: 11 errors, 8 warnings |

* **Every error is the MIME type of an attachment** (`application/pdf`, `text/plain; charset=utf-8`): the HL7 validator
  checks its required binding to the MimeType value set only with a terminology server, and the section entry holding
  that Binary or DocumentReference then "matches no profile". The IG's own examples fail in exactly the same way
  offline, and Firely, which checks MIME types offline, finds no error. Nothing else is an error.
* The warnings are codes that cannot be checked without a terminology server (UCUM units, BCP-47 languages, the
  DICOM modality value set an ImagingStudy binds), and the
  visit number's identifier type (v2-0203 `VN`), which is not in the R4 IdentifierType value set (an extensible
  binding, so a warning).
* To be done where tx.fhir.org is reachable: run the validator without `-tx n/a`, expecting 0 errors.

### ABDM gateway

Sending records to ABDM makes the hospital a Health Information Provider (HIP) on NHA's gateway (the HIE-CM). The
paths, headers and bodies follow NHA's reference HIP, `NHA-ABDM/ABDM-wrapper` (its `application-v3.properties` and v3
request classes) and the gateway specification in it, as ABDM's own sandbox documentation is not reachable from the
build. What is built:

| | |
|---|---|
| Settings | `abdm.gateway` (hospital-wide JSON): `enabled` (false), `baseUrl` (https, no user name, e.g. the sandbox `https://dev.abdm.gov.in/api/hiecm`), `hipId` (the HFR id registered as HIP), `cmId` (`sbx` or `abdm`), `timeoutSeconds` (30, between 5 and 120), `jwksUrl` (optional, https: where the gateway publishes the keys its callbacks are signed with; by default `{baseUrl}/gateway/v3/certs`, the v3 place of the specification's `/v0.5/certs`, to be confirmed in the sandbox). `abdm.gateway.secret` (encrypted secret setting): `clientId`, `clientSecret` issued by NHA. Both are added under Administration › Settings (Add setting); the secret is never shown again |
| Status | `GET /api/v1/abdm/gateway`: enabled, configured, `live` (false until records are sent, not only linked), the URL and ids, whether the client id and secret are set (never their values), the problems in the set-up and what remains |
| Client | `IAbdmGatewayClient` (gateway v3), registered as `HttpAbdmGatewayClient`: `CreateSessionAsync` (`POST /gateway/v3/sessions` with the client id and secret, `grantType: client_credentials`; the access token is kept and shared until five minutes before it expires, twenty minutes in the sandbox), `RequestLinkTokenAsync` (`POST /v3/token/generate-token`: ABHA address, name, gender `M`/`F`/`O`/`U`, year of birth), `LinkCareContextsAsync` (`POST /hip/v3/link/carecontext` with the token in `X-LINK-TOKEN`: the ABHA number and address and the care contexts, one group per health-information type: `OPConsultation`, `Prescription`, `DischargeSummary`, `DiagnosticReport` for lab, radiology and cardiology reports). Every call carries `Authorization: Bearer`, `X-CM-ID`, `REQUEST-ID`, `TIMESTAMP` (UTC, milliseconds) and `X-HIP-ID`. The gateway accepts with 202 and answers later on a callback; any other answer is refused with 422 `abdm.gateway_refused` and the gateway's code and message (it sends `{ error }` or `{ errors: [{ error }] }`, both read), and a gateway that cannot be reached within the timeout gives 422 `abdm.gateway_unreachable`. A call is not retried, as the gateway may have acted on it when its answer was lost; a session the gateway ended (401) is renewed once. `AcknowledgeConsentAsync` and `TransferHealthInformationAsync` are not built and are refused as before. While the gateway is off or its set-up incomplete nothing is sent and every call answers 422 `abdm.gateway_disabled`, naming what is missing (`DisabledAbdmGatewayClient`) |
| Encryption | `FideliusCipher` (`IFideliusCipher`): every document of a transfer encrypted for the HIU under one fresh HIP key pair, with each document's MD5 as the entry's checksum. Built and tested against NHA's reference implementation, see [Fidelius](#fidelius) |
| Callback check | `AbdmCallbackVerifier`: a call to the callback endpoints is the gateway's only when its `Authorization: Bearer` token is signed RS256 by a key in the gateway's key set (JWKS) whose key id the token names, and is within its lifetime (one minute's clock difference allowed). `none`, HMAC (including the HS256-with-the-public-key confusion) and any other algorithm are refused. A token naming a key id not in the set is refused, so the client fetches the set again (the gateway rotated its keys) rather than trying every key. Issuer and audience are checked once confirmed against the sandbox: ABDM publishes neither, nor the key set's URL. The key set is fetched from `jwksUrl` and kept six hours (`AbdmGatewayKeys`); a token naming a key not in it has it fetched again, at most once a minute. NHA's own reference HIP (`NHA-ABDM/ABDM-wrapper`) checks nothing, so anyone reaching its callback URLs could post a consent notification. `AbdmCallbackVerifierTests` holds a key set and tokens made with Python's jwcrypto 1.5.6, independent of .NET; they found two faults while this was built (a token naming an unknown key accepted by trying every key, and the lifetime check overflowing on the library's minimum date) |

#### Linking records to the patient's ABHA (ABDM M2)

HIP-initiated linking: records the hospital completed become care contexts on the patient's ABHA, which the patient
sees in their PHR app and can later consent to share (`AbdmLinkingService`).

1. `POST /api/v1/abdm/links` names records of one patient. Each must be one the export would send (signed, released,
   an issued prescription; 422 `abdm.not_exportable` otherwise) and one the caller may open (a restricted record needs
   the permission or break-the-glass, as for the export); all must be the same patient's (422 `abdm.link_patients`),
   who is not merged into another UHID (`abdm.link_merged`) and has an ABHA address (`abdm.no_abha_address`) and a date
   of birth (`abdm.no_year_of_birth`). Each record is a care context with reference `{kind}/{record id}`
   (`op-consultation/…`, `prescription/…`, `lab-report/…`), stable and unique per patient, and display
   "Consultation note, 12 Oct 2026"; the patient's reference is their HC360 patient id. Audited as `phi` /
   `abdm.link_requested`, "Records linked to the patient's ABHA: 2", entity `abdm.link`, details `{ api, records }`.
2. With a link token kept for the ABHA address the care contexts are sent at once (`Sent`). Otherwise a token is asked
   for (`AwaitingToken`), and the care contexts go when the gateway's `on-generate-token` brings it. Each step is saved
   before its call is made, so an answer that arrives first finds its request.
3. The gateway's `on_carecontext` says whether they were linked (`Linked`) or why not (`Failed`, with its code and
   message). A refusal, then or when the care contexts are sent, drops the kept token, so the next request asks for a
   new one (it may have expired or been revoked); a gateway that could not be reached keeps it.

The token is kept per HIP id and ABHA address, encrypted like other secrets in the database (`IFieldProtector`), and
never returned by the API. The request is answered 202 also when the gateway refused it at once or could not be
reached: its view then says `Failed` and why. A failed request is not retried by HC360: staff link the records again.

The gateway calls back on two endpoints, at the paths NHA's wrapper serves (`AbdmGatewayCallbackEndpoints`):

| Method | Path | Body |
|---|---|---|
| POST | `/api/v3/hip/token/on-generate-token` | `{ abhaAddress, linkToken, response: { requestId }, error }` |
| POST | `/api/v3/link/on_carecontext` | `{ abhaAddress, status, response: { requestId }, error }` |

They are anonymous at the HTTP level and checked by the gateway's signed bearer token ([Callback check](#abdm-gateway)):
401 when it is missing, forged or expired. They answer 503 `feature.disabled` while the ABDM switch is off, 404 while
the gateway is off or not set up, 422 for a body that is not the expected JSON, and 202 otherwise, also for an answer to no request of this hospital's,
which is logged and ignored. Bodies are limited to 64 KB and requests to the public rate limit per client address (60
a minute by default, `HealthCare360:RateLimits:Public`), which a busy hospital may need to raise for the gateway's
addresses. The hospital's public address for these paths is registered with NHA as the HIP's callback URL.

`HttpAbdmGatewayClientTests` checks each call against NHA's wrapper (paths, headers, bodies, the session's reuse and
renewal, both error shapes, no retry); `AbdmLinkingTests` runs the whole flow against a fake gateway inside the test
server (`FakeAbdmGateway`: sessions, 202s, a key set, and callbacks signed with its key). Neither has been run against
NHA's sandbox, which needs the hospital's credentials.

What remains, in order (also in the status's `remaining`):

1. Register the hospital as a HIP with its HFR id and get NHA **sandbox** client credentials (an outside approval);
   run the linking flow above in the sandbox and confirm the key set's URL and the token's issuer.
2. Build the other callback endpoints the gateway calls (user-initiated discovery, link initiation and confirmation,
   consent notification, health-information request), and link care contexts when a record is completed.
3. Pass NHA's sandbox functional tests and certification, then switch to the production gateway.
4. Sign documents (`Bundle.signature`, optional in the IG) with the doctor's DSC when the DSC signing of prescriptions
   (ERX-04) covers them.

### Fidelius

ABDM encrypts health information between the HIP and the HIU with Fidelius, as NHA's reference implementation
(`mgrmtech/fidelius-cli`, BouncyCastle) does it; the gateway's other parties run that code, so HC360 follows it rather
than a description of it (`Abdm/Fidelius.cs`):

* **ECDH on BouncyCastle's `curve25519`**: Curve25519 in short Weierstrass form (Wei25519), **not** RFC 7748 X25519,
  which would derive a different secret. Private keys are plain scalars, written by BouncyCastle as signed integers
  (31 to 33 bytes). Public keys are uncompressed points, raw (65 bytes, 88 base64 characters) or as an X.509
  SubjectPublicKeyInfo with the curve's parameters spelled out; HC360 reads both and sends the X.509 form, as the
  reference's HIUs do. A key in any other form, off the curve, or a nonce that is not 32 bytes is refused
  (`abdm.fidelius`).
* The shared secret is the x-coordinate (32 bytes). The two nonces are XORed: the first 20 bytes salt HKDF-SHA256
  (no info) for the 32-byte AES key, the last 12 are the AES-GCM IV. The 128-bit tag follows the ciphertext, base64.
* The document is encrypted as UTF-8. The reference encrypts with the JVM's default charset, UTF-8 from Java 18 on but
  not always before; a HIU on an older JVM may misread non-English names.

`FideliusTests` holds key material and a ciphertext made by the reference CLI 1.2.0: the same inputs give its
ciphertext byte for byte (with the HIU's key raw or X.509), its ciphertext decrypts here with Tamil text intact, and a
fresh HC360 key pair is X.509-encoded as the reference encodes it. In the other direction, a transfer encrypted here
was decrypted by the reference CLI when this was built (a one-off check, as it needs Java).

## Limitations and follow-ups

* **EHR-08 is partly met.** ABHA capture and the ABDM document export are built (behind `features.abdm`, off by
  default), and so is HIP-initiated linking of records to the patient's ABHA through the gateway, which needs the
  hospital's NHA credentials and a sandbox run. Not met: verifying the ABHA with ABDM, user-initiated linking,
  consent and the encrypted transfer of records ([ABDM gateway](#abdm-gateway)); and cardiology reports (ECG,
  echo, TMT, Holter, ABPM: their own studies, with no imaging study) are not exported as ABDM documents, nor are immunisations, wellness and
  invoice records. The FHIR read API's resources keep the ABDM-friendly identifiers (ABHA number, HFR id) but claim no
  NRCeS profiles; the ABDM documents do.
* Doctors' Healthcare Professionals Registry (HPR) ids are not recorded: ABDM documents name a doctor by medical
  council registration number and HC360 code until they are.
* An ABDM download is in the patient's record access log (`patient.accessed`), but the privacy data summary does not
  list its recipient, as HC360 does not know who receives the file.
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
