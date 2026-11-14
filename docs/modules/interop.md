# Interop module (`interop`) — HL7 FHIR R4 read API and ABDM health records

Read-only HL7 FHIR R4 API that lets other providers read a patient's record, **subject to the patient's consent**
(SRS **EHR-05**): Patient, Encounter, Observation (the clinic's vital signs, lab results and the readings of the patient's
home-monitoring devices), Condition, AllergyIntolerance, MedicationRequest, DiagnosticReport, DocumentReference,
Immunization and Procedure under `/fhir/r4`, with the hospital's own directory, Practitioner, Organization and Location,
which is no patient's and needs no consent ([Practitioner, Organization and Location](#practitioner-organization-and-location)). It is built with the
Firely .NET SDK (`Hl7.Fhir.R4` 6.6.0), returns JSON only and writes nothing.

It also exports discharge summaries, lab reports, radiology and cardiology reports, consultation notes, prescriptions, the vaccine doses given here and finalised bills as **ABDM health records**
(SRS **EHR-08**): FHIR documents of the NRCeS implementation guide for the Ayushman Bharat Digital Mission, carrying the
patient's ABHA number, behind the `features.abdm` switch, which is **off** until an administrator turns it on
([ABDM health records](#abdm-health-records-ehr-08)). Completed records can be linked to the patient's ABHA through the
ABDM gateway (HIP-initiated and user-initiated linking, ABDM M2) once the hospital has NHA credentials, and sent,
encrypted, to another provider the patient consented to in their PHR app; and a patient who scans the hospital's QR
code with their PHR app joins the registration desk's queue with their ABHA profile (scan and share, ABDM M1), and
the desk can check a patient's ABHA with ABDM by a code sent to the mobile number linked to it; and a doctor can ask a
patient, through ABDM, to let the hospital see their records at other providers, and read them when they arrive,
encrypted for the hospital alone and erased at the consent's erase date (the hospital as HIU, ABDM M3)
([ABDM gateway](#abdm-gateway)).

| | |
|---|---|
| Project | `src/server/Modules/HyFib.HealthCare360.Modules.Interop` (layer 8; references Patients, Clinical, Diagnostics, Inpatient, VirtualCare and Billing, layer 7, whose bills it exports as ABDM invoice records) |
| Schema | `interop`: `abdm_link_requests` (a request to link records to a patient's ABHA and where it has got to) `abdm_link_tokens` (the link token the gateway issued for an ABHA address, encrypted), `abdm_discoveries` (a patient's search for their records from their PHR app, and the code sent to them, hashed) `abdm_care_contexts` (every record linked to an ABHA address, whichever way), `abdm_consents` (the consents ABDM notified for this hospital's records), `abdm_transfers` (each request for records under a consent, where its push went and what was delivered) and `abdm_profile_shares` (ABHA profiles shared at a counter by scan and share, with the token given, the ABHA number encrypted; kept a week) `abdm_abha_verifications` (checks of a patient's ABHA with ABDM: the ABHA number masked, its address and what differed from the record), `abdm_consent_requests` (the hospital's requests for a patient's records at other providers, as HIU), `abdm_consent_artefacts` (the consents a patient granted: each provider and its records), `abdm_health_information_requests` (each request for the records under a consent, its transaction and its own key pair, the private key encrypted and dropped when the transfer ends) and `abdm_received_records` (the records received, decrypted, until the consent's erase date); migrations `AbdmLinking`, `AbdmUserLinking`, `AbdmDataFlow`, `AbdmProfileShares`, `AbhaVerifications`, `AbdmConsentRequests` and `AbdmHealthInformation`. Everything else is read from the other modules' entities through `HospitalDbContext` |
| Requirements | EHR-05 (with EHR-06 consent and EHR-07 restricted records); EHR-08, **partly met**: ABHA capture and the ABDM document export of consultations, prescriptions, lab reports, radiology and cardiology reports, discharge summaries, vaccine doses given here (immunisation records), signed clinical notes with their addenda (health documents) and the readings patients take at home in a remote monitoring programme (wellness records, a document for each month) and finalised bills (invoice records, not yet validated against the guide's InvoiceRecord profile) are built, and so is linking completed records to the patient's ABHA through the ABDM gateway (HIP-initiated linking, and user-initiated linking from the patient's PHR app), and so are the consent notification and the encrypted transfer of consented records to the HIU, and scan and share and checking a patient's ABHA with ABDM at the registration desk (ABDM M1), and asking a patient for their records at other providers, receiving their consents and then the records, decrypted and read by the doctor, and erased at the consent's erase date or when it is revoked (ABDM M3; tested against a fake gateway built to NHA's wrapper; they need the hospital's NHA credentials and a sandbox run); creating an ABHA for a patient is not built |
| Tests | `tests/Modules/HyFib.HealthCare360.Modules.Interop.Tests`: every mapper (`ClinicalNoteDocumentTests`: each kind of note under LOINC's document type, checked against FHIR R4's Document Type value set in the specification; the note as signed; who wrote and signed it and where; addenda; the restricted label on counselling notes; every kind against the ABDM guide's DocumentReference profile), the vital-sign codes and BP components, lab values and ranges, missing data, the consent and sharing rules, the SQL patient search, search parsing (`_count` cap, dates, tokens, strict parameters, `_sort`), in-memory search, ordering and paging links, OperationOutcomes, the CapabilityStatement, FHIR JSON round trips, the FHIR 401/403 handler and the SQL translation of every query (Npgsql, no database); ABDM: `AbdmDocumentTests` (each record type against the IG, ids, references, versions, narratives, statuses and attesters, referral laboratories, the validated samples in `Fixtures/Abdm`), `AbdmImmunizationRecordTests` (a dose given here with the next dose due, without one, by name alone, without an encounter; which doses are exported; a dose's name as a care context; every kind of record with its own reference, name and HI type, and an unknown kind refused; a consent's kinds of record), `AbdmClinicalNoteRecordTests` (a signed note as a health document, its addenda in the order signed with an addendum to an addendum, a note or addendum by staff who are not doctors, no encounter, a note written in a stay, the file name of each kind; which notes are exported; a note's name as a care context, never its title; its HI type and consent), `AbdmHomeReadingsRecordTests` (a month of home readings as a wellness record, each in the guide's section for it with the profile the section asks for, a blood pressure taken with a pulse as one panel; each section's narrative the readings with their times in IST, how each stood and who entered one by hand; the order measured whatever order the rows came in; a month bounded by midnight in India; the dates a consent asks for cutting a month, inclusive, and a cut month a document of its own that says what it covers; months as documents of their own; a month of more readings than one document holds in parts, never parting a blood pressure; the programme's name as a care context, by its kind, never its name; the HI type and consent), `AbdmInvoiceRecordTests` (a finalised bill as an invoice record of the facility that issued it with the printed bill; every line's price components and the totals adding up to the bill's, round-off included; CGST and SGST at half the rate, IGST on a supply to another state, none on exempt services; a bill free of cost by law adding up to nothing without its reason; issued until the patient's share is paid and the payer owes nothing; a draft and a cancelled or refunded bill refused; its HI type, reference, name as a care context and consent; base FHIR R4 enforced and the two profiles the package lacks reported unchecked), `AbdmDocumentVersionTests` (a document as of its record, its PDF made again, the order of rows, the PDF rule), `ValidatorSetupTests` (`Abdm/`: the Firely validator set-up, see [Validation](#validation)), `AbdmGatewayTests`, `HttpAbdmGatewayClientTests` (`Abdm/`: the gateway calls against a fake gateway, see [Linking](#linking-records-to-the-patients-abha-abdm-m2)) and `AbdmCallbackVerifierTests`. Elsewhere: `HealthIdTests` (Verhoeff for Aadhaar, ABHA number incl. the IG's example ids, ABHA address, Platform.Tests), `AbhaValidationTests` (Patients.Tests), `AbdmEndpointTests` (ArchitectureTests: switch, permissions, the one write, the anonymous signed callbacks), `AbdmLinkingTests` (IntegrationTests: a record linked end to end through a fake gateway and its signed callbacks, a kept token reused and a refused one dropped, forged and unsigned callbacks refused, nothing sent while the gateway is off, requests made at once for one record sending it once), `AbdmUserLinkingTests` (IntegrationTests: a patient finds their records by ABHA address or verified mobile, links one with the code sent to them; wrong, spent and re-asked codes, a record not offered, three wrong codes), `AbdmDemographicsTests` (Interop.Tests: the gender, year-of-birth and name match), `AbdmDataFlowTests` (IntegrationTests: a consent for records not linked here refused, a granted consent kept, a private push URL refused, the consented record encrypted, pushed and decrypted with the HIU's key into a FHIR document, the gateway told it was delivered, a revoked consent refusing the next request; a vaccine record not sent under a consent for prescriptions and sent, as an immunisation record, under one for immunisation records; a clinical note not sent under a consent for consultations and sent, with its addendum, as a health document under one for health documents; home readings not sent under a consent for consultations and sent under one for wellness records as a document for each month of the dates asked for, only those measured in them, and dates in which nothing was measured answered so; a bill not sent under a consent for consultations and sent, as an invoice record, under one for invoices; a programme whose months fill two
pages, the first refused and the second accepted, reported errored, not delivered), `AbdmDataPushTests` (Interop.Tests: the push URLs allowed and the gateway's times read), `AbdmConsentRequestTests` (IntegrationTests: only doctors ask, a patient without an ABHA address cannot be asked, the request sent as the HIU with the purpose, kinds, dates and the doctor's registration, on-init, a notification for another HIU ignored, two artefacts granted, acknowledged and fetched, one arriving with its provider and records, one naming another artefact ignored; its records asked for with a fresh key pair, a page refused before the transaction is named, two pages pushed encrypted for that key with a record for a visit not consented and one altered on the way, the consent manager told which arrived, the readable one opened by the doctor and not by front office; a revocation erasing what was received; records past their erase date deleted with their keys and others kept), staff `modules/patients/AbdmConsentRequestsPanel.test.tsx` and `receivedRecords.test.ts` (what a doctor reads of a received bundle), `AbhaVerificationTests` (IntegrationTests: against a fake ABHA service that decrypts what HC360 sends with its private key: the code requested for the ABHA number on record, a wrong code counted, the right one compared with the record, a mismatch listed, three wrong codes, the fourth code refused, only the identity permission starts one), `AbhaClientTests` (Interop.Tests: RSA-OAEP with SHA-1, ABDM's key read from JSON, base64, PEM or a certificate, the date of birth's year), staff `modules/patients/AbhaChecksPanel.test.tsx`, `AbdmProfileShareTests` (IntegrationTests: a share answered with a token, the same token when scanned again, the next for someone else, the match by ABHA address, the queue masked, the share in full, registered or dismissed once, a share without an address answered with an error, an unsigned one refused), staff `modules/front-office/abhaShares.test.tsx` (the form filled in from a share, the queue, done, take off, no Register without the contact permission), `AbdmExportTests` (IntegrationTests: registration, export, who may export, refusals audited, amended notes, records under a merged duplicate (a restricted one at either end of the merge, in a discharge summary, the survivor deleted), an ABHA number a merge links, a later allergy left out of a signed note, the LOINC name rule, the download's file and the DSC rule, every downloaded document validated; a vaccine dose given here exported with the next dose due, from the facility that recorded it, one from the card or in error refused, one in another patient's encounter refused when it is recorded; a signed note exported with its two signed addenda, one kept by another facility, and not a draft addendum; a counselling note, a draft and an addendum on its own refused; the note offered for linking by its kind; an enrolment's home readings exported a month at a time, the month of the latest reading unless one is named, a month with none and one that is not a month refused, the same month the same document, the programme offered for linking by its kind), `AbdmInvoiceExportTests` (IntegrationTests: a finalised bill downloaded by the medical records department and the hospital administrator, the same document twice, its lines and totals the bill's, audited; a draft and a cancelled bill refused, saying why; the billing desk and a doctor refused; offered for linking by its number, not the draft or the cancelled one; a paid bill balanced; nothing while ABDM is off), `LabTestLoincTests` (Diagnostics.Tests: a LOINC name only with its code), staff `modules/patients/validation.test.ts`, `modules/emr/AbdmRecordButton.test.tsx`, `modules/emr/abdmRecords.test.tsx`, `modules/emr/ImmunizationAbdm.test.tsx` (the dose's ABDM record downloaded and linked from its row, for a dose given here only), `modules/emr/NotesAbdm.test.tsx` (a signed note's ABDM record downloaded and linked from the note, not an addendum's, a counselling note's or a draft's), `modules/virtualcare/rpm/AbdmReadings.test.tsx` (an enrolment's months, a month's ABDM record downloaded and the programme linked from the enrolment, not while ABDM is off); `ClinicalNoteSharingTests` (IntegrationTests: a partner reads the signed notes and the addendum a consent for clinical notes covers, never a draft or the counselling note; a consent for the whole record withholds the counselling note, one naming counselling notes shares it labelled restricted; a note naming another patient's stay refused), staff `modules/patients/consents.test.ts` (what a consent shares, counselling notes only when named); `DeviceReadingObservationsTests` (Interop.Tests: each kind of home reading coded as the clinic's vital signs are, a blood pressure taken together as one panel and one taken alone, how each stood against the patient's limits, the device and who entered a reading by hand; every kind validated against the FHIR R4 vital-signs profiles and ABDM's; HC360's own device intake reading each one back as the reading it was; the reading types and times a search reaches; and the query's consent rule checked against the rule the rest of the record is shared by, at the edges of a day in hospital time), `DeviceReadingSharingTests` (IntegrationTests: readings a device pushed and a nurse entered, shared kind by kind; the days a consent names; a search past 5,000 readings refused unless narrowed, never on readings the consent leaves out; a merge taking the monitoring records along), `FhirObservationParserTests` (VirtualCare.Tests: ABDM's own heart rate example read by its UCUM code), `RemovedRecordSharingTests` (IntegrationTests: a document front office removes is no longer listed) |

## Permission and set-up

| Permission | Purpose | Default roles |
|---|---|---|
| `fhir.read` | Read and search patient records through the FHIR API (what the patient's record-sharing consents let the account read; every access is audited) | HospitalAdmin |
| `abdm.records.request` | Ask a patient, through ABDM, to let the hospital see their records at other providers, in their name as the requesting doctor (every request is audited) | Doctor |
| `fhir.clients.manage` | Register partner applications for SMART on FHIR (a second holder approves) and suspend them | HospitalAdmin |
| `abdm.export` | Export discharge summaries, lab reports, radiology and cardiology reports, consultation notes, prescriptions and vaccine doses given here as ABDM health records (every export is audited). Staff only: it does not check the record-sharing consent, so never give it to an integration service account | HospitalAdmin, MrdOfficer |

Integration partners (a referral hospital, a health information exchange) get their own service user:

1. Create a role such as "FHIR integration" with `fhir.read`. Add `patients.restricted.read` only if restricted (VIP)
   records may also be shared. Never add `abdm.export`: the ABDM export is a staff download that does not check the
   patient's record-sharing consent ([ABDM health records](#abdm-health-records-ehr-08)). The accounts of a role the
   hospital made are the ones a patient's consent can name (*Who may read*); the built-in roles that hold `fhir.read`,
   such as the hospital administrator's, are staff and are not offered.
2. Create a user for the partner, give it only that role, and sign it in with `POST /api/v1/auth/login` (and
   `/api/v1/auth/refresh`). Access tokens live 10 minutes; send them as `Authorization: Bearer <token>`.
3. Assign the user to **one** facility: the partner reads that facility's records and patients only (#251, decided
   by the hospital), whatever a patient's consent says. A request from an account assigned to no facility, or from a
   session at a facility it is not assigned to, is refused with 403 `facility-required` before anything is looked up
   (an account assigned to none may sign in at any facility, so it is no facility's partner). What "the facility's"
   means for each kind of record:
   * visits, encounters, orders, lab results and reports, imaging and cardiology reports, prescriptions, discharge
     summaries, clinical notes, procedures, home readings, wards, rooms and beds: recorded there (platform facility
     filter);
   * vital signs: taken there (the facility of the person who took them), or, for a set recorded without one, taken in
     one of its encounters;
   * conditions: diagnosed in one of its encounters;
   * vaccine doses: recorded there, given there or copied from a card there;
   * documents: uploaded there (the facility of the staff who stored the file);
   * patients: registered there, or with a visit, an encounter or a stay there; another facility's patient is not
     found (404) and no search lists them;
   * allergies are the patient's, not a facility's, and are read whole.

   Staff see a patient's clinical notes, procedures, vital signs, diagnoses and doses from every facility; the ABDM
   export, a staff download for the patient's own locker, is not limited this way.
4. The partner states why it reads in `X-Purpose-Of-Use` with each request (for example `TREAT`), as patients'
   consents may allow some purposes only ([Consent and access rules](#consent-and-access-rules)).

### SMART on FHIR (partner applications)

Partners read with SMART on FHIR access tokens (App Launch 2.0 and Backend Services), discovered at
`/fhir/r4/.well-known/smart-configuration` and in the CapabilityStatement (`rest.security.service` SMART-on-FHIR and
the `oauth-uris` extension).

* **Registration** (Administration, `/api/v1/smart/clients`, `fhir.clients.manage`): one administrator registers a
  client, a second approves it (`interop.smart_client`; the one who registered it cannot), and only then is it issued
  tokens. An **app** has exact redirect URIs (https, or http on 127.0.0.1/[::1] for a native app) and `patient/` or
  `user/` read scopes; a **backend service** has its public keys (a JSON Web Key Set: RSA 2048+ or EC P-256/P-384, no
  private parts), `system/` read scopes, the integration account it reads as and that account's facility. Suspending a
  client revokes all its tokens at once.
* **Apps** (`GET /oauth2/authorize`): authorization code with PKCE (S256 only). The request is checked (client, exact
  redirect URI, `aud`, scopes) and handed to the staff app's **Authorize an app** screen, where a staff member who holds
  `fhir.read` and is assigned to the facility signs in, sees what is asked, names the patient by UHID for a `patient/`
  grant, and allows or denies. The code is single use for ten minutes; presenting it twice revokes the token it bought.
  The token reads as that staff member, at their facility.
* **Backend services** (`POST /oauth2/token`, `grant_type=client_credentials`): a JWT client assertion signed by a
  registered key (RS384, ES384, RS256 or ES256), `iss` = `sub` = the client id, `aud` the token endpoint, expiring within
  five minutes, each `jti` accepted once. The token reads as the client's integration account at its facility.
* **Scopes**: SMART v2 (`patient/*.rs`, `user/Observation.r`…) and v1 `.read`, read and search only; others asked
  for are dropped and the granted scopes are said back. A token reads only the types its scopes name (403
  `scope-insufficient` otherwise); a `patient/` token only its patient's record: reads of another patient's records and
  searches without that patient are refused, and a Patient search must be `_id` of that patient.
* **Consent still applies on every read**: a token signs the request in as its account, so the account's
  `fhir.read`, its facility and each patient's record-sharing consent (who may read, purpose, kinds, dates, until)
  decide exactly as for a session; scopes only narrow.
* **Where**: on the staff app's address only, never the patient portal's: an app's authorization signs a staff member in
  on the staff app, and the FHIR API reads as a staff account. A partner outside the hospital connects over a VPN.
* **Tokens**: opaque, 15 minutes, stored as SHA-256, accepted by the FHIR API only. `POST /oauth2/introspect` (RFC 7662)
  and `POST /oauth2/revoke` (RFC 7009) answer the client the token was issued to (an assertion, or a public app's
  `client_id` for revocation). No refresh tokens are issued: an app asks again, a service mints a new assertion.
* **Migration**: partners on a password-signed-in service user keep working. Registering a backend service with
  *replaces password sign-in* makes the FHIR API refuse that account's own sessions (403 `smart-required`) once the
  client is active.
* Audit: `smart.client_registered`, `smart.client_approved`, `smart.client_suspended`, `smart.app_authorized`,
  `smart.token_issued`, `smart.token_refused`, beside the FHIR read audit, which names the account as before.
* Tests: `SmartOnFhirTests` (IntegrationTests), `SmartScopeTests` (Interop.Tests), staff `modules/interop/smartAuthorize.test.tsx`.

## Consent and access rules

Every read and search passes the same gate for the patient the data belongs to:

| Rule | Read (`GET {type}/{id}`) | Search |
|---|---|---|
| Patient does not exist | 404 | patient left out |
| Patient neither registered nor ever seen (a visit, an encounter or a stay) at the reader's facility | 404 | patient left out |
| Patient record deleted | 410 `deleted` | patient left out |
| Patient merged into another UHID | 410 `patient-merged` (names the surviving `Patient/{id}` when the caller may read it) | patient left out |
| No record-sharing consent admits the reader today, for the purpose they stated | **403 `consent-required`** | **patient left out** |
| The consents that admit the reader do not cover the record (its kind or its date) | **403 `consent-required`** | **record left out**, with a `records-withheld` outcome |
| Restricted (VIP) record the caller may not open | 403 `restricted-record` | patient left out |

* **Consent** is the Patients module's `PatientConsent` of type **`DataSharing`** (captured under EHR-06; the existing
  type, so no new consent type was added). A patient's record is shared under each granted, unrevoked `DataSharing`
  consent that no later recorded refusal supersedes, the same rule Engagement applies to marketing consent.
  Withdrawing a consent (revoking it, or recording a refusal) stops sharing at once.
* **What a consent covers** (`RecordSharingConsent`, `SharingScope`). Each consent is one permission, as a FHIR
  `Consent.provision` or an ABDM consent artefact is, and may be limited when it is recorded
  ([clinical.md](clinical.md#consents-ehr-06)):

  | Part | Admits or covers | Left open |
  |---|---|---|
  | Who may read (`shareWith`) | only the named accounts | every account with `fhir.read` |
  | For what (`purposes`) | only a reader who states one of them in `X-Purpose-Of-Use` | any purpose, or none stated |
  | Which records (`kinds`) | Encounter and Condition (*visits*), AllergyIntolerance, MedicationRequest, laboratory Observations and lab DiagnosticReports (*lab results*, a glucose by glucometer at home included), imaging and cardiology DiagnosticReports and an ECG patch's rhythm (*scans and heart tests*), vital-signs Observations (vital signs, pain score, coma scale, those measured at home), DocumentReference of documents and discharge summaries (*documents*), Immunization (*vaccines given*), DocumentReference of signed progress, procedure, referral and other notes (*clinical notes*), DocumentReference of signed counselling notes (*counselling notes*), Procedure (*procedures*, operations included) | every kind but counselling notes |
  | Records from / up to | records whose day (hospital time) is within: the visit's start, the diagnosis or allergy's recording, the prescription, the sample's collection or the study, the document (a note: the day it was signed), the day a vaccine was given, the day a reading was measured, the day a procedure was performed | every date; a record without a date is left out once dates are set |
  | Until | the consent admits nobody after its last day, without anyone withdrawing it | no end |

  A reader may read what any consent that admits them covers. The patient's own details (Patient) go with any consent
  that admits the reader: they say whose records they are. A consent recorded without a scope shares the whole
  record with every account, as every consent did before scopes, but for counselling notes.
* **Counselling notes are shared only under a consent that names them** (`SharedRecordKinds.NamedOnly`). A
  counselling note holds what the patient said in confidence, about an HIV test (HIV and AIDS (Prevention and Control)
  Act 2017, s.8), their mental health (Mental Healthcare Act 2017, s.23) or their family, so a consent to share "all
  records", or one naming other kinds, does not reach it. The kind is read from the note's LOINC type (47042-7), so a
  counselling note is never shared as a milder kind. One that is shared carries the confidentiality label `R`
  (restricted) for the receiving system to honour. Of the consents recorded before notes were served, one that names
  no kinds (the whole record) now shares the clinical notes too, as part of the record; one that names kinds shares no
  notes; and none shares a counselling note.
* **The purpose of use** is stated with each request in `X-Purpose-Of-Use`, an HL7 v3 ActReason code: `TREAT`,
  `ETREAT`, `CAREMGT`, `HPAYMT`, `PUBHLTH`, `HRESCH` or `PATRQT` (the codes ABDM consents use too). Any other value is
  refused with 400 `invalid-parameter`. A consent that names purposes admits no request without one. The stated
  purpose is in the audit entry of every read, search and refusal.
* **Restricted records** use the platform guard `PatientAccessGuard` (EHR-07): reads and patient-scoped searches need
  `patients.restricted.read` or an active break-the-glass grant. Patient searches include restricted patients only
  for callers with `patients.restricted.read` (a break-the-glass grant opens one record, not a search).
* Consent comes first: a record without consent is never opened, not even by break-the-glass.
* A patient-scoped search whose patient is left out returns an empty `searchset` with one `outcome` entry
  (information, `suppressed`, `records-withheld`); so does one that leaves out some of the patient's records the
  consents do not cover, beside the records it returns. Its wording ("Records of this patient may have been left
  out") is the same whatever the reason, so it reveals nothing about the patient, and it gives no count.
* A search lists only what a read by id would return: each provider reads its records with the platform's query
  filters on (removed rows left out; for a facility's records, other facilities' rows too), and the lookups that must
  see past a filter (an account since removed, a test since retired from the test master, a stored file, a device moved
  to another facility, an encounter in another facility) are made in queries of their own. A query that ignores a
  filter for one table it reads ignores it for every table it reads (EF Core's `IgnoreQueryFilters` is query-wide), so
  such a lookup inside a record's query once let a search list a document front office had removed, and a clinical
  note of another facility. `QueryTranslationTests` checks each provider query keeps its own filters
  (`RemovedRecordSharingTests` end to end).
* Only complete records are shared: released lab results and reports, released radiology and cardiology reports,
  signed prescriptions (and signed ones cancelled later), signed or amended discharge summaries, signed clinical notes
  and their signed addenda. Drafts, unverified
  and unreleased work never leave the hospital. Identity proofs and insurance cards are never shared.
* Deactivated (not merged) patients stay readable by id (`active: false`) but are not returned by Patient searches.
* ABDM health records pass the same gate, with answers worded for staff
  ([ABDM health records](#abdm-health-records-ehr-08)).

## Audit and logging

| Event | Category / action | Recorded |
|---|---|---|
| Read | `phi` / `patient.accessed` | patient id, `fhir.{Type}`, resource id, `{ api, interaction: read, resourceType, purpose }` (the stated purpose, or "not stated") |
| Search in a patient's record | `phi` / `patient.accessed` | one entry: patient id, `fhir.{Type}`, returned, total and withheld counts, parameter **names**, the stated purpose |
| Patient search | `phi` / `patient.accessed` | one entry per returned patient, written in one transaction |
| Patient search that returned nobody | `integration` / `fhir.search` | counts and parameter names |
| Read or search of the directory (Practitioner, Organization, Location) | `integration` / `fhir.directory` | `fhir.{Type}`, the resource id of a read, returned and total counts, parameter **names**, the stated purpose; no patient. A read that finds a deleted entry or none is recorded too (`gone`, `not-found`) |
| Read or search refused for consent or restriction | `security` / `fhir.access_denied` (Warning) | patient id, type, id, reason (`consent-required`, `restricted-record`, or `outside-consent` for a record the reader's consents do not cover), the stated purpose |
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
| GET | `/metadata` | anonymous, rate limit `public` | `CapabilityStatement` (FHIR 4.0.1, JSON, read + search-type for the thirteen types, every search parameter) |
| GET | `/{type}/{id}` | `fhir.read` | the resource, or an `OperationOutcome` |
| GET | `/{type}?…` | `fhir.read` | `searchset` `Bundle` |
| POST | `/{type}/_search` | `fhir.read` | same as GET; parameters as `application/x-www-form-urlencoded` (keeps identifiers out of the request line) |
| any | anything else under `/fhir` | anonymous, rate limit `public` | `OperationOutcome` 404 (unknown interaction) or 405 (the API is read-only) |

* Every read and search may carry `X-Purpose-Of-Use` (an HL7 v3 ActReason code, see
  [Consent and access rules](#consent-and-access-rules)); an unknown code is refused with 400 `invalid-parameter`.
* Content type `application/fhir+json; charset=utf-8`; `_pretty=true` indents. `_format` and `Accept` must allow JSON
  (406 otherwise). Responses carry `Cache-Control: no-store` (the CapabilityStatement `no-cache`).
* Paging: `_count` (default 20, capped at **100** — the `self` link shows the size used; `0` returns only `total`) and
  `_offset`. Bundles have `total`, `self`, `previous` and `next` links (which repeat the search parameters), and one
  `match` entry per resource with its `fullUrl`.
* The types of a patient's record are searched **one patient at a time** (`patient`/`subject` = `Patient/{id}`, `{id}`
  or an absolute URL). Patient and Practitioner searches need a selector ([search parameters](#search-parameters));
  the facilities and the places of care may be listed. Results are newest first by the resource's main date, then by id (the directory by id).
* Ordering (`_sort`): a comma-separated list of the type's search parameters in priority order, each with a leading `-`
  for decreasing order (`_sort=-date,code`), given once. Dates, strings and codes order, `_id` and `_lastUpdated`
  included; a reference (`patient`, `partof`…) or an `identifier` is refused with 400 (`invalid-parameter`), as is a
  parameter named twice or a `_sort` left empty (an empty value of any other parameter is left out). The value is the
  one the resource is served with:
  * where it has several (codes, names, the instants of a period or of a date at its precision), the one that comes
    first in the asked order counts: the earliest start in increasing order, the latest end in decreasing order, so an
    ongoing stay is the latest of all, and a birth date estimated from a stated age (served as its year) counts as the
    whole year;
  * a person's name orders by family name, then given names; names and other texts compare lower-cased, character by
    character; codes compare as written;
  * a resource without the value comes last, whichever the direction;
  * ties fall back to the default order, newest first, then by id, so paging through a sorted search is stable. The
    links repeat `_sort`.

  Patient searches are ordered in PostgreSQL by the same values (the name lower-cased and compared byte by byte,
  whatever the database's collation; the last update cut to the millisecond it is served with, so two patients changed
  within one millisecond tie); the other types in memory.
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
| 400 | too-costly | `too-many-records`: an Observation search that would load more than 5,000 home-monitoring readings; narrow its `date` range, `category` or `code` |
| 401 | login (with the scheme's `WWW-Authenticate`) | `authentication-required` |
| 403 | forbidden | `permission-required`, `facility-required` (an account not assigned to the facility of its session), `consent-required`, `restricted-record` |
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
| Observation | `patient`/`subject`, `category` (`vital-signs`, `laboratory`, `survey`, `exam`, `procedure`), `code` (LOINC), `date` (effective: recorded, collected or measured), `status` (`entered-in-error` only when asked for). A search that would load more than 5,000 home-monitoring readings is refused (400 `too-many-records`) |
| Condition | `patient`/`subject`, `clinical-status`, `verification-status`, `category`, `code` (ICD-10), `onset-date`, `recorded-date` |
| AllergyIntolerance | `patient`, `clinical-status`, `verification-status` (`entered-in-error` only when asked for), `category`, `date` |
| MedicationRequest | `patient`/`subject`, `status`, `intent`, `category`, `authoredon` |
| DiagnosticReport | `patient`/`subject`, `category` (`LAB`, `RAD`, `EC`, `CUS`, `OTH`), `code`, `date`, `status` |
| DocumentReference | `patient`/`subject`, `type` (LOINC: 18842-5 a discharge summary, 11506-3 a progress note, 28570-0 a procedure note, 57133-1 a referral note or letter, 47042-7 a counselling note, 34109-9 another note), `date` (the reference created, a summary or note signed), `status` |
| Immunization | `patient`, `status` (`entered-in-error` only when asked for), `vaccine-code` (SNOMED CT), `date` (the day given) |
| Procedure | `patient`, `status` (`entered-in-error` only when asked for), `date` (when it was performed) |
| Practitioner | `identifier` (HPR ID `https://doctor.ndhm.gov.in`, council registration `urn:hc360:medical-council-registration`, staff code `urn:hc360:practitioner`; a bare value tries each), `name` (each word must start a word of the name), `active`. At least one of `_id`, `identifier` or `name`: a practitioner is looked up, the staff are never listed |
| Organization | `identifier` (HFR ID `https://facility.ndhm.gov.in`, facility code `urn:hc360:facility`), `name`, `active` |
| Location | `identifier` (ward code `urn:hc360:ward`, bed label `urn:hc360:bed`), `name`, `type` (v3 RoleCode `HU`, `ICU`, `PEDICU`, `PEDNICU`, `ER`), `partof` (`Location/ward-{id}` or `Location/room-{id}`; a bare id or an absolute URL also), `organization` (`Organization/{id}`), `status` (`active`, `inactive`) |

## Resource ids

| Resource | Id | Source |
|---|---|---|
| Patient | `{patientId}` | Patients `Patient` |
| Encounter | `visit-{visitId}`, `enc-{encounterId}` | Patients `Visit` (+ Inpatient `Admission`), Clinical `Encounter` |
| Observation | `vs-{vitalsId}-{bp,hr,spo2,temp,rr,wt,ht,bmi,pain,gcs,glucose}`, `lab-{resultId}`, `rpm-{readingId}` (a blood pressure taken together: the systolic reading's id) | Clinical `VitalSigns`, Diagnostics `LabResult`, VirtualCare `RpmReading` (+ `RpmDevice`) |
| Condition | `{diagnosisId}` | Clinical `Diagnosis` |
| AllergyIntolerance | `{allergyId}` | Clinical `Allergy` |
| MedicationRequest | `{prescriptionItemId}` | Clinical `PrescriptionItem` + `Prescription` |
| DiagnosticReport | `lab-{labReportId}`, `rad-{radiologyReportId}`, `card-{cardiologyReportId}` | Diagnostics reports |
| DocumentReference | `doc-{documentId}`, `ds-{dischargeSummaryId}`, `note-{noteId}` | Patients `PatientDocument` (+ stored file metadata), Clinical `DischargeSummary`, Clinical `ClinicalNote` (signed only) |
| Immunization | `{immunizationId}` | Clinical `Immunization` |
| Procedure | `{procedureId}` | Clinical `ProcedureRecord` |
| Practitioner | `{practitionerId}` | the practitioner master (Platform `Practitioner`) |
| Organization | `{facilityId}` | the facility master (Platform `Facility`) |
| Location | `ward-{wardId}`, `room-{roomId}`, `bed-{bedId}` | Inpatient `Ward`, `Room`, `Bed` |

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
| `managingOrganization` | registering facility: `Organization/{id}`, display name, HFR id (`https://facility.ndhm.gov.in`) |

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
* Where the patient stayed (`location`, the read API only: ABDM documents name no ward or bed): for each stay in one bed,
  in order (Inpatient bed assignments), the ward the bed was in then (`Location/ward-{id}`, physical type `wa`) and the
  bed (`Location/bed-{id}`, `bd`), each with the stay's period and `completed`, or `active` for where the patient is now.
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

### Observation — home monitoring (`rpm-`, remote patient monitoring)
What a patient's monitoring devices push (RPM-02), or a nurse enters as the patient reads it out, coded as the clinic's
vital signs are, so a receiver reads a reading taken at home as one taken here:

| Reading | Observation |
|---|---|
| Systolic and diastolic taken together (same enrolment, same instant) | one blood pressure panel 85354-9 (profile `bp`) under the systolic reading's id, components 8480-6 and 8462-4 in `mm[Hg]`, each with its own interpretation; one taken alone is a panel stating the other absent (`dataAbsentReason` unknown) |
| Heart rate, SpO₂, temperature, weight | 8867-4 `/min`, 59408-5 with 2708-6 `%`, 8310-5 `Cel`, 29463-7 `kg`, with their FHIR vital-signs profiles |
| Glucose by glucometer | 41653-7 `mg/dL`, category `laboratory` (as a bedside glucose; shared with the lab results) |
| ECG patch rhythm | 8884-9, category `procedure`, `valueCodeableConcept` text: the device's words, else its class (normal sinus rhythm; other or inconclusive; atrial fibrillation or worse); shared with the heart tests |

`effective` = when it was measured, `issued` = when HC360 received it (a panel: its later reading), `performer` = the
patient, `device` = the device by name with its serial number (no Device resource is served), and a note "Read out by
the patient and entered by {who}" on a reading entered by hand. `interpretation` says how the reading stood against the
limits the care team set for the patient when it arrived: N within, A outside, AA beyond the critical limits (a panel:
its further reading). The limits are not sent as reference ranges: the care team changes them, and each reading was
judged against those of its day. Readings are kept as they arrived, so a reading's status is always `final`.

A search loads only the readings it could match and the consents cover, both decided in SQL on the readings' (patient,
time) index: the reading types its `category` and `code` can match (judged on a reading of each type, as the search
itself judges), the measurement times its `date` allows (a second wider either side), and the kinds and days the
consents admitting the reader cover, a day in hospital time being a range of instants. A unit test runs that query
condition against the rule every other record is shared by (`PatientAccess.Covers`), for each kind of reading at the
edges of a day under consents of every shape. Past 5,000 readings the search is refused (`too-many-records`) rather than
cut short, so no client takes part of the record for all of it; readings the consents do not cover never count toward
the limit, so a refusal says nothing of them. A read by the id of a diastolic reading taken with a systolic one is 404:
the panel is served under the systolic reading's id.

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
* Clinical notes (`note-`): only signed notes; a draft is answered 404 as if there were none.
  * `type`: LOINC's document type, as FHIR R4's Document Type value set names it: 11506-3 *Provider-unspecified
    Progress note*, 28570-0 *Provider-unspecified Procedure note*, 57133-1 *Referral note*, 47042-7 *Counseling note*,
    34109-9 *Note* for any other kind; HC360's name for it as text.
  * `docStatus` final; `date` = when it was signed; `description` = its title, else its kind.
  * `author` = the practitioner who wrote it (with the HPR ID), else the account by name (a nurse is not a
    practitioner); `authenticator` = who signed it, the same way; `custodian` = the facility where it was written (with
    its HFR id).
  * `context.encounter` = the consultation, else the stay's in-patient visit, only when it is the patient's own (a note
    filed against another patient's names none; such a note can no longer be written).
  * An addendum: `relatesTo` `appends` → `DocumentReference/note-{original}`. The original stays `current`.
  * `content`: the note as UTF-8 text exactly as it was signed (`text/plain; charset=utf-8`), its title, the signing
    time; no language is stated, as HC360 does not know it.
  * A counselling note carries the confidentiality label `R` (restricted) in `meta.security` and `securityLabel`.
  * Every kind meets the ABDM guide's DocumentReference profile (checked in the tests), though the API claims no
    profile.

### Immunization
* Every dose staff record on the patient's **Immunisations** tab at the reader's facility, given there or copied from
  the patient's card there (doses recorded at another facility of the hospital are that facility's).
* `vaccineCode`: the SNOMED CT vaccine product of the card code staff chose (`VaccineCodes`: the National Immunisation
  Schedule's BCG, hepatitis B, bOPV, pentavalent, rotavirus, fIPV and IPV, PCV, MR, JE, DPT, Td and TT, and the usual
  adult, travel and private vaccines), always one of the ABDM guide's vaccine codes (`ndhm-vaccine-codes`) with its
  display, and the name staff recorded as text. A dose recorded without a code, or with one HC360 does not know, is
  sent by its name only: HC360 never guesses a code.
* `occurrenceDateTime` = the day given; `recorded` = when it was recorded; `lotNumber`, `expirationDate`,
  `manufacturer` (name), `site` (as written); `route` coded in SNOMED CT (372464004 intradermal, 78421000
  intramuscular, 34206005 subcutaneous, 26643006 oral: the ABDM guide's profile fixes the route's system to SNOMED CT)
  when staff wrote one of those in any usual spelling ("ID", "I.M.", "s.c.", "oral drops"), else as written;
  `protocolApplied.doseNumber`.
* `performer` (function `AP`, administering provider): the account that recorded a dose given here, else who gave it
  as written on the card. Named only: an account or a card names no practitioner record.
* A dose copied from a card is not the hospital's own record: `primarySource` false and `reportOrigin` `record`
  (written record).
* A dose marked as entered in error is `entered-in-error`, with the reason as a note: a read by id still returns it, so
  a provider that read it learns it is wrong; searches leave it out unless `status` asks for it.
* Every mapping meets the ABDM guide's Immunization profile (checked in the tests), though the API claims no profile.

### Procedure
* Every procedure recorded at the partner's facility ([set-up](#permission-and-set-up), step 3): on the chart, and
  every operation recorded when the theatre completed it. A procedure on the chart is the facility's where it was
  recorded, an operation the theatre's. Staff see a patient's procedures from every facility; the API serves the
  partner's facility's own, as it does clinical notes.
* `code`: the procedure's name only. The ABDM guide's Procedure profile fixes the system of every coding of a
  procedure (code, body site, outcome, category) to SNOMED CT; HC360 codes a procedure in ICD-10-PCS (the procedure
  catalogue) or names it in words, and holds no SNOMED CT map for it. So an ICD-10-PCS code is never sent under SNOMED
  CT's system, as a diagnosis in an unknown code system goes by its title ([Condition](#condition)).
* `category`: SNOMED CT 387713003 *Surgical procedure* for an operation the theatre completed; none for a procedure on
  the chart, which may be minor or diagnostic.
* `performedDateTime` = when it was performed; `encounter` = the clinician's encounter, else the visit, else the stay's
  in-patient visit.
* `performer.actor`: the practitioner (`Practitioner/{id}`, by name with their HPR ID) when they have a record here;
  else the name written for a performer from elsewhere, by name only.
* `bodySite` and `outcome` as written, the side included in the body site ("Knee, left"; "Right side" when only the
  side was recorded); `note`: the notes.
* A corrected record is `completed`, with "Corrected:" and the reason as a note. A record marked as entered in error is
  `entered-in-error`, with the reason: a read by id still returns it, so a provider that read it learns it is wrong;
  searches leave it out unless `status` asks for it.
* A procedure that ends a pregnancy (ICD-10-PCS 10A) or is electroconvulsive therapy (GZB) puts the patient's record
  in the MTP or mental health category in the save that records it, as the matching diagnosis does (see the Clinical
  module's *Procedures*). From then on the record is restricted: a read is refused (403 `restricted-record`) and a search
  returns nothing, withheld, for the procedure and for everything shared before. So such a procedure never leaves under
  a consent, even one that names procedures.
* Every mapping meets the ABDM guide's Procedure profile (checked in the tests), though the API claims no profile.

### Practitioner, Organization and Location

The hospital's directory: who and where the people and places in the records are. It is in no patient's record, so a
reader needs `fhir.read` and no consent, and every read and search is audited as an integration event, not PHI access.
Every reference to a practitioner, a facility, a ward or a bed in the other resources is literal (`Practitioner/{id}`,
`Organization/{id}`, `Location/{id}`), with the name (and the HPR or HFR id), and resolves here.

* **Practitioner**: as the ABDM documents carry them (NRCeS Practitioner profile, claimed): name, gender,
  qualification, HPR ID, council registration with the council, and staff code; `active` false once taken out of use.
  Never a phone number, e-mail, photograph or signature: who a practitioner is, not how to reach them.
* **Organization**: as the ABDM documents carry it (NRCeS Organization profile, claimed): name, type `prov`, HFR ID,
  facility code, and the facility's own phone, e-mail and address; `active` false once out of use.
* **Location** (base R4: the guide has no Location profile): the wards (`ward-`), the rooms in them (`room-`) and their
  beds (`bed-`), of the reader's facility, as the stays that name them are.
  * The name (a room's is its ward's and its number); the ward code or the bed label as an identifier.
  * `type`: the kind of ward as a v3 service delivery location type (the binding is extensible), with the ward type in
    words: `ICU`, `PEDICU` (paediatric) and `PEDNICU` (neonatal) intensive care, `ER` the emergency ward, and `HU`
    (hospital unit) every other ward, whose kind the value set has no code for. A room or a bed has its ward's.
  * `physicalType` `wa`, `ro` or `bd`; `partOf`: a room its ward, a bed its room or, in an open ward, its ward;
    `managingOrganization` the facility; `status` active, or inactive once out of use.
  * `meta.lastUpdated`: a room's or bed's own last change, or its ward's (and a bed's room's) when later, as its name,
    kind and `partOf` come from them: a renamed ward is a change to each of its rooms and beds for a `_lastUpdated` search.
  * Never free text (a ward's notes, a room's description, why a bed is blocked, which may name a patient) and never
    occupancy (`operationalStatus`): what and where a place is, not what happens in it.
* A search needs `_id`, `identifier` or `name` for Practitioner, so the staff cannot be listed; the facilities and the
  places of care can be.
* Deleted (a practitioner, facility or place recorded by mistake): Gone (410) by id, as records may still refer to it;
  never in a search.
* A nurse or other account that wrote or gave something, and is not in the practitioner master, is named only.

## ABDM health records (EHR-08)

The Ayushman Bharat Digital Mission (ABDM) links a patient's records to their **ABHA** (Ayushman Bharat Health Account)
and moves them as FHIR documents of the NRCeS "FHIR Implementation Guide for ABDM" (package `ndhm.in`, validated against
**6.5.0**). HC360 captures the ABHA, builds the documents and exports them one at a time; through ABDM's gateway, which needs the
hospital's NHA credentials, it links records to the patient's ABHA and sends them, encrypted, to the provider the patient
consents to ([ABDM gateway](#abdm-gateway)).

### The switch

`features.abdm` ("ABDM health records", [features.md](features.md)) is **off by default**, unlike the switches that
existed before their feature: it is new and, once the gateway is live, sends records outside the hospital. While it is
off every `/api/v1/abdm/*` endpoint answers 503 `feature.disabled`. ABHA numbers and addresses are recorded and shown
whatever the switch says.

### ABHA on the patient

| | |
|---|---|
| Fields | `Patient.AbhaNumberCipher` (14 digits, AES-256-GCM), `AbhaNumberIndex` (blind index for search and duplicate checks), `AbhaNumberMasked` (`XX-XXXX-XXXX-1234`), `AbhaAddress` (lower case). No new columns |
| Number | 14 digits, typed run together or grouped with spaces or hyphens; anything else is refused ("Enter a valid 14-digit ABHA number."). **There is no check digit**: no NHA or ABDM specification publishes one, and the ABHA number of the IG's own example patient (`22-7225-4829-5255` in `ndhm.in#6.5.0`) fails a Verhoeff check, as do the 14-digit HPR ids of its example doctors (`21-1521-3828-3227`, `25-1531-3528-3228`, under `https://doctor.ndhm.gov.in`) and about 9 in 10 of any 14 digits. Refusing them would turn real ABHA cards away at the desk, so any 14 digits are taken, and a mistyped number cannot be told from a real one until it is checked with ABDM ([Checking a patient's ABHA](#checking-a-patients-abha-abdm-m1)). Shown as `xx-xxxx-xxxx-xxxx` (`Abha.Format`) where the full number is needed (ABDM documents, the FHIR API) and masked everywhere else |
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
| GET | `/bundles/immunizations/{id}` | `abdm.export` | ImmunizationRecord document of a vaccine dose given here (the dose's id), with the next dose when one is due, as `immunization-{UHID}-{card code}-{day given}-abdm.json`. A dose copied from the patient's card, or marked as entered in error, is not exported (422 `abdm.not_exportable`): the card's dose is the record of whoever gave it |
| GET | `/bundles/home-readings/{id}?month=yyyy-MM` | `abdm.export` | WellnessRecord document of a remote monitoring enrolment's home readings measured in a month in IST (the enrolment's id; the month of the latest reading when none is named), as `home-readings-{UHID}-{yyyy-MM}-abdm.json`. An enrolment with no reading yet, a month with none, and a month of more readings than one document holds (ABDM sends those in parts) are refused (422 `abdm.not_exportable`, saying why); a month not written `yyyy-MM` is 400 |
| GET | `/bundles/clinical-notes/{id}` | `abdm.export` | HealthDocumentRecord document of a signed progress, procedure, referral or other note (the note's id), carrying every addendum signed to it since, as `{kind}-{UHID}-{day signed}-abdm.json` (`progress-note-…`). A counselling note, an addendum on its own and a draft are not exported (422 `abdm.not_exportable`, saying why) |
| GET | `/bundles/invoices/{id}` | `abdm.export` | InvoiceRecord document of a finalised, partly paid or paid bill (the bill's id), with its Invoice and the printed bill, as `bill-{bill number}-abdm.json`. A draft (an estimate, with no number yet) and a cancelled bill, refunded or not, are not exported (422 `abdm.not_exportable`, saying why). The guide's InvoiceRecord and Invoice profiles are not in the package HC360 validates against: see [Validation](#validation) |
| GET | `/gateway` | `admin.integrations.manage` (no role has it by default: the super administrator) | `AbdmGatewayStatus`: how the gateway is set up, what is missing, `live` (switched on and set up) and what remains |
| GET | `/shares` | `patients.register` | `AbdmShareRow[]`: today's scan-and-share queue, the waiting first, by token; mobile and ABHA numbers masked, the patient a share matched or was registered as ([Scan and share](#scan-and-share-abdm-m1)) |
| GET | `/shares/{id}` | `patients.register` and `patients.contact.read` | `AbdmShareDetail`: one share in full (ABHA number, mobile number, date of birth, address) to fill in the registration form |
| POST | `/shares/{id}/registered` | `patients.register` | `{ patientId }`: the patient registered from a waiting share, or found on record (an active, unmerged one); 409 `abdm.share_handled` once it has left the queue |
| POST | `/shares/{id}/dismiss` | `patients.register` | Takes a waiting share off the queue without registering anyone |
| GET | `/patients/{patientId}/abha-checks` | `patients.read` | `AbhaVerificationView[]`: the patient's last five ABHA checks with ABDM, newest first ([Checking a patient's ABHA](#checking-a-patients-abha-abdm-m1)) |
| POST | `/patients/{patientId}/abha-checks` | `patients.identity.verify` | `{ method: "AbhaNumber" \| "Mobile" }`: ABDM sends a code to the mobile number linked to the ABHA number (or mobile number) on the patient's record |
| GET | `/patients/{patientId}/consent-requests` | `abdm.records.request` | `AbdmConsentRequestView[]`: the hospital's last 20 requests for the patient's records at other providers, with the consents granted ([Asking for a patient's records](#asking-for-a-patients-records-at-other-providers-abdm-m3)) |
| POST | `/patients/{patientId}/consent-requests` | `abdm.records.request` | `{ purpose, hiTypes, from, to, eraseAfter }`: asks the patient, through ABDM, in the requesting doctor's name |
| POST | `/patients/{patientId}/consent-artefacts/{artefactId}/records-requests` | `abdm.records.request` | Asks again for the records under a granted consent (after one failed); 409 while a request of the last 15 minutes awaits its records |
| GET | `/patients/{patientId}/received-records` | `abdm.records.request` | `AbdmReceivedRecordSummary[]`: the records received from other providers while their consents last, without content |
| GET | `/patients/{patientId}/received-records/{recordId}` | `abdm.records.request` | `AbdmReceivedRecordView`: one record with its FHIR bundle; audited as `phi` / `abdm.records_viewed` |
| POST | `/abha-checks/{id}/code` | `patients.identity.verify` | `{ code }` (six digits): the code the patient read out; the ABHA it opens compared with the record |
| POST | `/links` | `abdm.export` | 202 `AbdmLinkView`, `Location: /api/v1/abdm/links/{id}`: records of one patient (`{ records: [{ type, id }] }`, at most 50) to link to their ABHA ([Linking](#linking-records-to-the-patients-abha-abdm-m2)) |
| GET | `/links/{id}` | `abdm.export` | `AbdmLinkView`: the request's records and where it has got to (`AwaitingToken`, `Sent`, `Linked`, `Failed` with the gateway's code and reason, or `abdm.no_answer`), whether the hospital made it itself (`automatic`) and whether it can be sent again (`retryable`) |
| POST | `/links/{id}/retry` | `abdm.export` | 202 `AbdmLinkView` of a new request, `Location` as above: a failed request's records sent again, with the checks of `POST /links`; 422 `abdm.link_not_failed` while it is linked or awaiting ABDM's answer, and `abdm.already_linked` or `abdm.link_pending` when none of its records is left to send ([Requests ABDM does not answer](#requests-abdm-does-not-answer)) |
| GET | `/patients/{patientId}/links` | `abdm.export` | `AbdmLinkView[]`: the patient's last 20 link requests made in the caller's facility, newest first, automatic and staff's alike |
| POST | `/patients/{patientId}/consent-requests/{id}/retry` | `abdm.records.request` | `AbdmConsentRequestView` of a new request: the patient asked again on a failed request's terms, in the requesting doctor's name; 422 `abdm.consent_not_failed` for a request that did not fail, `abdm.consent_terms_stale` when its terms no longer pass the form's checks (its erase date has passed) |

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
  disabled while it has unsaved changes, hidden while it is being amended), on a signed consultation (the encounter),
  on a released lab, radiology or cardiology report, and in the menu of a signed prescription (the patient chart's and
  the encounter's prescriptions), and in the menu of a vaccine dose given here (patient chart > Immunisations). **Link to patient's ABHA** sits beside it on each
  ([Linking](#linking-records-to-the-patients-abha-abdm-m2)).

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
| Consultation | `OPConsultRecord`, SNOMED CT 371530004 "Clinical consultation report" | 422843007 chief complaint: Conditions; 371529009 medical history (HPI); 425044008 physical examination: vital-sign Observations; 722446000 allergies; 721912009 medications: MedicationRequests; 390906007 follow-up; 371525003 procedures: the Procedures done in the consultation; 306206005 referral; then LOINC 51847-2 assessment and plan (the IG has no such section; its slicing is open) |
| Immunisation record | `ImmunizationRecord`, SNOMED CT 41000179103 "Immunization record" | 41000179103 (the profile's one section): the Immunization, and an ImmunizationRecommendation when staff wrote when the next dose is due. The facility where the dose was given (`Immunization.FacilityId`, the facility it was recorded at) is the author and custodian; the nurse or doctor who gave it is the dose's administering provider, by name. Its date is when the dose was recorded; it is final, as a dose is not changed afterwards, only marked as entered in error, and then not exported. A dose recorded in an encounter the document does not hold names no encounter |
| Home readings | `WellnessRecord`, typed as the profile fixes it, by its text alone (`type.text` "Wellness Record", no coding); titled "Home readings: {programme}, {month}", with an `event.period` of what it covers: the month's days, or the instants the dates a consent asks for cut it at | The guide's sections, told apart by title alone as the profile slices them, each with the profile it asks of its entries: Vital Signs (blood pressure, a systolic and a diastolic reading taken together as one panel; heart rate; SpO₂; temperature: ObservationVitalSigns), Body Measurement (weight: ObservationBodyMeasurement), General Assessment (a glucometer reading: ObservationGeneralAssessment, whose value set names blood glucose) and Other Observations (an ECG patch's rhythm: Observation), each reading as the FHIR API serves it (the patient measured it, with the device named; one read out on the phone names who entered it; interpreted N, A or AA against the limits the care team set). Each section's narrative is its readings in the order measured, each with its time in IST, how it stood when not normal and who entered one by hand, `generated`. No one signs a reading: the facility that runs the programme is the author and custodian, no attester, dated when it received the latest reading. One document holds a month of one enrolment's readings in IST; a consent's dates cut the months they begin and end in (inclusive), and a month of more than 1,000 readings goes in parts in the order measured, a blood pressure never parted, the first part keeping the month's identity. The programme is one care context (`home-readings/{enrolment}`), named by its kind ("Home readings: Hypertension monitoring, from 02 Sep 2026"), never by its name, which the hospital writes as it likes; a programme of the hospital's own kind by the record's kind alone |
| Clinical note | `HealthDocumentRecord`, SNOMED CT 419891008 "Record artifact" (the profile fixes both, and the one section's code); its title and `type.text` the note's kind ("Progress note") | 419891008 (the profile's one section, titled with the note's own title): the note as the FHIR API serves it (DocumentReference typed with LOINC's document type for its kind, the text exactly as signed as a `text/plain` UTF-8 attachment: the hospital prints no clinical note), then each addendum signed to it since, an addendum to an addendum included, oldest first, each appending to the one it adds to and naming its own writer, signer and the facility where it was written (an addendum written at another facility of the hospital included, as the record staff see lists a patient's notes from every facility). The section's narrative is that text: the note's, then each addendum's, after who signed it and when (IST), `generated`. The doctor who wrote the note is the author (the facility, for a note by staff who are not doctors), and the doctor who signed it attests it; once an addendum is signed the document is `amended`, dated and attested as of the latest addendum (no attester when its signer is not a doctor), whose writer is a second author. A note written in an encounter the document does not hold names no encounter. A counselling note is not exported: an ABDM consent names kinds of record, and none names counselling notes apart from the patient's other health documents, which the FHIR API's consents can (a counselling note goes there only under a consent that names counselling notes). An addendum goes only with its note |
| Discharge summary | `DischargeSummaryRecord`, SNOMED CT 373942005 "Discharge summary" | 422843007 complaints and diagnoses: the stay's Conditions; 722446000 allergies; 721981007 investigations: the stay's lab DiagnosticReports; 1003606003 medications: the doctor's medicines on discharge as text, then the MedicationRequests prescribed during the stay; 1003640003 procedures: the doctor's procedures as text, then the Procedures recorded during the stay (operations the theatre completed included); 734163000 care plan (advice, follow-up); 373942005 the summary as DocumentReference (the printed PDF, else the summary's text); then LOINC 8648-8 hospital course |
| Invoice (bill) | `InvoiceRecord`, typed `INVOICE` "Invoice" of the guide's billing code system (`https://nrces.in/ndhm/fhir/r4/CodeSystem/ndhm-billing-codes`): SNOMED CT has no concept for an invoice, and the InvoiceRecord profile is not in the 6.5.0 package, so this is the guide's code as best known when it was written, **not checked against the published profile** ([Validation](#validation)) | The same code (one section, titled "Invoice"): the Invoice, and the printed bill as Binary (Billing's A4 bill, in the patient's language, with the payments received by then) where this server can make PDFs. The facility that issued the bill is the author, custodian and the invoice's issuer; the visit or stay is the encounter; no one attests it. Its date is when the bill was finalised. Only a finalised, partly paid or paid bill is exported; a draft is an estimate with no number, and a cancelled bill (`Cancelled`, or `Refunded`: a cancelled bill whose payments went back) is not owed |

Section and document codes, systems and displays are the ones the IG's profiles fix. Empty sections are left out.
A section's narrative reads whole on its own, since it is what the doctor attests: the free text the doctor wrote,
then one line per entry under a heading ("Diagnoses:", "Vital signs:", "Medicines:", "Lab reports:", "Prescribed
during the stay:", "Recorded during the stay:"), with status `additional`; a section without free text has one line per entry, `generated`. Every
resource has a generated narrative. A diagnosis's line says when it is provisional, differential, chronic or resolved,
and an allergy's when it is inactive or resolved, its high criticality and its reaction ("Viral fever (differential)",
"Allergy Penicillin (high criticality): Hives (severe)"): in FHIR those statuses change what the resource means, and a
narrative must read safely on its own. A procedure's line says when, where and on which side, by whom, and how it went
("Incision and drainage of abscess: performed 2026-09-25T08:45:00+05:30, Axilla, right, by Dr. Asha Rao. Outcome: Pus
drained; wound packed").

| Resource | Profile | What the IG adds to the read API's resource |
|---|---|---|
| Patient | `Patient` | ABHA number (`https://healthid.ndhm.gov.in`, `xx-xxxx-xxxx-xxxx`) typed `ABHA` from the IG's identifier types; UHID typed MR, and the UHIDs of merged duplicates that records in the document are still kept under, `old`. A patient without an ABHA is still exported, with the UHID |
| Practitioner | `Practitioner` | From the practitioner master: the HPR ID first where the master has one (`https://doctor.ndhm.gov.in`, v2-0203 MD, written `xx-xxxx-xxxx-xxxx`, as the guide's own examples identify a doctor), medical-council registration (v2-0203 MD, the council as assigner) and staff code (PRN), name, gender, qualification. No personal phone or e-mail |
| Organization | `Organization` | From the facility master: HFR id (`https://facility.ndhm.gov.in`, PRN), facility code (XX), type `prov`, work phone, e-mail, address. For a test sent out, the referral laboratory from the laboratory's master: its code (`urn:hc360:referral-lab`, XX), name, phone, e-mail, address |
| Encounter | `Encounter` | The visit (with its admission for a stay) or the clinical encounter; a consultation's link to its visit (`partOf`) is left out, as the visit is not in the document |
| Condition | `Condition` | ICD-10 coding only (free-text diagnoses as text) |
| AllergyIntolerance | `AllergyIntolerance` | Allergen as text; allergies recorded in error left out |
| Procedure | `Procedure` | As the read API ([Procedure](#procedure)): by name, as the profile fixes every coding's system to SNOMED CT and HC360 codes procedures in ICD-10-PCS; an operation the theatre completed is a surgical procedure (SNOMED CT 387713003), the one coding. Recorded by the document's date, and never one recorded in error; in the order done |
| MedicationRequest | `MedicationRequest` | As the read API (medicine as text, dosage with v3-GTSAbbreviation timing codes and their displays) |
| Observation | `ObservationVitalSigns`, `ObservationBodyMeasurement` (weight, height, BMI) or `Observation` (lab results, pain, GCS) | LOINC (and SNOMED CT) codings only, and only with the code system's own display, which the IG requires: a lab result's LOINC coding needs its LOINC name in the test master, else the test goes by its name as text; performer = the laboratory (results; the referral laboratory for a test sent out to one, as the printed report says) or the hospital (vital signs) |
| DiagnosticReport | `DiagnosticReportLab` | Category SNOMED CT 394595002 "Pathology"; LOINC 11502-2; performer and results interpreter = the laboratory; conclusion (the IG requires one; the laboratory writes none) = the results the laboratory flagged ("Flagged by the laboratory: Haemoglobin (Low)."), or "See the individual results." when it flagged none: an unflagged result is not a normal one (a qualitative result, or one without a reference range, is never flagged), so nothing is said about the results |
| DiagnosticReport (radiology) | `DiagnosticReportImaging` | Category SNOMED CT 394914008 "Radiology"; LOINC 18748-4 only (the service code dropped); performer = the hospital; results interpreter = the doctors who wrote it, signed it off and added addenda (else the hospital); `imagingStudy`; issued = the release or the latest addendum after it. The profile requires a key image (`media`): HC360 keeps none (the images stay in PACS), so the one media entry says so in its comment and its link carries data-absent-reason `unsupported`, rather than something that is not a key image |
| DiagnosticReport (cardiology) | `DiagnosticReportImaging` | Category SNOMED CT 394579002 "Cardiology"; the LOINC study only, where LOINC names one (see the cardiology mapping above; the service code dropped); performer = the hospital; results interpreter = the doctors who wrote and signed off the report; no ImagingStudy; the media entry and the measurements in `presentedForm` as for radiology. The measurements are not separate coded Observations: LOINC's codes for the ECG intervals, LVEF and ambulatory BP could not be checked against LOINC when this was built, and an unchecked code is worse than none |
| ImagingStudy | `ImagingStudy` | Accession number (ACSN) and, once PACS linked the images, the study instance UID (`urn:dicom:uid`) with the number of series and images; modality as a DICOM code as the worklist sends it (an X-ray as CR); started; interpreters; body part in the narrative (a series needs PACS's series UID, which HC360 does not keep) |
| Immunization | `Immunization` | As the read API ([Immunization](#immunization)): the vaccine as the SNOMED CT product of the guide's value set when its card code is known, else by name; the route in SNOMED CT; batch, expiry, manufacturer, site, dose number. In a document it is always a dose given here (`primarySource` true), completed |
| ImmunizationRecommendation | `ImmunizationRecommendation` | The next dose staff wrote is due: the same vaccine, `forecastStatus` `due` (immunization-recommendation-status), `dateCriterion` LOINC 30980-7 "Date vaccine due" with that day, the dose number after this one when it is known, `supportingImmunization` the dose, `authority` the hospital, `date` when the dose was recorded |
| Invoice | `Invoice` (not in the 6.5.0 package) | The bill: `identifier` its number (`urn:hc360:bill-number`); `status` `issued`, or `balanced` once the patient's share is paid and the payer owes nothing (a paid insured bill whose insurer has not settled stays `issued`); `type` out-patient, in-patient interim or in-patient final bill (`urn:hc360:bill-type`); `subject` the patient; `date` when it was finalised; `issuer` the facility; `participant` the doctors whose services it bills ("Service provider"). A `lineItem` per bill line, in the bill's order: `chargeItemCodeableConcept` the service by its HC360 code (`urn:hc360:service`) and its SAC code (`urn:hc360:gst-sac`), then the price components, which add up to the line's net amount: `base` (the quantity as `factor`, the unit price times the quantity as `amount`), the package cover (`deduction`), the tariff concession, the staff discount and the line's share of the bill discount (`discount`), each as a negative amount, then CGST and SGST at half the rate each, or IGST at the full rate on a supply to another state (`tax`, the rate as `factor`); the unit price is stated as `informational`, outside the sum. `totalPriceComponent` the same for the bill, with its round-off of the patient's share (`surcharge` or `deduction`), adding up to the bill's net amount; `totalNet` the taxable amount, `totalGross` the net amount, all in INR; `paymentTerms` the patient's share and the payer's. Component codes are HC360's (`urn:hc360:price-component`: `unit-price`, `package`, `tariff-discount`, `line-discount`, `bill-discount`, `not-charged`, `CGST`, `SGST`, `IGST`, `round-off`). On a bill free of cost by law each line's uncharged value is a `not-charged` deduction, so it adds up to nothing; its reason is never in the document. No free-text bill notes |
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
  for an amended record (`AbdmAsOf`). An allergy, diagnosis, vital sign, prescription, procedure or lab report entered later is
  not in it (a lab report counts from its first release, a result from its test's release); a diagnosis resolved since
  (on a later day, or that day after the time) is active, a prescription cancelled since is signed, a lab report
  completed since is partial with the tests released by then, and a medicine is active or completed as of that day; an
  entry recorded in error since is left out, as never true. These entries carry no `meta.lastUpdated`: their latest
  change may be after the date, and would make a new version of a document that says the same. HC360 keeps no history
  of other changes, which show as they are now and make a new version: an allergy made inactive, a diagnosis
  confirmed, an edited reaction, a corrected procedure, an amended lab result (its report stays in the summary, amended), the patient's
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

* `AbdmDocumentTests`: the sample document of each kind of record (consultation, prescription, lab report, radiology
  report, cardiology report, discharge summary, immunisation record, clinical note, home readings) validates with no
  error and no warning.
* **Invoices are validated only in part.** The guide's `InvoiceRecord` (Composition) and `Invoice` profiles are not in
  the 6.5.0 package HC360 has, and the package registry cannot be reached from the development machine, so a bill's
  document (`invoice.json`) is checked against base FHIR R4 for every resource (the Composition and the Invoice
  included), the guide's `DocumentBundle`, and its `Patient`, `Practitioner`, `Organization`, `Encounter` and `Binary`
  profiles. Firely reports each of the two missing profiles as an error ("Unable to resolve reference to profile …",
  `incomplete`): the tests require exactly those two errors and no other (`AbdmDocumentTests`,
  `AbdmInvoiceRecordTests`, `AbdmInvoiceExportTests`), so a missing profile is never passed silently, and
  `AbdmInvoiceRecordTests` shows base R4's rules are still enforced on the Invoice and the Composition (a line with no
  service, a price component with no type, a participant with no one, a Composition with no title each fail). The
  document's type code (`INVOICE` of `ndhm-billing-codes`), the price component codes (HC360's own) and whatever else
  the guide's profiles require of a bill are **not validated**: run the document through NHA's sandbox, or the HL7
  validator with a package that has InvoiceRecord, before it is sent to anyone.
* `Abdm/ValidatorSetupTests`: the guide's own discharge summary, prescription, immunisation record, health document and
  wellness record examples validate, and deliberately broken documents fail on the rule they break (for a wellness
  record: a type of other text, no patient, no section, a vital sign valued as a code, a vital-signs entry that is not
  an observation or names nothing), so a validator that checks nothing cannot pass.
  `AbdmDocumentTests` does the same for the radiology sample against DiagnosticReportImaging and ImagingStudy (seven
  breakages: no media entry, an empty media link, an absent reason the value set lacks, no interpreter, no
  conclusion, the service code kept, a series without its UID).
* `AbdmExportTests` (IntegrationTests): every document downloaded from the endpoints validates.

The sample documents built by `AbdmDocumentTests` are kept in
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
| `op-consultation.json` (2026-11-04, with a procedure) | **0** | 23 | `Bundle-OPConsultNote-example-05`: 5 errors, 4 warnings |
| `discharge-summary.json` (2026-11-04, with the stay's procedure) | 3 | 7 | `Bundle-DischargeSummary-example-04`: 8 errors, 14 warnings |
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
| Settings | `abdm.gateway` (hospital-wide JSON): `enabled` (false), `baseUrl` (https, no user name, e.g. the sandbox `https://dev.abdm.gov.in/api/hiecm`), `hipId` (the HFR id registered as HIP), `cmId` (`sbx` or `abdm`), `timeoutSeconds` (30, between 5 and 120), `jwksUrl` (optional, https: where the gateway publishes the keys its callbacks are signed with; by default `{baseUrl}/gateway/v3/certs`, the v3 place of the specification's `/v0.5/certs`, to be confirmed in the sandbox), `callbackClientId` and `callbackIssuer` (the `clientId` and `iss` claims of the tokens the gateway's own calls carry, as seen in the sandbox; `callbackClientId` is required with the production gateway, `cmId` `abdm`), `hiuId` (optional: the HIU id when the hospital asks for records, by default the HIP id), `abhaBaseUrl` (optional, https: ABDM's ABHA service for [ABHA checks](#checking-a-patients-abha-abdm-m1), by default the sandbox's or production's by `cmId`), `dataPushUrl` (https: the hospital's public address of `/api/v3/hiu/health-information/transfer`, where other providers post the records it asks for as HIU; without it records are not asked for), `autoLink` (true: records are linked to patients' ABHAs as they are completed, [Linking records automatically](#linking-records-automatically)), `answerTimeoutMinutes` (15, between 2 and 1,440: how long a request waits for the gateway's callback before it is marked failed, [Requests ABDM does not answer](#requests-abdm-does-not-answer)). `abdm.gateway.secret` (encrypted secret setting): `clientId`, `clientSecret` issued by NHA. Both are added under Administration › Settings (Add setting); the secret is never shown again |
| Status | `GET /api/v1/abdm/gateway`: enabled, configured, `live` (true when it is switched on and its set-up is complete: records are then linked and sent), the URL and ids, whether the client id and secret are set (never their values), the problems in the set-up and what remains |
| Client | `IAbdmGatewayClient` (gateway v3), registered as `HttpAbdmGatewayClient`: `CreateSessionAsync` (`POST /gateway/v3/sessions` with the client id and secret, `grantType: client_credentials`; the access token is kept and shared until five minutes before it expires, twenty minutes in the sandbox), `RequestLinkTokenAsync` (`POST /v3/token/generate-token`: ABHA address, name, gender `M`/`F`/`O`/`U`, year of birth), `LinkCareContextsAsync` (`POST /hip/v3/link/carecontext` with the token in `X-LINK-TOKEN`: the ABHA number and address and the care contexts, one group per health-information type: `OPConsultation`, `Prescription`, `DischargeSummary`, `DiagnosticReport` for lab, radiology and cardiology reports, `ImmunizationRecord` for vaccine doses, `HealthDocumentRecord` for clinical notes, `WellnessRecord` for home readings, `Invoice` for bills, each group under the patient's id and name), and the answers of user-initiated linking: `AnswerDiscoveryAsync`, `AnswerLinkInitAsync` and `AnswerLinkConfirmAsync` (`POST /user-initiated-linking/v3/patient/care-context/on-discover`, `…/v3/link/care-context/on-init` and `…/on-confirm`, each naming the gateway's call in `response.requestId`), and those of consent and the transfer of records: `AnswerConsentNotifyAsync` (`POST /consent/v3/request/hip/on-notify`), `AnswerHealthInformationRequestAsync` (`POST /data-flow/v3/health-information/hip/on-request`) and `NotifyTransferAsync` (`POST /data-flow/v3/health-information/notify`), and scan and share's `AnswerShareAsync` (`POST /patient-share/v3/on-share`). Every call carries `Authorization: Bearer`, `X-CM-ID`, `REQUEST-ID`, `TIMESTAMP` (UTC, milliseconds) and `X-HIP-ID`. The gateway accepts with 202 and answers later on a callback; any other answer is refused with 422 `abdm.gateway_refused` and the gateway's code and message (it sends `{ error }` or `{ errors: [{ error }] }`, both read), and a gateway that cannot be reached within the timeout gives 422 `abdm.gateway_unreachable`. A call is not retried, as the gateway may have acted on it when its answer was lost; a session the gateway ended (401) is renewed once. While the gateway is off or its set-up incomplete nothing is sent and every call answers 422 `abdm.gateway_disabled`, naming what is missing (`DisabledAbdmGatewayClient`). Every exchange is a row of the integration log ([In the integration log](#in-the-integration-log)) |
| Encryption | `FideliusCipher` (`IFideliusCipher`): every document of a transfer encrypted for the HIU under one fresh HIP key pair, with each document's MD5 as the entry's checksum. Built and tested against NHA's reference implementation, see [Fidelius](#fidelius) |
| Callback check | `AbdmCallbackVerifier`: a call to the callback endpoints is the gateway's only when its `Authorization: Bearer` token is signed RS256 by a key in the gateway's key set (JWKS) whose key id the token names, and is within its lifetime (one minute's clock difference allowed). `none`, HMAC (including the HS256-with-the-public-key confusion) and any other algorithm are refused. A token naming a key id not in the set is refused, so the client fetches the set again (the gateway rotated its keys) rather than trying every key. The gateway signs every ABDM client's session token with the same keys and issuer, so another provider's own token would pass on the signature alone: the token's `clientId` must be `callbackClientId` and its issuer `callbackIssuer` when set, and the production gateway is not live until `callbackClientId` is set (ABDM publishes neither value, nor the key set's URL; they are read off a sandbox callback). Audience is not checked. The key set is fetched from `jwksUrl` and kept six hours (`AbdmGatewayKeys`); a token naming a key not in it has it fetched again, at most once a minute. NHA's own reference HIP (`NHA-ABDM/ABDM-wrapper`) checks nothing, so anyone reaching its callback URLs could post a consent notification. `AbdmCallbackVerifierTests` holds a key set and tokens made with Python's jwcrypto 1.5.6, independent of .NET; they found two faults while this was built (a token naming an unknown key accepted by trying every key, and the lifetime check overflowing on the library's minimum date) |

#### Linking records to the patient's ABHA (ABDM M2)

HIP-initiated linking: records the hospital completed become care contexts on the patient's ABHA, which the patient
sees in their PHR app and can later consent to share (`AbdmLinkingService`).

1. `POST /api/v1/abdm/links` names records of one patient. Each must be one the export would send (signed, released,
   an issued prescription; 422 `abdm.not_exportable` otherwise) and one the caller may open (a restricted record needs
   the permission or break-the-glass, as for the export); all must be the same patient's (422 `abdm.link_patients`),
   who is not merged into another UHID (`abdm.link_merged`) and has an ABHA address (`abdm.no_abha_address`) and a date
   of birth (`abdm.no_year_of_birth`). Each record is a care context with reference `{kind}/{record id}`
   (`op-consultation/…`, `prescription/…`, `lab-report/…`, `immunization/…`, `invoice/…`), stable and unique per patient, and display
   "Consultation note, 12 Oct 2026", or for a dose its vaccine too ("Immunisation record: Pentavalent, dose 2, 12 Oct 2026":
   three vaccines are given at birth, and the patient tells them apart); the patient's reference is their HC360 patient id.
   Records already linked to the ABHA address, or in a request of the patient's still awaiting the gateway's answer,
   are left out before anything is sent, so a record is never sent twice: 422 `abdm.already_linked` or
   `abdm.link_pending` when none is left. They are read, and the request that sends the rest saved, in one transaction
   under the patient's advisory lock (`pg_advisory_xact_lock`, as `EncounterLocks` does for a visit), so requests made
   at once, staff's and the hospital's own or two staff members', cannot both send a record: the later one waits, then
   finds the record awaiting the earlier one's answer. Deduplicating only once the gateway has linked them would not do: NHA's mock
   gateway answers 208 to care contexts already linked, which HC360, like any answer but 202, counts as a refusal that
   fails the request and drops a good link token. Audited as `phi` / `abdm.link_requested`, "Records linked to the
   patient's ABHA: 2", entity `abdm.link`, details `{ api, records, automatic, retryOf }`.
2. With a link token kept for the ABHA address the care contexts are sent at once (`Sent`). Otherwise a token is asked
   for (`AwaitingToken`), and the care contexts go when the gateway's `on-generate-token` brings it. Each step is
   committed before its call is made, so an answer that arrives first finds its request; a token that comes after the
   request was marked unanswered resumes it under the same lock.
3. The gateway's `on_carecontext` says whether they were linked (`Linked`) or why not (`Failed`, with its code and
   message). A refusal, then or when the care contexts are sent, drops the kept token, so the next request asks for a
   new one (it may have expired or been revoked); a gateway that could not be reached keeps it.

The token is kept per HIP id and ABHA address, encrypted like other secrets in the database (`IFieldProtector`), and
never returned by the API. It is sent for six months after the gateway issued it, as NHA's wrapper keeps it
(`Utils.setLinkTokenExpiry`); an older one is not sent, and a new one is asked for, rather than a refusal that fails the
request. The request is answered 202 also when the gateway refused it at once or could not be reached: its view then
says `Failed` and why. HC360 never sends a request again by itself, as the gateway may have acted on a call whose
answer was lost: staff send a failed one again (`POST /links/{id}/retry`, **Link again**), as a new request.

On the screens, **Link to patient's ABHA** sits beside **Download ABDM record**, on the records the export sends:
a signed consultation, an issued prescription (in its row's menu), a released lab, radiology or cardiology report,
a signed discharge summary and a vaccine dose given here (in its row's menu). It is offered while ABDM health records are switched on, to staff with `abdm.export`. It
asks first, then links that one record (`useAbdmLink`). The screen asks after the request every five seconds for two
minutes and says that ABDM linked it, why it did not, or that it has not answered yet and where to link it again; a
dropped WiFi connection is asked again. The server says at once why it cannot link a record: no ABHA address or year
of birth on the patient, the gateway not set up, or the record linked already or awaiting ABDM's answer.

The patient record's **ABDM records** tab lists the patient's link requests (**Linked to the patient's ABHA**, for
`abdm.export`; doctors see the tab for their consent requests too): when each was sent and whether automatically or
by staff, its records and the ABHA address, where it has got to and why ABDM did not link it, and **Link again** on a
failed request whose records are not linked or awaiting an answer since. It is asked again every 15 seconds while a
request awaits ABDM's answer.

#### Linking records automatically

As each record is completed, the hospital links it to the patient's ABHA itself, the way staff link it, so the patient
sees it in their PHR app without anyone doing anything (`AbdmAutoLinking`):

| Event (module) | Record linked |
|---|---|
| `EncounterSigned` (Clinical): a consultation note signed, out-patient, emergency, telehealth or health check-up, not in a stay | its OP consultation |
| `DischargeSummarySigned` (Clinical) | the discharge summary |
| `DiagnosticReportReleased` (Platform), laboratory or radiology: a report released, or re-released after an amendment or addendum | the report (once, though a lab report raises one event per test it releases) |
| `PrescriptionSigned` (Platform), out-patient | the prescription, once it may be issued: where the hospital requires the doctor's DSC, once signed with it |

* The event's handler (`AbdmAutoLinkTriggers`) runs in the event's transaction, before the signature commits, so it
  only reads and enqueues: an outbox message `abdm.link_record` (`{ type, recordId, patientId }`, kept two days),
  committed with the signature or not at all. Nothing it does calls outside or can fail the signature. It queues only
  while ABDM health records are on for the record's facility (a report's release names none, so it is read from the
  report, not taken from whoever released it), the gateway is on and set up, `autoLink` is on, and the
  patient has an ABHA address and a date of birth, is not merged into another UHID, and is neither a VIP nor in a
  sensitive category (HIV, mental health, MTP). No user acts, so no one's access to the chart is checked: such records
  are left to staff, as they are not listed to readers who may not open them (`PatientSearchQuery`).
* The outbox job (`AbdmAutoLinkHandler`) runs the staff path's linking core in a DI scope of its own: the dispatcher
  handles a batch of messages in one transaction, and inside it the request saved before the gateway call would not be
  committed when the gateway's answer, which may come back within a second, looked for it. It checks the record again
  (ABDM health records still on for the record's own facility, which may have switched them off since the job was
  queued; exportable; the patient still eligible) and then:
  * waits while a link token for the patient's ABHA address is being asked for (queued again 30 seconds later, then
    twice as long each time up to 5 minutes), so a discharge's records share one token rather than each asking for its
    own; and while a prescription waits for the DSC the hospital requires (10 minutes, doubling up to 2 hours, until it
    may be issued or the message's two days are up). Each wait is a new outbox message, and the outbox keeps every
    message, so the waits lengthen: a DSC that never comes leaves 27 of them, not hundreds;
  * finds nothing to do for a record linked already or awaiting an answer: the outbox delivers at least once, and a
    second delivery sends nothing;
  * otherwise links it, as a request no user made (`automatic`), audited as `phi` / `abdm.link_requested`, "Records
    linked to the patient's ABHA automatically: 1", with no user and `automatic: true`.
* What the gateway answers, and a request it does not answer, are handled as for staff's requests; a refusal is not
  retried by the job.
* A gateway that cannot be reached (`abdm.gateway_unreachable`: the hospital's internet is down) is retried. Nothing
  reached ABDM, so sending again cannot link a record twice. The failed request stays on the patient's list, marked
  failed, and the job is queued again 2 minutes later, then twice as long each time up to 2 hours: 8 tries in all, the
  last some four hours after the first. When the last one fails (or the message's two days end first) the holders of
  `abdm.export` at the facility get one in-app warning, "A record was not linked to the patient's ABHA", naming the
  kind of record (never the patient) and linking to the patient's chart, where the failed request can be sent again
  with Retry once the internet is back. `AbdmAutoLinkRetryTests` covers a transient failure that then succeeds, the
  doubling waits and the single alert.
* Not linked automatically, though staff may link them: cardiology reports, clinical notes, vaccine doses, bills, home
  readings, in-patient notes and prescriptions, and records completed before ABDM, the gateway or `autoLink` was
  switched on (nothing is linked backwards).

#### Requests ABDM does not answer

The gateway answers a call in seconds, on a callback; one whose callback is lost would wait for ever. Every minute, on
the servers that run jobs (and once at start-up), `AbdmUnansweredRequestSweeper` marks what has waited longer than
`answerTimeoutMinutes` (15 by default) failed (`AbdmUnansweredRequests`, at most 200 of each kind a minute):

| Request | Waits for | Past the timeout | An answer that comes later |
|---|---|---|---|
| Link, `AwaitingToken` (from when it was sent) | `on-generate-token` | `Failed`, `abdm.no_answer`: "ABDM did not send a link token within 15 minutes. Link the records again; if ABDM answers later, its answer is still taken." | the token is kept, and the request's records not linked or sent again since go with it (`Sent`) |
| Link, `Sent` (from when the care contexts went) | `on_carecontext` | `Failed`, `abdm.no_answer` ("…whether it linked the records…") | linked (`Linked`, kept in the care contexts), or, refused, `Failed` with the gateway's code and the token dropped |
| Consent request, `Sent` (from when it was made) | `on-init` | `Failed` (**Not sent**): "The consent manager did not say within 15 minutes that it has the request. Ask again; if it answers later, this request goes ahead too." | `Requested` |
| Consent request, `Requested` | the patient, in their ABHA app | not marked: the patient decides in their own time, and the consent manager notifies when the request expires. Asking again would put a second request in the patient's app | |

* The mark is HC360's guess, not the gateway's word, so the gateway's answer, whenever it comes, is taken. Ignoring a
  late `on_carecontext` would leave a record linked in the patient's PHR app but not among the hospital's care
  contexts, and a consent naming it would be refused ("care contexts that are not linked to this ABHA address here").
* Each request is marked on its own: one answered as it is swept (its version changed) stays as answered, and an
  answer that collides with the sweep is read again and taken as a late one, rather than lost.
* Staff send a failed request again from the patient's **ABDM records** tab: **Link again** (`POST /links/{id}/retry`,
  a new request for the records not linked or awaiting an answer since) and **Ask the patient again**
  (`POST /patients/{id}/consent-requests/{id}/retry`, a new request on the same terms, checked again). A request
  ABDM did take after all goes ahead beside the new one; the patient may decline one.

#### In the integration log

Every call HC360 makes to the gateway (`HttpAbdmGatewayClient`: the session, link tokens, care contexts, the answers of
user-initiated linking, consent and data flow; and the key-set fetch of `AbdmGatewayKeys`) and every callback the
gateway makes (`AbdmGatewayCallbackEndpoints`) is a row of the integration log, through the platform's
`IntegrationTrace` (Administration › Integrations › Log, Integration **ABDM gateway**):

| Field | Outbound call | Callback |
|---|---|---|
| Integration, provider | `abdm`; `abdm-sandbox` (consent manager `sbx`) or `abdm-gateway` (`abdm`) | the same, direction inbound |
| Operation | the gateway's path (`/v3/token/generate-token`) | HC360's path (`/api/v3/hip/token/on-generate-token`) |
| Reference | the call's `REQUEST-ID` | the `REQUEST-ID` of the call it answers (`response.requestId`), or its own for a call of the gateway's |
| Request | what was asked ("link the records to the patient's ABHA: 2 care context(s): Prescription 1, DiagnosticReport 1") | its own `REQUEST-ID` |
| Response, error | `HTTP 202`; `HTTP 400 ABDM-1006` and the gateway's reason; a gateway not reached; a call given up before the gateway answered ("may have reached it"); a session renewed after a 401 | `HTTP 202`; a refusal (401, unsigned or forged), a call for another HIP or HIU, an unreadable body (422); an answer carrying the gateway's error is a failure, with its code and message |

The two rows of one exchange share a reference: searching the log by a request's id shows the call and its answer,
and a call whose answer never came is a row with nothing beside it. No body is logged: bodies carry the patient's ABHA
address and number, their name, link tokens and the records' names; nor are the session token, the client secret or
any link token. The platform logger masks ABHA addresses and numbers and tokens in the error text again before the
row is stored. Pushes of records to an HIU's data-push URL (`AbdmDataPush`) and calls to the ABHA service (`AbhaClient`)
are not in the log.

#### Linking from the patient's PHR app (user-initiated, ABDM M2)

The patient looks for their records at the hospital from their PHR app, which the gateway passes on in three calls;
HC360 answers each with a call of its own (`AbdmUserLinkingService`):

1. **Discover** (`{ transactionId, patient: { id, name, gender, yearOfBirth, verifiedIdentifiers, unverifiedIdentifiers } }`,
   `id` the ABHA address). The patient is found by the ABHA address on their record; else by the mobile number ABDM
   verified (`MOBILE`), among active, unmerged patients with that number, keeping those of the same gender, a year of
   birth within two years (five when the hospital estimated the date of birth), the same first name and, when both
   have one, a last name with the same initial ("Meera K" is "Meera Krishnan"), and the UHID when the patient typed it
   (`MR`). Exactly one must match: none is `ABDM-1010` "Patient not found", more than one `1000`, so that a family
   sharing a number is not guessed between. The answer offers the patient's completed records (what the export would
   send: signed consultations and discharge summaries, issued prescriptions, released lab, radiology and cardiology
   reports, vaccine doses given here, in every facility, newest first, at most 100) not already linked to that ABHA address, grouped as for
   HIP-initiated linking, with `matchedBy` (`ABHA_ADDRESS`, or `MOBILE` and `MR`). Audited as `phi` /
   `abdm.discovered` with the records offered. A repeated call with the same transaction id gets the same answer.
2. **Link init** (`{ transactionId, abhaAddress, patient: [{ referenceNumber, careContexts: [{ referenceNumber }] }] }`):
   the records the patient chose, within 30 minutes of the discovery, from the same ABHA address, all among those
   offered ("Care context reference numbers are invalid" otherwise, "Patient reference number is invalid" for another
   patient). A six-digit code goes by SMS (template `abdm.link_code`) to the mobile number on the patient's record,
   good for ten minutes; the answer gives a link reference, `authenticationType: MEDIATE` and `meta: { communicationMedium:
   MOBILE, communicationHint: +91******3210, communicationExpiry }`. One code per search, and at most three codes to a
   patient in 15 minutes; a patient with no mobile number on record cannot link this way.
3. **Link confirm** (`{ confirmation: { linkRefNumber, token } }`): the right code links the chosen records
   (`abdm_care_contexts`, route `UserInitiated`) and the answer lists them; audited as `phi` / `abdm.user_linked`. A
   wrong code is `ABDM-1035` "Incorrect OTP"; the third wrong code, or a code past its ten minutes, ends the request,
   and the patient searches again. The code is kept only as a hash, and dropped once used or ended.

Records of every kind are offered, a restricted (VIP) or sensitive-category patient's included: it is the patient's
own search, from the ABHA the mobile number or address on their record belongs to, and nothing is shared until they
consent to it. HC360 does not keep the ABHA address on the patient's record from a search; staff record it as before.

#### Consent and the transfer of records (ABDM M2)

A provider that wants the patient's records (a Health Information User, HIU) asks for them through the patient's PHR
app; the patient grants a consent naming records linked here, and the gateway passes it on. HC360 then sends the records
the HIU asks for under that consent, encrypted for the HIU alone (`AbdmDataFlowService`):

1. **Consent notification** (`{ notification: { consentId, status, consentDetail: { patient: { id }, careContexts:
   [{ patientReference, careContextReference }], hiTypes, permission: { dateRange, dataEraseAt, frequency }, purpose,
   hiu } } }`). A `GRANTED` consent is kept (`abdm_consents`) only when every record it names is linked here to that ABHA
   address, all the same patient's; otherwise it is answered `FAILURE` ("The consent names care contexts that are not
   linked to this ABHA address here."). Audited as `phi` / `abdm.consent_granted` with the HIU, purpose and records.
   `REVOKED`, `EXPIRED` or `DENIED` ends a kept consent (`phi` / `abdm.consent_ended`). A consent that has ended stays
   ended: a `GRANTED` notification for it, late or repeated, changes nothing and is audited as `phi` /
   `abdm.consent_grant_ignored`. Either way the gateway gets `on-notify` with
   `acknowledgement: { status: OK, consentId }`.
2. **Health-information request** (`{ transactionId, hiRequest: { consent: { id }, dateRange, dataPushUrl, keyMaterial:
   { cryptoAlg, curve, dhPublicKey: { expiry, parameters, keyValue }, nonce } } }`). It is refused, with the reason in
   `on-request`'s error, when the consent is unknown, not granted (revoked, expired), past its `dataEraseAt`, or asked
   for dates outside its own; when `dataPushUrl` is not https, carries a user name, or names this machine or an address
   on a private, link-local, carrier-grade or multicast network (the request is the gateway's, signed, but a push URL
   must not point the hospital's server at its own network; host names are not resolved); or when the key material is
   not ECDH on Curve25519 with a public key and nonce, or the HIU's key has expired. Otherwise the transfer is saved
   (`abdm_transfers`), queued in the outbox (good for a day) and answered `hiRequest: { sessionStatus: ACKNOWLEDGED }`.
   The same transaction again is acknowledged again and sent once.
3. **The transfer**, from the outbox: each record the consent names is made into its ABDM document as the export makes
   it (without the staff checks: the patient's consent authorises it, a restricted or sensitive record included) if it
   is of a kind the consent covers (its `hiTypes`: a consent for prescriptions does not send a vaccine record or a lab
   report the patient also chose; `ERRORED` "The consent does not cover this kind of record."), still at the hospital
   and within the dates asked for. A remote monitoring programme runs on, so its home readings go by when each was
   measured: a document for each month of those measured in the dates, all under the programme's care context. The
   documents are encrypted with a fresh HIP key pair ([Fidelius](#fidelius)) and posted to `dataPushUrl` in pages of
   seven (`{ pageNumber, pageCount, transactionId, entries: [{ content, media: application/fhir+json, checksum,
   careContextReference }], keyMaterial }`, the HIP's public key and nonce), plain JSON with no gateway session, as
   NHA's wrapper does, each page within 60 seconds.
   Audited as `phi` / `abdm.shared`, "Records sent through ABDM under the patient's consent: 2", entity
   `abdm.transfer` and the transaction id, details `{ api, consent, hiu, delivered, failed }`; the patient sees it in
   their privacy summary ("Times your records were sent to another provider through ABDM").
4. **Notify**: the gateway gets `health-information/notify` with the session status (`TRANSFERRED` when every page was
   accepted, else `FAILED`) and each record's `DELIVERED` or `ERRORED` with the reason. A record whose documents fill
   more than one page (a programme's months) is `DELIVERED` only when every page holding one of them was accepted. A
   transfer is not retried: the HIU asks again with a new transaction.

#### Scan and share (ABDM M1)

The hospital puts its QR code (from NHA's HFR, with a code for each counter) at the registration desk. A patient scans
it with their PHR app and shares their ABHA profile; the gateway passes it on, and HC360 answers with a token number
for the counter (`AbdmProfileShareService`), as NHA's wrapper does:

1. **Share** (`{ intent, metaData: { hipId, context, hprId, latitude, longitude }, profile: { patient: { abhaAddress,
   abhaNumber, name, gender, address: { line, district, state, pincode }, yearOfBirth, monthOfBirth, dayOfBirth,
   phoneNumber, identifiers } } }`, `context` the counter's code). Without a valid ABHA address, a name or a counter it
   is answered with error `1000` "Invalid profile share request". The date parts may come as numbers or strings; an
   empty or odd one is taken as missing. The ABHA number is kept encrypted, the mobile number only when it is an Indian
   mobile number.
2. **Token**: numbers run from 1 each day (in India) across the hospital, and are answered as four digits (`0007`) in
   `on-share`: `{ acknowledgement: { status: SUCCESS, abhaAddress, profile: { context, tokenNumber, expiry } },
   response: { requestId } }`, `expiry` the time the token holds until (an hour), as the gateway's other times are
   written: the wrapper does not say which form the gateway expects, so this is to be confirmed in the sandbox. The
   same ABHA address scanning again at the same counter within the hour, while still waiting, gets the same token,
   with the profile brought up to date; two shares at once cannot get the same number (a unique index; the second
   takes the next). Audited as `data` / `abdm.profile_shared`.
3. **Match**: the share is matched to an active, unmerged patient on record by ABHA address, else ABHA number, else
   the mobile number with gender, year of birth and name (the rule of [user-initiated linking](#linking-from-the-patients-phr-app-user-initiated-abdm-m2)).
   Only a single match is kept: the desk decides.
4. **The desk** (staff app: Front office › ABHA scan and share, for `patients.register` while ABDM is switched on;
   it updates every 15 seconds): today's shares by token, with the mobile and ABHA numbers masked and the record each
   matched. **Register** (with `patients.contact.read`) opens patient registration filled in from the share (the name
   split into first, middle and last, the date of birth when the ABHA has all of it or else the age from the year, the
   mobile number, the ABHA number and address, and the address) for the desk to check with the patient; saving it
   takes the share off the queue as registered. A matched patient's record is opened from the queue, and **Done**
   marks the share registered as that patient. **Take off queue** dismisses one (the patient left, or scanned by
   mistake). Audited as `data` / `abdm.share_registered` and `abdm.share_dismissed`.

Shares are deleted a week after their day, when the next share arrives. HC360 does not verify the ABHA with ABDM
(the profile comes from the gateway's signed call, from the patient's own app), nor change a record on file from a
share: the desk edits the patient if their ABHA details are missing.

#### Checking a patient's ABHA (ABDM M1)

An ABHA number is 14 digits with no check digit, so the desk cannot tell a mistyped one from a real one; ABDM can
(`AbhaVerificationService`, `AbhaClient`). From the patient record's **Identity** tab (staff app, while ABDM is
switched on), staff with `patients.identity.verify` (the desk, as for Aadhaar checks) **Check ABHA with ABDM**:

1. ABDM's ABHA service (`abhaBaseUrl` in `abdm.gateway`; by default the sandbox's
   `https://abhasbx.abdm.gov.in/abha/api/v3`, or `https://abha.abdm.gov.in/abha/api/v3` with consent manager `abdm`)
   is asked for a login code: `POST /profile/login/request/otp` with `{ scope: ["abha-login", "mobile-verify"],
   loginHint: "abha-number" | "mobile", loginId, otpSystem: "abdm" }`. Only an identifier **already on the patient's
   record** is sent, never one typed at the desk: the ABHA number on record, or the mobile number on record. ABDM
   sends the code to the mobile number linked to the ABHA and answers `{ txnId, message }` ("OTP sent to mobile
   number ending with ******3210"), which the desk is shown. At most three codes to a patient in 15 minutes.
2. The patient reads the code out; `POST /profile/login/verify` with `{ scope, authData: { authMethods: ["otp"],
   otp: { txnId, otpValue } } }`. A wrong code (`authResult` other than `success`) is counted: three end the check;
   a code is good for ten minutes. ABDM answers with the ABHA account(s) the code opens (`ABHANumber`,
   `preferredAbhaAddress`, `name`, `gender`, `dob`, `kycVerified`); with several (a mobile number shared by a
   family), the one with the ABHA number on record is taken, or the check ends and the desk checks by ABHA number.
3. The account is compared with the record: the ABHA number, the name (the matching rule of
   [user-initiated linking](#linking-from-the-patients-phr-app-user-initiated-abdm-m2)), gender, and the year of
   birth (exact, within five years when the record's date of birth is estimated). **Verified** when nothing differs,
   else **Mismatch** with what differs in words ("The year of birth on the ABHA is 1983."). The record is not
   changed: the desk corrects it if the patient agrees.

The login id and the code are encrypted with ABDM's public key (`GET /profile/public/certificate`, read as a public
key or certificate, bare or in JSON, kept half an hour) under RSA-OAEP with SHA-1 and MGF1 SHA-1, base64, which is
Java's `RSA/ECB/OAEPWithSHA-1AndMGF1Padding` that ABDM documents; checked when this was built by decrypting HC360's
ciphertext with OpenSSL (`pkeyutl`, `rsa_oaep_md:sha1`), which a SHA-256 key refuses. Each call carries the gateway
session, `X-CM-ID`, `REQUEST-ID` and `TIMESTAMP`. The token ABDM issues on a right code (it opens the patient's ABHA
profile) is not kept, nor the code. Audited as `data` / `abdm.abha_code_sent` and `abdm.abha_checked` with what
differed. The request shapes follow ABDM's V3 integrator guide as integrators use them against the sandbox (NHA's
wrapper does not cover ABHA); they are to be confirmed in the hospital's sandbox run.

#### Asking for a patient's records at other providers (ABDM M3)

The hospital as a Health Information User (HIU): a doctor asks the patient to let the hospital see their records at
other hospitals and labs (`AbdmConsentRequestService`), following NHA's wrapper. From the patient record's **ABDM
records** tab (staff app, while ABDM is switched on, for `abdm.records.request`: doctors):

1. The doctor chooses why (care management, break the glass, the patient asked: `CAREMGT`, `BTG`, `PATRQT`), the kinds
   of records (`OPConsultation`, `Prescription`, `DischargeSummary`, `DiagnosticReport`, `ImmunizationRecord`,
   `HealthDocumentRecord`, `WellnessRecord`, `Invoice`), the dates they may come from, and until when the hospital may keep them
   (at most a year). The patient needs an ABHA address on record, and the doctor a medical-council registration number
   on their staff record: the request is in their name (`requester: { name, identifier: { type: "REGNO", value,
   system: "https://www.nmc.org.in" } }`). `POST /consent/v3/request/init` as the HIU (`X-HIU-ID`: `hiuId` in
   `abdm.gateway`, by default the HIP id), with `{ requestId, timestamp, consent: { purpose, patient: { id },
   hiu: { id }, requester, hiTypes, permission: { accessMode: VIEW, dateRange, dataEraseAt, frequency: { unit: HOUR,
   value: 1, repeats: 0 } } } }`. Saved before it is sent; audited as `phi` / `abdm.consent_requested`.
2. The consent manager answers at `on-init` with its id for the request (**Waiting for the patient**), or refuses it.
   A request it has not taken within `answerTimeoutMinutes` is marked failed (**Not sent**), and a doctor asks again on
   the same terms (**Ask the patient again**); an `on-init` that comes later is still taken
   ([Requests ABDM does not answer](#requests-abdm-does-not-answer)).
3. The patient decides in their ABHA app; the consent manager notifies (`/api/v3/hiu/consent/request/notify`):
   `GRANTED` with one artefact per provider, `DENIED`, and later `REVOKED` or `EXPIRED`. A request that was denied,
   revoked or expired stays so: a later `GRANTED` is acknowledged and ignored (audited as `phi` /
   `abdm.consent_grant_ignored`). HC360 keeps the decision (audited as `phi` / `abdm.consent_decided`), acknowledges
   it (`POST /consent/v3/request/hiu/on-notify`, one `{ status: OK, consentId }` per artefact, naming the
   notification's `REQUEST-ID`), and fetches each new artefact (`POST /consent/v3/fetch`).
4. Each artefact arrives at `on-fetch` with the provider (`hip: { id, name }`), the records (care contexts), the
   kinds, dates and erase date the patient agreed to; one naming an artefact other than the one fetched is ignored.
   The tab lists each request with its status and, for a granted one, each provider and how many records it covers.

Then the records themselves (`AbdmHealthInformationService`):

5. As soon as an artefact arrives granted, its records are asked for: `POST /data-flow/v3/health-information/request`
   as the HIU, `{ requestId, timestamp, hiRequest: { consent: { id }, dateRange, dataPushUrl, keyMaterial: { cryptoAlg:
   ECDH, curve: Curve25519, dhPublicKey: { expiry, parameters, keyValue }, nonce } } }`, with the artefact's dates, the
   hospital's `dataPushUrl` (`abdm.gateway`) and a [Fidelius](#fidelius) key pair and nonce made for this request alone;
   the key expires at the artefact's erase date. The private key is field-encrypted and kept only until the transfer
   ends. Audited as `phi` / `abdm.records_requested`. A doctor may ask again (**Ask again**) after a request failed.
6. The consent manager answers at `/api/v3/hiu/health-information/on-request` with the transaction id the provider's
   pages will name (`{ hiRequest: { transactionId, sessionStatus }, response: { requestId }, error }`), or refuses it.
7. The provider posts the records to the data-push URL, `POST /api/v3/hiu/health-information/transfer`, in pages
   (`{ pageNumber, pageCount, transactionId, entries: [{ content, media, checksum, careContextReference }],
   keyMaterial }`). ABDM's push carries no token, so a page is admitted only for a transaction awaiting pages (404
   otherwise), while the consent is granted and before its erase date, with a page number within the count its first
   page announced (at most 100) and ECDH Curve25519 key material (400 otherwise); a page sent twice is kept once.
   Each entry is decrypted with the request's private key and the provider's public key and nonce; one for a care
   context the consent does not cover, sent as a link, not FHIR JSON, altered on the way (AES-GCM's tag), or not a
   FHIR bundle is kept as an error, not as a record. A readable one is kept with its Composition's title and date,
   and whether the entry's checksum (the MD5 of the document, as ABDM names it) matched: some providers send a
   placeholder, so a mismatch is shown, not refused. Audited as `phi` / `abdm.records_received` per page.
8. When every page has arrived, the private key is dropped and the consent manager told what arrived:
   `POST /data-flow/v3/health-information/notify` as the HIU, `{ notification: { consentId, transactionId, doneAt,
   notifier: { type: HIU, id }, statusNotification: { sessionStatus, hipId, statusResponses: [{ careContextReference,
   hiStatus, description }] } } }`, `OK` per care context received and `ERRORED` with the reason otherwise,
   `TRANSFERRED` when any arrived and `FAILED` when none did.
9. The tab lists the records received; a doctor opens one to read its entries (diagnoses, observations with their
   values, medicines with their dosage, documents, with any PDF inside to open), as text, never as the bundle's HTML
   narrative. Each opening is audited as `phi` / `abdm.records_viewed`.
10. The records are deleted outright (not soft-deleted) when the patient revokes the consent or it expires, and at its
    erase date (`AbdmReceivedRecordEraser`, hourly; none is shown past it in the meantime), with any key still kept;
    audited as `phi` / `abdm.records_erased`, one entry per patient naming the records erased (`records`, their ids,
    never their content). The deletes, the keys dropped and the entries commit in one transaction: an erase that fails
    part-way erases nothing, and nothing is erased without its entry.

NHA's wrapper, which this follows for the shapes, has faults HC360 does not copy: it sends the HIU's notification of
records received under a header named `HIU` rather than `X-HIU-ID`, and reports a success as `ERRORED`/`FAILED`; it
reuses one key pair for every transfer until it restarts; it never looks at a checksum or which care context a record
names; it drops `hip.name`; and it looks consent requests up by the wrong id on `on-status` (which HC360 does not use).

#### The gateway's calls

The gateway calls HC360 on twelve endpoints, at the paths NHA's wrapper serves (`AbdmGatewayCallbackEndpoints`), each
call a row of the integration log ([In the integration log](#in-the-integration-log)):

| Method | Path | Body |
|---|---|---|
| POST | `/api/v3/hip/token/on-generate-token` | `{ abhaAddress, linkToken, response: { requestId }, error }` |
| POST | `/api/v3/link/on_carecontext` | `{ abhaAddress, status, response: { requestId }, error }` |
| POST | `/api/v3/hip/patient/care-context/discover` | discover, as above |
| POST | `/api/v3/hip/link/care-context/init` | link init, as above |
| POST | `/api/v3/hip/link/care-context/confirm` | link confirm, as above |
| POST | `/api/v3/consent/request/hip/notify` | consent notification, as above |
| POST | `/api/v3/hip/health-information/request` | health-information request, as above |
| POST | `/api/v3/hip/patient/share` | profile share, as above |
| POST | `/api/v3/hiu/consent/request/on-init` | `{ consentRequest: { id }, response: { requestId }, error }` |
| POST | `/api/v3/hiu/consent/request/notify` | `{ notification: { consentRequestId, status, consentArtefacts: [{ id }] } }` |
| POST | `/api/v3/hiu/consent/on-fetch` | `{ consent: { status, consentDetail, signature }, response: { requestId }, error }` |
| POST | `/api/v3/hiu/health-information/on-request` | `{ hiRequest: { transactionId, sessionStatus }, response: { requestId }, error }` |

Other providers post the records the hospital asked for to a thirteenth, `/api/v3/hiu/health-information/transfer`
(its public https address is the `dataPushUrl` setting), without a gateway token: see step 7 above. Its bodies are
limited to 32 MB.

The last six are answered by a call back to the gateway naming their `REQUEST-ID` header; a call naming another HIP
in `X-HIP-ID` (or, for the HIU's four, another HIU in `X-HIU-ID`) is logged and ignored. HC360 answers before it returns 202, so the gateway's call waits for the
answer's delivery (at most the gateway timeout); an answer that cannot be delivered is logged and the patient tries
again from their app.

They are anonymous at the HTTP level and checked by the gateway's signed bearer token ([Callback check](#abdm-gateway)):
401 when it is missing, forged or expired. They answer 503 `feature.disabled` while the ABDM switch is off, 404 while
the gateway is off or not set up, 422 for a body that is not the expected JSON, and 202 otherwise, also for an answer to no request of this hospital's,
which is logged and ignored. Bodies are limited to 64 KB and requests to the partners' rate limit per client address
(1,200 a minute by default, `HealthCare360:RateLimits:Partner`), apart from the public limit on QR codes and links. The hospital's public address for these paths is registered with NHA as the HIP's callback URL: the patient portal's, the one address a hospital opens to the internet, where they answer as on the staff app's (`ListenerPaths`; DEPLOYMENT.md, "Network checklist").

`HttpAbdmGatewayClientTests` checks each call against NHA's wrapper (paths, headers, bodies, the session's reuse and
renewal, both error shapes, no retry) and what each writes to the integration log (no token, ABHA detail or name);
`AbdmLinkingTests` (four requests made at once for one record: one sent, three `abdm.link_pending`), `AbdmAutoLinkingTests` (a record linked as it is signed, a job delivered twice sending nothing,
one token for records completed together, a prescription waiting for its DSC, nothing linked for a facility that
switched ABDM off, whoever released the record or queued its job), `AbdmUnansweredRequestTests` (requests
marked failed, late answers taken, staff's retries), `AbdmUserLinkingTests`, `AbdmDataFlowTests`, `AbdmProfileShareTests` and `AbdmConsentRequestTests` run the flows against a fake gateway inside the test
server (`FakeAbdmGateway`: sessions, 202s, a key set, and callbacks signed with its key). Neither has been run against
NHA's sandbox, which needs the hospital's credentials.

What remains, in order (also in the status's `remaining`):

1. Register the hospital as a HIP with its HFR id and get NHA **sandbox** client credentials (an outside approval);
   run linking, consent, the transfer of records, scan and share, ABHA checks and consent requests and the receipt of records as HIU in the sandbox, and confirm the key
   set's URL, the client id and issuer of the gateway's callback tokens (`callbackClientId`, `callbackIssuer`), the form of a share token's expiry and the ABHA login's request and answer,
   how long the gateway's link tokens last (six months in NHA's wrapper), how it answers care contexts already linked
   (208 in NHA's mock gateway), and how long its callbacks take, against `answerTimeoutMinutes`.
2. Pass NHA's sandbox functional tests and certification, then switch to the production gateway.
3. Sign documents (`Bundle.signature`, optional in the IG) with the doctor's DSC when the DSC signing of prescriptions
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
  default), and so is HIP-initiated linking of records to the patient's ABHA through the gateway, by staff and
  automatically as records are completed, which needs the hospital's NHA credentials and a sandbox run, user-initiated linking from the patient's PHR app, and the consent
  notification and encrypted transfer of consented records to the HIU, and scan and share at the registration desk.
  Checking a patient's ABHA with ABDM is built. Not met: creating an ABHA for a patient at the desk (Aadhaar OTP enrolment)
  ([ABDM gateway](#abdm-gateway)). Finalised bills are exported as invoice records, but checked only against base FHIR R4 and the guide's other profiles: the guide's InvoiceRecord and Invoice profiles were not in the package, so the document's type code and what those profiles require need a sandbox validation run ([Validation](#validation)). The FHIR read API's resources keep the ABDM-friendly identifiers (ABHA number, HFR id) but claim no
  NRCeS profiles, Practitioner and Organization aside; the ABDM documents do.
* Automatic linking covers consultation notes, discharge summaries, lab and radiology reports and out-patient
  prescriptions as they are completed. Cardiology reports, clinical notes, vaccine doses, bills and home readings are
  linked by staff (or by the patient from their PHR app), as are a VIP's records and records in a sensitive category,
  and records completed before automatic linking was on are not linked backwards. `answerTimeoutMinutes` and the
  six-month life of a link token come from NHA's wrapper and the gateway's usual speed, not from figures ABDM
  publishes; the sandbox run is to confirm them.
* A doctor without an HPR ID in the practitioner master is identified in ABDM documents and NHCX claims by medical
  council registration number and HC360 code only. HC360 checks that an HPR ID has 14 digits; it does not look it up
  in the registry.
* An ABDM download is in the patient's record access log (`patient.accessed`), but the privacy data summary does not
  list its recipient, as HC360 does not know who receives the file.
* No `vread`/history, `_include`/`_revinclude`, `_summary`, `_elements`, XML, or bulk export (`$export`); no ordering by
  relevance (`_score`);
  clinical searches are per patient.
* Binary is not served, so uploaded documents are metadata only. Operating theatres are not Locations yet, so a
  procedure names no place; ABDM documents name no ward or bed; and a Location carries no occupancy.
* A counselling note is served by the FHIR API under a consent that names counselling notes, but never exported as an
  ABDM document (an ABDM consent cannot keep it apart from the patient's other health documents). A clinical note's
  ABDM document carries its text, not a printed copy, as the hospital prints no clinical note.
* A bill's ABDM record has no button in the staff app yet: it is downloaded from `/api/v1/abdm/bundles/invoices/{id}`, and offered to the patient's PHR app for linking (user-initiated). Its status is as of the export (issued, then balanced once paid and settled); a bill cancelled after it was sent through ABDM is not retracted from the HIU that holds it, as for a dose marked in error. Credit notes are not exported, so an invoice sent before a credit note was issued shows the bill's amounts without it.
* Home readings go to ABDM a month to a document, and a month's document is made when it is asked for: the gateway is
  not told when a linked programme has new readings (ABDM's care-context notify), so a PHR app shows the programme's
  readings as of its last request. The patient's other wellness data (steps, sleep, diet, women's health) is not kept by
  HC360, so those sections of the guide's wellness record are never filled.
* Not exported yet: antibiotic susceptibilities, vital-sign
  interpretation flags, the primary-diagnosis rank. A dose copied from the patient's card is served by the FHIR API but
  not exported as an ABDM immunisation record (it is the record of whoever gave it), and the FHIR API serves no
  `ImmunizationRecommendation` (the ABDM document carries the next dose due). A dose marked as entered in error after it
  was sent through ABDM is not retracted from the HIU that holds it: ABDM gives a HIP no way to push one, and the next
  request for it is refused.
* Offset paging: rows added or changed between two page requests can shift the pages. The `next` links repeat the
  search parameters (including identifiers); POST `_search` keeps them only out of the first request line.
* Sign-in is the HC360 bearer token (10-minute access token and refresh). A SMART-on-FHIR / OAuth client-credentials
  flow for system-to-system access is a follow-up.
* A consent's purposes are what the reader states in `X-Purpose-Of-Use`; HC360 cannot check what the partner then
  does with the record. The statement is audited with each request, and a consent can name the partner accounts too.
* The portal's privacy summary lists a record-sharing consent as given or withdrawn, without what it covers; staff see
  the scope on the patient's consents.
* Maintainer: add `application/fhir+json` to the server's response-compression MIME types in `Program.cs` (FHIR
  responses are not compressed today).
